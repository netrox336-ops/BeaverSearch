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

    public async Task WarmupAsync(CancellationToken ct)
    {
        // Caller treats warmup as best-effort. Do not swallow provider failures here so
        // diagnostics/logging can distinguish a complete warm cache from a failed warmup.
        await Task.WhenAll(
            _prices.GetPricesAsync(730, ct),
            _prices.GetPricesAsync(570, ct),
            _prices.GetPricesAsync(252490, ct));
    }

    public async Task<PlayerValuation> ValuePlayerAsync(string steamId64, string csFloatApiKey, CancellationToken ct)
    {
        var cs = ValueGameAsync(steamId64, 730, csFloatApiKey, ct);
        var dota = ValueGameAsync(steamId64, 570, string.Empty, ct);
        var rust = ValueGameAsync(steamId64, 252490, string.Empty, ct);
        await Task.WhenAll(cs, dota, rust);
        return new PlayerValuation(await cs, await dota, await rust);
    }

    private async Task<GameValuation> ValueGameAsync(string steamId64, int appId, string csFloatApiKey, CancellationToken ct)
    {
        var inventoryTask = _inventory.GetInventoryAsync(steamId64, appId, ct);
        var pricesTask = _prices.GetPricesAsync(appId, ct);
        await Task.WhenAll(inventoryTask, pricesTask);
        var inventory = await inventoryTask;
        if (!inventory.Accessible) return new GameValuation(appId, 0, 0, false, 0);

        var prices = await pricesTask;
        var lines = new List<(InventoryAsset Asset, InventoryDescription? Desc, decimal UnitPrice)>();
        var unpriced = 0;
        var itemCount = 0;

        foreach (var asset in inventory.Assets)
        {
            itemCount += (int)Math.Min(asset.Amount, int.MaxValue);
            inventory.Descriptions.TryGetValue($"{asset.ClassId}:{asset.InstanceId}", out var desc);
            decimal price = 0;
            if (desc is not null && !string.IsNullOrWhiteSpace(desc.MarketHashName))
                prices.TryGetValue(desc.MarketHashName, out price);
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
                        csFloatApiKey, candidate.Desc.MarketHashName, inspect, candidate.UnitPrice, ct);
                    if (comparable is null || comparable <= 0) continue;
                    var index = lines.FindIndex(x => ReferenceEquals(x.Desc, candidate.Desc) && x.Asset.AssetId == candidate.Asset.AssetId);
                    if (index >= 0) lines[index] = (candidate.Asset, candidate.Desc, comparable.Value);
                }
                catch { /* Advanced valuation is best-effort; base price remains valid. */ }
            }
        }

        var total = lines.Sum(x => x.UnitPrice * x.Asset.Amount);
        return new GameValuation(appId, decimal.Round(total, 2), itemCount, true, unpriced);
    }
}
