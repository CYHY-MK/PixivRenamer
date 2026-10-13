using System.Net.Http;
using System.Text.Json;

namespace PixivRenamer.Core;

/// <summary>检查更新的结果。</summary>
public sealed class UpdateCheckResult
{
    public bool Ok { get; set; }                 // 检查动作本身是否成功
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string ReleaseUrl { get; set; } = AppInfo.ReleasesUrl;
    public string DownloadUrl { get; set; } = "";
    public string Notes { get; set; } = "";      // 更新说明 / 最近提交
    public string PublishedAt { get; set; } = "";
    public string Error { get; set; } = "";
    public bool IsPrerelease { get; set; }

    public string Describe() => Ok
        ? (HasUpdate
            ? $"发现新版本 {LatestVersion}（当前 {CurrentVersion}）"
            : $"已是最新版本（{CurrentVersion}）")
        : ("检查更新失败：" + Error);
}

/// <summary>
/// 检查 GitHub 更新。
///
/// 发布方式：推送 tag（如 v1.0.1）尽量同时建 Release（`gh release create` / 网页发布均可）。
/// 这里先查最新 Release；仓库还没有 Release 时退化为查 tag 与最新提交，
/// 保证"刚建仓库、还没发 Release"时也能给出可读结果，而不是一句干巴巴的失败。
/// </summary>
public sealed class UpdateService
{
    private readonly string _userAgent = AppInfo.ProductName + "/" + AppConfig.VersionText;

