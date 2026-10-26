using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;

namespace PowerTools.Desktop;

public partial class MainWindow : Window
{
    private readonly string _url;

    public MainWindow(string url)
    {
        InitializeComponent();
        _url = url;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTools", "WebView2");
            await Browser.EnsureCoreWebView2Async(await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder));
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            Browser.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败：{ex.Message}\n\n请安装 Microsoft Edge WebView2 Runtime。", "PowerTools", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
                !Uri.TryCreate(_url, UriKind.Absolute, out var expected) || source.Scheme != expected.Scheme ||
                source.Host != expected.Host || source.Port != expected.Port)
                return;
            var message = JsonSerializer.Deserialize<DesktopMessage>(e.WebMessageAsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (message?.Type == "browse-project")
            {
                if (message.Target is not ("pathInput" or "compareBaseline" or "compareCurrent")) return;
                string? path = null;
                if (message.Kind == "file")
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择 Power BI 项目", Filter = "Power BI 项目 (*.pbip)|*.pbip", CheckFileExists = true };
                    if (dialog.ShowDialog(this) == true) path = dialog.FileName;
                }
                else
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 PBIP、Report 或 SemanticModel 目录", Multiselect = false };
                    if (dialog.ShowDialog(this) == true) path = dialog.FolderName;
                }
                if (path is not null) Reply(new { type = "project-selected", target = message.Target, path });
                return;
            }
            if (message?.Type == "get-recent-projects")
            {
                var paths = File.Exists(RecentProjectsFile) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(RecentProjectsFile)) ?? [] : [];
                Reply(new { type = "recent-projects", paths = paths.Take(6) });
                return;
            }
            if (message?.Type == "save-recent-projects")
            {
                var paths = (message.Paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path) && path.Length < 4096).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(RecentProjectsFile)!);
                File.WriteAllText(RecentProjectsFile, JsonSerializer.Serialize(paths));
                return;
            }
            if (message?.Type != "apply-update" || string.IsNullOrWhiteSpace(message.PackagePath) || string.IsNullOrWhiteSpace(message.PackageSha256) ||
                string.IsNullOrWhiteSpace(message.Mode) || string.IsNullOrWhiteSpace(message.TargetVersion)) return;
            var confirm = MessageBox.Show(
                $"准备更新到 PowerTools {message.TargetVersion}。\n\n模式：{(message.Mode == "delta" ? "增量更新" : "完整安装包")}\n更新包已完成 SHA-256 校验。应用将关闭 PowerTools，并可能显示 Windows 管理员授权提示。\n\n现在更新吗？",
                "PowerTools 更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;
            ((App)Application.Current).StartUpdate(new UpdateLaunchRequest(message.PackagePath, message.PackageSha256, message.Mode, message.TargetVersion));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Reply(new { type = "desktop-error", message = $"桌面操作失败：{ex.Message}" });
        }
    }

    private static string RecentProjectsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerTools", "recent-projects.json");

    private void Reply(object message) => Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));

    private sealed record DesktopMessage(string Type, string? PackagePath, string? PackageSha256, string? Mode, string? TargetVersion, string? Target, string? Kind, string[]? Paths);
}
