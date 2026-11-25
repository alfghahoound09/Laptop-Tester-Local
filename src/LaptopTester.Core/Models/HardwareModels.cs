namespace LaptopTester.Core.Models;

/// <summary>Top-level hardware summary for Dashboard and Hardware Scan views.</summary>
public sealed record SystemSummary(
    string Manufacturer,
    string Model,
    string OsName,
    string OsBuild,
    string CpuName,
    int PhysicalCores,
    int LogicalProcessors,
    uint MaxClockMhz,
    double TotalRamGb,
    string RamSummary,
    string GpuSummary,
    string StorageSummary,
    string DisplaySummary,
    string BatterySummary,
    bool IsVirtualMachine,
    bool IsAdministrator);

/// <summary>Detailed CPU specification.</summary>
public sealed record CpuDetails(
    string Name,
    string Manufacturer,
    string Socket,
    int Cores,
    int Threads,
    uint MaxClockMhz,
    uint CurrentClockMhz,
    uint L2CacheKb,
    uint L3CacheKb,
    string Architecture);

/// <summary>Individual physical RAM module (WMI / SMBIOS).</summary>
public sealed record RamModuleDetails(
    string Slot,
    double CapacityGb,
    uint SpeedMhz,
    string MemoryType,
    string Manufacturer,
    string PartNumber,
    string SerialNumber);

/// <summary>Aggregated RAM specification and slot capacity.</summary>
public sealed record RamDetails(
    double TotalInstalledGb,
    double UsableGb,
    uint SpeedMhz,
    string MemoryType,
    int UsedSlots,
    int TotalSlots,
    IReadOnlyList<RamModuleDetails> Modules);

/// <summary>Display adapter details.</summary>
public sealed record GpuDetails(
    string Name,
    string DriverVersion,
    double VramGb,
    string AdapterType,
    string Resolution);

/// <summary>Physical storage drive with health and interface classification.</summary>
public sealed record StorageDriveDetails(
    int Index,
    string Model,
    string InterfaceType,
    string MediaCategory,
    double SizeGb,
    bool IsExternal,
    string SerialNumber,
    string HealthStatus,
    int? TemperatureC,
    int? WearPercent,
    bool? SmartFailurePredicted,
    double? ReadSpeedMbPerSec,
    double? WriteSpeedMbPerSec);

/// <summary>Monitor / panel details including EDID identity.</summary>
public sealed record DisplayDetails(
    string DeviceName,
    string PanelManufacturer,
    string PanelModel,
    int WidthPx,
    int HeightPx,
    int RefreshRateHz,
    int BitsPerPixel,
    bool IsPrimary,
    string AspectRatio);

/// <summary>Battery health, capacity, cycle count, and live power telemetry.</summary>
public sealed record BatteryDetails(
    bool HasBattery,
    bool IsOnAcPower,
    bool IsCharging,
    int ChargePercent,
    int RemainingMinutes,
    int DesignedCapacityMwh,
    int FullChargedCapacityMwh,
    int RemainingCapacityMwh,
    double HealthPercent,
    int CycleCount,
    double VoltageVolts,
    double RateWatts,
    string Manufacturer,
    string Chemistry,
    string SerialNumber);

/// <summary>Complete hardware scan report.</summary>
public sealed record HardwareReport(
    SystemSummary Summary,
    CpuDetails Cpu,
    RamDetails Ram,
    IReadOnlyList<GpuDetails> Gpus,
    IReadOnlyList<StorageDriveDetails> Drives,
    IReadOnlyList<DisplayDetails> Displays,
    BatteryDetails Battery,
    DateTimeOffset ScannedAt);
