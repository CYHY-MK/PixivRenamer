namespace PixivRenamer.Core;

/// <summary>识图工作项：一组（可能包含多张近似重复图）只查一次识图。</summary>
public sealed class ReverseWorkItem
{
    public ulong GroupKey { get; init; }
    public List<FileItem> Items { get; init; } = new();

    /// <summary>识图命中的元数据；null = 未识别。</summary>
    public PixivMeta? Result { get; set; }
    public string Provider { get; set; } = "";
    public bool TimedOut { get; set; }
}

/// <summary>
/// 识图请求缓冲队列（2.3）。
/// 与 pixiv 元数据查询用线程池不同，SauceNAO / ascii2d 对频率非常敏感：
/// 这里用固定并发上限（默认 1 = 完全串行）+ 统一冷却，把识图请求排队送出，
/// 被 429/503 限流时全局暂停一段时间再继续，避免"越打越被拦"。
/// </summary>
public sealed class ReverseQueue
{
    public const string CooldownPhase = "识图冷却";

    private readonly AppConfig _cfg;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly Action<string>? _log;
    private readonly ReverseThrottle _throttle;

    public ReverseQueue(AppConfig cfg, ReverseThrottle throttle,
                        IProgress<ProgressInfo>? progress = null, Action<string>? log = null)
    {
        _cfg = cfg;
        _throttle = throttle;
        _progress = progress;
        _log = log;
    }

    public async Task RunAsync(IReadOnlyList<ReverseWorkItem> items,
                               Func<ReverseWorkItem, CancellationToken, Task> worker,
                               CancellationToken ct)
    {
        if (items.Count == 0) return;

        var concurrency = _cfg.ReverseQueueEnabled
            ? Math.Clamp(_cfg.ReverseConcurrency, 1, 8)
            : Math.Clamp(Math.Max(_cfg.ReverseConcurrency, _cfg.Threads), 1, 16);

        long done = 0;
        var completed = new System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct,
        };

        _progress?.Report(new ProgressInfo { Phase = "识图回退", Current = 0, Total = items.Count });
        try
        {
            await Parallel.ForEachAsync(items, options, async (item, token) =>
            {
                // 队列缓冲：每次取任务前先等全局冷却结束（被限流时暂停整条流水线）
                await _throttle.WaitAsync(token).ConfigureAwait(false);

                await worker(item, token).ConfigureAwait(false);

                completed.TryAdd(item.GroupKey, 0);
                var d = Interlocked.Increment(ref done);
                _progress?.Report(new ProgressInfo { Phase = "识图回退", Current = d, Total = items.Count });
                if (d % 20 == 0 || d == items.Count) _log?.Invoke($"识图进度 {d}/{items.Count}");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 队列本身出错不应该让整次整理失败：退化为串行补跑剩余项
            _log?.Invoke("识图队列异常，改为串行收尾：" + ex.Message);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (completed.ContainsKey(item.GroupKey)) continue;
                try { await worker(item, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception inner) { _log?.Invoke("识图串行收尾失败：" + inner.Message); }
            }
        }
    }
}

/// <summary>
/// 全局识图冷却闸门：任一请求被限流后，所有识图请求统一等待，且冷却时间指数增长（有上限）。
/// </summary>
public sealed class ReverseThrottle
{
    private readonly AppConfig _cfg;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private int _level;
    private int _count;

    public ReverseThrottle(AppConfig cfg, Action<string>? log = null)
    {
        _cfg = cfg;
        _log = log;
    }

    public int CooldownCount => Volatile.Read(ref _count);
    public bool Cooling => DateTimeOffset.UtcNow < _blockedUntil;

    /// <summary>被限流：按 Retry-After（若有）或指数退避设置全局冷却。</summary>
    public void Block(int retryAfterSeconds, string reason)
    {
        int ms;
        lock (_lock)
        {
            _level = Math.Min(_level + 1, 6);
            var baseMs = retryAfterSeconds > 0 ? retryAfterSeconds * 1000 : _cfg.ReverseCooldownMs * (1 << (_level - 1));
            ms = (int)Math.Min(_cfg.ReverseMaxCooldownMs, Math.Max(_cfg.ReverseCooldownMs, baseMs));
            var until = DateTimeOffset.UtcNow.AddMilliseconds(ms);
            if (until > _blockedUntil) _blockedUntil = until;
            Interlocked.Increment(ref _count);
        }
        _log?.Invoke($"识图被限流（{reason}）：全局冷却 {ms / 1000.0:0.#} 秒后继续（缓冲队列会等待）");
    }

    /// <summary>一次成功的识图请求：冷却等级回落，避免一直按最大间隔等待。</summary>
    public void Succeed()
    {
        lock (_lock) { if (_level > 0) _level--; }
    }

    /// <summary>等待冷却结束。返回 true 表示确实等待过。</summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        var waited = false;
        while (true)
        {
            DateTimeOffset until;
            lock (_lock) until = _blockedUntil;
            var left = until - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return waited;
            waited = true;
            // 分段等待，期间持续上报，界面上能看出"正在冷却"
            _log?.Invoke($"识图冷却中，还需等待 {left.TotalSeconds:0.#} 秒");
            await Task.Delay(left > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : left, ct).ConfigureAwait(false);
        }
    }
}
