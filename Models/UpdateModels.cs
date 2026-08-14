namespace PowerTools;

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string Mode,
    string ReleaseName,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    string ReleaseUrl,
    string? AssetName,
    long? AssetSize,
    string? AssetSha256,
    bool AutomaticInstallSupported,
    bool DesktopHost,
    string Message);

public sealed record UpdateDownloadRequest(bool Refresh = false);

public sealed record StagedUpdate(
    string CurrentVersion,
    string TargetVersion,
    string Mode,
    string PackagePath,
    string PackageSha256,
    long PackageSize,
    string ReleaseUrl,
    bool DesktopHost);
