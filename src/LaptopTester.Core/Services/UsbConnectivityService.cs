using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>Enumerates USB controllers/devices, watches live USB plug events, and checks Wi-Fi and Bluetooth.</summary>
public sealed class UsbConnectivityService : IDisposable
{
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;

    public event EventHandler<string>? UsbTopologyChanged;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_RADIO_INFO
    {
        public uint dwSize;
        public ulong address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_AVAILABLE_NETWORK
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public uint uDot11SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucDot11Ssid;
        public int dot11BssType;
        public uint uNumberOfBssids;
        public int bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] dot11PhyTypes;
        public int bMorePhyTypes;
        public uint wlanSignalQuality;
        public int bSecurityEnabled;
        public int dot11DefaultAuthAlgorithm;
        public int dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    public async Task<(IReadOnlyList<UsbControllerDetails> Controllers, IReadOnlyList<UsbDeviceDetails> Devices)> ScanUsbAsync(CancellationToken ct = default)
    {
        var ctrlTask = WmiQueryHelper.QueryAsync("SELECT Name, DeviceID, Status FROM Win32_USBController", ct: ct);
        var pnpTask = WmiQueryHelper.QueryAsync("SELECT Name, DeviceID, Manufacturer, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'", ct: ct);

        await Task.WhenAll(ctrlTask, pnpTask);

        var controllers = ctrlTask.Result
            .Select(r => new UsbControllerDetails(
                WmiQueryHelper.Str(r, "Name", "USB Controller"),
                WmiQueryHelper.Str(r, "DeviceID"),
                WmiQueryHelper.Str(r, "Status", "OK")))
            .ToList();

        var devices = pnpTask.Result
            .Select(r =>
            {
                string name = WmiQueryHelper.Str(r, "Name", "USB Device");
                string id = WmiQueryHelper.Str(r, "DeviceID");
                bool isHub = name.Contains("Hub", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Root", StringComparison.OrdinalIgnoreCase);
                string speed = name.Contains("USB4", StringComparison.OrdinalIgnoreCase) || name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                    ? "USB4 / 40 Gbps"
                    : name.Contains("3.2", StringComparison.OrdinalIgnoreCase) || name.Contains("3.1", StringComparison.OrdinalIgnoreCase)
                        ? "USB 3.1 / 10 Gbps"
                        : name.Contains("3.0", StringComparison.OrdinalIgnoreCase) || name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) || name.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase)
                            ? "USB 3.0 / 5 Gbps"
                            : "USB 2.0 / Standard";
                string portType = name.Contains("Type-C", StringComparison.OrdinalIgnoreCase) || name.Contains("UCSI", StringComparison.OrdinalIgnoreCase)
                    ? "USB-C"
                    : "USB";
                return new UsbDeviceDetails(name, id, portType, speed, WmiQueryHelper.Str(r, "Manufacturer", "Standard"), isHub);
            })
            .OrderBy(d => d.IsHub)
            .ThenBy(d => d.Name)
            .ToList();

