namespace LaptopTester.Core.Models;

public enum SpoofingRisk
{
    None,
    Low,
    Medium,
    High
}

/// <summary>Side-by-side comparison of one hardware component across WMI, Registry, and SMBIOS.</summary>
public sealed record SourceComparisonRow(
    string Component,
    string WmiValue,
    string RegistryValue,
    string SmbiosValue,
    bool IsConsistent,
    string Note);

/// <summary>Detected hardware spoofing or tampering indicator.</summary>
public sealed record SpoofingIndicator(
    string Category,
    string Title,
    string Description,
    SpoofingRisk Severity);

/// <summary>Multi-source anti-fake audit report.</summary>
public sealed record AntiFakeReport(
    SpoofingRisk OverallRisk,
    bool IsClean,
    bool SuspectedSoftwareDetected,
    IReadOnlyList<string> DetectedTools,
    IReadOnlyList<SourceComparisonRow> Comparisons,
    IReadOnlyList<SpoofingIndicator> Indicators,
    DateTimeOffset ScannedAt);
