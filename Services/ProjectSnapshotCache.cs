using System.Collections.Concurrent;

namespace PowerTools.Services;

public sealed class ProjectSnapshotCache(PowerBiProjectParser parser)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public ProjectSnapshot Get(string inputPath, bool refresh = false)
    {
        var key = Path.GetFullPath(Environment.ExpandEnvironmentVariables(inputPath.Trim().Trim('"')));
        if (!refresh && _cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < _lifetime)
            return cached.Snapshot;

        var snapshot = parser.Parse(key);
        _cache[key] = new CacheEntry(snapshot, DateTimeOffset.UtcNow);
        return snapshot;
    }

    private sealed record CacheEntry(ProjectSnapshot Snapshot, DateTimeOffset CreatedAt);
}
