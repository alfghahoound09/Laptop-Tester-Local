namespace LaptopTester.Core.Models;

public enum AppInstallMode
{
    Installer,
    Portable,
    Development
}

/// <summary>Downloadable binary or checksum file attached to a GitHub Release.</summary>
public sealed record ReleaseAssetInfo(
    string Name,
    string BrowserDownloadUrl,
    string ApiDownloadUrl,
    long SizeBytes,
    DateTimeOffset? UpdatedAt = null,
    string? Digest = null);

/// <summary>Parsed metadata and assets for a published GitHub Release.</summary>
public sealed record GitHubReleaseInfo(
    string TagName,
    string NormalizedVersion,
    string ReleaseName,
    string HtmlUrl,
    string ReleaseNotes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAssetInfo> Assets);

/// <summary>Parsed SHA256SUMS.txt content including optional commit SHA and per-binary SHA-256 headers.</summary>
public sealed record ReleaseChecksumManifest(
    IReadOnlyDictionary<string, string> FileHashes,
    string? CommitSha,
    DateTimeOffset? BuiltAtUtc,
    string? AppExeSha256,
    string? PortableExeSha256);

/// <summary>Result of checking the remote GitHub repository for a newer release or same-version hotfix.</summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    AppInstallMode InstallMode,
    GitHubReleaseInfo? LatestRelease,
    ReleaseAssetInfo? PrimaryAsset,
    ReleaseAssetInfo? ChecksumAsset,
    string StatusMessage,
    string? ErrorDetail = null,
    bool IsSameVersionHotfix = false,
    string? RemoteCommitSha = null,
    string? RemoteAssetSha256 = null);

/// <summary>Streaming download progress while fetching an update package.</summary>
public sealed record UpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    int Percent,
    string StatusText);

/// <summary>Verified update binary ready to replace the current installation on exit.</summary>
public sealed record PreparedUpdate(
    string DownloadedFilePath,
    string VersionTag,
    AppInstallMode InstallMode,
    string TargetExePath,
    string InstallDirectory,
    bool Sha256Verified);

/// <summary>Persisted local updater preferences stored under LocalAppData.</summary>
public sealed class UpdaterSettings
{
    public bool AutoCheckOnStartup { get; set; } = true;
    public string? GitHubToken { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? InstalledHotfixCommit { get; set; }
    public string? InstalledHotfixSha256 { get; set; }
}
