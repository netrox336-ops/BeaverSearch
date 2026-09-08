namespace BeaverSearch.Services;

/// <summary>
/// Compatibility facade kept to avoid a risky constructor migration in v0.4.1.
/// Pricing is no longer sourced from Skinport: all calls delegate to the Steam
/// Community Market RUB provider. The filename/class will be renamed in a later
/// cleanup once the hotfix is proven in runtime.
/// </summary>
public sealed class SkinportPriceProvider
{
    private readonly SteamMarketPriceProvider _steamMarket = new();

    public Task WarmupAsync(CancellationToken ct) => _steamMarket.WarmupAsync(ct);

    public Task<IReadOnlyDictionary<string, decimal>> GetPricesAsync(
        int appId,
        IEnumerable<string> marketHashNames,
        CancellationToken ct) =>
        _steamMarket.GetPricesAsync(appId, marketHashNames, ct);
}
