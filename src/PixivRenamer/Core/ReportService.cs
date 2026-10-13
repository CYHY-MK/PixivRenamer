using System.IO;
using System.Text;

namespace PixivRenamer.Core;

public sealed class ReportWriter : IDisposable
{
    private const string Header = "time,action,source,pid,author,title,original,target,note";
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ReportWriter(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, false, new UTF8Encoding(true));
        _writer.WriteLine(Header);
        _writer.Flush();
    }

    public void Write(ReportRow row)
    {
        lock (_lock)
        {
            _writer.WriteLine(string.Join(",",
                TextUtils.CsvEscape(row.Time), TextUtils.CsvEscape(row.Action), TextUtils.CsvEscape(row.Source),
                TextUtils.CsvEscape(row.Pid), TextUtils.CsvEscape(row.Author), TextUtils.CsvEscape(row.Title),
                TextUtils.CsvEscape(row.Original), TextUtils.CsvEscape(row.Target), TextUtils.CsvEscape(row.Note)));
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();
}

public static class ReportReader
{
    public static List<ReportRow> Load(string path)
    {
        var rows = new List<ReportRow>();
        if (!File.Exists(path)) return rows;
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return rows;

        var header = TextUtils.ParseCsvLine(lines[0]);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++) index[header[i].Trim()] = i;
        if (!index.ContainsKey("original") || !index.ContainsKey("target"))
        {
            index.Clear();
            var names = new[] { "time", "action", "source", "pid", "author", "title", "original", "target", "note" };
            for (var i = 0; i < names.Length; i++) index[names[i]] = i;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cols = TextUtils.ParseCsvLine(lines[i]);
            string Get(string name) => index.TryGetValue(name, out var idx) && idx < cols.Count ? cols[idx] : "";
            var row = new ReportRow
            {
                Time = Get("time"), Action = Get("action"), Source = Get("source"), Pid = Get("pid"),
                Author = Get("author"), Title = Get("title"), Original = Get("original"),
                Target = Get("target"), Note = Get("note"),
            };
            if (row.Original.Length == 0 && row.Target.Length == 0) continue;
            rows.Add(row);
        }
        return rows;
    }
}

public static class UndoService
{
    /// <summary>可撤销的 action：move 与 unresolved-move（未识别文件被移入「未识别」文件夹）。</summary>
    private static bool IsUndoable(string action)
        => action.Equals("move", StringComparison.OrdinalIgnoreCase) ||
           action.Equals("unresolved-move", StringComparison.OrdinalIgnoreCase);

    public static int Undo(string reportPath, bool dryRun, Action<string> log)
    {
        var rows = ReportReader.Load(reportPath);
        var restored = 0;
        foreach (var row in rows)
        {
            if (!IsUndoable(row.Action)) continue;
            if (row.Original.Length == 0 || row.Target.Length == 0) continue;
            if (!File.Exists(row.Target)) continue;
            if (File.Exists(row.Original)) { log($"撤销跳过（原位置已存在）：{row.Original}"); continue; }
            if (dryRun) { log($"[预览] 将撤销：{row.Target} -> {row.Original}"); restored++; continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(row.Original)!);
                File.Move(row.Target, row.Original, true);
                restored++;
                log($"已撤销：{Path.GetFileName(row.Target)} -> {row.Original}");
            }
            catch (Exception ex) { log($"撤销失败：{ex.Message}"); }
        }
        return restored;
    }
}