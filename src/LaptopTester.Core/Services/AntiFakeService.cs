using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LaptopTester.Core.Models;
using Microsoft.Win32;

namespace LaptopTester.Core.Services;

/// <summary>Performs 3-source hardware verification (WMI vs Registry vs SMBIOS) and spoofing checks.</summary>
public sealed class AntiFakeService
{
    private readonly StorageHealthService _storageService;

    public AntiFakeService(StorageHealthService storageService)
    {
        _storageService = storageService;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    public async Task<AntiFakeReport> ScanForSpoofingAsync(CancellationToken ct = default)
    {
        var smbios = SmbiosReader.Read();
        var wmiCpuTask = WmiQueryHelper.QueryAsync("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor", ct: ct);
        var wmiCsTask = WmiQueryHelper.QueryAsync("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", ct: ct);
        var wmiRamTask = WmiQueryHelper.QueryAsync("SELECT Capacity FROM Win32_PhysicalMemory", ct: ct);
        var drivesTask = _storageService.GetDrivesAsync(ct);

        await Task.WhenAll(wmiCpuTask, wmiCsTask, wmiRamTask, drivesTask);

        var indicators = new List<SpoofingIndicator>();
        var comparisons = new List<SourceComparisonRow>();

        // 1. CPU comparison across WMI, Registry, SMBIOS
        string wmiCpu = WmiQueryHelper.Str(wmiCpuTask.Result.FirstOrDefault() ?? new(), "Name");
        string regCpu = HardwareService.ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        string smbiosCpu = smbios.ProcessorVersion;
        bool cpuMatch = CpuNamesMatch(wmiCpu, regCpu) && (string.IsNullOrWhiteSpace(smbiosCpu) || CpuNamesMatch(wmiCpu, smbiosCpu));

        comparisons.Add(new SourceComparisonRow(
            "CPU",
            DisplayOrDash(wmiCpu),
            DisplayOrDash(regCpu),
            DisplayOrDash(smbiosCpu),
            cpuMatch,
            cpuMatch ? "Khớp giữa các nguồn / Consistent" : "Sai lệch tên CPU giữa WMI / Registry / SMBIOS"));

        if (!cpuMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "CPU",
                "CPU name mismatch across sources",
                $"WMI='{wmiCpu}' vs Registry='{regCpu}' vs SMBIOS='{smbiosCpu}'",
                SpoofingRisk.High));
        }

        // 2. RAM comparison across WMI PhysicalMemory, Win32 PhysMem API, and SMBIOS Type 17
        double wmiRamGb = Math.Round(wmiRamTask.Result.Sum(r => (double)WmiQueryHelper.ULong(r, "Capacity")) / (1024.0 * 1024.0 * 1024.0), 1);
        double csRamGb = Math.Round(WmiQueryHelper.ULong(wmiCsTask.Result.FirstOrDefault() ?? new(), "TotalPhysicalMemory") / (1024.0 * 1024.0 * 1024.0), 1);
        double apiRamGb = GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0 ? Math.Round(kb / (1024.0 * 1024.0), 1) : csRamGb;
        double smbiosRamGb = Math.Round(smbios.MemorySlots.Sum(s => s.CapacityGb), 1);

        var observedRamValues = new[] { wmiRamGb, apiRamGb, smbiosRamGb }.Where(v => v > 0).ToList();
        bool ramMatch = observedRamValues.Count < 2 || (observedRamValues.Max() - observedRamValues.Min()) <= 1.0;

        comparisons.Add(new SourceComparisonRow(
            "RAM",
            wmiRamGb > 0 ? $"{wmiRamGb:0.#} GB" : (csRamGb > 0 ? $"{csRamGb:0.#} GB" : "N/A"),
            apiRamGb > 0 ? $"OS API: {apiRamGb:0.#} GB" : "N/A",
            smbiosRamGb > 0 ? $"{smbiosRamGb:0.#} GB" : "N/A",
            ramMatch,
            ramMatch ? "Dung lượng hợp lệ / Consistent" : "Phát hiện chênh lệch dung lượng RAM"));

