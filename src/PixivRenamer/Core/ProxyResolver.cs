using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PixivRenamer.Core;

/// <summary>一个候选代理。</summary>
public sealed class ProxyCandidate
{
    public string Uri { get; set; } = "";
    public string Source { get; set; } = "";   // 来源说明（配置文件 / 系统设置 / 常见端口 / 手动填写）
    public bool Listening { get; set; }        // 端口是否在监听
    public bool HttpProxyOk { get; set; }      // 是否真的能作为 HTTP 代理（CONNECT 测试）
    public string Detail { get; set; } = "";

    public override string ToString()
        => $"{Uri}（{Source}）{(HttpProxyOk ? "可用" : Listening ? "端口在监听但代理不可用" : "未监听")}";
}

/// <summary>一次代理解析的结果，用于日志/弹窗/诊断报告。</summary>
public sealed class ProxyResolution
{
    public string Mode { get; set; } = "auto";
    public string Chosen { get; set; } = "";
    public string ChosenSource { get; set; } = "";
    public bool AutoDetected { get; set; }
    public List<ProxyCandidate> Candidates { get; } = new();
    public List<string> Notes { get; } = new();
    public string InvalidInput { get; set; } = "";
    public string InvalidReason { get; set; } = "";

    public string DescribeChosen()
        => string.IsNullOrEmpty(Chosen)
            ? (Mode == "direct" ? "直连（不使用代理）" : "未指定代理（交给系统代理 / 直连）")
            : $"{Chosen}（{ChosenSource}）";
}

/// <summary>
/// 本地代理自动侦测。
///
/// 背景：便携版发给别人后，最常见的失败原因就是"代理没填 / 填错端口"——
///   - Clash for Windows 默认 7890（mixed-port 旧默认）
///   - Clash Verge / Verge Rev 默认 7897，同时 socks-port 7898、port 7899
///   - v2rayN 默认 10808/10809，Sing-box / Nekoray 常见 2080
/// 所以这里按"配置文件 -> 系统代理设置 -> 常见端口"的顺序收集候选，
/// 再用真实 CONNECT 请求确认哪个真能当 HTTP 代理用，最后才决定用哪个。
/// </summary>
public static class ProxyResolver
{
    /// <summary>常见本地代理端口（顺序即优先级）。</summary>
    public static readonly int[] CommonPorts =
    {
        7897, 7890, 7891, 10809, 10808, 7899, 7898, 2080, 1080, 20171, 20172, 8889, 8888, 8080, 1087, 4780,
    };

    /// <summary>用来验证代理是否可用的目标（轻量、无需鉴权）。</summary>
    private static readonly (string Host, int Port)[] VerifyTargets =
    {
        ("www.gstatic.com", 443),
        ("www.pixiv.net", 443),
        ("ascii2d.net", 443),
    };

    // ---------------- 收集候选 ----------------

    /// <summary>收集所有候选代理（不发起真实请求，只做端口探测）。</summary>
    public static List<ProxyCandidate> Collect(bool includeCommonPorts = true)
    {
        var list = new List<ProxyCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string uri, string source)
        {
            var normalized = AppConfig.NormalizeProxy(uri);
            if (normalized.Length == 0) return;
            if (!seen.Add(normalized)) return;
            list.Add(new ProxyCandidate { Uri = normalized, Source = source });
        }

        foreach (var port in ClashPortsFromFiles(out var clashSources))
        {
            var src = clashSources.TryGetValue(port, out var s) ? s : "Clash 配置";
            Add($"http://127.0.0.1:{port}", src);
        }

        var sys = SystemProxy();
        if (sys.Length > 0) Add(sys, "系统代理设置（WinINET）");

        if (includeCommonPorts)
            foreach (var port in CommonPorts)
                Add($"http://127.0.0.1:{port}", "常见本地代理端口");

