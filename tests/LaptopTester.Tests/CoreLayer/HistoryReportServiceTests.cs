using System.IO;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class HistoryReportServiceTests
{
    private static QuickTestSession MakeSession(
        string id,
        DateTimeOffset ts,
        int score = 88,
        VerdictBand verdict = VerdictBand.Good,
        string model = "ThinkPad T14",
        string notes = "Clean machine") =>
        new(
            id,
            ts,
            model,
            "Intel Core Ultra 5",
            "16 GB DDR5",
            "512 GB NVMe",
            score,
            verdict,
            [new TestResultEntry("hardware", "Hardware", "Core", TestStatus.Passed, "OK", 0, ts)],
            verdict == VerdictBand.Good ? [] : [new ScoreDeduction("battery", "Battery", 12, "Wear")],
            notes);

    [Fact]
    public async Task SaveLoadDeleteAndClear_WorkAndHandleMissingOrCorruptFiles()
    {
        using var dir = new TempDirectory();
        var svc = new HistoryReportService(dir.Path);

        var older = MakeSession("old01", DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = MakeSession("new02", DateTimeOffset.UtcNow);
        await svc.SaveSessionAsync(older);
        await svc.SaveSessionAsync(newer);

        File.WriteAllText(Path.Combine(dir.Path, "corrupt.json"), "{ not valid json");

        var loaded = await svc.LoadSessionsAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("new02", loaded[0].Id);
        Assert.Equal("old01", loaded[1].Id);

        svc.DeleteSession("old01");
        var afterDelete = await svc.LoadSessionsAsync();
        Assert.Single(afterDelete);
        Assert.Equal("new02", afterDelete[0].Id);

        svc.ClearAllHistory();
        Assert.Empty(await svc.LoadSessionsAsync());

        var missingSvc = new HistoryReportService(Path.Combine(dir.Path, "does_not_exist"));
        Assert.Empty(await missingSvc.LoadSessionsAsync());
    }

    [Theory]
    [InlineData(VerdictBand.Good, 92, "GOOD / PASS")]
    [InlineData(VerdictBand.ConsiderCarefully, 75, "CONSIDER CAREFULLY")]
    [InlineData(VerdictBand.DoNotBuy, 45, "DO NOT BUY / ISSUES FOUND")]
    public async Task ExportJsonAndHtml_ContainScoreVerdictAndHtmlEncodeUserText(
        VerdictBand band, int score, string expectedVerdict)
    {
        using var dir = new TempDirectory();
        var svc = new HistoryReportService(dir.Path);
        var session = MakeSession(
            "exp01",
            DateTimeOffset.UtcNow,
            score,
            band,
            model: "Laptop <script>alert(1)</script>",
            notes: "Note <b>bold</b> & check");

        string jsonPath = Path.Combine(dir.Path, "report.json");
        await svc.ExportJsonAsync(jsonPath, session);
        Assert.True(File.Exists(jsonPath));
        Assert.Contains("\"FinalScore\": " + score, await File.ReadAllTextAsync(jsonPath));

        string htmlPath = Path.Combine(dir.Path, "report.html");
        await svc.ExportHtmlAsync(htmlPath, session);
        string html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains($"{score}/100", html);
        Assert.Contains(expectedVerdict, html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt;", html);
    }
}
