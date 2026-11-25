using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaptopTester.Core.Models;

namespace LaptopTester.Core.Services;

/// <summary>
/// Checks GitHub Releases for newer LaptopTester versions, verifies SHA-256 checksums,
/// downloads matching Portable or Installer binaries, and applies in-place updates on exit.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    public const string DefaultOwner = "alfghahoound09";
    public const string DefaultRepo = "Laptop-Tester-Local";
    public const string DefaultReleasesUrl = $"https://github.com/{DefaultOwner}/{DefaultRepo}/releases";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly string[] TrustedHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _configPath;
    private readonly string _updatesRootDir;

    public GitHubUpdateService(
        HttpClient? httpClient = null,
        string? configPath = null,
        string? updatesDirectory = null,
        string owner = DefaultOwner,
        string repo = DefaultRepo)
    {
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(12)
            })
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            _ownsHttp = true;
        }

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaptopTester");
        _configPath = configPath ?? Path.Combine(appData, "updater.json");
        _updatesRootDir = updatesDirectory ?? Path.Combine(appData, "updates");
        _owner = owner;
        _repo = repo;
    }

    public string ReleasesPageUrl => $"https://github.com/{_owner}/{_repo}/releases";

    public static string GetCurrentVersion(Assembly? entryAssembly = null)
    {
        var entry = Assembly.GetEntryAssembly();
        var asm = entryAssembly
            ?? (entry?.GetName().Name?.StartsWith("LaptopTester", StringComparison.OrdinalIgnoreCase) == true
                ? entry
                : typeof(GitHubUpdateService).Assembly);
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            string trimmed = infoAttr.Split('+')[0].Trim().TrimStart('v', 'V');
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        var v = asm.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.0.0";
    }

    public static string? GetCurrentCommitSha(Assembly? entryAssembly = null)
    {
        var entry = Assembly.GetEntryAssembly();
        var asm = entryAssembly
            ?? (entry?.GetName().Name?.StartsWith("LaptopTester", StringComparison.OrdinalIgnoreCase) == true
                ? entry
                : typeof(GitHubUpdateService).Assembly);
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(infoAttr))
            return null;

        int plus = infoAttr.IndexOf('+');
        if (plus < 0 || plus + 1 >= infoAttr.Length)
            return null;

        return NormalizeCommitSha(infoAttr[(plus + 1)..]);
    }

    public UpdaterSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<UpdaterSettings>(json, JsonOpts);
                if (parsed is not null) return parsed;
            }
        }
        catch { }
        return new UpdaterSettings();
    }

    public void SaveSettings(UpdaterSettings settings)
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(_configPath, json, Encoding.UTF8);
    }

    public void CleanupOldUpdateDownloads()
    {
        try
        {
            if (!Directory.Exists(_updatesRootDir)) return;
            foreach (var dir in Directory.GetDirectories(_updatesRootDir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string? currentVersion = null,
        string? tokenOverride = null,
        AppInstallMode? installModeOverride = null,
        CancellationToken ct = default,
        string? currentCommitOverride = null,
        string? currentProcessPathOverride = null)
    {
        string current = NormalizeVersion(currentVersion ?? GetCurrentVersion());
        AppInstallMode mode = installModeOverride ?? DetectInstallMode(currentProcessPathOverride ?? Environment.ProcessPath);
        UpdaterSettings settings = LoadSettings();
        string? token = ResolveToken(tokenOverride, settings);

        string apiUrl = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var req = CreateGitHubRequest(HttpMethod.Get, apiUrl, "application/vnd.github+json", token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            settings.LastCheckedAt = DateTimeOffset.UtcNow;
            try { SaveSettings(settings); } catch { }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    false, current, mode, null, null, null,
                    "No published GitHub release found yet (or repository is private — add an optional GitHub token below if private).",
                    "HTTP 404 Not Found");
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new UpdateCheckResult(
                    false, current, mode, null, null, null,
                    "GitHub token was rejected or expired. Check or clear your optional token in Settings.",
                    "HTTP 401 Unauthorized");
            }

            if (resp.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return new UpdateCheckResult(
                    false, current, mode, null, null, null,
                    "GitHub API rate limit reached. Wait a few minutes or provide an optional GitHub token.",
                    $"HTTP {(int)resp.StatusCode}");
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false, current, mode, null, null, null,
                    $"Could not check GitHub Releases (HTTP {(int)resp.StatusCode}).",
                    resp.ReasonPhrase);
            }

            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            var release = ParseReleaseJson(json);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(
                    false, current, mode, null, null, null,
                    "GitHub returned an unrecognized release payload.");
            }

            var (primary, checksum) = SelectPreferredAssets(release.Assets, mode);
            int cmp = CompareVersions(release.NormalizedVersion, current);
            bool newer = cmp > 0;
            bool isHotfix = false;
            string? remoteCommit = null;
            string? remoteAssetSha = ExtractSha256Digest(primary?.Digest);

            if (cmp == 0 && primary is not null && mode != AppInstallMode.Development)
            {
                var manifest = checksum is not null
                    ? await TryFetchChecksumManifestAsync(checksum, token, cts.Token)
                    : null;

                remoteCommit = manifest?.CommitSha;
                if (manifest is not null && manifest.FileHashes.TryGetValue(primary.Name, out var assetHash))
                    remoteAssetSha = assetHash;

                string? remoteRunningSha = mode == AppInstallMode.Portable
                    ? (manifest?.PortableExeSha256 ?? remoteAssetSha)
                    : manifest?.AppExeSha256;

                string? localCommit = NormalizeCommitSha(currentCommitOverride ?? GetCurrentCommitSha());

                if (!string.IsNullOrEmpty(remoteCommit) && !string.IsNullOrEmpty(localCommit))
                {
                    isHotfix = !CommitShasMatch(localCommit, remoteCommit);
                }
                else if (!string.IsNullOrEmpty(remoteRunningSha))
                {
                    string? localExePath = currentProcessPathOverride ?? Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(localExePath) && File.Exists(localExePath))
                    {
                        try
                        {
                            string localSha = await ComputeSha256Async(localExePath, cts.Token);
                            isHotfix = !localSha.Equals(remoteRunningSha, StringComparison.OrdinalIgnoreCase);
                        }
                        catch { }
                    }
                }
                else if (!string.IsNullOrEmpty(remoteAssetSha) && !string.IsNullOrWhiteSpace(settings.InstalledHotfixSha256))
                {
                    isHotfix = !settings.InstalledHotfixSha256.Equals(remoteAssetSha, StringComparison.OrdinalIgnoreCase);
                }
            }

            bool available = newer || isHotfix;
            string shortCommit = !string.IsNullOrEmpty(remoteCommit)
                ? $" ({remoteCommit[..Math.Min(7, remoteCommit.Length)]})"
                : "";

            string status = isHotfix && primary is not null
                ? $"Hotfix available for {release.TagName}{shortCommit} ({FormatMegabytes(primary.SizeBytes)} MB). Click 'Update & Restart Now' to patch in place without reinstalling."
                : !newer
                    ? $"You are up to date (v{current})."
                    : primary is null
                        ? $"Version {release.TagName} was found on GitHub, but no Windows .exe release asset is attached yet."
                        : $"Update available: {release.TagName} ({FormatMegabytes(primary.SizeBytes)} MB). Click 'Update & Restart Now' to update automatically without reinstalling.";

            return new UpdateCheckResult(
                available,
                current,
                mode,
                release,
                primary,
                checksum,
                status,
                IsSameVersionHotfix: isHotfix,
                RemoteCommitSha: remoteCommit,
                RemoteAssetSha256: remoteAssetSha);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(false, current, mode, null, null, null,
                "Update check timed out while contacting GitHub.", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(false, current, mode, null, null, null,
                "Could not reach GitHub. Check your internet connection and try again.", ex.Message);
        }
    }

    public async Task<PreparedUpdate> DownloadAndVerifyAsync(
        UpdateCheckResult check,
        IProgress<UpdateDownloadProgress>? progress = null,
        string? tokenOverride = null,
        string? targetProcessPath = null,
        CancellationToken ct = default)
    {
        if (check.LatestRelease is null || check.PrimaryAsset is null)
            throw new InvalidOperationException("No downloadable update asset is available.");

        var release = check.LatestRelease;
        var asset = check.PrimaryAsset;
        var settings = LoadSettings();
        string? token = ResolveToken(tokenOverride, settings);

        string downloadUrl = ChooseDownloadUrl(asset, token);
        if (!IsTrustedGitHubUrl(downloadUrl))
            throw new InvalidOperationException($"Refusing untrusted update URL: {downloadUrl}");

        string stageDir = Path.Combine(_updatesRootDir, SanitizeFileComponent(release.TagName));
        Directory.CreateDirectory(stageDir);

        string destPath = Path.Combine(stageDir, SanitizeFileComponent(asset.Name));
        if (File.Exists(destPath)) File.Delete(destPath);

        string? expectedSha = null;
        if (check.ChecksumAsset is not null)
        {
            progress?.Report(new UpdateDownloadProgress(0, 0, 0, $"Fetching checksums from {check.ChecksumAsset.Name}..."));
            string sumUrl = ChooseDownloadUrl(check.ChecksumAsset, token);
            if (!IsTrustedGitHubUrl(sumUrl))
                throw new InvalidOperationException($"Refusing untrusted checksum URL: {sumUrl}");

            string accept = !string.IsNullOrWhiteSpace(token) && sumUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
                ? "application/octet-stream"
                : "text/plain";
            using var sumReq = CreateGitHubRequest(HttpMethod.Get, sumUrl, accept, token);
            using var sumResp = await _http.SendAsync(sumReq, ct);
            sumResp.EnsureSuccessStatusCode();
            string sumText = await sumResp.Content.ReadAsStringAsync(ct);
            var map = ParseSha256Sums(sumText);
            if (!map.TryGetValue(asset.Name, out expectedSha))
                throw new InvalidOperationException($"Checksum file '{check.ChecksumAsset.Name}' did not list '{asset.Name}'.");
        }

        string binaryAccept = !string.IsNullOrWhiteSpace(token) && downloadUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream"
            : "*/*";
        using var req = CreateGitHubRequest(HttpMethod.Get, downloadUrl, binaryAccept, token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? asset.SizeBytes;
        await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var localStream = File.Create(destPath))
        {
            var buffer = new byte[64 * 1024];
            long received = 0;
            int read;
            int lastReportedPct = -1;

            while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                int pct = totalBytes > 0 ? (int)Math.Clamp(received * 100L / totalBytes, 0, 100) : 0;
                if (pct != lastReportedPct || received == totalBytes)
                {
                    lastReportedPct = pct;
                    string sizeText = totalBytes > 0
                        ? $"{FormatMegabytes(received)} / {FormatMegabytes(totalBytes)} MB ({pct}%)"
                        : $"{FormatMegabytes(received)} MB";
                    progress?.Report(new UpdateDownloadProgress(
                        received, totalBytes, pct, $"Downloading {release.TagName}... {sizeText}"));
                }
            }
        }

        bool verified = false;
        if (!string.IsNullOrWhiteSpace(expectedSha))
        {
            progress?.Report(new UpdateDownloadProgress(totalBytes, totalBytes, 100, "Verifying SHA-256 security checksum..."));
            string actualSha = await ComputeSha256Async(destPath, ct);
            if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(destPath); } catch { }
                throw new InvalidOperationException(
                    $"SHA-256 verification failed for '{asset.Name}'. Expected {expectedSha}, got {actualSha}. Update aborted.");
            }
            verified = true;
        }

        string exePath = targetProcessPath ?? Environment.ProcessPath ?? AppContext.BaseDirectory;
        string installDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        try
        {
            if (!string.IsNullOrWhiteSpace(check.RemoteCommitSha))
                settings.InstalledHotfixCommit = check.RemoteCommitSha;
            string? appliedSha = expectedSha ?? check.RemoteAssetSha256;
            if (!string.IsNullOrWhiteSpace(appliedSha))
                settings.InstalledHotfixSha256 = appliedSha;
            SaveSettings(settings);
        }
        catch { }

        progress?.Report(new UpdateDownloadProgress(totalBytes, totalBytes, 100, $"Update {release.TagName} verified and ready to install."));
        return new PreparedUpdate(destPath, release.TagName, check.InstallMode, exePath, installDir, verified);
    }

    public void LaunchUpdaterProcess(PreparedUpdate prepared, int? currentProcessId = null)
    {
        if (!File.Exists(prepared.DownloadedFilePath))
            throw new FileNotFoundException("Downloaded update file could not be found.", prepared.DownloadedFilePath);

        string scriptDir = Path.GetDirectoryName(prepared.DownloadedFilePath) ?? _updatesRootDir;
        string scriptPath = Path.Combine(scriptDir, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildApplyScript(), Encoding.UTF8);

        bool applyPortableSwap = prepared.InstallMode == AppInstallMode.Portable
            || !Path.GetFileName(prepared.DownloadedFilePath).Contains("Setup", StringComparison.OrdinalIgnoreCase);

        string modeArg = applyPortableSwap ? "Portable" : "Installer";
        string installDir = prepared.InstallMode == AppInstallMode.Development
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LaptopTester")
            : prepared.InstallDirectory;
        string targetExe = prepared.InstallMode == AppInstallMode.Development
            ? Path.Combine(installDir, "LaptopTester.exe")
            : prepared.TargetExePath;

        bool needsElevation = modeArg == "Installer"
            && prepared.InstallMode == AppInstallMode.Installer
            && !CanWriteDirectory(installDir);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = scriptDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ParentPid");
        psi.ArgumentList.Add((currentProcessId ?? Environment.ProcessId).ToString());
        psi.ArgumentList.Add("-Mode");
        psi.ArgumentList.Add(modeArg);
        psi.ArgumentList.Add("-SourceFile");
        psi.ArgumentList.Add(prepared.DownloadedFilePath);
        psi.ArgumentList.Add("-TargetFile");
        psi.ArgumentList.Add(targetExe);
        psi.ArgumentList.Add("-InstallDir");
        psi.ArgumentList.Add(installDir);
        psi.ArgumentList.Add("-ElevateInstaller");
        psi.ArgumentList.Add(needsElevation ? "1" : "0");

        Process.Start(psi);
    }

    internal static string BuildApplyScript() => """
        param(
            [int]$ParentPid,
            [string]$Mode,
            [string]$SourceFile,
            [string]$TargetFile,
            [string]$InstallDir,
            [string]$ElevateInstaller
        )

        $ErrorActionPreference = 'Stop'

        for ($i = 0; $i -lt 120; $i++) {
            $proc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if (-not $proc) { break }
            Start-Sleep -Milliseconds 250
        }
        Start-Sleep -Milliseconds 500

        if ($Mode -eq 'Portable') {
            $backup = "$TargetFile.bak"
            if (Test-Path -LiteralPath $TargetFile) {
                Copy-Item -LiteralPath $TargetFile -Destination $backup -Force
            }

            $copied = $false
            for ($attempt = 0; $attempt -lt 15; $attempt++) {
                try {
                    Copy-Item -LiteralPath $SourceFile -Destination $TargetFile -Force
                    $copied = $true
                    break
                }
                catch {
                    Start-Sleep -Milliseconds 350
                }
            }

            if ($copied) {
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $SourceFile -Force -ErrorAction SilentlyContinue
            }
            elseif (Test-Path -LiteralPath $backup) {
                Copy-Item -LiteralPath $backup -Destination $TargetFile -Force -ErrorAction SilentlyContinue
            }

            if (Test-Path -LiteralPath $TargetFile) {
                Start-Process -FilePath $TargetFile
            }
        }
        else {
            $argsList = @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                '/CLOSEAPPLICATIONS',
                '/FORCECLOSEAPPLICATIONS',
                "/DIR=$InstallDir"
            )

            if ($ElevateInstaller -eq '1') {
                $setup = Start-Process -FilePath $SourceFile -ArgumentList $argsList -Verb RunAs -Wait -PassThru
            }
            else {
                $setup = Start-Process -FilePath $SourceFile -ArgumentList $argsList -Wait -PassThru
            }

            Remove-Item -LiteralPath $SourceFile -Force -ErrorAction SilentlyContinue
            $relaunchExe = Join-Path $InstallDir 'LaptopTester.exe'
            if (Test-Path -LiteralPath $relaunchExe) {
                Start-Process -FilePath $relaunchExe
            }
        }
        """;

    public static AppInstallMode DetectInstallMode(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return AppInstallMode.Development;

        string? dir = Path.GetDirectoryName(processPath);
        string file = Path.GetFileName(processPath);
        if (string.IsNullOrEmpty(dir))
            return AppInstallMode.Development;

        if (File.Exists(Path.Combine(dir, "unins000.exe")))
            return AppInstallMode.Installer;

        if (file.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(dir, "LaptopTester.deps.json"))
            || File.Exists(Path.Combine(dir, "LaptopTester.runtimeconfig.json")))
        {
            return AppInstallMode.Development;
        }

        return AppInstallMode.Portable;
    }

    internal static GitHubReleaseInfo? ParseReleaseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        string name = GetString(root, "name");
        string htmlUrl = GetString(root, "html_url");
        string body = GetString(root, "body");
        bool prerelease = root.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True;
        DateTimeOffset publishedAt = root.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pubEl.GetString(), out var parsedDate)
            ? parsedDate
            : DateTimeOffset.MinValue;

        var assets = new List<ReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsEl.EnumerateArray())
            {
                string assetName = GetString(item, "name");
                string browserUrl = GetString(item, "browser_download_url");
                string apiUrl = GetString(item, "url");
                long size = item.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out long s) ? s : 0;
                DateTimeOffset? updatedAt = item.TryGetProperty("updated_at", out var updEl) && updEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(updEl.GetString(), out var parsedUpd)
                    ? parsedUpd
                    : null;
                string? digest = item.TryGetProperty("digest", out var digEl) && digEl.ValueKind == JsonValueKind.String
                    ? digEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(assetName))
                    assets.Add(new ReleaseAssetInfo(assetName, browserUrl, apiUrl, size, updatedAt, digest));
            }
        }

        return new GitHubReleaseInfo(
            tagName,
            NormalizeVersion(tagName),
            string.IsNullOrWhiteSpace(name) ? tagName : name,
            string.IsNullOrWhiteSpace(htmlUrl) ? DefaultReleasesUrl : htmlUrl,
            body.Trim(),
            publishedAt,
            prerelease,
            assets);
    }

    internal static (ReleaseAssetInfo? PrimaryAsset, ReleaseAssetInfo? ChecksumAsset) SelectPreferredAssets(
        IReadOnlyList<ReleaseAssetInfo> assets,
        AppInstallMode mode)
    {
        var checksum = assets.FirstOrDefault(a =>
            a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        var exes = assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (exes.Count == 0)
            return (null, checksum);

        ReleaseAssetInfo? primary = mode == AppInstallMode.Portable
            ? exes.FirstOrDefault(a => a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
                ?? exes.FirstOrDefault(a => !a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                ?? exes.First()
            : exes.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                ?? exes.FirstOrDefault(a => a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
                ?? exes.First();

        return (primary, checksum);
    }

    internal static Dictionary<string, string> ParseSha256Sums(string content) =>
        new(ParseChecksumManifest(content).FileHashes, StringComparer.OrdinalIgnoreCase);

    internal static ReleaseChecksumManifest ParseChecksumManifest(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? commit = null;
        DateTimeOffset? builtAt = null;
        string? appSha = null;
        string? portableSha = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith('#'))
                {
                    string comment = line.TrimStart('#').Trim();
                    int colon = comment.IndexOf(':');
                    if (colon > 0)
                    {
                        string key = comment[..colon].Trim();
                        string val = comment[(colon + 1)..].Trim();
                        if (key.Equals("commit", StringComparison.OrdinalIgnoreCase))
                            commit = NormalizeCommitSha(val);
                        else if (key.Equals("built_at", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(val, out var dt))
                            builtAt = dt.ToUniversalTime();
                        else if (key.Equals("app_sha256", StringComparison.OrdinalIgnoreCase))
                            appSha = ExtractSha256Digest(val);
                        else if (key.Equals("portable_sha256", StringComparison.OrdinalIgnoreCase))
                            portableSha = ExtractSha256Digest(val);
                    }
                    continue;
                }

                int sep = line.IndexOfAny([' ', '\t']);
                if (sep != 64) continue;

                string hash = line[..64];
                if (!IsHexString(hash)) continue;

                string file = line[sep..].Trim().TrimStart('*').Trim();
                if (!string.IsNullOrWhiteSpace(file))
                    map[Path.GetFileName(file)] = hash.ToLowerInvariant();
            }
        }

        return new ReleaseChecksumManifest(map, commit, builtAt, appSha, portableSha);
    }

    internal static bool CommitShasMatch(string? left, string? right)
    {
        string? l = NormalizeCommitSha(left);
        string? r = NormalizeCommitSha(right);
        if (l is null || r is null) return false;
        return l.StartsWith(r, StringComparison.OrdinalIgnoreCase) || r.StartsWith(l, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeCommitSha(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        int sep = s.IndexOfAny(['.', '-', ' ', '\t']);
        if (sep > 0) s = s[..sep];
        return s.Length is >= 7 and <= 40 && IsHexString(s) ? s.ToLowerInvariant() : null;
    }

    internal static string? ExtractSha256Digest(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s[7..].Trim();
        return s.Length == 64 && IsHexString(s) ? s.ToLowerInvariant() : null;
    }

    private async Task<ReleaseChecksumManifest?> TryFetchChecksumManifestAsync(
        ReleaseAssetInfo checksumAsset,
        string? token,
        CancellationToken ct)
    {
        try
        {
            string sumUrl = ChooseDownloadUrl(checksumAsset, token);
            if (!IsTrustedGitHubUrl(sumUrl)) return null;

            string accept = !string.IsNullOrWhiteSpace(token) && sumUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
                ? "application/octet-stream"
                : "text/plain";
            using var sumReq = CreateGitHubRequest(HttpMethod.Get, sumUrl, accept, token);
            using var sumResp = await _http.SendAsync(sumReq, ct);
            if (!sumResp.IsSuccessStatusCode) return null;

            string sumText = await sumResp.Content.ReadAsStringAsync(ct);
            return ParseChecksumManifest(sumText);
        }
        catch
        {
            return null;
        }
    }

    internal static int CompareVersions(string left, string right)
    {
        var l = ParseSemVer(left);
        var r = ParseSemVer(right);

        int cmp = l.Major.CompareTo(r.Major);
        if (cmp != 0) return cmp;
        cmp = l.Minor.CompareTo(r.Minor);
        if (cmp != 0) return cmp;
        cmp = l.Patch.CompareTo(r.Patch);
        if (cmp != 0) return cmp;

        bool lPre = !string.IsNullOrEmpty(l.PreRelease);
        bool rPre = !string.IsNullOrEmpty(r.PreRelease);
        if (!lPre && !rPre) return 0;
        if (!lPre) return 1;
        if (!rPre) return -1;

        var lp = l.PreRelease!.Split('.');
        var rp = r.PreRelease!.Split('.');
        int max = Math.Max(lp.Length, rp.Length);
        for (int i = 0; i < max; i++)
        {
            if (i >= lp.Length) return -1;
            if (i >= rp.Length) return 1;
            bool lNum = int.TryParse(lp[i], out int li);
            bool rNum = int.TryParse(rp[i], out int ri);
            int partCmp = (lNum, rNum) switch
            {
                (true, true) => li.CompareTo(ri),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(lp[i], rp[i], StringComparison.OrdinalIgnoreCase)
            };
            if (partCmp != 0) return partCmp;
        }
        return 0;
    }

    internal static string NormalizeVersion(string raw)
    {
        var parsed = ParseSemVer(raw);
        return string.IsNullOrEmpty(parsed.PreRelease)
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}"
            : $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}-{parsed.PreRelease}";
    }

    public static bool IsTrustedGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return TrustedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase))
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static (int Major, int Minor, int Patch, string? PreRelease) ParseSemVer(string raw)
    {
        string s = (raw ?? string.Empty).Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int major = parts.Length > 0 && int.TryParse(parts[0], out int mj) ? mj : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int mn) ? mn : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int pt) ? pt : 0;
        return (major, minor, patch, pre);
    }

    private static string? ResolveToken(string? tokenOverride, UpdaterSettings settings) =>
        !string.IsNullOrWhiteSpace(tokenOverride)
            ? tokenOverride.Trim()
            : !string.IsNullOrWhiteSpace(settings.GitHubToken)
                ? settings.GitHubToken.Trim()
                : Environment.GetEnvironmentVariable("LAPTOPTESTER_GITHUB_TOKEN");

    private static string ChooseDownloadUrl(ReleaseAssetInfo asset, string? token) =>
        !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(asset.ApiDownloadUrl)
            ? asset.ApiDownloadUrl
            : (!string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ? asset.BrowserDownloadUrl : asset.ApiDownloadUrl);

    private static HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url, string accept, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd("LaptopTester-Updater/1.0");
        req.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(token) && Uri.TryCreate(url, UriKind.Absolute, out var u)
            && (u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)))
        {
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static bool CanWriteDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    private static bool IsHexString(string value)
    {
        foreach (char c in value)
        {
            bool ok = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    private static string GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private static string FormatMegabytes(long bytes) =>
        bytes <= 0 ? "0.0" : (bytes / (1024.0 * 1024.0)).ToString("0.1");

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
