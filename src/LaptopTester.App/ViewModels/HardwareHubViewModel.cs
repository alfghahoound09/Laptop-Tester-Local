using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public partial class ChecklistToggleItem : ObservableObject
{
    public ChecklistToggleItem(string label, bool isChecked = false)
    {
        Label = label;
        _isChecked = isChecked;
    }

    public string Label { get; }
    [ObservableProperty] private bool _isChecked;
}

public partial class HardwareHubViewModel : ObservableObject
{
    private readonly HardwareService _hardwareService;
    private readonly AntiFakeService _antiFakeService;
    private readonly BatteryService _batteryService;
    private readonly StorageHealthService _storageService;
    private readonly BenchmarkThermalService _benchService;
    private readonly SystemAuditService _auditService;
    private readonly TestResultsStore _store;
    private readonly LocalizationService _loc;
    private CancellationTokenSource? _stressCts;

    [ObservableProperty] private string _activeSubTab = "Hardware";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusBanner = "";

    // Localized Sub-Tabs & Labels
    [ObservableProperty] private string _tabHardwareLabel = "";
    [ObservableProperty] private string _tabAntiFakeLabel = "";
    [ObservableProperty] private string _tabBatteryLabel = "";
    [ObservableProperty] private string _tabThermalLabel = "";
    [ObservableProperty] private string _tabStorageAuditLabel = "";
    [ObservableProperty] private string _stressButtonText = "";

    // Hardware details
    [ObservableProperty] private HardwareReport? _hardwareReport;
    public ObservableCollection<RamModuleDetails> RamModules { get; } = new();
    public ObservableCollection<GpuDetails> Gpus { get; } = new();
    public ObservableCollection<StorageDriveDetails> Drives { get; } = new();
    public ObservableCollection<DisplayDetails> Displays { get; } = new();

    // Anti-Fake
    [ObservableProperty] private AntiFakeReport? _antiFakeReport;
    [ObservableProperty] private string _antiFakeBannerText = "";
    [ObservableProperty] private bool _antiFakeIsClean = true;
    public ObservableCollection<SourceComparisonRow> Comparisons { get; } = new();
    public ObservableCollection<SpoofingIndicator> Indicators { get; } = new();

    // Battery
    [ObservableProperty] private BatteryDetails? _battery;
    [ObservableProperty] private bool _isStressRunning;
    [ObservableProperty] private string _stressStatusText = "";
    [ObservableProperty] private int _stressProgressPercent;
    public ObservableCollection<ChecklistToggleItem> BatteryChecklist { get; } = new();

    // Storage Speed & System Audit
    [ObservableProperty] private string _storageBenchResult = "";
    public ObservableCollection<DriverIssueDetails> DriverIssues { get; } = new();
    public ObservableCollection<StartupEntryDetails> StartupEntries { get; } = new();

    // Thermal & CPU/RAM Benchmark
    [ObservableProperty] private TemperatureSnapshot? _thermal;
    [ObservableProperty] private BenchmarkReport? _benchmark;
    [ObservableProperty] private string _benchmarkStatus = "";

    public HardwareHubViewModel(
        HardwareService hardwareService,
        AntiFakeService antiFakeService,
        BatteryService batteryService,
        StorageHealthService storageService,
        BenchmarkThermalService benchService,
        SystemAuditService auditService,
        TestResultsStore store,
        LocalizationService loc)
    {
        _hardwareService = hardwareService;
        _antiFakeService = antiFakeService;
        _batteryService = batteryService;
        _storageService = storageService;
        _benchService = benchService;
        _auditService = auditService;
        _store = store;
        _loc = loc;

        BatteryChecklist.Add(new ChecklistToggleItem("Set Windows power mode to 'Balanced'"));
        BatteryChecklist.Add(new ChecklistToggleItem("Unplug the charger to measure real battery drain"));
        BatteryChecklist.Add(new ChecklistToggleItem("Close heavy background apps or games before starting"));
        BatteryChecklist.Add(new ChecklistToggleItem("Keep Wi-Fi connected for normal everyday load"));

        RefreshLabels();
        _ = RefreshHardwareAsync();
    }

    private void RefreshLabels()
    {
        TabHardwareLabel = "Hardware Specs";
        TabAntiFakeLabel = "Anti-Fake Check";
        TabBatteryLabel = "Battery Health";
        TabThermalLabel = "Thermals & Speed";
        TabStorageAuditLabel = "Storage & Drivers";
        StressButtonText = IsStressRunning ? _loc.Get("Battery.StressStop") : _loc.Get("Battery.StressStart");

        if (AntiFakeReport is null)
            AntiFakeBannerText = "Click 'Run Genuine Spec Check' to verify your parts against Windows and BIOS firmware.";
        if (!IsStressRunning && StressProgressPercent == 0)
            StressStatusText = "Ready to run a 30-second battery drain stress test";
        if (string.IsNullOrEmpty(StorageBenchResult))
            StorageBenchResult = "Storage speed test has not been run yet";
        if (Benchmark is null)
            BenchmarkStatus = "Click 'Run Benchmark' to test CPU, RAM, and drive speed";
    }

