using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public partial class WizardStepItem : ObservableObject
{
    public WizardStepItem(string id, string title, int weight, string? deviceTarget)
    {
        Id = id;
        Title = title;
        Weight = weight;
        DeviceTarget = deviceTarget;
    }

    public string Id { get; }
    public string Title { get; }
    public int Weight { get; }
    public string? DeviceTarget { get; }

    [ObservableProperty] private TestStatus _status = TestStatus.Pending;
    [ObservableProperty] private string _summary = "Chưa kiểm tra";
}

public partial class QuickTestViewModel : ObservableObject
{
    private readonly HardwareService _hardwareService;
    private readonly AntiFakeService _antiFakeService;
    private readonly StorageHealthService _storageService;
    private readonly BatteryService _batteryService;
    private readonly UsbConnectivityService _usbService;
    private readonly QuickTestEngine _engine;
    private readonly HistoryReportService _historyService;
    private readonly TestResultsStore _store;
    private readonly LocalizationService _loc;

    public event Action<string>? NavigateToDeviceRequested;

    [ObservableProperty] private bool _isRunningAuto;
    [ObservableProperty] private int _currentScore = 100;
    [ObservableProperty] private string _verdictText = "Sẵn sàng kiểm tra";
    [ObservableProperty] private VerdictBand _verdictBand = VerdictBand.Good;

    public ObservableCollection<WizardStepItem> Steps { get; } = new();

    public QuickTestViewModel(
        HardwareService hardwareService,
        AntiFakeService antiFakeService,
        StorageHealthService storageService,
        BatteryService batteryService,
        UsbConnectivityService usbService,
        QuickTestEngine engine,
        HistoryReportService historyService,
        TestResultsStore store,
        LocalizationService loc)
    {
        _hardwareService = hardwareService;
        _antiFakeService = antiFakeService;
        _storageService = storageService;
        _batteryService = batteryService;
        _usbService = usbService;
        _engine = engine;
        _historyService = historyService;
        _store = store;
        _loc = loc;

        BuildSteps();
        _store.ResultsChanged += (_, _) => SyncFromStore();
        _loc.LanguageChanged += (_, _) => BuildSteps();
    }

    private void BuildSteps()
    {
        Steps.Clear();
        foreach (var (id, vi, en, weight) in QuickTestEngine.StandardSteps)
        {
            string? target = id switch
            {
                "display" => "Display",
                "keyboard" => "Keyboard",
                "touchpad" => "Touchpad",
                "speaker" => "Audio",
                "microphone" => "Audio",
                "camera" => "Camera",
                "connectivity" => "Connectivity",
                _ => null
            };
            Steps.Add(new WizardStepItem(id, _loc.Pick(vi, en), weight, target));
        }
        SyncFromStore();
    }

    [RelayCommand]
    private async Task RunAutoChecksAsync()
    {
        if (IsRunningAuto) return;
        IsRunningAuto = true;
        try
        {
            var hw = await _hardwareService.ScanHardwareAsync();
            var antiFake = await _antiFakeService.ScanForSpoofingAsync();
            _store.Upsert("hardware", "Hardware & Anti-Fake", "Core",
                antiFake.IsClean ? TestStatus.Passed : TestStatus.Warning,
                $"{hw.Summary.CpuName} · {hw.Summary.RamSummary} · Risk: {antiFake.OverallRisk}");

            var drives = await _storageService.GetDrivesAsync();
            bool smartRisk = drives.Any(d => d.SmartFailurePredicted == true || d.HealthStatus == "Unhealthy");
            _store.Upsert("storage", "Storage & SMART", "Storage",
                smartRisk ? TestStatus.Failed : TestStatus.Passed,
                $"{drives.Count} drives · {(smartRisk ? "SMART warning" : "Healthy")}");

            var bat = await _batteryService.GetBatteryInfoAsync();
            _store.Upsert("battery", "Battery Health", "Power",
                !bat.HasBattery || bat.HealthPercent >= 65 ? TestStatus.Passed : TestStatus.Warning,
                !bat.HasBattery ? "No battery / AC" : $"Health {bat.HealthPercent:F0}% · {bat.CycleCount} cycles");

            var (ctrls, devs) = await _usbService.ScanUsbAsync();
            var bt = await _usbService.ScanBluetoothAsync();
            _store.Upsert("connectivity", "USB / Wi-Fi / BT", "Connectivity", TestStatus.Passed,
                $"USB: {ctrls.Count} ctrl / {devs.Count} dev · BT: {(bt.HasRadio ? "OK" : "N/A")}");
        }
        finally
        {
            IsRunningAuto = false;
            SyncFromStore();
        }
    }

    [RelayCommand]
    private void SetStepStatus(string spec)
    {
        var parts = spec.Split('|');
        if (parts.Length < 2) return;
        string id = parts[0];
        var status = parts[1] switch
        {
            "Pass" => TestStatus.Passed,
            "Fail" => TestStatus.Failed,
            _ => TestStatus.Skipped
        };
        var step = Steps.FirstOrDefault(s => s.Id == id);
        if (step is null) return;
        _store.Upsert(id, step.Title, "QuickTest", status, $"Marked {status}");
    }

    [RelayCommand]
    private void OpenDeviceTest(WizardStepItem step)
    {
        if (!string.IsNullOrEmpty(step.DeviceTarget))
            NavigateToDeviceRequested?.Invoke(step.DeviceTarget);
    }

    [RelayCommand]
    private async Task SaveQuickSessionAsync()
    {
        var hw = await _hardwareService.ScanHardwareAsync();
        var (score, verdict, deductions) = _engine.Evaluate(_store.Results);
        var session = new QuickTestSession(
            Guid.NewGuid().ToString("N")[..8],
            DateTimeOffset.Now,
            $"{hw.Summary.Manufacturer} {hw.Summary.Model}".Trim(),
            hw.Summary.CpuName,
            hw.Summary.RamSummary,
            hw.Summary.StorageSummary,
            score,
            verdict,
            _store.Results.ToList(),
            deductions,
            "Quick Test Wizard session");
        await _historyService.SaveSessionAsync(session);
        VerdictText = $"Saved ({session.Id}) — {CurrentScore}/100";
    }

    private void SyncFromStore()
    {
        foreach (var step in Steps)
        {
            var r = _store.Get(step.Id);
            if (r is not null)
            {
                step.Status = r.Status;
                step.Summary = r.Summary;
            }
        }
        var (score, verdict, _) = _engine.Evaluate(_store.Results);
        CurrentScore = score;
        VerdictBand = verdict;
        VerdictText = verdict switch
        {
            VerdictBand.Good => _loc.Get("Verdict.Good"),
            VerdictBand.ConsiderCarefully => _loc.Get("Verdict.Consider"),
            _ => _loc.Get("Verdict.DoNotBuy")
        };
    }
}
