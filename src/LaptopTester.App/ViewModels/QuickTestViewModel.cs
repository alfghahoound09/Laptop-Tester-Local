using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public partial class WizardStepItem : ObservableObject
{
    public WizardStepItem(string id, string title, int weight, int priority, QuickTestRoundKind kind, string? deviceTarget, string pendingSummary)
    {
        Id = id;
        Title = title;
        Weight = weight;
        Priority = priority;
        Kind = kind;
        DeviceTarget = deviceTarget;
        _summary = pendingSummary;
    }

    public string Id { get; }
    public string Title { get; }
    public int Weight { get; }
    public int Priority { get; }
    public QuickTestRoundKind Kind { get; }
    public string KindLabel => Kind switch
    {
        QuickTestRoundKind.Automated => "Auto",
        QuickTestRoundKind.HybridAuto => "Hybrid",
        _ => "Input"
    };
    public string? DeviceTarget { get; }

    [ObservableProperty] private TestStatus _status = TestStatus.Pending;
    [ObservableProperty] private string _summary;
}

public partial class QuickTestViewModel : ObservableObject
{
    private const int QuickKeyTargetCount = 6;

    private readonly HardwareService _hardwareService;
    private readonly AntiFakeService _antiFakeService;
    private readonly StorageHealthService _storageService;
    private readonly BatteryService _batteryService;
    private readonly UsbConnectivityService _usbService;
    private readonly AudioCameraService _audioService;
    private readonly QuickTestEngine _engine;
    private readonly HistoryReportService _historyService;
    private readonly TestResultsStore _store;
    private readonly LocalizationService _loc;
    private readonly HashSet<string> _quickPressedKeys = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? NavigateToDeviceRequested;
    public event Action<Action<TestStatus?>>? LaunchExpressDisplayRequested;

    [ObservableProperty] private bool _isRunningAuto;
    [ObservableProperty] private int _currentScore = 100;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private VerdictBand _verdictBand = VerdictBand.Good;

    // Hybrid Priority Queue & Fail-Fast Options
    [ObservableProperty] private bool _enableAcousticLoopback = true;
    [ObservableProperty] private bool _stopOnCriticalFailure = true;
    [ObservableProperty] private string _hybridQueueStatus = "Click 'Run Hybrid Quick Test' to prioritize hardware, storage, battery, connectivity, camera & audio first.";
    [ObservableProperty] private bool _hasCriticalAbort;
    [ObservableProperty] private string _criticalAbortMessage = "";

    // Inline Micro-Input Queue (Display -> Touchpad -> Keyboard)
    [ObservableProperty] private string _activeInteractiveStepId = "";
    [ObservableProperty] private string _activeInteractiveTitle = "";
    [ObservableProperty] private string _activeInteractivePrompt = "";
    [ObservableProperty] private bool _quickTouchMoved;
    [ObservableProperty] private bool _quickTouchClicked;
    [ObservableProperty] private bool _quickTouchScrolled;
    [ObservableProperty] private int _quickKeysCount;
    [ObservableProperty] private string _quickKeyLastLabel = "";

