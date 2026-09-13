using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class InventoryValuationService
{
    private readonly SteamInventoryService _inventory;
    private readonly SteamInventoryPresenceService _presence = new();
    private readonly SkinportPriceProvider _prices;
    private readonly CsFloatComparableProvider _csFloat;
    private readonly SemaphoreSlim _playerValuationGate = new(2, 2);

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
            // One ordinary profile inventory HTML page tells us which app inventories
            // actually contain items. This avoids two needless JSON calls for Dota/Rust
            // on the majority of CS2 players and is the main protection against 429.
            var presence = await _presence.GetAsync(steamId64, ct).ConfigureAwait(false);

            var cs = presence.IsKnownEmpty(730)
                ? EmptyGame(730, "Steam profile inventory: 0 items")
                : await ValueGameAsync(steamId64, 730, csFloatApiKey, ct).ConfigureAwait(false);
            if (cs.TemporaryFailure)
            {
                return new PlayerValuation(
                    cs,
                    presence.IsKnownEmpty(570) ? EmptyGame(570, "Steam profile inventory: 0 items") : DeferredGame(570, "не проверено: CS2 inventory временно недоступен"),
                    presence.IsKnownEmpty(252490) ? EmptyGame(252490, "Steam profile inventory: 0 items") : DeferredGame(252490, "не проверено: CS2 inventory временно недоступен"));
            }

            var dota = presence.IsKnownEmpty(570)
                ? EmptyGame(570, "Steam profile inventory: 0 items")
                : await ValueGameAsync(steamId64, 570, string.Empty, ct).ConfigureAwait(false);
            if (dota.TemporaryFailure)
            {
                return new PlayerValuation(
                    cs,
                    dota,
                    presence.IsKnownEmpty(252490) ? EmptyGame(252490, "Steam profile inventory: 0 items") : DeferredGame(252490, "не проверено: Dota 2 inventory временно недоступен"));
            }

            var rust = presence.IsKnownEmpty(252490)
                ? EmptyGame(252490, "Steam profile inventory: 0 items")
                : await ValueGameAsync(steamId64, 252490, string.Empty, ct).ConfigureAwait(false);
            return new PlayerValuation(cs, dota, rust);
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
            return TemporaryGame(appId, $"Steam inventory transport {ex.GetType().Name}: {ex.Message}");
        }

        if (!inventory.Accessible)
        {
            // Defensive compatibility: current Steam Community behaviour can use
            // HTTP 401 + empty/null body for an unallocated/empty app inventory.
            // SteamInventoryService already normalizes it, but keep this guard so a
            // future parser change cannot turn an empty Rust/Dota inventory into an error.
            if (inventory.HttpStatusCode == 401 && IsNoDetails401(inventory.Error))
                return EmptyGame(appId, "Steam 401 empty/unallocated inventory");

            var error = string.IsNullOrWhiteSpace(inventory.Error)
                ? $"Steam inventory unavailable{(inventory.HttpStatusCode is int code ? $" (HTTP {code})" : string.Empty)}"
                : inventory.Error!;
            return new GameValuation(appId, 0, 0, false, 0)
            {
                TemporaryFailure = inventory.TransientFailure,
                Error = error
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

        if (itemCount == 0)
            return EmptyGame(appId, "0 items");

        IReadOnlyDictionary<string, decimal> prices;
        try
        {
            prices = await _prices.GetPricesAsync(appId, marketNames, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GameValuation(appId, 0, itemCount, true, marketableItems)
            {
                MarketableItems = marketableItems,
                PricingAvailable = false,
                TemporaryFailure = true,
                Error = $"{GameName(appId)} price provider: {ex.Message}"
            };
        }

        var pricingAvailable = marketNames.Count == 0 || prices.Count > 0;
        if (!pricingAvailable && marketableItems > 0)
        {
            return new GameValuation(appId, 0, itemCount, true, marketableItems)
            {
                MarketableItems = marketableItems,
                PricingAvailable = false,
                TemporaryFailure = true,
                Error = $"{GameName(appId)}: ни один price source не вернул цену для marketable-предметов"
            };
        }

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
                catch { }
            }
        }

        var total = lines.Sum(x => x.UnitPrice * x.Asset.Amount);
        return new GameValuation(appId, decimal.Round(total, 2), itemCount, true, unpriced)
        {
            MarketableItems = marketableItems,
            PricingAvailable = pricingAvailable
        };
    }

    private static bool IsNoDetails401(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return true;
        var text = error.ToLowerInvariant();
        return text.Contains("no details") || text.Contains("null") || text.Contains("empty") || text.Contains("unallocated");
    }

    private static GameValuation EmptyGame(int appId, string note) => new(appId, 0, 0, true, 0)
    {
        MarketableItems = 0,
        PricingAvailable = true,
        TemporaryFailure = false,
        Error = note
    };

    private static GameValuation TemporaryGame(int appId, string error) => new(appId, 0, 0, false, 0)
    {
        TemporaryFailure = true,
        Error = error
    };

    private static GameValuation DeferredGame(int appId, string reason) => new(appId, 0, 0, false, 0)
    {
        TemporaryFailure = true,
        Error = reason
    };

    private static string GameName(int appId) => appId switch
    {
        730 => "CS2",
        570 => "Dota 2",
        252490 => "Rust",
        _ => appId.ToString()
    };
}
