using System.Diagnostics;
using PowerTools.Updater;

var arguments = ParseArguments(args);
var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var logRoot = Path.Combine(localData, "PowerTools", "UpdateLogs");
Directory.CreateDirectory(logRoot);
var logPath = Path.Combine(logRoot, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");
var restartOnFailure = arguments.TryGetValue("restart", out var restartArgument) ? Path.GetFullPath(restartArgument) : null;
var noRestart = arguments.TryGetValue("no-restart", out var noRestartValue) && noRestartValue == "1";

try
{
    var package = Required("package");
    var expectedSha = Required("sha256");
    var mode = Required("mode");
    var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required("install-root")));
    var currentVersion = Required("current-version");
    var targetVersion = Required("target-version");
    var restart = Path.GetFullPath(Required("restart"));
    var waitPid = int.Parse(Required("wait-pid"));
    Log($"准备把 PowerTools {currentVersion} 更新到 {targetVersion}，模式 {mode}。");
    ValidatePackageLocation(package, localData);
    var actualSha = DeltaUpdateEngine.ComputeSha256(package);
    if (!actualSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("暂存更新包 SHA-256 校验失败。");
    await WaitForExitAsync(waitPid, TimeSpan.FromSeconds(90));
    await StopInstalledProcessesAsync(installRoot, TimeSpan.FromSeconds(30));

    if (mode.Equals("delta", StringComparison.OrdinalIgnoreCase))
    {
        var workRoot = Path.Combine(localData, "PowerTools", "UpdateBackups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{targetVersion}");
        var result = DeltaUpdateEngine.Apply(package, installRoot, workRoot, currentVersion, targetVersion);
        Log($"增量更新完成：{result.UpdatedFileCount} 个文件，备份 {result.BackupPath}。");
    }
    else if (mode.Equals("full", StringComparison.OrdinalIgnoreCase))
    {
        var installer = Process.Start(new ProcessStartInfo
        {
            FileName = package,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP-",
            WorkingDirectory = Path.GetDirectoryName(package)!,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("无法启动完整安装程序。");
        await installer.WaitForExitAsync();
        if (installer.ExitCode != 0) throw new InvalidOperationException($"完整安装程序返回错误代码 {installer.ExitCode}。");
        Log("完整安装程序已完成。");
    }
    else throw new InvalidDataException($"未知更新模式：{mode}");

    if (!noRestart && File.Exists(restart)) Process.Start(new ProcessStartInfo { FileName = restart, WorkingDirectory = Path.GetDirectoryName(restart)!, UseShellExecute = true });
}
catch (Exception ex)
{
    Log("更新失败：" + ex);
    if (!noRestart && restartOnFailure is not null && File.Exists(restartOnFailure))
    {
        try { Process.Start(new ProcessStartInfo { FileName = restartOnFailure, WorkingDirectory = Path.GetDirectoryName(restartOnFailure)!, UseShellExecute = true }); }
        catch { }
    }
    Environment.ExitCode = 1;
}

return;

string Required(string name) => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
    ? value : throw new InvalidDataException($"缺少更新参数 --{name}。");

void Log(string value) => File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");

static Dictionary<string, string> ParseArguments(IReadOnlyList<string> values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Count; index++)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Count) continue;
        result[values[index][2..]] = values[++index];
    }
    return result;
}

static async Task WaitForExitAsync(int processId, TimeSpan timeout)
{
    Process? process;
    try { process = Process.GetProcessById(processId); }
    catch (ArgumentException) { return; }
    using (process)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("等待 PowerTools 退出超时，未修改安装文件。"); }
    }
}

static async Task StopInstalledProcessesAsync(string installRoot, TimeSpan timeout)
{
    installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
    var end = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < end)
    {
        var matches = FindInstalledProcesses(installRoot).ToList();
        if (matches.Count == 0) return;
        foreach (var process in matches)
        {
            try
            {
                if (process.ProcessName.Equals("PowerTools.Desktop", StringComparison.OrdinalIgnoreCase)) process.CloseMainWindow();
                else process.Kill(true);
            }
            catch { }
            finally { process.Dispose(); }
        }
        await Task.Delay(500);
    }
    var remaining = FindInstalledProcesses(installRoot).ToList();
    foreach (var process in remaining)
    {
        try { process.Kill(true); process.WaitForExit(5000); }
        catch { }
        finally { process.Dispose(); }
    }
    var stillRunning = FindInstalledProcesses(installRoot).ToList();
    foreach (var process in stillRunning) process.Dispose();
    if (stillRunning.Count > 0) throw new InvalidOperationException("仍有 PowerTools 进程占用安装文件，更新未执行。");
}

static IEnumerable<Process> FindInstalledProcesses(string installRoot)
{
    foreach (var name in new[] { "PowerTools.Desktop", "PowerTools" })
    foreach (var process in Process.GetProcessesByName(name))
    {
        if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
        var matches = false;
        try
        {
            var path = process.MainModule?.FileName;
            matches = path is not null && (path.Equals(Path.Combine(installRoot, "PowerTools.Desktop.exe"), StringComparison.OrdinalIgnoreCase) ||
                path.Equals(Path.Combine(installRoot, "server", "PowerTools.exe"), StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        if (matches) yield return process;
        else process.Dispose();
    }
}

static void ValidatePackageLocation(string packagePath, string localData)
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(localData, "PowerTools", "Updates")));
    var package = Path.GetFullPath(packagePath);
    if (!package.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(package))
        throw new UnauthorizedAccessException("更新包不在 PowerTools 受控暂存目录中。");
}
