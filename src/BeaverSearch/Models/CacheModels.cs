using System.Collections.Concurrent;

namespace BeaverSearch.Models;

public sealed class CacheState
{
    // Increment when valuation semantics change in a way that makes old 24h checks
    // unsafe to reuse. v4 fixes Steam inventory paging/403 handling, so old checks
    // that were incorrectly stored as 0 ₽ / inaccessible must be rescanned.
    public int PriceEngineVersion { get; set; } = 4;

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
