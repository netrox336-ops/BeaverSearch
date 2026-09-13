using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace BeaverSearch.Services;

/// <summary>
/// Reads the normal public Steam profile inventory page and extracts g_rgAppContextData.
/// This lets BeaverSearch skip Dota/Rust/CS2 JSON inventory calls when Steam already says
/// the profile has zero items for that app. The HTML endpoint is cached separately and
/// does not use the user's browser cookies or Steam login.
/// </summary>
public sealed class SteamInventoryPresenceService
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(6);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly object _paceSync = new();
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public SteamInventoryPresenceService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<InventoryPresence> GetAsync(string steamId64, CancellationToken ct)
    {
        if (_cache.TryGetValue(steamId64, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Value;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(steamId64, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Value;

            await PaceAsync(ct).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://steamcommunity.com/profiles/{steamId64}/inventory/");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId64}/");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var failed = InventoryPresence.Unknown($"profile inventory HTTP {(int)response.StatusCode}");
                    _cache[steamId64] = new CacheEntry(DateTime.UtcNow + FailureTtl, failed);
                    return failed;
                }

                var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var json = ExtractAssignedJsonObject(html, "g_rgAppContextData");
                if (string.IsNullOrWhiteSpace(json))
                {
                    var failed = InventoryPresence.Unknown("g_rgAppContextData not found");
                    _cache[steamId64] = new CacheEntry(DateTime.UtcNow + FailureTtl, failed);
                    return failed;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    var failed = InventoryPresence.Unknown("g_rgAppContextData is not an object");
                    _cache[steamId64] = new CacheEntry(DateTime.UtcNow + FailureTtl, failed);
                    return failed;
                }

                var apps = new HashSet<int>();
                foreach (var app in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(app.Name, out var appId) || app.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    var count = ReadInt(app.Value, "asset_count");
                    if (count <= 0 && app.Value.TryGetProperty("rgContexts", out var contexts) && contexts.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var context in contexts.EnumerateObject())
                        {
                            if (context.Value.ValueKind == JsonValueKind.Object)
                                count += Math.Max(0, ReadInt(context.Value, "asset_count"));
                        }
                    }
                    if (count > 0) apps.Add(appId);
                }

                var result = new InventoryPresence(true, apps, null);
                _cache[steamId64] = new CacheEntry(DateTime.UtcNow + PositiveTtl, result);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var failed = InventoryPresence.Unknown(ex.GetType().Name + ": " + ex.Message);
                _cache[steamId64] = new CacheEntry(DateTime.UtcNow + FailureTtl, failed);
                return failed;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_paceSync)
        {
            var now = DateTime.UtcNow;
            delay = _nextRequestUtc > now ? _nextRequestUtc - now : TimeSpan.Zero;
            var baseTime = _nextRequestUtc > now ? _nextRequestUtc : now;
            _nextRequestUtc = baseTime + RequestSpacing;
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return 0;
    }

    private static string? ExtractAssignedJsonObject(string text, string variableName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var marker = text.IndexOf(variableName, StringComparison.Ordinal);
        if (marker < 0) return null;
        var equals = text.IndexOf('=', marker + variableName.Length);
        if (equals < 0) return null;
        var start = text.IndexOf('{', equals + 1);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private sealed record CacheEntry(DateTime ExpiresUtc, InventoryPresence Value);
}

public sealed record InventoryPresence(bool Known, IReadOnlySet<int> AppsWithItems, string? Error)
{
    public bool HasItems(int appId) => Known && AppsWithItems.Contains(appId);
    public bool IsKnownEmpty(int appId) => Known && !AppsWithItems.Contains(appId);
    public static InventoryPresence Unknown(string error) => new(false, new HashSet<int>(), error);
}
