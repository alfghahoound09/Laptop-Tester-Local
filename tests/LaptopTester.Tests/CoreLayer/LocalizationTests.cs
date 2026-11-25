using LaptopTester.Core.Localization;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class LocalizationTests
{
    [Fact]
    public void Get_ReturnsEnglishValueOrKeyFallback()
    {
        var loc = new LocalizationService();

        Assert.Equal("Dashboard", loc.Get("Nav.Dashboard"));
        Assert.Equal("System Overview", loc.Get("Dashboard.Title"));
        Assert.Equal("Unknown.Key", loc.Get("Unknown.Key"));
    }

    [Fact]
    public void AllKeys_HaveNonEmptyEnglishCopyWithoutLanguageSetting()
    {
        var loc = new LocalizationService();
        Assert.DoesNotContain("Settings.Language", LocalizationService.AllKeys);

        foreach (string key in LocalizationService.AllKeys)
        {
            string value = loc.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(key, value);
        }
    }

    [Theory]
    [InlineData("Nav.Dashboard")]
    [InlineData("Nav.QuickTest")]
    [InlineData("Nav.Hardware")]
    [InlineData("Nav.Devices")]
    [InlineData("Nav.Report")]
    [InlineData("Nav.History")]
    [InlineData("Nav.Settings")]
    [InlineData("Dashboard.Title")]
    [InlineData("Dashboard.Subtitle")]
    [InlineData("Dashboard.Tile.Hardware")]
    [InlineData("Dashboard.Tile.Battery")]
    [InlineData("Dashboard.Tile.AntiFake")]
    [InlineData("Dashboard.Tile.Benchmark")]
    [InlineData("Dashboard.SystemInfo")]
    [InlineData("Dashboard.Rescan")]
    [InlineData("Dashboard.AboutTitle")]
    [InlineData("Dashboard.AboutDesc")]
    [InlineData("Dashboard.Tip")]
    [InlineData("AntiFake.Clean")]
    [InlineData("AntiFake.Warning")]
    [InlineData("Battery.StressStart")]
    [InlineData("Battery.StressStop")]
    [InlineData("QuickTest.Title")]
    [InlineData("QuickTest.Subtitle")]
    [InlineData("QuickTest.RunAuto")]
    [InlineData("QuickTest.Pass")]
    [InlineData("QuickTest.Fail")]
    [InlineData("QuickTest.Skip")]
    [InlineData("Verdict.Good")]
    [InlineData("Verdict.Consider")]
    [InlineData("Verdict.DoNotBuy")]
    [InlineData("Report.Title")]
    [InlineData("Report.ExportHtml")]
    [InlineData("Report.ExportJson")]
    [InlineData("Report.SaveSession")]
    [InlineData("History.Title")]
    [InlineData("History.Refresh")]
    [InlineData("History.Clear")]
    [InlineData("Settings.Title")]
    [InlineData("Settings.Privilege")]
    public void ViewModelRequiredKeys_ExistInTable(string key)
    {
        Assert.Contains(key, LocalizationService.AllKeys);
    }
}
