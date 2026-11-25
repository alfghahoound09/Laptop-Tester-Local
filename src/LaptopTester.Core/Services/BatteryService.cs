using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

public sealed record BatteryStressProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    int CurrentPercent,
    double DischargeWatts,
    double PercentDropped,
    double EstimatedTotalMinutes,
    string StatusText);

internal sealed record KernelBatteryProbe(
    int DesignedCapacityMwh,
    int FullChargedCapacityMwh,
    int CycleCount,
    string? Chemistry,
    string? Manufacturer,
    string? SerialNumber,
    string? ManufactureDate);

internal sealed record PowerCfgBatteryReport(
    int ReportedCycleCount,
    int EstimatedCycleCount,
    int DesignedCapacityMwh,
    int FullChargedCapacityMwh,
    string? Manufacturer,
    string? SerialNumber,
    string? ManufactureDate,
    string? HistoryTrendNote);

/// <summary>
/// Reads Windows battery health and lifecycle telemetry across 4 independent layers:
/// Win32 Power Status, kernel Battery IOCTLs, InstanceName-correlated root\wmi,
/// SMBIOS Type 22, and cached powercfg /batteryreport XML history.
/// </summary>
public sealed class BatteryService
{
    private static readonly Guid GuidDevClassBattery = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;
    private const uint BatterySystemBattery = 0x80000000;

