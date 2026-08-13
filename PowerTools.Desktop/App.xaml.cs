using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Windows;

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
        _mutex = new Mutex(true, "PowerTools.Desktop.SingleInstance", out _ownsMutex);
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
        _server = Process.Start(new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--urls {url}",
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
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

        var window = new MainWindow(url);
        MainWindow = window;
        window.Show();
    }

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
}
