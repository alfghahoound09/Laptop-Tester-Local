using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class UsbConnectivityTests
{
    [Theory]
    [InlineData("USB4 Root Router", "USB", "USB4 / 40 Gbps", true)]
    [InlineData("Intel(R) USB 3.20 eXtensible Host Controller", "USB", "USB 3.1 / 10 Gbps", false)]
    [InlineData("USB 3.0 eXtensible Host Controller - 1.0 (Microsoft)", "USB", "USB 3.0 / 5 Gbps", false)]
    [InlineData("USB Composite Device", "USB", "USB 2.0 / Standard", false)]
    [InlineData("UCSI USB Connector Manager", "USB-C", "USB 2.0 / Standard", false)]
    [InlineData("Generic USB Hub", "USB", "USB 2.0 / Standard", true)]
    [InlineData("Thunderbolt(TM) Controller", "USB", "USB4 / 40 Gbps", false)]
    public void ClassifyUsbDevice_DetectsPortTypeSpeedAndHub(
        string name, string expPort, string expSpeed, bool expHub)
    {
        var (portType, speedLabel, isHub) = UsbConnectivityService.ClassifyUsbDevice(name);
        Assert.Equal(expPort, portType);
        Assert.Equal(expSpeed, speedLabel);
        Assert.Equal(expHub, isHub);
    }

    [Theory]
    [InlineData(4, "802.11a")]
    [InlineData(5, "802.11b")]
    [InlineData(6, "802.11g")]
    [InlineData(7, "Wi-Fi 4 (802.11n)")]
    [InlineData(8, "Wi-Fi 5 (802.11ac)")]
    [InlineData(9, "802.11ad (WiGig)")]
    [InlineData(10, "Wi-Fi 6/6E (802.11ax)")]
    [InlineData(11, "Wi-Fi 7 (802.11be)")]
    [InlineData(0, "802.11")]
    public void MapPhyType_MapsDot11PhyEnumValues(int phyType, string expected)
    {
        Assert.Equal(expected, UsbConnectivityService.MapPhyType(phyType));
    }

    [Theory]
    [InlineData(0, "Not Ready")]
    [InlineData(1, "Connected")]
    [InlineData(2, "Ad-hoc")]
    [InlineData(3, "Disconnecting")]
    [InlineData(4, "Disconnected")]
    [InlineData(5, "Associating")]
    [InlineData(6, "Discovering")]
    [InlineData(7, "Authenticating")]
    [InlineData(99, "State 99")]
    public void MapWlanInterfaceState_MapsWin32States(int state, string expected)
    {
        Assert.Equal(expected, UsbConnectivityService.MapWlanInterfaceState(state));
    }

    [Fact]
    public void FormatBluetoothAddress_FormatsSixOctetsInOrder()
    {
        Assert.Equal("A1:B2:C3:D4:E5:F6", UsbConnectivityService.FormatBluetoothAddress(0xA1B2C3D4E5F6UL));
    }

    [Fact]
    public void DedupeNetworks_KeepsStrongestPerSsidAndSortsDescending()
    {
        var list = UsbConnectivityService.DedupeNetworks([
            new WifiNetworkSummary("Office", 40, "Secured", "Wi-Fi 5 (802.11ac)"),
            new WifiNetworkSummary("Home", 65, "Secured", "Wi-Fi 6/6E (802.11ax)"),
            new WifiNetworkSummary("Office", 90, "Secured", "Wi-Fi 6/6E (802.11ax)")
        ]);

        Assert.Equal(2, list.Count);
        Assert.Equal("Office", list[0].Ssid);
        Assert.Equal(90, list[0].SignalPercent);
        Assert.Equal("Home", list[1].Ssid);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task LiveUsbWifiBluetooth_ScansAndWatcherDoNotThrow()
    {
        using var svc = new UsbConnectivityService();

        var (controllers, devices) = await svc.ScanUsbAsync();
        Assert.NotNull(controllers);
        Assert.NotNull(devices);

        svc.StartUsbWatcher();
        svc.StartUsbWatcher();
        svc.StopUsbWatcher();
        svc.StopUsbWatcher();

        var (adapters, networks) = await svc.ScanWifiAsync();
        Assert.NotNull(adapters);
        Assert.All(networks, n => Assert.InRange(n.SignalPercent, 0, 100));

        var bt = await svc.ScanBluetoothAsync();
        if (bt.HasRadio)
            Assert.False(string.IsNullOrWhiteSpace(bt.RadioName));
    }
}
