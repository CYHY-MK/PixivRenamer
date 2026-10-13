using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;

namespace PixivRenamer.Core;

public sealed class LoginResult
{
    public bool Success { get; set; }
    public string Cookie { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// pixiv 账号密码登录（网页登录流程），成功后返回可用的 Cookie 字符串。
/// 说明：pixiv 可能触发人机验证/邮箱验证；失败时请改用浏览器手动复制 Cookie。
/// </summary>
public sealed class PixivLoginService
{
    private readonly AppConfig _cfg;

    public PixivLoginService(AppConfig cfg) => _cfg = cfg;

    public async Task<LoginResult> LoginAsync(string account, string password, CancellationToken ct)
    {
        try
        {
            var container = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = container,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (!string.IsNullOrWhiteSpace(_cfg.Proxy))
            {
                handler.Proxy = new WebProxy(_cfg.Proxy);
                handler.UseProxy = true;
            }
            else if (_cfg.UseSystemProxy)
            {
                handler.UseProxy = true;
                handler.Proxy = null;
            }
            else
            {
                handler.UseProxy = false;
            }

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(_cfg.UserAgent);
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8,en;q=0.7");

            var loginPage = "https://accounts.pixiv.net/login?return_to=https%3A%2F%2Fwww.pixiv.net%2F&lang=zh";
            var html = await http.GetStringAsync(loginPage, ct).ConfigureAwait(false);
            var postKey = ExtractPostKey(html);
            if (string.IsNullOrWhiteSpace(postKey))
                return Fail("无法解析 pixiv 登录页的 post_key，可能页面结构变化或网络被拦截。请改用手动 Cookie。");

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["pixiv_id"] = account,
                ["password"] = password,
                ["post_key"] = postKey,
                ["source"] = "accounts",
                ["return_to"] = "https://www.pixiv.net/",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.pixiv.net/login") { Content = content };
            request.Headers.Referrer = new Uri(loginPage);
            request.Headers.TryAddWithoutValidation("Origin", "https://accounts.pixiv.net");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var cookies = new List<Cookie>();
            foreach (var uri in new[] { "https://www.pixiv.net/", "https://accounts.pixiv.net/" })
                cookies.AddRange(container.GetCookies(new Uri(uri)).Cast<Cookie>());
            cookies = cookies
                .GroupBy(c => c.Name + "|" + c.Value)
                .Select(g => g.First())
                .ToList();

            if (cookies.All(c => c.Name != "PHPSESSID"))
            {
                if (body.Contains("reCAPTCHA", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("認証コード", StringComparison.Ordinal) ||
                    body.Contains("二段階", StringComparison.Ordinal))
                    return Fail("pixiv 要求人机验证/两步验证，无法自动登录。请改用浏览器手动复制 Cookie。");
                return Fail("登录失败：账号或密码错误，或 pixiv 返回了验证页面。\n" +
                            "如果确实无法自动登录，请在浏览器登录后复制 Cookie 粘贴到上方输入框。");
            }

            var cookieText = string.Join("; ", cookies
                .Where(c => c.Domain.Contains("pixiv", StringComparison.OrdinalIgnoreCase) || c.Domain.Length == 0)
                .Select(c => $"{c.Name}={c.Value}"));
            if (string.IsNullOrWhiteSpace(cookieText))
                return Fail("登录请求成功，但没有取得可用的 Cookie。请改用手动复制 Cookie。");
            return new LoginResult { Success = true, Cookie = cookieText, Message = "Cookie 获取成功。" };
        }
        catch (OperationCanceledException)
        {
            return Fail("登录请求已取消或超时。");
        }
        catch (Exception ex)
        {
            return Fail("自动登录失败：" + ex.Message + "\n请检查网络/代理，或改用浏览器手动复制 Cookie。");
        }
    }

    private static LoginResult Fail(string message) => new() { Success = false, Message = message };

    private static string ExtractPostKey(string html)
    {
        var patterns = new[]
        {
            "name=[\"']post_key[\"'][^>]*value=[\"']([^\"']+)[\"']",
            "value=[\"']([^\"']+)[\"'][^>]*name=[\"']post_key[\"']",
            "post_key[\"']?\\s*[:=]\\s*[\"']([^\"']+)[\"']",
        };
        foreach (var pattern in patterns)
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success) return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        return "";
    }
}