    private readonly SemaphoreSlim _reportLock = new(1, 1);
    private PowerCfgBatteryReport? _cachedPowerCfgReport;
    private DateTimeOffset _cachedPowerCfgAt = DateTimeOffset.MinValue;

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    public async Task<BatteryDetails> GetBatteryInfoAsync(CancellationToken ct = default)
    {
        GetSystemPowerStatus(out var sps);
        var (noSystemBattery, isAc, isCharging, pct, remMinutes) = DecodePowerStatus(
            sps.ACLineStatus, sps.BatteryFlag, sps.BatteryLifePercent, sps.BatteryLifeTime);

        var kernelTask = Task.Run(ProbeKernelBatteries, ct);
        var wmiBatTask = WmiQueryHelper.QueryAsync("SELECT DesignedCapacity, FullChargeCapacity, Chemistry, DeviceID, Name FROM Win32_Battery", ct: ct);
        var staticTask = WmiQueryHelper.QueryAsync("SELECT InstanceName, DesignedCapacity, ManufactureName, SerialNumber, Chemistry FROM BatteryStaticData", @"root\wmi", ct);
        var fullTask = WmiQueryHelper.QueryAsync("SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi", ct);
        var cycleTask = WmiQueryHelper.QueryAsync("SELECT InstanceName, CycleCount FROM BatteryCycleCount", @"root\wmi", ct);
        var statusTask = WmiQueryHelper.QueryAsync("SELECT InstanceName, Voltage, DischargeRate, ChargeRate, RemainingCapacity, Charging, Discharging, PowerOnline FROM BatteryStatus", @"root\wmi", ct);

        await Task.WhenAll(kernelTask, wmiBatTask, staticTask, fullTask, cycleTask, statusTask);

        var kernelProbes = kernelTask.Result;
        var wmiBats = wmiBatTask.Result;
        var staticRows = staticTask.Result;

        bool hasBattery = !noSystemBattery && (kernelProbes.Count > 0 || wmiBats.Count > 0 || staticRows.Count > 0 || sps.BatteryLifePercent <= 100);
        if (!hasBattery)
        {
            return new BatteryDetails(
                false, isAc, false, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "N/A", "N/A", "N/A",
                CycleDisplay: "N/A",
                CycleSource: "No Battery",
                IsEstimatedCycleCount: false,
                ManufactureDate: "N/A",
                LifecycleNote: "Desktop or AC-powered system without an internal battery");
        }

        // Correlate root\wmi battery classes by InstanceName so dual-battery systems aggregate properly.
        var correlatedWmi = CorrelateWmiBatteries(staticRows, fullTask.Result, cycleTask.Result, statusTask.Result);

        int wmiDesigned = correlatedWmi.DesignedMwh;
        if (wmiDesigned <= 0 && wmiBats.Count > 0)
            wmiDesigned = wmiBats.Sum(r => SanitizeUint(WmiQueryHelper.ULong(r, "DesignedCapacity"), 10_000_000));

        int wmiFull = correlatedWmi.FullChargedMwh;
        if (wmiFull <= 0 && wmiBats.Count > 0)
            wmiFull = wmiBats.Sum(r => SanitizeUint(WmiQueryHelper.ULong(r, "FullChargeCapacity"), 10_000_000));

        int kernelDesigned = kernelProbes.Sum(p => p.DesignedCapacityMwh);
        int kernelFull = kernelProbes.Sum(p => p.FullChargedCapacityMwh);
        int kernelCycles = kernelProbes.Count > 0 ? kernelProbes.Max(p => p.CycleCount) : 0;

        int designed = wmiDesigned > 0 ? wmiDesigned : kernelDesigned;
        int full = wmiFull > 0 ? wmiFull : kernelFull;

        var smbios = SmbiosReader.Read();
        if (designed <= 0 && smbios.BatteryDesignCapacityMwh > 0)
            designed = smbios.BatteryDesignCapacityMwh;

        int cycles = kernelCycles > 0 ? kernelCycles : correlatedWmi.CycleCount;
        string cycleSource = kernelCycles > 0
            ? "ACPI Firmware (IOCTL)"
            : correlatedWmi.CycleCount > 0
                ? "Windows WMI"
                : "Pending";

        string? mfgDate = kernelProbes.Select(p => p.ManufactureDate).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        PowerCfgBatteryReport? powerCfg = null;

        // If firmware/WMI reports 0 cycles (or we lack manufacture date), consult cached Windows powercfg history.
        if (cycles <= 0 || string.IsNullOrWhiteSpace(mfgDate))
        {
            powerCfg = await GetCachedPowerCfgReportAsync(ct);
            if (powerCfg is not null)
            {
                if (designed <= 0 && powerCfg.DesignedCapacityMwh > 0) designed = powerCfg.DesignedCapacityMwh;
                if (full <= 0 && powerCfg.FullChargedCapacityMwh > 0) full = powerCfg.FullChargedCapacityMwh;
                if (string.IsNullOrWhiteSpace(mfgDate)) mfgDate = powerCfg.ManufactureDate;

                if (cycles <= 0 && powerCfg.ReportedCycleCount > 0)
                {
                    cycles = powerCfg.ReportedCycleCount;
                    cycleSource = "Windows Battery Report";
                }
            }
        }

        bool isEstimated = false;
        if (cycles <= 0 && powerCfg is not null && powerCfg.EstimatedCycleCount > 0)
        {
            cycles = powerCfg.EstimatedCycleCount;
            isEstimated = true;
            cycleSource = "Windows Drain History (Est.)";
        }

        string cycleDisplay = cycles > 0
            ? (isEstimated ? $"~{cycles}" : cycles.ToString())
            : "0 (EC N/R)";
        if (cycles <= 0 && cycleSource == "Pending")
            cycleSource = "EC Not Reporting";

        isCharging = isCharging || correlatedWmi.AnyCharging;
        isAc = isAc || correlatedWmi.AnyPowerOnline;

        double health = ComputeHealthPercent(designed, full);
        double rateWatts = Math.Round((isCharging ? correlatedWmi.TotalChargeMw : correlatedWmi.TotalDischargeMw) / 1000.0, 2);
        double volts = Math.Round(correlatedWmi.VoltageMv / 1000.0, 2);

        string mfg = FirstNonEmpty(
            correlatedWmi.Manufacturer,
            kernelProbes.Select(p => p.Manufacturer).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            powerCfg?.Manufacturer,
            smbios.BatteryManufacturer,
            "Unknown");

        string serial = FirstNonEmpty(
            correlatedWmi.SerialNumber,
            kernelProbes.Select(p => p.SerialNumber).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            powerCfg?.SerialNumber,
            "N/A");

        string chem = FirstNonEmpty(
            kernelProbes.Select(p => p.Chemistry).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            DecodeChemistry(wmiBats.FirstOrDefault() is { } wb ? WmiQueryHelper.Int(wb, "Chemistry") : 0),
            "Li-Ion / Polymer");

        string lifecycleNote = BuildLifecycleNote(
            hasBattery, isAc, isCharging, pct, rateWatts, health, isEstimated, powerCfg?.HistoryTrendNote, cycles);

        return new BatteryDetails(
            true, isAc, isCharging, pct, remMinutes,
            designed, full, correlatedWmi.RemainingMwh, health, cycles,
            volts, rateWatts, mfg, chem, serial,
            CycleDisplay: cycleDisplay,
            CycleSource: cycleSource,
            IsEstimatedCycleCount: isEstimated,
            ManufactureDate: string.IsNullOrWhiteSpace(mfgDate) ? "N/A" : mfgDate,
            LifecycleNote: lifecycleNote);
    }

