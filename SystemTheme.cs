using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopIconChanger;

/// <summary>
/// 系统主题帮助：检测 Windows 是否启用深色模式，并在主题变化时通知
/// </summary>
public static class SystemTheme
{
    /// <summary>true = 系统深色模式，false = 浅色</summary>
    public static bool IsDark => GetAppsUseDarkTheme();

    public static event EventHandler? ThemeChanged;

    private static RegistryMonitor? _monitor;

    public static void StartListening()
    {
        try
        {
            _monitor = new RegistryMonitor(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _monitor.RegistryChanged += (_, _) => ThemeChanged?.Invoke(null, EventArgs.Empty);
            _monitor.Start();
        }
        catch { /* 忽略监听失败，启动时检测一次就够了 */ }
    }

    private static bool GetAppsUseDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var val = key.GetValue("AppsUseLightTheme");
                // 0 = 深色, 1 = 浅色（默认）
                if (val is int i) return i == 0;
            }
        }
        catch { }
        return false;
    }

    /// <summary>轻量注册表变更监视器</summary>
    private class RegistryMonitor : IDisposable
    {
        public event EventHandler? RegistryChanged;

        private readonly string _subKey;
        private IntPtr _eventHandle;
        private Thread? _thread;
        private bool _stop;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr RegNotifyChangeKeyValue(
            IntPtr hKey, bool watchSubtree, uint notifyFilter,
            IntPtr hEvent, bool asynchronous);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyExW(
            IntPtr hKey, string subKey, int reserved, uint samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset,
            bool bInitialState, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        public RegistryMonitor(string subKey) { _subKey = subKey; }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            const uint KEY_NOTIFY_CHANGE_VALUE = 0x00000001;
            const uint KEY_READ = 0x20019;
            const uint INFINITE = 0xFFFFFFFF;

            while (!_stop)
            {
                IntPtr hKey = IntPtr.Zero;
                try
                {
                    int hr = RegOpenKeyExW(HKEY_CURRENT, _subKey, 0, KEY_READ, out hKey);
                    if (hr != 0) { Thread.Sleep(5000); continue; }

                    _eventHandle = CreateEvent(IntPtr.Zero, true, false, null);
                    if (_eventHandle == IntPtr.Zero) { Thread.Sleep(5000); continue; }

                    RegNotifyChangeKeyValue(hKey, true, KEY_NOTIFY_CHANGE_VALUE, _eventHandle, true);
                    WaitForSingleObject(_eventHandle, INFINITE);

                    RegistryChanged?.Invoke(null, EventArgs.Empty);
                }
                finally
                {
                    if (hKey != IntPtr.Zero) RegCloseKey(hKey);
                }
            }
        }

        public void Dispose() { _stop = true; }
        private static readonly IntPtr HKEY_CURRENT = new(0x80000001);
    }
}
