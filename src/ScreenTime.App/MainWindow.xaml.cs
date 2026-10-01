using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenTime.Core;

// 本项目同时引用 WPF 与 WinForms，同名类型需要固定到 WPF 版本
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using ImageSource = System.Windows.Media.ImageSource;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ScreenTime.App;

/// <summary>
/// 主窗口：单日视图。
/// 数据由 <see cref="DayViewModel"/> 提供，界面每 2 秒刷新一次（数据本身每 60 秒才落库，
/// 更快的刷新只是为了让"实时状态"看起来是活的）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly TrayContext _owner;
    private readonly Log _log;
    private readonly DayViewModel _vm;
    private readonly RangeViewModel _range;
    private readonly DispatcherTimer _timer;

    /// <summary>当前打开的关闭询问对话框（用于程序退出时先关掉它）。</summary>
    private CloseDialog? _closeDialog;

    /// <summary>设置面板初始化期间抑制控件回调，避免把默认值写回配置。</summary>
    private bool _suppressSettingsEvents;

    /// <summary>上次重建时间轴/柱状图的时刻，用于限流（列表重建比 KPI 刷新贵得多）。</summary>
    private DateTime _lastChartBuildUtc;

    /// <summary>上次重建图表时对应的日期，切日期时强制重建。</summary>
    private DateTime _lastChartDay;

    /// <summary>
    /// 关闭询问对话框（如果开着）。从托盘菜单退出时调用：
    /// 否则 ShowDialog 会一直阻塞，窗口无法关闭。
    /// </summary>
    internal void ClosePendingDialog()
    {
        try
        {
            _closeDialog?.Close();
        }
        catch
        {
            // 忽略
        }
    }

    internal MainWindow(TrayContext owner, Log log)
    {
        _owner = owner;
        _log = log;

        InitializeComponent();

        _vm = new DayViewModel(_owner.Store, () => _owner.LiveTotals());
        _range = new RangeViewModel(_owner.Store);
        DataContext = _vm;

        _vm.Refresh();

        // 界面每 1 秒刷新。数据本身每 60 秒才落库，但 KPI 里的秒数取自
        // Recorder 内存中尚未落库的片段（LiveTotals），所以能逐秒递增。
        // 早先用 2 秒间隔，秒数会一次跳两格，看起来不跟手。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Reload();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>
    /// 显示窗口并置前。
    ///
    /// 关键是显示后**强制一次完整重绘**：窗口在隐藏期间数据可能已经变化
    /// （也是被另一个实例唤醒的场景），不做这一步容易出现"内容还在、
    /// 但画面上是黑的/旧的"这类渲染陈旧问题。
    /// </summary>
    internal void ShowAndFocus()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;      // 置前后立即撤销，避免长期压住其他窗口
        Focus();

        // 强制重新布局与重绘，防止显示的是隐藏前的旧画面
        InvalidateVisual();
        UpdateLayout();
        Reload();
    }

    /// <summary>
    /// 关闭窗口的处理。默认询问"最小化到托盘"还是"彻底退出"。
    ///
    /// 为什么不能直接关掉：只有托盘图标的应用一旦关掉窗口就"消失"了，
    /// 用户无法判断程序是否还在记录，也找不到重新打开界面的入口。
    /// 若用户勾选"记住我的选择"，则以后按保存的偏好直接执行，不再打扰。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 程序正在退出（托盘菜单"退出"或界面"退出"按钮）→ 直接放行，不再询问
        if (_owner.IsExiting) return;

        CloseAction action = _owner.WindowCloseAction;

        if (action == CloseAction.Ask)
        {
            var dlg = new CloseDialog(_owner.IsRecording) { Owner = this };
            _closeDialog = dlg;
            try
            {
                dlg.ShowDialog();
            }
            finally
            {
                _closeDialog = null;
            }

            if (dlg.Choice == CloseChoice.Cancel)
            {
                e.Cancel = true;      // 留在界面上
                return;
            }

            action = dlg.Choice == CloseChoice.Exit ? CloseAction.Exit : CloseAction.MinimizeToTray;

            if (dlg.Remember)
            {
                _owner.WindowCloseAction = action;   // 持久化，下次不再问
            }
        }

        if (action == CloseAction.Exit)
        {
            e.Cancel = true;          // 先撤销这次关闭，交给统一的退出流程收尾
            _owner.RequestExit();
        }
        else
        {
            // 最小化到托盘：隐藏而不是关闭，这样托盘双击能立刻唤回
            e.Cancel = true;
            _owner.HideWindow();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.Initialize(this, OnThemeChanged);
        UpdateThemeGlyph();

        // 图表设置要在第一次 Reload 之前推给 ViewModel，
        // 否则首屏会按默认值画、再被下一次刷新纠正（闪一下）
        _vm.ChartStartHour = Math.Clamp(AppSettings.Current.ChartStartHour, 0, 23);
        _vm.TimelineBucketMinutes = AppSettings.Current.TimelineBucketMinutes;

        ApplyRangeMode();
        LoadSettingsIntoUi();
        Reload();
        _timer.Start();

        // 各分区的入场动效：整体淡入 + 轻微上移
        if (AppSettings.Current.EnableAnimations) PlayEntrance();
    }

    // ================= 设置面板 =================

    /// <summary>把已保存的设置填进界面控件（初始化期间抑制回调）。</summary>
    private void LoadSettingsIntoUi()
    {
        _suppressSettingsEvents = true;
        try
        {
            AppSettings s = AppSettings.Current;

            SelectComboByTag(IdleCombo, s.IdleThresholdSeconds.ToString());
            SelectComboByTag(FlushCombo, s.FlushIntervalSeconds.ToString());

            AnimCheck.IsChecked = s.EnableAnimations;
            OpenWindowCheck.IsChecked = s.OpenWindowOnStartup;
            CompareCheck.IsChecked = s.CompareWithPrevious;

            CloseAskRadio.IsChecked = s.CloseAction == CloseAction.Ask;
            CloseTrayRadio.IsChecked = s.CloseAction == CloseAction.MinimizeToTray;
            CloseExitRadio.IsChecked = s.CloseAction == CloseAction.Exit;

            ThemeSystemRadio.IsChecked = s.Theme == AppTheme.System;
            ThemeLightRadio.IsChecked = s.Theme == AppTheme.Light;
            ThemeDarkRadio.IsChecked = s.Theme == AppTheme.Dark;

            FullscreenActiveCheck.IsChecked = s.FullscreenCountsAsActive;

            // 横轴起始时间：0–23 全列出，默认 0 点
            if (StartHourCombo.Items.Count == 0)
            {
                for (int h = 0; h < 24; h++)
                    StartHourCombo.Items.Add(new ComboBoxItem { Content = $"{h:00}:00", Tag = h.ToString() });
            }
            SelectComboByTag(StartHourCombo, s.ChartStartHour.ToString());
            SelectComboByTag(BucketCombo, s.TimelineBucketMinutes.ToString());

            // 自启状态以**系统实际配置**为准，而不是配置里的记录——
            // 用户可能手动删掉计划任务或启动项，那样勾选状态就该跟着变。
            bool autoOn = AutoStart.IsEnabled();
            AutoStartCheck.IsChecked = autoOn;
            AutoStartHint.Text = autoOn
                ? "当前已启用。登录后静默启动，仅托盘图标，不弹窗口。"
                : "勾选后在下次登录时自动启动，静默驻留托盘。";

            DataDirText.Text = "数据目录：" + AppPaths.DataDirectory +
                               (AppPaths.UsedFallback ? "（便携模式，标准位置被拒绝）" : "");

            if (_owner.TrayUnavailable)
            {
                TrayWarnBox.Visibility = Visibility.Visible;
                TrayWarnText.Text = "⚠ 系统拒绝了托盘图标的注册，因此托盘不可用。" +
                                    "关闭窗口时会直接退出程序，以免界面无法找回。";
                CloseTrayRadio.IsEnabled = false;
            }
            else
            {
                TrayWarnBox.Visibility = Visibility.Collapsed;
                CloseTrayRadio.IsEnabled = true;
            }
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                combo.SelectedItem = ci;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>供命令行 --opensettings 使用：启动即展开设置面板（便于截图与自检）。</summary>
    internal void OpenSettingsForDiagnostics() => OpenSettings();

    private void OpenSettings()
    {
        LoadSettingsIntoUi();
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsHint.Text = string.Empty;

        if (!AppSettings.Current.EnableAnimations) return;

        // 面板从下方轻微滑入 + 淡入
        SettingsSlide.Y = 24;
        var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SettingsSlide.BeginAnimation(TranslateTransform.YProperty, slide);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        SettingsCard.BeginAnimation(OpacityProperty, fade);
        SettingsOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnSettingsClose(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.BeginAnimation(OpacityProperty, null);
        SettingsCard.BeginAnimation(OpacityProperty, null);
        SettingsSlide.BeginAnimation(TranslateTransform.YProperty, null);

        // 记录阈值与落库间隔改动后立即生效
        _owner.ApplyRuntimeSettings();
    }

    private void OnSettingsBackdropClick(object sender, MouseButtonEventArgs e) => CloseSettings();

    private void OnIdleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (IdleCombo.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag as string, out int v))
        {
            AppSettings.Current.IdleThresholdSeconds = v;
            AppSettings.Current.Save();
            SettingsHint.Text = "空闲阈值已更新为 " + ci.Content;
        }
    }

    private void OnFlushChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (FlushCombo.SelectedItem is ComboBoxItem ci && int.TryParse(ci.Tag as string, out int v))
        {
            AppSettings.Current.FlushIntervalSeconds = v;
            AppSettings.Current.Save();
            SettingsHint.Text = "写入间隔已更新为 " + ci.Content;
        }
    }

    /// <summary>设置面板里切换外观模式。</summary>
    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (sender is not RadioButton rb) return;
        if (!int.TryParse(rb.Tag as string, out int v)) return;

        var mode = (AppTheme)v;
        ThemeManager.SetMode(mode);          // 立即生效并持久化
        UpdateThemeGlyph();
        SettingsHint.Text = mode switch
        {
            AppTheme.Light => "已切换为浅色外观",
            AppTheme.Dark => "已切换为深色外观",
            _ => "已设为跟随系统（Windows 设置里改深浅色会立即同步）",
        };
    }

    /// <summary>开机自启勾选：勾上就配置（优先计划任务），取消就清理两种方式。</summary>
    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;

        bool want = AutoStartCheck.IsChecked == true;
        string exe = Environment.ProcessPath ?? "";

        if (want)
        {
            if (string.IsNullOrEmpty(exe))
            {
                AutoStartHint.Text = "✗ 无法确定程序路径，设置失败";
                AutoStartCheck.IsChecked = false;
                return;
            }

            (bool ok, string method, string detail) = AutoStart.Enable(exe);
            AppSettings.Current.AutoStart = ok;
            AppSettings.Current.Save();
            if (!ok) AutoStartCheck.IsChecked = false;

            AutoStartHint.Text = ok
                ? $"✓ 已通过「{method}」启用，登录后静默启动（只有托盘图标，不弹窗口）"
                : "✗ " + detail;
            _log.Info($"开机自启：{(ok ? "已启用，方式=" + method : "启用失败：" + detail)}");
        }
        else
        {
            (bool ok, string detail) = AutoStart.Disable();
            AppSettings.Current.AutoStart = false;
            AppSettings.Current.Save();
            AutoStartHint.Text = ok ? "✓ 已关闭开机自启" : "✗ " + detail;
            _log.Info($"开机自启：{(ok ? "已关闭" : "关闭失败：" + detail)}");
        }
    }

    /// <summary>全屏应用是否算作"正在使用"。</summary>
    private void OnFullscreenActiveToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        AppSettings.Current.FullscreenCountsAsActive = FullscreenActiveCheck.IsChecked == true;
        AppSettings.Current.Save();
        _owner.ApplyRuntimeSettings();
        SettingsHint.Text = AppSettings.Current.FullscreenCountsAsActive
            ? "已启用：全屏看视频/玩游戏不会记为「空闲」"
            : "已关闭：只看键鼠输入判定空闲";
    }

    /// <summary>横轴起始时间。</summary>
    private void OnStartHourChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (StartHourCombo.SelectedItem is not ComboBoxItem ci) return;
        if (!int.TryParse(ci.Tag as string, out int v)) return;

        AppSettings.Current.ChartStartHour = v;
        AppSettings.Current.Save();
        ApplyChartSettings();
        SettingsHint.Text = $"横轴起点已改为 {v:00}:00";
    }

    /// <summary>时间轴分格粒度。</summary>
    private void OnBucketChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (BucketCombo.SelectedItem is not ComboBoxItem ci) return;
        if (!int.TryParse(ci.Tag as string, out int v)) return;

        AppSettings.Current.TimelineBucketMinutes = v;
        AppSettings.Current.Save();
        ApplyChartSettings();
        SettingsHint.Text = $"时间轴改为每 {ci.Content}";
    }

    /// <summary>把图表相关设置推给 ViewModel 并立即重画。</summary>
    private void ApplyChartSettings()
    {
        _vm.ChartStartHour = Math.Clamp(AppSettings.Current.ChartStartHour, 0, 23);
        _vm.TimelineBucketMinutes = AppSettings.Current.TimelineBucketMinutes;

        // 强制重建：日期没变时限流逻辑会跳过重建
        _lastChartBuildUtc = default;
        Reload();
    }

    private void OnAnimToggled(object sender, RoutedEventArgs e)    {
        if (_suppressSettingsEvents) return;
        AppSettings.Current.EnableAnimations = AnimCheck.IsChecked == true;
        AppSettings.Current.Save();
        SettingsHint.Text = AppSettings.Current.EnableAnimations ? "已启用界面动效" : "已关闭界面动效";
    }

    private void OnOpenWindowToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        AppSettings.Current.OpenWindowOnStartup = OpenWindowCheck.IsChecked == true;
        AppSettings.Current.Save();
        SettingsHint.Text = "将在下次启动时生效";
    }

    private void OnCompareToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        AppSettings.Current.CompareWithPrevious = CompareCheck.IsChecked == true;
        AppSettings.Current.Save();
        Reload();
        SettingsHint.Text = "已更新排行对比";
    }

    private void OnCloseActionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        if (sender is not RadioButton rb) return;
        if (!int.TryParse(rb.Tag as string, out int v)) return;

        var action = (CloseAction)v;
        _owner.WindowCloseAction = action;
        SettingsHint.Text = action switch
        {
            CloseAction.MinimizeToTray => "关闭窗口后将最小化到托盘继续记录",
            CloseAction.Exit => "关闭窗口后将彻底退出程序",
            _ => "每次关闭窗口都会询问",
        };
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SettingsHint.Text = "打开失败：" + ex.Message;
        }
    }

    // ================= 入场动效 =================

    /// <summary>
    /// 入场动效：内容整体淡入 + 从下方轻微上移。
    ///
    /// 只做一次、只作用于顶层容器，避免在数据刷新路径上堆动画
    /// （每 2 秒重建列表时再跑动画会明显卡顿）。
    /// </summary>
    private void PlayEntrance()
    {
        try
        {
            if (Content is not FrameworkElement root) return;

            root.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            root.BeginAnimation(OpacityProperty, fade);
        }
        catch (Exception ex)
        {
            _log.Warn($"入场动效失败（忽略）：{ex.Message}");
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 左右方向键切换日期/区间，符合看统计数据时的直觉
        if (e.Key == Key.Left) ShiftRange(-1);
        else if (e.Key == Key.Right) ShiftRange(1);
        else if (e.Key == Key.Escape) Hide();
    }

    /// <summary>在单日 / 周 / 月之间切换视图。</summary>
    private void OnRangeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;   // 初始化期间 TabDay 的 IsChecked 会先触发一次
        ApplyRangeMode();
        Reload();
    }

    private void ApplyRangeMode()
    {
        RangeMode mode = TabWeek.IsChecked == true ? RangeMode.Week
                       : TabMonth.IsChecked == true ? RangeMode.Month
                       : RangeMode.Day;

        SectionDay.Visibility = mode == RangeMode.Day ? Visibility.Visible : Visibility.Collapsed;
        SectionWeek.Visibility = mode == RangeMode.Week ? Visibility.Visible : Visibility.Collapsed;
        SectionMonth.Visibility = mode == RangeMode.Month ? Visibility.Visible : Visibility.Collapsed;

        if (mode != RangeMode.Day) _range.SetMode(mode);
    }

    private RangeMode CurrentMode =>
        TabWeek.IsChecked == true ? RangeMode.Week
        : TabMonth.IsChecked == true ? RangeMode.Month
        : RangeMode.Day;

    /// <summary>按当前模式切换日期 / 区间。</summary>
    private void ShiftRange(int direction)
    {
        if (CurrentMode == RangeMode.Day) _vm.ShiftDay(direction);
        else _range.Shift(direction);
        Reload();
    }

    /// <summary>重新计算数据并重建需要动态布局的部分。</summary>
    private void Reload()
    {
        try
        {
            RangeMode mode = CurrentMode;

            // 实时状态栏在所有视图下都显示
            Sample? s = _owner.CurrentSample;
            _vm.SetLive(
                s?.State ?? UsageState.Active,
                s?.App.DisplayName ?? "—",
                _owner.IsPaused);
            LiveText.Text = _vm.LiveStatusText;
            LiveDot.Fill = _vm.LiveStatusBrush;
            SubTitleText.Text = $"前台应用：{_vm.LiveAppText}";
            FooterInfo.Text = _owner.FooterInfo();

            if (mode == RangeMode.Day)
            {
                // 重建时间轴与 24 根柱子不便宜（含图标取色），而数据本身
                // 每 60 秒才落库一次，所以按较慢的节奏重建；
                // KPI 里的秒数走内存中的实时值，仍然每秒更新。
                bool needCharts = _lastChartBuildUtc == default
                                  || (DateTime.UtcNow - _lastChartBuildUtc).TotalSeconds >= 5
                                  || _lastChartDay != _vm.SelectedDate;

                _vm.Refresh(needCharts);
                ApplySummary();

                if (needCharts)
                {
                    BuildTimeline();
                    BuildHourChart();
                    _lastChartBuildUtc = DateTime.UtcNow;
                    _lastChartDay = _vm.SelectedDate;
                }

                EmptyRankHint.Visibility = _vm.Ranking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AttributionWarn.Visibility = _vm.ShowAttributionWarn ? Visibility.Visible : Visibility.Collapsed;
                AttributionWarnText.Text = _vm.AttributionWarnText;
                NextDayButton.IsEnabled = !_vm.IsToday;
                DayHintText.Text = _vm.IsToday ? "正在记录中 · 数据每 60 秒落库一次" : "历史数据 · 已归档";
            }
            else
            {
                _range.Refresh();
                if (mode == RangeMode.Week) ApplyWeek();
                else ApplyMonth();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"界面刷新失败: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 周视图
    // ------------------------------------------------------------------
    private void ApplyWeek()
    {
        WeekRangeLabel.Text = _range.RangeTitle;
        WeekSummary.Text = _range.SummaryText;
        WeekCompareText.Text = _range.ComparisonText;
        WeekCompareText.Foreground = _range.ComparisonBrush;
        WeekRanking.ItemsSource = _range.Ranking;
        NextWeekButton.IsEnabled = !IsCurrentWeek();
        BuildWeekChart();
    }

    private bool IsCurrentWeek()
    {
        DateTime today = DateTime.Today;
        int diff = ((int)today.DayOfWeek + 6) % 7;
        DateTime curMonday = today.AddDays(-diff);
        int otherDiff = ((int)_range.Anchor.DayOfWeek + 6) % 7;
        return _range.Anchor.Date.AddDays(-otherDiff) >= curMonday;
    }

    /// <summary>
    /// 周柱状图：每根柱子按当天"屏幕前时长"占本周最大值的比例定高。
    /// 与分时柱状图一致，直接构建子元素——用 ItemsControl 需要在容器生成后
    /// 再回填内容，容易和绑定互相干扰。
    /// </summary>
    private void BuildWeekChart()
    {
        WeekChart.Children.Clear();

        foreach (DayBarVm bar in _range.DayBars)
        {
            var col = new Grid { Margin = new Thickness(6, 0, 6, 0), ToolTip = bar.Tooltip };
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 数值
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 星期

            var amount = new TextBlock
            {
                Text = bar.AmountText,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = FindResource("Text3Brush") as Brush ?? Brushes.Gray,
            };
            Grid.SetRow(amount, 0);
            col.Children.Add(amount);

            // 固定高度的轨道，柱身在底部按比例生长
            var track = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Height = 150 };
            var stem = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = Math.Max(3, bar.HeightRatio * 150),
                CornerRadius = new CornerRadius(7, 7, 3, 3),
                Background = FindResource("AccentBrush") as Brush ?? Brushes.SteelBlue,
            };

            if (bar.IsToday)
            {
                // 今天用外发光高亮，和原型里的描边效果等价但更清晰
                stem.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.9,
                };
            }

            track.Children.Add(stem);
            Grid.SetRow(track, 1);
            col.Children.Add(track);

            var cap = new TextBlock
            {
                Text = bar.IsToday ? bar.Caption + " · 今天" : bar.Caption,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = FindResource("TextBrush") as Brush ?? Brushes.Black,
            };
            Grid.SetRow(cap, 2);
            col.Children.Add(cap);

            WeekChart.Children.Add(col);
        }
    }

    // ------------------------------------------------------------------
    // 月视图
    // ------------------------------------------------------------------
    private void ApplyMonth()
    {
        MonthRangeLabel.Text = _range.RangeTitle;
        MonthSummary.Text = _range.SummaryText;
        MonthRanking.ItemsSource = _range.Ranking;
        NextMonthButton.IsEnabled = _range.Anchor.Year < DateTime.Today.Year
            || (_range.Anchor.Year == DateTime.Today.Year && _range.Anchor.Month < DateTime.Today.Month);
        BuildHeatGrid();
    }

    /// <summary>日历热力图：单元格颜色按当天屏幕前时长占当月最大值的比例决定。</summary>
    private void BuildHeatGrid()
    {
        HeatGrid.ItemsSource = _range.HeatCells;
        HeatGrid.ItemTemplate = new DataTemplate
        {
            VisualTree = BuildHeatCellFactory(),
        };

        // 图例三档
        Color accent = (FindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        HeatLegendMin.Background = new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B));
        HeatLegendMid.Background = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B));
        HeatLegendMax.Background = new SolidColorBrush(accent);
    }

    private FrameworkElementFactory BuildHeatCellFactory()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.MarginProperty, new Thickness(3));
        border.SetValue(Border.MinHeightProperty, 46.0);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.Fill)));
        border.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.Tooltip)));

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var day = new FrameworkElementFactory(typeof(TextBlock));
        day.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.DayText)));
        day.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.TextBrush)));
        day.SetValue(TextBlock.FontSizeProperty, 12.5);
        day.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        day.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var hour = new FrameworkElementFactory(typeof(TextBlock));
        hour.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.HourText)));
        hour.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(HeatCellVm.TextBrush)));
        hour.SetValue(TextBlock.FontSizeProperty, 10.0);
        hour.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        panel.AppendChild(day);
        panel.AppendChild(hour);
        border.AppendChild(panel);
        return border;
    }

    private void OnPrevRange(object sender, RoutedEventArgs e) { _range.Shift(-1); Reload(); }
    private void OnNextRange(object sender, RoutedEventArgs e) { _range.Shift(1); Reload(); }
    private void OnCurrentRange(object sender, RoutedEventArgs e) { _range.GoCurrent(); Reload(); }

    private void ApplySummary()
    {
        DateLabel.Text = _vm.DateLabel;

        LiveText.Text = _vm.LiveStatusText;
        LiveDot.Fill = _vm.LiveStatusBrush;
        SubTitleText.Text = $"前台应用：{_vm.LiveAppText}";

        // 主卡数字拆成"数字 + 单位"两段样式
        KpiTotal.Inlines.Clear();
        AppendValueWithUnit(KpiTotal, _vm.TotalScreenText, heroStyle: true);

        DeltaText.Text = _vm.DeltaVsYesterdayText;
        DeltaBadge.Visibility = _vm.HasDelta ? Visibility.Visible : Visibility.Collapsed;

        KpiActive.Inlines.Clear();
        AppendValueWithUnit(KpiActive, _vm.ActiveText, heroStyle: false);
        KpiActiveShare.Text = _vm.ActiveShareText;

        KpiOff.Inlines.Clear();
        AppendValueWithUnit(KpiOff, _vm.OffText, heroStyle: false);

        KpiTop.Text = _vm.TopAppText;
        KpiTopFoot.Text = _vm.TopAppFootText;
    }

    /// <summary>把 "8 小时 42 分" 渲染成数字大、单位小的样式。</summary>
    private static void AppendValueWithUnit(TextBlock target, string text, bool heroStyle)
    {
        // text 形如 "8 小时 42 分" 或 "42 分 10 秒" 或 "10 秒"
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool isUnit = parts[i] is "小时" or "分" or "秒";
            var run = new System.Windows.Documents.Run(parts[i] + (isUnit ? "" : " "));
            if (isUnit)
            {
                run.FontSize = 13;
                run.Foreground = target.FindResource(heroStyle ? "HeroTextDimBrush" : "Text3Brush") as Brush
                                 ?? Brushes.Gray;
            }
            target.Inlines.Add(run);
        }
    }

    // ------------------------------------------------------------------
    // 时间轴：24 小时色块，列宽按持续时长比例分配
    // ------------------------------------------------------------------
    private void BuildTimeline()
    {
        TimelineGrid.Children.Clear();
        TimelineGrid.ColumnDefinitions.Clear();

        int col = 0;
        foreach (SegmentVm seg in _vm.Timeline)
        {
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(seg.Seconds, GridUnitType.Star),
            });

            // 每段一个独立容器：内含底色 + 悬停提亮层。
            // 提亮用白色覆盖层的透明度动画，而不是改 Brush 的明度
            // （SolidColorBrush 是共享/冻结资源，就地改颜色会污染其他位置）。
            var host = new Grid { Background = seg.Fill, ToolTip = seg.Tooltip };
            var veil = new Border { Background = Brushes.White, Opacity = 0, IsHitTestVisible = false };
            host.Children.Add(veil);

            host.MouseEnter += (_, _) => AnimateOpacity(veil, 0.16);
            host.MouseLeave += (_, _) => AnimateOpacity(veil, 0);
            host.ToolTipOpening += OnBarTooltipOpening;   // 提示框淡入

            Grid.SetColumn(host, col);
            TimelineGrid.Children.Add(host);
            col++;
        }

        BuildTimelineScale();
        BuildTimelineGridLines();
    }

    /// <summary>
    /// 时间轴上的整点分隔线（每 3 小时一条）。
    /// 用 25 等分的网格而不是按段定位：段宽是"时长比例"，
    /// 直接算像素位置会随窗口宽度变化而失准。
    /// </summary>
    private void BuildTimelineGridLines()
    {
        TimelineGridLines.Children.Clear();
        TimelineGridLines.ColumnDefinitions.Clear();

        // 与刻度对齐：每 3 小时一条分隔线，首尾不画
        int startHour = _vm.ChartStartHour;

        for (int i = 0; i <= 8; i++)
        {
            TimelineGridLines.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(3, GridUnitType.Star),
            });

            if (i is 0 or 8) continue;

            // 跨过午夜的那条线画得更明显，提示"这里进入次日"
            bool isMidnight = (startHour + i * 3) % 24 == 0;

            var line = new Border
            {
                Width = isMidnight ? 2 : 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = FindResource("SurfaceBrush") as Brush ?? Brushes.White,
                Opacity = isMidnight ? 0.9 : 0.55,
            };
            Grid.SetColumn(line, i);
            TimelineGridLines.Children.Add(line);
        }
    }

    /// <summary>把元素的不透明度动画到目标值。动效关闭时直接赋值。</summary>
    private void AnimateOpacity(UIElement element, double to)
    {
        if (element is not Border b) return;
        if (!AppSettings.Current.EnableAnimations)
        {
            b.Opacity = to;
            return;
        }

        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        b.BeginAnimation(OpacityProperty, anim);
    }

    // ---- 柱状图的悬停动效 ----

    /// <summary>鼠标进入某根柱子：提亮该柱。</summary>
    private void OnHourBarEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (FindChild<Border>(fe, "HoverVeil") is { } veil) AnimateOpacity(veil, 0.18);
    }

    private void OnHourBarLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (FindChild<Border>(fe, "HoverVeil") is { } veil) AnimateOpacity(veil, 0);
    }

    /// <summary>
    /// 提示框弹出时先透明再淡入，对齐原型的 #tip 过渡。
    /// 系统默认是瞬间出现，观感比较生硬。
    /// </summary>
    private void OnBarTooltipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ToolTip is not ToolTip tip) return;
        if (!AppSettings.Current.EnableAnimations) return;
        tip.Opacity = 0;
        tip.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>在视觉树里按名字找子元素（模板展开后无法用字段引用）。</summary>
    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && t.Name == name) return t;
            if (FindChild<T>(child, name) is { } deep) return deep;
        }
        return null;
    }

    /// <summary>时间轴上方的小时刻度。</summary>
    private void BuildTimelineScale()
    {
        TimelineScale.Children.Clear();
        TimelineScale.ColumnDefinitions.Clear();

        // 刻度跟随显示窗口的起始小时：起始 0 点时显示 00:00 … 21:00 … 24:00
        int startHour = _vm.ChartStartHour;

        for (int i = 0; i <= 8; i++)
        {
            TimelineScale.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(3, GridUnitType.Star),
            });

            string text;
            if (i == 8)
            {
                // 收尾刻度：起始 0 点时终点其实也是 0 点，
                // 直接写会变成"左边 00:00、右边又 00:00"。
                // 按习惯写成 24:00（这一天到 24 点结束），与柱状图保持一致。
                text = startHour == 0 ? "24:00" : $"{startHour:00}:00";
            }
            else
            {
                text = $"{((startHour + i * 3) % 24):00}:00";
            }

            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = FindResource("Text3Brush") as Brush ?? Brushes.Gray,
                HorizontalAlignment = i switch
                {
                    0 => HorizontalAlignment.Left,
                    8 => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Center,
                },
            };
            Grid.SetColumn(tb, i);
            TimelineScale.Children.Add(tb);
        }
    }

    // ------------------------------------------------------------------
    // 分时柱状图：每根柱子 = 1 小时，柱内按占比堆叠，柱顶挂 Top3 图标
    //
    // 柱身完全用代码构建：每个色块占一行，行高用"剩余空间"的星号比例表示，
    // 这样在固定高度的容器里 WPF 会自动把各段按秒数比例分配像素，
    // 不需要手算高度（手算会在布局未完成时得到 0）。
    // ------------------------------------------------------------------
    private const double BarAreaHeight = 150;

    private void BuildHourChart()
    {
        HourChart.Children.Clear();
        HourChart.ColumnDefinitions.Clear();

        // 24 根柱子等分宽度，末尾再多一列放收尾刻度（右对齐的下一小时钟点）
        for (int i = 0; i < 24; i++)
            HourChart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        HourChart.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int col = 0;
        foreach (HourBarVm bar in _vm.Hours)
        {
            var colGrid = new Grid { Margin = new Thickness(1.5, 0, 1.5, 0) };
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 顶部图标
            colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 小时标签
            colGrid.ToolTip = bar.Tooltip;

            double activeFraction = Math.Clamp(bar.BarHeight / BarAreaHeight, 0, 1);

            // 柱身按占满一格的比例定高。设一个下限，避免只有几十秒记录时
            // 柱子被 0.5px 的分隔线吃掉、在界面上完全看不见。
            double barPixels = activeFraction * BarAreaHeight;
            if (barPixels < 3) barPixels = 3;

            if (activeFraction > 0.0005)
            {
                var barArea = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Height = BarAreaHeight };

                var body = new Grid
                {
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = barPixels,
                    ClipToBounds = true,
                };

                // 柱顶 Top3 图标（叠在柱身上方，负边距让它们紧凑排列）
                if (bar.TopIcons.Count > 0)
                {
                    var iconRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, -18, 0, 0),
                    };
                    foreach (ImageSource icon in bar.TopIcons)
                    {
                        var holder = new Grid { Margin = new Thickness(-4, 0, 0, 0) };
                        holder.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            Background = FindResource("SurfaceBrush") as Brush ?? Brushes.White,
                            Width = 17,
                            Height = 17,
                        });
                        holder.Children.Add(new Image
                        {
                            Source = icon,
                            Width = 15,
                            Height = 15,
                            Stretch = Stretch.Uniform,
                        });
                        // 图标是矢量化的位图，开启高质量缩放避免在小尺寸下糊
                        RenderOptions.SetBitmapScalingMode(holder.Children[^1] as Image, BitmapScalingMode.HighQuality);
                        iconRow.Children.Add(holder);
                    }
                    body.Children.Add(iconRow);
                }

                // 色块按秒数比例分配高度
                var sliceGrid = new Grid { ClipToBounds = true };
                double totalSeconds = 0;
                foreach (SliceVm s in bar.Slices) totalSeconds += s.Seconds;
                if (totalSeconds <= 0) totalSeconds = 1;

                foreach (SliceVm s in bar.Slices)
                {
                    sliceGrid.RowDefinitions.Add(new RowDefinition
                    {
                        Height = new GridLength(s.Seconds, GridUnitType.Star),
                    });
                }

                int row = 0;
                foreach (SliceVm s in bar.Slices)
                {
                    var slice = new Border
                    {
                        Background = s.Fill,
                        ToolTip = s.Tooltip,
                        Margin = new Thickness(0, 0, 0, row == bar.Slices.Count - 1 ? 0 : 0.5),
                    };
                    Grid.SetRow(slice, row);
                    sliceGrid.Children.Add(slice);
                    row++;
                }

                body.Children.Add(sliceGrid);
                barArea.Children.Add(body);
                Grid.SetRow(barArea, 1);
                colGrid.Children.Add(barArea);
            }

            var label = new TextBlock
            {
                Text = bar.HourLabel,
                FontSize = 10,
                Foreground = FindResource("Text3Brush") as Brush ?? Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
            };
            Grid.SetRow(label, 2);
            colGrid.Children.Add(label);

            Grid.SetColumn(colGrid, col);
            HourChart.Children.Add(colGrid);
            col++;
        }

        // 收尾刻度：标出"这一天到此结束"。
        //
        // 起始 0 点时窗口是 [今天00:00 → 明天00:00]，终点其实也是 0 点，
        // 于是"左边 0 点、右边又 0 点"——看着像首尾重复。
        // 按用户习惯应写成 **24:00**：一天到 24 点结束，
        // 第 24 根柱子是 23:00–24:00，24:00 本身只是刻度、没有柱子。
        // 起始点非 0 点时（滚动窗口）仍显示真实的收尾钟点。
        string endText = _vm.ChartStartHour == 0 ? "24:00" : $"{_vm.ChartStartHour:00}:00";
        var endLabel = new TextBlock
        {
            Text = endText,
            FontSize = 10,
            Foreground = FindResource("Text3Brush") as Brush ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = _vm.ChartStartHour == 0
                ? "一天的结束刻度（24:00）。最后一根柱子是 23:00–24:00，24:00 之后不再有内容。"
                : $"时间轴收尾于 {endText}（与左侧起点相隔 24 小时）",
        };
        Grid.SetColumn(endLabel, col);
        HourChart.Children.Add(endLabel);
    }

    // ------------------------------------------------------------------
    // 交互
    // ------------------------------------------------------------------
    private void OnPrevDay(object sender, RoutedEventArgs e) { _vm.ShiftDay(-1); Reload(); }
    private void OnNextDay(object sender, RoutedEventArgs e) { _vm.ShiftDay(1); Reload(); }
    private void OnToday(object sender, RoutedEventArgs e) { _vm.GoToday(); Reload(); }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        // 循环：跟随系统 → 浅色 → 深色 → 跟随系统
        AppTheme next = ThemeManager.Mode switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System,
        };
        ThemeManager.SetMode(next);
        UpdateThemeGlyph();
    }

    private void OnThemeChanged(AppTheme mode)
    {
        UpdateThemeGlyph();
        // 主题变了，依赖取色的柱子需要重画
        Reload();
    }

    private void UpdateThemeGlyph()
    {
        ThemeGlyph.Text = ThemeManager.Mode switch
        {
            AppTheme.Light => "\uE706",   // 太阳
            AppTheme.Dark => "\uE708",    // 月亮
            _ => "\uE793",                // 跟随系统
        };
    }
}
