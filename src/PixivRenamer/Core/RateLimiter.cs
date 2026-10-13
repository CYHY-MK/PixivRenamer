namespace PixivRenamer.Core;

/// <summary>按 host 维度的最小请求间隔限速器。</summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _intervals = new(StringComparer.OrdinalIgnoreCase);

    public void SetInterval(string key, int milliseconds)
    {
        lock (_lock) _intervals[key] = Math.Max(0, milliseconds);
    }

    public async Task WaitAsync(string key, CancellationToken ct)
    {
        int waitMs;
        lock (_lock)
        {
            var interval = _intervals.TryGetValue(key, out var v) ? v : 0;
            var now = DateTimeOffset.UtcNow;
            if (!_next.TryGetValue(key, out var next) || next < now) next = now;
            waitMs = (int)Math.Max(0, (next - now).TotalMilliseconds);
            _next[key] = next.AddMilliseconds(interval + Random.Shared.Next(0, 150));
        }
        if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);
    }
}