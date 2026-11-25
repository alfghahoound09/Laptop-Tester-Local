using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;

namespace LaptopTester.App.ViewModels;

public partial class KeyTileState : ObservableObject
{
    public KeyTileState(string code, string label, double width = 46)
    {
        Code = code;
        Label = label;
        Width = width;
        KeyValue = Enum.TryParse<Key>(code, true, out var parsed) ? parsed : null;
    }

    public string Code { get; }
    public string Label { get; }
    public double Width { get; }
    public Key? KeyValue { get; }
    [ObservableProperty] private bool _isPressed;
    [ObservableProperty] private bool _isTested;
}

public partial class DeviceHubViewModel : ObservableObject, IDisposable
{
    private readonly UsbConnectivityService _usbService;
    private readonly AudioCameraService _audioService;
    private readonly BatteryService _batteryService;
    private readonly TestResultsStore _store;

    [ObservableProperty] private string _activeSection = "Hub";

    // Keyboard
    [ObservableProperty] private string _lastKeyText = "Press any key on your keyboard to test it...";
    [ObservableProperty] private int _testedKeyCount;
    public ObservableCollection<ObservableCollection<KeyTileState>> KeyboardRows { get; } = new();

    // Display
    [ObservableProperty] private string _displaySummary = "Full-screen color and grid checks to spot dead pixels, color tints, or light bleed";
    public ObservableCollection<DisplayDetails> Displays { get; } = new();
    public event Action<string>? LaunchDisplayFullScreenRequested;

    // Touchpad
    [ObservableProperty] private int _leftClicks;
    [ObservableProperty] private int _rightClicks;
    [ObservableProperty] private int _middleClicks;
    [ObservableProperty] private int _wheelEvents;

    // Speaker & Mic
    [ObservableProperty] private string _audioSummary = "";
    [ObservableProperty] private bool _isRecordingMic;
    [ObservableProperty] private string _micButtonText = "Start Recording";
    [ObservableProperty] private double _micLevelPercent;
    [ObservableProperty] private bool _hasRecordedAudio;
    public ObservableCollection<string> DetectedCameras { get; } = new();

    // USB & Connectivity
    [ObservableProperty] private string _usbWatcherLog = "Plug or unplug a USB device into each port to confirm Windows detects it";
    [ObservableProperty] private string _bluetoothSummary = "Not scanned yet";
    public ObservableCollection<UsbControllerDetails> UsbControllers { get; } = new();
    public ObservableCollection<UsbDeviceDetails> UsbDevices { get; } = new();
    public ObservableCollection<WifiNetworkSummary> WifiNetworks { get; } = new();

    // Charger & Physical
    [ObservableProperty] private string _chargerSummary = "Not checked yet";
    public ObservableCollection<ChecklistToggleItem> PhysicalChecklist { get; } = new();

