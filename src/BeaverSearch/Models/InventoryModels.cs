namespace BeaverSearch.Models;

public sealed record InventoryAsset(string AssetId, string ClassId, string InstanceId, long Amount);

public sealed class InventoryDescription
{
    public string ClassId { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public string MarketHashName { get; init; } = string.Empty;
    public bool Marketable { get; init; }
    public string? InspectLink { get; init; }
}

public sealed class SteamInventory
{
    public int AppId { get; init; }
    public bool Accessible { get; init; }
    public bool TransientFailure { get; init; }
    public string? Error { get; init; }
    public int? HttpStatusCode { get; init; }
    public List<InventoryAsset> Assets { get; init; } = [];
    public Dictionary<string, InventoryDescription> Descriptions { get; init; } = new(StringComparer.Ordinal);
}

public sealed record GameValuation(int AppId, decimal ValueRub, int ItemCount, bool Accessible, int UnpricedItems)
{
    public int MarketableItems { get; init; }
    public bool PricingAvailable { get; init; } = true;
    public bool TemporaryFailure { get; init; }
    public string? Error { get; init; }
}

public sealed record PlayerValuation(
    GameValuation Cs2,
    GameValuation Dota2,
    GameValuation Rust)
{
    public decimal Total => Cs2.ValueRub + Dota2.ValueRub + Rust.ValueRub;
    public bool HasTemporaryFailures => Cs2.TemporaryFailure || Dota2.TemporaryFailure || Rust.TemporaryFailure;
    public bool HasAnySuccessfulGame =>
        (Cs2.Accessible && !Cs2.TemporaryFailure) ||
        (Dota2.Accessible && !Dota2.TemporaryFailure) ||
        (Rust.Accessible && !Rust.TemporaryFailure);
}
