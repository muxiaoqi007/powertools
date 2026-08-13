using System.IO;
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
            Browser.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            Browser.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 初始化失败：{ex.Message}\n\n请安装 Microsoft Edge WebView2 Runtime。", "PowerTools", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
