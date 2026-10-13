using System.IO;
using System.Collections.Concurrent;

namespace PixivRenamer.Core;

/// <summary>核心流程：扫描 -> pixiv 查询 -> 识图回退 -> 生成计划 -> 执行/预览。</summary>
public sealed class RenamerEngine
{
    private readonly AppConfig _cfg;
    private readonly IProgress<string>? _log;
    private readonly IProgress<ProgressInfo>? _progress;

    public RenamerEngine(AppConfig cfg, IProgress<string>? log = null, IProgress<ProgressInfo>? progress = null)
    {
        _cfg = cfg;
        _log = log;
        _progress = progress;
    }

    private void Log(string message) => _log?.Report(message);
    private void Report(string phase, long current, long total)
        => _progress?.Report(new ProgressInfo { Phase = phase, Current = current, Total = total });

    public async Task<RunResult> RunAsync(CancellationToken ct)
    {
        var result = new RunResult { ReportPath = _cfg.ReportPath };
        Log($"=== PixivRenamer {AppConfig.VersionText} ===");
        Log($"源目录：{_cfg.SourceDir}");
        Log(_cfg.OutputDir.Length > 0 ? $"输出目录：{_cfg.OutputDir}" : "输出目录：原地重命名");
        Log($"命名模板：{_cfg.Template}");
        Log($"分类方式：{_cfg.Organize}" + (_cfg.SplitR18 ? "（R-18 单独文件夹）" : ""));
        Log(_cfg.ReverseEnabled ? $"识图回退：开启（{_cfg.ReverseOrder}）" : "识图回退：关闭");
        Log(_cfg.DryRun ? "运行模式：预览（不会修改任何文件）" : "运行模式：正式执行");

        // 代理：按配置决定的模式解析（auto 会自动侦测本地 Clash/v2rayN 等端口）
        var netLog = new List<string>();
        void NetLog(string m) { netLog.Add($"[{DateTime.Now:HH:mm:ss}] {m}"); Log(m); }
        var proxyResolution = ProxyResolver.Resolve(_cfg, NetLog);
        var effective = _cfg.Clone();
        effective.Proxy = proxyResolution.Chosen;
        effective.UseSystemProxy = proxyResolution.Chosen.Length == 0 && _cfg.ProxyMode != "direct";
        Log($"代理：{proxyResolution.DescribeChosen()}");
        if (proxyResolution.InvalidInput.Length > 0)
        {
            Log($"⚠ 填写的代理无法识别：{proxyResolution.InvalidInput}");
            Log("  " + proxyResolution.InvalidReason);
        }

        Report("扫描文件", 0, 0);
        var items = Scan();
        Report("扫描文件", 1, 1);
        Log($"扫描到 {items.Count} 个图片文件");
        if (items.Count == 0) { Log("未找到可处理的图片文件"); return result; }

        if (_cfg.Resume) ApplyResume(items);

        using var http = new HttpService(effective, Log);
        http.SetInterval("www.pixiv.net", _cfg.PixivDelayMs);
        http.SetInterval("saucenao.com", _cfg.SauceNaoDelayMs);
        http.SetInterval("ascii2d.net", _cfg.Ascii2dDelayMs);
        var pixiv = new PixivClient(http, effective, Log);
        // 识图请求统一走缓冲队列：全局冷却闸门被所有识图请求共享
        var throttle = new ReverseThrottle(_cfg, Log);
        var reverse = new ReverseSearchService(http, effective, Log, throttle);
        var reverseQueue = new ReverseQueue(_cfg, throttle, _progress, Log);

        // 先做一次快速连接测试：pixiv 不可达时（大陆网络/代理未配置）跳过 pixiv 元数据查询。
        // 识图走的是 SauceNAO / ascii2d 等其它域名，通常仍可用，所以单独放行。
        var probeCfg = effective.Clone();
        probeCfg.Retries = 1;
        probeCfg.TimeoutMs = Math.Min(_cfg.TimeoutMs, 8000);
        var pixivOk = false;
        using (var probeHttp = new HttpService(probeCfg, Log))
            pixivOk = await ProbePixivAsync(probeHttp, ct).ConfigureAwait(false);

        if (!pixivOk)
        {
            Log("=========== 网络连接失败 ===========");
            Log($"代理：{proxyResolution.DescribeChosen()}");
            Log("无法访问 pixiv，已跳过 pixiv 元数据查询（识图接口是其它域名，仍会尝试）。");
            Log("Clash 用户请检查：设置→端口里的 mixed-port 是否和这里填的一致（Verge 默认 7897、CFW 默认 7890）。");
            Log("可以在pixiv / 识图页点「检测本地代理」，让它自动找出可用的端口。");
            try
            {
                var diagCfg = effective.Clone();
                var probes = await NetworkDiagnostics.ProbeAsync(diagCfg, _cfg.PixivApiBase, ct).ConfigureAwait(false);
                var report = NetworkDiagnostics.WriteReport(AppConfig.ExeDirectory(),
                    "pixiv 连通性预检失败（整理任务）", _cfg, proxyResolution, probes, netLog);
                if (report.Length > 0) Log($"网络诊断报告：{report}");
            }
            catch (Exception ex) { Log("写网络诊断报告失败：" + ex.Message); }
            Log("===================================");
        }
        else
        {
            await ResolvePidsAsync(items, pixiv, ct).ConfigureAwait(false);
        }

        if (pixivOk || _cfg.ReverseWhenPixivDown)
            await ReverseSearchAsync(items, reverse, reverseQueue, http, pixiv, pixivOk, ct).ConfigureAwait(false);
        else
            Log("识图回退已跳过：pixiv 不可达，且未开启pixiv 不通时仍识图。");

        foreach (var item in items)
        {
            item.Resolved = item.Meta.HasIdentity;
            if (!item.Resolved && string.IsNullOrWhiteSpace(item.UnresolvedReason))
                item.UnresolvedReason = BuildUnresolvedReason(item);
        }
        var resolved = items.Count(i => i.Resolved);
        Log($"元数据解析完成：可命名 {resolved} / {items.Count}，未识别 {items.Count - resolved}");

        var plan = new List<PlanEntry>();
        var unresolved = new List<int>();
        BuildPlan(items, plan, unresolved);
        WriteUnresolvedList(items, unresolved);
        if (unresolved.Count > 0)
            Log(_cfg.MoveUnresolvedToFolder
                ? $"未识别文件单独归类：{unresolved.Count} 个 -> {UnresolvedDir()}（{( _cfg.CopyMode ? "复制" : "移动")}，按原名保留，原因写入报告）"
                : $"未识别文件：{unresolved.Count} 个（未开启单独归类，保持原样）");

        if (_cfg.DryRun)
        {
            WriteReport(items, plan, "dryrun");
            result.DryRun = plan.Count;
            Log($"预览完成：{plan.Count} 条计划，未识别 {unresolved.Count} 条，未修改任何文件");
            Log($"报告文件：{_cfg.ReportPath}");
            return result;
        }

        await ExecuteAsync(items, plan, result, ct).ConfigureAwait(false);
        Log($"处理完成：移动 {result.Moved}，复制 {result.Copied}，跳过 {result.Noop}，" +
            $"未识别 {result.Unresolved}，失败 {result.Failed}");
        Log($"报告文件：{_cfg.ReportPath}");
        result.ExitCode = result.Failed > 0 || result.Canceled ? 1 : 0;
        return result;
    }

