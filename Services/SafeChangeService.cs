using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PowerTools.Services;

public sealed class SafeChangeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "artifacts", "publish", ".powertools"
    };
    private static readonly EnumerationOptions SafeEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly ProjectSnapshotCache _cache;
    private readonly string _workspaceRoot;
    private readonly string _planRoot;
    private readonly int _maxOperations;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _planLocks = new(StringComparer.OrdinalIgnoreCase);

    public SafeChangeService(ProjectSnapshotCache cache, IOptions<SafeChangeOptions> options)
    {
        _cache = cache;
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
        _workspaceRoot = NormalizeRoot(options.Value.WorkspaceRoot, Path.Combine(localData, "PowerTools", "Workspaces"));
        _planRoot = NormalizeRoot(options.Value.PlanRoot, Path.Combine(localData, "PowerTools", "ChangePlans"));
        _maxOperations = Math.Clamp(options.Value.MaxOperations, 1, 500);
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_planRoot);
    }

    public async Task<SafeChangePlan> CreatePlanAsync(string sourcePath, IReadOnlyList<SafeChangeSelection> selections, CancellationToken cancellationToken)
    {
        if (selections.Count == 0) throw new InvalidDataException("请至少选择一个待隔离对象。");
        if (selections.Count > _maxOperations) throw new InvalidDataException($"单个计划最多允许 {_maxOperations} 个操作。");
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException($"项目目录不存在：{sourcePath}");
        sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        if (IsWithin(_workspaceRoot, sourcePath) || IsWithin(_planRoot, sourcePath))
            throw new InvalidDataException("SafeChanges 受控目录不能配置在待修改源项目内部。");

        var snapshot = await _cache.GetAsync(sourcePath, true, cancellationToken);
        if (!snapshot.Format.Contains("TMDL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安全修改首版仅支持 PBIP/TMDL 项目；PBIX 实时模型和 model.bim 保持只读。");

        var uniqueSelections = selections
            .DistinctBy(item => $"{item.ObjectType}\u001f{item.TableName}\u001f{item.ObjectName}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (uniqueSelections.Count != selections.Count) throw new InvalidDataException("选择中包含重复对象。");

        var operations = new List<SafeChangeOperation>();
        foreach (var selection in uniqueSelections)
        {
            var objectType = selection.ObjectType.Trim().ToLowerInvariant();
            if (objectType is not ("measure" or "column")) throw new InvalidDataException($"暂不支持修改对象类型：{selection.ObjectType}");
            var candidate = snapshot.RemovalCandidates.FirstOrDefault(item =>
                item.ObjectType.Equals(objectType, StringComparison.OrdinalIgnoreCase) &&
                item.TableName.Equals(selection.TableName, StringComparison.OrdinalIgnoreCase) &&
                item.ObjectName.Equals(selection.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (candidate is null) throw new InvalidDataException($"对象不在当前删除候选清单中：{selection.TableName}[{selection.ObjectName}]");
            if (!candidate.Status.Equals("candidate", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"对象存在模型内引用，风险门禁已阻止修改：{selection.TableName}[{selection.ObjectName}]");

            var match = FindObject(sourcePath, objectType, selection.TableName, selection.ObjectName);
            if (match.IsHidden) throw new InvalidDataException($"对象已经隐藏，无需重复隔离：{selection.TableName}[{selection.ObjectName}]");
            operations.Add(new SafeChangeOperation(
                Guid.NewGuid().ToString("N"), "quarantine-hide", objectType, selection.TableName, selection.ObjectName,
                Path.GetRelativePath(sourcePath, match.FilePath), candidate.RiskScore, candidate.Confidence,
                candidate.Reasons, candidate.Evidence, $"在隔离副本中为 {objectType} {selection.TableName}[{selection.ObjectName}] 添加 isHidden"));
        }

        var now = DateTimeOffset.Now;
        var planId = $"CHG-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..29];
        var plan = new SafeChangePlan(
            planId, snapshot.Name, Path.GetFullPath(sourcePath), ComputeFingerprint(sourcePath), now, now, "planned",
            $"APPLY {planId}", $"ROLLBACK {planId}", operations,
            new[]
            {
                "计划只会在 PowerTools 管理的隔离副本中隐藏对象，源项目不会被写入。",
                "静态分析无法发现其他 PBIX、Excel/XMLA 客户端、动态字符串和外部系统引用。",
                "应用后必须在 Power BI Desktop 中打开隔离副本，验证刷新、视觉对象、书签、RLS 与下游消费者。"
            },
            new[] { new SafeChangeAuditEvent(now, "plan-created", $"已生成 {operations.Count} 项隔离修改计划。") });
        await SavePlanAsync(plan, cancellationToken);
        return plan;
    }

    public Task<SafeChangePlan?> GetPlanAsync(string planId, CancellationToken cancellationToken) => LoadPlanAsync(planId, cancellationToken);

    public async Task<SafeChangePlan> ApplyAsync(string planId, string confirmationPhrase, CancellationToken cancellationToken)
    {
        var gate = _planLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1));
        SafeChangePlan? applying = null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var plan = await RequirePlanAsync(planId, cancellationToken);
            if (!plan.Status.Equals("planned", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"计划当前状态为 {plan.Status}，不能重复应用。");
            if (!string.Equals(confirmationPhrase?.Trim(), plan.ConfirmationPhrase, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("确认短语不匹配，未执行任何修改。");
            if (!Directory.Exists(plan.SourcePath)) throw new DirectoryNotFoundException($"源项目目录不存在：{plan.SourcePath}");
            var currentFingerprint = ComputeFingerprint(plan.SourcePath);
            if (!string.Equals(currentFingerprint, plan.SourceFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("源项目自计划生成后已发生变化。请重新分析并生成新计划。");

            var workspace = CreateWorkspacePath(plan);
            Directory.CreateDirectory(workspace);
            CopyProject(plan.SourcePath, workspace);
            if (!string.Equals(ComputeFingerprint(workspace), plan.SourceFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("隔离复制期间检测到源文件变化或复制不完整，未应用修改。");
            var controlRoot = Path.Combine(workspace, ".powertools");
            var backupRoot = Path.Combine(controlRoot, "backups", plan.PlanId);
            Directory.CreateDirectory(backupRoot);

            foreach (var operation in plan.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(operation.SourceFile);
                var targetFile = ResolveWithin(workspace, relative);
                var backupFile = ResolveWithin(backupRoot, relative);
                if (!File.Exists(targetFile)) throw new FileNotFoundException("隔离副本缺少计划文件。", targetFile);
                if (!File.Exists(backupFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(targetFile, backupFile, false);
                }
            }

            var auditPath = Path.Combine(controlRoot, "audit.json");
            var applyingAt = DateTimeOffset.Now;
            applying = plan with
            {
                Status = "applying",
                UpdatedAt = applyingAt,
                WorkspacePath = workspace,
                AuditPath = auditPath,
                AuditTrail = plan.AuditTrail.Append(new SafeChangeAuditEvent(applyingAt, "applying", "隔离副本和完整文件备份已就绪，开始应用修改。")).ToList()
            };
            await WriteJsonAtomicAsync(auditPath, applying, cancellationToken);
            await SavePlanAsync(applying, cancellationToken);

            foreach (var operation in applying.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFile = ResolveWithin(workspace, NormalizeRelativePath(operation.SourceFile));
                await HideObjectAsync(targetFile, operation.ObjectType, operation.TableName, operation.ObjectName, cancellationToken);
            }

            var now = DateTimeOffset.Now;
            var applied = applying with
            {
                Status = "applied",
                UpdatedAt = now,
                AuditTrail = applying.AuditTrail.Append(new SafeChangeAuditEvent(now, "applied", $"已在隔离副本应用 {plan.Operations.Count} 项修改；源项目未改动。")).ToList()
            };
            await WriteJsonAtomicAsync(auditPath, applied, cancellationToken);
            await SavePlanAsync(applied, cancellationToken);
            return applied;
        }
        catch (Exception ex)
        {
            if (applying is not null)
            {
                var restored = false;
                try { restored = await RestoreWorkspaceBackupsAsync(applying, CancellationToken.None); }
                catch { }
                var now = DateTimeOffset.Now;
                var failed = applying with
                {
                    Status = "apply-failed",
                    UpdatedAt = now,
                    AuditTrail = applying.AuditTrail.Append(new SafeChangeAuditEvent(now, "apply-failed", restored
                        ? $"应用失败，隔离副本已自动从备份恢复：{ex.Message}"
                        : $"应用失败且自动恢复未完整完成，请执行回滚：{ex.Message}")).ToList()
                };
                if (failed.AuditPath is not null) await WriteJsonAtomicAsync(failed.AuditPath, failed, CancellationToken.None);
                await SavePlanAsync(failed, CancellationToken.None);
            }
            else await RecordFailureAsync(planId, "apply-failed", ex.Message, CancellationToken.None);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<SafeChangePlan> RollbackAsync(string planId, string confirmationPhrase, CancellationToken cancellationToken)
    {
        var gate = _planLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var plan = await RequirePlanAsync(planId, cancellationToken);
            if (plan.Status is not ("applied" or "applying" or "apply-failed"))
                throw new InvalidOperationException($"计划当前状态为 {plan.Status}，没有可回滚的已应用修改。");
            if (!string.Equals(confirmationPhrase?.Trim(), plan.RollbackPhrase, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("回滚确认短语不匹配，未执行任何修改。");
            if (string.IsNullOrWhiteSpace(plan.WorkspacePath) || !IsWithin(plan.WorkspacePath, _workspaceRoot))
                throw new UnauthorizedAccessException("工作区不属于 PowerTools 管理目录，拒绝回滚。");

            if (!await RestoreWorkspaceBackupsAsync(plan, cancellationToken)) throw new InvalidOperationException("部分回滚备份不存在，未能完整恢复隔离副本。");

            var now = DateTimeOffset.Now;
            var rolledBack = plan with
            {
                Status = "rolled-back",
                UpdatedAt = now,
                AuditTrail = plan.AuditTrail.Append(new SafeChangeAuditEvent(now, "rolled-back", "已从备份恢复隔离副本；源项目始终未改动。")).ToList()
            };
            if (rolledBack.AuditPath is not null) await WriteJsonAtomicAsync(rolledBack.AuditPath, rolledBack, cancellationToken);
            await SavePlanAsync(rolledBack, cancellationToken);
            return rolledBack;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(planId, "rollback-failed", ex.Message, cancellationToken);
            throw;
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> RestoreWorkspaceBackupsAsync(SafeChangePlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.WorkspacePath)) return false;
        var backupRoot = Path.Combine(plan.WorkspacePath, ".powertools", "backups", plan.PlanId);
        var restoredAll = true;
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(operation.SourceFile);
            var backupFile = ResolveWithin(backupRoot, relative);
            var targetFile = ResolveWithin(plan.WorkspacePath, relative);
            if (!File.Exists(backupFile)) { restoredAll = false; continue; }
            await CopyFileAtomicAsync(backupFile, targetFile, cancellationToken);
        }
        return restoredAll;
    }

    private async Task RecordFailureAsync(string planId, string eventType, string detail, CancellationToken cancellationToken)
    {
        try
        {
            var plan = await LoadPlanAsync(planId, cancellationToken);
            if (plan is null) return;
            var now = DateTimeOffset.Now;
            var updated = plan with { UpdatedAt = now, AuditTrail = plan.AuditTrail.Append(new SafeChangeAuditEvent(now, eventType, detail)).ToList() };
            await SavePlanAsync(updated, cancellationToken);
        }
        catch { }
    }

    private async Task<SafeChangePlan> RequirePlanAsync(string planId, CancellationToken cancellationToken) =>
        await LoadPlanAsync(planId, cancellationToken) ?? throw new KeyNotFoundException($"找不到修改计划：{planId}");

    private async Task<SafeChangePlan?> LoadPlanAsync(string planId, CancellationToken cancellationToken)
    {
        var path = GetPlanPath(planId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SafeChangePlan>(stream, JsonOptions, cancellationToken);
    }

    private Task SavePlanAsync(SafeChangePlan plan, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(GetPlanPath(plan.PlanId), plan, cancellationToken);

    private string GetPlanPath(string planId)
    {
        if (!Regex.IsMatch(planId ?? "", "^[A-Za-z0-9-]{8,64}$")) throw new InvalidDataException("计划编号格式无效。");
        return ResolveWithin(_planRoot, planId + ".json");
    }

    private string CreateWorkspacePath(SafeChangePlan plan)
    {
        var safeName = Regex.Replace(plan.ProjectName, "[^A-Za-z0-9._-]+", "-").Trim('-');
        if (safeName.Length == 0) safeName = "PowerBI-Project";
        return ResolveWithin(_workspaceRoot, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}-{plan.PlanId[^8..]}");
    }

    private static ObjectMatch FindObject(string root, string objectType, string tableName, string objectName)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.tmdl", SafeEnumeration))
        {
            var lines = File.ReadAllLines(file);
            var table = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("table ", StringComparison.OrdinalIgnoreCase));
            if (table is null || !Unquote(table[6..].Trim()).Equals(tableName, StringComparison.OrdinalIgnoreCase)) continue;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryParseDeclaration(lines[index].Trim(), out var kind, out var name) || kind != objectType || !name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                var end = FindBlockEnd(lines, index);
                var hidden = lines.Skip(index + 1).Take(end - index - 1).Any(line => line.Trim().Equals("isHidden", StringComparison.OrdinalIgnoreCase) || line.Trim().StartsWith("isHidden:", StringComparison.OrdinalIgnoreCase));
                return new ObjectMatch(file, index, end, hidden);
            }
        }
        throw new InvalidDataException($"未在 TMDL 中定位对象：{tableName}[{objectName}]");
    }

    private static async Task HideObjectAsync(string file, string objectType, string tableName, string objectName, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, cancellationToken);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var table = lines.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("table ", StringComparison.OrdinalIgnoreCase));
        if (table is null || !Unquote(table[6..].Trim()).Equals(tableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"计划文件中的表名已变化：{tableName}");
        for (var index = 0; index < lines.Count; index++)
        {
            if (!TryParseDeclaration(lines[index].Trim(), out var kind, out var name) || kind != objectType || !name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
            var end = FindBlockEnd(lines, index);
            if (lines.Skip(index + 1).Take(end - index - 1).Any(line => line.Trim().Equals("isHidden", StringComparison.OrdinalIgnoreCase) || line.Trim().StartsWith("isHidden:", StringComparison.OrdinalIgnoreCase))) return;
            var declarationIndent = lines[index][..(lines[index].Length - lines[index].TrimStart().Length)];
            lines.Insert(end, declarationIndent + "\tisHidden");
            var updated = string.Join(newline, lines) + (trailingNewline ? newline : string.Empty);
            await WriteTextAtomicAsync(file, updated, cancellationToken);
            return;
        }
        throw new InvalidDataException($"隔离副本中未找到对象：{tableName}[{objectName}]");
    }

    private static int FindBlockEnd(IReadOnlyList<string> lines, int declarationIndex)
    {
        var indent = LeadingWhitespace(lines[declarationIndex]);
        var end = declarationIndex + 1;
        for (; end < lines.Count; end++)
            if (!string.IsNullOrWhiteSpace(lines[end]) && LeadingWhitespace(lines[end]) <= indent) break;
        return end;
    }

    private static bool TryParseDeclaration(string text, out string kind, out string name)
    {
        kind = string.Empty;
        name = string.Empty;
        var split = text.IndexOfAny(new[] { ' ', '\t' });
        if (split <= 0) return false;
        kind = text[..split].Trim().ToLowerInvariant();
        if (kind is not ("measure" or "column")) return false;
        var remainder = text[(split + 1)..].Trim();
        var equals = FindUnquotedEquals(remainder);
        name = Unquote((equals >= 0 ? remainder[..equals] : remainder).Trim());
        return name.Length > 0;
    }

    private static int FindUnquotedEquals(string text)
    {
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'' && index + 1 < text.Length && text[index + 1] == '\'') index++;
            else if (text[index] == '\'') quoted = !quoted;
            else if (text[index] == '=' && !quoted) return index;
        }
        return -1;
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
        ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
        : value;

    private static int LeadingWhitespace(string value) => value.TakeWhile(char.IsWhiteSpace).Count();

    private static string ComputeFingerprint(string root)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateProjectFiles(root).OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            using var stream = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) aggregate.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyProject(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SafeEnumeration).Where(path => !ShouldIgnore(path, source)))
            Directory.CreateDirectory(ResolveWithin(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in EnumerateProjectFiles(source))
        {
            var target = ResolveWithin(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SafeEnumeration).Where(path => !ShouldIgnore(path, root));

    private static bool ShouldIgnore(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => IgnoredDirectories.Contains(part));
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAtomicAsync(path, json, cancellationToken);
    }

    private static async Task WriteTextAtomicAsync(string path, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, value, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyFileAtomicAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string NormalizeRoot(string? configured, string fallback) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim())));

    private static string NormalizeRelativePath(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
            throw new UnauthorizedAccessException("计划包含不安全的相对路径。");
        return relative;
    }

    private static string ResolveWithin(string root, string relative)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!IsWithin(path, normalizedRoot)) throw new UnauthorizedAccessException("目标路径超出受控目录。");
        return path;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ObjectMatch(string FilePath, int StartLine, int EndLine, bool IsHidden);
}
