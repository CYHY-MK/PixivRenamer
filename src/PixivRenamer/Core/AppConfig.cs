using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace PixivRenamer.Core;

public sealed class AppConfig
{
    /// <summary>版本号（只认 csproj 的 Version，避免各处字符串写歪）。</summary>
    public static string VersionText
    {
        get
        {
            try
            {
                var asm = typeof(AppConfig).Assembly;
                var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                if (!string.IsNullOrWhiteSpace(info?.InformationalVersion))
                    return info!.InformationalVersion.Split(' ')[0];
                var v = asm.GetName().Version;
                if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { /* 回退 */ }
            return "1.0.0";
        }
    }

    // 输入 / 输出
    public string SourceDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public bool CopyMode { get; set; }
    public bool Overwrite { get; set; }
    public bool Recursive { get; set; } = true;
    public bool IncludeHidden { get; set; }
    public bool DryRun { get; set; } = true;     // 默认预览，安全第一
    public bool Resume { get; set; }
    public string ReportPath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public bool LogToFile { get; set; } = true;
    public bool Verbose { get; set; }
    public int FileLimit { get; set; }        // >0 时只处理前 N 个文件，便于试跑
    // 权限（2.3.1）
    public bool RunAsAdmin { get; set; } = true;   // 默认以管理员身份启动（需要与管理员权限的浏览器交互）
    // 更新检查（1.0）
    public bool CheckUpdateOnStartup { get; set; } = true;  // 启动时静默检查 GitHub 新版本
    public string LastUpdateCheck { get; set; } = "";       // 上次检查时间（避免频繁请求）

    // pixiv / HTTP
    public string Cookie { get; set; } = "";
    public string Proxy { get; set; } = "";
    public bool UseSystemProxy { get; set; } = false; // 使用系统代理（自动检测）
    // 代理获取方式（2.3.1）：auto=自动侦测（默认）/ explicit=手动指定 / system=系统代理 / direct=直连
    public string ProxyMode { get; set; } = "auto";
    public bool ProxyAutoDetect { get; set; } = true;   // 兼容旧开关：等价于 ProxyMode=auto
    public int ProxyVerifyTimeoutMs { get; set; } = 6000;
    public string PixivApiBase { get; set; } = "https://www.pixiv.net"; // 高级：可改为镜像/本地调试地址
    public string PixivWebBase { get; set; } = "https://www.pixiv.net";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public int Threads { get; set; } = 3;
    public int PixivDelayMs { get; set; } = 1200;
    public int TimeoutMs { get; set; } = 30000;
    public int Retries { get; set; } = 3;
    public bool HtmlFallback { get; set; } = true;
    public bool AggressivePid { get; set; }
    public bool CacheEnabled { get; set; } = true;
    public int CacheDays { get; set; } = 30;
    public string CacheDir { get; set; } = "";

    // 命名 / 分类
    public string Template { get; set; } = "{author} - {title} ({pid})";
    public string Organize { get; set; } = "author";
    public bool SplitR18 { get; set; }
    public string R18Folder { get; set; } = "R-18";
    public string UnknownAuthor { get; set; } = "未知作者";
    public string UnknownTitle { get; set; } = "未命名";
    public int MaxNameLen { get; set; } = 150;
    // 未识别文件单独分类（2.3）
    public bool MoveUnresolvedToFolder { get; set; } = true;   // 未识别文件移入/复制到「未识别」文件夹
    public string UnresolvedFolderName { get; set; } = "未识别";

    // 识图
    public bool ReverseEnabled { get; set; } = true;
    public string ReverseOrder { get; set; } = "saucenao,ascii2d";
    public string SauceNaoApiKey { get; set; } = "";
    public int SauceNaoDelayMs { get; set; } = 8000;
    public int Ascii2dDelayMs { get; set; } = 5000;
    public double MinSimilarity { get; set; } = 60;
    public int MaxUploadKB { get; set; } = 2048;
    // 识图增强（2.2）
    public string SauceNaoApiBase { get; set; } = "https://saucenao.com"; // 高级：可指向自建中转/镜像
    public long SauceNaoDbMask { get; set; }                              // 0 = 用 db=999 全库；可为 pixiv|pixivhistorical = 96
    public bool SauceNaoTestMode { get; set; }                            // 只验证连通性，不消耗配额
    public bool VerifyByHash { get; set; } = true;                        // 中置信度候选做 dHash 复核
    public bool GroupByPerceptualHash { get; set; } = true;               // 近似重复图只查一次识图
    public int HashMaxDistance { get; set; } = 10;                        // dHash 汉明距离上限（64 位）
    public bool ReverseWhenPixivDown { get; set; } = true;                // pixiv 不通时仍尝试识图
    // 识图请求缓冲（2.3）：排队 + 并发上限 + 限流冷却 + 失败退避重试
    public bool ReverseQueueEnabled { get; set; } = true;                 // 识图请求走统一队列（缓冲）
    public int ReverseConcurrency { get; set; } = 1;                      // 识图并发上限（1 = 完全串行）
    public int ReverseCooldownMs { get; set; } = 12000;                   // 被限流后的基础冷却时间
    public int ReverseMaxCooldownMs { get; set; } = 120000;               // 冷却上限
    public int ReverseMaxRetries { get; set; } = 2;                       // 每个文件识图失败后的重试次数
    public int ReverseTimeoutMs { get; set; } = 90000;                    // 单次识图调用上限（含退避等待）

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                return cfg;
            }
        }
        catch { /* 损坏的配置回退默认值 */ }
        return new AppConfig();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions), new System.Text.UTF8Encoding(true));
    }

    public void Finalize(string baseDir)
    {
        // 代理模式：老配置只有 UseSystemProxy / Proxy 两个字段，这里做一次迁移。
        // 注意：已保存过 ProxyMode 的配置以它为准，避免每次 Finalize 把 auto 改成 system。
        ProxyMode = (ProxyMode ?? "").Trim().ToLowerInvariant();
        if (ProxyMode.Length == 0)
            ProxyMode = UseSystemProxy ? "system" : (ProxyAutoDetect ? "auto" : "explicit");
        if (ProxyMode is not ("auto" or "explicit" or "system" or "direct"))
            ProxyMode = "auto";

        if (Threads < 1) Threads = 1;
        if (Threads > 16) Threads = 16;
        if (Retries < 1) Retries = 1;
        if (Retries > 8) Retries = 8;
        if (PixivDelayMs < 0) PixivDelayMs = 0;
        if (TimeoutMs < 5000) TimeoutMs = 5000;
        if (TimeoutMs > 600000) TimeoutMs = 600000;
        if (CacheDays < 0) CacheDays = 0;
        if (FileLimit < 0) FileLimit = 0;
        if (MaxNameLen < 20) MaxNameLen = 20;
        if (MaxNameLen > 200) MaxNameLen = 200;
        if (MinSimilarity < 0) MinSimilarity = 0;
        if (MinSimilarity > 100) MinSimilarity = 100;
        if (MaxUploadKB < 64) MaxUploadKB = 64;
        if (HashMaxDistance < 0) HashMaxDistance = 0;
        if (HashMaxDistance > 20) HashMaxDistance = 20;
        if (ReverseConcurrency < 1) ReverseConcurrency = 1;
        if (ReverseConcurrency > 8) ReverseConcurrency = 8;
        if (ReverseCooldownMs < 1000) ReverseCooldownMs = 1000;
        if (ReverseCooldownMs > 600000) ReverseCooldownMs = 600000;
        if (ReverseMaxCooldownMs < ReverseCooldownMs) ReverseMaxCooldownMs = ReverseCooldownMs;
        if (ReverseMaxCooldownMs > 900000) ReverseMaxCooldownMs = 900000;
        if (ReverseMaxRetries < 0) ReverseMaxRetries = 0;
        if (ReverseMaxRetries > 8) ReverseMaxRetries = 8;
        if (ReverseTimeoutMs < 10000) ReverseTimeoutMs = 10000;
        if (ReverseTimeoutMs > 1800000) ReverseTimeoutMs = 1800000;
        if (SauceNaoDelayMs < 0) SauceNaoDelayMs = 0;
        if (Ascii2dDelayMs < 0) Ascii2dDelayMs = 0;
        UnresolvedFolderName = TextUtils.SanitizeFileName(
            string.IsNullOrWhiteSpace(UnresolvedFolderName) ? "未识别" : UnresolvedFolderName.Trim(), 64);
        if (SauceNaoDbMask < 0) SauceNaoDbMask = 0;
        // 代理允许只写 host:port（实测 WebProxy 能接受），但补上 http:// 对用户更直观也更稳。
        // 注意：非法地址必须原样留下，交给 ProxyResolver 报出"你填的地址无法识别"，
        // 不能在这里悄悄清空——那样用户只会看到"连不上"，却不知道是自己填错了。
        var rawProxy = (Proxy ?? "").Trim();
        if (rawProxy.Length > 0)
        {
            var normalized = NormalizeProxy(rawProxy);
            Proxy = normalized.Length > 0 ? normalized : rawProxy;
        }
        SauceNaoApiBase = string.IsNullOrWhiteSpace(SauceNaoApiBase) ? "https://saucenao.com" : SauceNaoApiBase.TrimEnd('/');

        PixivApiBase = string.IsNullOrWhiteSpace(PixivApiBase) ? "https://www.pixiv.net" : PixivApiBase.TrimEnd('/');
        PixivWebBase = string.IsNullOrWhiteSpace(PixivWebBase) ? "https://www.pixiv.net" : PixivWebBase.TrimEnd('/');
        Template = string.IsNullOrWhiteSpace(Template) ? "{author} - {title} ({pid})" : Template;
        Organize = (Organize ?? "author").Trim().ToLowerInvariant();
        if (Organize is not ("none" or "author" or "date" or "author_date" or "date_author"))
            Organize = "author";

        if (!string.IsNullOrWhiteSpace(SourceDir) && !Path.IsPathRooted(SourceDir))
            SourceDir = Path.GetFullPath(SourceDir);

        OutputDir = Resolve(baseDir, OutputDir);
        CacheDir = Resolve(baseDir, CacheDir);
        CacheDir = string.IsNullOrEmpty(CacheDir) ? Path.Combine(baseDir, "cache") : CacheDir;
        LogPath = Resolve(baseDir, LogPath);
        LogPath = string.IsNullOrEmpty(LogPath) ? Path.Combine(baseDir, "Logs", "PixivRenamer.log") : LogPath;

        ReportPath = Resolve(baseDir, ReportPath);
        if (string.IsNullOrEmpty(ReportPath))
        {
            ReportPath = string.IsNullOrEmpty(SourceDir)
                ? Path.Combine(baseDir, "_pixiv_rename_report.csv")
                : Path.Combine(SourceDir, "_pixiv_rename_report.csv");
        }
    }

    private static string Resolve(string baseDir, string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        return Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
    }

    /// <summary>
    /// exe 所在的真实目录。
    /// 单文件发布时 AppContext.BaseDirectory 指向 %TEMP% 里的解包目录，而配置/日志
    /// 应当跟着 exe 走（自提权重启前也必须取到同一个目录），所以用进程主模块路径。
    /// </summary>
    public static string ExeDirectory()
    {
        try
        {
            var dir = Path.GetDirectoryName(Elevation.ExecutablePath());
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir!;
        }
        catch { /* 回退 */ }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 代理规范化：用户常写 "127.0.0.1:7897" 或 "socks5://..."。
    /// 没写协议时补 http://（HttpClient 的 HTTP 代理也支持 https CONNECT），
    /// 同时挡住会导致 WebProxy 抛异常的写法，避免整个任务因为一个笔误失败。
    /// </summary>
    public static string NormalizeProxy(string? proxy)
    {
        var p = (proxy ?? "").Trim();
        if (p.Length == 0) return "";
        if (!p.Contains("://")) p = "http://" + p;
        if (!Uri.TryCreate(p, UriKind.Absolute, out var uri)) return "";
        if (uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5")) return "";

        // 只接受 host 或 host:port：拒绝 "http://这不是一个地址"、带路径或空主机这类写法
        if (string.IsNullOrWhiteSpace(uri.Host)) return "";
        var host = uri.Host;
        if (host.Any(ch => ch > 127)) return "";
        if (uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0) return "";
        if (!uri.IsDefaultPort && (uri.Port < 1 || uri.Port > 65535)) return "";
        return p;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}