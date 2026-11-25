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
            cpuMatch ? "Verified — Matches across Windows, Registry & BIOS" : "Mismatch — CPU model name differs across system sources"));

        if (!cpuMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "CPU",
                "CPU name mismatch across sources",
                $"Windows reported '{wmiCpu}', Registry has '{regCpu}', BIOS/SMBIOS has '{smbiosCpu}'.",
                SpoofingRisk.High));
        }

        // 2. RAM comparison across WMI PhysicalMemory, Win32 PhysMem API, and SMBIOS Type 17
        double wmiRamGb = Math.Round(wmiRamTask.Result.Sum(r => (double)WmiQueryHelper.ULong(r, "Capacity")) / (1024.0 * 1024.0 * 1024.0), 1);
        double csRamGb = Math.Round(WmiQueryHelper.ULong(wmiCsTask.Result.FirstOrDefault() ?? new(), "TotalPhysicalMemory") / (1024.0 * 1024.0 * 1024.0), 1);
        double apiRamGb = GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0 ? Math.Round(kb / (1024.0 * 1024.0), 1) : csRamGb;
        double smbiosRamGb = Math.Round(smbios.MemorySlots.Sum(s => s.CapacityGb), 1);

        bool ramMatch = RamValuesConsistent([wmiRamGb, apiRamGb, smbiosRamGb]);

        comparisons.Add(new SourceComparisonRow(
            "RAM",
            wmiRamGb > 0 ? $"{wmiRamGb:0.#} GB" : (csRamGb > 0 ? $"{csRamGb:0.#} GB" : "N/A"),
            apiRamGb > 0 ? $"Windows API: {apiRamGb:0.#} GB" : "N/A",
            smbiosRamGb > 0 ? $"{smbiosRamGb:0.#} GB" : "N/A",
            ramMatch,
            ramMatch ? "Verified — RAM capacity matches hardware" : "Mismatch — Installed RAM capacity differs across sources"));

        if (!ramMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "RAM",
                "RAM capacity mismatch",
                $"WMI reports {wmiRamGb} GB, Windows API reports {apiRamGb} GB, BIOS reports {smbiosRamGb} GB.",
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
            "Laptop Model",
            DisplayOrDash(wmiModel),
            DisplayOrDash(regModel),
            DisplayOrDash(smbiosModel),
            modelMatch,
            modelMatch ? "Verified — Model name matches BIOS firmware" : "Mismatch — Model name differs between Windows and BIOS"));

        if (!modelMatch)
        {
            indicators.Add(new SpoofingIndicator(
                "Laptop Model",
                "Laptop model mismatch",
                $"Windows reported '{wmiModel}', Registry has '{regModel}', BIOS/SMBIOS has '{smbiosModel}'.",
                SpoofingRisk.Medium));
        }

        // 4. Storage SSD vs HDD media truth check (catches HDD renamed with "SSD" in FriendlyName)
        var internalDrives = drivesTask.Result.Where(d => !d.IsExternal).ToList();
        var suspiciousDrive = FindMislabeledSsd(drivesTask.Result);

        string driveSummary = internalDrives.Count == 0
            ? "N/A"
            : string.Join("; ", internalDrives.Select(d => $"{d.Model} ({d.MediaCategory})"));

        comparisons.Add(new SourceComparisonRow(
            "Storage Drive",
            driveSummary,
            "Windows Disk Driver",
            internalDrives.Count == 0 ? "N/A" : string.Join("; ", internalDrives.Select(d => $"{d.InterfaceType} / {d.MediaCategory}")),
            suspiciousDrive is null,
            suspiciousDrive is null ? "Verified — Physical drive type matches label" : $"Warning: '{suspiciousDrive.Model}' says SSD in its name, but the hardware is HDD"));

        if (suspiciousDrive is not null)
        {
            indicators.Add(new SpoofingIndicator(
                "Storage",
                "Mislabeled drive (HDD labeled as SSD)",
                $"Drive '{suspiciousDrive.Model}' includes 'SSD' in its name, but its physical hardware type is {suspiciousDrive.MediaCategory}.",
                SpoofingRisk.High));
        }

        // 5. Check for common spoofing/masking processes (read-only observation)
        var detectedTools = DetectSuspectedTools();
        if (detectedTools.Count > 0)
        {
            indicators.Add(new SpoofingIndicator(
                "Running Software",
                "Known hardware-spoofing utility running",
                string.Join(", ", detectedTools),
                SpoofingRisk.Medium));
        }

        SpoofingRisk risk = AggregateRisk(indicators);

        return new AntiFakeReport(risk, indicators.Count == 0, detectedTools.Count > 0, detectedTools, comparisons, indicators, DateTimeOffset.Now);
    }

    internal static readonly string[] SpoofToolMarkers =
        { "hwidspoofer", "dmi_edit", "dmiedit", "amidewin", "afudos", "cheatengine" };

    internal static bool IsSuspectProcess(string processName) =>
        SpoofToolMarkers.Any(m => processName.Contains(m, StringComparison.OrdinalIgnoreCase));

    internal static bool RamValuesConsistent(IEnumerable<double> values, double toleranceGb = 1.0)
    {
        var observed = values.Where(v => v > 0).ToList();
        return observed.Count < 2 || (observed.Max() - observed.Min()) <= toleranceGb;
    }

    internal static StorageDriveDetails? FindMislabeledSsd(IEnumerable<StorageDriveDetails> drives) =>
        drives.FirstOrDefault(d =>
            !d.IsExternal &&
            d.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase) &&
            d.MediaCategory.Equals("HDD", StringComparison.OrdinalIgnoreCase));

    internal static SpoofingRisk AggregateRisk(IReadOnlyCollection<SpoofingIndicator> indicators) =>
        indicators.Any(i => i.Severity == SpoofingRisk.High)
            ? SpoofingRisk.High
            : indicators.Any(i => i.Severity == SpoofingRisk.Medium)
                ? SpoofingRisk.Medium
                : indicators.Count > 0
                    ? SpoofingRisk.Low
                    : SpoofingRisk.None;

    private static List<string> DetectSuspectedTools()
    {
        var found = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (IsSuspectProcess(p.ProcessName))
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

    internal static bool ModelsMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        return Normalize(a).Contains(Normalize(b)) || Normalize(b).Contains(Normalize(a));
    }

    internal static string Normalize(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"(\(r\)|\(tm\)|cpu|processor|\s+|@.*$)", "");

    private static string DisplayOrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}
