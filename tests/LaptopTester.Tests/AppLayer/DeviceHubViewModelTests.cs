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
            var tiles = vm.KeyboardRows.SelectMany(r => r).ToList();
            Assert.Equal(tiles.Count, tiles.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(tiles, t => Assert.NotNull(t.KeyValue));

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

            Assert.Equal(tiles.Count, vm.TestedKeyCount);

            vm.ResetKeyboardCommand.Execute(null);
            Assert.Equal(0, vm.TestedKeyCount);
            Assert.All(tiles, t =>
            {
                Assert.False(t.IsPressed);
                Assert.False(t.IsTested);
            });

            // Both "Enter" and "Return" hit the Enter key tile
            vm.RecordKeyEvent("Enter", isDown: true);
            var enterTile = tiles.Single(t => t.Code == "Return");
            Assert.True(enterTile.IsTested);
            vm.ResetKeyboardCommand.Execute(null);
            vm.RecordKeyEvent("Return", isDown: true);
            Assert.True(enterTile.IsTested);
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