    public DeviceHubViewModel(
        UsbConnectivityService usbService,
        AudioCameraService audioService,
        BatteryService batteryService,
        TestResultsStore store,
        LocalizationService loc)
    {
        _usbService = usbService;
        _audioService = audioService;
        _batteryService = batteryService;
        _store = store;
        _ = loc;

        _usbService.UsbTopologyChanged += (_, msg) =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UsbWatcherLog = $"{DateTime.Now:HH:mm:ss} — {msg}";
                _ = ScanUsbAsync();
            });

        _audioService.MicrophoneLevelMeasured += (_, lvl) =>
            Application.Current?.Dispatcher.Invoke(() => MicLevelPercent = Math.Round(lvl * 100.0, 1));

        RefreshLabels();
        InitKeyboardRows();
        InitPhysicalChecklist();
        RefreshAudioDevices();
    }

    private void RefreshLabels()
    {
        MicButtonText = IsRecordingMic ? "Stop Recording" : "Start Recording";
    }

    [RelayCommand]
    public void SelectSection(string section)
    {
        ActiveSection = section;
        if (section == "USB")
        {
            _usbService.StartUsbWatcher();
            _ = ScanUsbAsync();
        }
        else if (section == "Connectivity")
        {
            _ = ScanConnectivityAsync();
        }
        else if (section == "Display")
        {
            Displays.Clear();
            foreach (var d in HardwareService.EnumerateDisplays()) Displays.Add(d);
        }
        else if (section == "Camera")
        {
            _ = ScanCamerasAsync();
        }
        else if (section == "Charger")
        {
            _ = CheckChargerAsync();
        }
    }

    public void RecordKeyEvent(string keyCode, bool isDown)
    {
        bool hasParsedKey = Enum.TryParse<Key>(keyCode, true, out var incomingKey);
        foreach (var row in KeyboardRows)
        {
            foreach (var tile in row)
            {
                bool matches = (hasParsedKey && tile.KeyValue == incomingKey)
                    || tile.Code.Equals(keyCode, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    tile.IsPressed = isDown;
                    if (isDown)
                    {
                        tile.IsTested = true;
                        LastKeyText = $"Last Key: {tile.Label} ({keyCode})";
                    }
                }
            }
        }
        TestedKeyCount = KeyboardRows.SelectMany(r => r).Count(k => k.IsTested);
    }

    [RelayCommand]
    private void ResetKeyboard()
    {
        foreach (var tile in KeyboardRows.SelectMany(r => r))
        {
            tile.IsPressed = false;
            tile.IsTested = false;
        }
        TestedKeyCount = 0;
        LastKeyText = "Keyboard reset — press any key to test";
    }

    [RelayCommand]
    private void MarkDeviceResult(string spec)
    {
        var parts = spec.Split('|');
        if (parts.Length < 4) return;
        var st = parts[3].Equals("Pass", StringComparison.OrdinalIgnoreCase) ? TestStatus.Passed : TestStatus.Failed;
        _store.Upsert(parts[0], parts[1], parts[2], st, st == TestStatus.Passed ? "User verified PASS" : "User marked FAIL");
    }

    [RelayCommand]
    private void LaunchDisplayTest(string mode) => LaunchDisplayFullScreenRequested?.Invoke(mode);

    [RelayCommand]
    private void ResetTouchpad()
    {
        LeftClicks = 0;
        RightClicks = 0;
        MiddleClicks = 0;
        WheelEvents = 0;
    }

    public void RefreshAudioDevices()
    {
        var dev = _audioService.GetAudioDevices();
        AudioSummary = $"Speakers ({dev.OutputDeviceCount}): {dev.PrimaryOutputName}  ·  Mic ({dev.InputDeviceCount}): {dev.PrimaryInputName}";
    }

    [RelayCommand] private void PlayLeftSpeaker() => _audioService.PlayStereoTest(true, false, 440);
    [RelayCommand] private void PlayRightSpeaker() => _audioService.PlayStereoTest(false, true, 523);
    [RelayCommand] private void PlayBothSpeakers() => _audioService.PlayStereoTest(true, true, 440);
    [RelayCommand] private void PlaySweep() => _audioService.PlaySweepTest();
    [RelayCommand] private void StopAudio() => _audioService.StopPlayback();

    [RelayCommand]
    private void ToggleMicRecording()
    {
        if (IsRecordingMic)
        {
            _audioService.StopMicrophoneRecording();
            IsRecordingMic = false;
            MicLevelPercent = 0;
            HasRecordedAudio = _audioService.HasRecordedAudio;
        }
        else
        {
            IsRecordingMic = _audioService.StartMicrophoneRecording();
        }
        RefreshLabels();
    }

    [RelayCommand]
    private void PlayRecordedMic() => _audioService.PlayRecordedMicrophone();

    [RelayCommand]
    public async Task ScanCamerasAsync()
    {
        DetectedCameras.Clear();
        var cams = await _audioService.EnumerateCamerasAsync();
        if (cams.Count == 0) DetectedCameras.Add("No camera devices returned (or restricted)");
        else foreach (var c in cams) DetectedCameras.Add(c);
    }

    [RelayCommand]
    public async Task ScanUsbAsync()
    {
        var (controllers, devices) = await _usbService.ScanUsbAsync();
        UsbControllers.Clear();
        foreach (var c in controllers) UsbControllers.Add(c);
        UsbDevices.Clear();
        foreach (var d in devices) UsbDevices.Add(d);
    }

    [RelayCommand]
    public async Task ScanConnectivityAsync()
    {
        var wifiTask = _usbService.ScanWifiAsync();
        var btTask = _usbService.ScanBluetoothAsync();
        await Task.WhenAll(wifiTask, btTask);

        WifiNetworks.Clear();
        foreach (var n in wifiTask.Result.Networks) WifiNetworks.Add(n);
        var bt = btTask.Result;
        BluetoothSummary = bt.HasRadio ? $"{bt.RadioName} ({bt.Address})" : "No Bluetooth radio detected";
        _store.Upsert("connectivity", "USB / Wi-Fi / Bluetooth", "Connectivity", TestStatus.Passed,
            $"{WifiNetworks.Count} Wi-Fi networks · BT: {BluetoothSummary}");
    }

    [RelayCommand]
    public async Task CheckChargerAsync()
    {
        var b = await _batteryService.GetBatteryInfoAsync();
        ChargerSummary = !b.HasBattery
            ? "Desktop / Wall Power Only (No Battery)"
            : b.IsOnAcPower
                ? $"Charger connected · {b.ChargePercent}% · Charge rate: {b.RateWatts:0.0}W · Voltage: {b.VoltageVolts:0.00}V"
                : $"Running on battery · {b.ChargePercent}% · Draining: {b.RateWatts:0.0}W (Plug in charger to verify charging)";
    }

    private void InitKeyboardRows()
    {
        (string, string, double)[][] layout =
        [
            [("Escape","Esc",46),("F1","F1",42),("F2","F2",42),("F3","F3",42),("F4","F4",42),("F5","F5",42),("F6","F6",42),("F7","F7",42),("F8","F8",42),("F9","F9",42),("F10","F10",42),("F11","F11",42),("F12","F12",42),("Delete","Del",48)],
            [("Oem3","`",42),("D1","1",42),("D2","2",42),("D3","3",42),("D4","4",42),("D5","5",42),("D6","6",42),("D7","7",42),("D8","8",42),("D9","9",42),("D0","0",42),("OemMinus","-",42),("OemPlus","=",42),("Back","Backspace",84)],
            [("Tab","Tab",64),("Q","Q",42),("W","W",42),("E","E",42),("R","R",42),("T","T",42),("Y","Y",42),("U","U",42),("I","I",42),("O","O",42),("P","P",42),("OemOpenBrackets","[",42),("Oem6","]",42),("Oem5","\\",62)],
            [("Capital","Caps",74),("A","A",42),("S","S",42),("D","D",42),("F","F",42),("G","G",42),("H","H",42),("J","J",42),("K","K",42),("L","L",42),("Oem1",";",42),("OemQuotes","'",42),("Return","Enter",88)],
            [("LeftShift","Shift",94),("Z","Z",42),("X","X",42),("C","C",42),("V","V",42),("B","B",42),("N","N",42),("M","M",42),("Oemcomma",",",42),("OemPeriod",".",42),("OemQuestion","/",42),("RightShift","Shift",102)],
            [("LeftCtrl","Ctrl",58),("LWin","Win",50),("LeftAlt","Alt",50),("Space","Space",248),("RightAlt","Alt",50),("RightCtrl","Ctrl",58),("Left","←",42),("Up","↑",42),("Down","↓",42),("Right","→",42)]
        ];

        foreach (var r in layout)
        {
            var row = new ObservableCollection<KeyTileState>();
            foreach (var (code, label, w) in r) row.Add(new KeyTileState(code, label, w));
            KeyboardRows.Add(row);
        }
    }

    private void InitPhysicalChecklist()
    {
        PhysicalChecklist.Add(new ChecklistToggleItem("Hinges open and close smoothly with no looseness or cracking"));
        PhysicalChecklist.Add(new ChecklistToggleItem("Chassis, corners, and ports have no dents, cracks, or bent pins"));
        PhysicalChecklist.Add(new ChecklistToggleItem("Bottom cover screws are intact with no stripped heads or missing seals"));
        PhysicalChecklist.Add(new ChecklistToggleItem("Screen has no severe light bleed, pressure spots, or keyboard marks"));
        PhysicalChecklist.Add(new ChecklistToggleItem("Cooling fans spin smoothly without rattling, grinding, or buzzing"));
    }

    public void Dispose()
    {
        _usbService.Dispose();
        _audioService.Dispose();
    }
}
