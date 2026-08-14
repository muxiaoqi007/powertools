using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerTools.Updater;

public static class DeltaUpdateEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UpdateApplyResult Apply(string packagePath, string installRoot, string workRoot, string expectedCurrentVersion, string expectedTargetVersion)
    {
        installRoot = NormalizeRoot(installRoot);
        workRoot = NormalizeRoot(workRoot);
        if (!File.Exists(Path.Combine(installRoot, "PowerTools.Desktop.exe"))) throw new InvalidDataException("目标目录不是有效的 PowerTools 安装目录。");
        Directory.CreateDirectory(workRoot);
        var extractRoot = ResolveWithin(workRoot, "extract");
        var backupRoot = ResolveWithin(workRoot, "backup");
        Directory.CreateDirectory(extractRoot);
        Directory.CreateDirectory(backupRoot);
        ExtractPackage(packagePath, extractRoot);

        var manifestPath = ResolveWithin(extractRoot, "update-package.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("增量包缺少 update-package.json。");
        var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("无法解析增量更新清单。");
        ValidateManifest(manifest, extractRoot, expectedCurrentVersion, expectedTargetVersion);

        var touched = new List<(string Target, string Backup, bool Existed)>();
        try
        {
            foreach (var file in manifest.Files)
            {
                var relative = NormalizeRelative(file.Path);
                var source = ResolveWithin(Path.Combine(extractRoot, "payload"), relative);
                var target = ResolveInstallTarget(installRoot, relative);
                var backup = ResolveWithin(backupRoot, relative);
                var existed = File.Exists(target);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, false);
                }
                touched.Add((target, backup, existed));
                AtomicCopy(source, target);
            }

            foreach (var path in manifest.RemovedFiles)
            {
                var relative = NormalizeRelative(path);
                var target = ResolveInstallTarget(installRoot, relative);
                if (!File.Exists(target)) continue;
                var backup = ResolveWithin(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup, false);
                touched.Add((target, backup, true));
            }

            var result = new UpdateApplyResult(manifest.FromVersion, manifest.ToVersion, DateTimeOffset.Now,
                manifest.Files.Count, manifest.RemovedFiles.Count, backupRoot);
            File.WriteAllText(ResolveWithin(workRoot, "update-result.json"), JsonSerializer.Serialize(result, JsonOptions));
            return result;
        }
        catch
        {
            Rollback(touched);
            throw;
        }
    }

    private static void ExtractPackage(string packagePath, string extractRoot)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > 5000) throw new InvalidDataException("增量包文件数量超出限制。");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > 1_073_741_824L) throw new InvalidDataException("增量包解压大小超出 1 GB 限制。");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var relative = NormalizeRelative(entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!relative.Equals("update-package.json", StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith("payload" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"增量包包含不允许的条目：{entry.FullName}");
            var destination = ResolveWithin(extractRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void ValidateManifest(UpdatePackageManifest manifest, string extractRoot, string expectedCurrentVersion, string expectedTargetVersion)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"不支持的增量包格式：{manifest.SchemaVersion}");
        if (!NormalizeVersion(manifest.FromVersion).Equals(NormalizeVersion(expectedCurrentVersion), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"增量包基线版本不匹配：需要 {manifest.FromVersion}，当前 {expectedCurrentVersion}。");
        if (!NormalizeVersion(manifest.ToVersion).Equals(NormalizeVersion(expectedTargetVersion), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("增量包目标版本与下载任务不匹配。");
        if (!manifest.Runtime.Equals("win-x64", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("增量包运行时不受支持。");
        if (manifest.Files.Count == 0) throw new InvalidDataException("增量包没有可应用文件。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relative = NormalizeRelative(file.Path);
            if (!paths.Add(relative)) throw new InvalidDataException($"增量包包含重复文件：{relative}");
            if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"增量包文件元数据无效：{relative}");
            var payload = ResolveWithin(Path.Combine(extractRoot, "payload"), relative);
            if (!File.Exists(payload) || new FileInfo(payload).Length != file.Size) throw new InvalidDataException($"增量包文件大小不匹配：{relative}");
            if (!ComputeSha256(payload).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"增量包文件 SHA-256 不匹配：{relative}");
        }
        foreach (var removed in manifest.RemovedFiles)
            if (!paths.Add(NormalizeRelative(removed))) throw new InvalidDataException($"同一文件不能同时更新和移除：{removed}");

        var actualPayload = Directory.EnumerateFiles(Path.Combine(extractRoot, "payload"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(extractRoot, "payload"), path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPayload.SetEquals(manifest.Files.Select(file => NormalizeRelative(file.Path)))) throw new InvalidDataException("增量包 payload 与清单不一致。");
    }

    private static void Rollback(IEnumerable<(string Target, string Backup, bool Existed)> touched)
    {
        foreach (var item in touched.Reverse())
        {
            try
            {
                if (item.Existed && File.Exists(item.Backup)) AtomicCopy(item.Backup, item.Target);
                else if (!item.Existed && File.Exists(item.Target)) File.Delete(item.Target);
            }
            catch { }
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.update");
        try
        {
            File.Copy(source, temporary, false);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string value)
    {
        var result = value.Trim().TrimStart('v', 'V');
        var suffix = result.IndexOfAny(new[] { '+', '-' });
        return suffix > 0 ? result[..suffix] : result;
    }

    private static string NormalizeRelative(string value)
    {
        var normalized = value.Replace('/', Path.DirectorySeparatorChar).Trim();
        var parts = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains(':') ||
            parts.Any(part => part is ".." or "." || part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.') || IsReservedWindowsName(part)))
            throw new InvalidDataException($"更新包包含不安全路径：{value}");
        return normalized;
    }

    private static bool IsReservedWindowsName(string part)
    {
        var name = Path.GetFileNameWithoutExtension(part).ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
               name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }

    private static string NormalizeRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root == Path.GetPathRoot(root)) throw new UnauthorizedAccessException("拒绝把磁盘根目录作为更新目标。");
        return root;
    }

    private static string ResolveWithin(string root, string relative)
    {
        var normalizedRoot = NormalizeRoot(root);
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("更新文件超出受控目录。");
        return path;
    }

    private static string ResolveInstallTarget(string installRoot, string relative)
    {
        var path = ResolveWithin(installRoot, relative);
        var current = NormalizeRoot(installRoot);
        foreach (var part in NormalizeRelative(relative).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException($"安装目录包含重解析点，拒绝更新：{relative}");
        }
        return path;
    }
}
