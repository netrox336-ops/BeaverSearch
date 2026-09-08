using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class InventoryValuationService
{
    private readonly SteamInventoryService _inventory;
    private readonly SkinportPriceProvider _prices;
    private readonly CsFloatComparableProvider _csFloat;

    public InventoryValuationService(
        SteamInventoryService inventory,
        SkinportPriceProvider prices,
        CsFloatComparableProvider csFloat)
    {
        _inventory = inventory;
        _prices = prices;
        _csFloat = csFloat;
    }

    public Task WarmupAsync(CancellationToken ct) => _prices.WarmupAsync(ct);

    public async Task<PlayerValuation> ValuePlayerAsync(string steamId64, string csFloatApiKey, CancellationToken ct)
    {
        // Inventory/network work must never run on the WPF dispatcher. Each game is
        // independent and the provider has its own global request/cache limits.
        var cs = ValueGameAsync(steamId64, 730, csFloatApiKey, ct);
        var dota = ValueGameAsync(steamId64, 570, string.Empty, ct);
        var rust = ValueGameAsync(steamId64, 252490, string.Empty, ct);
        await Task.WhenAll(cs, dota, rust).ConfigureAwait(false);
        return new PlayerValuation(
            await cs.ConfigureAwait(false),
            await dota.ConfigureAwait(false),
            await rust.ConfigureAwait(false));
    }

    private async Task<GameValuation> ValueGameAsync(string steamId64, int appId, string csFloatApiKey, CancellationToken ct)
    {
        var inventory = await _inventory.GetInventoryAsync(steamId64, appId, ct).ConfigureAwait(false);
        if (!inventory.Accessible) return new GameValuation(appId, 0, 0, false, 0);

        var itemCount = 0;
        var marketNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in inventory.Assets)
        {
            itemCount += (int)Math.Min(asset.Amount, int.MaxValue);
            if (!inventory.Descriptions.TryGetValue($"{asset.ClassId}:{asset.InstanceId}", out var desc)) continue;
            if (!string.IsNullOrWhiteSpace(desc.MarketHashName)) marketNames.Add(desc.MarketHashName.Trim());
        }

        // Steam Community Market lookup happens only for names that occur in this
        // inventory. The provider deduplicates/cache-shares requests across all players.
        var prices = await _prices.GetPricesAsync(appId, marketNames, ct).ConfigureAwait(false);
        var lines = new List<(InventoryAsset Asset, InventoryDescription? Desc, decimal UnitPrice)>(inventory.Assets.Count);
        var unpriced = 0;

        foreach (var asset in inventory.Assets)
        {
            inventory.Descriptions.TryGetValue($"{asset.ClassId}:{asset.InstanceId}", out var desc);
            decimal price = 0;
            if (desc is not null && !string.IsNullOrWhiteSpace(desc.MarketHashName))
                prices.TryGetValue(desc.MarketHashName.Trim(), out price);
            if (price <= 0) unpriced += (int)Math.Min(asset.Amount, int.MaxValue);
            lines.Add((asset, desc, price));
        }

        if (appId == 730 && !string.IsNullOrWhiteSpace(csFloatApiKey))
        {
            var candidates = lines
                .Where(x => x.UnitPrice >= 1000m && x.Desc is not null && !string.IsNullOrWhiteSpace(x.Desc.InspectLink))
                .OrderByDescending(x => x.UnitPrice * x.Asset.Amount)
                .Take(8)
                .ToList();

            foreach (var candidate in candidates)
            {
                var inspect = Cs2InspectDecoder.TryDecode(candidate.Desc!.InspectLink, steamId64, candidate.Asset.AssetId);
                if (inspect is null) continue;
                try
                {
                    var comparable = await _csFloat.GetComparableRubAsync(
                        csFloatApiKey, candidate.Desc.MarketHashName, inspect, candidate.UnitPrice, ct).ConfigureAwait(false);
                    if (comparable is null || comparable <= 0) continue;
                    var index = lines.FindIndex(x => ReferenceEquals(x.Desc, candidate.Desc) && x.Asset.AssetId == candidate.Asset.AssetId);
                    if (index >= 0) lines[index] = (candidate.Asset, candidate.Desc, comparable.Value);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Advanced valuation is best-effort; base Steam price remains valid. */ }
            }
        }

        var total = lines.Sum(x => x.UnitPrice * x.Asset.Amount);
        return new GameValuation(appId, decimal.Round(total, 2), itemCount, true, unpriced);
    }
}
