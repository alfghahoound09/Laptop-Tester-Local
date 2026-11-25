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
    public KeyTileState(string code, string label, double x, double y, double width, double height = 46)
    {
        Code = code;
        Label = label;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        KeyValue = Enum.TryParse<Key>(code, true, out var parsed) ? parsed : null;
    }

    public string Code { get; }
    public string Label { get; }
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
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

    // Immediate live status badges per device test
    [ObservableProperty] private TestStatus _keyboardStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _displayStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _touchpadStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _speakerStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _microphoneStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _audioHubStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _usbStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _connectivityStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _cameraStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _chargerStatus = TestStatus.Pending;
    [ObservableProperty] private TestStatus _physicalStatus = TestStatus.Pending;

    // Keyboard
    [ObservableProperty] private string _lastKeyText = "Press any key on your keyboard to test it...";
    [ObservableProperty] private int _testedKeyCount;
    [ObservableProperty] private int _totalKeyCount;
    public ObservableCollection<ObservableCollection<KeyTileState>> KeyboardRows { get; } = new();
    public ObservableCollection<KeyTileState> KeyboardKeys { get; } = new();

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

        _store.ResultsChanged += (_, _) => SyncStatusesFromStore();

        RefreshLabels();
        InitKeyboardRows();
        InitPhysicalChecklist();
        RefreshAudioDevices();
        SyncStatusesFromStore();
    }

    private void SyncStatusesFromStore()
    {
        KeyboardStatus = _store.Get("keyboard")?.Status ?? TestStatus.Pending;
        DisplayStatus = _store.Get("display")?.Status ?? TestStatus.Pending;
        TouchpadStatus = _store.Get("touchpad")?.Status ?? TestStatus.Pending;
        SpeakerStatus = _store.Get("speaker")?.Status ?? TestStatus.Pending;
        MicrophoneStatus = _store.Get("microphone")?.Status ?? TestStatus.Pending;
        UsbStatus = _store.Get("usb")?.Status ?? TestStatus.Pending;
        ConnectivityStatus = _store.Get("connectivity")?.Status ?? TestStatus.Pending;
        CameraStatus = _store.Get("camera")?.Status ?? TestStatus.Pending;
        ChargerStatus = _store.Get("charger")?.Status ?? TestStatus.Pending;
        PhysicalStatus = _store.Get("physical")?.Status ?? TestStatus.Pending;

        AudioHubStatus = (SpeakerStatus, MicrophoneStatus) switch
        {
            (TestStatus.Failed, _) or (_, TestStatus.Failed) => TestStatus.Failed,
            (TestStatus.Passed, TestStatus.Passed) => TestStatus.Passed,
            (TestStatus.Passed, _) or (_, TestStatus.Passed) => TestStatus.Warning,
            _ => TestStatus.Pending
        };
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

    public void RecordKeyEvent(string keyCode, bool isDown, bool? isExtended = null)
    {
        bool hasParsedKey = Enum.TryParse<Key>(keyCode, true, out var incomingKey);
        foreach (var tile in KeyboardKeys)
        {
            bool matches = (hasParsedKey && tile.KeyValue == incomingKey)
                || tile.Code.Equals(keyCode, StringComparison.OrdinalIgnoreCase);

            if (matches && isExtended.HasValue && hasParsedKey && incomingKey == Key.Return)
            {
                // Distinguish physical Numpad Enter (extended flag = true) from Main Enter (false).
                matches = isExtended.Value
                    ? tile.Code.Equals("Enter", StringComparison.OrdinalIgnoreCase)
                    : tile.Code.Equals("Return", StringComparison.OrdinalIgnoreCase);
            }

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
        TestedKeyCount = KeyboardKeys.Count(k => k.IsTested);
    }

    [RelayCommand]
    private void ResetKeyboard()
    {
        foreach (var tile in KeyboardKeys)
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
        const double pitch = 52.0;
        const double gap = 4.0;
        const double rowH = 46.0;
        const double tallH = 98.0; // Spans 2 rows + vertical spacing (46 + 6 + 46)

        double[] rowY = [0, 62, 114, 166, 218, 270];

        (string Code, string Label, double Col, double Units, double Height)[][] layout =
        [
            // Row 0: Function row + PrintScreen/ScrollLock/Pause (16 keys)
            [
                ("Escape","Esc",0,1,rowH),
                ("F1","F1",2,1,rowH),("F2","F2",3,1,rowH),("F3","F3",4,1,rowH),("F4","F4",5,1,rowH),
                ("F5","F5",6.5,1,rowH),("F6","F6",7.5,1,rowH),("F7","F7",8.5,1,rowH),("F8","F8",9.5,1,rowH),
                ("F9","F9",11,1,rowH),("F10","F10",12,1,rowH),("F11","F11",13,1,rowH),("F12","F12",14,1,rowH),
                ("Snapshot","PrtSc",15.5,1,rowH),("Scroll","ScrLk",16.5,1,rowH),("Pause","Pause",17.5,1,rowH)
            ],
            // Row 1: Number row + Ins/Home/PgUp + Numpad top (21 keys)
            [
                ("Oem3","`",0,1,rowH),("D1","1",1,1,rowH),("D2","2",2,1,rowH),("D3","3",3,1,rowH),("D4","4",4,1,rowH),
                ("D5","5",5,1,rowH),("D6","6",6,1,rowH),("D7","7",7,1,rowH),("D8","8",8,1,rowH),("D9","9",9,1,rowH),
                ("D0","0",10,1,rowH),("OemMinus","-",11,1,rowH),("OemPlus","=",12,1,rowH),("Back","Backspace",13,2,rowH),
                ("Insert","Ins",15.5,1,rowH),("Home","Home",16.5,1,rowH),("PageUp","PgUp",17.5,1,rowH),
                ("NumLock","Num",19,1,rowH),("Divide","/",20,1,rowH),("Multiply","*",21,1,rowH),("Subtract","-",22,1,rowH)
            ],
            // Row 2: QWERTY + Del/End/PgDn + Numpad 7-9/+ (21 keys)
            [
                ("Tab","Tab",0,1.5,rowH),("Q","Q",1.5,1,rowH),("W","W",2.5,1,rowH),("E","E",3.5,1,rowH),("R","R",4.5,1,rowH),
                ("T","T",5.5,1,rowH),("Y","Y",6.5,1,rowH),("U","U",7.5,1,rowH),("I","I",8.5,1,rowH),("O","O",9.5,1,rowH),
                ("P","P",10.5,1,rowH),("OemOpenBrackets","[",11.5,1,rowH),("Oem6","]",12.5,1,rowH),("Oem5","\\",13.5,1.5,rowH),
                ("Delete","Del",15.5,1,rowH),("End","End",16.5,1,rowH),("Next","PgDn",17.5,1,rowH),
                ("NumPad7","7",19,1,rowH),("NumPad8","8",20,1,rowH),("NumPad9","9",21,1,rowH),("Add","+",22,1,tallH)
            ],
            // Row 3: Home row + Numpad 4-6 (16 keys)
            [
                ("Capital","Caps",0,1.75,rowH),("A","A",1.75,1,rowH),("S","S",2.75,1,rowH),("D","D",3.75,1,rowH),("F","F",4.75,1,rowH),
                ("G","G",5.75,1,rowH),("H","H",6.75,1,rowH),("J","J",7.75,1,rowH),("K","K",8.75,1,rowH),("L","L",9.75,1,rowH),
                ("Oem1",";",10.75,1,rowH),("OemQuotes","'",11.75,1,rowH),("Return","Enter",12.75,2.25,rowH),
                ("NumPad4","4",19,1,rowH),("NumPad5","5",20,1,rowH),("NumPad6","6",21,1,rowH)
            ],
            // Row 4: Shift row + Up arrow + Numpad 1-3/Enter (17 keys)
            [
                ("LeftShift","Shift",0,2.25,rowH),("Z","Z",2.25,1,rowH),("X","X",3.25,1,rowH),("C","C",4.25,1,rowH),("V","V",5.25,1,rowH),
                ("B","B",6.25,1,rowH),("N","N",7.25,1,rowH),("M","M",8.25,1,rowH),("OemComma",",",9.25,1,rowH),("OemPeriod",".",10.25,1,rowH),
                ("OemQuestion","/",11.25,1,rowH),("RightShift","Shift",12.25,2.75,rowH),
                ("Up","↑",16.5,1,rowH),
                ("NumPad1","1",19,1,rowH),("NumPad2","2",20,1,rowH),("NumPad3","3",21,1,rowH),("Enter","Enter",22,1,tallH)
            ],
            // Row 5: Bottom modifier row + Left/Down/Right + Numpad 0/. (13 keys)
            [
                ("LeftCtrl","Ctrl",0,1.25,rowH),("LWin","Win",1.25,1.25,rowH),("LeftAlt","Alt",2.5,1.25,rowH),("Space","Space",3.75,6.25,rowH),
                ("RightAlt","Alt",10,1.25,rowH),("RWin","Win",11.25,1.25,rowH),("Apps","Menu",12.5,1.25,rowH),("RightCtrl","Ctrl",13.75,1.25,rowH),
                ("Left","←",15.5,1,rowH),("Down","↓",16.5,1,rowH),("Right","→",17.5,1,rowH),
                ("NumPad0","0",19,2,rowH),("Decimal",".",21,1,rowH)
            ]
        ];

        for (int rIdx = 0; rIdx < layout.Length; rIdx++)
        {
            var row = new ObservableCollection<KeyTileState>();
            double y = rowY[rIdx];
            foreach (var (code, label, col, units, h) in layout[rIdx])
            {
                var tile = new KeyTileState(code, label, col * pitch, y, units * pitch - gap, h);
                row.Add(tile);
                KeyboardKeys.Add(tile);
            }
            KeyboardRows.Add(row);
        }
        TotalKeyCount = KeyboardKeys.Count;
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
