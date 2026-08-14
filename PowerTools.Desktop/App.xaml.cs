using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Reflection;

namespace PowerTools.Desktop;

public partial class App : Application
{
    private Mutex? _mutex;
    private Process? _server;
    private StreamWriter? _serverLog;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launch = ParseLaunchArguments(e.Args);
        _mutex = new Mutex(true, "PowerTools.Desktop." + InstanceKey(launch), out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("PowerTools 已经在运行。", "PowerTools", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var serverPath = Path.Combine(AppContext.BaseDirectory, "server", "PowerTools.exe");
        if (!File.Exists(serverPath))
        {
            MessageBox.Show("未找到 server\\PowerTools.exe，请使用完整桌面发布包。", "PowerTools 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var port = FindAvailablePort();
        var url = $"http://127.0.0.1:{port}";
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTools", "Logs");
        Directory.CreateDirectory(logFolder);
        _serverLog = new StreamWriter(Path.Combine(logFolder, $"server-{DateTime.Now:yyyyMMdd}.jsonl"), append: true) { AutoFlush = true };
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(url);
        startInfo.Environment["POWERTOOLS_DESKTOP_HOST"] = "1";
        if (launch is not null)
        {
            startInfo.Environment["POWERTOOLS_LIVE_SERVER"] = launch.Server;
            startInfo.Environment["POWERTOOLS_LIVE_DATABASE"] = launch.Database;
        }
        _server = Process.Start(startInfo);
        if (_server is not null)
        {
            _ = PumpLogAsync(_server.StandardOutput);
            _ = PumpLogAsync(_server.StandardError);
        }

        if (_server is null || !await WaitUntilReady(url, TimeSpan.FromSeconds(20)))
        {
            StopServer();
            MessageBox.Show($"本地分析服务未能启动。请查看日志：\n{logFolder}", "PowerTools 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(3);
            return;
        }

        var window = new MainWindow(launch is null ? url : url + "/?live=1");
        MainWindow = window;
        window.Show();
    }

    private static LaunchContext? ParseLaunchArguments(IReadOnlyList<string> args)
    {
        string? server = null, database = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) server = args[++i];
            else if (args[i].Equals("--database", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) database = args[++i];
        }
        return string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database) ? null : new(server, database);
    }

    private static string InstanceKey(LaunchContext? launch)
    {
        if (launch is null) return "Standalone";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{launch.Server}\n{launch.Database}")))[..16];
    }

    private sealed record LaunchContext(string Server, string Database);

    protected override void OnExit(ExitEventArgs e)
    {
        StopServer();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void StopServer()
    {
        try { if (_server is { HasExited: false }) _server.Kill(true); }
        catch { }
        _server?.Dispose();
        _serverLog?.Dispose();
    }

    private async Task PumpLogAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                if (_serverLog is not null) await _serverLog.WriteLineAsync(line);
        }
        catch { }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitUntilReady(string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if ((await client.GetAsync(url + "/health/ready")).IsSuccessStatusCode) return true; }
            catch { }
            await Task.Delay(250);
        }
        return false;
    }

    public void StartUpdate(UpdateLaunchRequest request)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var updateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(localData, "PowerTools", "Updates")));
        var package = Path.GetFullPath(request.PackagePath);
        if (!package.StartsWith(updateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(package))
            throw new UnauthorizedAccessException("更新包不在 PowerTools 受控暂存目录中。");
        if (request.PackageSha256.Length != 64 || !request.PackageSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("更新包摘要格式无效。");
        if (request.Mode is not ("delta" or "full")) throw new InvalidDataException("更新模式无效。");

        var updater = Path.Combine(AppContext.BaseDirectory, "PowerTools.Updater.exe");
        if (!File.Exists(updater)) throw new FileNotFoundException("当前安装缺少 PowerTools.Updater.exe，请先使用完整安装包升级。", updater);
        var launcher = Path.Combine(Path.GetDirectoryName(package)!, $"PowerTools.Updater-{Guid.NewGuid():N}.exe");
        File.Copy(updater, launcher, false);
        var currentVersion = NormalizeVersion(typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        if (!Version.TryParse(currentVersion, out var current) || !Version.TryParse(NormalizeVersion(request.TargetVersion), out var targetVersion) || targetVersion <= current)
            throw new InvalidDataException("目标版本不是可接受的升级版本。");
        var expectedName = request.Mode == "delta"
            ? $"PowerTools-Delta-{currentVersion}-to-{NormalizeVersion(request.TargetVersion)}-win-x64.zip"
            : $"PowerTools-Setup-{NormalizeVersion(request.TargetVersion)}-win-x64.exe";
        if (!Path.GetFileName(package).Equals(expectedName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包名称与升级任务不匹配。");
        var start = new ProcessStartInfo
        {
            FileName = launcher,
            WorkingDirectory = Path.GetDirectoryName(package)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Add("package", package);
        Add("sha256", request.PackageSha256);
        Add("mode", request.Mode);
        Add("install-root", AppContext.BaseDirectory);
        Add("current-version", currentVersion);
        Add("target-version", request.TargetVersion);
        Add("restart", Path.Combine(AppContext.BaseDirectory, "PowerTools.Desktop.exe"));
        Add("wait-pid", Environment.ProcessId.ToString());
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动独立更新器。");

        void Add(string name, string value)
        {
            start.ArgumentList.Add("--" + name);
            start.ArgumentList.Add(value);
        }
    }

    private static string NormalizeVersion(string value)
    {
        var result = value.Trim().TrimStart('v', 'V');
        var suffix = result.IndexOfAny(new[] { '+', '-' });
        return suffix > 0 ? result[..suffix] : result;
    }
}

public sealed record UpdateLaunchRequest(string PackagePath, string PackageSha256, string Mode, string TargetVersion);
