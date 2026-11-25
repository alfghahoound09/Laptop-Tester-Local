using LaptopTester.Core.Services;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class SystemAuditTests
{
    [Theory]
    [InlineData(@"C:\Users\User\AppData\Local\Temp\evil.exe", "Review")]
    [InlineData("powershell.exe -NoProfile -File test.ps1", "Review")]
    [InlineData("wscript.exe helper.vbs", "Review")]
    [InlineData(@"C:\Program Files\OneDrive\OneDrive.exe", "Normal")]
    public void ClassifyStartupRisk_FlagsSuspiciousLaunchCommands(string command, string expected)
    {
        Assert.Equal(expected, SystemAuditService.ClassifyStartupRisk(command));
    }

    [Fact]
    public void DescribeCmError_MapsKnownAndUnknownCodes()
    {
        Assert.Contains("Code 43", SystemAuditService.DescribeCmError(43));
        Assert.Equal("Device Manager error Code 99", SystemAuditService.DescribeCmError(99));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task LiveAudit_ReturnsConsistentDriverAndStartupEntries()
    {
        var svc = new SystemAuditService();
        var driverIssues = await svc.ScanDriverIssuesAsync();
        var startupEntries = await svc.ScanStartupEntriesAsync();

        Assert.All(driverIssues, d => Assert.NotEqual(0, d.ErrorCode));
        Assert.All(startupEntries, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Location));
        });
    }
}
