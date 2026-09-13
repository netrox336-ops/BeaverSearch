using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class SteamInventoryService
{
    // Steam Community inventory is heavily rate-limited by IP. Use one global request
    // lane and conservative pacing. When the modern endpoint is temporarily rejected,
    // fall back to the older profile inventory JSON route instead of immediately failing.
    private const int PageSize = 1000;
    private static readonly TimeSpan NormalSpacing = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ThrottledSpacing = TimeSpan.FromSeconds(6);

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
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
    }

    public async Task<SteamInventory> GetInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        await _inventoryRequestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAnonymousSessionAsync(ct).ConfigureAwait(false);

            var modern = await GetModernInventoryAsync(steamId64, appId, ct).ConfigureAwait(false);
            if (modern.Accessible || !modern.TransientFailure)
                return modern;

            // A normal browser can often open the public inventory page while direct
            // JSON calls are being challenged. Visit that page once to refresh anonymous
            // Steam cookies, then try the legacy JSON route used by the profile page.
            await PrimeProfileInventoryAsync(steamId64, appId, ct).ConfigureAwait(false);
            var legacy = await GetLegacyInventoryAsync(steamId64, appId, ct).ConfigureAwait(false);
            if (legacy.Accessible || !legacy.TransientFailure)
                return legacy;

            return Failure(
                appId,
                $"modern: {CleanError(modern.Error)} | legacy: {CleanError(legacy.Error)}",
                transient: true,
                status: legacy.HttpStatusCode ?? modern.HttpStatusCode);
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
        finally { _sessionGate.Release(); }
    }

    private async Task PrimeProfileInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        try
        {
            await PaceAsync(ct).ConfigureAwait(false);
            var url = $"https://steamcommunity.com/profiles/{steamId64}/inventory/#730_2";
            if (appId != 730) url = $"https://steamcommunity.com/profiles/{steamId64}/inventory/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                RegisterSuccess();
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task<SteamInventory> GetModernInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal);
        string? startAssetId = null;
        var accessible = false;

        for (var page = 0; page < 40; page++)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId64}/{appId}/2?l=english&count={PageSize}";
            if (!string.IsNullOrWhiteSpace(startAssetId))
                url += $"&start_assetid={Uri.EscapeDataString(startAssetId)}";

            var payload = await SendInventoryRequestAsync(url, steamId64, ct).ConfigureAwait(false);
            if (!payload.Success)
                return Failure(appId, payload.Error, transient: payload.Transient, status: payload.StatusCode);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload.Body); }
            catch (JsonException ex)
            {
                return Failure(appId, $"modern inventory invalid JSON: {ex.Message}", transient: true, status: payload.StatusCode);
            }

            using (doc)
            {
                var root = doc.RootElement;
                accessible = ReadSuccess(root);
                if (!accessible)
                {
                    var detail = CleanError(ReadSteamError(root));
                    var message = string.IsNullOrWhiteSpace(detail)
                        ? "modern inventory success=false without details"
                        : detail;
                    return Failure(appId, message, transient: !IsPrivateInventoryMessage(message), status: payload.StatusCode);
                }

                ParseModernAssets(root, allAssets);
                ParseModernDescriptions(root, descriptions);

                var more = ReadFlag(root, "more_items");
                startAssetId = GetString(root, "last_assetid");
                if (!more || string.IsNullOrWhiteSpace(startAssetId)) break;
            }
        }

        return Success(appId, accessible, allAssets, descriptions);
    }

    private async Task<SteamInventory> GetLegacyInventoryAsync(string steamId64, int appId, CancellationToken ct)
    {
        var allAssets = new List<InventoryAsset>();
        var descriptions = new Dictionary<string, InventoryDescription>(StringComparer.Ordinal);
        string? start = null;
        var accessible = false;

        for (var page = 0; page < 40; page++)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/inventory/json/{appId}/2/?l=english&count={PageSize}";
            if (!string.IsNullOrWhiteSpace(start))
                url += $"&start={Uri.EscapeDataString(start)}";

            var payload = await SendInventoryRequestAsync(url, steamId64, ct).ConfigureAwait(false);
            if (!payload.Success)
                return Failure(appId, "legacy inventory: " + payload.Error, transient: payload.Transient, status: payload.StatusCode);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload.Body); }
            catch (JsonException ex)
            {
                return Failure(appId, $"legacy inventory invalid JSON: {ex.Message}", transient: true, status: payload.StatusCode);
            }

            using (doc)
            {
                var root = doc.RootElement;
                accessible = ReadSuccess(root);
                if (!accessible)
                {
                    var detail = CleanError(ReadSteamError(root));
                    var message = string.IsNullOrWhiteSpace(detail)
                        ? "legacy inventory success=false without details"
                        : detail;
                    return Failure(appId, message, transient: !IsPrivateInventoryMessage(message), status: payload.StatusCode);
                }

                ParseLegacyAssets(root, allAssets);
                ParseLegacyDescriptions(root, descriptions);

                var more = ReadFlag(root, "more");
                start = GetString(root, "more_start");
                if (!more || string.IsNullOrWhiteSpace(start)) break;
            }
        }

        return Success(appId, accessible, allAssets, descriptions);
    }

    private async Task<RequestPayload> SendInventoryRequestAsync(string url, string steamId64, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                response?.Dispose();
                await PaceAsync(ct).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId64}/inventory/");
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    RegisterSuccess();
                    var normalized = NormalizeErrorText(body);
                    if (string.IsNullOrWhiteSpace(normalized))
                        return new RequestPayload(false, body, "HTTP 200 returned empty/null inventory body", true, code);
                    return new RequestPayload(true, body, string.Empty, false, code);
                }

                var message = DescribeHttpError(response, body);
                var privateInventory = IsPrivateInventoryFailure(response.StatusCode, message);
                if (code is 403 or 429) RegisterThrottleStrike();

                if (privateInventory)
                    return new RequestPayload(false, body, message, false, code);

                if (attempt == 0 && (code is 403 or 429 || code >= 500))
                {
                    await Task.Delay(TimeSpan.FromSeconds(code == 429 ? 5 : 2), ct).ConfigureAwait(false);
                    continue;
                }

                return new RequestPayload(false, body, message, true, code);
            }

            return new RequestPayload(false, string.Empty, "inventory request exhausted retries", true, response is null ? null : (int)response.StatusCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new RequestPayload(false, string.Empty, $"inventory transport {ex.GetType().Name}: {ex.Message}", true, null);
        }
        finally { response?.Dispose(); }
    }

    private static void ParseModernAssets(JsonElement root, List<InventoryAsset> assetsOut)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return;
        foreach (var asset in assets.EnumerateArray())
        {
            var assetId = GetString(asset, "assetid");
            var classId = GetString(asset, "classid");
            var instanceId = GetString(asset, "instanceid") ?? "0";
            var amount = long.TryParse(GetString(asset, "amount"), out var n) ? n : 1;
            if (assetId is not null && classId is not null)
                assetsOut.Add(new InventoryAsset(assetId, classId, instanceId, Math.Max(1, amount)));
        }
    }

    private static void ParseModernDescriptions(JsonElement root, Dictionary<string, InventoryDescription> descriptions)
    {
        if (!root.TryGetProperty("descriptions", out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var description in values.EnumerateArray())
            AddDescription(description, descriptions);
    }

    private static void ParseLegacyAssets(JsonElement root, List<InventoryAsset> assetsOut)
    {
        if (!root.TryGetProperty("rgInventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object) return;
        foreach (var property in inventory.EnumerateObject())
        {
            var asset = property.Value;
            var assetId = GetString(asset, "id") ?? GetString(asset, "assetid") ?? property.Name;
            var classId = GetString(asset, "classid");
            var instanceId = GetString(asset, "instanceid") ?? "0";
            var amount = long.TryParse(GetString(asset, "amount"), out var n) ? n : 1;
            if (!string.IsNullOrWhiteSpace(assetId) && !string.IsNullOrWhiteSpace(classId))
                assetsOut.Add(new InventoryAsset(assetId, classId, instanceId, Math.Max(1, amount)));
        }
    }

    private static void ParseLegacyDescriptions(JsonElement root, Dictionary<string, InventoryDescription> descriptions)
    {
        if (!root.TryGetProperty("rgDescriptions", out var values) || values.ValueKind != JsonValueKind.Object) return;
        foreach (var property in values.EnumerateObject())
            AddDescription(property.Value, descriptions);
    }

    private static void AddDescription(JsonElement description, Dictionary<string, InventoryDescription> descriptions)
    {
        var classId = GetString(description, "classid") ?? string.Empty;
        var instanceId = GetString(description, "instanceid") ?? "0";
        if (string.IsNullOrWhiteSpace(classId)) return;
        var marketName = GetString(description, "market_hash_name")
                         ?? GetString(description, "market_name")
                         ?? GetString(description, "name")
                         ?? string.Empty;
        descriptions[$"{classId}:{instanceId}"] = new InventoryDescription
        {
            ClassId = classId,
            InstanceId = instanceId,
            MarketHashName = marketName,
            Marketable = ReadFlag(description, "marketable"),
            InspectLink = FindInspectLink(description)
        };
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

    private void RegisterThrottleStrike()
    {
        lock (_paceSync)
        {
            _throttleStrikes = Math.Min(4, _throttleStrikes + 1);
            var seconds = _throttleStrikes switch
            {
                1 => 20,
                2 => 40,
                3 => 75,
                _ => 120
            };
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
        }
    }

    private void RegisterSuccess()
    {
        lock (_paceSync)
        {
            if (_throttleStrikes > 0) _throttleStrikes--;
            if (_throttleStrikes == 0 && _cooldownUntilUtc < DateTime.UtcNow)
                _cooldownUntilUtc = DateTime.MinValue;
        }
    }

    private static SteamInventory Success(
        int appId,
        bool accessible,
        List<InventoryAsset> assets,
        Dictionary<string, InventoryDescription> descriptions) => new()
    {
        AppId = appId,
        Accessible = accessible,
        Assets = assets,
        Descriptions = descriptions
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
        var detail = CleanError(ReadSteamError(body));
        if (string.IsNullOrWhiteSpace(detail))
        {
            return response.StatusCode switch
            {
                HttpStatusCode.Forbidden => $"HTTP {code} {reason}: empty/null body (Steam throttle/anti-bot)",
                HttpStatusCode.TooManyRequests => $"HTTP {code} {reason}: Steam rate limit reached",
                _ => $"HTTP {code} {reason}"
            };
        }
        return $"HTTP {code} {reason}: {detail}";
    }

    private static string CleanError(string? value)
    {
        var normalized = NormalizeErrorText(value);
        return string.IsNullOrWhiteSpace(normalized) ? "no details" : normalized;
    }

    private static string? NormalizeErrorText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("{}", StringComparison.Ordinal) ||
            text.Equals("[]", StringComparison.Ordinal))
            return null;
        return text;
    }

    private static bool ReadSuccess(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)) return false;
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
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var n) && n != 0,
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
            if (!description.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var action in arr.EnumerateArray())
            {
                var link = GetString(action, "link");
                if (!string.IsNullOrWhiteSpace(link) && link.Contains("econ_action_preview", StringComparison.OrdinalIgnoreCase))
                    return link;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }

    private sealed record RequestPayload(
        bool Success,
        string Body,
        string Error,
        bool Transient,
        int? StatusCode);
}
