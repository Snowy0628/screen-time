using System;
using System.Runtime.InteropServices;

namespace ScreenTime.Core;

/// <summary>
/// 手柄空闲检测（XInput）。
///
/// ### 为什么需要
///
/// 原来的判据只看键鼠。用手柄玩游戏时长时间不碰键鼠，明明在玩却会被记成
/// 空闲。全屏游戏靠"窗口占据整屏"侥幸躲过误判，但**窗口化玩游戏就会被记空闲**，
/// 统计直接少一大块。
///
/// ### 实现要点
///
/// XInput 没有"距上次输入多少秒"这种接口，只能轮询 `XInputGetState`，
/// 自己比较摇杆/扳机/按键的变化。所以这里：
///   · 后台线程按固定间隔采样（默认 250ms）
///   · 与上一帧比对，任何一处变化都视为"有输入"，刷新时间戳
///   · 对外只暴露 `IdleSeconds()`，语义与键鼠那路一致
///
/// 采样频率的取舍：250ms 足够捕捉摇杆动作（人手动摇杆不可能在 250ms 内
/// 从静止回到静止），又不会明显占 CPU。XInputGetState 本身极轻量。
///
/// 摇杆死区：手柄静止时摇杆会有轻微漂移，不做死区的话会被当成一直在输入，
/// 于是**永远不判空闲**。这里用 XInput 自带的死区常量（7849/8689）。
///
/// ### 支持范围
///
/// XInput 只覆盖 Xbox 兼容手柄（Xbox 手柄、大部分第三方手柄的 XInput 模式）。
/// 老式 DirectInput 手柄、部分国产手柄的 DInput 模式不在覆盖范围内——
/// 那些会被当作"没有手柄"，退化成原来的键鼠判据，不会更差。
/// </summary>
public sealed class GamepadWatcher : IDisposable
{
    /// <summary>采样间隔（毫秒）。</summary>
    private const int PollIntervalMs = 250;

    // XInput 官方死区常量
    private const short LeftThumbDeadzone = 7849;
    private const short RightThumbDeadzone = 8689;
    private const byte TriggerThreshold = 30;

    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>是否检测到手柄。没有任何手柄时这个判据整体不生效。</summary>
    private volatile bool _connected;

    /// <summary>最后一次检测到输入的时刻（Environment.TickCount64，毫秒）。</summary>
    private long _lastInputMs;

    private XINPUT_STATE _prev;
    private bool _hasPrev;

    public GamepadWatcher()
    {
        _lastInputMs = Environment.TickCount64;
        try
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "GamepadWatcher",
            };
            _thread.Start();
        }
        catch
        {
            _thread = null;
        }
    }

    /// <summary>当前是否有手柄连接。</summary>
    public bool Connected => _connected;

    /// <summary>距最后一次手柄输入的秒数。没有手柄时返回一个很大的值（表示"手柄这条路没有输入"）。</summary>
    public uint IdleSeconds()
    {
        if (!_connected) return uint.MaxValue;
        long delta = Environment.TickCount64 - Interlocked.Read(ref _lastInputMs);
        if (delta < 0) return 0;
        return (uint)(delta / 1000);
    }

    /// <summary>手柄是否已空闲超过给定秒数。没有手柄时恒为 true（不干扰其它判据）。</summary>
    public bool IsIdleFor(int seconds)
    {
        if (!_connected) return true;
        return IdleSeconds() >= (uint)Math.Max(1, seconds);
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Sample();
            }
            catch
            {
                // 单次采样失败不影响后续
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    private void Sample()
    {
        // 只查 0 号手柄。多手柄场景下玩家通常也只用第一个；
        // 遍历 4 个槽位每 250ms 一次的开销没必要。
        var state = new XINPUT_STATE();
        uint res = XInputGetState(0, ref state);

        if (res != 0)   // ERROR_DEVICE_NOT_CONNECTED = 1167
        {
            if (_connected)
            {
                _connected = false;
                _hasPrev = false;
            }
            return;
        }

        if (!_connected)
        {
            _connected = true;
            _hasPrev = false;
            // 刚插上手柄就算一次输入，避免刚连上就被判空闲
            Interlocked.Exchange(ref _lastInputMs, Environment.TickCount64);
        }

        if (_hasPrev && HasInput(_prev.Gamepad, state.Gamepad))
        {
            Interlocked.Exchange(ref _lastInputMs, Environment.TickCount64);
        }

        _prev = state;
        _hasPrev = true;
    }

    /// <summary>比较两帧，判断是否有实际输入（含死区处理）。</summary>
    private static bool HasInput(XINPUT_GAMEPAD a, XINPUT_GAMEPAD b)
    {
        if (a.wButtons != b.wButtons) return true;

        if (Math.Abs(a.bLeftTrigger - b.bLeftTrigger) >= TriggerThreshold) return true;
        if (Math.Abs(a.bRightTrigger - b.bRightTrigger) >= TriggerThreshold) return true;

        // 摇杆要过死区才算——静止时会有轻微漂移，不过死区就永远不判空闲
        if (ExceedsDeadzone(a.sThumbLX, b.sThumbLX, LeftThumbDeadzone)) return true;
        if (ExceedsDeadzone(a.sThumbLY, b.sThumbLY, LeftThumbDeadzone)) return true;
        if (ExceedsDeadzone(a.sThumbRX, b.sThumbRX, RightThumbDeadzone)) return true;
        if (ExceedsDeadzone(a.sThumbRY, b.sThumbRY, RightThumbDeadzone)) return true;

        return false;
    }

    private static bool ExceedsDeadzone(short x, short y, short deadzone)
    {
        int dx = x - y;
        if (dx < 0) dx = -dx;
        return dx > deadzone;
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            _thread?.Join(1000);
        }
        catch
        {
            // 忽略
        }
        _thread = null;
    }

    // ---- XInput 互操作 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    /// <summary>
    /// xinput1_4.dll 是 Windows 8+ 自带的；老系统上是 xinput1_3.dll。
    /// 用两个 DllImport + 运行时选择，避免在个别机器上因为找不到库而整体失败。
    /// </summary>
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState13(uint dwUserIndex, ref XINPUT_STATE pState);

    private static bool _useLegacyDll;

    private static uint XInputGetState(uint index, ref XINPUT_STATE state)
    {
        if (!_useLegacyDll)
        {
            try
            {
                return XInputGetState14(index, ref state);
            }
            catch (DllNotFoundException)
            {
                _useLegacyDll = true;
            }
            catch (EntryPointNotFoundException)
            {
                _useLegacyDll = true;
            }
        }
        try
        {
            return XInputGetState13(index, ref state);
        }
        catch
        {
            // 两个都没有：当作没手柄
            return 1167;
        }
    }
}