    /// <summary>Runs a multi-core CPU workload for the requested duration and streams battery drain telemetry.</summary>
    public async Task RunStressTestAsync(
        int durationSeconds,
        IProgress<BatteryStressProgress> progress,
        CancellationToken ct)
    {
        var initial = await GetBatteryInfoAsync(ct);
        int startPct = initial.ChargePercent;
        double startVolts = initial.VoltageVolts;
        int workerCount = Math.Max(1, Environment.ProcessorCount / 2);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
        {
            double x = 1.000123;
            while (!linkedCts.Token.IsCancellationRequested)
            {
                for (int i = 0; i < 200_000; i++)
                    x = Math.Sqrt(x * 1.000001 + 3.14159);
                if (x > 1e12) x = 1.000123;
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            for (int sec = 1; sec <= durationSeconds; sec++)
            {
                await Task.Delay(1000, ct);
                var snap = await GetBatteryInfoAsync(ct);
                int dropped = Math.Max(0, startPct - snap.ChargePercent);
                double voltSag = startVolts > 0 && snap.VoltageVolts > 0 ? Math.Max(0, startVolts - snap.VoltageVolts) : 0;
                double estMinutes = EstimateRemainingMinutes(snap.RateWatts, snap.RemainingCapacityMwh, sec, snap.ChargePercent, dropped);

                string status = snap.IsOnAcPower
                    ? "Charger plugged in — unplug AC power to measure real battery drain"
                    : $"{workerCount} CPU stress threads running · {snap.RateWatts:0.0}W · {snap.VoltageVolts:0.00}V" +
                      (voltSag >= 0.4 ? $" (Sag -{voltSag:0.00}V)" : "");

                progress.Report(new BatteryStressProgress(
                    sec, durationSeconds, snap.ChargePercent,
                    snap.RateWatts, dropped, Math.Round(estMinutes, 0), status));
            }
        }
        finally
        {
            linkedCts.Cancel();
            try { await Task.WhenAll(workers); } catch { }
        }
    }

    internal static (
        int DesignedMwh,
        int FullChargedMwh,
        int RemainingMwh,
        int CycleCount,
        int VoltageMv,
        int TotalDischargeMw,
        int TotalChargeMw,
        bool AnyCharging,
        bool AnyPowerOnline,
        string? Manufacturer,
        string? SerialNumber) CorrelateWmiBatteries(
            IReadOnlyList<Dictionary<string, object?>> staticRows,
            IReadOnlyList<Dictionary<string, object?>> fullRows,
            IReadOnlyList<Dictionary<string, object?>> cycleRows,
            IReadOnlyList<Dictionary<string, object?>> statusRows)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in staticRows) keys.Add(WmiQueryHelper.Str(r, "InstanceName", "_0"));
        foreach (var r in fullRows) keys.Add(WmiQueryHelper.Str(r, "InstanceName", "_0"));
        foreach (var r in cycleRows) keys.Add(WmiQueryHelper.Str(r, "InstanceName", "_0"));
        foreach (var r in statusRows) keys.Add(WmiQueryHelper.Str(r, "InstanceName", "_0"));

        int designed = 0, full = 0, remaining = 0, maxCycles = 0, voltage = 0, disch = 0, chg = 0;
        bool anyCharging = false, anyOnline = false;
        string? mfg = null, serial = null;

