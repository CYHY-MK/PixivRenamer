using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PixivRenamer.Core;

public static class TextUtils
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    public static string Trim(string? s) => (s ?? "").Trim();

    public static string SanitizeFileName(string name, int maxLen = 180)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsControl(ch) || invalid.Contains(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        var s = sb.ToString();
        s = string.Join(" ", s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        s = s.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(s)) s = "untitled";

        var stem = s.Split('.')[0];
        if (ReservedNames.Contains(stem)) s = "_" + s;

        if (maxLen <= 0) maxLen = 180;
        if (s.Length > maxLen)
        {
            var cut = maxLen;
            if (cut > 0 && cut < s.Length && char.IsHighSurrogate(s[cut - 1])) cut--;
            s = s[..cut].TrimEnd(' ', '.');
        }
        return string.IsNullOrWhiteSpace(s) ? "untitled" : s;
    }

    public static string CsvEscape(string? v)
    {
        v ??= "";
        var needQuote = v.Contains(',') || v.Contains('"') || v.Contains('\r') || v.Contains('\n');
        var s = v.Replace("\"", "\"\"");
        return needQuote ? "\"" + s + "\"" : s;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuote = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    public static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < template.Length;)
        {
            if (template[i] != '{') { sb.Append(template[i++]); continue; }
            var j = template.IndexOf('}', i + 1);
            if (j < 0) { sb.Append(template[i++]); continue; }
            var key = template.Substring(i + 1, j - i - 1).Trim().ToLowerInvariant();
            if (vars.TryGetValue(key, out var v)) sb.Append(v);
            i = j + 1;
        }
        return sb.ToString();
    }

    public static string CleanupBaseName(string raw)
    {
        var s = raw;
        for (var round = 0; round < 4; round++)
        {
            s = s.Replace("()", "").Replace("[]", "").Replace("{}", "").Replace("<>", "")
                 .Replace("( )", "").Replace("[ ]", "").Replace("{ }", "").Replace("(  )", "");
        }
        while (true)
        {
            var t = s.Replace("  ", " ").Replace("- -", "-").Replace("_ _", "_").Replace("--", "-").Replace("__", "_")
                       .Replace(" - _", " - ").Replace("_ - ", " - ");
            if (t == s) break;
            s = t;
        }
        var trimChars = " \t-_.+,;:|/\\*?!@#$%^&~".ToCharArray();
        s = s.Trim(trimChars);
        return s;
    }

    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".jpe" or ".png" or ".gif" or ".webp" or ".bmp" or ".jfif" or ".avif" or ".tif" or ".tiff";
    }

    public static string GuessMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream",
        };
    }

    public static ulong HashFileQuick(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64 * 1024];
            var read = fs.Read(buffer, 0, buffer.Length);
            ulong hash = 1469598103934665603UL;
            for (var i = 0; i < read; i++) { hash ^= buffer[i]; hash *= 1099511628211UL; }
            hash ^= (ulong)fs.Length; hash *= 1099511628211UL;
            return hash == 0 ? 1 : hash;
        }
        catch { return 0; }
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            var pa = Path.GetFullPath(a).TrimEnd('\\', '/');
            var pb = Path.GetFullPath(b).TrimEnd('\\', '/');
            return pa.Equals(pb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsUnder(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return false;
        try
        {
            var c = Path.GetFullPath(child).TrimEnd('\\', '/');
            var p = Path.GetFullPath(parent).TrimEnd('\\', '/');
            return c.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>child 是否为 parent 的真子目录（两者相同不算）。</summary>
    public static bool IsStrictSubdirectory(string? child, string? parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return false;
        return !SamePath(child, parent) && IsUnder(child!, parent!);
    }

    public static bool IsHidden(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    public static string NowStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public static string HtmlDecode(string? s) => System.Net.WebUtility.HtmlDecode(s ?? "");
}