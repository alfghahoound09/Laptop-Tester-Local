using System.IO;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

[Collection("Heavy")]
public sealed class StorageTests
{
    [Theory]
    [InlineData(17, "Generic", "SCSI", "NVMe")]
    [InlineData(11, "Generic", "IDE", "SATA")]
    [InlineData(7, "Generic", "USB", "USB")]
    [InlineData(8, "Generic", "SCSI", "RAID")]
    [InlineData(3, "Generic", "IDE", "ATA")]
    [InlineData(0, "Samsung SSD 980 NVMe", "SCSI", "NVMe")]
    [InlineData(0, "WDC WD10EZEX", "IDE", "IDE")]
    public void MapBusType_MapsBusAndFallback(int bus, string model, string wmiIface, string expected)
    {
        Assert.Equal(expected, StorageHealthService.MapBusType(bus, model, wmiIface));
    }

    [Theory]
    [InlineData(4, 11, "Generic", "SSD")]
    [InlineData(3, 11, "Generic", "HDD")]
    [InlineData(5, 17, "Generic", "SCM")]
    [InlineData(0, 17, "Generic", "SSD")]
    [InlineData(0, 11, "Crucial SSD MX500", "SSD")]
    [InlineData(0, 11, "ST1000DM010", "HDD / Fixed")]
    public void MapMediaCategory_MapsMediaAndHeuristics(int msftMedia, int bus, string model, string expected)
    {
        Assert.Equal(expected, StorageHealthService.MapMediaCategory(msftMedia, bus, model));
    }

    [Theory]
    [InlineData(0, "Healthy")]
    [InlineData(1, "Warning")]
    [InlineData(2, "Unhealthy")]
    [InlineData(-1, "OK (WMI)")]
    public void MapHealthStatus_MapsCodes(int code, string expected)
    {
        Assert.Equal(expected, StorageHealthService.MapHealthStatus(code));
    }

    [Theory]
    [InlineData(7, "SCSI", "Fixed", true)]
    [InlineData(11, "USB", "Fixed", true)]
    [InlineData(11, "SCSI", "External hard disk media", true)]
    [InlineData(17, "SCSI", "Fixed hard disk media", false)]
    public void IsExternalDrive_DetectsUsbAndExternalMedia(int bus, string iface, string media, bool expected)
    {
        Assert.Equal(expected, StorageHealthService.IsExternalDrive(bus, iface, media));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task GetDrivesAsync_ReturnsAtLeastOneDriveWithPositiveSize()
    {
        var svc = new StorageHealthService();
        var drives = await svc.GetDrivesAsync();

        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.True(d.SizeGb > 0));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RunSpeedTestAsync_MeasuresThroughputAndCleansTempFile()
    {
        using var dir = new TempDirectory();
        var svc = new StorageHealthService();

        var (readMb, writeMb) = await svc.RunSpeedTestAsync(dir.Path, sizeMb: 8);
        Assert.True(readMb > 0);
        Assert.True(writeMb > 0);
        Assert.Empty(Directory.GetFiles(dir.Path));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.RunSpeedTestAsync(dir.Path, sizeMb: 8, cancelled.Token));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }
}