    private async Task<bool> ProbePixivAsync(HttpService http, CancellationToken ct)
    {
        try
        {
            var url = _cfg.PixivApiBase.TrimEnd('/') + "/";
            var resp = await http.GetAsync(url, "www.pixiv.net", null, ct, 1).ConfigureAwait(false);
            if (resp.TransportOk) return true;
            Log("pixiv 连接测试失败：" + resp.Error);
        }
        catch (Exception ex)
        {
            Log("pixiv 连接测试异常：" + ex.Message);
        }
        return false;
    }

    // ---------------- 扫描 ----------------
    private List<FileItem> Scan()
    {
        var list = new List<FileItem>();
        foreach (var path in EnumerateFiles(_cfg.SourceDir))
        {
            if (!TextUtils.IsImageFile(path)) continue;
            if (IsExcluded(path)) continue;
            var item = CreateItem(path);
            list.Add(item);
        }
        list.Sort((a, b) => string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (_cfg.FileLimit > 0 && list.Count > _cfg.FileLimit)
        {
            Log($"试跑限制：仅处理前 {_cfg.FileLimit} 个文件（共扫到 {list.Count} 个）");
            list.RemoveRange(_cfg.FileLimit, list.Count - _cfg.FileLimit);
        }
        return list;
    }

    private IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                if (!_cfg.IncludeHidden && TextUtils.IsHidden(f)) continue;
                yield return f;
            }
            if (!_cfg.Recursive) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { continue; }
            foreach (var s in subs)
            {
                if (!_cfg.IncludeHidden && TextUtils.IsHidden(s)) continue;
                if (IsExcludedSubtree(s)) continue;
                stack.Push(s);
            }
        }
    }

    private bool IsExcluded(string path)
    {
        if (IsExcludedSubtree(path)) return true;
        var name = Path.GetFileName(path);
        if (name.StartsWith("_pixiv_rename", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(Path.GetFileName(_cfg.ReportPath), StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(Path.GetFileName(_cfg.LogPath), StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("_unresolved.txt", StringComparison.OrdinalIgnoreCase)) return true;
        // 已经归入「未识别」文件夹的文件不再重复扫描；
        // 只在该文件夹位于源目录内部时排除，避免误排除用户自己请过的同名目录。
        if (name.Equals(_cfg.UnresolvedFolderName, StringComparison.OrdinalIgnoreCase) &&
            TextUtils.IsUnder(path, _cfg.SourceDir))
            return true;
        return false;
    }

    // 只有输出/缓存目录确实是源目录的子目录时才排除其内容。
    // 这样输出目录 = 源目录或输出目录是源目录上级时，不会把全部文件误判为已处理而跳过。
    private bool IsExcludedSubtree(string path)
    {
        return IsSubtreeBelowSource(path, _cfg.OutputDir) ||
               IsSubtreeBelowSource(path, _cfg.CacheDir);
    }

    private bool IsSubtreeBelowSource(string path, string subtreeRoot)
    {
        if (string.IsNullOrWhiteSpace(subtreeRoot) || string.IsNullOrWhiteSpace(_cfg.SourceDir)) return false;
        return TextUtils.IsStrictSubdirectory(subtreeRoot, _cfg.SourceDir) &&
               TextUtils.IsUnder(path, subtreeRoot);
    }

    private FileItem CreateItem(string path)
    {
        var item = new FileItem
        {
            SourcePath = path,
            Stem = Path.GetFileNameWithoutExtension(path),
            Ext = Path.GetExtension(path).ToLowerInvariant(),
        };
        var hit = PidParser.Extract(item.Stem, _cfg.AggressivePid);
        if (hit == null || !hit.ExplicitMarker)
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            var parentHit = string.IsNullOrEmpty(parent) ? null : PidParser.Extract(parent, _cfg.AggressivePid);
            if (parentHit != null)
            {
                var startsWithPid = parentHit.Pid.Length > 0 && parent.StartsWith(parentHit.Pid, StringComparison.Ordinal) &&
                                    (parent.Length == parentHit.Pid.Length || !char.IsDigit(parent[parentHit.Pid.Length]));
                var parentOk = parentHit.ExplicitMarker || _cfg.AggressivePid || (startsWithPid && parentHit.Pid.Length >= 7);
                var overrideOk = hit == null || parentHit.ExplicitMarker;
                if (parentOk && overrideOk) hit = parentHit;
            }
        }
        if (hit != null)
        {
            item.Pid = hit.Pid;
            item.PidExplicit = hit.ExplicitMarker;
            item.PageIndex = hit.PageIndex >= 0 ? hit.PageIndex : PidParser.ExtractPageIndex(item.Stem);
        }
        else
        {
            item.PageIndex = PidParser.ExtractPageIndex(item.Stem);
        }
        item.PageSuffix = PidParser.PageSuffix(item.PageIndex);
        return item;
    }

    private void ApplyResume(List<FileItem> items)
    {
        var rows = ReportReader.Load(_cfg.ReportPath);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (row.Action.Equals("move", StringComparison.OrdinalIgnoreCase) || row.Action.Equals("copy", StringComparison.OrdinalIgnoreCase))
                done.Add(row.Original);
        if (done.Count == 0) return;
        var before = items.Count;
        items.RemoveAll(i => done.Contains(i.SourcePath));
        Log($"断点续跑：根据报告跳过 {before - items.Count} 个已处理文件");
    }

    // ---------------- PID 查询 ----------------
    private async Task ResolvePidsAsync(List<FileItem> items, PixivClient pixiv, CancellationToken ct)
    {
        var groups = items.Where(i => !i.Skip && !string.IsNullOrWhiteSpace(i.Pid))
                          .GroupBy(i => i.Pid, StringComparer.Ordinal)
                          .ToList();
        if (groups.Count == 0) { Log("未能从文件名/父目录中提取 PID，将全部文件尝试识图查询"); return; }
        Log($"发现 {groups.Count} 个不同 PID，开始查询 pixiv（线程 {_cfg.Threads}）");

        var cache = new ConcurrentDictionary<string, PixivMeta>(StringComparer.Ordinal);
        long done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(_cfg.Threads, 1, 16),
            CancellationToken = ct,
        };
        await Parallel.ForEachAsync(groups, options, async (group, token) =>
        {
            var meta = await pixiv.FetchByPidAsync(group.Key, token).ConfigureAwait(false);
            cache[group.Key] = meta;
            var d = Interlocked.Increment(ref done);
            Report("查询 pixiv", d, groups.Count);
            if (d % 20 == 0 || d == groups.Count) Log($"pixiv 查询进度 {d}/{groups.Count}");
        }).ConfigureAwait(false);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Pid)) continue;
            if (cache.TryGetValue(item.Pid, out var meta))
            {
                item.Meta = meta.Clone();
                item.PidMetaOk = meta.HasIdentity;
            }
            if (string.IsNullOrWhiteSpace(item.Meta.Pid)) item.Meta.Pid = item.Pid;
        }
    }

    // ---------------- 识图回退 ----------------
    private async Task ReverseSearchAsync(List<FileItem> items, ReverseSearchService reverse, ReverseQueue queue,
                                          HttpService http, PixivClient pixiv, bool pixivAvailable, CancellationToken ct)
    {
        if (!_cfg.ReverseEnabled) { Log("识图回退已关闭；缺少作者/标题的文件将保持原名"); return; }
        var need = items.Where(i => !i.Skip &&
                                    (string.IsNullOrWhiteSpace(i.Meta.Author) || string.IsNullOrWhiteSpace(i.Meta.Title))).ToList();
        if (need.Count == 0) return;
        Log($"有 {need.Count} 个文件缺少作者或标题，开始使用识图服务（{_cfg.ReverseOrder}）");
        Log(_cfg.ReverseQueueEnabled
            ? $"识图请求缓冲：开启（并发 {Math.Clamp(_cfg.ReverseConcurrency, 1, 8)}，被限流后冷却 {_cfg.ReverseCooldownMs / 1000.0:0.#}~{_cfg.ReverseMaxCooldownMs / 1000.0:0.#} 秒，失败重试 {_cfg.ReverseMaxRetries} 次）"
            : "识图请求缓冲：关闭（按线程数并发直接请求）");
        if (!pixivAvailable) Log("pixiv 当前不可用：识图命中后直接采用 SauceNAO 返回的作者/标题，不再回查 pixiv 详情");
        if (_cfg.VerifyByHash) Log($"置信度低于 85 的候选需通过 dHash 复核（汉明距离上限 {_cfg.HashMaxDistance}/64）");

        // 分组：先按完整文件哈希，再用 dHash 把同一张图的不同分辨率/重存版本并成一组，一组只查一次识图。
        var buckets = new Dictionary<ulong, List<FileItem>>();
        var synthetic = 0UL;
        foreach (var item in need)
        {
            if (item.Hash == 0) item.Hash = TextUtils.HashFileQuick(item.SourcePath);
            var key = item.Hash != 0 ? item.Hash : (0x8000000000000000UL | (++synthetic));
            if (!buckets.TryGetValue(key, out var list)) { list = new List<FileItem>(); buckets[key] = list; }
            list.Add(item);
        }

        var groups = new Dictionary<ulong, List<FileItem>>();
        var representatives = new List<(ulong Phash, ulong Key)>();
        var merged = 0;
        foreach (var bucket in buckets.Values)
        {
            var sample = bucket[0];
            var phash = _cfg.GroupByPerceptualHash ? PerceptualHash.Compute(sample.SourcePath) : 0;
            ulong key = 0;
            if (phash != 0)
                foreach (var rep in representatives)
                    if (PerceptualHash.Distance(rep.Phash, phash) <= _cfg.HashMaxDistance) { key = rep.Key; break; }

            if (key == 0)
            {
                key = sample.Hash != 0 ? sample.Hash : (0x8000000000000000UL | (++synthetic));
                representatives.Add((phash, key));
                groups[key] = new List<FileItem>();
            }
            else merged++;
            groups[key].AddRange(bucket);
        }
        foreach (var group in groups)
            foreach (var item in group.Value) item.GroupKey = group.Key;

        if (merged > 0)
            Log($"感知分组：{need.Count} 个文件归并为 {groups.Count} 组（{merged} 组为近似重复），少发 {need.Count - groups.Count} 次识图请求");

        var work = groups.Select(kv => new ReverseWorkItem { GroupKey = kv.Key, Items = kv.Value }).ToList();
        var byGroup = work.ToDictionary(w => w.GroupKey);
        var stats = new ConcurrentDictionary<string, int>();
        var unresolvedByGroup = new ConcurrentDictionary<ulong, string>();
        await queue.RunAsync(work, (w, token) => SearchGroupAsync(w, reverse, http, pixiv, pixivAvailable, stats, unresolvedByGroup, token), ct)
                   .ConfigureAwait(false);

        if (stats.Count > 0)
            Log("识图统计：" + string.Join("，", stats.Select(kv => $"{kv.Key} {kv.Value}")));
        if (unresolvedByGroup.Count > 0)
            Log($"识图未识别：{unresolvedByGroup.Count} 组（{string.Join("，", unresolvedByGroup.Values.GroupBy(v => v).Select(g => $"{g.Key} {g.Count()}"))}）");

        foreach (var item in need)
        {
            if (unresolvedByGroup.TryGetValue(item.GroupKey, out var reason))
            {
                item.UnresolvedReason = $"识图未识别（{reason}）";
                if (reason.Contains("限流", StringComparison.Ordinal)) item.ReverseTimedOut = true;
            }

            if (!byGroup.TryGetValue(item.GroupKey, out var w) || w.Result is null) continue;
            var meta = w.Result;
            var originalPidValid = item.PidMetaOk && !string.IsNullOrWhiteSpace(item.Pid);
            if (!originalPidValid && !string.IsNullOrWhiteSpace(meta.Pid)) item.Meta.Pid = meta.Pid;
            else if (!string.IsNullOrWhiteSpace(meta.Pid) && !string.IsNullOrWhiteSpace(item.Meta.Pid) && meta.Pid != item.Meta.Pid)
                item.ResolveNote = $"识图 PID({meta.Pid}) 与原 PID({item.Meta.Pid}) 不一致";
            if (string.IsNullOrWhiteSpace(item.Meta.Author)) item.Meta.Author = meta.Author;
            if (string.IsNullOrWhiteSpace(item.Meta.Title)) item.Meta.Title = meta.Title;
            if (string.IsNullOrWhiteSpace(item.Meta.UserId)) item.Meta.UserId = meta.UserId;
            if (string.IsNullOrWhiteSpace(item.Meta.Date)) item.Meta.Date = meta.Date;
            if (string.IsNullOrWhiteSpace(item.Meta.Tags)) item.Meta.Tags = meta.Tags;
            if (string.IsNullOrWhiteSpace(item.Meta.Source)) item.Meta.Source = meta.Source;
            if (string.IsNullOrWhiteSpace(item.Meta.Note)) item.Meta.Note = meta.Note;
            if (string.IsNullOrWhiteSpace(item.Meta.Pid)) item.Meta.Pid = item.Pid;
            item.ReverseUsed = true;
            if (string.IsNullOrWhiteSpace(item.ReverseProvider)) item.ReverseProvider = w.Provider;
        }
    }

    /// <summary>单组识图：走缓冲队列的 worker，组内所有近似重复图共用一次查询结果。</summary>
    private async Task SearchGroupAsync(ReverseWorkItem w, ReverseSearchService reverse, HttpService http,
                                        PixivClient pixiv, bool pixivAvailable,
                                        ConcurrentDictionary<string, int> stats,
                                        ConcurrentDictionary<ulong, string> unresolved,
                                        CancellationToken ct)
    {
        var samplePath = w.Items[0].SourcePath;
        List<ReverseResult> list;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(Math.Max(10000, _cfg.ReverseTimeoutMs));
            try
            {
                // 缓冲：失败/限流时退避重试，而不是直接判定为未识别
                list = await reverse.SearchWithRetryAsync(samplePath, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 限流冷却太久导致该文件超时：标记为待重试，不误记为"识图无结果"
                w.TimedOut = true;
                stats.AddOrUpdate("识图超时（限流冷却）", 1, (_, v) => v + 1);
                unresolved[w.GroupKey] = $"限流/冷却等待超时（{reverse.LastProvider}）";
                Log($"识图超时：{Path.GetFileName(samplePath)}（{reverse.LastProvider} 被限流，冷却等待超过 {_cfg.ReverseTimeoutMs / 1000} 秒）");
                return;
            }
        }

        var localHash = list.Count > 0 && _cfg.VerifyByHash ? PerceptualHash.Compute(samplePath) : 0;
        var pixivHits = list.Where(x => x.HasPixiv).ToList();
        var blocked = false;
        string lastNote = "";

        PixivMeta? chosen = null;
        foreach (var r in pixivHits)
        {
            var (accept, note) = await JudgeAsync(r, localHash, http, ct).ConfigureAwait(false);
            lastNote = note;
            if (!accept)
            {
                blocked = true;
                stats.AddOrUpdate("被复核拦下", 1, (_, v) => v + 1);
                Log($"放弃候选 {r.Provider} {r.Similarity:0.#}（{r.IndexName}，PID {r.PixivId}）：{note}");
                continue;
            }
            r.Verified = _cfg.VerifyByHash;
            r.VerifyNote = note;

            PixivMeta meta;
            if (pixivAvailable)
            {
                meta = await pixiv.FetchByPidAsync(r.PixivId, ct).ConfigureAwait(false);
                if (!meta.HasIdentity)
                    meta = new PixivMeta
                    {
                        Pid = r.PixivId, UserId = r.UserId, Author = r.Author, Title = r.Title,
                        Source = r.Provider + "(回退)", Note = "pixiv 详情查询失败，使用识图结果",
                    };
                else meta.Source = r.Provider + "->" + meta.Source;
            }
            else
            {
                meta = new PixivMeta
                {
                    Pid = r.PixivId, UserId = r.UserId, Author = r.Author, Title = r.Title,
                    Source = r.Provider + "(回退)",
                    Note = "pixiv 不可达，使用识图结果" + (string.IsNullOrWhiteSpace(note) ? "" : $"；{note}"),
                };
            }
            if (meta.HasIdentity)
            {
                chosen = meta;
                stats.AddOrUpdate(r.Provider, 1, (_, v) => v + 1);
                break;
            }
        }
        if (chosen == null)
        {
            var r = list.FirstOrDefault(x => !x.HasPixiv && x.HasIdentity);
            if (r != null)
                chosen = new PixivMeta { Author = r.Author, Title = r.Title, UserId = r.UserId, Source = r.Provider + "(无PID)", Note = r.Url };
        }

        if (chosen != null) { w.Result = chosen; w.Provider = reverse.LastProvider; return; }

        // 未识别：记录原因分类，供「未识别」文件夹与报告使用
        string reason;
        if (list.Count == 0) { reason = $"{reverse.LastProvider} 无候选结果"; stats.AddOrUpdate("识图无结果", 1, (_, v) => v + 1); }
        else if (blocked) reason = "候选被 dHash 复核拦下";
        else { reason = "命中作品但没有作者/标题字段"; stats.AddOrUpdate("命中但字段缺失", 1, (_, v) => v + 1); }
        if (!string.IsNullOrWhiteSpace(lastNote) && blocked) reason += $"（{lastNote}）";
        unresolved[w.GroupKey] = reason;
    }

    /// <summary>
    /// 判断一个识图候选是否可信：
    ///   置信度  85 直接采纳；
    ///   60~85 必须下载来源缩略图做 dHash 复核  同角色/相似构图的误判基本都落在这一段。
    /// </summary>
    private async Task<(bool Accept, string Note)> JudgeAsync(ReverseResult r, ulong localHash, HttpService http, CancellationToken ct)
    {
        if (!_cfg.VerifyByHash || localHash == 0) return (true, "");
        if (r.Similarity >= 85) return (true, "高置信度");
        if (string.IsNullOrWhiteSpace(r.ThumbnailUrl)) return (false, "中置信度且没有可比对的缩略图");

        try
        {
            // 缩略图在 img*.saucenao.com，用独立限速键，避免占用搜索接口的间隔
            var resp = await http.GetAsync(r.ThumbnailUrl, "saucenao-image", null, ct, 2).ConfigureAwait(false);
            if (!resp.Ok) return (resp.TransportOk ? (false, $"缩略图 HTTP {resp.Status}") : (false, "缩略图下载失败：" + resp.Error));

            var remote = PerceptualHash.Compute(resp.Raw);
            var dist = PerceptualHash.Distance(localHash, remote);
            return dist <= _cfg.HashMaxDistance
                ? (true, $"dHash 复核通过（差异 {dist}/64）")
                : (false, $"dHash 复核不通过（差异 {dist}/64，可能只是同角色或相似构图）");
        }
        catch (Exception ex) { return (false, "缩略图校验异常：" + ex.Message); }
    }
    // ---------------- 识别失败原因分类 ----------------
    /// <summary>把未识别文件的原因归类，用于「未识别」文件夹与报告。</summary>
    private string BuildUnresolvedKind(FileItem item) => ClassifyUnresolved(item);

    /// <summary>未识别原因分类（自检与报告共用）。</summary>
    public string ClassifyUnresolved(FileItem item)
    {
        var note = item.ResolveNote + " " + item.UnresolvedReason + " " + item.Meta.Note;
        if (item.Meta.Note.Contains("过大", StringComparison.Ordinal) || note.Contains("无法读取", StringComparison.Ordinal))
            return "文件过大或无法读取";
        if (note.Contains("复核拦下", StringComparison.Ordinal) || note.Contains("复核不通过", StringComparison.Ordinal))
            return "识图复核未通过";
        if (item.ReverseTimedOut || note.Contains("限流", StringComparison.Ordinal))
            return "识图限流待重试";
        if (note.Contains("拒绝请求", StringComparison.Ordinal) || note.Contains("API Key", StringComparison.Ordinal))
            return "识图服务不可用";
        if (item.ReverseUsed || item.UnresolvedReason.Contains("识图", StringComparison.Ordinal))
            return "识图无结果";
        if (item.PidMetaOk && !string.IsNullOrWhiteSpace(item.Pid)) return "pixiv 查询失败";
        if (!string.IsNullOrWhiteSpace(item.Pid)) return "PID 查询无结果";
        return "无 PID 且识图无结果";
    }

    private string BuildUnresolvedReason(FileItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ResolveNote)) return item.ResolveNote;
        if (!string.IsNullOrWhiteSpace(item.UnresolvedReason)) return item.UnresolvedReason;
        if (!string.IsNullOrWhiteSpace(item.Meta.Note)) return item.Meta.Note;
        if (item.ReverseTimedOut) return "识图被限流，冷却等待超时";
        if (item.ReverseUsed) return "识图未找到可用的作者 / 标题";
        if (string.IsNullOrWhiteSpace(item.Pid)) return "文件名与父目录都没有 PID，且识图没有结果";
        return "未识别到作者 / 标题";
    }

    /// <summary>未识别文件的归档目录：输出目录（或源目录）下的「未识别」文件夹。</summary>
    public string UnresolvedDir()
    {
        var root = !string.IsNullOrWhiteSpace(_cfg.OutputDir) ? _cfg.OutputDir : _cfg.SourceDir;
        return Path.Combine(root, _cfg.UnresolvedFolderName);
    }

    // ---------------- 生成计划 ----------------
    private void BuildPlan(List<FileItem> items, List<PlanEntry> plan, List<int> unresolved)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedDir = UnresolvedDir();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.Resolved)
            {
                item.UnresolvedKind = BuildUnresolvedKind(item);
                item.UnresolvedReason = BuildUnresolvedReason(item);
                var reason = $"[{item.UnresolvedKind}] {item.UnresolvedReason}";
                var archiveTarget = _cfg.MoveUnresolvedToFolder
                    ? UniqueTarget(Path.Combine(unresolvedDir, Path.GetFileName(item.SourcePath)), used, item.SourcePath)
                    : "";
                plan.Add(new PlanEntry
                {
                    ItemIndex = i,
                    Action = "unresolved",
                    Target = archiveTarget,
                    Note = archiveTarget.Length > 0 ? reason + "；将归入 " + _cfg.UnresolvedFolderName + " 文件夹" : reason,
                });
                unresolved.Add(i);
                continue;
            }
            var sourceDir = Path.GetDirectoryName(item.SourcePath)!;
            var dir = NamingService.BuildTargetDir(item.Meta, _cfg, sourceDir);
            var baseName = NamingService.BuildBaseName(item.Meta, _cfg, item.Stem, item.PageSuffix);
            var ext = string.IsNullOrEmpty(item.Ext) ? Path.GetExtension(item.SourcePath).ToLowerInvariant() : item.Ext;
            var target = UniqueTarget(Path.Combine(dir, baseName + ext), used, item.SourcePath);

            var guard = 0;
            while (target.Length > 250 && baseName.Length > 24 && guard++ < 8)
            {
                baseName = TextUtils.SanitizeFileName(baseName[..(baseName.Length - 12)], _cfg.MaxNameLen);
                target = UniqueTarget(Path.Combine(dir, baseName + ext), used, item.SourcePath);
            }
            if (target.Length > 255)
            {
                plan.Add(new PlanEntry { ItemIndex = i, Action = "unresolved", Note = "目标路径过长，已跳过" });
                unresolved.Add(i);
                continue;
            }
            var same = Path.GetFullPath(target).Equals(Path.GetFullPath(item.SourcePath), StringComparison.OrdinalIgnoreCase);
            plan.Add(new PlanEntry
            {
                ItemIndex = i,
                Target = target,
                Action = same ? "noop" : (_cfg.CopyMode ? "copy" : "move"),
            });
        }
    }

    private static string UniqueTarget(string desired, HashSet<string> used, string source)
    {
        bool ExistsDifferent(string p) => File.Exists(p) && !Path.GetFullPath(p).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        if (!used.Contains(desired) && !ExistsDifferent(desired)) { used.Add(desired); return desired; }
        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var n = 2; n < 10000; n++)
        {
            var cand = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!used.Contains(cand) && !ExistsDifferent(cand)) { used.Add(cand); return cand; }
        }
        used.Add(desired);
        return desired;
    }

    // ---------------- 报告 / 执行 ----------------
    private void WriteUnresolvedList(List<FileItem> items, List<int> unresolved)
    {
        if (unresolved.Count == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(_cfg.ReportPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "_unresolved.txt");
            using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("以下文件未能识别到作者/标题：");
            w.WriteLine(_cfg.MoveUnresolvedToFolder
                ? $"正式执行时会{( _cfg.CopyMode ? "复制" : "移动")}到「{_cfg.UnresolvedFolderName}」文件夹（预览模式不改动文件）。"
                : "正式执行时保持原样（未开启「未识别文件单独归类」）。");
            w.WriteLine();
            foreach (var idx in unresolved)
            {
                var item = items[idx];
                w.WriteLine($"{item.SourcePath}\t{item.UnresolvedKind}\t{item.UnresolvedReason}");
            }
            var summary = unresolved.Select(idx => items[idx].UnresolvedKind)
                                    .GroupBy(k => k)
                                    .Select(g => $"{g.Key} {g.Count()}")
                                    .ToList();
            w.WriteLine();
            w.WriteLine("原因分类统计：" + string.Join("，", summary));
            Log($"未识别文件列表：{path}（{string.Join("，", summary)}）");
        }
        catch (Exception ex) { Log("写入未识别列表失败：" + ex.Message); }
    }

    private void WriteReport(List<FileItem> items, List<PlanEntry> plan, string dryAction)
    {
        using var writer = new ReportWriter(_cfg.ReportPath);
        foreach (var entry in plan)
        {
            var item = items[entry.ItemIndex];
            writer.Write(new ReportRow
            {
                Time = TextUtils.NowStamp(),
                Action = entry.Action == "unresolved" ? "unresolved" : (dryAction == "dryrun" ? "dryrun" : entry.Action),
                Source = item.Meta.Source,
                Pid = item.Meta.Pid,
                Author = item.Meta.Author,
                Title = item.Meta.Title,
                Original = item.SourcePath,
                Target = entry.Target,
                Note = entry.Note,
            });
        }
    }

    private async Task ExecuteAsync(List<FileItem> items, List<PlanEntry> plan, RunResult result, CancellationToken ct)
    {
        using var writer = new ReportWriter(_cfg.ReportPath);
        for (var i = 0; i < plan.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Canceled = true; break; }
            var entry = plan[i];
            var item = items[entry.ItemIndex];
            Report("处理文件", i + 1, plan.Count);

            var row = new ReportRow
            {
                Time = TextUtils.NowStamp(),
                Action = entry.Action,
                Source = item.Meta.Source,
                Pid = item.Meta.Pid,
                Author = item.Meta.Author,
                Title = item.Meta.Title,
                Original = item.SourcePath,
                Target = entry.Target,
                Note = entry.Note,
            };

            if (entry.Action == "unresolved")
            {
                row.Note = entry.Note;
                var archiveTarget = entry.Target;
                if (_cfg.MoveUnresolvedToFolder && !string.IsNullOrWhiteSpace(archiveTarget) &&
                    !Path.GetFullPath(archiveTarget).Equals(Path.GetFullPath(item.SourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var archiveDir = Path.GetDirectoryName(archiveTarget);
                        if (!string.IsNullOrEmpty(archiveDir)) Directory.CreateDirectory(archiveDir);
                        if (File.Exists(archiveTarget) && !_cfg.Overwrite)
                            archiveTarget = UniqueTarget(archiveTarget, new HashSet<string>(StringComparer.OrdinalIgnoreCase), item.SourcePath);
                        if (_cfg.CopyMode) File.Copy(item.SourcePath, archiveTarget, true);
                        else
                        {
                            try { File.Move(item.SourcePath, archiveTarget, true); }
                            catch (IOException)
                            {
                                File.Copy(item.SourcePath, archiveTarget, true);
                                File.Delete(item.SourcePath);
                            }
                        }
                        row.Target = archiveTarget;
                        row.Action = _cfg.CopyMode ? "unresolved-copy" : "unresolved-move";
                        if (_cfg.Verbose) Log($"未识别归档：{Path.GetFileName(item.SourcePath)} -> {archiveTarget}");
                    }
                    catch (Exception ex)
                    {
                        row.Note += $"；归入未识别文件夹失败：{ex.Message}";
                        result.Failed++;
                        Log($"未识别文件归档失败：{item.SourcePath}：{ex.Message}");
                    }
                }
                writer.Write(row);
                result.Unresolved++;
                continue;
            }
            if (entry.Action == "noop") { writer.Write(row); result.Noop++; continue; }

            try
            {
                var target = entry.Target;
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(target) && !Path.GetFullPath(target).Equals(Path.GetFullPath(item.SourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    if (_cfg.Overwrite) File.Delete(target);
                    else
                    {
                        var unique = UniqueTarget(target, new HashSet<string>(StringComparer.OrdinalIgnoreCase), item.SourcePath);
                        target = unique;
                        row.Target = target;
                    }
                }

                if (entry.Action == "move")
                {
                    try { File.Move(item.SourcePath, target, true); }
                    catch (IOException)
                    {
                        File.Copy(item.SourcePath, target, true);
                        File.Delete(item.SourcePath);
                    }
                    result.Moved++;
                }
                else
                {
                    File.Copy(item.SourcePath, target, true);
                    result.Copied++;
                }
                if (_cfg.Verbose) Log($"完成：{Path.GetFileName(item.SourcePath)} -> {target}");
            }
            catch (Exception ex)
            {
                row.Action = "fail";
                row.Note = ex.Message;
                result.Failed++;
                Log($"处理失败：{item.SourcePath}：{ex.Message}");
            }
            writer.Write(row);
        }
    }
}