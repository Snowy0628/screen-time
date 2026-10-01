// 升级行为测试：复制 installer 的 CopyTree/IsUserData 逻辑到独立程序里验证。
// 目的是绕开安装程序的交互确认（受限环境下无法应答），
// 单独确认"废弃文件被清掉、用户数据原样保留"这两条升级保证成立。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal static class UpgradeTest
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: UpgradeTest <源目录> <目标目录>");
            return 2;
        }
        string from = args[0], to = args[1];

        Console.WriteLine($"源  : {from}");
        Console.WriteLine($"目标: {to}");
        Console.WriteLine();

        Console.WriteLine("--- 升级前 ---");
        Show(to);

        Console.WriteLine("--- 执行 CopyTree ---");
        CopyTree(from, to);

        Console.WriteLine();
        Console.WriteLine("--- 升级后 ---");
        Show(to);

        // 断言
        int fail = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + what);
            if (!ok) fail++;
        }

        Console.WriteLine();
        Console.WriteLine("--- 断言 ---");
        Check(File.Exists(Path.Combine(to, "ScreenTime.App.exe")), "程序本体已就位");
        Check(File.Exists(Path.Combine(to, "e_sqlite3.dll")), "原生库已就位");
        Check(!File.Exists(Path.Combine(to, "OldRemovedFeature.dll")), "废弃 DLL 已清理");
        Check(!File.Exists(Path.Combine(to, "legacy.config")), "废弃配置已清理");
        Check(File.Exists(Path.Combine(to, "data", "settings.json")), "用户设置未被删除");
        Check(File.Exists(Path.Combine(to, "data", "usage.db")), "用户数据库未被删除");

        string settings = File.ReadAllText(Path.Combine(to, "data", "settings.json"));
        Check(settings.Contains("IdleThresholdSeconds"), "用户设置内容未被改写");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "升级行为测试通过" : $"升级行为测试失败（{fail} 项）");
        return fail == 0 ? 0 : 1;
    }

    private static void Show(string dir)
    {
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(dir.Length).TrimStart('\\');
            Console.WriteLine("  " + rel);
        }
    }

    // ===== 与 Installer.cs 保持一致的两段逻辑 =====

    private static bool IsUserData(string relativePath)
    {
        string rel = relativePath.TrimStart('\\');
        return rel.Equals("data", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(from.Length).TrimStart('\\');
            if (IsUserData(rel)) continue;
            wanted.Add(rel);
        }

        int removed = 0;
        foreach (string f in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(to.Length).TrimStart('\\');
            if (IsUserData(rel)) continue;
            if (wanted.Contains(rel)) continue;
            try { File.Delete(f); removed++; } catch { }
        }
        if (removed > 0) Console.WriteLine($"  清理旧版本遗留文件 {removed} 个");

        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));

        int copied = 0;
        foreach (string rel in wanted)
        {
            File.Copy(Path.Combine(from, rel), Path.Combine(to, rel), true);
            copied++;
        }
        Console.WriteLine($"  已复制 {copied} 个文件（覆盖升级）");
    }
}
