using System.Text.RegularExpressions;

namespace PixivRenamer.Core;

public static class PidParser
{
    private static readonly Regex RePidPage = new(@"(?:^|[^0-9])(\d{5,10})[_\-\s]p(\d{1,3})(?:[^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReIllust = new(@"(?:illust|illust_id|artwork|artworks|pixiv)[_\-\s]*?(?:id)?[_\-\s=]*(\d{5,10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RePid = new(@"\bpid[_\-\s=]*(\d{5,10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReBracket = new(@"[\s\[\(【](\d{5,10})[\s\]\)】]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReSharp = new(@"#\s*(\d{5,10})(?:[^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReBare = new(@"(?:^|[^0-9])(\d{6,9})(?:[^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RePage = new(@"(?:^|[^0-9a-z])p(\d{1,3})(?:[^0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PidHit? Extract(string text, bool aggressive = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = RePidPage.Match(text);
        if (m.Success && IsValidPid(m.Groups[1].Value))
        {
            return new PidHit
            {
                Pid = m.Groups[1].Value,
                PageIndex = ParseInt(m.Groups[2].Value, -1),
                ExplicitMarker = true,
            };
        }

        foreach (var re in new[] { ReIllust, RePid, ReBracket, ReSharp })
        {
            m = re.Match(text);
            if (m.Success && IsValidPid(m.Groups[1].Value))
            {
                return new PidHit
                {
                    Pid = m.Groups[1].Value,
                    PageIndex = ExtractPageIndex(text),
                    ExplicitMarker = true,
                };
            }
        }

        m = ReBare.Match(text);
        if (m.Success && IsValidPid(m.Groups[1].Value))
        {
            var pid = m.Groups[1].Value;
            var date = IsLikelyDate(pid);
            var allow = aggressive || (!date && pid.Length >= 7);
            if (allow)
            {
                return new PidHit { Pid = pid, PageIndex = ExtractPageIndex(text), ExplicitMarker = false };
            }
            if (aggressive && date)
            {
                var rest = text.Remove(m.Index, m.Length);
                var m2 = ReBare.Match(rest);
                if (m2.Success && IsValidPid(m2.Groups[1].Value) && !IsLikelyDate(m2.Groups[1].Value))
                {
                    return new PidHit { Pid = m2.Groups[1].Value, PageIndex = ExtractPageIndex(text), ExplicitMarker = false };
                }
            }
        }
        return null;
    }

    public static int ExtractPageIndex(string text)
    {
        var m = RePage.Match(text);
        if (!m.Success) return -1;
        return ParseInt(m.Groups[1].Value, -1);
    }

    public static string PageSuffix(int pageIndex) => pageIndex < 0 ? "" : "_p" + pageIndex;

    public static bool IsLikelyDate(string digits)
    {
        if (digits.Length != 8 || !digits.All(char.IsDigit)) return false;
        int y = int.Parse(digits[..4]);
        int mo = int.Parse(digits.Substring(4, 2));
        int d = int.Parse(digits.Substring(6, 2));
        return y is >= 1990 and <= 2099 && mo is >= 1 and <= 12 && d is >= 1 and <= 31;
    }

    private static bool IsValidPid(string digits)
    {
        if (digits.Length is < 5 or > 10) return false;
        if (!digits.All(char.IsDigit)) return false;
        return digits.Any(c => c != '0');
    }

    private static int ParseInt(string s, int def) => int.TryParse(s, out var v) ? v : def;
}