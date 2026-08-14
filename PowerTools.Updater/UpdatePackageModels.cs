namespace PowerTools.Updater;

public sealed record UpdatePackageManifest(
    int SchemaVersion,
    string FromVersion,
    string ToVersion,
    string Runtime,
    DateTimeOffset CreatedAt,
    IReadOnlyList<UpdatePackageFile> Files,
    IReadOnlyList<string> RemovedFiles);

public sealed record UpdatePackageFile(string Path, string Sha256, long Size);

public sealed record UpdateApplyResult(
    string FromVersion,
    string ToVersion,
    DateTimeOffset AppliedAt,
    int UpdatedFileCount,
    int RemovedFileCount,
    string BackupPath);
