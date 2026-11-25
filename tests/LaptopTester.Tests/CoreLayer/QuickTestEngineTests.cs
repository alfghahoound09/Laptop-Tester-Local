using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class QuickTestEngineTests
{
    private readonly QuickTestEngine _engine = new();

    [Fact]
    public void StandardSteps_HasTenDistinctStepsSummingTo100()
    {
        Assert.Equal(10, QuickTestEngine.StandardSteps.Count);
        Assert.Equal(10, QuickTestEngine.StandardSteps.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(100, QuickTestEngine.StandardSteps.Sum(s => s.Weight));
        Assert.All(QuickTestEngine.StandardSteps, s =>
            Assert.False(string.IsNullOrWhiteSpace(s.Title)));
    }

    [Fact]
    public void BuildRoundQueue_DequeuesDefaultAndVariablePrioritiesInOrder()
    {
        Assert.Equal(10, QuickTestEngine.DefaultHybridRounds.Count);
        Assert.Equal(100, QuickTestEngine.DefaultHybridRounds.Sum(r => r.Weight));

        var q = QuickTestEngine.BuildRoundQueue();
        var ids = new List<string>();
        while (q.TryDequeue(out var r, out _)) ids.Add(r.StepId);

        Assert.Equal(["hardware", "storage", "battery", "connectivity", "camera", "speaker", "microphone", "display", "touchpad", "keyboard"], ids);

        // Variable queue priorities can be customized per run
        var custom = new[]
        {
            new QuickTestRound("battery", "Bat", 3, 14, QuickTestRoundKind.Automated, false),
            new QuickTestRound("storage", "Sto", 1, 14, QuickTestRoundKind.Automated, true),
            new QuickTestRound("hardware", "Hw", 2, 18, QuickTestRoundKind.Automated, true)
        };
        var cq = QuickTestEngine.BuildRoundQueue(custom);
        var customIds = new List<string>();
        while (cq.TryDequeue(out var r, out _)) customIds.Add(r.StepId);
        Assert.Equal(["storage", "hardware", "battery"], customIds);
    }

    [Fact]
    public void Evaluate_EmptyAndAllPassed_Score100Good()
    {
        var empty = _engine.Evaluate([]);
        Assert.Equal(100, empty.FinalScore);
        Assert.Equal(VerdictBand.Good, empty.Verdict);
        Assert.Empty(empty.Deductions);

        var allPassed = QuickTestEngine.StandardSteps.Select(s =>
            new TestResultEntry(s.Id, s.Title, "Core", TestStatus.Passed, "OK", 0, DateTimeOffset.UtcNow));
        var res = _engine.Evaluate(allPassed);
        Assert.Equal(100, res.FinalScore);
        Assert.Equal(VerdictBand.Good, res.Verdict);
    }

    [Fact]
    public void Evaluate_PortedSelfTestCase_Returns81DoNotBuyAndTwoDeductions()
    {
        var now = DateTimeOffset.UtcNow;
        var results = new[]
        {
            new TestResultEntry("hardware", "Hardware", "Core", TestStatus.Passed, "OK", 0, now),
            new TestResultEntry("battery", "Battery", "Power", TestStatus.Warning, "Health 68%", 0, now),
            new TestResultEntry("display", "Display", "Display", TestStatus.Failed, "Dead pixels", 0, now)
        };

        var (score, verdict, deductions) = _engine.Evaluate(results);
        Assert.Equal(81, score);
        Assert.Equal(VerdictBand.DoNotBuy, verdict);
        Assert.Equal(2, deductions.Count);
    }

    [Theory]
    [InlineData("hardware")]
    [InlineData("storage")]
    [InlineData("display")]
    public void Evaluate_CriticalStepFailure_AlwaysForcesDoNotBuy(string criticalId)
    {
        var res = _engine.Evaluate([
            new TestResultEntry(criticalId, "Critical", "Core", TestStatus.Failed, "Bad", 0, DateTimeOffset.UtcNow)
        ]);
        Assert.Equal(VerdictBand.DoNotBuy, res.Verdict);
    }

    [Fact]
    public void Evaluate_RulesForWarningsNonCriticalFailsDeduplicationAndClamping()
    {
        var t0 = DateTimeOffset.UtcNow;

        // Warning deducts max(2, weight/2): camera weight 6 -> 3
        var camWarn = _engine.Evaluate([new TestResultEntry("camera", "Cam", "Input", TestStatus.Warning, "Blurry", 0, t0)]);
        Assert.Equal(97, camWarn.FinalScore);

        // Two small warnings -> ConsiderCarefully (even when score >= 82)
        var twoWarn = _engine.Evaluate([
            new TestResultEntry("camera", "Cam", "Input", TestStatus.Warning, "W1", 0, t0),
            new TestResultEntry("speaker", "Spk", "Audio", TestStatus.Warning, "W2", 0, t0)
        ]);
        Assert.Equal(94, twoWarn.FinalScore);
        Assert.Equal(VerdictBand.ConsiderCarefully, twoWarn.Verdict);

        // Single non-critical keyboard fail (-10 -> 90, 1 deduction) -> Good
        var kbFail = _engine.Evaluate([new TestResultEntry("keyboard", "KB", "Input", TestStatus.Failed, "Stuck", 0, t0)]);
        Assert.Equal(90, kbFail.FinalScore);
        Assert.Equal(VerdictBand.Good, kbFail.Verdict);

        // Multiple non-critical fails pushing score < 60 -> DoNotBuy
        var manyFails = _engine.Evaluate([
            new TestResultEntry("battery", "Bat", "Power", TestStatus.Failed, "Dead", 0, t0),
            new TestResultEntry("keyboard", "KB", "Input", TestStatus.Failed, "Dead", 0, t0),
            new TestResultEntry("touchpad", "TP", "Input", TestStatus.Failed, "Dead", 0, t0),
            new TestResultEntry("speaker", "Spk", "Audio", TestStatus.Failed, "Dead", 0, t0),
            new TestResultEntry("microphone", "Mic", "Audio", TestStatus.Failed, "Dead", 0, t0)
        ]);
        Assert.Equal(56, manyFails.FinalScore);
        Assert.Equal(VerdictBand.DoNotBuy, manyFails.Verdict);

        // Latest UpdatedAt wins, case-insensitive id, unknown/Skipped/Pending ignored, clamp >= 0
        var dedup = _engine.Evaluate([
            new TestResultEntry("HARDWARE", "HW", "Core", TestStatus.Failed, "Old fail", 0, t0.AddSeconds(-10)),
            new TestResultEntry("hardware", "HW", "Core", TestStatus.Passed, "Retested OK", 0, t0),
            new TestResultEntry("unknown_step", "X", "Misc", TestStatus.Failed, "Ignored", 0, t0),
            new TestResultEntry("camera", "Cam", "Input", TestStatus.Skipped, "Skipped", 0, t0),
            new TestResultEntry("speaker", "Spk", "Audio", TestStatus.Pending, "Pending", 0, t0)
        ]);
        Assert.Equal(100, dedup.FinalScore);
        Assert.Equal(VerdictBand.Good, dedup.Verdict);
    }
}
