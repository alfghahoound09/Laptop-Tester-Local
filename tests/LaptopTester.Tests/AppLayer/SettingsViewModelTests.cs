using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LaptopTester.App.ViewModels;
using LaptopTester.Core.Localization;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.AppLayer;

public sealed class SettingsViewModelTests
{
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    [Fact]
    public async Task CheckForUpdates_AndPersistence_UpdatePropertiesAndRaiseNotification()
    {
        using var dir = new TempDirectory();
        string cfgPath = Path.Combine(dir.Path, "updater.json");
        string updatesDir = Path.Combine(dir.Path, "updates");

        string releaseJson = """
            {
              "tag_name": "v9.9.9",
              "name": "LaptopTester v9.9.9",
              "html_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/tag/v9.9.9",
              "body": "- Added instant updates",
              "prerelease": false,
              "published_at": "2026-11-26T12:00:00Z",
              "assets": [
                {
                  "name": "LaptopTester-Setup-9.9.9-win-x64.exe",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v9.9.9/LaptopTester-Setup-9.9.9-win-x64.exe",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/10",
                  "size": 45000000
                }
              ]
            }
            """;

        using var handler = new MockHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
            });
        using var http = new HttpClient(handler);
        var updater = new GitHubUpdateService(http, cfgPath, updatesDir);

        var vm = new SettingsViewModel(
            new LocalizationService(),
            new HistoryReportService(Path.Combine(dir.Path, "history")),
            updater,
            shutdownApp: () => { });

        bool notified = false;
        vm.UpdateStatusChanged += r => notified = r.IsUpdateAvailable;

        Assert.Equal($"v{GitHubUpdateService.GetCurrentVersion()}", vm.CurrentVersionText);
        Assert.False(vm.HasAvailableUpdate);
        Assert.False(vm.CanInstallUpdate);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(notified);
        Assert.True(vm.HasAvailableUpdate);
        Assert.True(vm.CanInstallUpdate);
        Assert.Equal("v9.9.9", vm.AvailableUpdateTag);
        Assert.Contains("Added instant updates", vm.UpdateReleaseNotes);

        // Persisting AutoCheckUpdates and optional GitHub token
        vm.AutoCheckUpdates = false;
        vm.GitHubTokenInput = "test-pat-123";
        vm.SaveTokenCommand.Execute(null);

        var saved = updater.LoadSettings();
        Assert.False(saved.AutoCheckOnStartup);
        Assert.Equal("test-pat-123", saved.GitHubToken);
    }
}
