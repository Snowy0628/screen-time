using System;
using System.Collections.Generic;
using System.Diagnostics;
using ScreenTime.Core;
using Windows.Media.Control;

namespace ScreenTime.App;

/// <summary>
/// 媒体播放检测诊断（`--mediacheck`）。
///
/// 媒体判定依赖一个容易出错的映射：SMTC 会话给的 `SourceAppUserModelId`
/// 与前台进程名**不一定相同**。实测到的两种写法：
///
///   PotPlayer → "PotPlayerMini64.exe"   进程名 PotPlayerMini64  （差个 .exe）
///   Edge      → "MSEdge"                进程名 msedge           （大小写 + 前缀）
///
/// 所以匹配规则必须宽松。这个命令把原始字符串、每一步匹配结果和最终判定
/// 都列出来，出问题时能直接看出是"匹配规则不够宽"还是"播放器没上报"。
/// </summary>
internal static class MediaCheck
{
    public static int Run()
    {
        // ---- 1. SMTC 原始会话 ----
        GlobalSystemMediaTransportControlsSessionManager? mgr = null;
        try
        {
            mgr = GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 获取 SMTC 管理器失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("    （Windows 10 1809 以下不支持 SMTC）");
            return 2;
        }

        if (mgr is null)
        {
            Console.WriteLine("  ✗ SMTC 不可用（RequestAsync 返回 null）");
            return 2;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = mgr.GetSessions();
        Console.WriteLine($"── SMTC 会话（共 {sessions.Count} 个）──");
        if (sessions.Count == 0)
        {
            Console.WriteLine("  （当前没有任何程序向系统上报媒体状态）");
        }

        var playing = new List<string>();
        for (int i = 0; i < sessions.Count; i++)
        {
            GlobalSystemMediaTransportControlsSession s = sessions[i];
            string appId = s.SourceAppUserModelId ?? "";

            GlobalSystemMediaTransportControlsSessionPlaybackInfo? pb = s.GetPlaybackInfo();
            string status = pb?.PlaybackStatus.ToString() ?? "?";

            string title = "";
            try
            {
                var info = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                title = info?.Title ?? "";
            }
            catch
            {
                // 取不到标题不影响判断
            }

            Console.WriteLine($"  [{i}] 会话 AppId = \"{appId}\"");
            Console.WriteLine($"      播放状态 = {status}"
                              + (status == "Playing" ? "   ← 计入「正在播放」" : "   （不计入）"));
            if (title.Length > 0) Console.WriteLine($"      标题     = {title}");

            if (status == "Playing") playing.Add(appId);
        }

        // ---- 2. 前台进程 ----
        Console.WriteLine();
        Console.WriteLine("── 前台进程 ──");
        string fgName = MediaWatcher.CurrentForegroundProcessName();
        if (fgName.Length > 0)
        {
            Console.WriteLine($"  进程名 = \"{fgName}\"");
            string path = MediaWatcher.CurrentForegroundProcessPath();
            if (path.Length > 0) Console.WriteLine($"  路径   = {path}");
        }
        else
        {
            Console.WriteLine("  取不到前台进程（无前台窗口或无权读取）");
        }

        // ---- 3. 匹配结果 ----
        Console.WriteLine();
        Console.WriteLine("── 名称匹配（会话 AppId ↔ 前台进程名）──");
        if (playing.Count == 0)
        {
            Console.WriteLine("  没有「正在播放」的会话，无需匹配");
        }
        else
        {
            bool any = false;
            foreach (string appId in playing)
            {
                bool m = MediaWatcher.NamesMatch(appId, fgName);
                Console.WriteLine($"  \"{appId}\" ↔ \"{fgName}\"  → {(m ? "✓ 匹配" : "✗ 不匹配")}");
                if (m) any = true;
            }
            Console.WriteLine();
            Console.WriteLine($"  结论：{(any ? "属于前台进程 → 判定为「在放媒体」，不判空闲"
                                          : "都不属于前台进程 → 按空闲计时")}");
        }

        // ---- 4. 走一遍真正的观察器 ----
        Console.WriteLine();
        Console.WriteLine("── 用真实观察器复核（等待 4 秒采样）──");
        using (var w = new MediaWatcher())
        {
            System.Threading.Thread.Sleep(4000);
            Console.WriteLine("  " + w.Describe());
        }

        return 0;
    }
}
