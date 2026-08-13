using System.Collections.Concurrent;

namespace PowerTools.Services;

public sealed class ProjectSnapshotCache(PowerBiProjectParser parser)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public async Task<ProjectSnapshot> GetAsync(string inputPath, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(Environment.ExpandEnvironmentVariables(inputPath.Trim().Trim('"')));
        var signature = GetProjectSignature(key);
        if (refresh) _cache.TryRemove(key, out _);

        while (true)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                try
                {
                    var cached = await existing.Value.WaitAsync(cancellationToken);
                    if (DateTimeOffset.UtcNow - cached.CreatedAt < _lifetime && cached.Signature == signature) return cached.Snapshot;
                }
                catch when (!cancellationToken.IsCancellationRequested) { }
                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CacheEntry>>>(key, existing));
                continue;
            }

            var created = new Lazy<Task<CacheEntry>>(() => Task.Run(() =>
                new CacheEntry(parser.Parse(key), DateTimeOffset.UtcNow, GetProjectSignature(key)), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
            if (!_cache.TryAdd(key, created)) continue;
            try { return (await created.Value.WaitAsync(cancellationToken)).Snapshot; }
            catch
            {
                _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CacheEntry>>>(key, created));
                throw;
            }
        }
    }

    public bool Remove(string inputPath) => _cache.TryRemove(Path.GetFullPath(inputPath), out _);
    public void Clear() => _cache.Clear();

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
