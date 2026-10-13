namespace PixivRenamer.Core;

/// <summary>
/// 项目身份信息（作者 / 仓库 / 许可 / 声明）。
/// 界面「关于」、检查更新、诊断报告都从这里取，改一处即可。
/// </summary>
public static class AppInfo
{
    public const string RepositoryOwner = "CYHY-MK";
    public const string RepositoryName = "PixivRenamer";
    public const string RepositoryUrl = "https://github.com/CYHY-MK/PixivRenamer";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/CYHY-MK/PixivRenamer/releases/latest";
    public const string TagsApi = "https://api.github.com/repos/CYHY-MK/PixivRenamer/tags";
    public const string CommitsApi = "https://api.github.com/repos/CYHY-MK/PixivRenamer/commits?per_page=1";
    /// <summary>Atom feed：GitHub 的 feed 不计入 API 匿名限额，用代理的用户也不会被 403 卡住。</summary>
    public const string ReleasesAtom = RepositoryUrl + "/releases.atom";
    public const string TagsAtom = RepositoryUrl + "/tags.atom";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string Author = "CYHY-MK";
    public const string AuthorUrl = "https://github.com/CYHY-MK";
    public const string License = "MIT";
    public const string LicenseUrl = RepositoryUrl + "/blob/master/LICENSE";
    public const string ProductName = "PixivRenamer";

    /// <summary>一句话定位。</summary>
    public const string Tagline = "pixiv 插画批量重命名 / 分类整理工具";

    /// <summary>免责声明（软件内与 README 使用同一份文字）。</summary>
    public const string Disclaimer =
        "本工具仅用于整理你自己合法下载、自己有权处理的图片。\n" +
        "请遵守 pixiv、SauceNAO、ascii2d 等网站的服务条款，控制请求频率，\n" +
        "不要用于大规模爬取、商业用途或任何侵犯版权的行为。\n" +
        "使用本工具产生的一切后果由使用者自行承担。";

    /// <summary>功能简介（关于对话框用）。</summary>
    public const string Features =
        "· PID 提取：从文件名 / 父目录识别 pixiv 作品号（支持 7~9 位裸数字，自动排除日期）\n" +
        "· pixiv 查询：AJAX 接口 + HTML 兜底，支持 Cookie / R-18，本地查询缓存\n" +
        "· 识图回退：SauceNAO / ascii2d，库索引优先 + dHash 复核 + 近似重复合并\n" +
        "· 请求缓冲：识图排队送出、限流自动冷却、失败退避重试\n" +
        "· 命名分类：自定义模板、按作者 / 日期分类、R-18 单独文件夹\n" +
        "· 未识别归类：认不出作者标题的图片按原名收进「未识别」文件夹并记录原因\n" +
        "· 代理侦测：自动识别本机 Clash / v2rayN 等代理端口，失败生成网络诊断报告\n" +
        "· 报告与回滚：CSV 报告、断点续跑、--undo 撤销";
}
