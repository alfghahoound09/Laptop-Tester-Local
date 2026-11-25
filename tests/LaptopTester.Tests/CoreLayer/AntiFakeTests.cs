using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class AntiFakeTests
{
    [Fact]
    public void RamValuesConsistent_RespectsToleranceAndIgnoresZeros()
    {
        Assert.True(AntiFakeService.RamValuesConsistent([16.0, 16.0, 15.8]));
        Assert.False(AntiFakeService.RamValuesConsistent([16.0, 8.0]));
        Assert.True(AntiFakeService.RamValuesConsistent([16.0, 0.0, 0.0]));
        Assert.True(AntiFakeService.RamValuesConsistent([16.0, 15.0], 1.0));
        Assert.False(AntiFakeService.RamValuesConsistent([16.0, 14.9], 1.0));
    }

    [Fact]
    public void FindMislabeledSsd_DetectsInternalSsdLabeledHddAndIgnoresExternalOrClean()
    {
        var fakeInternal = new StorageDriveDetails(0, "Fake SSD 1TB", "SATA", "HDD", 1000, false, "S1", "Healthy", null, null, null, null, null);
        var fakeExternal = new StorageDriveDetails(1, "External SSD", "USB", "HDD", 500, true, "S2", "Healthy", null, null, null, null, null);
        var normalHdd = new StorageDriveDetails(2, "ST1000LM048", "SATA", "HDD / Fixed", 1000, false, "S3", "Healthy", null, null, null, null, null);
        var realSsd = new StorageDriveDetails(3, "Samsung SSD 980", "NVMe", "SSD", 1000, false, "S4", "Healthy", null, null, null, null, null);

        Assert.Equal(fakeInternal, AntiFakeService.FindMislabeledSsd([normalHdd, fakeExternal, fakeInternal, realSsd]));
        Assert.Null(AntiFakeService.FindMislabeledSsd([fakeExternal, normalHdd, realSsd]));
    }

    [Fact]
    public void AggregateRisk_ReturnsHighestSeverity()
    {
        Assert.Equal(SpoofingRisk.None, AntiFakeService.AggregateRisk([]));
        Assert.Equal(SpoofingRisk.Low, AntiFakeService.AggregateRisk([new SpoofingIndicator("C", "T", "D", SpoofingRisk.Low)]));
        Assert.Equal(SpoofingRisk.Medium, AntiFakeService.AggregateRisk([
            new SpoofingIndicator("C", "T", "D", SpoofingRisk.Low),
            new SpoofingIndicator("C", "T", "D", SpoofingRisk.Medium)
        ]));
        Assert.Equal(SpoofingRisk.High, AntiFakeService.AggregateRisk([
            new SpoofingIndicator("C", "T", "D", SpoofingRisk.Medium),
            new SpoofingIndicator("C", "T", "D", SpoofingRisk.High)
        ]));
    }

    [Theory]
    [InlineData("AMIDEWINx64", true)]
    [InlineData("HwidSpoofer_v2", true)]
    [InlineData("explorer", false)]
    [InlineData("svchost", false)]
    public void IsSuspectProcess_MatchesKnownMarkersCaseInsensitively(string processName, bool expected)
    {
        Assert.Equal(expected, AntiFakeService.IsSuspectProcess(processName));
    }

    [Fact]
    public void ModelsMatch_ComparesNormalizedSubstrings()
    {
        Assert.True(AntiFakeService.ModelsMatch("LENOVO 21ML0001US ThinkPad T14 Gen 6", "ThinkPad T14 Gen 6"));
        Assert.False(AntiFakeService.ModelsMatch("Dell XPS 13 9340", "ThinkPad T14 Gen 6"));
        Assert.True(AntiFakeService.ModelsMatch("", "ThinkPad T14"));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ScanForSpoofingAsync_ReturnsFourComparisonsAndConsistentCleanFlag()
    {
        var svc = new AntiFakeService(new StorageHealthService());
        var report = await svc.ScanForSpoofingAsync();

        Assert.True(report.Comparisons.Count >= 4);
        Assert.Contains(report.Comparisons, c => c.Component == "CPU");
        Assert.Contains(report.Comparisons, c => c.Component == "RAM");
        Assert.Contains(report.Comparisons, c => c.Component == "Laptop Model");
        Assert.Contains(report.Comparisons, c => c.Component == "Storage Drive");
        Assert.Equal(report.Indicators.Count == 0, report.IsClean);
    }
}
