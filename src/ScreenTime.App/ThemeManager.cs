using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ScreenTime.App;

/// <summary>外观模式。</summary>
internal enum AppTheme
{
    /// <summary>跟随系统（默认）。</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// 主题管理：深浅色的加载、切换，以及"跟随系统"的实时响应。
///
/// 实现方式是把配色令牌放进一个可变位置的 ResourceDictionary，
/// 所有界面元素用 DynamicResource 引用，因此替换字典即可整体换肤。
/// 跟随系统时监听 WM_SETTINGCHANGE 的 ImmersiveColorSet 广播——
/// 用户在 Windows 设置里改主题会立刻生效，无需重启。
/// </summary>
internal static class ThemeManager
{
    private const string LightUri = "Themes/Light.xaml";
    private const string DarkUri = "Themes/Dark.xaml";

    /// <summary>
    /// 当前外观模式。初值从用户设置里读，因此重启后能记住上次的选择。
    /// 之前写死为 System，等于设置面板里的选择重启就丢。
    /// </summary>
    private static AppTheme _mode = AppSettings.Current.Theme;

    private static bool _currentIsDark;
    private static HwndSource? _hook;
    private static Action<AppTheme>? _onChanged;

    /// <summary>
    /// 日志回调。Core 层不依赖 App 的 Log，这里用注入的方式接进来。
    /// 主题切换（尤其是"跟随系统"时 Windows 广播触发的那种）出问题时，
    /// 没有日志就只能靠猜是哪一环断的。
    /// </summary>
    private static Action<string>? _log;

    public static AppTheme Mode => _mode;
    public static bool IsDark => _currentIsDark;

    /// <summary>初始化：应用主题并挂接系统主题变化通知。</summary>
    public static void Initialize(Window hookWindow, Action<AppTheme>? onChanged = null,
                                  Action<string>? log = null)
    {
        _onChanged = onChanged;
        _log = log;

        // 先拿到窗口句柄：标题栏深色化需要它，必须在 Apply 之前
        var helper = new WindowInteropHelper(hookWindow);
        _titleBarHook = helper.Handle;

        _mode = AppSettings.Current.Theme;
        Apply(_mode);

        // 监听系统设置变化（含浅色/深色切换）
        _hook = HwndSource.FromHwnd(helper.Handle);
        _hook?.AddHook(WndProc);
    }

    /// <summary>切换外观模式。选择会被持久化，"跟随系统"时还会立即响应系统变化。</summary>
    public static void SetMode(AppTheme mode)
    {
        _mode = mode;
        Apply(mode);
        _onChanged?.Invoke(mode);

        try
        {
            AppSettings.Current.Theme = mode;
            AppSettings.Current.Save();
        }
        catch
        {
            // 存不了设置不影响本次生效
        }
    }

    private static void Apply(AppTheme mode)
    {
        bool dark = mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };
        _currentIsDark = dark;
        SwapDictionary(dark);

        // 必须在换字典**之后**再套用户配色：
        // 合并新字典会把 AccentBrush / HeroBrush / StateActiveBrush 重置回
        // 主题文件里的默认值，所以每次深浅色切换都要重新覆盖一次，
        // 否则用户选的颜色切一次主题就丢了。
        ThemeCustomizer.Apply(AppSettings.Current);

        // 让 Windows 把标题栏也画成深色。
        // 不做这一步，深色主题下标题栏仍是白的，和整个界面割裂。
        ApplyTitleBarTheme(dark);
    }

    // ---- 标题栏深色化 ----

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;      // Win10 20H1+
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;  // Win10 1809–1909

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
        ref int value, int size);

    /// <summary>
    /// 通知 DWM 用深色绘制标题栏。
    ///
    /// 标题栏是**系统绘制**的，不属于 WPF 视觉树，改不了 Resources 里的画刷，
    /// 只能通过 DwmSetWindowAttribute 告诉系统。
    /// 20 是新版属性号、19 是旧版；两个都试，失败也无妨（只是标题栏保持原样）。
    /// </summary>
    internal static void ApplyTitleBarTheme(bool dark)
    {
        if (_titleBarHook == IntPtr.Zero) return;
        try
        {
            int v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(_titleBarHook, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(_titleBarHook, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));

            // 属性改动后需要让窗口重画一次非客户区
            NativeReload();
        }
        catch
        {
            // 系统不支持则忽略
        }
    }

    private static IntPtr _titleBarHook;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>触发非客户区重绘（SWP_FRAMECHANGED），让标题栏颜色立即生效。</summary>
    private static void NativeReload()
    {
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
                   SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
        try { SetWindowPos(_titleBarHook, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED); }
        catch { }
    }

    /// <summary>读注册表判断系统是深色还是浅色（AppsUseLightTheme: 0=深色）。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把旧的配色字典换成新的，保持位置不变。</summary>
    private static void SwapDictionary(bool dark)
    {
        Application app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;
        ResourceDictionary? old = merged.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.EndsWith(LightUri, StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.EndsWith(DarkUri, StringComparison.OrdinalIgnoreCase)));

        var fresh = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkUri : LightUri, UriKind.Relative),
        };

        if (old is not null)
        {
            int index = merged.IndexOf(old);
            merged.RemoveAt(index);
            merged.Insert(index, fresh);
        }
        else
        {
            merged.Add(fresh);
        }
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && _mode == AppTheme.System)
        {
            string? area = lParam != IntPtr.Zero
                ? System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam)
                : null;

            // "ImmersiveColorSet" 就是浅色/深色切换的广播标识
            if (string.Equals(area, ImmersiveColorSet, StringComparison.Ordinal))
            {
                _log?.Invoke("收到系统深浅色切换广播，重新应用主题");
                Apply(AppTheme.System);
                _onChanged?.Invoke(AppTheme.System);
            }
        }
        return IntPtr.Zero;
    }
}
