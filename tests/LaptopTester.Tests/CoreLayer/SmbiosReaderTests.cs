using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class SmbiosReaderTests
{
    [Fact]
    public void Parse_Type0Type1Type2_ExtractsBiosSystemAndBaseboard()
    {
        byte[] raw = new SmbiosBuilder()
            .Add(0, 0x12, b =>
            {
                b[0x04] = 1;
                b[0x05] = 2;
                b[0x08] = 3;
            }, "LENOVO", "N3AET12W", "01/15/2025")
            .Add(1, 0x1B, b =>
            {
                b[0x04] = 1;
                b[0x05] = 2;
                b[0x07] = 3;
                b[0x19] = 4;
            }, "LENOVO", "ThinkPad T14 Gen 6", "PF999999", "LENOVO_MT_21ML")
            .Add(2, 0x08, b =>
            {
                b[0x04] = 1;
                b[0x05] = 2;
            }, "LENOVO", "21ML0001US")
            .End()
            .Build();

        var snap = SmbiosReader.Parse(raw);

        Assert.True(snap.IsAvailable);
        Assert.Equal("LENOVO", snap.BiosVendor);
        Assert.Equal("N3AET12W", snap.BiosVersion);
        Assert.Equal("01/15/2025", snap.BiosReleaseDate);
        Assert.Equal("LENOVO", snap.SystemManufacturer);
        Assert.Equal("ThinkPad T14 Gen 6", snap.SystemProductName);
        Assert.Equal("PF999999", snap.SystemSerialNumber);
        Assert.Equal("LENOVO_MT_21ML", snap.SystemSku);
        Assert.Equal("LENOVO", snap.BaseboardManufacturer);
        Assert.Equal("21ML0001US", snap.BaseboardProduct);
    }

    [Fact]
    public void Parse_Type4_StandardAndExtendedCoreThreadCounts()
    {
        byte[] raw = new SmbiosBuilder()
            .Add(4, 0x30, b =>
            {
                b[0x04] = 1;
                b[0x07] = 2;
                b[0x10] = 3;
                BitConverter.GetBytes((ushort)4800).CopyTo(b, 0x14);
                b[0x23] = 8;
                b[0x25] = 16;
                BitConverter.GetBytes((ushort)8).CopyTo(b, 0x2A);
                BitConverter.GetBytes((ushort)16).CopyTo(b, 0x2E);
            }, "FP7", "AMD", "AMD Ryzen 7 7840U")
            .End()
            .Build();

        var snap = SmbiosReader.Parse(raw);
        Assert.Equal("FP7", snap.ProcessorSocket);
        Assert.Equal("AMD", snap.ProcessorManufacturer);
        Assert.Equal("AMD Ryzen 7 7840U", snap.ProcessorVersion);
        Assert.Equal(4800, snap.ProcessorMaxMhz);
        Assert.Equal(8, snap.ProcessorCores);
        Assert.Equal(16, snap.ProcessorThreads);

        byte[] highCoreRaw = new SmbiosBuilder()
            .Add(4, 0x30, b =>
            {
                b[0x04] = 1;
                b[0x07] = 2;
                b[0x10] = 3;
                BitConverter.GetBytes((ushort)3800).CopyTo(b, 0x14);
                b[0x23] = 0xFF;
                b[0x25] = 0xFF;
                BitConverter.GetBytes((ushort)128).CopyTo(b, 0x2A);
                BitConverter.GetBytes((ushort)256).CopyTo(b, 0x2E);
            }, "SP5", "AMD", "EPYC 9754")
            .End()
            .Build();

        var highSnap = SmbiosReader.Parse(highCoreRaw);
        Assert.Equal(128, highSnap.ProcessorCores);
        Assert.Equal(256, highSnap.ProcessorThreads);
    }

    [Fact]
    public void Parse_Type17_DecodesStandardExtendedKbEmptyBankAndMultiSlot()
    {
        byte[] raw = new SmbiosBuilder()
            // Slot 1: 8192 MB DDR5, rated 4800, configured 5600
            .Add(17, 0x28, b =>
            {
                BitConverter.GetBytes((ushort)8192).CopyTo(b, 0x0C);
                b[0x10] = 1;
                b[0x11] = 2;
                b[0x12] = 0x22; // DDR5
                BitConverter.GetBytes((ushort)4800).CopyTo(b, 0x15);
                b[0x17] = 3;
                b[0x18] = 4;
                b[0x1A] = 5;
                BitConverter.GetBytes((ushort)5600).CopyTo(b, 0x20);
            }, "DIMM A", "BANK 0", "Samsung", "S1", "M425R1GB4BB0")
            // Slot 2: 0x7FFF -> extended 32768 MB, bank used when locator index is 0
            .Add(17, 0x28, b =>
            {
                BitConverter.GetBytes((ushort)0x7FFF).CopyTo(b, 0x0C);
                b[0x10] = 0;
                b[0x11] = 1;
                b[0x12] = 0x1A; // DDR4
                BitConverter.GetBytes((ushort)3200).CopyTo(b, 0x15);
                b[0x17] = 2;
                b[0x18] = 3;
                b[0x1A] = 4;
                BitConverter.GetBytes(32768u).CopyTo(b, 0x1C);
            }, "BANK 1", "SK Hynix", "S2", "HMA84GS6DJR8N")
            // Slot 3: KB-flag (0x8000 | 512 KB)
            .Add(17, 0x28, b =>
            {
                BitConverter.GetBytes((ushort)(0x8000 | 512)).CopyTo(b, 0x0C);
                b[0x10] = 1;
                b[0x12] = 0x1E;
            }, "KB Slot")
            // Slot 4: size 0 -> 0 GB
            .Add(17, 0x28, b =>
            {
                BitConverter.GetBytes((ushort)0).CopyTo(b, 0x0C);
                b[0x10] = 1;
                b[0x12] = 0x22;
            }, "Empty Slot")
            .End()
            .Build();

        var snap = SmbiosReader.Parse(raw);

        Assert.Equal(4, snap.MemorySlots.Count);
        Assert.Equal("DIMM A", snap.MemorySlots[0].DeviceLocator);
        Assert.Equal(8.0, snap.MemorySlots[0].CapacityGb);
        Assert.Equal("DDR5", snap.MemorySlots[0].MemoryType);
        Assert.Equal(5600, snap.MemorySlots[0].SpeedMTs);
        Assert.Equal("Samsung", snap.MemorySlots[0].Manufacturer);
        Assert.Equal("S1", snap.MemorySlots[0].SerialNumber);
        Assert.Equal("M425R1GB4BB0", snap.MemorySlots[0].PartNumber);

        Assert.Equal("BANK 1", snap.MemorySlots[1].DeviceLocator);
        Assert.Equal(32.0, snap.MemorySlots[1].CapacityGb);
        Assert.Equal("DDR4", snap.MemorySlots[1].MemoryType);

        Assert.Equal(Math.Round(512.0 / (1024.0 * 1024.0), 2), snap.MemorySlots[2].CapacityGb);
        Assert.Equal(0.0, snap.MemorySlots[3].CapacityGb);
    }

    [Fact]
    public void Parse_Type22_AndStopAtType127()
    {
        byte[] raw = new SmbiosBuilder()
            .Add(22, 0x1A, b =>
            {
                b[0x05] = 1;
                b[0x08] = 2;
                BitConverter.GetBytes((ushort)5000).CopyTo(b, 0x0A);
                b[0x15] = 10;
            }, "SMP", "L22M3P71")
            .End()
            .Add(1, 0x1B, b => { b[0x04] = 1; b[0x05] = 2; }, "SHOULD_NOT_PARSE", "IGNORED")
            .Build();

        var snap = SmbiosReader.Parse(raw);
        Assert.Equal("SMP", snap.BatteryManufacturer);
        Assert.Equal("L22M3P71", snap.BatteryDeviceName);
        Assert.Equal(50000, snap.BatteryDesignCapacityMwh);
        Assert.Equal(string.Empty, snap.SystemManufacturer);
    }

    [Fact]
    public void Parse_InvalidInputsAndOutOfRangeStringIndex_AreHandledSafely()
    {
        Assert.False(SmbiosReader.Parse(null).IsAvailable);
        Assert.False(SmbiosReader.Parse(new byte[8]).IsAvailable);

        byte[] malformed = new byte[20];
        BitConverter.GetBytes(12).CopyTo(malformed, 4);
        malformed[8] = 1;
        malformed[9] = 2; // invalid length < 4
        var malformedSnap = SmbiosReader.Parse(malformed);
        Assert.True(malformedSnap.IsAvailable);
        Assert.Equal(string.Empty, malformedSnap.SystemManufacturer);

        byte[] badStrIdx = new SmbiosBuilder()
            .Add(1, 0x1B, b =>
            {
                b[0x04] = 99; // out of range
                b[0x05] = 1;
            }, "RealProduct")
            .End()
            .Build();
        var badStrSnap = SmbiosReader.Parse(badStrIdx);
        Assert.Equal(string.Empty, badStrSnap.SystemManufacturer);
        Assert.Equal("RealProduct", badStrSnap.SystemProductName);
    }

    [Theory]
    [InlineData(0x1A, "DDR4")]
    [InlineData(0x1E, "LPDDR4")]
    [InlineData(0x22, "DDR5")]
    [InlineData(0x23, "LPDDR5")]
    [InlineData(0x00, "Unknown")]
    [InlineData(0x99, "Type 0x99")]
    public void DecodeMemoryType_MapsExpectedCodes(byte code, string expected)
    {
        Assert.Equal(expected, SmbiosReader.DecodeMemoryType(code));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void Read_LiveSystem_ReturnsAvailableSmbiosSnapshot()
    {
        var snap = SmbiosReader.Read();
        Assert.True(snap.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(snap.SystemManufacturer));
    }
}
