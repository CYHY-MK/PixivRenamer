using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace PixivRenamer.Core;

/// <summary>一次网络诊断的结果。</summary>
public sealed class NetworkProbe
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>
/// 网络诊断与错误报告（2.3.1）。
///
/// 便携版分发出去以后，最常见的报错就是"连不上 pixiv / 代理没配好"，而原来的日志只有一行
/// "请求超时"，用户不知道下一步该做什么。这里做两件事：
///   1. 把"用了哪个代理、试过哪些候选、每个候选的结果、pixiv / SauceNAO 是否可达"写进
///      exe 同目录的 error-reports\网络诊断_*.txt，用户可以直接把这个文件发回来；
///   2. 在界面上给出可直接照做的处理建议（按 Clash / v2rayN / Sing-box 分别给）。
/// </summary>
public static class NetworkDiagnostics
{
    public const string ReportFolderName = "error-reports";

    public static string ReportDir(string exeDir) => Path.Combine(exeDir, ReportFolderName);

    /// <summary>生成诊断报告文件，返回路径（失败返回空字符串）。</summary>
    public static string WriteReport(string exeDir, string title, AppConfig cfg, ProxyResolution? proxy,
                                     IEnumerable<NetworkProbe>? probes,
                                     IEnumerable<string>? recentLog = null,
                                     string? extra = null)
    {
        try
        {
            var dir = ReportDir(exeDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"网络诊断_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var sb = new StringBuilder();
            sb.AppendLine("PixivRenamer 网络诊断报告");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"生成时间：{TextUtils.NowStamp()}");
            sb.AppendLine($"触发原因：{title}");
            sb.AppendLine();
            sb.AppendLine("【程序环境】");
            sb.AppendLine($"  程序版本   ：{AppConfig.VersionText}");
            sb.AppendLine($"  项目主页   ：{AppInfo.RepositoryUrl}");
            sb.AppendLine($"  作者 / 许可：{AppInfo.Author} / {AppInfo.License}");
            sb.AppendLine($"  程序路径   ：{Elevation.ExecutablePath()}");
            sb.AppendLine($"  工作目录   ：{exeDir}");
            sb.AppendLine($"  是否管理员 ：{(Elevation.IsElevated ? "是" : "否")}");
            sb.AppendLine($"  系统       ：{RuntimeInformation.OSDescription}（{(Environment.Is64BitProcess ? "x64" : "x86")} 进程）");
            sb.AppendLine($"  .NET       ：{RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"  时区       ：{TimeZoneInfo.Local.DisplayName}");
            sb.AppendLine($"  Clash 端口 ：{DescribeClashFiles()}");
            sb.AppendLine();
            sb.AppendLine("【代理设置】");
            sb.AppendLine($"  配置里的模式：{ProxyResolver.ModeLabel(cfg.ProxyMode)}");
            sb.AppendLine($"  手动填写的地址：{(string.IsNullOrWhiteSpace(cfg.Proxy) ? "（空）" : cfg.Proxy)}");
            sb.AppendLine($"  使用系统代理：{(cfg.UseSystemProxy ? "是" : "否")}");
            sb.AppendLine($"  系统代理设置（WinINET）：{(ProxyResolver.SystemProxy() is { Length: > 0 } sp ? sp : "（未设置）")}");
            if (proxy != null)
            {
                sb.AppendLine($"  最终使用   ：{proxy.DescribeChosen()}");
                if (proxy.InvalidInput.Length > 0)
                    sb.AppendLine($"  ⚠ 无法识别的代理：{proxy.InvalidInput} —— {proxy.InvalidReason}");
                sb.AppendLine();
                sb.AppendLine("【候选代理检测】");
                if (proxy.Candidates.Count == 0) sb.AppendLine("  （没有候选）");
                foreach (var c in proxy.Candidates)
                    sb.AppendLine($"  {(c.HttpProxyOk ? "[可用]" : c.Listening ? "[端口通/代理不通]" : "[未监听]")} {c.Uri}  <- {c.Source}：{c.Detail}");
                foreach (var note in proxy.Notes) sb.AppendLine("  说明：" + note);
            }
            sb.AppendLine();
            sb.AppendLine("【连通性测试】");
            if (probes == null) sb.AppendLine("  （未执行）");
            else
            {
                foreach (var p in probes)
                    sb.AppendLine($"  {(p.Ok ? "[成功]" : "[失败]")} {p.Name} {p.Url}\n           {p.Detail}");
            }
            if (!string.IsNullOrWhiteSpace(extra))
            {
                sb.AppendLine();
                sb.AppendLine("【补充信息】");
                sb.AppendLine(extra);
            }
            if (recentLog != null)
            {
                var lines = recentLog.ToList();
                if (lines.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("【最近日志】");
                    foreach (var line in lines.TakeLast(60)) sb.AppendLine("  " + line);
                }
            }
            sb.AppendLine();
            sb.AppendLine("【如果是 Clash 用户，按顺序检查】");
            sb.AppendLine("  1. 确认 Clash 正在运行，且已经选好节点、能正常打开网页。");
            sb.AppendLine("  2. 打开 Clash 的「设置 / 端口」页，看 mixed-port（混合端口）是多少值，默认 7897；");
            sb.AppendLine("     如果那里不是 7897，就把本程序「代理」里的端口改成同一个值（例如 7890）。");
            sb.AppendLine("  3. 确认 Clash 的「系统代理」开关或 TUN 模式已开启（本程序默认自动侦测，两者都能识别）。");
            sb.AppendLine("  4. 如果还是不行：在本程序「pixiv / 识图」页点「检测本地代理」，看哪个端口可用；");
            sb.AppendLine("     也可以在 Clash 里把 mixed-port 改回 7897 再试。");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch { return ""; }
    }

    /// <summary>列出读到的 Clash 配置文件，便于判断"到底是哪个客户端、端口多少"。</summary>
    private static string DescribeClashFiles()
    {
        try
        {
            var portTexts = new List<string>();
            foreach (var c in ProxyResolver.Collect(includeCommonPorts: false))
                if (c.Source.Contains("配置") || c.Source.Contains("系统"))
                    portTexts.Add($"{c.Uri}（{c.Source}）");
            return portTexts.Count > 0 ? string.Join("，", portTexts) : "未从配置文件读到端口";
        }
        catch { return "读取失败"; }
    }

    /// <summary>对关键站点做连通性测试（走指定代理），用于诊断报告与"测试网络"按钮。</summary>
    public static async Task<List<NetworkProbe>> ProbeAsync(AppConfig cfg, string pixivBase, CancellationToken ct)
    {
        var list = new List<NetworkProbe>();
        var targets = new (string Name, string Url)[]
        {
            ("pixiv", pixivBase.TrimEnd('/') + "/"),
            ("SauceNAO", "https://saucenao.com/"),
            ("ascii2d", "https://ascii2d.net/"),
        };

        foreach (var (name, url) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var probe = new NetworkProbe { Name = name, Url = url };
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(4000, Math.Min(cfg.TimeoutMs, 15000))),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                };
                if (cfg.ProxyMode == "direct") handler.UseProxy = false;
                else if (!string.IsNullOrWhiteSpace(cfg.Proxy)) { handler.Proxy = new System.Net.WebProxy(cfg.Proxy); handler.UseProxy = true; }
                else { handler.UseProxy = true; handler.Proxy = null; }

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(5000, Math.Min(cfg.TimeoutMs, 15000))) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(cfg.UserAgent);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                probe.Ok = (int)resp.StatusCode is >= 200 and < 400;
                probe.Detail = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                               (probe.Ok ? "" : "（服务端可达但返回非成功状态）");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                probe.Ok = false;
                probe.Detail = Describe(ex);
            }
            list.Add(probe);
        }
        return list;
    }

    /// <summary>把异常翻译成用户能看懂的一句话。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException or OperationCanceledException
            => "请求超时（网络不通，或代理没生效）",
        HttpRequestException hre => hre.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "域名解析失败（DNS 问题，或代理未生效）",
            HttpRequestError.ConnectionError => "连接被拒绝 / 无法建立连接（代理端口不对？防火墙？）",
            HttpRequestError.SecureConnectionError => "TLS 握手失败（可能被中间设备干扰）",
            _ => "网络错误：" + hre.Message,
        },
        _ => ex.GetType().Name + "：" + ex.Message,
    };

    /// <summary>面向用户的处理建议（弹窗用）。</summary>
    public static string Advice(string reportPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("网络连不上 pixiv，请按下面顺序检查：");
        sb.AppendLine();
        sb.AppendLine("1) 代理软件（Clash / v2rayN 等）是否已启动、节点是否可用——先在浏览器里打开 pixiv 确认。");
        sb.AppendLine("2) 端口要对上：Clash Verge 默认 7897，Clash for Windows 默认 7890。");
        sb.AppendLine("   在 Clash 的「设置 → 端口」看 mixed-port 的值，和本程序里填的保持一致。");
        sb.AppendLine("3) 本程序默认「自动侦测」本地代理：先点「检测本地代理」，它会列出所有探测到的端口和结果。");
        sb.AppendLine("4) 如果 Clash 开了 TUN 模式但没开系统代理，也可以选「使用系统代理」或直接选「自动侦测」。");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(reportPath))
            sb.AppendLine("详细诊断已写入：\r\n" + reportPath + "\r\n（把这个文件发给我就能定位问题）");
        return sb.ToString().TrimEnd();
    }
}
