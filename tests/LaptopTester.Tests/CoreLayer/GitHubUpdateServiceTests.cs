using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using LaptopTester.Core.Models;
using LaptopTester.Core.Services;
using LaptopTester.Tests.TestInfrastructure;
using Xunit;

namespace LaptopTester.Tests.CoreLayer;

public sealed class GitHubUpdateServiceTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastApiRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastApiRequest = request;
            return Task.FromResult(handler(request));
        }
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("v1.0.1", "1.0.0", 1)]
    [InlineData("1.10.0", "1.2.0", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("v1.0", "1.0.0", 0)]
    [InlineData("1.0.1+abc123", "1.0.1", 0)]
    [InlineData("1.0.1-beta.1", "1.0.1", -1)]
    [InlineData("1.0.1-beta.2", "1.0.1-beta.1", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    public void CompareVersions_OrdersSemanticVersionsCorrectly(string left, string right, int expectedSign)
    {
        int cmp = GitHubUpdateService.CompareVersions(left, right);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void DetectInstallMode_DetectsInstallerPortableAndDevelopment()
    {
        using var dir = new TempDirectory();

        string portableExe = Path.Combine(dir.Path, "LaptopTester-1.0.0-win-x64-portable.exe");
        File.WriteAllText(portableExe, "stub");
        Assert.Equal(AppInstallMode.Portable, GitHubUpdateService.DetectInstallMode(portableExe));

        string depsFile = Path.Combine(dir.Path, "LaptopTester.deps.json");
        File.WriteAllText(depsFile, "{}");
        Assert.Equal(AppInstallMode.Development, GitHubUpdateService.DetectInstallMode(portableExe));

        string uninsFile = Path.Combine(dir.Path, "unins000.exe");
        File.WriteAllText(uninsFile, "stub");
        Assert.Equal(AppInstallMode.Installer, GitHubUpdateService.DetectInstallMode(portableExe));
    }

    [Fact]
    public void SelectPreferredAssets_ChoosesCorrectBinaryPerInstallMode()
    {
        var assets = new[]
        {
            new ReleaseAssetInfo("LaptopTester-Setup-1.0.1-win-x64.exe", "https://github.com/o/r/releases/download/v1.0.1/LaptopTester-Setup-1.0.1-win-x64.exe", "", 45_000_000),
            new ReleaseAssetInfo("LaptopTester-1.0.1-win-x64-portable.exe", "https://github.com/o/r/releases/download/v1.0.1/LaptopTester-1.0.1-win-x64-portable.exe", "", 65_000_000),
            new ReleaseAssetInfo("SHA256SUMS.txt", "https://github.com/o/r/releases/download/v1.0.1/SHA256SUMS.txt", "", 214)
        };

        var (instPrimary, instSum) = GitHubUpdateService.SelectPreferredAssets(assets, AppInstallMode.Installer);
        Assert.Equal("LaptopTester-Setup-1.0.1-win-x64.exe", instPrimary?.Name);
        Assert.Equal("SHA256SUMS.txt", instSum?.Name);

        var (portPrimary, portSum) = GitHubUpdateService.SelectPreferredAssets(assets, AppInstallMode.Portable);
        Assert.Equal("LaptopTester-1.0.1-win-x64-portable.exe", portPrimary?.Name);
        Assert.Equal("SHA256SUMS.txt", portSum?.Name);
    }

    [Fact]
    public void ParseSha256Sums_ParsesStandardAndBinaryMarkersIgnoringInvalidLines()
    {
        string content = """
            # comment line
            0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  LaptopTester-Setup-1.0.1-win-x64.exe
            ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789 *dist/LaptopTester-1.0.1-win-x64-portable.exe
            not-a-hash  bad.exe
            """;

        var map = GitHubUpdateService.ParseSha256Sums(content);
        Assert.Equal(2, map.Count);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", map["LaptopTester-Setup-1.0.1-win-x64.exe"]);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", map["LaptopTester-1.0.1-win-x64-portable.exe"]);
    }

    [Theory]
    [InlineData("https://github.com/alfghahoound09/Laptop-Tester-Local/releases", true)]
    [InlineData("https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/latest", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://release-assets.githubusercontent.com/file.exe", true)]
    [InlineData("http://github.com/alfghahoound09/Laptop-Tester-Local", false)]
    [InlineData("https://example.com/github.com/malware.exe", false)]
    [InlineData("not-a-url", false)]
    public void IsTrustedGitHubUrl_AllowsOnlyHttpsGitHubHosts(string url, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateService.IsTrustedGitHubUrl(url));
    }

    [Fact]
    public async Task CheckAndDownload_VerifiesSha256AndStagesUpdateCleanly()
    {
        using var dir = new TempDirectory();
        byte[] fakePortableBytes = Encoding.UTF8.GetBytes("FAKE-SINGLE-FILE-EXE-PAYLOAD-FOR-TEST");
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(fakePortableBytes));

        string releaseJson = """
            {
              "tag_name": "v1.0.1",
              "name": "LaptopTester v1.0.1",
              "html_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/tag/v1.0.1",
              "body": "Bug fixes and thermal improvements",
              "prerelease": false,
              "published_at": "2026-11-26T10:00:00Z",
              "assets": [
                {
                  "name": "LaptopTester-Setup-1.0.1-win-x64.exe",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/LaptopTester-Setup-1.0.1-win-x64.exe",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/1",
                  "size": 45000000
                },
                {
                  "name": "LaptopTester-1.0.1-win-x64-portable.exe",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/LaptopTester-1.0.1-win-x64-portable.exe",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/2",
                  "size": 37
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/SHA256SUMS.txt",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/3",
                  "size": 120
                }
              ]
            }
            """;

        string sumsTxt = $"{expectedSha}  LaptopTester-1.0.1-win-x64-portable.exe\n";

        using var handler = new StubHttpHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.EndsWith("/releases/latest", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") };
            if (uri.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sumsTxt, Encoding.UTF8, "text/plain") };
            if (uri.EndsWith("-portable.exe", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fakePortableBytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new GitHubUpdateService(
            http,
            configPath: Path.Combine(dir.Path, "updater.json"),
            updatesDirectory: Path.Combine(dir.Path, "updates"));

        var check = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.0",
            installModeOverride: AppInstallMode.Portable);

        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("v1.0.1", check.LatestRelease?.TagName);
        Assert.Equal("LaptopTester-1.0.1-win-x64-portable.exe", check.PrimaryAsset?.Name);
        Assert.Contains("Update available", check.StatusMessage);

        var progress = new SyncProgress<UpdateDownloadProgress>();
        string fakeRunningExe = Path.Combine(dir.Path, "LaptopTester.exe");
        var prepared = await svc.DownloadAndVerifyAsync(check, progress, targetProcessPath: fakeRunningExe);

        Assert.True(prepared.Sha256Verified);
        Assert.True(File.Exists(prepared.DownloadedFilePath));
        Assert.Equal("v1.0.1", prepared.VersionTag);
        Assert.Equal(AppInstallMode.Portable, prepared.InstallMode);
        Assert.Contains(progress.Snapshot(), p => p.Percent == 100);
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_DeletesFileAndThrowsOnSha256Mismatch()
    {
        using var dir = new TempDirectory();
        byte[] tamperedBytes = Encoding.UTF8.GetBytes("TAMPERED-PAYLOAD");
        string wrongSha = new string('a', 64);

        var check = new UpdateCheckResult(
            IsUpdateAvailable: true,
            CurrentVersion: "1.0.0",
            InstallMode: AppInstallMode.Portable,
            LatestRelease: new GitHubReleaseInfo(
                "v1.0.1", "1.0.1", "LaptopTester v1.0.1",
                "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/tag/v1.0.1",
                "", DateTimeOffset.UtcNow, false, []),
            PrimaryAsset: new ReleaseAssetInfo(
                "LaptopTester-1.0.1-win-x64-portable.exe",
                "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/LaptopTester-1.0.1-win-x64-portable.exe",
                "", tamperedBytes.Length),
            ChecksumAsset: new ReleaseAssetInfo(
                "SHA256SUMS.txt",
                "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/SHA256SUMS.txt",
                "", 96),
            StatusMessage: "Update available");

        using var handler = new StubHttpHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{wrongSha}  LaptopTester-1.0.1-win-x64-portable.exe\n", Encoding.UTF8, "text/plain")
                };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tamperedBytes) };
        });

        using var http = new HttpClient(handler);
        string updatesDir = Path.Combine(dir.Path, "updates");
        var svc = new GitHubUpdateService(http, Path.Combine(dir.Path, "updater.json"), updatesDir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DownloadAndVerifyAsync(check));
        Assert.Contains("SHA-256 verification failed", ex.Message);
        Assert.False(File.Exists(Path.Combine(updatesDir, "v1.0.1", "LaptopTester-1.0.1-win-x64-portable.exe")));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HandlesNotFoundGracefully()
    {
        using var dir = new TempDirectory();
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var svc = new GitHubUpdateService(http, Path.Combine(dir.Path, "updater.json"), Path.Combine(dir.Path, "updates"));

        var res = await svc.CheckForUpdatesAsync(currentVersion: "1.0.0", installModeOverride: AppInstallMode.Installer);
        Assert.False(res.IsUpdateAvailable);
        Assert.Equal("HTTP 404 Not Found", res.ErrorDetail);
        Assert.Contains("404", res.ErrorDetail);
    }

    [Fact]
    public void BuildApplyScript_HandlesPortableSwapAndSilentInnoInstall()
    {
        string script = GitHubUpdateService.BuildApplyScript();
        Assert.Contains("/VERYSILENT", script);
        Assert.Contains("/SUPPRESSMSGBOXES", script);
        Assert.Contains("/CLOSEAPPLICATIONS", script);
        Assert.Contains("$Mode -eq 'Portable'", script);
        Assert.Contains(".bak", script);
    }

    [Fact]
    public void ParseChecksumManifest_ExtractsCommitMetadataAndFileHashes()
    {
        string appSha = new string('a', 64);
        string portSha = new string('b', 64);
        string setupSha = new string('c', 64);
        string text = $"""
            # version: 1.0.1
            # commit: e09261f8ae9555d84b161dea1d8e4a75a41600b8
            # built_at: 2026-11-25T15:30:00Z
            # app_sha256: {appSha}
            # portable_sha256: {portSha}
            {portSha}  LaptopTester-1.0.1-win-x64-portable.exe
            {setupSha}  LaptopTester-Setup-1.0.1-win-x64.exe
            """;

        var manifest = GitHubUpdateService.ParseChecksumManifest(text);
        Assert.Equal("e09261f8ae9555d84b161dea1d8e4a75a41600b8", manifest.CommitSha);
        Assert.Equal(appSha, manifest.AppExeSha256);
        Assert.Equal(portSha, manifest.PortableExeSha256);
        Assert.NotNull(manifest.BuiltAtUtc);
        Assert.Equal(2, manifest.FileHashes.Count);
        Assert.Equal(setupSha, manifest.FileHashes["LaptopTester-Setup-1.0.1-win-x64.exe"]);

        // Backward-compatible ParseSha256Sums still returns only the file hash entries
        var legacyMap = GitHubUpdateService.ParseSha256Sums(text);
        Assert.Equal(2, legacyMap.Count);
        Assert.Equal(portSha, legacyMap["LaptopTester-1.0.1-win-x64-portable.exe"]);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SameVersionHotfix_DetectsCommitAndSha256MismatchWithoutLoop()
    {
        using var dir = new TempDirectory();
        string fakePortableExe = Path.Combine(dir.Path, "LaptopTester-1.0.1-win-x64-portable.exe");
        byte[] oldExeBytes = Encoding.UTF8.GetBytes("old-v1.0.1-binary");
        await File.WriteAllBytesAsync(fakePortableExe, oldExeBytes);
        string oldSha = Convert.ToHexStringLower(SHA256.HashData(oldExeBytes));
        string newSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("patched-v1.0.1-binary")));

        string releaseJson = $$"""
            {
              "tag_name": "v1.0.1",
              "name": "LaptopTester v1.0.1",
              "html_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/tag/v1.0.1",
              "body": "Hotfix build",
              "prerelease": false,
              "published_at": "2026-11-25T15:30:00Z",
              "assets": [
                {
                  "name": "LaptopTester-1.0.1-win-x64-portable.exe",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/LaptopTester-1.0.1-win-x64-portable.exe",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/1",
                  "size": 65000000,
                  "updated_at": "2026-11-25T15:30:00Z",
                  "digest": "sha256:{{newSha}}"
                },
                {
                  "name": "LaptopTester-Setup-1.0.1-win-x64.exe",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/LaptopTester-Setup-1.0.1-win-x64.exe",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/2",
                  "size": 45000000,
                  "updated_at": "2026-11-25T15:30:00Z"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "browser_download_url": "https://github.com/alfghahoound09/Laptop-Tester-Local/releases/download/v1.0.1/SHA256SUMS.txt",
                  "url": "https://api.github.com/repos/alfghahoound09/Laptop-Tester-Local/releases/assets/3",
                  "size": 300
                }
              ]
            }
            """;

        string sumsContent = $"""
            # version: 1.0.1
            # commit: e09261f8ae9555d84b161dea1d8e4a75a41600b8
            # built_at: 2026-11-25T15:30:00Z
            # portable_sha256: {newSha}
            {newSha}  LaptopTester-1.0.1-win-x64-portable.exe
            {newSha}  LaptopTester-Setup-1.0.1-win-x64.exe
            """;

        using var handler = new StubHttpHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sumsContent, Encoding.UTF8, "text/plain") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") };
        });

        using var http = new HttpClient(handler);
        var svc = new GitHubUpdateService(http, Path.Combine(dir.Path, "updater.json"), Path.Combine(dir.Path, "updates"));

        // 1. Installer mode with older commit SHA -> detects hotfix
        var diffCommit = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.1",
            installModeOverride: AppInstallMode.Installer,
            currentCommitOverride: "3420c3955a6fc4d2601272d4278c48286e58a6a8");
        Assert.True(diffCommit.IsUpdateAvailable);
        Assert.True(diffCommit.IsSameVersionHotfix);
        Assert.Equal("e09261f8ae9555d84b161dea1d8e4a75a41600b8", diffCommit.RemoteCommitSha);
        Assert.Contains("Hotfix available", diffCommit.StatusMessage);

        // 2. Installer mode with matching commit SHA -> up to date (no loop)
        var sameCommit = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.1",
            installModeOverride: AppInstallMode.Installer,
            currentCommitOverride: "e09261f");
        Assert.False(sameCommit.IsUpdateAvailable);
        Assert.False(sameCommit.IsSameVersionHotfix);

        // 3. Portable mode without commit SHA -> falls back to running .exe SHA-256 comparison
        var diffPortSha = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.1",
            installModeOverride: AppInstallMode.Portable,
            currentCommitOverride: "",
            currentProcessPathOverride: fakePortableExe);
        Assert.True(diffPortSha.IsUpdateAvailable);
        Assert.True(diffPortSha.IsSameVersionHotfix);
        Assert.NotEqual(oldSha, diffPortSha.RemoteAssetSha256);

        // Once the portable exe matches the remote SHA-256 -> up to date (no loop)
        await File.WriteAllBytesAsync(fakePortableExe, Encoding.UTF8.GetBytes("patched-v1.0.1-binary"));
        var samePortSha = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.1",
            installModeOverride: AppInstallMode.Portable,
            currentCommitOverride: "",
            currentProcessPathOverride: fakePortableExe);
        Assert.False(samePortSha.IsUpdateAvailable);
        Assert.False(samePortSha.IsSameVersionHotfix);

        // 4. Development mode never prompts same-version hotfixes
        var devCheck = await svc.CheckForUpdatesAsync(
            currentVersion: "1.0.1",
            installModeOverride: AppInstallMode.Development,
            currentCommitOverride: "3420c39");
        Assert.False(devCheck.IsUpdateAvailable);
        Assert.False(devCheck.IsSameVersionHotfix);
    }
}
