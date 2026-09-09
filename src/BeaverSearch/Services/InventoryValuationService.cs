using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class InventoryValuationService
{
    private readonly SteamInventoryService _inventory;
    private readonly SkinportPriceProvider _prices;
    private readonly CsFloatComparableProvider _csFloat;
    private readonly SemaphoreSlim _playerValuationGate = new(4, 4);

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
        await _playerValuationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Do NOT fan out CS2/Dota/Rust at once. Steam Community inventory is an
            // IP-rate-limited endpoint; three simultaneous requests per player caused
            // bursts of HTTP 403/429 and the old "CS2:null | Dota:null | Rust:null"
            // errors. Query games sequentially and stop immediately on a transient
            // Steam failure so two more requests cannot make the throttle worse.
            var cs = await ValueGameAsync(steamId64, 730, csFloatApiKey, ct).ConfigureAwait(false);
            ThrowIfTemporary(cs);

            var dota = await ValueGameAsync(steamId64, 570, string.Empty, ct).ConfigureAwait(false);
            ThrowIfTemporary(dota);

            var rust = await ValueGameAsync(steamId64, 252490, string.Empty, ct).ConfigureAwait(false);
            ThrowIfTemporary(rust);

            var result = new PlayerValuation(cs, dota, rust);
            var games = new[] { result.Cs2, result.Dota2, result.Rust };
            if (games.Any(x => x.Accessible && x.MarketableItems > 0 && !x.PricingAvailable))
            {
                var affected = string.Join(", ", games
                    .Where(x => x.Accessible && x.MarketableItems > 0 && !x.PricingAvailable)
                    .Select(x => GameName(x.AppId)));
                throw new HttpRequestException($"Price provider не вернул ни одной цены для marketable inventory: {affected}. Ложный 0 ₽ не кэшируется.");
            }

            return result;
        }
        finally
        {
            _playerValuationGate.Release();
        }
    }

    private async Task<GameValuation> ValueGameAsync(string steamId64, int appId, string csFloatApiKey, CancellationToken ct)
    {
        SteamInventory inventory;
        try
        {
            inventory = await _inventory.GetInventoryAsync(steamId64, appId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GameValuation(appId, 0, 0, false, 0)
            {
                TemporaryFailure = true,
                Error = $"Steam inventory transport {ex.GetType().Name}: {ex.Message}"
            };
        }

        if (!inventory.Accessible)
        {
            return new GameValuation(appId, 0, 0, false, 0)
            {
                TemporaryFailure = inventory.TransientFailure,
                Error = string.IsNullOrWhiteSpace(inventory.Error)
                    ? $"Steam inventory unavailable{(inventory.HttpStatusCode is int code ? $" (HTTP {code})" : string.Empty)}"
                    : inventory.Error
            };
        }

        var itemCount = 0;
        var marketableItems = 0;
        var marketNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in inventory.Assets)
        {
            var amount = (int)Math.Min(asset.Amount, int.MaxValue);
            itemCount += amount;
            if (!inventory.Descriptions.TryGetValue($"{asset.ClassId}:{asset.InstanceId}", out var desc)) continue;
            if (!desc.Marketable || string.IsNullOrWhiteSpace(desc.MarketHashName)) continue;
            marketableItems += amount;
            marketNames.Add(desc.MarketHashName.Trim());
        }

        IReadOnlyDictionary<string, decimal> prices;
        try
        {
            prices = await _prices.GetPricesAsync(appId, marketNames, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new HttpRequestException($"{GameName(appId)} price provider: {ex.Message}", ex);
        }

        var pricingAvailable = marketNames.Count == 0 || prices.Count > 0;
        var lines = new List<(InventoryAsset Asset, InventoryDescription? Desc, decimal UnitPrice)>(inventory.Assets.Count);
        var unpriced = 0;

        foreach (var asset in inventory.Assets)
        {
            inventory.Descriptions.TryGetValue($"{asset.ClassId}:{asset.InstanceId}", out var desc);
            decimal price = 0;
            if (desc is not null && desc.Marketable && !string.IsNullOrWhiteSpace(desc.MarketHashName))
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
                catch { /* Advanced valuation is best-effort; base market price remains valid. */ }
            }
        }

        var total = lines.Sum(x => x.UnitPrice * x.Asset.Amount);
        return new GameValuation(appId, decimal.Round(total, 2), itemCount, true, unpriced)
        {
            MarketableItems = marketableItems,
            PricingAvailable = pricingAvailable
        };
    }

    private static void ThrowIfTemporary(GameValuation game)
    {
        if (!game.TemporaryFailure) return;
        var reason = string.IsNullOrWhiteSpace(game.Error) ? "временная ошибка Steam inventory без деталей" : game.Error.Trim();
        throw new HttpRequestException($"{GameName(game.AppId)}: {reason}");
    }

    private static string GameName(int appId) => appId switch
    {
        730 => "CS2",
        570 => "Dota 2",
        252490 => "Rust",
        _ => appId.ToString()
    };
}
