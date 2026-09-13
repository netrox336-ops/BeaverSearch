using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BeaverSearch.Services;

/// <summary>
/// Resilient bulk price provider used by mass monitoring.
///
/// The old implementation treated a Skinport 403 as a fatal error for the whole
/// player valuation. This provider never has a single external point of failure:
/// CS2 -> SkinCash public feed -> Skinport -> Steam Market fallback
/// Dota 2 -> market.dota2.net public RUB feed -> Skinport -> Steam Market fallback
/// Rust -> Skinport -> Steam Market fallback.
///
/// Bulk feeds are cached, and only missing names use the slower Steam Market reader.
/// </summary>
public sealed class SkinportPriceProvider
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(2);
    private const int MaxSteamFallbackNames = 20;

    private readonly HttpClient _http;
    private readonly SteamMarketPriceProvider _steamMarket = new();
    private readonly CbrCurrencyService _cbr = new();
    private readonly SemaphoreSlim _catalogDownloadGate = new(1, 1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _appGates = new();
    private readonly ConcurrentDictionary<int, CatalogCacheEntry> _catalogs = new();

    public SkinportPriceProvider()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ru;q=0.7");
    }

    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<IReadOnlyDictionary<string, decimal>> GetPricesAsync(
        int appId,
        IEnumerable<string> marketHashNames,
        CancellationToken ct)
    {
        var names = marketHashNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
            return new Dictionary<string, decimal>(StringComparer.Ordinal);

        var catalog = await GetCatalogSafeAsync(appId, ct).ConfigureAwait(false);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var name in names)
        {
            if (catalog.TryGetValue(name, out var price) && price > 0)
                result[name] = price;
            else
                missing.Add(name);
        }

        // A bulk source outage must not fail the whole player. Fill a bounded number
        // of gaps from Steam Market. Common items are then shared through its cache.
        if (missing.Count > 0)
        {
            var fallback = await _steamMarket.GetPricesAsync(
                appId,
                missing.Take(MaxSteamFallbackNames),
                ct).ConfigureAwait(false);
            foreach (var pair in fallback)
                if (pair.Value > 0) result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> GetCatalogSafeAsync(int appId, CancellationToken ct)
    {
        if (_catalogs.TryGetValue(appId, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
            return hit.Prices;

        var appGate = _appGates.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));
        await appGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogs.TryGetValue(appId, out hit) && hit.ExpiresUtc > DateTime.UtcNow)
                return hit.Prices;

            await _catalogDownloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Dictionary<string, decimal> prices = appId switch
                {
                    730 => await LoadCs2CatalogAsync(ct).ConfigureAwait(false),
                    570 => await LoadDotaCatalogAsync(ct).ConfigureAwait(false),
                    252490 => await LoadSkinportCatalogAsync(appId, ct).ConfigureAwait(false),
                    _ => new Dictionary<string, decimal>(StringComparer.Ordinal)
                };

                var ttl = prices.Count > 0 ? CatalogTtl : FailureTtl;
                _catalogs[appId] = new CatalogCacheEntry(DateTime.UtcNow + ttl, prices);
                return prices;
            }
            finally
            {
                _catalogDownloadGate.Release();
            }
        }
        finally
        {
            appGate.Release();
        }
    }

    private async Task<Dictionary<string, decimal>> LoadCs2CatalogAsync(CancellationToken ct)
    {
        // Primary: free public no-key CS2 price feed. It returns USD prices and is
        // intentionally used only as a valuation reference, not for trading actions.
        try
        {
            using var response = await SendJsonAsync("https://api.skincash.gg/v1/prices", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("items", out var items) &&
                    items.ValueKind == JsonValueKind.Array)
                {
                    var usdRub = await _cbr.GetUsdRubAsync(ct).ConfigureAwait(false);
                    var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    foreach (var item in items.EnumerateArray())
                    {
                        var name = ReadString(item, "market_hash_name");
                        var usd = ReadDecimal(item, "price");
                        if (!string.IsNullOrWhiteSpace(name) && usd > 0 && usdRub > 0)
                            prices[name.Trim()] = decimal.Round(usd * usdRub, 2);
                    }
                    if (prices.Count > 100) return prices;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Secondary: Skinport. Their /v1/items endpoint explicitly requires Brotli.
        return await LoadSkinportCatalogAsync(730, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, decimal>> LoadDotaCatalogAsync(CancellationToken ct)
    {
        // Public RUB feed. No account/API key is needed for the price list.
        try
        {
            using var response = await SendJsonAsync("https://market.dota2.net/api/v2/prices/class_instance/RUB.json", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("items", out var items) &&
                    items.ValueKind == JsonValueKind.Object)
                {
                    var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    foreach (var property in items.EnumerateObject())
                    {
                        var item = property.Value;
                        var name = ReadString(item, "market_hash_name");
                        var price = ReadDecimal(item, "price");
                        if (price <= 0) price = ReadDecimal(item, "avg_price");
                        if (!string.IsNullOrWhiteSpace(name) && price > 0)
                            prices[name.Trim()] = price;
                    }
                    if (prices.Count > 100) return prices;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return await LoadSkinportCatalogAsync(570, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, decimal>> LoadSkinportCatalogAsync(int appId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.skinport.com/v1/items?app_id={appId}&currency=RUB&tradable=0");
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Dictionary<string, decimal>(StringComparer.Ordinal);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new Dictionary<string, decimal>(StringComparer.Ordinal);

            var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = ReadString(item, "market_hash_name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var price = FirstPositive(item, "suggested_price", "median_price", "mean_price", "min_price");
                if (price > 0) prices[name.Trim()] = price;
            }
            return prices;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }
    }

    private async Task<HttpResponseMessage> SendJsonAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static decimal ReadDecimal(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }

    private static decimal FirstPositive(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ReadDecimal(item, property);
            if (value > 0) return value;
        }
        return 0;
    }

    private sealed record CatalogCacheEntry(
        DateTime ExpiresUtc,
        IReadOnlyDictionary<string, decimal> Prices);
}
