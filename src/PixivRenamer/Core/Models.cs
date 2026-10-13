namespace PixivRenamer.Core;

public sealed class PixivMeta
{
    public string Pid { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Author { get; set; } = "";
    public string Account { get; set; } = "";
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Tags { get; set; } = "";
    public int PageCount { get; set; } = 1;
    public int XRestrict { get; set; }
    public bool R18 { get; set; }
    public string Source { get; set; } = "";
    public string Note { get; set; } = "";

    public bool HasIdentity => !string.IsNullOrWhiteSpace(Author) || !string.IsNullOrWhiteSpace(Title);
    public PixivMeta Clone() => (PixivMeta)MemberwiseClone();
}

public sealed class PidHit
{
    public string Pid { get; set; } = "";
    public int PageIndex { get; set; } = -1;
    public bool ExplicitMarker { get; set; }
}

public sealed class FileItem
{
    public string SourcePath { get; set; } = "";
    public string Stem { get; set; } = "";
    public string Ext { get; set; } = "";
    public string Pid { get; set; } = "";
    public int PageIndex { get; set; } = -1;
    public string PageSuffix { get; set; } = "";
    public bool PidExplicit { get; set; }
    public bool Skip { get; set; }
    public string SkipReason { get; set; } = "";
    public PixivMeta Meta { get; set; } = new();
    public bool Resolved { get; set; }
    public bool PidMetaOk { get; set; }
    public ulong Hash { get; set; }
    public ulong GroupKey { get; set; }
    public bool ReverseUsed { get; set; }
    public string ResolveNote { get; set; } = "";
    // 未识别分类（2.3）
    public string UnresolvedReason { get; set; } = "";   // 具体原因（写入报告）
    public string UnresolvedKind { get; set; } = "";     // 分类：无PID / 识图无结果 / 复核被拦下 / pixiv查询失败 / 目标路径过长
    public string ReverseProvider { get; set; } = "";    // 识图服务（未识别时用于说明）
    public bool ReverseTimedOut { get; set; }            // 识图因限流冷却超时未完成
}

public sealed class PlanEntry
{
    public int ItemIndex { get; set; }
    public string Action { get; set; } = "move";
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class ReverseResult
{
    public string Provider { get; set; } = "";
    public string PixivId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public double Similarity { get; set; }

    // SauceNAO 的库索引（官方 index_details.txt）：5=pixiv，6=pixivhistorical（已删除/老作品）
    public int IndexId { get; set; } = -1;
    public string IndexName { get; set; } = "";
    // 来源缩略图，用于 dHash 复核中置信度候选
    public string ThumbnailUrl { get; set; } = "";
    public bool Verified { get; set; }
    public string VerifyNote { get; set; } = "";

    public bool HasPixiv => !string.IsNullOrWhiteSpace(PixivId);
    public bool HasIdentity => !string.IsNullOrWhiteSpace(Author) || !string.IsNullOrWhiteSpace(Title);
}

public sealed class ProgressInfo
{
    public string Phase { get; set; } = "";
    public long Current { get; set; }
    public long Total { get; set; }
}

public sealed class RunResult
{
    public int ExitCode { get; set; }
    public long Moved { get; set; }
    public long Copied { get; set; }
    public long Noop { get; set; }
    public long Unresolved { get; set; }
    public long Failed { get; set; }
    public long DryRun { get; set; }
    public bool Canceled { get; set; }
    public string ReportPath { get; set; } = "";
}

public sealed class ReportRow
{
    public string Time { get; set; } = "";
    public string Action { get; set; } = "";
    public string Source { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string Original { get; set; } = "";
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
}