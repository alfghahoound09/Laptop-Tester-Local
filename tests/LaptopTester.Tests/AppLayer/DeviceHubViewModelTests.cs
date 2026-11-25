using System.Windows.Input;
using LaptopTester.App.ViewModels;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class DeviceHubViewModelTests
{
    private static (DeviceHubViewModel Vm, TestResultsStore Store) CreateSut()
    {
        var store = new TestResultsStore();
        var vm = new DeviceHubViewModel(
            new UsbConnectivityService(),
            new AudioCameraService(),
            new BatteryService(),
            store,
            new LocalizationService());
        return (vm, store);
    }

    [Fact]
    public void KeyboardLayout_EveryTileHasDistinctCodeAndParsableKey_AndRegistersFromWpfKeyToString()
    {
        var (vm, _) = CreateSut();
        using (vm)
        {
            Assert.Equal(6, vm.KeyboardRows.Count);
            Assert.Equal(104, vm.TotalKeyCount);
            Assert.Equal(104, vm.KeyboardKeys.Count);

            var tiles = vm.KeyboardKeys.ToList();
            Assert.Equal(tiles.Count, tiles.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(tiles, t =>
            {
                Assert.NotNull(t.KeyValue);
                Assert.True(t.Width > 0);
                Assert.True(t.Height > 0);
            });

            foreach (var tile in tiles)
            {
                Key parsed = (Key)Enum.Parse(typeof(Key), tile.Code, ignoreCase: true);
                vm.RecordKeyEvent(parsed.ToString(), isDown: true);
                Assert.True(tile.IsPressed);
                Assert.True(tile.IsTested);

                vm.RecordKeyEvent(parsed.ToString(), isDown: false);
                Assert.False(tile.IsPressed);
                Assert.True(tile.IsTested);
            }

            Assert.Equal(104, vm.TestedKeyCount);

            vm.ResetKeyboardCommand.Execute(null);
            Assert.Equal(0, vm.TestedKeyCount);
            Assert.All(tiles, t =>
            {
                Assert.False(t.IsPressed);
                Assert.False(t.IsTested);
            });

            // Main Enter vs Numpad Enter extended-key disambiguation + compatibility with Enter alias
            var enterTile = tiles.Single(t => t.Code == "Return");
            var numEnterTile = tiles.Single(t => t.Code == "Enter");

            vm.RecordKeyEvent("Return", isDown: true, isExtended: false);
            Assert.True(enterTile.IsTested);
            Assert.False(numEnterTile.IsTested);

            vm.RecordKeyEvent("Return", isDown: true, isExtended: true);
            Assert.True(numEnterTile.IsTested);

            vm.ResetKeyboardCommand.Execute(null);
            vm.RecordKeyEvent("Enter", isDown: true);
            Assert.True(enterTile.IsTested);
            vm.ResetKeyboardCommand.Execute(null);
            vm.RecordKeyEvent("Return", isDown: true);
            Assert.True(enterTile.IsTested);
        }
    }

    [Fact]
    public void MarkDeviceResult_ImmediatelyUpdatesStatusPropertiesAcrossAllTests()
    {
        var (vm, _) = CreateSut();
        using (vm)
        {
            Assert.Equal(TestStatus.Pending, vm.KeyboardStatus);
            Assert.Equal(TestStatus.Pending, vm.DisplayStatus);
            Assert.Equal(TestStatus.Pending, vm.AudioHubStatus);

            vm.MarkDeviceResultCommand.Execute("keyboard|Keyboard Key Test|Input|Pass");
            vm.MarkDeviceResultCommand.Execute("display|Screen & Dead Pixel Check|Display|Pass");
            vm.MarkDeviceResultCommand.Execute("touchpad|Touchpad & Pointer|Input|Pass");
            vm.MarkDeviceResultCommand.Execute("speaker|Stereo Speakers|Audio|Pass");
            Assert.Equal(TestStatus.Warning, vm.AudioHubStatus); // Speaker passed, Mic pending

            vm.MarkDeviceResultCommand.Execute("microphone|Microphone Recording|Audio|Pass");
            vm.MarkDeviceResultCommand.Execute("usb|USB Ports|Connectivity|Pass");
            vm.MarkDeviceResultCommand.Execute("connectivity|USB, Wi-Fi & Bluetooth|Connectivity|Pass");
            vm.MarkDeviceResultCommand.Execute("camera|Webcam Preview|Input|Pass");
            vm.MarkDeviceResultCommand.Execute("charger|AC Charger & Power|Power|Pass");
            vm.MarkDeviceResultCommand.Execute("physical|Physical Body & Hinges|Physical|Pass");

            Assert.Equal(TestStatus.Passed, vm.KeyboardStatus);
            Assert.Equal(TestStatus.Passed, vm.DisplayStatus);
            Assert.Equal(TestStatus.Passed, vm.TouchpadStatus);
            Assert.Equal(TestStatus.Passed, vm.SpeakerStatus);
            Assert.Equal(TestStatus.Passed, vm.MicrophoneStatus);
            Assert.Equal(TestStatus.Passed, vm.AudioHubStatus);
            Assert.Equal(TestStatus.Passed, vm.UsbStatus);
            Assert.Equal(TestStatus.Passed, vm.ConnectivityStatus);
            Assert.Equal(TestStatus.Passed, vm.CameraStatus);
            Assert.Equal(TestStatus.Passed, vm.ChargerStatus);
            Assert.Equal(TestStatus.Passed, vm.PhysicalStatus);

            vm.MarkDeviceResultCommand.Execute("microphone|Microphone Recording|Audio|Fail");
            Assert.Equal(TestStatus.Failed, vm.MicrophoneStatus);
            Assert.Equal(TestStatus.Failed, vm.AudioHubStatus);
        }
    }

    [Fact]
    public void MarkDeviceResult_And_ResetTouchpad_WorkAndIgnoreMalformedInput()
    {
        var (vm, store) = CreateSut();
        using (vm)
        {
            vm.MarkDeviceResultCommand.Execute("bad_spec");
            Assert.Empty(store.Results);

            vm.MarkDeviceResultCommand.Execute("keyboard|Keyboard Test|Input|Pass");
            Assert.Equal(TestStatus.Passed, store.Get("keyboard")?.Status);

            vm.LeftClicks = 3;
            vm.RightClicks = 2;
            vm.MiddleClicks = 1;
            vm.WheelEvents = 7;
            vm.ResetTouchpadCommand.Execute(null);
            Assert.Equal(0, vm.LeftClicks);
            Assert.Equal(0, vm.RightClicks);
            Assert.Equal(0, vm.MiddleClicks);
            Assert.Equal(0, vm.WheelEvents);
        }
    }

    [SkippableFact]
    [Trait("Category", "Hardware")]
    public void SelectSection_Display_EnumeratesAttachedDisplays()
    {
        var (vm, _) = CreateSut();
        using (vm)
        {
            vm.SelectSection("Display");
            Skip.If(vm.Displays.Count == 0, "No attached displays detected in headless environment.");
            Assert.All(vm.Displays, d => Assert.True(d.WidthPx > 0));
        }
    }
}
