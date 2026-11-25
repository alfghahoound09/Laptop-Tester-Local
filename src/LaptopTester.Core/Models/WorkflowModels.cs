namespace LaptopTester.Core.Models;

public enum TestStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed,
    Skipped
}

public enum VerdictBand
{
    Good,
    ConsiderCarefully,
    DoNotBuy
}

/// <summary>Single diagnostic result stored across hubs and Quick Test wizard.</summary>
public sealed record TestResultEntry(
    string TestId,
    string Title,
    string Category,
    TestStatus Status,
    string Summary,
    int ScoreImpact,
    DateTimeOffset UpdatedAt);

/// <summary>Single deduction explaining points subtracted from 100.</summary>
public sealed record ScoreDeduction(
    string TestId,
    string Title,
    int PointsDeducted,
    string Reason);

/// <summary>Final Quick Test session summary and scorecard.</summary>
public sealed record QuickTestSession(
    string Id,
    DateTimeOffset Timestamp,
    string MachineModel,
    string CpuName,
    string RamSummary,
    string StorageSummary,
    int FinalScore,
    VerdictBand Verdict,
    IReadOnlyList<TestResultEntry> Results,
    IReadOnlyList<ScoreDeduction> Deductions,
    string Notes);

public enum QuickTestRoundKind
{
    Automated,
    HybridAuto,
    Interactive
}

/// <summary>Variable Quick Test round descriptor ordered inside a PriorityQueue.</summary>
public sealed record QuickTestRound(
    string StepId,
    string Title,
    int Priority,
    int Weight,
    QuickTestRoundKind Kind,
    bool IsCriticalFail);
