using System.Collections.Concurrent;

namespace BeaverSearch.Models;

public sealed class CacheState
{
    // Increment when valuation semantics change in a way that makes old 24h checks
    // unsafe to reuse. v2 switches base prices to Steam Community Market RUB.
    public int PriceEngineVersion { get; set; } = 2;

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