        return (controllers, devices);
    }

    public void StartUsbWatcher()
    {
        if (_insertWatcher is not null) return;
        try
        {
            _insertWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'"));
            _insertWatcher.EventArrived += (_, _) => UsbTopologyChanged?.Invoke(this, "USB device connected");
            _insertWatcher.Start();

            _removeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'"));
            _removeWatcher.EventArrived += (_, _) => UsbTopologyChanged?.Invoke(this, "USB device disconnected");
            _removeWatcher.Start();
        }
        catch { }
    }

    public void StopUsbWatcher()
    {
        try { _insertWatcher?.Stop(); _insertWatcher?.Dispose(); } catch { }
        try { _removeWatcher?.Stop(); _removeWatcher?.Dispose(); } catch { }
        _insertWatcher = null;
        _removeWatcher = null;
    }

    public Task<(IReadOnlyList<WifiAdapterDetails> Adapters, IReadOnlyList<WifiNetworkSummary> Networks)> ScanWifiAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var adapters = new List<WifiAdapterDetails>();
            var networks = new List<WifiNetworkSummary>();

            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0)
                    return ((IReadOnlyList<WifiAdapterDetails>)adapters, (IReadOnlyList<WifiNetworkSummary>)networks);

                if (WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr listPtr) == 0 && listPtr != IntPtr.Zero)
                {
                    try
                    {
                        int count = Marshal.ReadInt32(listPtr, 0);
                        int offset = 8; // dwNumberOfItems + dwIndex
                        int structSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

                        for (int i = 0; i < count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(listPtr + offset + i * structSize);
                            string status = info.isState switch
                            {
                                1 => "Connected",
                                4 => "Disconnected",
                                6 => "Associating",
                                _ => $"State {info.isState}"
                            };
                            adapters.Add(new WifiAdapterDetails("Wi-Fi Adapter", info.strInterfaceDescription, status, "", 0, ""));

                            Guid guid = info.InterfaceGuid;
                            if (WlanGetAvailableNetworkList(client, ref guid, 2, IntPtr.Zero, out IntPtr netListPtr) == 0 && netListPtr != IntPtr.Zero)
                            {
                                try
                                {
                                    int netCount = Marshal.ReadInt32(netListPtr, 0);
                                    int netOff = 8;
                                    int netSize = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                                    for (int n = 0; n < netCount; n++)
                                    {
                                        var net = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(netListPtr + netOff + n * netSize);
                                        string ssid = net.uDot11SsidLength > 0 && net.ucDot11Ssid is not null
                                            ? Encoding.UTF8.GetString(net.ucDot11Ssid, 0, (int)Math.Min(32, net.uDot11SsidLength))
                                            : "<Hidden Network>";
                                        if (!string.IsNullOrWhiteSpace(ssid))
                                        {
                                            networks.Add(new WifiNetworkSummary(
                                                ssid,
                                                (int)net.wlanSignalQuality,
                                                net.bSecurityEnabled != 0 ? "Secured" : "Open",
                                                net.dot11PhyTypes?.FirstOrDefault() switch { 8 => "Wi-Fi 6 (802.11ax)", 7 => "Wi-Fi 5 (802.11ac)", 6 => "802.11n", _ => "802.11" }));
                                        }
                                    }
                                }
                                finally { WlanFreeMemory(netListPtr); }
                            }
                        }
                    }
                    finally { WlanFreeMemory(listPtr); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally
            {
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }

            return ((IReadOnlyList<WifiAdapterDetails>)adapters, (IReadOnlyList<WifiNetworkSummary>)networks
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(x => x.SignalPercent).First())
                .OrderByDescending(x => x.SignalPercent)
                .ToList());
        }, ct);
    }

    public async Task<BluetoothDetails> ScanBluetoothAsync(CancellationToken ct = default)
    {
        var pnpTask = WmiQueryHelper.QueryAsync("SELECT Name, Status FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'", ct: ct);

        var radioResult = await Task.Run(() =>
        {
            var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
            if (find == IntPtr.Zero)
                return (Found: false, Name: "", Address: "");
            try
            {
                var info = new BLUETOOTH_RADIO_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>() };
                uint rc = BluetoothGetRadioInfo(radio, ref info);
                string addr = rc == 0
                    ? string.Join(":", BitConverter.GetBytes(info.address).Take(6).Reverse().Select(b => b.ToString("X2")))
                    : "N/A";
                return (Found: true, Name: info.szName ?? "Bluetooth Radio", Address: addr);
            }
            finally
            {
                CloseHandle(radio);
                BluetoothFindRadioClose(find);
            }
        }, ct);

        var pnpRows = await pnpTask;
        var devices = pnpRows.Select(r => WmiQueryHelper.Str(r, "Name")).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        return new BluetoothDetails(
            radioResult.Found || devices.Count > 0,
            radioResult.Found,
            radioResult.Found ? radioResult.Name : (devices.FirstOrDefault() ?? "No Bluetooth Radio"),
            radioResult.Address,
            devices);
    }

    public void Dispose() => StopUsbWatcher();
}
