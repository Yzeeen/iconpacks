using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopIconChanger.Native;

namespace DesktopIconChanger;

/// <summary>
/// 让窗口标题栏颜色整体跟随应用当前主题（浅色 / 深色）。
/// 系统绘制的标题栏默认不随应用主题变：DWM 的沉浸式深色只改文字/按钮明暗，
/// 标题栏背景仍受系统「标题栏和窗口边框」设置残留。因此这里再用
/// DWMWA_CAPTION_COLOR / DWMWA_BORDER_COLOR / DWMWA_TEXT_COLOR
/// 显式指定标题栏背景、边框与文字色，确保整体（含背景）跟随主题。
/// </summary>
public static class TitleBarTheme
{
    /// <summary>标题栏沉浸式深色模式（深色标题栏 + 白字/白按钮）。</summary>
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    /// <summary>窗口边框颜色（Win11 22000+）。</summary>
    private const int DWMWA_BORDER_COLOR = 34;
    /// <summary>标题栏背景颜色（Win11 22000+）。</summary>
    private const int DWMWA_CAPTION_COLOR = 35;
    /// <summary>标题文字颜色（Win11 22000+）。</summary>
    private const int DWMWA_TEXT_COLOR = 36;

    public static void Apply(Window window, bool dark)
    {
        if (window == null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1) 标题栏明暗：深色下文字/按钮变白
        var immersive = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersive, sizeof(int));

        // 2) 标题栏背景色：取自主题资源，深/浅各配对应色，避免残留系统浅色标题栏
        var bg = GetThemeColor("BgWindow", dark
            ? Color.FromRgb(0x20, 0x20, 0x20)
            : Color.FromRgb(0xFA, 0xFA, 0xFA));
        var caption = ToArgb(bg);
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

        // 3) 边框色与主题一致
        var border = GetThemeColor("BorderDefault", bg);
        var borderArgb = ToArgb(border);
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderArgb, sizeof(int));

        // 4) 标题文字颜色：深色白 / 浅色黑
        var text = dark ? 0x00FFFFFF : 0x00000000;
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));

        // 5) 强制重绘非客户区（标题栏）：DWM 属性设置后不会主动刷新标题栏，
        //    不加这一步会等到下一次激活/失焦循环才生效（表现为"刚打开白、切后台才正常"）。
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002,
                   SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                   SWP_FRAMECHANGED = 0x0020;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 让窗口标题栏持续跟随主题。仅在 Loaded 设一次不够：窗口刚创建时 DWM 尚未完成首绘，
    /// 设置的标题栏色会被系统浅色覆盖（表现为刚打开白色、失焦重绘后才正常）。
    /// 这里在 hwnd 就绪后立即+延迟各设一次，并在每次窗口激活时重设，确保首显与切换后都正确。
    /// </summary>
    public static void Attach(Window window)
    {
        if (window == null) return;

        window.SourceInitialized += (_, _) =>
        {
            Apply(window, App.IsDark);
            // 等 DWM 首绘完成后再补一次（空闲优先级），避免被首绘覆盖
            window.Dispatcher.BeginInvoke(new Action(() => Apply(window, App.IsDark)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };

        // 每次激活都重设标题栏颜色，覆盖「刚打开为白、切到后台再回来才正常」的情况
        window.Activated += (_, _) => Apply(window, App.IsDark);
    }

    private static Color GetThemeColor(string key, Color fallback)
        => App.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    /// <summary>DWM 标题栏颜色采用 0xAARRGGBB。</summary>
    private static int ToArgb(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
}
