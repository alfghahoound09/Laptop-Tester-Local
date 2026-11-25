using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly HardwareService _hardwareService;
    private readonly LocalizationService _loc;
    private readonly TestResultsStore _store;

    public event Action<(AppTab Tab, string? SubSection)>? NavigateRequested;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _subtitleText = "";
    [ObservableProperty] private string _tileHardware = "";
    [ObservableProperty] private string _tileHardwareSub = "";
    [ObservableProperty] private string _tileBattery = "";
    [ObservableProperty] private string _tileBatterySub = "";
    [ObservableProperty] private string _tileAntiFake = "";
    [ObservableProperty] private string _tileAntiFakeSub = "";
    [ObservableProperty] private string _tileBenchmark = "";
    [ObservableProperty] private string _tileBenchmarkSub = "";
    [ObservableProperty] private string _systemInfoHeader = "";
    [ObservableProperty] private string _aboutTitle = "";
    [ObservableProperty] private string _aboutDesc = "";
    [ObservableProperty] private string _tipText = "";
    [ObservableProperty] private string _rescanLabel = "";
    [ObservableProperty] private string _quickTestBtnLabel = "";
    [ObservableProperty] private string _featuresHeading = "";
    [ObservableProperty] private string _peripheralsHeading = "";
    [ObservableProperty] private string _sessionLabel = "";
    [ObservableProperty] private string _privilegeLabel = "";
    [ObservableProperty] private string _vmNotice = "";

    [ObservableProperty] private string _modelText = "Scanning...";
    [ObservableProperty] private string _osText = "—";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _storageText = "—";
    [ObservableProperty] private string _displayText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _privilegeBadge = "Standard User";
    [ObservableProperty] private string _completedCountText = "0 checks recorded";

    public DashboardViewModel(HardwareService hardwareService, LocalizationService loc, TestResultsStore store)
    {
        _hardwareService = hardwareService;
        _loc = loc;
        _store = store;

        _store.ResultsChanged += (_, _) => RefreshCounts();
        RefreshLabels();
        RefreshCounts();
        _ = ScanSystemAsync();
    }

    [RelayCommand]
    public async Task ScanSystemAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var report = await _hardwareService.ScanHardwareAsync();
            var s = report.Summary;
            ModelText = $"{s.Manufacturer} {s.Model}".Trim();
            OsText = $"{s.OsName} (Build {s.OsBuild})";
            CpuText = $"{s.CpuName} ({s.PhysicalCores}C/{s.LogicalProcessors}T)";
            RamText = s.RamSummary;
            GpuText = s.GpuSummary;
            StorageText = s.StorageSummary;
            DisplayText = s.DisplaySummary;
            BatteryText = s.BatterySummary;
            PrivilegeBadge = s.IsAdministrator ? "Administrator" : "Standard User";
            VmNotice = s.IsVirtualMachine ? "Running inside a Virtual Machine (VM)" : "";
            _store.Upsert("hardware", "Hardware Scan", "Core", TestStatus.Passed, $"{s.CpuName} · {s.RamSummary}");
        }
        catch (Exception ex)
        {
            ModelText = $"Scan warning: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void OpenHardwareTile(string section) => NavigateRequested?.Invoke((AppTab.Hardware, section));

    [RelayCommand]
    private void StartQuickTest() => NavigateRequested?.Invoke((AppTab.QuickTest, null));

    private void RefreshCounts()
    {
        int count = _store.Results.Count;
        CompletedCountText = $"{count} check{(count == 1 ? "" : "s")} completed this session";
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("Dashboard.Title");
        SubtitleText = _loc.Get("Dashboard.Subtitle");
        TileHardware = _loc.Get("Dashboard.Tile.Hardware");
        TileHardwareSub = "CPU, RAM, GPU & screen";
        TileBattery = _loc.Get("Dashboard.Tile.Battery");
        TileBatterySub = "Health, cycles & 30s drain";
        TileAntiFake = _loc.Get("Dashboard.Tile.AntiFake");
        TileAntiFakeSub = "Windows vs. BIOS specs";
        TileBenchmark = _loc.Get("Dashboard.Tile.Benchmark");
        TileBenchmarkSub = "CPU, RAM & cooling test";
        SystemInfoHeader = _loc.Get("Dashboard.SystemInfo");
        AboutTitle = _loc.Get("Dashboard.AboutTitle");
        AboutDesc = _loc.Get("Dashboard.AboutDesc");
        TipText = _loc.Get("Dashboard.Tip");
        RescanLabel = _loc.Get("Dashboard.Rescan");
        QuickTestBtnLabel = "Quick Test Wizard (F2)";
        FeaturesHeading = "Core Diagnostics:";
        PeripheralsHeading = "Devices & Export:";
        SessionLabel = "Current Session:";
        PrivilegeLabel = "Run Mode:";
        RefreshCounts();
    }
}
