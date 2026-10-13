using System.IO;
namespace PixivRenamer.Core;

public static class NamingService
{
    public static string BuildBaseName(PixivMeta meta, AppConfig cfg, string fallbackStem, string pageSuffix)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["author"] = string.IsNullOrWhiteSpace(meta.Author) ? cfg.UnknownAuthor : meta.Author,
            ["title"] = string.IsNullOrWhiteSpace(meta.Title) ? cfg.UnknownTitle : meta.Title,
            ["pid"] = meta.Pid,
            ["userid"] = meta.UserId,
            ["account"] = meta.Account,
            ["date"] = meta.Date,
            ["tags"] = meta.Tags,
            ["r18"] = meta.R18 ? "R-18" : "",
            ["page"] = string.IsNullOrEmpty(pageSuffix) ? "" : pageSuffix.TrimStart('_'),
        };

        var basename = TextUtils.CleanupBaseName(TextUtils.ExpandTemplate(cfg.Template, vars));
        if (string.IsNullOrWhiteSpace(basename))
            basename = TextUtils.CleanupBaseName(TextUtils.ExpandTemplate("{author} - {title} ({pid})", vars));
        if (string.IsNullOrWhiteSpace(basename)) basename = fallbackStem;

        var templateHasPage = cfg.Template.Contains("{page}", StringComparison.OrdinalIgnoreCase);
        var reserve = templateHasPage ? 0 : pageSuffix.Length;
        var limit = Math.Max(20, cfg.MaxNameLen);
        basename = TextUtils.SanitizeFileName(basename, limit > reserve + 5 ? limit - reserve : 20);
        if (!templateHasPage && !string.IsNullOrEmpty(pageSuffix)) basename += pageSuffix;
        return basename;
    }

    public static string BuildTargetDir(PixivMeta meta, AppConfig cfg, string sourceDir)
    {
        var dir = string.IsNullOrWhiteSpace(cfg.OutputDir) ? sourceDir : cfg.OutputDir;
        var author = string.IsNullOrWhiteSpace(meta.Author) ? cfg.UnknownAuthor : meta.Author;
        var month = meta.Date.Length >= 7 ? meta.Date[..7] : "unknown-date";
        dir = cfg.Organize switch
        {
            "author" => Path.Combine(dir, TextUtils.SanitizeFileName(author, 120)),
            "date" => Path.Combine(dir, TextUtils.SanitizeFileName(month, 120)),
            "author_date" => Path.Combine(dir, TextUtils.SanitizeFileName(author, 120), TextUtils.SanitizeFileName(month, 120)),
            "date_author" => Path.Combine(dir, TextUtils.SanitizeFileName(month, 120), TextUtils.SanitizeFileName(author, 120)),
            _ => dir,
        };
        if (cfg.SplitR18 && meta.R18)
            dir = Path.Combine(dir, TextUtils.SanitizeFileName(cfg.R18Folder, 60));
        return dir;
    }
}