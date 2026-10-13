using System.IO;
namespace PixivRenamer.Core;

public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var baseDir = AppConfig.ExeDirectory();
        string? configPath = null;
        string? source = null;
        string? report = null;
        string? undo = null;
        bool dryRun = false;
        bool noReverse = false;
        bool yes = false;
        bool verbose = false;
        int limit = 0;
        string? sauceKey = null;
        int reverseConcurrency = 0;
        bool noReverseQueue = false;
        string? unresolvedFolder = null;
        bool noUnresolvedFolder = false;
        int testNet = 0;
        string? proxyArg = null;
        string? proxyModeArg = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--config": if (i + 1 < args.Length) configPath = args[++i]; break;
                case "--source": if (i + 1 < args.Length) source = args[++i]; break;
                case "--report": if (i + 1 < args.Length) report = args[++i]; break;
                case "--undo": if (i + 1 < args.Length) undo = args[++i]; break;
                case "--dry-run":
                case "-n": dryRun = true; break;
                case "--no-reverse": noReverse = true; break;
                case "--yes":
                case "-y": yes = true; break;
                case "--limit": if (i + 1 < args.Length && int.TryParse(args[i + 1], out var lim)) { limit = lim; i++; } break;
                case "--sauce-key": if (i + 1 < args.Length) sauceKey = args[++i]; break;
                case "--reverse-concurrency": if (i + 1 < args.Length && int.TryParse(args[i + 1], out var rc)) { reverseConcurrency = rc; i++; } break;
                case "--no-reverse-queue": noReverseQueue = true; break;
                case "--unresolved-folder": if (i + 1 < args.Length) unresolvedFolder = args[++i]; break;
                case "--no-unresolved-folder": noUnresolvedFolder = true; break;
                case "--test-net": testNet = 1; break;
                case "--proxy": if (i + 1 < args.Length) proxyArg = args[++i]; break;
                case "--proxy-mode": if (i + 1 < args.Length) proxyModeArg = args[++i]; break;
                case "--verbose":
                case "-v": verbose = true; break;
                default:
                    if (!a.StartsWith("-") && source == null) source = a;
                    break;
            }
        }

        var cfg = AppConfig.Load(configPath ?? Path.Combine(baseDir, "PixivRenamer.json"));
        if (!string.IsNullOrWhiteSpace(source)) cfg.SourceDir = source;
        if (!string.IsNullOrWhiteSpace(report)) cfg.ReportPath = report;
        if (dryRun) cfg.DryRun = true;
        if (noReverse) cfg.ReverseEnabled = false;
        if (verbose) cfg.Verbose = true;
        if (limit > 0) cfg.FileLimit = limit;
        if (!string.IsNullOrWhiteSpace(sauceKey)) cfg.SauceNaoApiKey = sauceKey;
        if (reverseConcurrency > 0) cfg.ReverseConcurrency = reverseConcurrency;
        if (noReverseQueue) cfg.ReverseQueueEnabled = false;
        if (!string.IsNullOrWhiteSpace(unresolvedFolder)) cfg.UnresolvedFolderName = unresolvedFolder;
        if (noUnresolvedFolder) cfg.MoveUnresolvedToFolder = false;
        if (!string.IsNullOrWhiteSpace(proxyArg)) cfg.Proxy = proxyArg;
        if (!string.IsNullOrWhiteSpace(proxyModeArg)) cfg.ProxyMode = proxyModeArg;
        if (!yes && !dryRun && testNet == 0)
        {
            Console.WriteLine("命令行为正式执行模式，请显式加 --yes 确认（或加 --dry-run 预览）。");
            return 2;
        }
        cfg.Finalize(baseDir);

        // 只做网络/代理诊断，不整理文件：写报告到 exe 隔壁的 error-reports
        if (testNet > 0)
        {
            Console.WriteLine("=== 网络与代理诊断 ===");
            var resolution = ProxyResolver.Resolve(cfg, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine(ProxyResolver.Describe(resolution));
            var diagCfg = cfg.Clone();
            diagCfg.Proxy = resolution.Chosen;
            diagCfg.UseSystemProxy = resolution.Chosen.Length == 0 && cfg.ProxyMode != "direct";
            var probes = await NetworkDiagnostics.ProbeAsync(diagCfg, cfg.PixivApiBase, CancellationToken.None);
            Console.WriteLine();
            Console.WriteLine("连通性测试：");
            foreach (var p in probes)
                Console.WriteLine($"  {(p.Ok ? "[成功]" : "[失败]")} {p.Name} {p.Url}\n           {p.Detail}");
            var diagReport = NetworkDiagnostics.WriteReport(baseDir, "命令行 --test-net", cfg, resolution, probes);
            if (diagReport.Length > 0) Console.WriteLine("\n诊断报告：" + diagReport);
            var pixivOkNow = probes.FirstOrDefault(p => p.Name == "pixiv")?.Ok == true;
            Console.WriteLine();
            Console.WriteLine(pixivOkNow ? "结论：pixiv 可达。" : "结论：pixiv 不可达，请按报告里的建议检查代理。");
            return pixivOkNow ? 0 : 1;
        }

        if (!string.IsNullOrWhiteSpace(undo))
        {
            var count = UndoService.Undo(undo, dryRun, Console.WriteLine);
            Console.WriteLine($"撤销完成，共恢复 {count} 个文件。");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(cfg.SourceDir) || !Directory.Exists(cfg.SourceDir))
        {
            Console.WriteLine("用法：PixivRenamer.exe --cli <插画目录> [--dry-run] [--no-reverse] [--limit N] [--sauce-key KEY] [--report 报告.csv] [--yes]");
            Console.WriteLine("      PixivRenamer.exe --cli --undo <报告.csv>");
            Console.WriteLine("  识图缓冲：--reverse-concurrency N（识图并发，默认 1） / --no-reverse-queue（关闭排队缓冲）");
            Console.WriteLine("  未识别归类：--unresolved-folder 目录名（默认「未识别」） / --no-unresolved-folder（保持原样）");
            Console.WriteLine("  网络诊断：--test-net [--proxy 127.0.0.1:7897] [--proxy-mode auto|explicit|system|direct]");
            return 2;
        }

        var log = new SyncProgress<string>(Console.WriteLine);
        var progress = new SyncProgress<ProgressInfo>(p =>
        {
            if (p.Total > 0) Console.WriteLine($"{p.Phase} {p.Current}/{p.Total}");
        });
        var engine = new RenamerEngine(cfg, log, progress);
        try
        {
            var result = await engine.RunAsync(CancellationToken.None);
            return result.ExitCode;
        }
        catch (OperationCanceledException) { return 1; }
        catch (Exception ex) { Console.WriteLine("运行失败：" + ex.Message); return 1; }
    }
}