        foreach (var c in list)
        {
            c.Listening = IsListening(c.Uri);
            c.Detail = c.Listening ? "端口在监听" : "端口未监听";
        }
        return list;
    }

    /// <summary>从各 Clash 系客户端的配置文件里读出端口（最准，优先用）。</summary>
    private static List<int> ClashPortsFromFiles(out Dictionary<int, string> sources)
    {
        var ports = new List<int>();
        sources = new Dictionary<int, string>();

        foreach (var (label, path) in ClashConfigFiles())
        {
            try
            {
                if (!File.Exists(path)) continue;
                // 用 Share.ReadWrite 打开：Clash 可能正持有该文件
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                    text = reader.ReadToEnd();

                var found = ParseClashPorts(text);
                if (found.Count == 0) continue;

                // mixed-port 优先，其次 port（HTTP），最后 socks-port
                foreach (var p in found)
                    if (!sources.ContainsKey(p)) sources[p] = $"{label} 配置（{Path.GetFileName(path)}）";
                ports.AddRange(found);
            }
            catch { /* 读不到就跳过 */ }
        }
        return ports.Distinct().ToList();
    }

    private static IEnumerable<(string Label, string Path)> ClashConfigFiles()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dirs = new (string Label, string Dir)[]
        {
            ("Clash Verge Rev", Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev")),
            ("Clash Verge", Path.Combine(appData, "clash-verge")),
            ("Clash Verge Rev(本地)", Path.Combine(localAppData, "io.github.clash-verge-rev.clash-verge-rev")),
            ("Clash for Windows", Path.Combine(appData, "Clash for Windows")),
            ("Clash Nyanpasu", Path.Combine(appData, "clash-nyanpasu")),
            ("Clash Nyanpasu", Path.Combine(userProfile, ".config", "clash-nyanpasu")),
            ("Mihomo Party", Path.Combine(appData, "mihomo-party")),
            ("Clash", Path.Combine(userProfile, ".config", "clash")),
            ("Clash Premium", Path.Combine(userProfile, ".config", "mihomo")),
        };

        foreach (var (label, dir) in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // config.yaml 是运行时生成的，端口最贴近当前实际；clash-verge.yaml 是界面设置
            foreach (var file in new[] { "config.yaml", "clash-verge.yaml", "verge.yaml", "config.yml" })
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) yield return (label, path);
            }
        }
    }

    /// <summary>
    /// 解析 clash 配置文本里的端口，只取**顶层**键。
    /// proxies / proxy-groups / rules 段落里的 "port: 443" 是节点端口，绝不能当本地代理端口
    /// （否则会给用户报一堆根本没监听的端口，反而干扰判断）。
    /// </summary>
    public static List<int> ParseClashPorts(string yaml)
    {
        var ports = new List<int>();
        if (string.IsNullOrWhiteSpace(yaml)) return ports;

        var keys = new[] { "mixed-port", "socks-port", "http-port", "port" };
        var inNestedSection = false;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith("#")) continue;

            var indented = line.Length != line.TrimStart().Length;
            if (!indented)
            {
                // 顶层键：port 类直接取值；proxies: 之类的段落开始后，里面的缩进行一律忽略
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim().TrimStart('-').Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');

                if (keys.Contains(key))
                {
                    inNestedSection = false;
                    if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
                    {
                        if (key == "mixed-port") ports.Insert(0, port);  // 混合口最适合本程序，优先
                        else ports.Add(port);
                    }
                    continue;
                }

                // 其它顶层键：值是空 => 后面跟的是嵌套段落
                inNestedSection = value.Length == 0;
                continue;
            }

            // 只认配置文件顶层且值就是数字的端口键：
            // 形如 "port: 7899" 这样带缩进的（多 profile 的覆写段）不当成本地代理端口。
            if (indented && inNestedSection)
            {
                var colonInner = line.IndexOf(':');
                var keyInner = colonInner > 0 ? line[..colonInner].Trim().TrimStart('-').Trim().ToLowerInvariant() : "";
                var valueInner = colonInner > 0 ? line[(colonInner + 1)..].Trim().Trim('"', '\'') : "";
                if ((keyInner == "mixed-port" || keyInner == "socks-port" || keyInner == "http-port") &&
                    int.TryParse(valueInner, out var nestedPort) && nestedPort is >= 1 and <= 65535)
                {
                    if (keyInner == "mixed-port") ports.Insert(0, nestedPort);
                    else ports.Add(nestedPort);
                }
            }
        }

        return ports.Distinct().ToList();
    }

    /// <summary>读取 Windows 系统代理设置（WinINET），PAC 自动配置脚本不算。</summary>
    public static string SystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is int enabled && enabled != 0)
            {
                var server = key.GetValue("ProxyServer") as string ?? "";
                var uri = FirstProxyFromServerLine(server);
                return AppConfig.NormalizeProxy(uri);
            }
        }
        catch { /* 读不到就算了 */ }
        return "";
    }

    /// <summary>系统代理字符串可能是 "127.0.0.1:7897" 或 "http=127.0.0.1:7897;https=127.0.0.1:7897"。</summary>
    public static string FirstProxyFromServerLine(string server)
    {
        if (string.IsNullOrWhiteSpace(server)) return "";
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            value = value.Trim();
            if (value.Length > 0) return value;
        }
        return "";
    }

    // ---------------- 探测 / 验证 ----------------

    /// <summary>轻量端口探测（只判断有没有在监听）。</summary>
    public static bool IsListening(string proxyUri)
    {
        try
        {
            var uri = new Uri(proxyUri);
            using var client = new TcpClient();
            var task = client.ConnectAsync(uri.Host, uri.Port);
            return task.Wait(300) && client.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// 用真实 CONNECT 请求验证该代理能不能用。
    /// 只建立隧道后立刻断开，不会消耗 SauceNAO 配额、也不会真的抓取页面。
    /// </summary>
    public static (bool Ok, string Detail) Verify(string proxyUri)
    {
        Uri uri;
        try { uri = new Uri(proxyUri); }
        catch { return (false, "地址格式不对"); }

        var errors = new List<string>();
        foreach (var (host, port) in VerifyTargets)
        {
            try
            {
                using var client = new TcpClient();
                if (!client.ConnectAsync(uri.Host, uri.Port).Wait(1500))
                {
                    errors.Add($"{host}: 连不上代理端口");
                    continue;
                }
                using var stream = client.GetStream();
                stream.ReadTimeout = 6000;
                stream.WriteTimeout = 6000;
                var request = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: Keep-Alive\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(request);
                stream.Write(bytes, 0, bytes.Length);

                var buffer = new byte[512];
                var read = stream.Read(buffer, 0, buffer.Length);
                var response = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                if (response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) ||
                    response.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                    return (true, $"CONNECT {host}:{port} 成功");
                var firstLine = response.Split('\r', '\n').FirstOrDefault() ?? "";
                errors.Add($"{host}: {firstLine.Trim()}");
            }
            catch (Exception ex)
            {
                errors.Add($"{host}: {ex.GetType().Name} {ex.Message}");
            }
        }
        return (false, string.Join("；", errors));
    }

    // ---------------- 主入口 ----------------

    /// <summary>
    /// 按配置决定最终使用的代理。
    /// mode: auto=自动侦测；explicit=只用填写的；system=用系统代理；direct=直连。
    /// </summary>
    public static ProxyResolution Resolve(AppConfig cfg, Action<string>? log = null, bool verify = true)
    {
        var result = new ProxyResolution
        {
            Mode = string.IsNullOrWhiteSpace(cfg.ProxyMode) ? "auto" : cfg.ProxyMode.Trim().ToLowerInvariant(),
        };

        // 用户手填的地址（无论哪个模式都先检查一遍语法，报错更明确）
        var manualRaw = (cfg.Proxy ?? "").Trim();
        var manual = AppConfig.NormalizeProxy(manualRaw);
        if (manualRaw.Length > 0 && manual.Length == 0)
        {
            result.InvalidInput = manualRaw;
            result.InvalidReason = "地址格式不正确：需要形如 127.0.0.1:7897 或 http://127.0.0.1:7897，只能填主机和端口，不能带路径/空格/中文。";
        }

        if (result.Mode == "direct")
        {
            result.Notes.Add("已设置为直连，未使用任何代理。");
            return result;
        }

        if (result.Mode == "explicit")
        {
            if (manual.Length == 0)
            {
                result.Notes.Add(result.InvalidInput.Length > 0
                    ? "填写的地址无法识别，已忽略（本次没有可用代理）。"
                    : "手动指定代理模式，但代理地址为空。");
                return result;
            }
            var candidate = new ProxyCandidate { Uri = manual, Source = "手动填写" };
            Probe(candidate, verify);
            result.Candidates.Add(candidate);
            if (candidate.HttpProxyOk || !verify)
            {
                result.Chosen = candidate.Uri;
                result.ChosenSource = candidate.Source;
            }
            else
            {
                result.Notes.Add($"手动填写的代理不可用：{candidate.Detail}");
            }
            return result;
        }

        if (result.Mode == "system")
        {
            var sys = SystemProxy();
            if (sys.Length == 0)
            {
                result.Notes.Add("系统代理设置里没有找到代理（可能 Clash 用了 TUN 模式或没开启系统代理）。");
                return result;
            }
            var candidate = new ProxyCandidate { Uri = sys, Source = "系统代理设置（WinINET）" };
            Probe(candidate, verify);
            result.Candidates.Add(candidate);
            if (candidate.HttpProxyOk || !verify)
            {
                result.Chosen = candidate.Uri;
                result.ChosenSource = candidate.Source;
            }
            return result;
        }

        // auto：收集候选 -> 优先试"配置文件/系统设置"里的 -> 再试常见端口
        var candidates = Collect();
        if (manual.Length > 0)
        {
            var explicitCandidate = new ProxyCandidate { Uri = manual, Source = "手动填写（优先）" };
            candidates.Insert(0, explicitCandidate);
        }

        foreach (var candidate in candidates)
        {
            var alreadyListed = result.Candidates.Any(c => c.Uri.Equals(candidate.Uri, StringComparison.OrdinalIgnoreCase));
            if (!alreadyListed) result.Candidates.Add(candidate);
            if (candidate.Uri.Equals("http://127.0.0.1:7890", StringComparison.OrdinalIgnoreCase))
            { /* 7890 是 Clash for Windows 旧默认，可能没开，继续找 */ }
        }

        // 先测"配置文件/系统设置/手动"这些高可信来源，避免被常见端口里的野端口抢走
        var ordered = result.Candidates
            .OrderByDescending(c => c.Source.Contains("手动"))
            .ThenByDescending(c => c.Source.Contains("配置"))
            .ThenByDescending(c => c.Source.Contains("系统"))
            .ToList();

        foreach (var candidate in ordered)
        {
            if (verify)
            {
                if (!candidate.Listening) { candidate.Detail = "端口未监听"; continue; }
                var (ok, detail) = Verify(candidate.Uri);
                candidate.HttpProxyOk = ok;
                candidate.Detail = detail;
                if (ok)
                {
                    result.Chosen = candidate.Uri;
                    result.ChosenSource = candidate.Source;
                    result.AutoDetected = true;
                    log?.Invoke($"自动侦测到可用代理：{candidate.Uri}（{candidate.Source}）");
                    return result;
                }
            }
            else if (candidate.Listening)
            {
                result.Chosen = candidate.Uri;
                result.ChosenSource = candidate.Source;
                result.AutoDetected = true;
                return result;
            }
        }

        log?.Invoke("未能侦测到可用代理，先按直连尝试。");
        return result;
    }

    private static void Probe(ProxyCandidate candidate, bool verify)
    {
        candidate.Listening = IsListening(candidate.Uri);
        if (!candidate.Listening)
        {
            candidate.Detail = "端口未监听（代理软件没启动？端口填错了？）";
            return;
        }
        if (!verify) { candidate.Detail = "端口在监听"; return; }
        var (ok, detail) = Verify(candidate.Uri);
        candidate.HttpProxyOk = ok;
        candidate.Detail = detail;
    }

    /// <summary>把解析结果整理成给人看的几行文本（日志/弹窗共用）。</summary>
    public static string Describe(ProxyResolution resolution)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"代理模式：{ModeLabel(resolution.Mode)}");
        if (resolution.InvalidInput.Length > 0)
            sb.AppendLine($"⚠ 填写的代理无法识别：{resolution.InvalidInput}\n   {resolution.InvalidReason}");
        sb.AppendLine($"最终使用：{resolution.DescribeChosen()}");
        if (resolution.Candidates.Count > 0)
        {
            sb.AppendLine("候选代理检测结果：");
            foreach (var c in resolution.Candidates.Take(12))
                sb.AppendLine($"  - {c.Uri}  [{c.Source}]  {c.Detail}");
        }
        foreach (var note in resolution.Notes) sb.AppendLine("说明：" + note);
        return sb.ToString().TrimEnd();
    }

    public static string ModeLabel(string mode) => mode switch
    {
        "direct" => "直连（不使用代理）",
        "explicit" => "手动指定",
        "system" => "使用系统代理",
        _ => "自动侦测（推荐）",
    };
}
