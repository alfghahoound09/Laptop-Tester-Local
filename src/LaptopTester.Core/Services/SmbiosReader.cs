using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LaptopTester.Core.Services;

public sealed record SmbiosSnapshot(
    string BiosVendor,
    string BiosVersion,
    string BiosReleaseDate,
    string SystemManufacturer,
    string SystemProductName,
    string SystemSerialNumber,
    string SystemSku,
    string BaseboardManufacturer,
    string BaseboardProduct,
    string ProcessorVersion,
    string ProcessorManufacturer,
    string ProcessorSocket,
    int ProcessorMaxMhz,
    int ProcessorCores,
    int ProcessorThreads,
    IReadOnlyList<SmbiosMemorySlot> MemorySlots,
    string? BatteryManufacturer,
    string? BatteryDeviceName,
    int BatteryDesignCapacityMwh,
    bool IsAvailable);

public sealed record SmbiosMemorySlot(
    string DeviceLocator,
    double CapacityGb,
    int SpeedMTs,
    string MemoryType,
    string Manufacturer,
    string PartNumber,
    string SerialNumber);

/// <summary>Reads and parses raw SMBIOS firmware tables via Win32 GetSystemFirmwareTable('RSMB').</summary>
public static class SmbiosReader
{
    private const uint RsmbSignature = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, byte[]? pFirmwareTableBuffer, uint bufferSize);

    public static SmbiosSnapshot Read()
    {
        try
        {
            byte[]? buffer = ReadViaFirmwareApi() ?? ReadViaRegistryFallback();
            if (buffer is null || buffer.Length <= 8)
                return Empty();

            int tableLength = BitConverter.ToInt32(buffer, 4);
            int tableStart = 8;
            int tableEnd = Math.Min(buffer.Length, tableStart + tableLength);

            string biosVendor = "", biosVersion = "", biosDate = "";
            string sysMfg = "", sysProduct = "", sysSerial = "", sysSku = "";
            string boardMfg = "", boardProduct = "";
            string cpuVer = "", cpuMfg = "", cpuSocket = "";
            int cpuMaxMhz = 0, cpuCores = 0, cpuThreads = 0;
            var slots = new List<SmbiosMemorySlot>();
            string? batMfg = null, batName = null;
            int batDesignMwh = 0;

            int pos = tableStart;
            while (pos + 4 <= tableEnd)
            {
                byte type = buffer[pos];
                byte length = buffer[pos + 1];
                if (length < 4 || pos + length > tableEnd)
                    break;

                int stringsStart = pos + length;
                var strings = ReadStringTable(buffer, stringsStart, tableEnd, out int nextOffset);

                switch (type)
                {
                    case 0 when length >= 0x09:
                        biosVendor = GetString(strings, buffer[pos + 0x04]);
                        biosVersion = GetString(strings, buffer[pos + 0x05]);
                        biosDate = GetString(strings, buffer[pos + 0x08]);
                        break;

                    case 1 when length >= 0x08:
                        sysMfg = GetString(strings, buffer[pos + 0x04]);
                        sysProduct = GetString(strings, buffer[pos + 0x05]);
                        sysSerial = GetString(strings, buffer[pos + 0x07]);
                        if (length >= 0x1A)
                            sysSku = GetString(strings, buffer[pos + 0x19]);
                        break;

                    case 2 when length >= 0x06:
                        boardMfg = GetString(strings, buffer[pos + 0x04]);
                        boardProduct = GetString(strings, buffer[pos + 0x05]);
                        break;

                    case 4 when length >= 0x16:
                        cpuSocket = GetString(strings, buffer[pos + 0x04]);
                        cpuMfg = GetString(strings, buffer[pos + 0x07]);
                        cpuVer = GetString(strings, buffer[pos + 0x10]);
                        cpuMaxMhz = BitConverter.ToUInt16(buffer, pos + 0x14);
                        if (length >= 0x26)
                        {
                            cpuCores = buffer[pos + 0x23];
                            cpuThreads = buffer[pos + 0x25];
                        }
                        if (length >= 0x30)
                        {
                            int cores2 = BitConverter.ToUInt16(buffer, pos + 0x2A);
                            int threads2 = BitConverter.ToUInt16(buffer, pos + 0x2E);
                            if (cores2 > cpuCores) cpuCores = cores2;
                            if (threads2 > cpuThreads) cpuThreads = threads2;
                        }
                        break;

                    case 17 when length >= 0x1B:
                        ushort sizeField = BitConverter.ToUInt16(buffer, pos + 0x0C);
                        double capGb = 0;
                        if (sizeField == 0x7FFF && length >= 0x20)
                        {
                            uint extMb = BitConverter.ToUInt32(buffer, pos + 0x1C) & 0x7FFFFFFF;
                            capGb = extMb / 1024.0;
                        }
                        else if (sizeField != 0 && sizeField != 0xFFFF)
                        {
                            capGb = (sizeField & 0x8000) != 0
                                ? (sizeField & 0x7FFF) / (1024.0 * 1024.0)
                                : sizeField / 1024.0;
                        }
                        string loc = GetString(strings, buffer[pos + 0x10]);
                        string bank = GetString(strings, buffer[pos + 0x11]);
                        string slotName = string.IsNullOrWhiteSpace(loc) ? bank : loc;
                        string memType = DecodeMemoryType(buffer[pos + 0x12]);
                        int speed = BitConverter.ToUInt16(buffer, pos + 0x15);
                        if (length >= 0x22)
                        {
                            int cfgSpeed = BitConverter.ToUInt16(buffer, pos + 0x20);
                            if (cfgSpeed > 0 && cfgSpeed < 30000) speed = cfgSpeed;
                        }
                        string mfg = GetString(strings, buffer[pos + 0x17]);
                        string ser = GetString(strings, buffer[pos + 0x18]);
                        string part = GetString(strings, buffer[pos + 0x1A]);
                        slots.Add(new SmbiosMemorySlot(slotName, Math.Round(capGb, 2), speed, memType, mfg, part, ser));
                        break;

                    case 22 when length >= 0x10:
                        batMfg = GetString(strings, buffer[pos + 0x05]);
                        batName = GetString(strings, buffer[pos + 0x08]);
                        int rawCap = BitConverter.ToUInt16(buffer, pos + 0x0A);
                        int mult = length >= 0x16 && buffer[pos + 0x15] > 0 ? buffer[pos + 0x15] : 1;
                        batDesignMwh = rawCap * mult;
                        break;
                }

                pos = nextOffset;
                if (type == 127) // End-of-table structure
                    break;
            }

            return new SmbiosSnapshot(
                biosVendor, biosVersion, biosDate,
                sysMfg, sysProduct, sysSerial, sysSku,
                boardMfg, boardProduct,
                cpuVer, cpuMfg, cpuSocket, cpuMaxMhz, cpuCores, cpuThreads,
                slots, batMfg, batName, batDesignMwh, true);
        }
        catch
        {
            return Empty();
        }
    }

    private static byte[]? ReadViaFirmwareApi()
    {
        try
        {
            uint needed = GetSystemFirmwareTable(RsmbSignature, 0, null, 0);
            if (needed <= 8) return null;
            byte[] buffer = new byte[needed];
            uint written = GetSystemFirmwareTable(RsmbSignature, 0, buffer, needed);
            return written > 8 ? buffer : null;
        }
        catch { return null; }
    }

    private static byte[]? ReadViaRegistryFallback()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mssmbios\Data");
            return key?.GetValue("SMBiosData") as byte[];
        }
        catch { return null; }
    }

    private static SmbiosSnapshot Empty() => new(
        "", "", "", "", "", "", "", "", "", "", "", "", 0, 0, 0,
        Array.Empty<SmbiosMemorySlot>(), null, null, 0, false);

    private static List<string> ReadStringTable(byte[] buffer, int start, int limit, out int nextOffset)
    {
        var list = new List<string>();
        int p = start;
        while (p < limit)
        {
            if (buffer[p] == 0)
            {
                if (list.Count == 0 && p + 1 < limit && buffer[p + 1] == 0)
                {
                    nextOffset = p + 2;
                    return list;
                }
                nextOffset = p + 1;
                return list;
            }

            int end = p;
            while (end < limit && buffer[end] != 0)
                end++;

            list.Add(Encoding.UTF8.GetString(buffer, p, end - p).Trim());
            p = end + 1;
            if (p < limit && buffer[p] == 0)
            {
                nextOffset = p + 1;
                return list;
            }
        }
        nextOffset = limit;
        return list;
    }

    private static string GetString(IReadOnlyList<string> table, byte index) =>
        index > 0 && index <= table.Count ? table[index - 1] : string.Empty;

    private static string DecodeMemoryType(byte code) => code switch
    {
        0x14 => "DDR",
        0x15 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => code == 0 ? "Unknown" : $"Type 0x{code:X2}"
    };
}
