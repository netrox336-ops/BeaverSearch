using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

/// <summary>
/// Conservative reader for Steam Community public inventories.
///
/// The undocumented Community endpoint is rate-limited by IP. The important rule here
/// is therefore one request lane, no duplicate legacy retry storm, long pacing, and a
/// real cooldown after 403/429. HTTP 401 with an empty/null body is treated as an empty
/// app inventory rather than an authentication failure; this is a current behaviour of
/// the public endpoint for app inventories that are not allocated for a profile.
/// </summary>
public sealed class SteamInventoryService
{
    private const int PageSize = 2000;
    private static readonly TimeSpan NormalSpacing = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ThrottledSpacing = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _inventoryRequestGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _paceSync = new();
    private DateTime _nextRequestUtc = DateTime.MinValue;
    private DateTime _cooldownUntilUtc = DateTime.MinValue;
    private int _throttleStrikes;
    private bool _anonymousSessionPrimed;

    public SteamInventoryService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<SteamInventory> GetInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        await _inventoryRequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAnonymousSessionAsync(ct).ConfigureAwait(false);
            return await GetInventoryCoreAsync(steamId64, appId, ct).ConfigureAwait(false);
        }
        finally
        {
            _inventoryRequestGate.Release();
        }
    }

    private async Task EnsureAnonymousSessionAsync(CancellationToken ct)
    {
        if (_anonymousSessionPrimed) return;
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_anonymousSessionPrimed) return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://steamcommunity.com/");
                request.Headers.Referrer = new Uri("https://steamcommunity.com/");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { _anonymousSessionPrimed = true; }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<SteamInventory> GetInventoryCoreAsync(string steamId64, int appId, CancellationToken ct)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal);
        string? startAssetId = null;

        for (var page = 0; page < 30; page++)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId64}/{appId}/2?l=english&count={PageSize}";
            if (!string.IsNullOrWhiteSpace(startAssetId))
                url += $"&start_assetid={Uri.EscapeDataString(startAssetId)}";

            var payload = await SendPageAsync(url, steamId64, ct).ConfigureAwait(false);
            if (payload.EmptyInventory)
                return Empty(appId, payload.StatusCode);
            if (!payload.Success)
                return Failure(appId, payload.Error, payload.Transient, payload.StatusCode);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload.Body);
            }
            catch (JsonException ex)
            {
                return Failure(appId, $"Steam inventory HTTP {payload.StatusCode ?? 200} invalid JSON: {ex.Message}", true, payload.StatusCode);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return Failure(appId, "Steam inventory returned JSON null", true, payload.StatusCode);
                if (root.ValueKind != JsonValueKind.Object)
                    return Failure(appId, "Steam inventory root is not an object", true, payload.StatusCode);

                var accessible = ReadSuccess(root) ||
                                 (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array) ||
                                 (root.TryGetProperty("descriptions", out var descriptionArray) && descriptionArray.ValueKind == JsonValueKind.Array);
                if (!accessible)
                {
                    var detail = NormalizeErrorText(ReadSteamError(root));
                    var message = string.IsNullOrWhiteSpace(detail)
                        ? "Steam inventory response success=false without details"
                        : detail!;
                    return Failure(appId, message, !IsPrivateInventoryMessage(message), payload.StatusCode);
                }

                ParseAssets(root, allAssets);
                ParseDescriptions(root, descriptions);

                var more = ReadFlag(root, "more_items");
                startAssetId = GetString(root, "last_assetid");
                if (!more || string.IsNullOrWhiteSpace(startAssetId))
                    break;
            }
        }

        return new SteamInventory
        {
            AppId = appId,
            Accessible = true,
            Assets = allAssets,
            Descriptions = descriptions
        };
    }

    private async Task<RequestPayload> SendPageAsync(string url, string steamId64, CancellationToken ct)
    {
        try
        {
            await PaceAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId64}/inventory/");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                RegisterSuccess();
                if (IsNullishBody(body))
                    return new RequestPayload(false, false, body, "Steam inventory HTTP 200 returned empty/null body", true, code);
                return new RequestPayload(true, false, body, string.Empty, false, code);
            }

            // Steam currently uses 401 + null/empty for some valid profiles that simply
            // do not have an allocated inventory for the requested app. Do not turn this
            // into a retry storm or a partial-valuation error.
            if (response.StatusCode == HttpStatusCode.Unauthorized && IsNullishBody(body))
            {
                RegisterSuccess();
                return new RequestPayload(false, true, body, "empty app inventory", false, code);
            }

            var message = DescribeHttpError(response, body);
            var explicitlyPrivate = IsPrivateInventoryMessage(message);

            if (code is 403 or 429)
                RegisterThrottleStrike(code);

            if (explicitlyPrivate)
                return new RequestPayload(false, false, body, message, false, code);

            // No immediate retry for 401/403/429. A second request to the same Steam host
            // is exactly what made the previous builds extend the IP throttle.
            return new RequestPayload(false, false, body, message, true, code);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RequestPayload(false, false, string.Empty, $"Steam inventory transport {ex.GetType().Name}: {ex.Message}", true, null);
        }
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_paceSync)
        {
            var now = DateTime.UtcNow;
            var target = _nextRequestUtc;
            if (_cooldownUntilUtc > target) target = _cooldownUntilUtc;
            if (target < now) target = now;

            delay = target - now;
            var spacing = _throttleStrikes > 0 ? ThrottledSpacing : NormalSpacing;
            _nextRequestUtc = target + spacing;
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private void RegisterThrottleStrike(int statusCode)
    {
        lock (_paceSync)
        {
            _throttleStrikes = Math.Min(5, _throttleStrikes + 1);
            var seconds = statusCode == 429
                ? _throttleStrikes switch { 1 => 90, 2 => 180, 3 => 300, _ => 420 }
                : _throttleStrikes switch { 1 => 60, 2 => 120, 3 => 240, _ => 360 };
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
        }
    }

    private void RegisterSuccess()
    {
        lock (_paceSync)
        {
            if (_throttleStrikes > 0) _throttleStrikes--;
            if (_throttleStrikes == 0 && _cooldownUntilUtc <= DateTime.UtcNow)
                _cooldownUntilUtc = DateTime.MinValue;
        }
    }

    private static void ParseAssets(JsonElement root, List<InventoryAsset> output)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return;
        foreach (var asset in assets.EnumerateArray())
        {
            var assetId = GetString(asset, "assetid");
            var classId = GetString(asset, "classid");
            var instanceId = GetString(asset, "instanceid") ?? "0";
            var amount = long.TryParse(GetString(asset, "amount"), out var n) ? Math.Max(1, n) : 1;
            if (!string.IsNullOrWhiteSpace(assetId) && !string.IsNullOrWhiteSpace(classId))
                output.Add(new InventoryAsset(assetId, classId, instanceId, amount));
        }
    }

    private static void ParseDescriptions(JsonElement root, Dictionary<string, InventoryDescription> output)
    {
        if (!root.TryGetProperty("descriptions", out var descriptions) || descriptions.ValueKind != JsonValueKind.Array) return;
        foreach (var description in descriptions.EnumerateArray())
        {
            var classId = GetString(description, "classid") ?? string.Empty;
            var instanceId = GetString(description, "instanceid") ?? "0";
            if (string.IsNullOrWhiteSpace(classId)) continue;
            var marketName = GetString(description, "market_hash_name")
                             ?? GetString(description, "market_name")
                             ?? GetString(description, "name")
                             ?? string.Empty;
            output[$"{classId}:{instanceId}"] = new InventoryDescription
            {
                ClassId = classId,
                InstanceId = instanceId,
                MarketHashName = marketName,
                Marketable = ReadFlag(description, "marketable"),
                InspectLink = FindInspectLink(description)
            };
        }
    }

    private static SteamInventory Empty(int appId, int? status = null) => new()
    {
        AppId = appId,
        Accessible = true,
        HttpStatusCode = status,
        Assets = [],
        Descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal)
    };

    private static SteamInventory Failure(int appId, string message, bool transient, int? status = null) => new()
    {
        AppId = appId,
        Accessible = false,
        TransientFailure = transient,
        Error = string.IsNullOrWhiteSpace(message) ? "Steam inventory request failed without details" : message.Trim(),
        HttpStatusCode = status
    };

    private static string DescribeHttpError(HttpResponseMessage response, string body)
    {
        var code = (int)response.StatusCode;
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase!;
        var detail = NormalizeErrorText(ReadSteamError(body));
        if (!string.IsNullOrWhiteSpace(detail))
            return $"HTTP {code} {reason}: {detail}";
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => $"HTTP {code} {reason}: Steam Community inventory rate limit",
            HttpStatusCode.Forbidden => $"HTTP {code} {reason}: Steam temporarily rejected the public inventory request",
            HttpStatusCode.Unauthorized => $"HTTP {code} {reason}: no details",
            _ => $"HTTP {code} {reason}: no details"
        };
    }

    private static bool IsNullishBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return true;
        var text = body.Trim();
        return text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("{}", StringComparison.Ordinal) ||
               text.Equals("[]", StringComparison.Ordinal);
    }

    private static string? NormalizeErrorText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return IsNullishBody(text) ? null : text;
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

    private static bool ReadSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n == 1,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static bool ReadFlag(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
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
        if (root.ValueKind == JsonValueKind.String) return root.GetString();
        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (root.ValueKind != JsonValueKind.Object) return root.GetRawText();
        foreach (var key in new[] { "Error", "error", "message", "strError" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    private static string? FindInspectLink(JsonElement description)
    {
        foreach (var property in new[] { "actions", "owner_actions" })
        {
            if (!description.TryGetProperty(property, out var actions) || actions.ValueKind != JsonValueKind.Array) continue;
            foreach (var action in actions.EnumerateArray())
            {
                var link = GetString(action, "link");
                if (!string.IsNullOrWhiteSpace(link) && link.Contains("econ_action_preview", StringComparison.OrdinalIgnoreCase))
                    return link;
            }
        }
        return null;
    }

    private sealed record RequestPayload(
        bool Success,
        bool EmptyInventory,
        string Body,
        string Error,
        bool Transient,
        int? StatusCode);
}
