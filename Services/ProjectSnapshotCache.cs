using System.Collections.Concurrent;

namespace PowerTools.Services;

public sealed class ProjectSnapshotCache(PowerBiProjectParser parser)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CacheEntry> _lastSuccessful = new(StringComparer.OrdinalIgnoreCase);
    private long _hitCount;
    private long _missCount;
    private long _fallbackCount;
    private long _failureCount;
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public async Task<ProjectSnapshot> GetAsync(string inputPath, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(Environment.ExpandEnvironmentVariables(inputPath.Trim().Trim('"')));
        await WaitForStableFilesAsync(key, cancellationToken);
        var signature = GetProjectSignature(key);
        if (refresh) _cache.TryRemove(key, out _);

        while (true)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                try
                {
                    var cached = await existing.Value.WaitAsync(cancellationToken);
                    if (DateTimeOffset.UtcNow - cached.CreatedAt < _lifetime && cached.Signature == signature)
                    {
                        Interlocked.Increment(ref _hitCount);
                        return cached.Snapshot;
                    }
                }
                catch when (!cancellationToken.IsCancellationRequested) { }
                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CacheEntry>>>(key, existing));
                continue;
            }

            var created = new Lazy<Task<CacheEntry>>(() => Task.Run(() =>
                new CacheEntry(parser.Parse(key), DateTimeOffset.UtcNow, GetProjectSignature(key)), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
            if (!_cache.TryAdd(key, created)) continue;
            Interlocked.Increment(ref _missCount);
            try
            {
                var entry = await created.Value.WaitAsync(cancellationToken);
                _lastSuccessful[key] = entry;
                return entry.Snapshot;
            }
            catch
            {
                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CacheEntry>>>(key, created));
                Interlocked.Increment(ref _failureCount);
                if (_lastSuccessful.TryGetValue(key, out var fallback))
                {
                    Interlocked.Increment(ref _fallbackCount);
                    return fallback.Snapshot with { Warnings = fallback.Snapshot.Warnings.Append("项目最新解析失败，当前显示上一次成功快照。").ToList() };
                }
                throw;
            }
        }
    }

    public bool Remove(string inputPath) => _cache.TryRemove(Path.GetFullPath(inputPath), out _);
    public void Clear() { _cache.Clear(); _lastSuccessful.Clear(); }
    public object GetDiagnostics() => new
    {
        entries = _cache.Count,
        successfulSnapshots = _lastSuccessful.Count,
        hits = Interlocked.Read(ref _hitCount),
        misses = Interlocked.Read(ref _missCount),
        failures = Interlocked.Read(ref _failureCount),
        fallbackResponses = Interlocked.Read(ref _fallbackCount)
    };

    private static async Task WaitForStableFilesAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        var previous = GetProjectSignature(root);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(150, cancellationToken);
            var current = GetProjectSignature(root);
            if (current == previous) return;
            previous = current;
        }
    }

    private static ProjectSignature GetProjectSignature(string root)
    {
        if (!Directory.Exists(root)) return new ProjectSignature(0, DateTime.MinValue.Ticks);
        long count = 0, latestTicks = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            count++;
            try { latestTicks = Math.Max(latestTicks, File.GetLastWriteTimeUtc(file).Ticks); }
            catch { }
        }
        return new ProjectSignature(count, latestTicks);
    }

    private sealed record CacheEntry(ProjectSnapshot Snapshot, DateTimeOffset CreatedAt, ProjectSignature Signature);
    private readonly record struct ProjectSignature(long FileCount, long LatestWriteTicks);
}
