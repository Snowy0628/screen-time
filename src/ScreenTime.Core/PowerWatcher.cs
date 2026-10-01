using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenTime.Core;

/// <summary>
/// 锁屏 / 熄屏 / 睡眠 / 合盖 四态检测。
///
/// 数据来源（互为补充，缺一不可）：
///   1. SessionSwitch          —— 锁屏、解锁、远程连接、控制台切换；
///   2. PowerModeChanged       —— 挂起、恢复、电源状态变化；
///   3. RegisterPowerSettingNotification —— 显示器开关、合盖（需要窗口句柄收消息）；
///   4. 墙钟跳变兜底（由 Recorder 负责）—— 现代待机（S0ix）下计时器被节流，
///      上面的通知可能不触发，只能靠"实际间隔远大于 1 秒"来反推睡眠。
/// </summary>
public sealed class PowerWatcher : IDisposable
{
    /// <summary>是否处于锁屏状态。</summary>
    public bool IsLocked { get; private set; }

    /// <summary>显示器是否处于关闭/变暗状态。</summary>
    public bool IsDisplayOff { get; private set; }

    /// <summary>笔记本是否合盖。</summary>
    public bool IsLidClosed { get; private set; }

    /// <summary>系统是否正在挂起/睡眠。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>状态发生变化时触发（用于日志与立即落库）。</summary>
    public event Action<string>? StateChanged;

    private readonly PowerNotificationWindow? _window;
    private bool _disposed;

    public PowerWatcher()
    {
        // 系统事件：锁屏与电源
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // 电源设置通知：需要一个真实窗口句柄来接收 WM_POWERBROADCAST
        _window = new PowerNotificationWindow(this);
    }

    /// <summary>当前是否处于"屏幕不该被算作使用中"的状态。</summary>
    public bool IsScreenOffLike => IsLocked || IsDisplayOff || IsLidClosed || IsSuspended;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                IsLocked = true;
                StateChanged?.Invoke($"锁屏 ({e.Reason})");
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                IsLocked = false;
                StateChanged?.Invoke($"解锁 ({e.Reason})");
                break;

            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.SessionLogon:
                StateChanged?.Invoke($"会话切换 ({e.Reason})");
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                IsSuspended = true;
                StateChanged?.Invoke("系统挂起");
                break;

            case PowerModes.Resume:
                IsSuspended = false;
                StateChanged?.Invoke("系统恢复");
                break;

            case PowerModes.StatusChange:
                // 电源状态变化（插拔电源等），不需要单独记录
                break;
        }
    }

    /// <summary>由电源设置通知窗口回调。</summary>
    internal void OnDisplayStateChanged(bool on)
    {
        if (IsDisplayOff == !on) return;
        IsDisplayOff = !on;
        StateChanged?.Invoke(on ? "显示器开启" : "显示器关闭");
    }

    internal void OnMonitorPowerChanged(bool on)
    {
        if (IsDisplayOff == !on) return;
        IsDisplayOff = !on;
        StateChanged?.Invoke(on ? "显示器通电" : "显示器断电");
    }

    internal void OnLidStateChanged(bool open)
    {
        if (IsLidClosed == !open) return;
        IsLidClosed = !open;
        StateChanged?.Invoke(open ? "开盖" : "合盖");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _window?.Dispose();
    }

    /// <summary>
    /// 仅用于接收 WM_POWERBROADCAST 的消息窗口。
    /// 用 WinForms 的 NativeWindow 而不是自建窗口类，避免手写 WndProc 消息循环。
    /// </summary>
    private sealed class PowerNotificationWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        private readonly PowerWatcher _owner;
        private readonly List<IntPtr> _handles = new();

        public PowerNotificationWindow(PowerWatcher owner)
        {
            _owner = owner;
            // 创建消息窗口
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                Caption = "ScreenTime.PowerNotify",
            });

            Register(NativeMethods.GUID_CONSOLE_DISPLAY_STATE);
            Register(NativeMethods.GUID_MONITOR_POWER_ON);
            Register(NativeMethods.GUID_LIDSWITCH_STATE_CHANGE);
            Register(NativeMethods.GUID_SESSION_DISPLAY_STATUS);
        }

        private void Register(Guid guid)
        {
            Guid g = guid;
            IntPtr h = NativeMethods.RegisterPowerSettingNotification(
                Handle, ref g, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (h != IntPtr.Zero) _handles.Add(h);
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == NativeMethods.WM_POWERBROADCAST)
            {
                int evt = m.WParam.ToInt32();
                switch (evt)
                {
                    case NativeMethods.PBT_APMSUSPEND:
                        _owner.IsSuspended = true;
                        _owner.StateChanged?.Invoke("挂起广播");
                        break;

                    case NativeMethods.PBT_APMRESUMESUSPEND:
                    case NativeMethods.PBT_APMRESUMEAUTOMATIC:
                        _owner.IsSuspended = false;
                        _owner.StateChanged?.Invoke("恢复广播");
                        break;

                    case NativeMethods.PBT_POWERSETTINGCHANGE:
                        HandlePowerSettingChange(m.LParam);
                        break;
                }
                // 返回 1 表示已处理
                m.Result = new IntPtr(1);
                return;
            }

            base.WndProc(ref m);
        }

        private void HandlePowerSettingChange(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero) return;

            var setting = Marshal.PtrToStructure<NativeMethods.POWERBROADCAST_SETTING>(lParam);
            // Data 是变长字段，取第一个字节即为 0/1/2 状态值
            byte value = setting.Data;

            if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
            {
                // 0 = 关闭, 1 = 开启, 2 = 变暗（变暗按"开着"处理）
                _owner.OnDisplayStateChanged(value != 0);
            }
            else if (setting.PowerSetting == NativeMethods.GUID_MONITOR_POWER_ON)
            {
                _owner.OnMonitorPowerChanged(value != 0);
            }
            else if (setting.PowerSetting == NativeMethods.GUID_SESSION_DISPLAY_STATUS)
            {
                _owner.OnDisplayStateChanged(value != 0);
            }
            else if (setting.PowerSetting == NativeMethods.GUID_LIDSWITCH_STATE_CHANGE)
            {
                _owner.OnLidStateChanged(value != 0);
            }
        }

        public void Dispose()
        {
            foreach (IntPtr h in _handles)
            {
                NativeMethods.UnregisterPowerSettingNotification(h);
            }
            _handles.Clear();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
