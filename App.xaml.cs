using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DesktopIconChanger;

public partial class App : Application
{
    private static ResourceDictionary? _activeTheme;
    public static bool IsDark { get; private set; }
    private static string LogPath = @"d:\Code\desktop_icon_changer\crash.txt";
    private static void Log(string msg) { try { File.AppendAllText(LogPath, $"[App] {msg}\n"); } catch { } }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        File.Delete(LogPath);

        // 全局异常捕获（保证能落盘）
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"!!! DispatcherUnhandledException: {args.Exception.GetType().FullName}\n{args.Exception.Message}\n{args.Exception.StackTrace}");
            if (args.Exception.InnerException != null)
                Log($"--- Inner ---\n{args.Exception.InnerException.Message}\n{args.Exception.InnerException.StackTrace}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log($"!!! AppDomain.UnhandledException: {ex.GetType().FullName}\n{ex.Message}\n{ex.StackTrace}");
        };

        Log($"OnStartup BEGIN");
        try
        {
            Log($"  SystemTheme.IsDark = {SystemTheme.IsDark}");
            ApplyTheme(SystemTheme.IsDark);
            SystemTheme.StartListening();
            SystemTheme.ThemeChanged += (_, _) =>
            {
                Log($"ThemeChanged fired, new IsDark = {SystemTheme.IsDark}");
                Dispatcher.Invoke(() => ApplyTheme(SystemTheme.IsDark));
            };
            Log("OnStartup END");
        }
        catch (Exception ex)
        {
            Log($"OnStartup EXCEPTION: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show($"启动失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static void ApplyTheme(bool dark)
    {
        Log($"ApplyTheme dark={dark}");
        if (_activeTheme != null)
        {
            Current.Resources.MergedDictionaries.Remove(_activeTheme);
            Log("  old dictionary removed");
        }

        var uri = dark
            ? new Uri("pack://application:,,,/Themes/DarkTheme.xaml", UriKind.Absolute)
            : new Uri("pack://application:,,,/Themes/LightTheme.xaml", UriKind.Absolute);
        Log($"  uri = {uri}");

        try
        {
            var dict = new ResourceDictionary { Source = uri };
            Log($"  dict loaded, count={dict.Count}");
            Current.Resources.MergedDictionaries.Add(dict);
            _activeTheme = dict;
            IsDark = dark;
            // 标题栏由系统绘制，需用 DWM 跟随主题（遍历所有已打开窗口）
            foreach (var win in Current.Windows)
                if (win is Window w && w.IsLoaded) TitleBarTheme.Apply(w, dark);
            var test = Current.TryFindResource("BgWindow");
            Log($"  TryFindResource BgWindow = {test}");
        }
        catch (IOException ex)
        {
            Log($"  ApplyTheme FAIL: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