        if (!ramMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "RAM",
                "RAM capacity discrepancy",
                $"WMI={wmiRamGb} GB, OS Physical API={apiRamGb} GB, SMBIOS={smbiosRamGb} GB",
                SpoofingRisk.High));
        }

        // 3. System Model comparison across WMI, Registry BIOS, SMBIOS Type 1
        var csRow = wmiCsTask.Result.FirstOrDefault() ?? new();
        string wmiModel = $"{WmiQueryHelper.Str(csRow, "Manufacturer")} {WmiQueryHelper.Str(csRow, "Model")}".Trim();
        string regModel = $"{HardwareService.ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer")} {HardwareService.ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName")}".Trim();
        string smbiosModel = $"{smbios.SystemManufacturer} {smbios.SystemProductName}".Trim();

        bool modelMatch = string.IsNullOrWhiteSpace(regModel) || ModelsMatch(wmiModel, regModel);
        if (modelMatch && !string.IsNullOrWhiteSpace(smbiosModel))
            modelMatch = ModelsMatch(wmiModel, smbiosModel);

        comparisons.Add(new SourceComparisonRow(
            "Model / BIOS",
            DisplayOrDash(wmiModel),
            DisplayOrDash(regModel),
            DisplayOrDash(smbiosModel),
            modelMatch,
            modelMatch ? "Model khớp / Consistent" : "Tên máy không khớp giữa các nguồn"));

        if (!modelMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "System Model",
                "Model identity mismatch",
                $"WMI='{wmiModel}' vs Registry='{regModel}' vs SMBIOS='{smbiosModel}'",
                SpoofingRisk.Medium));
        }

        // 4. Storage SSD vs HDD media truth check (catches HDD renamed with "SSD" in FriendlyName)
        var internalDrives = drivesTask.Result.Where(d => !d.IsExternal).ToList();
        var suspiciousDrive = internalDrives.FirstOrDefault(d =>
            d.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase) &&
            d.MediaCategory.Equals("HDD", StringComparison.OrdinalIgnoreCase));

        string driveSummary = internalDrives.Count == 0
            ? "N/A"
            : string.Join("; ", internalDrives.Select(d => $"{d.Model} ({d.MediaCategory})"));

        comparisons.Add(new SourceComparisonRow(
            "Storage",
            driveSummary,
            "Win32_DiskDrive",
            internalDrives.Count == 0 ? "N/A" : string.Join("; ", internalDrives.Select(d => $"{d.InterfaceType} / {d.MediaCategory}")),
            suspiciousDrive is null,
            suspiciousDrive is null ? "Loại ổ cứng khớp thực tế" : $"Cảnh báo: '{suspiciousDrive.Model}' có tên SSD nhưng bus/media là HDD"));

        if (suspiciousDrive is not null)
        {
            indicators.Add(new SpoofingIndicator(
                "Storage",
                "Drive label vs media mismatch",
                $"Drive '{suspiciousDrive.Model}' reports SSD in name but physical media is {suspiciousDrive.MediaCategory}.",
                SpoofingRisk.High));
        }

        // 5. Check for common spoofing/masking processes (read-only observation)
        var detectedTools = DetectSuspectedTools();
        if (detectedTools.Count > 0)
        {
            indicators.Add(new SpoofingIndicator(
                "Software",
                "Suspicious system/spoofer tools running",
                string.Join(", ", detectedTools),
                SpoofingRisk.Medium));
        }

        SpoofingRisk risk = indicators.Any(i => i.Severity == SpoofingRisk.High)
            ? SpoofingRisk.High
            : indicators.Any(i => i.Severity == SpoofingRisk.Medium)
                ? SpoofingRisk.Medium
                : indicators.Count > 0
                    ? SpoofingRisk.Low
                    : SpoofingRisk.None;

        return new AntiFakeReport(risk, indicators.Count == 0, detectedTools.Count > 0, detectedTools, comparisons, indicators, DateTimeOffset.Now);
    }

    private static List<string> DetectSuspectedTools()
    {
        string[] knownMarkers = { "hwidspoofer", "dmi_edit", "dmiedit", "amidewin", "afudos", "cheatengine" };
        var found = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (knownMarkers.Any(m => p.ProcessName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                        found.Add(p.ProcessName);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool CpuNamesMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        return Normalize(a) == Normalize(b) || Normalize(a).Contains(Normalize(b)) || Normalize(b).Contains(Normalize(a));
    }

    private static bool ModelsMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        return Normalize(a).Contains(Normalize(b)) || Normalize(b).Contains(Normalize(a));
    }

    private static string Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"(\(r\)|\(tm\)|cpu|processor|\s+|@.*$)", "");

    private static string DisplayOrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}