    /// <summary>
    /// 查询 GitHub 上的最新版本。
    /// 先走 releases.atom（不计 API 限额，用代理时不会被 403 卡住），
    /// 失败再退到 REST API（更详细，但匿名额度是按 IP 算的，代理出口 IP 常被打满）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(AppConfig? cfg = null, CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = AppConfig.VersionText };
        var errors = new List<string>();
        try
        {
            using var http = BuildClient(cfg);

            // 1) Atom feed（主通道）
            var (atomStatus, atomBody) = await GetStringAsync(http, AppInfo.ReleasesAtom, ct).ConfigureAwait(false);
            if (atomStatus == 200 && ParseAtom(atomBody, result))
            {
                result.Ok = true;
                return result;
            }
            if (atomStatus != 200) errors.Add($"releases.atom HTTP {atomStatus}");

            var (tagAtomStatus, tagAtomBody) = await GetStringAsync(http, AppInfo.TagsAtom, ct).ConfigureAwait(false);
            if (tagAtomStatus == 200 && ParseAtom(tagAtomBody, result, tagsOnly: true))
            {
                result.Ok = true;
                return result;
            }
            if (tagAtomStatus != 200) errors.Add($"tags.atom HTTP {tagAtomStatus}");

            // 2) REST API 兜底
            var (apiStatus, apiBody) = await GetStringAsync(http, AppInfo.LatestReleaseApi, ct).ConfigureAwait(false);
            if (apiStatus == 200)
            {
                ParseRelease(apiBody, result);
                result.Ok = true;
                return result;
            }
            errors.Add(apiStatus == 403
                ? "GitHub API 匿名限额已用尽（HTTP 403）"
                : $"releases API HTTP {apiStatus}");

            var (tagsStatus, tagsBody) = await GetStringAsync(http, AppInfo.TagsApi, ct).ConfigureAwait(false);
            if (tagsStatus == 200)
            {
                ParseTag(tagsBody, result);
                var (commitStatus, commitBody) = await GetStringAsync(http, AppInfo.CommitsApi, ct).ConfigureAwait(false);
                if (commitStatus == 200) result.Notes = ParseCommitMessage(commitBody);
                result.Ok = true;
                return result;
            }
            errors.Add($"tags API HTTP {tagsStatus}");

            result.Error = errors.Count > 0 ? string.Join("；", errors) : "无法获取版本信息";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Error = "已取消";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = NetworkDiagnostics.Describe(ex);
            return result;
        }
    }

    /// <summary>
    /// 解析 GitHub 的 Atom feed（releases.atom / tags.atom）。
    /// 取第一条 entry：tag 在 &lt;id&gt; 里（形如 tag:github.com,2008:Repository/123/v1.0.0），
    /// 标题与正文分别给版本名与更新说明。feed 不计 API 限额，是最稳的通道。
    /// </summary>
    public static bool ParseAtom(string xml, UpdateCheckResult result, bool tagsOnly = false)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var ns = doc.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
            var entry = doc.Root?.Elements(ns + "entry").FirstOrDefault();
            if (entry == null) return false;

            var id = entry.Element(ns + "id")?.Value ?? "";
            var title = entry.Element(ns + "title")?.Value ?? "";
            var updated = entry.Element(ns + "updated")?.Value ?? "";
            var link = entry.Elements(ns + "link")
                            .FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate")
                            ?.Attribute("href")?.Value ?? "";
            var content = entry.Element(ns + "content")?.Value ?? "";

            var version = NormalizeTag(id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id);
            if (version.Length == 0) version = NormalizeTag(title);
            if (version.Length == 0) return false;

            result.LatestVersion = version;
            result.PublishedAt = updated;
            if (link.Length > 0) result.ReleaseUrl = link;
            result.ReleaseName = tagsOnly ? ("v" + version) : title;

            var notes = AnnouncementToText(content);
            if (notes.Length == 0 && !tagsOnly) notes = title;
            if (notes.Length > 0) result.Notes = notes;

            result.HasUpdate = CompareVersions(version, result.CurrentVersion) > 0;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Atom 的 content 是转义过的 HTML，粗转成可读纯文本。</summary>
    public static string AnnouncementToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = System.Net.WebUtility.HtmlDecode(html);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</(p|div|li|h\d)>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<li[^>]*>", "· ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private HttpClient BuildClient(AppConfig? cfg)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (cfg != null)
        {
            if (cfg.ProxyMode == "direct") { handler.UseProxy = false; }
            else if (!string.IsNullOrWhiteSpace(cfg.Proxy)) { handler.Proxy = new System.Net.WebProxy(cfg.Proxy); handler.UseProxy = true; }
            else { handler.UseProxy = true; handler.Proxy = null; }
        }
        else { handler.UseProxy = true; handler.Proxy = null; }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task<(int Status, string Body)> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, body);
    }

    private void ParseRelease(string json, UpdateCheckResult result)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        result.LatestVersion = NormalizeTag(GetString(root, "tag_name"));
        result.ReleaseName = GetString(root, "name");
        result.Notes = GetString(root, "body");
        result.PublishedAt = GetString(root, "published_at");
        result.IsPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        var htmlUrl = GetString(root, "html_url");
        if (htmlUrl.Length > 0) result.ReleaseUrl = htmlUrl;

        // 优先给第一个 .zip/.exe 资源的下载地址（没有就直接开 Release 页面）
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url = GetString(asset, "browser_download_url");
                if (url.Length == 0) continue;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    result.DownloadUrl = url;
                    break;
                }
            }
        }

        result.HasUpdate = CompareVersions(result.LatestVersion, result.CurrentVersion) > 0;
    }

    private void ParseTag(string json, UpdateCheckResult result)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { result.LatestVersion = ""; return; }
        // GitHub 的 tags 接口按 "最新" 排序不完全可靠，这里取版本号最大的那个
        var best = "";
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var tag = NormalizeTag(GetString(item, "name"));
            if (tag.Length == 0) continue;
            if (best.Length == 0 || CompareVersions(tag, best) > 0) best = tag;
        }
        result.LatestVersion = best;
        result.HasUpdate = best.Length > 0 && CompareVersions(best, result.CurrentVersion) > 0;
    }

    private static string ParseCommitMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return "";
            var first = doc.RootElement[0];
            if (first.TryGetProperty("commit", out var commit) && commit.TryGetProperty("message", out var msg))
                return msg.GetString() ?? "";
        }
        catch { /* 忽略 */ }
        return "";
    }

    private static string GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>把 "v1.0.1" / "1.0.1" / "release-1.0.1-beta" 归一成 "1.0.1"。</summary>
    public static string NormalizeTag(string? tag)
    {
        var s = (tag ?? "").Trim();
        if (s.Length == 0) return "";
        var start = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsDigit(s[i])) { start = i; break; }
        }
        if (start < 0) return "";
        var end = start;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        return s[start..end].Trim('.');
    }

    /// <summary>版本号比较：1.0.10 &gt; 1.0.9 &gt; 1.0；非法输入按 0 处理。返回 &gt;0 表示 a 更新。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        var len = Math.Max(pa.Count, pb.Count);
        for (var i = 0; i < len; i++)
        {
            var va = i < pa.Count ? pa[i] : 0;
            var vb = i < pb.Count ? pb[i] : 0;
            if (va != vb) return va > vb ? 1 : -1;
        }
        return 0;
    }

    private static List<int> ParseParts(string? version)
    {
        var list = new List<int>();
        foreach (var part in (version ?? "").Split('.', '-', '+', ' '))
        {
            if (part.Length == 0) continue;
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            list.Add(int.TryParse(digits, out var v) ? v : 0);
        }
        return list;
    }
}
