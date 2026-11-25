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
    [ObservableProperty] private string _tileBattery = "";
    [ObservableProperty] private string _tileAntiFake = "";
    [ObservableProperty] private string _tileBenchmark = "";
    [ObservableProperty] private string _systemInfoHeader = "";
    [ObservableProperty] private string _aboutTitle = "";
    [ObservableProperty] private string _aboutDesc = "";
    [ObservableProperty] private string _tipText = "";
    [ObservableProperty] private string _rescanLabel = "Quét lại";
    [ObservableProperty] private string _quickTestBtnLabel = "Quy Trình / Quick Test (F2)";

    [ObservableProperty] private string _modelText = "Scanning...";
    [ObservableProperty] private string _osText = "—";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _storageText = "—";
    [ObservableProperty] private string _displayText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _privilegeBadge = "Standard User";
    [ObservableProperty] private string _completedCountText = "0 tests recorded";

    public DashboardViewModel(HardwareService hardwareService, LocalizationService loc, TestResultsStore store)
    {
        _hardwareService = hardwareService;
        _loc = loc;
        _store = store;

        _loc.LanguageChanged += (_, _) => RefreshLabels();
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
        CompletedCountText = _loc.Pick(
            $"{_store.Results.Count} hạng mục đã kiểm tra trong phiên",
            $"{_store.Results.Count} checks recorded this session");
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("Dashboard.Title");
        SubtitleText = _loc.Get("Dashboard.Subtitle");
        TileHardware = _loc.Get("Dashboard.Tile.Hardware");
        TileBattery = _loc.Get("Dashboard.Tile.Battery");
        TileAntiFake = _loc.Get("Dashboard.Tile.AntiFake");
        TileBenchmark = _loc.Get("Dashboard.Tile.Benchmark");
        SystemInfoHeader = _loc.Get("Dashboard.SystemInfo");
        AboutTitle = _loc.Get("Dashboard.AboutTitle");
        AboutDesc = _loc.Get("Dashboard.AboutDesc");
        TipText = _loc.Get("Dashboard.Tip");
        RescanLabel = _loc.Get("Dashboard.Rescan");
        QuickTestBtnLabel = _loc.Pick("Quy Trình / Quick Test (F2)", "Quick Test Wizard (F2)");
        RefreshCounts();
    }
}
