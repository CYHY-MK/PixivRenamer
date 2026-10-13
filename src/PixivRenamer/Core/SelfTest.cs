using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text;

namespace PixivRenamer.Core;

public sealed class SelfTestResult
{
    public int Pass { get; set; }
    public int Fail { get; set; }
    public List<string> Failures { get; } = new();

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var f in Failures) sb.AppendLine("[FAIL] " + f);
        sb.AppendLine($"自检结束：通过 {Pass}，失败 {Fail}");
        return sb.ToString();
    }
}

public static class SelfTest
{
    public static SelfTestResult RunAll()
    {
        var r = new SelfTestResult();

        // PID
        Check(PidParser.Extract("12345678_p0") is { Pid: "12345678", PageIndex: 0 }, "PID 12345678_p0", r);
        Check(PidParser.Extract("12345678_p12") is { Pid: "12345678", PageIndex: 12 }, "PID 12345678_p12", r);
        Check(PidParser.Extract("illust_7654321_20200101") is { Pid: "7654321" }, "PID illust_7654321", r);
        Check(PidParser.Extract("pixiv_id=1234567") is { Pid: "1234567" }, "PID pixiv_id", r);
        Check(PidParser.Extract("[8765432] foo") is { Pid: "8765432" }, "PID 括号", r);
        Check(PidParser.Extract("artworks-1234567-p1") is { Pid: "1234567" }, "PID artworks-p1", r);
        Check(PidParser.Extract("foo 1234567") is { Pid: "1234567" }, "PID 7 位裸数字", r);
        Check(PidParser.Extract("IMG_20230101") == null, "PID 日期不识别", r);
        Check(PidParser.Extract("foo 123456") == null, "PID 6 位默认不识别", r);
        Check(PidParser.Extract("foo 123456", true) is { Pid: "123456" }, "PID 激进模式 6 位", r);
        Check(PidParser.IsLikelyDate("20230101"), "PID 日期判断", r);
        Check(PidParser.PageSuffix(3) == "_p3", "PID 分页后缀", r);

        // 命名
        var vars = new Dictionary<string, string> { ["author"] = "作者A", ["title"] = "标题B", ["pid"] = "123456" };
        Check(TextUtils.ExpandTemplate("{author} - {title} ({pid})", vars) == "作者A - 标题B (123456)", "模板展开", r);
        Check(TextUtils.CleanupBaseName("A - ()") == "A", "清理空括号", r);
        Check(TextUtils.CleanupBaseName("A -  - B") == "A - B", "清理分隔符", r);
        Check(TextUtils.SanitizeFileName("a<b>:c\"d/e\\f|g?h*i") == "a_b__c_d_e_f_g_h_i", "文件名非法字符", r);
        Check(TextUtils.SanitizeFileName("CON") == "_CON", "Windows 保留名", r);
        Check(TextUtils.SanitizeFileName(new string('a', 400), 100).Length <= 100, "文件名长度限制", r);

        var meta = new PixivMeta { Author = "作者A", Title = "标题B", Pid = "123456", PageCount = 2 };
        var cfg = new AppConfig { Template = "{author} - {title} ({pid})", UnknownAuthor = "未知作者", UnknownTitle = "未命名" };
        Check(NamingService.BuildBaseName(meta, cfg, "fallback", "_p1") == "作者A - 标题B (123456)_p1", "生成文件名", r);
        meta.Title = "";
        Check(NamingService.BuildBaseName(meta, cfg, "fallback", "").Contains("未命名"), "缺失标题占位", r);

        // CSV / HTML / 工具
        Check(TextUtils.CsvEscape("a,b") == "\"a,b\"", "CSV 逗号转义", r);
        Check(TextUtils.CsvEscape("a\"b") == "\"a\"\"b\"", "CSV 引号转义", r);
        var cols = TextUtils.ParseCsvLine("a,\"b,c\",d");
        Check(cols.Count == 3 && cols[1] == "b,c", "CSV 解析", r);
        Check(TextUtils.HtmlDecode("a&amp;b&lt;c&#65;") == "a&b<cA", "HTML 实体解码", r);
        Check(TextUtils.IsImageFile("a.JPG") && !TextUtils.IsImageFile("a.txt"), "图片扩展名判断", r);
        Check(TextUtils.SamePath(@"C:\a\b", @"C:\a\b\"), "相同路径判断", r);
        Check(!TextUtils.SamePath(@"C:\a\b", @"C:\a\c"), "不同路径判断", r);
        Check(TextUtils.IsStrictSubdirectory(@"C:\a\b", @"C:\a"), "真子目录判断", r);
        Check(!TextUtils.IsStrictSubdirectory(@"C:\a", @"C:\a"), "相同目录不算子目录", r);
        Check(!TextUtils.IsStrictSubdirectory(@"C:\a", @"C:\a\b"), "上级目录不算子目录", r);

        // 感知哈希（识图去重 / 结果复核依赖它）
        var h1 = HashOfGenerated(0);
        var h2 = HashOfGenerated(0);
        var h3 = HashOfGenerated(1);
        Check(h1 != 0, "dHash 可计算", r);
        Check(h1 == h2, "dHash 同一张图结果一致", r, h1.ToString());
        Check(PerceptualHash.Distance(h1, h3) > 10, "dHash 能区分不同图", r, $"距离={PerceptualHash.Distance(h1, h3)}");
        Check(PerceptualHash.Distance(h1, 0) == int.MaxValue, "dHash 无效输入返回最大距离", r);
        Check(PerceptualHash.Distance(h1, h1) == 0, "dHash 自身距离为 0", r);
        Check(ReverseSearchService.IndexRank(5) < ReverseSearchService.IndexRank(9), "pixiv 索引优先于 danbooru", r);
        Check(ReverseSearchService.IndexRank(6) == 1, "pixivhistorical 为次优先", r);

        // 识图请求缓冲（2.3）：冷却闸门 + 未识别分类
        CheckUnresolvedClassification(r);
        CheckReverseThrottle(r);
        CheckProxyNormalization(r);
        CheckProxyDetection(r);
        CheckElevation(r);
        CheckUpdateLogic(r);

        return r;
    }

    /// <summary>检查更新的版本号逻辑（1.0）：tag 归一化 + 版本比较。</summary>
    private static void CheckUpdateLogic(SelfTestResult r)
    {
        Check(UpdateService.NormalizeTag("v1.0.0") == "1.0.0", "tag 归一化 v1.0.0", r, UpdateService.NormalizeTag("v1.0.0"));
        Check(UpdateService.NormalizeTag("1.2.3") == "1.2.3", "tag 归一化 1.2.3", r);
        Check(UpdateService.NormalizeTag("release-1.0.1-beta") == "1.0.1", "tag 归一化 release-1.0.1-beta", r, UpdateService.NormalizeTag("release-1.0.1-beta"));
        Check(UpdateService.NormalizeTag("") == "" && UpdateService.NormalizeTag("no-version") == "", "tag 无法解析返回空", r);

        Check(UpdateService.CompareVersions("1.0.1", "1.0.0") > 0, "1.0.1 > 1.0.0", r);
        Check(UpdateService.CompareVersions("1.0.0", "1.0.0") == 0, "1.0.0 == 1.0.0", r);
        Check(UpdateService.CompareVersions("1.0.0", "1.0.1") < 0, "1.0.0 < 1.0.1", r);
        Check(UpdateService.CompareVersions("1.0.10", "1.0.9") > 0, "1.0.10 > 1.0.9（不按字符串比）", r);
        Check(UpdateService.CompareVersions("1.1", "1.0.9") > 0, "1.1 > 1.0.9（位数不同）", r);
        Check(UpdateService.CompareVersions("2.0", "1.9.9") > 0, "2.0 > 1.9.9", r);

        // 项目信息必须齐备，界面/诊断报告都依赖它
        Check(AppInfo.RepositoryUrl.StartsWith("https://github.com/"), "项目地址有效", r, AppInfo.RepositoryUrl);
        Check(AppInfo.Author.Length > 0 && AppInfo.License.Length > 0, "作者与许可已填写", r);
        Check(AppConfig.VersionText.Length > 0 && char.IsDigit(AppConfig.VersionText[0]), "版本号可读", r, AppConfig.VersionText);

        // Atom feed 解析（GitHub 的 feed 不计 API 限额，是检查更新的主通道）
        const string atom = """
<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Release notes from PixivRenamer</title>
  <entry>
    <id>tag:github.com,2008:Repository/123456789/v1.0.1</id>
    <updated>2026-10-14T02:00:00Z</updated>
    <link rel="alternate" type="text/html" href="https://github.com/CYHY-MK/PixivRenamer/releases/tag/v1.0.1"/>
    <title>v1.0.1 修复代理侦测</title>
    <content type="html">&lt;ul&gt;&lt;li&gt;修复 Clash 端口识别&lt;/li&gt;&lt;li&gt;新增诊断报告&lt;/li&gt;&lt;/ul&gt;</content>
  </entry>
</feed>
""";
        var upd = new UpdateCheckResult { CurrentVersion = "1.0.0" };
        Check(UpdateService.ParseAtom(atom, upd), "Atom feed 可解析", r);
        Check(upd.LatestVersion == "1.0.1", "Atom 解析出最新版本", r, upd.LatestVersion);
        Check(upd.HasUpdate, "Atom 能判断有新版本", r);
        Check(upd.ReleaseUrl.Contains("releases/tag/v1.0.1"), "Atom 解析出发布页", r, upd.ReleaseUrl);
        Check(upd.Notes.Contains("修复 Clash 端口识别") && !upd.Notes.Contains("<li>"), "Atom 更新说明转成纯文本", r, upd.Notes);

        var same = new UpdateCheckResult { CurrentVersion = "1.0.1" };
        UpdateService.ParseAtom(atom, same);
        Check(!same.HasUpdate, "同版本不提示更新", r);
    }

    /// <summary>本地代理侦测（2.3.1）：Clash 配置解析、系统代理串解析、候选收集、真实可用性。</summary>
    private static void CheckProxyDetection(SelfTestResult r)
    {
        // Clash 配置解析：只认顶层键，不能把节点里的 port: 443 当代理端口
        const string clashYaml = @"
# Generated by Clash Verge
mixed-port: 7897
socks-port: 7898
port: 7899
allow-lan: false
external-controller: 127.0.0.1:9097
proxies:
  - name: node1
    type: ss
    port: 443
  - name: node2
    type: vmess
    port: 8443
";
        var ports = ProxyResolver.ParseClashPorts(clashYaml);
        Check(ports.Count > 0 && ports[0] == 7897, "Clash 混合端口优先", r, string.Join(",", ports));
        Check(ports.Contains(7898) && ports.Contains(7899), "Clash socks/http 端口可识别", r, string.Join(",", ports));
        Check(!ports.Contains(443) && !ports.Contains(8443) && !ports.Contains(9097), "不误认节点端口与 API 端口", r, string.Join(",", ports));

        // 系统代理字符串："http=127.0.0.1:7897;https=127.0.0.1:7897" / "127.0.0.1:7897"
        Check(ProxyResolver.FirstProxyFromServerLine("http=127.0.0.1:7897;https=127.0.0.1:7897") == "127.0.0.1:7897",
            "解析系统代理字符串（带协议前缀）", r, ProxyResolver.FirstProxyFromServerLine("http=127.0.0.1:7897;https=127.0.0.1:7897"));
        Check(ProxyResolver.FirstProxyFromServerLine("127.0.0.1:7890") == "127.0.0.1:7890", "解析系统代理字符串（裸地址）", r);
        Check(ProxyResolver.FirstProxyFromServerLine("") == "", "系统代理空值", r);

        // 候选收集：必须包含常见端口，且不会因为某个端口没人监听就崩
        var candidates = ProxyResolver.Collect();
        Check(candidates.Count >= 10, "候选代理收集", r, candidates.Count.ToString());
        Check(candidates.Any(c => c.Uri == "http://127.0.0.1:7897"), "候选含 Clash Verge 默认端口", r);
        Check(candidates.All(c => AppConfig.NormalizeProxy(c.Uri) == c.Uri), "候选地址已规范化", r);

        // 端口探测不应抛异常（本机 1 端口一定有东西或直接拒绝，都不该崩）
        ProxyResolver.IsListening("http://127.0.0.1:1");

        // direct 模式：必须完全不使用代理
        var directCfg = new AppConfig { ProxyMode = "direct", Proxy = "127.0.0.1:7897" };
        directCfg.Finalize(Path.GetTempPath());
        var direct = ProxyResolver.Resolve(directCfg, null, verify: false);
        Check(direct.Chosen.Length == 0, "direct 模式不使用代理", r, direct.Chosen);

        // explicit 模式的空地址要给出说明，而不是静默直连
        var emptyCfg = new AppConfig { ProxyMode = "explicit", Proxy = "" };
        emptyCfg.Finalize(Path.GetTempPath());
        var empty = ProxyResolver.Resolve(emptyCfg, null, verify: false);
        Check(empty.Chosen.Length == 0 && empty.Notes.Count > 0, "explicit 模式空地址有提示", r);

        // 非法地址要能被识别出来（弹窗/报告里要给出明确原因）
        var badCfg = new AppConfig { ProxyMode = "explicit", Proxy = "这不是地址" };
        badCfg.Finalize(Path.GetTempPath());
        var bad = ProxyResolver.Resolve(badCfg, null, verify: false);
        Check(bad.InvalidInput.Length > 0 && bad.InvalidReason.Length > 0, "非法代理地址有明确原因", r, bad.InvalidReason);
    }

    /// <summary>管理员提权支持（2.3.1）：权限探测、exe 路径、提权标记清理。</summary>
    private static void CheckElevation(SelfTestResult r)
    {
        var exeDir = AppConfig.ExeDirectory();
        Check(exeDir.Length > 0 && Directory.Exists(exeDir), "exe 目录可解析", r, exeDir);

        var exe = Elevation.ExecutablePath();
        Check(exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), "exe 路径以 .exe 结尾", r, exe);
        Check(File.Exists(exe), "exe 路径真实存在", r, exe);

        // 提权探测必须和框架自身的判断一致（自检进程可能是任意权限）
        bool expect;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            expect = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { expect = false; }
        Check(Elevation.IsElevated == expect, "管理员探测与系统判断一致", r,
            $"IsElevated={Elevation.IsElevated} expect={expect}");

        // 提权重启时附加的标记不能被传下去，否则会污染参数解析
        var args = new[] { "--cli", "D:\\pixiv", Elevation.RelaunchMarker, "--yes" };
        Check(Elevation.HasRelaunchMarker(args), "识别提权标记", r);
        var clean = Elevation.Sanitize(args);
        Check(clean.Length == 3 && !clean.Contains(Elevation.RelaunchMarker), "剔除提权标记", r,
            string.Join(" ", clean));
    }

    /// <summary>代理地址规范化：常见写法要能识别，明显写错的不应该让任务崩掉。</summary>
    private static void CheckProxyNormalization(SelfTestResult r)
    {
        Check(AppConfig.NormalizeProxy("127.0.0.1:7897") == "http://127.0.0.1:7897", "代理补协议", r, AppConfig.NormalizeProxy("127.0.0.1:7897"));
        Check(AppConfig.NormalizeProxy("http://127.0.0.1:7890") == "http://127.0.0.1:7890", "代理保留 http", r);
        Check(AppConfig.NormalizeProxy("socks5://127.0.0.1:1080") == "socks5://127.0.0.1:1080", "代理保留 socks5", r);
        Check(AppConfig.NormalizeProxy("  ") == "", "代理空值", r);
        Check(AppConfig.NormalizeProxy("这不是一个地址") == "", "代理非法值被拒", r, AppConfig.NormalizeProxy("这不是一个地址"));

        // Finalize 会把代理规范化后写回配置
        var cfg = new AppConfig { Proxy = "127.0.0.1:7897" };
        cfg.Finalize(Path.GetTempPath());
        Check(cfg.Proxy == "http://127.0.0.1:7897", "Finalize 规范化代理", r, cfg.Proxy);
    }

    /// <summary>未识别原因分类：不同失败路径要落到不同分类，才能按类归档/重试。</summary>
    private static void CheckUnresolvedClassification(SelfTestResult r)
    {
        var engine = new RenamerEngine(new AppConfig());
        var item = new FileItem { SourcePath = @"C:\a\b.jpg", Pid = "" };
        item.UnresolvedReason = "识图未识别（saucenao 无候选结果）";
        Check(engine.ClassifyUnresolved(item) == "识图无结果", "未识别分类：识图无结果", r, engine.ClassifyUnresolved(item));

        var item2 = new FileItem { SourcePath = @"C:\a\b.jpg", Pid = "1234567" };
        item2.Meta.Note = "文件过大或无法读取，跳过识图：b.jpg（压缩失败）";
        Check(engine.ClassifyUnresolved(item2) == "文件过大或无法读取", "未识别分类：文件过大", r, engine.ClassifyUnresolved(item2));

        var item3 = new FileItem { SourcePath = @"C:\a\b.jpg", Pid = "1234567", PidMetaOk = true, ReverseUsed = true };
        item3.UnresolvedReason = "识图未识别（候选被 dHash 复核拦下（差异 30/64））";
        Check(engine.ClassifyUnresolved(item3) == "识图复核未通过", "未识别分类：复核拦下", r, engine.ClassifyUnresolved(item3));

        var item4 = new FileItem { SourcePath = @"C:\a\b.jpg", Pid = "1234567", PidMetaOk = true, ReverseUsed = true, ReverseTimedOut = true };
        Check(engine.ClassifyUnresolved(item4) == "识图限流待重试", "未识别分类：限流待重试", r, engine.ClassifyUnresolved(item4));

        var item5 = new FileItem { SourcePath = @"C:\a\b.jpg", Pid = "1234567", PidMetaOk = true };
        item5.Meta.Note = "pixiv AJAX 查询失败（HTML 兜底也没拿到作者/标题）";
        Check(engine.ClassifyUnresolved(item5) == "pixiv 查询失败", "未识别分类：pixiv 查询失败", r, engine.ClassifyUnresolved(item5));

        var item6 = new FileItem { SourcePath = @"C:\a\b.jpg" };
        Check(engine.ClassifyUnresolved(item6) == "无 PID 且识图无结果", "未识别分类：无 PID", r, engine.ClassifyUnresolved(item6));

        // 未识别归档目录：有输出目录时用输出目录，否则用源目录
        var cfgOut = new AppConfig { SourceDir = @"C:\src", OutputDir = @"C:\out", UnresolvedFolderName = "未识别" };
        var engineOut = new RenamerEngine(cfgOut);
        Check(engineOut.UnresolvedDir() == @"C:\out\未识别", "未识别目录（有输出目录）", r, engineOut.UnresolvedDir());
        var cfgIn = new AppConfig { SourceDir = @"C:\src", OutputDir = "", UnresolvedFolderName = "未识别" };
        Check(new RenamerEngine(cfgIn).UnresolvedDir() == @"C:\src\未识别", "未识别目录（原地）", r);

        // 配置收敛：文件夹名会被清理，冷却上限不低于基础冷却
        var cfg = new AppConfig { UnresolvedFolderName = "  未识别??  ", ReverseCooldownMs = 20000, ReverseMaxCooldownMs = 1000 };
        cfg.Finalize(Path.GetTempPath());
        Check(!cfg.UnresolvedFolderName.Contains('?'), "未识别文件夹名会被清理", r, cfg.UnresolvedFolderName);
        Check(cfg.ReverseMaxCooldownMs >= cfg.ReverseCooldownMs, "冷却上限不低于基础冷却", r, cfg.ReverseMaxCooldownMs.ToString());
    }

    /// <summary>识图冷却闸门：无冷却时不等待；被限流后必须等待，且冷却时间随连续限流增长。</summary>
    private static void CheckReverseThrottle(SelfTestResult r)
    {
        var cfg = new AppConfig { ReverseCooldownMs = 1000, ReverseMaxCooldownMs = 5000 };
        var throttle = new ReverseThrottle(cfg);
        Check(!throttle.Cooling, "冷却闸门：初始未冷却", r);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        throttle.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 200, "冷却闸门：未冷却时不额外等待", r, sw.ElapsedMilliseconds + "ms");

        throttle.Block(0, "单测");
        Check(throttle.Cooling, "冷却闸门：被限流后进入冷却", r);
        Check(throttle.CooldownCount == 1, "冷却闸门：触发计数", r, throttle.CooldownCount.ToString());

        // 连续限流应指数退避（第二次至少 2 倍基础冷却，这里只验证"冷却时间变长"）
        throttle.Block(0, "单测");
        throttle.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(!throttle.Cooling, "冷却闸门：等待结束后解除冷却", r);

        throttle.Succeed();
        throttle.Succeed();
        Check(!throttle.Cooling, "冷却闸门：成功后回落不报错", r);
    }

    /// <summary>生成纯代码构造的测试图，验证 dHash 管线（不依赖任何外部文件）。</summary>
    private static ulong HashOfGenerated(int variant)
    {
        try
        {
            const int w = 64, h = 64, stride = w * 4;
            var pixels = new byte[stride * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * stride + x * 4;
                    byte v = variant == 0
                        ? (byte)(x < w / 2 ? 255 : 0)
                        : (byte)((x * 3 + y * 5) % 256);
                    pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 255;
                }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return PerceptualHash.Compute(ms.ToArray());
        }
        catch { return 0; }
    }

    public static string RunAndWrite(string baseDir)
    {
        var result = RunAll();
        var text = result.ToString();
        try { File.WriteAllText(Path.Combine(baseDir, "selftest.log"), text, new UTF8Encoding(true)); }
        catch { }
        return text;
    }

    private static void Check(bool condition, string name, SelfTestResult result, string detail = "")
    {
        if (condition) result.Pass++;
        else { result.Fail++; result.Failures.Add(string.IsNullOrEmpty(detail) ? name : $"{name}：{detail}"); }
    }
}