    [ObservableProperty] private string _titleText = "";
    [ObservableProperty] private string _subtitleText = "";
    [ObservableProperty] private string _runAutoLabel = "";
    [ObservableProperty] private string _saveLabel = "";
    [ObservableProperty] private string _passLabel = "";
    [ObservableProperty] private string _failLabel = "";
    [ObservableProperty] private string _skipLabel = "";
    [ObservableProperty] private string _openTestLabel = "";
    [ObservableProperty] private string _verdictEyebrow = "";

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
        LocalizationService loc,
        AudioCameraService? audioService = null)
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
        _audioService = audioService ?? new AudioCameraService();

        RefreshLabels();
        BuildSteps();
        _store.ResultsChanged += (_, _) => SyncFromStore();
    }

    private void RefreshLabels()
    {
        TitleText = _loc.Get("QuickTest.Title");
        SubtitleText = "Priority-queued diagnostics: Hardware -> Storage -> Battery -> Connectivity/Camera -> Audio Loopback -> Fast Input Verify";
        RunAutoLabel = "Run Hybrid Quick Test";
        SaveLabel = _loc.Get("Report.SaveSession");
        PassLabel = _loc.Get("QuickTest.Pass");
        FailLabel = _loc.Get("QuickTest.Fail");
        SkipLabel = _loc.Get("QuickTest.Skip");
        OpenTestLabel = "Open Test";
        VerdictEyebrow = "OVERALL VERDICT";
    }

    private void BuildSteps()
    {
        Steps.Clear();
        const string pendingText = "Not tested yet";
        var queue = QuickTestEngine.BuildRoundQueue();
        while (queue.TryDequeue(out var round, out _))
        {
            string? target = round.StepId switch
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
            Steps.Add(new WizardStepItem(round.StepId, round.Title, round.Weight, round.Priority, round.Kind, target, pendingText));
        }
        SyncFromStore();
    }

    [RelayCommand]
    private async Task RunAutoChecksAsync()
    {
        await RunPriorityQueueAsync(bypassAbortStop: false);
    }

    [RelayCommand]
    private async Task ContinueAfterAbortAsync()
    {
        HasCriticalAbort = false;
        CriticalAbortMessage = "";
        await RunPriorityQueueAsync(bypassAbortStop: true);
    }

    private async Task RunPriorityQueueAsync(bool bypassAbortStop)
    {
        if (IsRunningAuto) return;
        IsRunningAuto = true;
        if (!bypassAbortStop)
        {
            HasCriticalAbort = false;
            CriticalAbortMessage = "";
            ResetInlineMicroInputs();
        }

        try
        {
            var queue = QuickTestEngine.BuildRoundQueue();
            HardwareReport? hwSnapshot = null;
            AntiFakeReport? antiFakeSnapshot = null;
            AcousticLoopbackResult? audioLoopback = null;

            // Background prefetch of independent I/O probes while Priority 1 runs cleanly
            Task<IReadOnlyList<string>>? camerasTask = null;
            Task<IReadOnlyList<DriverIssueDetails>>? pnpIssuesTask = null;

            while (queue.TryDequeue(out var round, out _))
            {
                if (bypassAbortStop && _store.Get(round.StepId) is { Status: not TestStatus.Pending })
                    continue;

                if (round.Kind == QuickTestRoundKind.Interactive)
                {
                    await PrecheckInteractiveDriverAsync(round.StepId, pnpIssuesTask);
                    continue;
                }

                MarkRunning(round.StepId, $"Priority {round.Priority}: Running {round.Title}...");
                HybridQueueStatus = $"Running P{round.Priority}: {round.Title}...";

                if (round.StepId == "hardware")
                {
                    var hwTask = _hardwareService.ScanHardwareAsync();
                    var afTask = _antiFakeService.ScanForSpoofingAsync();
                    camerasTask ??= _audioService.EnumerateCamerasAsync();
                    pnpIssuesTask ??= new SystemAuditService().ScanDriverIssuesAsync();

                    await Task.WhenAll(hwTask, afTask);
                    hwSnapshot = hwTask.Result;
                    antiFakeSnapshot = afTask.Result;

                    var hwStatus = antiFakeSnapshot.IsClean
                        ? TestStatus.Passed
                        : antiFakeSnapshot.OverallRisk == SpoofingRisk.High
                            ? TestStatus.Failed
                            : TestStatus.Warning;

                    string hwMsg = antiFakeSnapshot.IsClean
                        ? $"{hwSnapshot.Summary.CpuName} · {hwSnapshot.Summary.RamSummary} · Verified genuine"
                        : $"{hwSnapshot.Summary.CpuName} · Risk: {antiFakeSnapshot.OverallRisk}";

                    _store.Upsert("hardware", round.Title, "Core", hwStatus, hwMsg);

                    if (hwStatus == TestStatus.Failed && StopOnCriticalFailure && !bypassAbortStop)
                    {
                        HasCriticalAbort = true;
                        CriticalAbortMessage = $"Priority 1 Critical Fail: {hwMsg}. Verdict is Do Not Buy — review Anti-Fake or continue manually.";
                        break;
                    }
                }
                else if (round.StepId == "storage")
                {
                    var drives = hwSnapshot?.Drives ?? await _storageService.GetDrivesAsync();
                    bool smartRisk = drives.Any(d => d.SmartFailurePredicted == true || d.HealthStatus == "Unhealthy");
                    bool mislabeledSsd = antiFakeSnapshot?.Indicators.Any(i => i.Category.Equals("Storage", StringComparison.OrdinalIgnoreCase)) == true;

                    var stStatus = (drives.Count == 0 || smartRisk || mislabeledSsd)
                        ? TestStatus.Failed
                        : TestStatus.Passed;

                    string stMsg = smartRisk
                        ? "SMART failure warning detected"
                        : mislabeledSsd
                            ? "Drive reports SSD in name but physical media is HDD"
                            : drives.Count == 0
                                ? "No internal storage drives detected"
                                : $"{drives.Count} drive(s) · SMART Healthy";

                    _store.Upsert("storage", round.Title, "Storage", stStatus, stMsg);

                    if (stStatus == TestStatus.Failed && StopOnCriticalFailure && !bypassAbortStop)
                    {
                        HasCriticalAbort = true;
                        CriticalAbortMessage = $"Priority 2 Critical Fail: {stMsg}. Verdict is Do Not Buy — inspect drive immediately or continue.";
                        break;
                    }
                }
                else if (round.StepId == "battery")
                {
                    var bat = hwSnapshot?.Battery ?? await _batteryService.GetBatteryInfoAsync();
                    var batStatus = !bat.HasBattery
                        ? TestStatus.Passed
                        : bat.HealthPercent is > 0 and < 40
                            ? TestStatus.Failed
                            : bat.HealthPercent is > 0 and < 65
                                ? TestStatus.Warning
                                : TestStatus.Passed;

                    string batMsg = !bat.HasBattery
                        ? "No internal battery / AC Desktop"
                        : $"Health {bat.HealthPercent:F0}% · {bat.CycleCount} cycles · {bat.FullChargedCapacityMwh}/{bat.DesignedCapacityMwh} mWh";

                    _store.Upsert("battery", round.Title, "Power", batStatus, batMsg);
                }
                else if (round.StepId == "connectivity")
                {
                    var usbTask = _usbService.ScanUsbAsync();
                    var btTask = _usbService.ScanBluetoothAsync();
                    var wifiTask = _usbService.ScanWifiAsync();
                    await Task.WhenAll(usbTask, btTask, wifiTask);

                    var (ctrls, devs) = usbTask.Result;
                    var bt = btTask.Result;
                    var (wifiAdapters, wifiNetworks) = wifiTask.Result;

                    var connStatus = ctrls.Count == 0
                        ? TestStatus.Failed
                        : wifiAdapters.Count == 0 && !bt.HasRadio
                            ? TestStatus.Warning
                            : TestStatus.Passed;

                    _store.Upsert("connectivity", round.Title, "Connectivity", connStatus,
                        $"USB: {ctrls.Count} ctrl / {devs.Count} dev · Wi-Fi: {wifiNetworks.Count} net · BT: {(bt.HasRadio ? "OK" : "N/A")}");
                }
                else if (round.StepId == "camera")
                {
                    var cams = camsOrEmpty(await (camerasTask ?? _audioService.EnumerateCamerasAsync()));
                    var issues = await (pnpIssuesTask ?? Task.FromResult<IReadOnlyList<DriverIssueDetails>>([]));
                    bool camDriverErr = issues.Any(i => i.PnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase)
                                                     || i.PnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase));

                    var camStatus = camDriverErr
                        ? TestStatus.Failed
                        : cams.Count > 0
                            ? TestStatus.Passed
                            : TestStatus.Warning;

                    string camMsg = camDriverErr
                        ? "Webcam driver error reported in Device Manager"
                        : cams.Count > 0
                            ? $"{cams[0]} — detected"
                            : "No webcam detected — verify privacy shutter or use Fail";

                    _store.Upsert("camera", round.Title, "Input", camStatus, camMsg);
                }
                else if (round.StepId is "speaker" or "microphone")
                {
                    if (round.StepId == "speaker")
                    {
                        audioLoopback = EnableAcousticLoopback
                            ? await _audioService.RunAcousticLoopbackTestAsync()
                            : BuildDeviceOnlyAudioResult(_audioService.GetAudioDevices());

                        var spkStatus = !audioLoopback.OutputPresent
                            ? TestStatus.Failed
                            : audioLoopback.LoopbackDetected
                                ? TestStatus.Passed
                                : TestStatus.Pending;

                        string spkMsg = !audioLoopback.OutputPresent
                            ? "No speaker playback device detected"
                            : audioLoopback.LoopbackDetected
                                ? audioLoopback.Summary
                                : "Audio playback device ready — confirm sound or Open Test";

                        _store.Upsert("speaker", "Stereo Speakers", "Audio", spkStatus, spkMsg);
                    }
                    else
                    {
                        audioLoopback ??= BuildDeviceOnlyAudioResult(_audioService.GetAudioDevices());
                        var micStatus = !audioLoopback.InputPresent
                            ? TestStatus.Failed
                            : audioLoopback.LoopbackDetected
                                ? TestStatus.Passed
                                : TestStatus.Pending;

                        string micMsg = !audioLoopback.InputPresent
                            ? "No microphone input device detected"
                            : audioLoopback.LoopbackDetected
                                ? audioLoopback.Summary
                                : "Microphone detected — confirm voice or click Pass/Fail";

                        _store.Upsert("microphone", "Microphone Recording", "Audio", micStatus, micMsg);
                    }
                }
            }

            if (!HasCriticalAbort)
                AdvanceInteractiveQueue();
            else
                HybridQueueStatus = CriticalAbortMessage;
        }
        finally
        {
            IsRunningAuto = false;
            SyncFromStore();
        }

        static IReadOnlyList<string> camsOrEmpty(IReadOnlyList<string>? list) => list ?? [];
    }

    private async Task PrecheckInteractiveDriverAsync(string stepId, Task<IReadOnlyList<DriverIssueDetails>>? issuesTask)
    {
        if (issuesTask is not null)
        {
            var issues = await issuesTask;
            string? targetPnp = stepId switch
            {
                "keyboard" => "Keyboard",
                "touchpad" => "Mouse",
                "display" => "Monitor",
                _ => null
            };
            if (targetPnp is not null && issues.Any(i => i.PnpClass.Equals(targetPnp, StringComparison.OrdinalIgnoreCase)))
            {
                string label = Steps.FirstOrDefault(s => s.Id == stepId)?.Title ?? stepId;
                _store.Upsert(stepId, label, "Input", TestStatus.Failed, $"Device Manager reported {targetPnp} driver error");
                return;
            }
        }

        if (stepId == "keyboard")
        {
            var stuck = FindCurrentlyPressedNonModifierKeys();
            if (stuck.Count > 0)
            {
                string label = Steps.FirstOrDefault(s => s.Id == "keyboard")?.Title ?? "Keyboard";
                _store.Upsert("keyboard", label, "Input", TestStatus.Failed, $"Stuck key detected: {string.Join(", ", stuck)}");
            }
        }
    }

    private static List<string> FindCurrentlyPressedNonModifierKeys()
    {
        var stuck = new List<string>();
        try
        {
            if (System.Windows.Application.Current is null) return stuck;
            for (Key k = Key.Back; k <= Key.OemQuotes; k++)
            {
                if (k is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt)
                    continue;
                if (Keyboard.IsKeyDown(k))
                    stuck.Add(k.ToString());
            }
        }
        catch { }
        return stuck;
    }

    private static AcousticLoopbackResult BuildDeviceOnlyAudioResult(AudioDeviceSummary summary) =>
        new(summary.OutputDeviceCount > 0, summary.InputDeviceCount > 0, 0, false,
            summary.OutputDeviceCount > 0 && summary.InputDeviceCount > 0
                ? "Devices detected (acoustic chirp disabled)"
                : "Audio output/input device missing");

    private void MarkRunning(string stepId, string message)
    {
        var step = Steps.FirstOrDefault(s => s.Id == stepId);
        if (step is not null)
        {
            step.Status = TestStatus.Running;
            step.Summary = message;
        }
    }

    public void RecordQuickTouchpadActivity(bool moved, bool clicked, bool scrolled)
    {
        if (ActiveInteractiveStepId != "touchpad") return;
        if (moved) QuickTouchMoved = true;
        if (clicked) QuickTouchClicked = true;
        if (scrolled) QuickTouchScrolled = true;

        if (QuickTouchMoved && QuickTouchClicked && QuickTouchScrolled)
        {
            _store.Upsert("touchpad", "Touchpad & Pointer", "Input", TestStatus.Passed,
                "Verified pointer movement, click & scroll wheel");
        }
    }

    public void RecordQuickKeyPress(string keyCode)
    {
        if (ActiveInteractiveStepId != "keyboard" || string.IsNullOrWhiteSpace(keyCode)) return;
        _quickPressedKeys.Add(keyCode);
        QuickKeysCount = _quickPressedKeys.Count;
        QuickKeyLastLabel = $"Captured {QuickKeysCount} / {QuickKeyTargetCount}: {keyCode}";

        if (QuickKeysCount >= QuickKeyTargetCount)
        {
            _store.Upsert("keyboard", "Keyboard Key Test", "Input", TestStatus.Passed,
                $"Quick sweep passed ({QuickKeysCount} keys responded cleanly)");
        }
    }

    [RelayCommand]
    private void OpenFullKeyboardTest() => NavigateToDeviceRequested?.Invoke("Keyboard");

    [RelayCommand]
    private void LaunchExpressDisplay()
    {
        LaunchExpressDisplayRequested?.Invoke(verdict =>
        {
            if (verdict is { } status)
            {
                _store.Upsert("display", "Screen & Dead Pixel Check", "Display", status,
                    status == TestStatus.Passed ? "User verified screen clean in express check" : "Dead pixel / display defect flagged");
            }
        });
    }

    private void ResetInlineMicroInputs()
    {
        QuickTouchMoved = false;
        QuickTouchClicked = false;
        QuickTouchScrolled = false;
        _quickPressedKeys.Clear();
        QuickKeysCount = 0;
        QuickKeyLastLabel = "";
    }

    private void AdvanceInteractiveQueue()
    {
        var interactiveQueue = QuickTestEngine.BuildRoundQueue(
            QuickTestEngine.DefaultHybridRounds.Where(r => r.Kind == QuickTestRoundKind.Interactive));

        while (interactiveQueue.TryDequeue(out var next, out _))
        {
            var existing = _store.Get(next.StepId);
            if (existing is null || existing.Status == TestStatus.Pending)
            {
                ActiveInteractiveStepId = next.StepId;
                ActiveInteractiveTitle = $"Up Next (Priority {next.Priority}): {next.Title}";
                ActiveInteractivePrompt = next.StepId switch
                {
                    "display" => "Launch the color flash screen, inspect for dead pixels, and press P / Enter to Pass or F to Fail.",
                    "touchpad" => "Swipe across the pad below, tap/click once, and scroll once — auto-passes immediately.",
                    "keyboard" => $"Tap any {QuickKeyTargetCount} keys right now to auto-pass (or click Fail / Open Full 104-Key Test).",
                    _ => ""
                };
                HybridQueueStatus = $"Auto checks complete — waiting on P{next.Priority}: {next.Title}";
                return;
            }
        }

        ActiveInteractiveStepId = "";
        ActiveInteractiveTitle = "";
        ActiveInteractivePrompt = "";
        if (!HasCriticalAbort && CheckedCount > 0)
            HybridQueueStatus = $"Hybrid queue complete — {CheckedCount} / {Steps.Count} checks finished.";
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
        VerdictText = $"Saved ({session.Id}) — Score {CurrentScore}/100";
    }

    private void SyncFromStore()
    {
        int done = 0;
        foreach (var step in Steps)
        {
            var r = _store.Get(step.Id);
            if (r is not null)
            {
                step.Status = r.Status;
                step.Summary = r.Summary;
                if (r.Status != TestStatus.Pending) done++;
            }
        }
        CheckedCount = done;
        var (score, verdict, _) = _engine.Evaluate(_store.Results);
        CurrentScore = score;
        VerdictBand = verdict;
        VerdictText = verdict switch
        {
            VerdictBand.Good => _loc.Get("Verdict.Good"),
            VerdictBand.ConsiderCarefully => _loc.Get("Verdict.Consider"),
            _ => _loc.Get("Verdict.DoNotBuy")
        };
        if (!IsRunningAuto && !string.IsNullOrEmpty(ActiveInteractiveStepId))
            AdvanceInteractiveQueue();
    }
}
