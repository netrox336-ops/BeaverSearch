using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class SteamInventoryService
{
    // Steam Community tightened inventory limits. count=5000 may return HTTP 403
    // even for public inventories. Keep pages conservative and pace requests so a
    // batch of players cannot make Steam temporarily block BeaverSearch's IP.
    private const int PageSize = 2000;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _inventoryRequestGate = new(2, 2);
    private readonly object _paceSync = new();
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public SteamInventoryService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36 BeaverSearch/0.4.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://steamcommunity.com/");
    }

    public async Task<SteamInventory> GetInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        await _inventoryRequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetInventoryCoreAsync(steamId64, appId, ct).ConfigureAwait(false);
        }
        finally
        {
            _inventoryRequestGate.Release();
        }
    }

    private async Task<SteamInventory> GetInventoryCoreAsync(string steamId64, int appId, CancellationToken ct)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal);
        string? startAssetId = null;
        var accessible = false;

        for (var page = 0; page < 30; page++)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId64}/{appId}/2?l=english&count={PageSize}";
            if (!string.IsNullOrWhiteSpace(startAssetId))
                url += $"&start_assetid={Uri.EscapeDataString(startAssetId)}";

            using var response = await GetWithRetryAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var message = ReadSteamError(body) ?? $"Steam inventory HTTP {(int)response.StatusCode}";
                var privateInventory = IsPrivateInventoryFailure(response.StatusCode, message);
                return new SteamInventory
                {
                    AppId = appId,
                    Accessible = false,
                    TransientFailure = !privateInventory,
                    Error = message,
                    HttpStatusCode = (int)response.StatusCode
                };
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return new SteamInventory
                {
                    AppId = appId,
                    Accessible = false,
                    TransientFailure = true,
                    Error = "Steam inventory returned invalid JSON: " + ex.Message,
                    HttpStatusCode = (int)response.StatusCode
                };
            }

            using (doc)
            {
                var root = doc.RootElement;
                accessible = ReadSuccess(root);
                if (!accessible)
                {
                    var message = ReadSteamError(root) ?? "Steam inventory response success=false";
                    var privateInventory = IsPrivateInventoryMessage(message);
                    return new SteamInventory
                    {
                        AppId = appId,
                        Accessible = false,
                        TransientFailure = !privateInventory,
                        Error = message,
                        HttpStatusCode = (int)response.StatusCode
                    };
                }

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
                        var marketName = GetString(d, "market_hash_name") ?? GetString(d, "market_name") ?? GetString(d, "name") ?? string.Empty;
                        var marketable = ReadFlag(d, "marketable");
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

                var more = ReadFlag(root, "more_items");
                startAssetId = GetString(root, "last_assetid");
                if (!more || string.IsNullOrWhiteSpace(startAssetId)) break;
            }
        }

        return new SteamInventory
        {
            AppId = appId,
            Accessible = accessible,
            Assets = allAssets,
            Descriptions = descriptions
        };
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            last?.Dispose();
            await PaceAsync(ct).ConfigureAwait(false);
            last = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)last.StatusCode;

            // 429/5xx are clearly transient. Steam also occasionally responds 403
            // while throttling inventory traffic, so retry it briefly before deciding
            // whether the body describes a genuinely private inventory.
            if (code != 429 && code != 403 && code < 500) return last;
            if (attempt < 3)
            {
                var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(650 * (attempt + 1));
                if (delay > TimeSpan.FromSeconds(5)) delay = TimeSpan.FromSeconds(5);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        return last!;
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_paceSync)
        {
            var now = DateTime.UtcNow;
            delay = _nextRequestUtc > now ? _nextRequestUtc - now : TimeSpan.Zero;
            var baseTime = _nextRequestUtc > now ? _nextRequestUtc : now;
            _nextRequestUtc = baseTime + TimeSpan.FromMilliseconds(450);
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static bool ReadSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var success)) return false;
        return success.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => success.TryGetInt32(out var n) && n == 1,
            JsonValueKind.String => success.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static bool ReadFlag(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static bool IsPrivateInventoryFailure(HttpStatusCode status, string message)
    {
        if (status == HttpStatusCode.NotFound) return true;
        return IsPrivateInventoryMessage(message);
    }

    private static bool IsPrivateInventoryMessage(string message)
    {
        var text = message.ToLowerInvariant();
        return text.Contains("private") ||
               text.Contains("inventory is not available") ||
               text.Contains("inventory unavailable") ||
               text.Contains("not allowed to view") ||
               text.Contains("profile is private");
    }

    private static string? ReadSteamError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ReadSteamError(doc.RootElement);
        }
        catch
        {
            var text = body.Trim();
            return text.Length <= 240 ? text : text[..240];
        }
    }

    private static string? ReadSteamError(JsonElement root)
    {
        foreach (var key in new[] { "Error", "error", "message", "strError" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
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