        foreach (string key in keys)
        {
            var st = staticRows.FirstOrDefault(r => WmiQueryHelper.Str(r, "InstanceName", "_0").Equals(key, StringComparison.OrdinalIgnoreCase));
            var fl = fullRows.FirstOrDefault(r => WmiQueryHelper.Str(r, "InstanceName", "_0").Equals(key, StringComparison.OrdinalIgnoreCase));
            var cy = cycleRows.FirstOrDefault(r => WmiQueryHelper.Str(r, "InstanceName", "_0").Equals(key, StringComparison.OrdinalIgnoreCase));
            var pw = statusRows.FirstOrDefault(r => WmiQueryHelper.Str(r, "InstanceName", "_0").Equals(key, StringComparison.OrdinalIgnoreCase));

            if (st is not null)
            {
                designed += SanitizeUint(WmiQueryHelper.ULong(st, "DesignedCapacity"), 10_000_000);
                mfg ??= NullIfEmpty(WmiQueryHelper.Str(st, "ManufactureName"));
                serial ??= NullIfEmpty(WmiQueryHelper.Str(st, "SerialNumber"));
            }
            if (fl is not null)
                full += SanitizeUint(WmiQueryHelper.ULong(fl, "FullChargedCapacity"), 10_000_000);
            if (cy is not null)
                maxCycles = Math.Max(maxCycles, SanitizeUint(WmiQueryHelper.ULong(cy, "CycleCount"), 50_000));
            if (pw is not null)
            {
                remaining += SanitizeUint(WmiQueryHelper.ULong(pw, "RemainingCapacity"), 10_000_000);
                int v = SanitizeUint(WmiQueryHelper.ULong(pw, "Voltage"), 100_000);
                if (v > voltage) voltage = v;
                disch += SanitizeUint(WmiQueryHelper.ULong(pw, "DischargeRate"), 1_000_000);
                chg += SanitizeUint(WmiQueryHelper.ULong(pw, "ChargeRate"), 1_000_000);
                anyCharging |= WmiQueryHelper.Bool(pw, "Charging");
                anyOnline |= WmiQueryHelper.Bool(pw, "PowerOnline");
            }
        }

