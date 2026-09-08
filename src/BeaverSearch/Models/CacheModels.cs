using System.Collections.Concurrent;

namespace BeaverSearch.Models;

public sealed class CacheState
{
    // Increment when valuation semantics change in a way that makes old 24h checks
    // unsafe to reuse. v3 switches mass scans to a cached bulk RUB catalog with a
    // bounded Steam Market fallback, so old zero/partial valuations must be rescanned.
    public int PriceEngineVersion { get; set; } = 3;

    // Monitoring polls and inventory jobs overlap. Concurrent dictionaries keep the
    // 24-hour cache safe while several servers are processed at the same time.
    public ConcurrentDictionary<string, SteamCheckCache> SteamChecks { get; set; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, NameResolveCache> NameResolves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SteamCheckCache
{
    public DateTime LastCheckedUtc { get; set; }
}

public sealed class NameResolveCache
{
    public string? SteamId64 { get; set; }
    public DateTime LastAttemptUtc { get; set; }
}
