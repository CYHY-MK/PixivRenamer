using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixivRenamer.Core;

/// <summary>
/// SauceNAO / ascii2d 识图回退。
/// SauceNAO 结果按库索引优先级 + 置信度排序：pixiv(#5) / pixivhistorical(#6) 优先，
/// 其次是 Twitter / Pawoo / Skeb 等画师常发源站，最后才是 booru 转载站。
/// 索引号来自官方 https://saucenao.com/tools/examples/api/index_details.txt
/// </summary>
public sealed class ReverseSearchService
{
    private readonly HttpService _http;
    private readonly AppConfig _cfg;
    private readonly Action<string>? _log;
    private readonly ReverseThrottle? _throttle;

    public ReverseSearchService(HttpService http, AppConfig cfg, Action<string>? log = null, ReverseThrottle? throttle = null)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
        _throttle = throttle;
    }

    /// <summary>上一次实际发起请求的识图服务（未识别时写进报告，便于排查）。</summary>
    public string LastProvider { get; private set; } = "";

    private static bool IsRejected(HttpResult r)
        => r.TransportOk && (r.Status is 401 or 402 or 403 || r.Status == 429 || r.Status >= 500);

    /// <summary>命中"匿名账号不允许调用 API"这类拒绝提示：再重试也没有意义。</summary>
    private static bool IsPermanentRejection(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (body.Contains("anonymous account type does not permit", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("api key is required", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>SauceNAO 库索引优先级，越小越可信。</summary>
    public static int IndexRank(int indexId) => indexId switch
    {
        5 => 0,    // pixiv
        6 => 1,    // pixivhistorical（已删除 / 早期作品，能救回一批）
        41 => 2,   // Twitter
        35 => 3,   // Pawoo
        44 => 4,   // Skeb
        11 => 5,   // Nijie
        8 => 6,    // Nico Nico Seiga
        9 => 7,    // Danbooru
        25 => 8,   // Gelbooru
        26 => 9,   // Konachan
        27 => 10,  // Sankaku
        12 => 11,  // Yande.re
        28 => 12,  // Anime-Pictures
        43 => 13,  // Kemono
        34 => 14,  // deviantArt
        39 => 15,  // ArtStation
        20 => 16,  // MediBang
        19 => 17,  // 2D-Market
        31 => 18,  // bcy.net Illust
        _ => 99,
    };

    public async Task<List<ReverseResult>> SearchAsync(string file, CancellationToken ct)
    {
        var all = new List<ReverseResult>();
        if (!_cfg.ReverseEnabled) return all;

        var order = (_cfg.ReverseOrder ?? "saucenao,ascii2d")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();
        if (order.Count == 0) order = new List<string> { "saucenao", "ascii2d" };

        // 一次识图调用只交给一个服务：被限流时整组重试同一个服务，
        // 而不是把半截结果再丢给下一个服务（那样既费配额又容易拿到低质量候选）。
        var start = 0;
        var lastUsed = LastProvider;
        if (!string.IsNullOrEmpty(lastUsed))
        {
            var idx = order.IndexOf(lastUsed.ToLowerInvariant());
            if (idx > 0) start = idx;
        }

        for (var i = start; i < order.Count; i++)
        {
            var provider = order[i];
            LastProvider = provider;
            List<ReverseResult> results;
            try
            {
                if (provider == "saucenao") results = await RunSauceNaoAsync(file, ct).ConfigureAwait(false);
                else if (provider == "ascii2d") results = await RunAscii2dAsync(file, ct).ConfigureAwait(false);
                else { _log?.Invoke($"未知识图服务：{provider}"); continue; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Invoke($"{provider} 识图异常：{ex.Message}");
                continue;
            }

            _throttle?.Succeed();
            all.AddRange(results);
            if (results.Any(r => r.HasPixiv)) break; // 找到 pixiv 作品链接就不再问后面的引擎
        }

        return all
            .OrderByDescending(r => r.HasPixiv)
            .ThenBy(r => IndexRank(r.IndexId))
            .ThenByDescending(r => r.Similarity)
            .ToList();
    }

    /// <summary>
    /// 带缓冲的重试：只有"限流 / 服务端临时错误 / 网络失败"才重试，
    /// API Key 无效之类的永久性拒绝直接放弃。重试期间依赖 ReverseThrottle 的全局冷却。
    /// </summary>
    public async Task<List<ReverseResult>> SearchWithRetryAsync(string file, CancellationToken ct)
    {
        var attempts = Math.Max(1, _cfg.ReverseMaxRetries + 1);
        List<ReverseResult> last = new();
        for (var attempt = 1; ; attempt++)
        {
            last = await SearchAsync(file, ct).ConfigureAwait(false);
            if (last.Count > 0 || attempt >= attempts) return last;

            if (_throttle == null || !_throttle.Cooling)
            {
                // 没有限流信号时也做一点小退避，避免瞬时抖动直接判定为未识别
                var backoff = Math.Min(15000, 2000 * (1 << Math.Min(attempt - 1, 3)));
                _log?.Invoke($"识图没有结果，{backoff / 1000.0:0.#} 秒后重试（{attempt + 1}/{attempts}）");
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            else
            {
                _log?.Invoke($"识图被限流，等待冷却后重试（{attempt + 1}/{attempts}）");
                await _throttle.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<ReverseResult>> RunSauceNaoAsync(string file, CancellationToken ct)
    {
        var result = new List<ReverseResult>();
        var limit = (long)Math.Max(256, _cfg.MaxUploadKB) * 1024;
        var upload = await PrepareUploadAsync(file, limit, ct).ConfigureAwait(false);
        if (upload == null) return result;

        var fields = new List<FormField>();
        if (!string.IsNullOrWhiteSpace(_cfg.SauceNaoApiKey))
            fields.Add(new FormField { Name = "api_key", Data = System.Text.Encoding.UTF8.GetBytes(_cfg.SauceNaoApiKey) });
        fields.Add(new FormField { Name = "output_type", Data = System.Text.Encoding.UTF8.GetBytes("2") });
        fields.Add(new FormField { Name = "db", Data = System.Text.Encoding.UTF8.GetBytes("999") });
        fields.Add(new FormField { Name = "numres", Data = System.Text.Encoding.UTF8.GetBytes("10") });
        fields.Add(new FormField { Name = "hide", Data = System.Text.Encoding.UTF8.GetBytes("0") });
        fields.Add(new FormField { Name = "minsim", Data = System.Text.Encoding.UTF8.GetBytes("30") });
        if (_cfg.SauceNaoDbMask > 0)
            fields.Add(new FormField { Name = "dbmask", Data = System.Text.Encoding.UTF8.GetBytes(_cfg.SauceNaoDbMask.ToString()) });
        if (_cfg.SauceNaoTestMode)
            fields.Add(new FormField { Name = "testmode", Data = System.Text.Encoding.UTF8.GetBytes("1") });
        fields.Add(new FormField { Name = "file", FileName = upload.Value.FileName, ContentType = upload.Value.Mime, Data = upload.Value.Data });

        var endpoint = _cfg.SauceNaoApiBase.TrimEnd('/') + "/search.php";
        var resp = await _http.PostMultipartAsync(endpoint, "saucenao.com", fields, null, ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            _log?.Invoke(resp.TransportOk ? $"SauceNAO 返回 HTTP {resp.Status}" : $"SauceNAO 请求失败：{resp.Error}");
            if (IsRejected(resp))
                _throttle?.Block(resp.RetryAfterSeconds, $"SauceNAO HTTP {resp.Status}");
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            var root = doc.RootElement;

            // 服务端状态：-1 通常是"匿名账号不允许调用 API"（需要填 API Key）
            if (root.TryGetProperty("header", out var top) && top.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() != 0)
            {
                var msg = top.TryGetProperty("message", out var m) ? m.GetString() : null;
                var permanent = IsPermanentRejection(msg ?? "");
                _log?.Invoke($"SauceNAO 拒绝请求（status={st.GetInt32()}）：{msg}" +
                             (string.IsNullOrWhiteSpace(_cfg.SauceNaoApiKey) ? "　 请在pixiv / 识图页填写 SauceNAO API Key。" : ""));
                // 配额耗尽 / 被限流时进入冷却；匿名拒绝这种永久性错误不冷却（重试也没用）
                if (!permanent)
                    _throttle?.Block(0, $"SauceNAO status={st.GetInt32()}");
                return result;
            }
            if (root.TryGetProperty("header", out var hdr))
            {
                var shortLeft = GetString(hdr, "short_remaining");
                var longLeft = GetString(hdr, "long_remaining");
                if (!string.IsNullOrWhiteSpace(shortLeft) || !string.IsNullOrWhiteSpace(longLeft))
                    _log?.Invoke($"SauceNAO 剩余配额：短周期 {shortLeft}，今日 {longLeft}");
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in results.EnumerateArray())
            {
                var header = item.TryGetProperty("header", out var h) ? h : default;
                var data = item.TryGetProperty("data", out var d) ? d : default;
                if (data.ValueKind != JsonValueKind.Object) continue;
                var rr = new ReverseResult
                {
                    Provider = "SauceNAO",
                    Similarity = ParseDouble(GetString(header, "similarity")),
                    PixivId = GetScalar(data, "pixiv_id"),
                    UserId = GetScalar(data, "member_id"),
                    Author = GetString(data, "member_name"),
                    Title = GetString(data, "title"),
                    IndexName = GetString(header, "index_name"),
                    ThumbnailUrl = GetString(header, "thumbnail"),
                };
                if (header.ValueKind == JsonValueKind.Object && header.TryGetProperty("index_id", out var idx) && idx.TryGetInt32(out var indexId))
                    rr.IndexId = indexId;

                if (data.TryGetProperty("ext_urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                {
                    var first = urls.EnumerateArray().FirstOrDefault();
                    rr.Url = first.ValueKind == JsonValueKind.String ? first.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(rr.PixivId))
                    {
                        foreach (var u in urls.EnumerateArray())
                        {
                            var id = ExtractPixivId(u.GetString() ?? "");
                            if (!string.IsNullOrEmpty(id)) { rr.PixivId = id; break; }
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(rr.PixivId) && !string.IsNullOrWhiteSpace(rr.Url))
                    rr.PixivId = ExtractPixivId(rr.Url);
                if (rr.Similarity + 0.0001 < _cfg.MinSimilarity) continue;
                if (!rr.HasPixiv && !rr.HasIdentity) continue;
                result.Add(rr);
            }
        }
        catch (Exception ex) { _log?.Invoke("SauceNAO 返回解析失败：" + ex.Message); }
        _log?.Invoke($"SauceNAO 返回 {result.Count} 条可用结果");
        return result;
    }

    private async Task<List<ReverseResult>> RunAscii2dAsync(string file, CancellationToken ct)
    {
        var result = new List<ReverseResult>();
        var upload = await PrepareUploadAsync(file, 4L * 1024 * 1024, ct).ConfigureAwait(false);
        if (upload == null) return result;

        var fields = new List<FormField>
        {
            new() { Name = "file", FileName = upload.Value.FileName, ContentType = upload.Value.Mime, Data = upload.Value.Data },
        };
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://ascii2d.net/",
            ["Origin"] = "https://ascii2d.net",
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        };
        var resp = await _http.PostMultipartAsync("https://ascii2d.net/search/file", "ascii2d.net", fields, headers, ct).ConfigureAwait(false);
        var html = resp.Body ?? "";

        // Cloudflare 人机校验：可能是 403，也可能是 200 + "Just a moment..."。
        // 不识别出来的话，用户只会看到0 条结果，误以为真的没找到来源。
        if (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"ascii2d 被 Cloudflare 人机校验拦截（HTTP {resp.Status}）：需要能通过校验的网络/代理，或换用其它识图服务，本次跳过");
            return result;
        }

        if (!resp.Ok)
        {
            _log?.Invoke(resp.TransportOk ? $"ascii2d 返回 HTTP {resp.Status}" : $"ascii2d 请求失败：{resp.Error}");
            if (IsRejected(resp))
                _throttle?.Block(resp.RetryAfterSeconds, $"ascii2d HTTP {resp.Status}");
            return result;
        }

        var seen = new HashSet<string>();
        var matches = Regex.Matches(html,
            "pixiv\\.net/(?:[a-z]{2}/)?(?:artworks/|member_illust\\.php\\?[^\"'<> ]*?illust_id=)(\\d{1,12})",
            RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var id = m.Groups[1].Value;
            if (!seen.Add(id)) continue;
            var rr = new ReverseResult
            {
                Provider = "ascii2d",
                PixivId = id,
                Url = "https://www.pixiv.net/artworks/" + id,
                // ascii2d 是"按图找原图来源"，命中即视为高置信度；不给分值时按 90 处理
                Similarity = 90,
                IndexId = 5,
                IndexName = "pixiv (ascii2d)",
            };
            var pos = m.Index + m.Length;
            var gt = html.IndexOf('>', pos);
            if (gt >= 0)
            {
                var lt = html.IndexOf('<', gt + 1);
                if (lt > gt + 1)
                {
                    var text = TextUtils.HtmlDecode(html.Substring(gt + 1, lt - gt - 1).Trim());
                    if (!string.IsNullOrWhiteSpace(text) && text.Length < 200 &&
                        !text.Contains("pixiv", StringComparison.OrdinalIgnoreCase))
                        rr.Title = text;
                }
            }
            result.Add(rr);
        }
        _log?.Invoke($"ascii2d 返回 {result.Count} 条可用结果");
        return result;
    }

    private async Task<(byte[] Data, string FileName, string Mime, string Note)?> PrepareUploadAsync(string file, long limit, CancellationToken ct)
    {
        long size;
        try { size = new FileInfo(file).Length; } catch { return null; }

        if (size > 0 && size <= limit)
        {
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            return (bytes, Path.GetFileName(file), TextUtils.GuessMime(file), "原图上传");
        }

        if (Thumbnailer.TryCreateJpegThumbnail(file, 1600, 88, out var thumb, out var name, out var terr) && thumb.Length > 0)
            return (thumb, name, "image/jpeg", "已压缩后上传");

        if (size > 0 && size <= limit * 4 && size <= 12L * 1024 * 1024)
        {
            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            return (bytes, Path.GetFileName(file), TextUtils.GuessMime(file), "压缩不可用，尝试原图上传");
        }

        _log?.Invoke($"文件过大或无法读取，跳过识图：{Path.GetFileName(file)}（{terr}）");
        return null;
    }

    private static string ExtractPixivId(string url)
    {
        var m = Regex.Match(url, "pixiv\\.net/(?:[a-z]{2}/)?(?:artworks/|member_illust\\.php\\?[^\"'<> ]*?illust_id=)(\\d{1,12})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static string GetScalar(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" :
               v.ValueKind == JsonValueKind.Number ? v.ToString() : "") : "";

    private static double ParseDouble(string s) => double.TryParse(s, out var v) ? v : 0;
}