        return (designed, full, remaining, maxCycles, voltage, disch, chg, anyCharging, anyOnline, mfg, serial);
    }

    internal static PowerCfgBatteryReport? ParseBatteryReportXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root is null) return null;

            int reportedCycles = 0;
            int designedMwh = 0;
            int fullMwh = 0;
            string? mfg = null;
            string? serial = null;
            string? mfgDate = null;

            var batteryEls = root.Descendants().Where(e => e.Name.LocalName == "Battery").ToList();
            foreach (var bat in batteryEls)
            {
                int cy = ParsePositiveInt(ChildVal(bat, "CycleCount"), 50_000);
                if (cy > reportedCycles) reportedCycles = cy;

                designedMwh += ParsePositiveInt(ChildVal(bat, "DesignCapacity"), 10_000_000);
                fullMwh += ParsePositiveInt(ChildVal(bat, "FullChargeCapacity"), 10_000_000);
                mfg ??= NullIfEmpty(ChildVal(bat, "Manufacturer"));
                serial ??= NullIfEmpty(ChildVal(bat, "SerialNumber"));
                mfgDate ??= NullIfEmpty(ChildVal(bat, "ManufactureDate"));
            }

            // Compute cumulative drain energy (mWh) across HistoryEntry / UsageEntry if firmware left CycleCount empty.
            long totalDrainedMwh = 0;
            int firstHistFull = 0;
            int lastHistFull = 0;

            foreach (var hist in root.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
            {
                int histFull = ParsePositiveInt(AttrOrChild(hist, "FullChargeCapacity"), 10_000_000);
                if (histFull > 0)
                {
                    if (firstHistFull == 0) firstHistFull = histFull;
                    lastHistFull = histFull;
                }
                totalDrainedMwh += ParsePositiveInt(AttrOrChild(hist, "ActiveEnergyDrain"), 500_000_000);
                totalDrainedMwh += ParsePositiveInt(AttrOrChild(hist, "ConnectedStandbyEnergyDrain"), 500_000_000);
            }

            int estimatedCycles = 0;
            if (reportedCycles <= 0 && designedMwh > 0 && totalDrainedMwh > 0)
                estimatedCycles = (int)Math.Round(totalDrainedMwh / (double)designedMwh);

            string? trend = null;
            if (firstHistFull > 0 && lastHistFull > 0 && designedMwh > 0)
            {
                double startPct = Math.Round(firstHistFull * 100.0 / designedMwh, 1);
                double endPct = Math.Round(lastHistFull * 100.0 / designedMwh, 1);
                trend = $"OS History: {startPct:0.#}% -> {endPct:0.#}%";
            }

            return new PowerCfgBatteryReport(
                reportedCycles,
                estimatedCycles,
                designedMwh,
                fullMwh,
                mfg,
                serial,
                mfgDate,
                trend);
        }
        catch
        {
            return null;
        }
    }

    internal static string BuildLifecycleNote(
        bool hasBattery,
        bool isAc,
        bool isCharging,
        int chargePercent,
        double rateWatts,
        double healthPercent,
        bool isEstimatedCycles,
        string? historyTrend,
        int cycleCount)
    {
        if (!hasBattery) return "No battery installed";

        var notes = new List<string>();
        if (isAc && !isCharging && chargePercent is >= 45 and <= 92 && rateWatts < 1.5)
            notes.Add($"Charge-limit mode likely active (holding at {chargePercent}% on AC)");

        if (healthPercent > 0 && healthPercent < 45)
            notes.Add("Severe cell wear (<45%) — battery replacement recommended");
        else if (healthPercent is >= 45 and < 70)
            notes.Add("Moderate cell wear — run 30s drain stress test to check voltage sag");
        else if (healthPercent >= 70)
            notes.Add("Capacity healthy");

        if (isEstimatedCycles)
            notes.Add($"Cycles estimated (~{cycleCount}) from Windows drain log because EC omitted CycleCount");
        else if (cycleCount <= 0)
            notes.Add("EC firmware returned 0 cycles (new pack or counter unsupported on this board)");

        if (!string.IsNullOrWhiteSpace(historyTrend))
            notes.Add(historyTrend);

        return string.Join(" · ", notes);
    }

    private async Task<PowerCfgBatteryReport?> GetCachedPowerCfgReportAsync(CancellationToken ct)
    {
        if (_cachedPowerCfgReport is not null && (DateTimeOffset.UtcNow - _cachedPowerCfgAt) < TimeSpan.FromMinutes(2))
            return _cachedPowerCfgReport;

        await _reportLock.WaitAsync(ct);
        try
        {
            if (_cachedPowerCfgReport is not null && (DateTimeOffset.UtcNow - _cachedPowerCfgAt) < TimeSpan.FromMinutes(2))
                return _cachedPowerCfgReport;

            string tempPath = Path.Combine(Path.GetTempPath(), $"lt_bat_{Guid.NewGuid():N}.xml");
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = $"/batteryreport /xml /output \"{tempPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc is not null)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await proc.WaitForExitAsync(waitCts.Token); } catch { try { proc.Kill(); } catch { } }
                }

                if (File.Exists(tempPath))
                {
                    string xml = await File.ReadAllTextAsync(tempPath, Encoding.UTF8, ct);
                    _cachedPowerCfgReport = ParseBatteryReportXml(xml);
                    _cachedPowerCfgAt = DateTimeOffset.UtcNow;
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
            return _cachedPowerCfgReport;
        }
        finally
        {
            _reportLock.Release();
        }
    }

    private static List<KernelBatteryProbe> ProbeKernelBatteries()
    {
        var list = new List<KernelBatteryProbe>();
        Guid guid = GuidDevClassBattery;
        IntPtr hDevInfo = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (hDevInfo == IntPtr.Zero || hDevInfo == InvalidHandleValue)
            return list;

        try
        {
            int spSize = IntPtr.Size == 8 ? 32 : 28;
            int cbDetailHeader = IntPtr.Size == 8 ? 8 : 6;

            for (uint i = 0; i < 8; i++)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = spSize };
                if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, i, ref iface))
                    break;

                SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref iface, IntPtr.Zero, 0, out int reqSize, IntPtr.Zero);
                if (reqSize <= cbDetailHeader) continue;

                IntPtr detailBuf = Marshal.AllocHGlobal(reqSize);
                string? devicePath = null;
                try
                {
                    Marshal.WriteInt32(detailBuf, cbDetailHeader);
                    if (SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref iface, detailBuf, reqSize, out _, IntPtr.Zero))
                        devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }

                if (string.IsNullOrWhiteSpace(devicePath)) continue;
                var probe = QuerySingleBatteryDevice(devicePath);
                if (probe is not null) list.Add(probe);
            }
        }
        catch { }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
        return list;
    }

    private static KernelBatteryProbe? QuerySingleBatteryDevice(string devicePath)
    {
        IntPtr handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == InvalidHandleValue || handle == IntPtr.Zero)
            handle = CreateFileW(devicePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == InvalidHandleValue || handle == IntPtr.Zero)
            return null;

        try
        {
            byte[] tagIn = new byte[4];
            byte[] tagOut = new byte[4];
            if (!DeviceIoControl(handle, IoctlBatteryQueryTag, tagIn, 4, tagOut, 4, out int tagBytes, IntPtr.Zero) || tagBytes < 4)
                return null;

            uint tag = BitConverter.ToUInt32(tagOut, 0);
            if (tag == 0) return null;

            byte[] infoOut = new byte[36];
            if (!QueryBatteryInfoIoctl(handle, tag, 0 /* BatteryInformation */, infoOut, out int infoBytes) || infoBytes < 36)
                return null;

            uint caps = BitConverter.ToUInt32(infoOut, 0);
            if ((caps & BatterySystemBattery) == 0)
                return null;

            string chemCode = Encoding.ASCII.GetString(infoOut, 8, 4).TrimEnd('\0', ' ');
            int designed = SanitizeUint(BitConverter.ToUInt32(infoOut, 12), 10_000_000);
            int full = SanitizeUint(BitConverter.ToUInt32(infoOut, 16), 10_000_000);
            int cycle = SanitizeUint(BitConverter.ToUInt32(infoOut, 32), 50_000);

            string? mfgDate = null;
            byte[] dateOut = new byte[4];
            if (QueryBatteryInfoIoctl(handle, tag, 4 /* BatteryManufactureDate */, dateOut, out int dBytes) && dBytes >= 4)
            {
                byte day = dateOut[0], month = dateOut[1];
                ushort year = BitConverter.ToUInt16(dateOut, 2);
                if (year is >= 1995 and <= 2100 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
                    mfgDate = $"{year:D4}-{month:D2}-{day:D2}";
            }

            string? mfg = QueryBatteryStringIoctl(handle, tag, 5 /* BatteryManufactureName */);
            string? serial = QueryBatteryStringIoctl(handle, tag, 8 /* BatterySerialNumber */);

            return new KernelBatteryProbe(designed, full, cycle, NullIfEmpty(chemCode), mfg, serial, mfgDate);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool QueryBatteryInfoIoctl(IntPtr handle, uint tag, uint level, byte[] outBuf, out int bytesReturned)
    {
        byte[] inBuf = new byte[12];
        BitConverter.GetBytes(tag).CopyTo(inBuf, 0);
        BitConverter.GetBytes(level).CopyTo(inBuf, 4);
        return DeviceIoControl(handle, IoctlBatteryQueryInformation, inBuf, 12, outBuf, outBuf.Length, out bytesReturned, IntPtr.Zero);
    }

    private static string? QueryBatteryStringIoctl(IntPtr handle, uint tag, uint level)
    {
        byte[] buf = new byte[256];
        if (!QueryBatteryInfoIoctl(handle, tag, level, buf, out int bytes) || bytes < 2)
            return null;
        return NullIfEmpty(Encoding.Unicode.GetString(buf, 0, Math.Min(bytes, buf.Length)).Split('\0')[0]);
    }

    internal static int SanitizeUint(ulong raw, int maxValid) =>
        raw == 0 || raw >= 0xFFFFFF00UL || raw > (ulong)maxValid ? 0 : (int)raw;

    private static int ParsePositiveInt(string? text, int maxValid) =>
        int.TryParse(text?.Trim(), out int v) && v > 0 && v <= maxValid ? v : 0;

    private static string ChildVal(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";

    private static string AttrOrChild(XElement parent, string localName) =>
        parent.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value.Trim()
        ?? ChildVal(parent, localName);

    private static string? NullIfEmpty(string? val) => string.IsNullOrWhiteSpace(val) ? null : val.Trim();

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "Unknown";

    internal static (bool NoSystemBattery, bool IsAc, bool IsCharging, int Percent, int RemainingMinutes) DecodePowerStatus(
        byte acLineStatus, byte batteryFlag, byte batteryLifePercent, int batteryLifeTimeSeconds) =>
        (
            (batteryFlag & 128) != 0,
            acLineStatus == 1,
            (batteryFlag & 8) != 0,
            batteryLifePercent <= 100 ? batteryLifePercent : 0,
            batteryLifeTimeSeconds > 0 ? batteryLifeTimeSeconds / 60 : 0
        );

    internal static double ComputeHealthPercent(int designed, int full) =>
        designed > 0 && full > 0 ? Math.Round(Math.Clamp(full * 100.0 / designed, 0, 120), 1) : 0;

    internal static double EstimateRemainingMinutes(
        double rateWatts, int remainingCapacityMwh, int elapsedSeconds, int currentPercent, int droppedPercent) =>
        rateWatts > 0.5 && remainingCapacityMwh > 0
            ? (remainingCapacityMwh / 1000.0) / rateWatts * 60.0
            : droppedPercent > 0 ? elapsedSeconds / 60.0 * (currentPercent / (double)droppedPercent) : 0;

    internal static string DecodeChemistry(int code) => code switch
    {
        3 => "Lead Acid",
        4 => "NiCd",
        5 => "NiMH",
        6 => "Li-Ion",
        7 => "Zinc-Air",
        8 => "Li-Poly",
        _ => "Li-Ion / Polymer"
    };
}
