using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class SteamInventoryService
{
    // Steam Community inventory is heavily rate-limited by IP. Keep both page size and
    // request rate conservative: correctness is more important than flooding Steam and
    // turning every public inventory into HTTP 403/429.
    private const int PageSize = 1000;
    private static readonly TimeSpan NormalSpacing = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ThrottledSpacing = TimeSpan.FromSeconds(5);

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
        // Use a private in-process anonymous cookie jar. We never read the user's browser
        // profile/cookies, but Steam still gets the same anonymous session continuity a
        // normal fresh browser tab would have.
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
                // The purpose of this request is only to establish an anonymous Steam
                // Community cookie/session jar. A non-2xx response is not fatal here.
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Inventory request below will expose the real transport/status error.
            }
            finally
            {
                _anonymousSessionPrimed = true;
            }
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
        var accessible = false;

        for (var page = 0; page < 40; page++)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId64}/{appId}/2?l=english&count={PageSize}";
            if (!string.IsNullOrWhiteSpace(startAssetId))
                url += $"&start_assetid={Uri.EscapeDataString(startAssetId)}";

            HttpResponseMessage response;
            try
            {
                response = await GetWithRetryAsync(url, steamId64, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failure(appId, $"Steam inventory transport {ex.GetType().Name}: {ex.Message}", transient: true);
            }

            using (response)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return Failure(appId, $"Steam inventory body read failed: {ex.Message}", transient: true, status: (int)response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var message = DescribeHttpError(response, body);
                    var privateInventory = IsPrivateInventoryFailure(response.StatusCode, message);
                    return Failure(appId, message, transient: !privateInventory, status: (int)response.StatusCode);
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(body);
                }
                catch (JsonException ex)
                {
                    return Failure(
                        appId,
                        $"Steam inventory HTTP {(int)response.StatusCode} returned invalid JSON: {ex.Message}",
                        transient: true,
                        status: (int)response.StatusCode);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    accessible = ReadSuccess(root);
                    if (!accessible)
                    {
                        var detail = ReadSteamError(root);
                        var message = string.IsNullOrWhiteSpace(detail)
                            ? "Steam inventory response success=false without an error message"
                            : detail!;
                        var privateInventory = IsPrivateInventoryMessage(message);
                        return Failure(appId, message, transient: !privateInventory, status: (int)response.StatusCode);
                    }

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var assetId = GetString(asset, "assetid");
                            var classId = GetString(asset, "classid");
                            var instanceId = GetString(asset, "instanceid");
                            var amount = long.TryParse(GetString(asset, "amount"), out var n) ? n : 1;
                            if (assetId is not null && classId is not null && instanceId is not null)
                                allAssets.Add(new InventoryAsset(assetId, classId, instanceId, amount));
                        }
                    }

                    if (root.TryGetProperty("descriptions", out var descs) && descs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var description in descs.EnumerateArray())
                        {
                            var classId = GetString(description, "classid") ?? string.Empty;
                            var instanceId = GetString(description, "instanceid") ?? string.Empty;
                            var marketName = GetString(description, "market_hash_name")
                                             ?? GetString(description, "market_name")
                                             ?? GetString(description, "name")
                                             ?? string.Empty;
                            var marketable = ReadFlag(description, "marketable");
                            var inspect = FindInspectLink(description);
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
        }

        return new SteamInventory
        {
            AppId = appId,
            Accessible = accessible,
            Assets = allAssets,
            Descriptions = descriptions
        };
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, string steamId64, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last?.Dispose();
            await PaceAsync(ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId64}/inventory/");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

            last = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)last.StatusCode;

            if (code is 403 or 429)
                RegisterThrottleStrike();
            else if (code < 500)
                RegisterSuccess();

            // Retry only statuses that can realistically recover. Do not hammer Steam
            // four times for the same player if it has already started throttling us.
            if (code != 429 && code != 403 && code < 500) return last;
            if (attempt < 2)
            {
                var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(code is 403 or 429 ? 1800 * (attempt + 1) : 750 * (attempt + 1));
                if (delay > TimeSpan.FromSeconds(8)) delay = TimeSpan.FromSeconds(8);
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
                1 => 15,
                2 => 30,
                3 => 60,
                _ => 90
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

        if (string.IsNullOrWhiteSpace(detail))
        {
            return response.StatusCode switch
            {
                HttpStatusCode.Forbidden => $"Steam inventory HTTP {code} {reason}: empty/null body; Steam is temporarily rejecting inventory requests (rate limit/anti-bot) rather than proving the inventory is private",
                HttpStatusCode.TooManyRequests => $"Steam inventory HTTP {code} {reason}: Steam rate limit reached",
                _ => $"Steam inventory HTTP {code} {reason}"
            };
        }

        return $"Steam inventory HTTP {code} {reason}: {detail}";
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
            _ => null
        };
    }
}
