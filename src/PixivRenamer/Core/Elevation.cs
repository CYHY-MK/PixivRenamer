using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace PixivRenamer.Core;

/// <summary>
/// 管理员权限（提权）支持。
///
/// 为什么要提权：本程序会打开/配合浏览器使用（打开 pixiv 登录页、读取浏览器 Cookie、
/// 与浏览器进程交互）。当浏览器是「以管理员身份运行」的（高完整性级别）时，
/// 普通权限的 PixivRenamer 无法与之交互，所以默认以管理员身份启动。
///
/// 实现方式：运行期自提权（ShellExecute + runas），而不是给 exe 打 requireAdministrator 清单，
/// 这样 --cli / --selftest 这类自动化用法不会被 UAC 卡住，便携版换到任何目录也都有效。
/// </summary>
public static class Elevation
{
    /// <summary>提权重启时附加的标记参数，用来防止提权失败后无限重启。</summary>
    public const string RelaunchMarker = "--elevated-launch";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>当前进程的启动命令行（自提权重启时原样传递）。</summary>
    public static string CommandLine() => Environment.CommandLine;

    /// <summary>当前进程是否由本次自提权拉起（命令行里带标记）。</summary>
    public static bool HasRelaunchMarker(string[] args)
        => args.Any(a => a.Equals(RelaunchMarker, StringComparison.OrdinalIgnoreCase));

    /// <summary>剔除标记参数，避免把它传给其它解析逻辑。</summary>
    public static string[] Sanitize(string[] args)
        => args.Where(a => !a.Equals(RelaunchMarker, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// 以管理员身份重新启动自己。成功返回 true（调用方应立即退出当前实例）。
    /// 用户取消 UAC 时返回 false，调用方继续以普通权限运行。
    /// </summary>
    public static bool TryRelaunchElevated()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath(),
                Arguments = CommandLine() + " " + RelaunchMarker,
                UseShellExecute = true,          // 触发 UAC 必须用 ShellExecute
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };
            var proc = Process.Start(psi);
            return proc != null;
        }
        catch
        {
            // 用户在 UAC 对话框点了「否」，或系统策略禁止提权
            return false;
        }
    }

    /// <summary>
    /// 真正的 exe 路径：单文件发布时 AppContext.BaseDirectory 指向 %TEMP% 解包目录，
    /// 必须用进程主模块路径才能拿到用户实际双击的那个 exe。
    /// </summary>
    public static string ExecutablePath()
    {
        try
        {
            var main = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(main)) return main!;
        }
        catch { /* 回退到进程路径 */ }
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path!;
        }
        catch { /* 回退到 AppContext */ }
        return Path.Combine(AppContext.BaseDirectory, "PixivRenamer.exe");
    }
}
