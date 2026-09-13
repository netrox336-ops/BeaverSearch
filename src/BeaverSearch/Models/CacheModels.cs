using System.Collections.Concurrent;

namespace BeaverSearch.Models;

public sealed class CacheState
{
    // v6 changes Steam inventory semantics again: 401/null app inventories are empty,
    // a profile-page presence precheck skips absent games, and request storms/legacy
    // retries were removed. Re-evaluate every previously successful 24h valuation once.
    public int PriceEngineVersion { get; set; } = 6;

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
