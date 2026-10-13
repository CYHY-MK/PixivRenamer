using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixivRenamer.Core;

/// <summary>pixiv 作品元数据查询：AJAX 优先、HTML 兜底、内存 + 磁盘缓存。</summary>
public sealed class PixivClient
{
    private readonly HttpService _http;
    private readonly AppConfig _cfg;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, PixivMeta> _memory = new();
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly object _cacheLock = new();

    public PixivClient(HttpService http, AppConfig cfg, Action<string>? log = null)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public async Task<PixivMeta> FetchByPidAsync(string pid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pid)) return new PixivMeta { Source = "invalid" };
        if (_memory.TryGetValue(pid, out var cached)) return cached.Clone();
        if (_failed.ContainsKey(pid)) return new PixivMeta { Pid = pid, Source = "failed-cache", Note = "本次运行中已确认失败" };

        var disk = TryLoadDiskCache(pid);
        if (disk != null)
        {
            _memory[pid] = disk;
            return disk.Clone();
        }

        var meta = await FetchAjaxAsync(pid, ct).ConfigureAwait(false);
        if (!meta.HasIdentity && _cfg.HtmlFallback)
        {
            var html = await FetchHtmlAsync(pid, ct).ConfigureAwait(false);
            if (html.HasIdentity) meta = html;
        }

        if (meta.HasIdentity)
        {
            _memory[pid] = meta;
            SaveDiskCache(pid, meta);
            return meta.Clone();
        }

        _failed.TryAdd(pid, 1);
        _memory[pid] = meta;
        return meta.Clone();
    }

    private async Task<PixivMeta> FetchAjaxAsync(string pid, CancellationToken ct)
    {
        var meta = new PixivMeta { Pid = pid };
        var url = $"{_cfg.PixivApiBase}/ajax/illust/{pid}";
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = $"{_cfg.PixivWebBase}/artworks/{pid}",
            ["Accept"] = "application/json, text/plain, */*",
        };
        var resp = await _http.GetAsync(url, "www.pixiv.net", headers, ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            meta.Source = "http-error";
            meta.Note = resp.TransportOk ? $"HTTP {resp.Status}" : resp.Error;
            return meta;
        }
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            {
                meta.Source = "ajax-error";
                meta.Note = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                return meta;
            }
            if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                meta.Source = "ajax-empty";
                return meta;
            }
            meta.Title = GetString(body, "title");
            meta.Author = GetString(body, "userName");
            meta.UserId = GetString(body, "userId");
            meta.Account = GetString(body, "userAccount");
            meta.PageCount = Math.Max(1, GetInt(body, "pageCount", 1));
            meta.XRestrict = GetInt(body, "xRestrict", 0);
            meta.Date = FirstNonEmpty(GetString(body, "uploadDate"), GetString(body, "createDate"));
            if (meta.Date.Length >= 10) meta.Date = meta.Date[..10];
            meta.Tags = ExtractTags(body);
            meta.R18 = meta.XRestrict > 0 || meta.Tags.Contains("R-18", StringComparison.OrdinalIgnoreCase) ||
                       meta.Tags.Contains("R18", StringComparison.OrdinalIgnoreCase);
            meta.Source = "pixiv-ajax";
            if (string.IsNullOrWhiteSpace(meta.Author) && !string.IsNullOrWhiteSpace(meta.UserId))
            {
                var user = await ResolveUserAsync(meta.UserId, ct).ConfigureAwait(false);
                if (user.HasIdentity) { meta.Author = user.Author; meta.Account = user.Account; }
            }
            return meta;
        }
        catch (Exception ex)
        {
            meta.Source = "bad-json";
            meta.Note = ex.Message;
            return meta;
        }
    }

    private async Task<PixivMeta> FetchHtmlAsync(string pid, CancellationToken ct)
    {
        var meta = new PixivMeta { Pid = pid, Source = "pixiv-html" };
        var url = $"{_cfg.PixivWebBase}/artworks/{pid}";
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        };
        var resp = await _http.GetAsync(url, "www.pixiv.net", headers, ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            meta.Note = resp.TransportOk ? $"HTTP {resp.Status}" : resp.Error;
            return meta;
        }
        var html = resp.Body;
        if (!html.Contains(pid, StringComparison.Ordinal))
        {
            meta.Note = "HTML 中未出现目标 PID";
            return meta;
        }
        meta.Author = ExtractJsonString(html, "userName");
        meta.Account = ExtractJsonString(html, "userAccount");
        meta.UserId = ExtractJsonString(html, "userId");
        meta.Title = ExtractJsonString(html, "title");
        var page = ExtractJsonInt(html, "pageCount");
        if (page > 0) meta.PageCount = page;
        var xr = ExtractJsonInt(html, "xRestrict");
        if (xr >= 0) meta.XRestrict = xr;

        var og = FindMetaContent(html, "og:title");
        if (string.IsNullOrWhiteSpace(og)) og = FindTagText(html, "title");
        if (!string.IsNullOrWhiteSpace(og))
        {
            var (t, a) = SplitTitleAndAuthor(og);
            if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = t;
            if (string.IsNullOrWhiteSpace(meta.Author)) meta.Author = a;
        }
        var dateMatch = Regex.Match(html, "\"(?:uploadDate|createDate)\"\\s*:\\s*\"([^\"]{10,})");
        if (dateMatch.Success) meta.Date = dateMatch.Groups[1].Value[..10];
        meta.R18 = meta.XRestrict > 0 || html.Contains("R-18", StringComparison.OrdinalIgnoreCase);
        return meta;
    }

    private async Task<PixivMeta> ResolveUserAsync(string userId, CancellationToken ct)
    {
        var meta = new PixivMeta();
        try
        {
            var resp = await _http.GetAsync($"{_cfg.PixivApiBase}/ajax/user/{userId}", "www.pixiv.net",
                new Dictionary<string, string> { ["Accept"] = "application/json" }, ct).ConfigureAwait(false);
            if (!resp.Ok) return meta;
            using var doc = JsonDocument.Parse(resp.Body);
            if (!doc.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return meta;
            meta.Author = GetString(body, "name");
            meta.Account = GetString(body, "account");
        }
        catch { }
        return meta;
    }

    private PixivMeta? TryLoadDiskCache(string pid)
    {
        if (!_cfg.CacheEnabled) return null;
        try
        {
            var path = Path.Combine(_cfg.CacheDir, $"illust_{pid}.json");
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(Math.Max(1, _cfg.CacheDays))) return null;
            var json = File.ReadAllText(path);
            var meta = JsonSerializer.Deserialize<PixivMeta>(json);
            if (meta == null || !meta.HasIdentity) return null;
            meta.Pid = string.IsNullOrWhiteSpace(meta.Pid) ? pid : meta.Pid;
            meta.Source = "cache";
            meta.Date = string.IsNullOrWhiteSpace(meta.Date) ? "" : meta.Date;
            meta.R18 = meta.XRestrict > 0 || meta.Tags.Contains("R-18", StringComparison.OrdinalIgnoreCase);
            return meta;
        }
        catch { return null; }
    }

    private void SaveDiskCache(string pid, PixivMeta meta)
    {
        if (!_cfg.CacheEnabled) return;
        try
        {
            Directory.CreateDirectory(_cfg.CacheDir);
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(Path.Combine(_cfg.CacheDir, $"illust_{pid}.json"), json);
        }
        catch { }
    }

    private static string ExtractTags(JsonElement body)
    {
        if (!body.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return "";
        if (!tags.TryGetProperty("tags", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            var t = GetString(item, "tag");
            if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
        }
        return string.Join("; ", list);
    }

    private static string GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static int GetInt(JsonElement obj, string name, int def)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : def;

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string ExtractJsonString(string html, string key)
    {
        var m = Regex.Match(html, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? UnescapeJson(m.Groups[1].Value) : "";
    }

    private static int ExtractJsonInt(string html, string key)
    {
        var m = Regex.Match(html, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
    }

    private static string UnescapeJson(string s)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? ""; }
        catch { return s; }
    }

    private static string FindMetaContent(string html, string name)
    {
        var idx = 0;
        while ((idx = html.IndexOf("<meta", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = html.IndexOf('>', idx);
            if (end < 0) break;
            var tag = html[idx..end];
            if (tag.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(tag, "content\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
                if (m.Success) return TextUtils.HtmlDecode(m.Groups[1].Value);
            }
            idx = end + 1;
        }
        return "";
    }

    private static string FindTagText(string html, string tag)
    {
        var m = Regex.Match(html, "<" + tag + "[^>]*>([\\s\\S]*?)</" + tag + ">", RegexOptions.IgnoreCase);
        return m.Success ? TextUtils.HtmlDecode(m.Groups[1].Value.Trim()) : "";
    }

    private static (string Title, string Author) SplitTitleAndAuthor(string raw)
    {
        var s = raw.Trim().Replace("｜", " - ").Replace(" | ", " - ");
        if (s.EndsWith("pixiv", StringComparison.OrdinalIgnoreCase))
            s = s[..^5].Trim().TrimEnd('-', '|', ' ');
        var parts = s.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var author = parts[1].Trim();
            foreach (var suffix in new[] { "のイラスト", "の作品", "の小説", "のマンガ", "のうごくイラスト" })
                if (author.EndsWith(suffix, StringComparison.Ordinal)) author = author[..^suffix.Length].Trim();
            return (parts[0].Trim(), author);
        }
        return (s.Trim(), "");
    }
}