using System.IO;
using System.Windows;
using System.Windows.Threading;
using PixivRenamer.Core;

namespace PixivRenamer;

public partial class App : Application
{
    /// <summary>本进程是否以管理员身份运行（界面用于显示状态）。</summary>
    public static bool IsRunningElevated { get; private set; }

    /// <summary>用户在 UAC 对话框里拒绝了提权（界面用于提示）。</summary>
    public static bool ElevationDeclined { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LoggerFallback("未处理异常：" + args.ExceptionObject);

        var args = e.Args;
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleHelper.Attach();
            var result = SelfTest.RunAll();
            var text = result.ToString();
            try { File.WriteAllText(Path.Combine(AppConfig.ExeDirectory(), "selftest.log"), text, new System.Text.UTF8Encoding(true)); } catch { }
            Console.WriteLine(text);
            Console.Out.Flush();
            Environment.Exit(result.Fail == 0 ? 0 : 1);
            return;
        }

        if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleHelper.Attach();
            // CLI 在线程池运行，避免 WPF Dispatcher 同步上下文造成死锁/进程不退出
            var code = Task.Run(() => CliRunner.RunAsync(args)).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleHelper.Attach();
            Console.WriteLine($"{AppInfo.ProductName} {AppConfig.VersionText}");
            Console.WriteLine($"项目主页：{AppInfo.RepositoryUrl}");
            Console.WriteLine($"作者：{AppInfo.Author}　许可：{AppInfo.License}");
            Console.WriteLine($"exe：{Elevation.ExecutablePath()}");
            Console.WriteLine($"权限：{(Elevation.IsElevated ? "管理员" : "普通用户")}");
            Environment.Exit(0);
            return;
        }

        if (args.Any(a => a.Equals("--check-update", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleHelper.Attach();
            var cfgForUpdate = AppConfig.Load(Path.Combine(AppConfig.ExeDirectory(), "PixivRenamer.json"));
            cfgForUpdate.Finalize(AppConfig.ExeDirectory());
            var resolution = ProxyResolver.Resolve(cfgForUpdate, null);
            cfgForUpdate.Proxy = resolution.Chosen;
            cfgForUpdate.UseSystemProxy = resolution.Chosen.Length == 0 && cfgForUpdate.ProxyMode != "direct";
            var update = Task.Run(() => new UpdateService().CheckAsync(cfgForUpdate)).GetAwaiter().GetResult();
            Console.WriteLine(update.Describe());
            if (update.Ok && update.HasUpdate)
            {
                Console.WriteLine($"发布页：{update.ReleaseUrl}");
                if (!string.IsNullOrWhiteSpace(update.DownloadUrl)) Console.WriteLine($"下载：{update.DownloadUrl}");
                if (!string.IsNullOrWhiteSpace(update.Notes)) Console.WriteLine("\n更新说明：\n" + update.Notes);
            }
            else if (!update.Ok)
            {
                Console.WriteLine("（需要能访问 github.com；如果用了代理，可先跑 --test-net）");
            }
            Environment.Exit(update.Ok && !update.HasUpdate ? 0 : 1);
            return;
        }

        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleHelper.Attach();
            Console.WriteLine("PixivRenamer " + AppConfig.VersionText);
            Console.WriteLine("用法：");
            Console.WriteLine("  PixivRenamer.exe                         启动图形界面（默认自动以管理员身份启动）");
            Console.WriteLine("  PixivRenamer.exe --selftest              运行内置自检（写入 selftest.log）");
            Console.WriteLine("  PixivRenamer.exe --cli <目录> [--dry-run] [--no-reverse] [--report 报告.csv] [--yes]");
            Console.WriteLine("  PixivRenamer.exe --cli --undo <报告.csv> [--dry-run]");
            Console.WriteLine("     识图缓冲：--reverse-concurrency N / --no-reverse-queue");
            Console.WriteLine("     未识别归类：--unresolved-folder 目录名 / --no-unresolved-folder");
            Console.WriteLine("  PixivRenamer.exe --uitest [页码0-2] [--uitest-exit 秒]  界面自检：打开指定页面（可自动退出）");
            Console.WriteLine("  PixivRenamer.exe --no-elevate            本次不自动提权（界面模式）");
            Console.WriteLine("  PixivRenamer.exe --test-net [--proxy 127.0.0.1:7897] [--proxy-mode auto|explicit|system|direct]");
            Console.WriteLine("                                           只做网络/代理诊断，报告写入 error-reports");
            Console.WriteLine("  PixivRenamer.exe --version               显示版本与运行环境");
            Console.WriteLine("  PixivRenamer.exe --check-update          检查 GitHub 上是否有新版本");
            Console.WriteLine($"  项目主页：{AppInfo.RepositoryUrl}");
            Console.Out.Flush();
            Environment.Exit(0);
            return;
        }

        // 权限：默认以管理员身份启动（要与「以管理员身份运行」的浏览器交互）。
        // 早于窗口创建提权，避免刚建好窗口又重启；--cli/--selftest 等自动化用法不提权。
        // 用户在 UAC 里点「否」时，本次请求被取消 → 继续以普通权限运行，并记下来在界面上提示。
        AppConfig? earlyConfig = null;
        try { earlyConfig = AppConfig.Load(Path.Combine(AppConfig.ExeDirectory(), "PixivRenamer.json")); }
        catch { /* 配置损坏时按默认值处理 */ }
        var noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        var wantAdmin = earlyConfig?.RunAsAdmin ?? true;
        if (wantAdmin && !noElevate && !Elevation.IsElevated && !Elevation.HasRelaunchMarker(args))
        {
            if (Elevation.TryRelaunchElevated())
            {
                Shutdown();
                return;
            }
            ElevationDeclined = true;
        }
        IsRunningElevated = Elevation.IsElevated;

        // 界面自检：打开窗口并停留在指定页面，便于自动化截图核对布局是否错位。
        // 用法：--uitest [页面序号 0-2]，另可加 --uitest-exit <秒> 自动退出。
        var uiTestArg = args.FirstOrDefault(a => a.StartsWith("--uitest", StringComparison.OrdinalIgnoreCase));
        if (uiTestArg != null)
        {
            var page = 0;
            var eq = uiTestArg.IndexOf('=');
            if (eq > 0 && int.TryParse(uiTestArg[(eq + 1)..], out var p)) page = Math.Clamp(p, 0, 2);
            else
            {
                var idx = Array.FindIndex(args, a => a.Equals(uiTestArg, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var p2)) page = Math.Clamp(p2, 0, 2);
            }

            var testWindow = new MainWindow();
            MainWindow = testWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            testWindow.Show();
            testWindow.SelectPage(page);

            var exitIdx = Array.FindIndex(args, a => a.Equals("--uitest-exit", StringComparison.OrdinalIgnoreCase));
            if (exitIdx >= 0 && exitIdx + 1 < args.Length && double.TryParse(args[exitIdx + 1], out var seconds) && seconds > 0)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) => { timer.Stop(); testWindow.Close(); };
                timer.Start();
            }
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LoggerFallback("界面线程异常：" + e.Exception);
        MessageBox.Show("发生未处理异常：\n" + e.Exception.Message, "PixivRenamer",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LoggerFallback(string message)
    {
        try
        {
            var dir = Path.Combine(AppConfig.ExeDirectory(), "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "PixivRenamer.error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new System.Text.UTF8Encoding(true));
        }
        catch { }
    }
}
