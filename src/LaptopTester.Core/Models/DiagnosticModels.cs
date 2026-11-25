namespace LaptopTester.Core.Models;

public sealed record UsbControllerDetails(string Name, string DeviceId, string Status);

public sealed record UsbDeviceDetails(
    string Name,
    string DeviceId,
    string PortType,
    string SpeedLabel,
    string Manufacturer,
    bool IsHub);

public sealed record WifiAdapterDetails(
    string Name,
    string InterfaceDescription,
    string Status,
    string Ssid,
    int SignalPercent,
    string LinkSpeed);

public sealed record WifiNetworkSummary(
    string Ssid,
    int SignalPercent,
    string Security,
    string PhyType);

public sealed record BluetoothDetails(
    bool HasRadio,
    bool IsEnabled,
    string RadioName,
    string Address,
    IReadOnlyList<string> PairedDevices);

public sealed record DriverIssueDetails(
    string DeviceName,
    string PnpClass,
    int ErrorCode,
    string Description);

public sealed record StartupEntryDetails(
    string Name,
    string Command,
    string Location,
    string RiskLevel);

public sealed record TemperatureSnapshot(
    double? CpuPackageC,
    double? MaxDriveC,
    double CpuLoadPercent,
    double RamUsedPercent,
    string SourceNote,
    DateTimeOffset CapturedAt);

public sealed record BenchmarkReport(
    double CpuSingleScore,
    double CpuMultiScore,
    double CpuElapsedMs,
    double MemoryBandwidthGbPerSec,
    double? DiskSeqReadMbPerSec,
    double? DiskSeqWriteMbPerSec,
    string OverallTier,
    DateTimeOffset CompletedAt);