    [RelayCommand]
    public void SelectSubTab(string subTab) => ActiveSubTab = subTab;

    [RelayCommand]
    public async Task RefreshHardwareAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _hardwareService.ScanHardwareAsync();
            HardwareReport = r;
            RamModules.Clear();
            foreach (var m in r.Ram.Modules) RamModules.Add(m);
            Gpus.Clear();
            foreach (var g in r.Gpus) Gpus.Add(g);
            Drives.Clear();
            foreach (var d in r.Drives) Drives.Add(d);
            Displays.Clear();
            foreach (var disp in r.Displays) Displays.Add(disp);
            Battery = r.Battery;
            StatusBanner = $"Scanned {r.Summary.Model} at {r.ScannedAt:HH:mm:ss}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task RunAntiFakeScanAsync()
    {
        IsBusy = true;
        try
        {
            var r = await _antiFakeService.ScanForSpoofingAsync();
            AntiFakeReport = r;
            AntiFakeIsClean = r.IsClean;
            AntiFakeBannerText = r.IsClean
                ? _loc.Get("AntiFake.Clean")
                : $"{_loc.Get("AntiFake.Warning")} ({r.OverallRisk})";

            Comparisons.Clear();
            foreach (var c in r.Comparisons) Comparisons.Add(c);
            Indicators.Clear();
            foreach (var ind in r.Indicators) Indicators.Add(ind);

            _store.Upsert("hardware", "Hardware & Anti-Fake", "Core",
                r.IsClean ? TestStatus.Passed : (r.OverallRisk == SpoofingRisk.High ? TestStatus.Failed : TestStatus.Warning),
                AntiFakeBannerText);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task CheckBatteryAsync()
    {
        IsBusy = true;
        try
        {
            Battery = await _batteryService.GetBatteryInfoAsync();
            if (Battery.HasBattery)
            {
                var st = Battery.HealthPercent is > 0 and < 60 ? TestStatus.Warning : TestStatus.Passed;
                _store.Upsert("battery", "Battery Health", "Power", st,
                    $"Health {Battery.HealthPercent:F0}% · {Battery.CycleCount} cycles · {Battery.FullChargedCapacityMwh}/{Battery.DesignedCapacityMwh} mWh");
            }
            else
            {
                _store.Upsert("battery", "Battery Health", "Power", TestStatus.Passed, "No internal battery / AC Desktop");
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleBatteryStressAsync()
    {
        if (IsStressRunning)
        {
            _stressCts?.Cancel();
            return;
        }

        _stressCts = new CancellationTokenSource();
        IsStressRunning = true;
        StressButtonText = _loc.Get("Battery.StressStop");
        StressProgressPercent = 0;
        try
        {
            var progress = new Progress<BatteryStressProgress>(p =>
            {
                StressProgressPercent = (int)Math.Round(p.ElapsedSeconds * 100.0 / p.TotalSeconds);
                StressStatusText = $"{p.ElapsedSeconds}s / {p.TotalSeconds}s · {p.CurrentPercent}% · {p.StatusText}";
            });
            await _batteryService.RunStressTestAsync(30, progress, _stressCts.Token);
            await CheckBatteryAsync();
            StressStatusText = "Completed 30-second battery drain test";
        }
        catch (OperationCanceledException)
        {
            StressStatusText = "Battery drain test stopped early";
        }
        finally
        {
            IsStressRunning = false;
            StressButtonText = _loc.Get("Battery.StressStart");
            _stressCts.Dispose();
            _stressCts = null;
        }
    }

    [RelayCommand]
    public async Task RunStorageSpeedTestAsync()
    {
        IsBusy = true;
        StorageBenchResult = "Testing 128 MB sequential read and write speed...";
        try
        {
            var (read, write) = await _storageService.RunSpeedTestAsync(sizeMb: 128);
            StorageBenchResult = $"Sequential Read: {read:0.0} MB/s  ·  Write: {write:0.0} MB/s";
            _store.Upsert("storage", "Storage Health & Speed", "Storage", TestStatus.Passed, StorageBenchResult);
        }
        catch (Exception ex)
        {
            StorageBenchResult = $"Benchmark error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task RunSystemAuditAsync()
    {
        IsBusy = true;
        try
        {
            var drvTask = _auditService.ScanDriverIssuesAsync();
            var stTask = _auditService.ScanStartupEntriesAsync();
            await Task.WhenAll(drvTask, stTask);
            DriverIssues.Clear();
            foreach (var d in drvTask.Result) DriverIssues.Add(d);
            StartupEntries.Clear();
            foreach (var s in stTask.Result) StartupEntries.Add(s);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task RefreshThermalAsync()
    {
        Thermal = await _benchService.CaptureThermalSnapshotAsync();
    }

    [RelayCommand]
    public async Task RunBenchmarkAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(msg => BenchmarkStatus = msg);
            Benchmark = await _benchService.RunBenchmarkAsync(includeDisk: true, progress);
            BenchmarkStatus = $"Done · {Benchmark.OverallTier}";
            Thermal = await _benchService.CaptureThermalSnapshotAsync();
        }
        finally { IsBusy = false; }
    }
}
