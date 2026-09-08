using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class SteamInventoryService
{
    private readonly HttpClient _http;

    public SteamInventoryService()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BeaverSearch/0.4.1");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<SteamInventory> GetInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal);
        string? startAssetId = null;
        var accessible = false;

        for (var page = 0; page < 20; page++)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId64}/{appId}/2?l=english&count=5000";
            if (!string.IsNullOrWhiteSpace(startAssetId)) url += $"&start_assetid={Uri.EscapeDataString(startAssetId)}";

            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return new SteamInventory { AppId = appId, Accessible = false };
            if (!response.IsSuccessStatusCode)
                return new SteamInventory { AppId = appId, Accessible = false };

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            accessible = root.TryGetProperty("success", out var success) &&
                         (success.ValueKind == JsonValueKind.Number ? success.GetInt32() == 1 : success.GetBoolean());
            if (!accessible) return new SteamInventory { AppId = appId, Accessible = false };

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var assetId = GetString(a, "assetid");
                    var classId = GetString(a, "classid");
                    var instanceId = GetString(a, "instanceid");
                    var amount = long.TryParse(GetString(a, "amount"), out var n) ? n : 1;
                    if (assetId is not null && classId is not null && instanceId is not null)
                        allAssets.Add(new InventoryAsset(assetId, classId, instanceId, amount));
                }
            }

            if (root.TryGetProperty("descriptions", out var descs) && descs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in descs.EnumerateArray())
                {
                    var classId = GetString(d, "classid") ?? string.Empty;
                    var instanceId = GetString(d, "instanceid") ?? string.Empty;
                    var marketName = GetString(d, "market_hash_name") ?? GetString(d, "name") ?? string.Empty;
                    var marketable = d.TryGetProperty("marketable", out var m) && m.TryGetInt32(out var mi) && mi == 1;
                    var inspect = FindInspectLink(d);
                    descriptions[$"{classId}:{instanceId}"] = new InventoryDescription
                    {
                        ClassId = classId,
                        InstanceId = instanceId,
                        MarketHashName = marketName,
                        Marketable = marketable,
                        InspectLink = inspect
                    };
                }
            }

            var more = root.TryGetProperty("more_items", out var moreItems) &&
                       (moreItems.ValueKind == JsonValueKind.Number ? moreItems.GetInt32() == 1 : moreItems.GetBoolean());
            startAssetId = root.TryGetProperty("last_assetid", out var last) ? last.GetString() : null;
            if (!more || string.IsNullOrWhiteSpace(startAssetId)) break;
        }

        return new SteamInventory { AppId = appId, Accessible = accessible, Assets = allAssets, Descriptions = descriptions };
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last?.Dispose();
            last = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)last.StatusCode;
            if (code != 429 && code < 500) return last;
            if (attempt < 2)
            {
                var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(350 * (attempt + 1));
                if (delay > TimeSpan.FromSeconds(4)) delay = TimeSpan.FromSeconds(4);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        return last!;
    }

    private static string? FindInspectLink(JsonElement d)
    {
        foreach (var prop in new[] { "actions", "owner_actions" })
        {
            if (!d.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var action in arr.EnumerateArray())
            {
                var link = GetString(action, "link");
                if (!string.IsNullOrWhiteSpace(link) && link.Contains("econ_action_preview", StringComparison.OrdinalIgnoreCase))
                    return link;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null
        };
    }
}
