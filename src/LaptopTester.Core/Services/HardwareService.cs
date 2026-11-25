using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using LaptopTester.Core.Models;
using Microsoft.Win32;

namespace LaptopTester.Core.Services;

internal static class GpuKind
{
    public const string Discrete = "Discrete";
    public const string Integrated = "Integrated";
    public const string Virtual = "Virtual / Basic";
}

/// <summary>Collects real Windows CPU, RAM, GPU, Storage, Display/EDID, and OS specifications.</summary>
public sealed class HardwareService
{
    private readonly BatteryService _batteryService;
    private readonly StorageHealthService _storageService;

    public HardwareService(BatteryService batteryService, StorageHealthService storageService)
    {
        _batteryService = batteryService;
        _storageService = storageService;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }

    public static bool IsCurrentProcessAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public async Task<HardwareReport> ScanHardwareAsync(CancellationToken ct = default)
    {
        var smbios = SmbiosReader.Read();

        var csTask = WmiQueryHelper.QueryAsync("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem", ct: ct);
        var osTask = WmiQueryHelper.QueryAsync("SELECT Caption, BuildNumber FROM Win32_OperatingSystem", ct: ct);
        var cpuTask = WmiQueryHelper.QueryAsync("SELECT Name, Manufacturer, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, Architecture FROM Win32_Processor", ct: ct);
        var ramTask = WmiQueryHelper.QueryAsync("SELECT DeviceLocator, Capacity, Speed, SMBIOSMemoryType, Manufacturer, PartNumber, SerialNumber FROM Win32_PhysicalMemory", ct: ct);
        var gpuTask = WmiQueryHelper.QueryAsync("SELECT Name, DriverVersion, AdapterRAM, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController", ct: ct);
        var drivesTask = _storageService.GetDrivesAsync(ct);
        var batteryTask = _batteryService.GetBatteryInfoAsync(ct);

        await Task.WhenAll(csTask, osTask, cpuTask, ramTask, gpuTask, drivesTask, batteryTask);

        var csRow = csTask.Result.FirstOrDefault() ?? new();
        var osRow = osTask.Result.FirstOrDefault() ?? new();
        var cpuRow = cpuTask.Result.FirstOrDefault() ?? new();

        string regMfg = ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
        string regModel = ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName");
        string mfg = FirstNonEmpty(WmiQueryHelper.Str(csRow, "Manufacturer"), smbios.SystemManufacturer, regMfg, "Unknown");
        string model = FirstNonEmpty(WmiQueryHelper.Str(csRow, "Model"), smbios.SystemProductName, regModel, "Unknown PC");
        string osName = FirstNonEmpty(WmiQueryHelper.Str(osRow, "Caption"), Environment.OSVersion.VersionString);
        string osBuild = WmiQueryHelper.Str(osRow, "BuildNumber", Environment.OSVersion.Version.Build.ToString());

        string regCpuName = ReadRegistryString(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        string cpuName = FirstNonEmpty(WmiQueryHelper.Str(cpuRow, "Name"), regCpuName, smbios.ProcessorVersion, "Unknown CPU");
        int cores = WmiQueryHelper.Int(cpuRow, "NumberOfCores", smbios.ProcessorCores > 0 ? smbios.ProcessorCores : Environment.ProcessorCount);
        int threads = WmiQueryHelper.Int(cpuRow, "NumberOfLogicalProcessors", Environment.ProcessorCount);
        uint maxMhz = (uint)WmiQueryHelper.Int(cpuRow, "MaxClockSpeed", smbios.ProcessorMaxMhz);
        uint curMhz = (uint)WmiQueryHelper.Int(cpuRow, "CurrentClockSpeed", (int)maxMhz);

        var cpu = new CpuDetails(
            cpuName,
            FirstNonEmpty(WmiQueryHelper.Str(cpuRow, "Manufacturer"), smbios.ProcessorManufacturer, "Unknown"),
            FirstNonEmpty(WmiQueryHelper.Str(cpuRow, "SocketDesignation"), smbios.ProcessorSocket, "N/A"),
            cores,
            threads,
            maxMhz,
            curMhz,
            (uint)WmiQueryHelper.Int(cpuRow, "L2CacheSize"),
            (uint)WmiQueryHelper.Int(cpuRow, "L3CacheSize"),
            RuntimeInformation.OSArchitecture.ToString());

        var ram = BuildRamDetails(ramTask.Result, csRow, smbios);
        var gpus = BuildGpuDetails(gpuTask.Result);
        var drives = drivesTask.Result;
        var displays = EnumerateDisplays();
        var battery = batteryTask.Result;

        bool isVm = DetectVirtualMachine(mfg, model, smbios.BiosVendor);

        string primaryGpu = BuildGpuSummary(gpus);
        var internalDrives = drives.Where(d => !d.IsExternal).ToList();
        double totalStorageGb = internalDrives.Sum(d => d.SizeGb);
        string storageSummary = internalDrives.Count == 0
            ? "No internal drives detected"
            : $"{Math.Round(totalStorageGb)} GB ({internalDrives.Count} {(internalDrives.Count == 1 ? "drive" : "drives")}: {string.Join(", ", internalDrives.Select(d => $"{d.MediaCategory} {Math.Round(d.SizeGb)} GB"))})";

        string displaySummary = displays.FirstOrDefault() is { } disp
            ? $"{disp.WidthPx} x {disp.HeightPx} @ {disp.RefreshRateHz} Hz"
            : "Unknown";

        string batterySummary = !battery.HasBattery
            ? "AC Desktop / No Battery"
            : $"{battery.ChargePercent}% ({(battery.IsCharging ? "Charging" : battery.IsOnAcPower ? "AC Plugged" : "Discharging")}) · Health {battery.HealthPercent:F0}%";

        var summary = new SystemSummary(
            mfg, model, osName, osBuild,
            cpu.Name, cpu.Cores, cpu.Threads, cpu.MaxClockMhz,
            ram.TotalInstalledGb,
            $"{ram.TotalInstalledGb:0.#} GB {ram.MemoryType}{(ram.SpeedMhz > 0 ? $"-{ram.SpeedMhz}" : "")}",
            primaryGpu,
            storageSummary,
            displaySummary,
            batterySummary,
            isVm,
            IsCurrentProcessAdministrator());

        return new HardwareReport(summary, cpu, ram, gpus, drives, displays, battery, DateTimeOffset.Now);
    }

    private static RamDetails BuildRamDetails(
        IReadOnlyList<Dictionary<string, object?>> wmiRows,
        Dictionary<string, object?> csRow,
        SmbiosSnapshot smbios)
    {
        var modules = new List<RamModuleDetails>();
        foreach (var row in wmiRows)
        {
            double capGb = Math.Round(WmiQueryHelper.ULong(row, "Capacity") / (1024.0 * 1024.0 * 1024.0), 2);
            if (capGb <= 0) continue;
            uint speed = (uint)WmiQueryHelper.Int(row, "Speed");
            string memType = DecodeSmbiosMemoryType(WmiQueryHelper.Int(row, "SMBIOSMemoryType"));
            modules.Add(new RamModuleDetails(
                WmiQueryHelper.Str(row, "DeviceLocator", $"Slot {modules.Count + 1}"),
                capGb,
                speed,
                memType,
                WmiQueryHelper.Str(row, "Manufacturer", "Unknown"),
                WmiQueryHelper.Str(row, "PartNumber", ""),
                WmiQueryHelper.Str(row, "SerialNumber", "")));
        }

        if (modules.Count == 0 && smbios.MemorySlots.Count > 0)
        {
            foreach (var s in smbios.MemorySlots.Where(x => x.CapacityGb > 0))
                modules.Add(new RamModuleDetails(s.DeviceLocator, s.CapacityGb, (uint)s.SpeedMTs, s.MemoryType, s.Manufacturer, s.PartNumber, s.SerialNumber));
        }

        double usableGb = Math.Round(WmiQueryHelper.ULong(csRow, "TotalPhysicalMemory") / (1024.0 * 1024.0 * 1024.0), 2);
        if (usableGb <= 0)
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref ms))
                usableGb = Math.Round(ms.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 2);
        }
        double physicalGb = GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0
            ? Math.Round(kb / (1024.0 * 1024.0), 2)
            : modules.Sum(m => m.CapacityGb);
        if (physicalGb <= 0) physicalGb = usableGb;

        int totalSlots = Math.Max(modules.Count, smbios.MemorySlots.Count);
        uint primarySpeed = modules.FirstOrDefault()?.SpeedMhz ?? 0;
        string primaryType = modules.FirstOrDefault(m => m.MemoryType != "Unknown")?.MemoryType ?? "RAM";

        return new RamDetails(physicalGb, usableGb, primarySpeed, primaryType, modules.Count, totalSlots, modules);
    }

    internal static string ClassifyGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return GpuKind.Integrated;

        string[] virtualMarkers =
        [
            "Microsoft Basic Display",
            "Microsoft Basic Render",
            "Remote Display",
            "Hyper-V",
            "VMware SVGA",
            "VirtualBox",
            "Parsec",
            "Virtual Display",
            "QXL",
            "Citrix"
        ];
        if (virtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return GpuKind.Virtual;

        string[] discreteMarkers =
        [
            "NVIDIA",
            "GeForce",
            "Quadro",
            "Radeon RX",
            "Radeon Pro",
            "FirePro",
            "Xe MAX"
        ];
        if (discreteMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))
            || Regex.IsMatch(name, @"\bRTX\b|Arc(\(TM\))?\s*[AB]\d", RegexOptions.IgnoreCase))
            return GpuKind.Discrete;

        return GpuKind.Integrated;
    }

    internal static GpuDetails? SelectPrimaryGpu(IReadOnlyList<GpuDetails> gpus) =>
        gpus.FirstOrDefault(g => g.AdapterType == GpuKind.Discrete)
        ?? gpus.FirstOrDefault(g => g.AdapterType == GpuKind.Integrated)
        ?? gpus.FirstOrDefault();

    internal static string BuildGpuSummary(IReadOnlyList<GpuDetails> gpus)
    {
        if (gpus.Count == 0) return "Unknown GPU";

        var active = gpus.Where(g => g.AdapterType != GpuKind.Virtual).ToList();
        if (active.Count == 0) active = gpus.ToList();

        return string.Join(" + ", active
            .OrderByDescending(g => g.AdapterType == GpuKind.Discrete)
            .Select(g =>
            {
                string tag = g.AdapterType switch
                {
                    GpuKind.Discrete => "dGPU",
                    GpuKind.Integrated => "iGPU",
                    _ => "Virtual"
                };
                return $"{g.Name} ({tag})";
            }));
    }

    internal static List<GpuDetails> BuildGpuDetails(
        IReadOnlyList<Dictionary<string, object?>> gpuRows,
        IReadOnlyDictionary<string, double>? registryVramByDesc = null)
    {
        var regVram = registryVramByDesc ?? ReadRegistryGpuVramByDesc();
        var list = new List<GpuDetails>();
        foreach (var row in gpuRows)
        {
            string name = WmiQueryHelper.Str(row, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            double vramGb = Math.Round(WmiQueryHelper.ULong(row, "AdapterRAM") / (1024.0 * 1024.0 * 1024.0), 2);
            if (regVram.TryGetValue(name, out double qwVramGb) && qwVramGb > vramGb)
                vramGb = qwVramGb;
            int w = WmiQueryHelper.Int(row, "CurrentHorizontalResolution");
            int h = WmiQueryHelper.Int(row, "CurrentVerticalResolution");
            string res = w > 0 && h > 0 ? $"{w}x{h}" : "Active";
            string kind = ClassifyGpu(name);
            list.Add(new GpuDetails(name, WmiQueryHelper.Str(row, "DriverVersion", "N/A"), vramGb, kind, res));
        }
        if (list.Count == 0 && registryVramByDesc is null)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var clsKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (clsKey is not null)
                {
                    foreach (var sub in clsKey.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])))
                    {
                        using var adapterKey = clsKey.OpenSubKey(sub);
                        string? desc = adapterKey?.GetValue("DriverDesc")?.ToString()?.Trim();
                        string? ver = adapterKey?.GetValue("DriverVersion")?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            double vramGb = ParseRegistryVramGb(
                                adapterKey?.GetValue("HardwareInformation.qwMemorySize")
                                ?? adapterKey?.GetValue("HardwareInformation.MemorySize"));
                            string kind = ClassifyGpu(desc);
                            list.Add(new GpuDetails(desc, ver ?? "N/A", vramGb, kind, "Active"));
                        }
                    }
                }
            }
            catch { }
        }
        return list;
    }

    internal static double ParseRegistryVramGb(object? raw)
    {
        if (raw is null) return 0;
        ulong bytes = raw switch
        {
            long l when l > 0 => (ulong)l,
            ulong ul => ul,
            int i when i > 0 => (uint)i,
            uint ui => ui,
            byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
            _ => 0
        };
        return bytes > 0 ? Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 2) : 0;
    }

    private static Dictionary<string, double> ReadRegistryGpuVramByDesc()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var clsKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (clsKey is null) return map;
            foreach (var sub in clsKey.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])))
            {
                using var adapterKey = clsKey.OpenSubKey(sub);
                string? desc = adapterKey?.GetValue("DriverDesc")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(desc)) continue;
                double vramGb = ParseRegistryVramGb(
                    adapterKey?.GetValue("HardwareInformation.qwMemorySize")
                    ?? adapterKey?.GetValue("HardwareInformation.MemorySize"));
                if (vramGb > 0 && (!map.TryGetValue(desc, out double prev) || vramGb > prev))
                    map[desc] = vramGb;
            }
        }
        catch { }
        return map;
    }

    public static IReadOnlyList<DisplayDetails> EnumerateDisplays()
    {
        var list = new List<DisplayDetails>();
        var edids = ReadEdidPanels();

        for (uint i = 0; i < 16; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 0x00000001) == 0) continue; // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref dm)) continue;

            bool primary = (dd.StateFlags & 0x00000004) != 0;
            string aspect = ComputeAspectRatio(dm.dmPelsWidth, dm.dmPelsHeight);
            var (mfg, model) = list.Count < edids.Count ? edids[list.Count] : ("Standard", dd.DeviceString);

            list.Add(new DisplayDetails(
                dd.DeviceName,
                mfg,
                model,
                dm.dmPelsWidth,
                dm.dmPelsHeight,
                dm.dmDisplayFrequency,
                dm.dmBitsPerPel,
                primary,
                aspect));
        }
        return list;
    }

    internal static string ComputeAspectRatio(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return string.Empty;
        int g = Gcd(widthPx, heightPx);
        if (g <= 0) return string.Empty;
        int rw = widthPx / g;
        int rh = heightPx / g;
        if (rw == 8 && rh == 5) return "16:10";
        return $"{rw}:{rh}";
    }

    internal static (string Mfg, string Model)? ParseEdid(byte[] edid, string fallbackModel)
    {
        if (edid.Length < 128) return null;
        string mfg = DecodeEdidManufacturer((ushort)((edid[8] << 8) | edid[9]));
        string name = ParseEdidDescriptorName(edid);
        return (mfg, string.IsNullOrWhiteSpace(name) ? fallbackModel : name);
    }

    private static List<(string Mfg, string Model)> ReadEdidPanels()
    {
        var panels = new List<(string, string)>();
        try
        {
            using var dispKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (dispKey is null) return panels;
            foreach (var modelKeyName in dispKey.GetSubKeyNames())
            {
                using var modelKey = dispKey.OpenSubKey(modelKeyName);
                if (modelKey is null) continue;
                foreach (var instName in modelKey.GetSubKeyNames())
                {
                    using var devParams = modelKey.OpenSubKey($@"{instName}\Device Parameters");
                    if (devParams?.GetValue("EDID") is byte[] edid && ParseEdid(edid, modelKeyName) is { } parsed)
                        panels.Add(parsed);
                }
            }
        }
        catch { }
        return panels;
    }

    internal static string DecodeEdidManufacturer(ushort raw)
    {
        char c1 = (char)(((raw >> 10) & 0x1F) + 'A' - 1);
        char c2 = (char)(((raw >> 5) & 0x1F) + 'A' - 1);
        char c3 = (char)((raw & 0x1F) + 'A' - 1);
        string code = new(new[] { c1, c2, c3 });
        return code switch
        {
            "AUO" => "AU Optronics (AUO)",
            "BOE" => "BOE Technology",
            "CMN" => "Chi Mei / Innolux",
            "LGD" => "LG Display",
            "SDC" => "Samsung Display",
            "SHP" => "Sharp",
            "DEL" => "Dell",
            "LEN" => "Lenovo",
            _ => code
        };
    }

    internal static string ParseEdidDescriptorName(byte[] edid)
    {
        for (int off = 54; off <= 108; off += 18)
        {
            if (edid[off] == 0 && edid[off + 1] == 0 && edid[off + 3] == 0xFC)
            {
                return System.Text.Encoding.ASCII.GetString(edid, off + 5, 13).Trim('\n', '\r', ' ', '\0');
            }
        }
        return string.Empty;
    }

    public static string ReadRegistryString(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string DecodeSmbiosMemoryType(int code) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "RAM"
    };

    internal static bool DetectVirtualMachine(string mfg, string model, string biosVendor)
    {
        string combined = $"{mfg} {model} {biosVendor}";
        return combined.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("QEMU", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
