using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PixivRenamer.Core;

public sealed class FormField
{
    public string Name { get; set; } = "";
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class HttpResult
{
    public bool TransportOk { get; set; }
    public int Status { get; set; }
    public byte[] Raw { get; set; } = Array.Empty<byte>();
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>服务端 Retry-After 头（秒），识图队列用它决定冷却时长。</summary>
    public int RetryAfterSeconds { get; set; }

    public bool Ok => TransportOk && Status is >= 200 and < 300;
    /// <summary>被限流/服务端过载：识图链路应进入冷却，而不是继续按间隔硬打。</summary>
    public bool IsRateLimited => TransportOk && Status is 429 or 503;
}

/// <summary>HttpClient 封装：限速、重试、Cookie 隔离、multipart 上传。</summary>
public sealed class HttpService : IDisposable
{
    private readonly HttpClient _client;
    private readonly AppConfig _cfg;
    private readonly RateLimiter _limiter = new();
    private readonly Action<string>? _log;

    public HttpService(AppConfig cfg, Action<string>? log = null)
    {
        _cfg = cfg;
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(5000, cfg.TimeoutMs)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(cfg.Proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(cfg.Proxy);
                handler.UseProxy = true;
            }
            catch (Exception ex)
            {
                // 代理地址写错不应该让整次任务失败：给出明确提示并回退到系统代理
                _log?.Invoke($"代理地址无法解析（{cfg.Proxy}）：{ex.Message}；已改为使用系统代理");
                handler.UseProxy = true;
                handler.Proxy = null;
            }
        }
        else if (cfg.UseSystemProxy)
        {
            handler.UseProxy = true;
            handler.Proxy = null;
        }
        else
        {
            handler.UseProxy = false; // 默认直连，避免系统代理/PAC 自动检测导致卡死
        }
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(5000, cfg.TimeoutMs)),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(cfg.UserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en-US;q=0.8,en;q=0.7,zh-CN;q=0.6");
    }

    public void SetInterval(string host, int milliseconds) => _limiter.SetInterval(host, milliseconds);

    public Task<HttpResult> GetAsync(string url, string rateKey, IReadOnlyDictionary<string, string>? headers, CancellationToken ct,
                                    int attemptsOverride = -1)
        => SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(req, url, headers);
            return req;
        }, rateKey, url, ct, attemptsOverride);

    public Task<HttpResult> PostMultipartAsync(string url, string rateKey, IEnumerable<FormField> fields,
                                               IReadOnlyDictionary<string, string>? headers, CancellationToken ct,
                                               int attemptsOverride = -1)
        => SendAsync(() =>
        {
            var content = new MultipartFormDataContent();
            foreach (var f in fields)
            {
                var bytes = new ByteArrayContent(f.Data);
                if (!string.IsNullOrEmpty(f.ContentType)) bytes.Headers.ContentType = new MediaTypeHeaderValue(f.ContentType);
                if (string.IsNullOrEmpty(f.FileName)) content.Add(bytes, f.Name);
                else
                {
                    var safeName = f.FileName.Replace("\"", "_").Replace("\r", "_").Replace("\n", "_");
                    content.Add(bytes, f.Name, safeName);
                }
            }
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            ApplyHeaders(req, url, headers);
            return req;
        }, rateKey, url, ct, attemptsOverride);

    private void ApplyHeaders(HttpRequestMessage req, string url, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers != null)
            foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

        var isPixiv = url.Contains("pixiv.net", StringComparison.OrdinalIgnoreCase) ||
                      url.Contains("pximg.net", StringComparison.OrdinalIgnoreCase);
        if (isPixiv && !string.IsNullOrWhiteSpace(_cfg.Cookie))
            req.Headers.TryAddWithoutValidation("Cookie", _cfg.Cookie);
    }

    private async Task<HttpResult> SendAsync(Func<HttpRequestMessage> factory, string rateKey, string url, CancellationToken ct,
                                             int attemptsOverride)
    {
        Exception? lastError = null;
        var attempts = attemptsOverride >= 0 ? Math.Max(1, attemptsOverride) : Math.Max(1, _cfg.Retries);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _limiter.WaitAsync(rateKey, ct).ConfigureAwait(false);
            try
            {
                using var req = factory();
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var raw = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var result = new HttpResult
                {
                    TransportOk = true,
                    Status = (int)resp.StatusCode,
                    Raw = raw,
                    Body = DecodeBody(raw, resp.Content.Headers.ContentType?.MediaType ?? "", url),
                    ContentType = resp.Content.Headers.ContentType?.ToString() ?? "",
                    RetryAfterSeconds = ReadRetryAfter(resp),
                };
                if (!IsRetryable(result.Status) || attempt + 1 >= attempts) return result;
                _log?.Invoke($"HTTP {result.Status}，准备重试（{attempt + 2}/{attempts}）：{Shorten(url)}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("请求超时");
                if (attempt + 1 >= attempts) break;
                _log?.Invoke($"请求超时，准备重试（{attempt + 2}/{attempts}）：{Shorten(url)}");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (ex.HttpRequestError is HttpRequestError.NameResolutionError
                    or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError)
                    return new HttpResult { TransportOk = false, Error = ex.Message };
                if (attempt + 1 >= attempts) break;
                _log?.Invoke($"网络错误：{ex.Message}，准备重试（{attempt + 2}/{attempts}）：{Shorten(url)}");
            }

            var delay = Math.Min(30000, 1000 * (1 << Math.Min(attempt, 5))) + Random.Shared.Next(0, 500);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        return new HttpResult { TransportOk = false, Error = lastError?.Message ?? "网络请求失败" };
    }

    private static bool IsRetryable(int status) => status is 0 or 408 or 429 or >= 500;

    /// <summary>解析 Retry-After（秒或 HTTP 日期），最多 10 分钟。</summary>
    private static int ReadRetryAfter(HttpResponseMessage resp)
    {
        try
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null) return 0;
            var seconds = ra.Delta?.TotalSeconds ?? (ra.Date.HasValue
                ? (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds
                : 0);
            if (seconds <= 0) return 0;
            return (int)Math.Min(600, Math.Ceiling(seconds));
        }
        catch { return 0; }
    }

    private static string DecodeBody(byte[] raw, string mediaType, string url)
    {
        if (raw.Length == 0) return "";
        try
        {
            if (url.Contains("ascii2d.net", StringComparison.OrdinalIgnoreCase))
            {
                var probe = Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, 4096)).ToLowerInvariant();
                if (probe.Contains("shift_jis") || probe.Contains("sjis"))
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(932).GetString(raw);
                }
            }
            return Encoding.UTF8.GetString(raw);
        }
        catch { return Encoding.UTF8.GetString(raw); }
    }

    private static string Shorten(string url) => url.Length <= 80 ? url : url[..80] + "...";

    public void Dispose()
    {
        // SocketsHttpHandler 在网络异常时 Dispose 可能等待连接释放；
        // 后台释放，避免阻塞 GUI/CLI 的退出流程。
        var client = _client;
        _ = Task.Run(() => { try { client.Dispose(); } catch { } });
    }
}
