using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BeaverSearch.Services;

/// <summary>
/// Fast inventory pricing for mass scans.
///
/// A full Skinport catalog is loaded lazily once per app and cached for 20 minutes.
/// This replaces hundreds of Steam Market priceoverview calls during a server batch.
/// Missing catalog entries fall back to the selective Steam Community Market provider.
/// All JSON/network work stays off the WPF dispatcher.
/// </summary>
public sealed class SkinportPriceProvider
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(2);
    private const int MaxSteamFallbackNames = 16;

    private readonly HttpClient _http;
    private readonly SteamMarketPriceProvider _steamMarket = new();
    private readonly SemaphoreSlim _catalogDownloadGate = new(1, 1);
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _appGates = new();
    private readonly ConcurrentDictionary<int, CatalogCacheEntry> _catalogs = new();

    public SkinportPriceProvider()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(28) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) BeaverSearch/0.4.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.6");
    }

    // Do not download three large catalogs when monitoring starts. The first non-empty
    // public inventory loads only the catalog it actually needs.
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

        var catalog = await GetCatalogAsync(appId, ct).ConfigureAwait(false);
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var name in names)
        {
            if (catalog.TryGetValue(name, out var price) && price > 0)
                result[name] = price;
            else
                missing.Add(name);
        }

        // Skinport does not list every Steam item. A small bounded fallback fills gaps
        // without turning one inventory into hundreds of individual HTTP requests.
        if (missing.Count > 0)
        {
            var fallbackNames = missing.Take(MaxSteamFallbackNames).ToArray();
            var fallback = await _steamMarket.GetPricesAsync(appId, fallbackNames, ct).ConfigureAwait(false);
            foreach (var pair in fallback)
                if (pair.Value > 0) result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> GetCatalogAsync(int appId, CancellationToken ct)
    {
        if (_catalogs.TryGetValue(appId, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
        {
            if (hit.Error is not null) throw new HttpRequestException(hit.Error);
            return hit.Prices;
        }

        var appGate = _appGates.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));
        await appGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogs.TryGetValue(appId, out hit) && hit.ExpiresUtc > DateTime.UtcNow)
            {
                if (hit.Error is not null) throw new HttpRequestException(hit.Error);
                return hit.Prices;
            }

            await _catalogDownloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Exception? last = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var response = await _http.GetAsync(
                            $"https://api.skinport.com/v1/items?app_id={appId}&currency=RUB&tradable=0",
                            HttpCompletionOption.ResponseHeadersRead,
                            ct).ConfigureAwait(false);

                        var code = (int)response.StatusCode;
                        if (code == 429 || code >= 500)
                        {
                            if (attempt < 2)
                            {
                                var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(900 * (attempt + 1));
                                if (retry > TimeSpan.FromSeconds(5)) retry = TimeSpan.FromSeconds(5);
                                await Task.Delay(retry, ct).ConfigureAwait(false);
                            }
                            continue;
                        }

                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array)
                            throw new JsonException("Skinport catalog root is not an array.");

                        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (!item.TryGetProperty("market_hash_name", out var n) || n.ValueKind != JsonValueKind.String)
                                continue;
                            var name = n.GetString()?.Trim();
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            var price = FirstPositive(item, "suggested_price", "median_price", "mean_price", "min_price");
                            if (price > 0) prices[name] = price;
                        }

                        if (prices.Count == 0)
                            throw new JsonException("Skinport catalog is empty or contains no prices.");

                        _catalogs[appId] = new CatalogCacheEntry(DateTime.UtcNow + CatalogTtl, prices, null);
                        return prices;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (attempt < 2)
                            await Task.Delay(TimeSpan.FromMilliseconds(650 * (attempt + 1)), ct).ConfigureAwait(false);
                    }
                }

                var message = $"Price catalog {appId} unavailable: {last?.Message ?? "unknown error"}";
                _catalogs[appId] = new CatalogCacheEntry(
                    DateTime.UtcNow + FailureTtl,
                    new Dictionary<string, decimal>(StringComparer.Ordinal),
                    message);
                throw new HttpRequestException(message, last);
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

    private static decimal FirstPositive(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number > 0)
                return number;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text) &&
                    decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    return parsed;
            }
        }
        return 0;
    }

    private sealed record CatalogCacheEntry(
        DateTime ExpiresUtc,
        IReadOnlyDictionary<string, decimal> Prices,
        string? Error);
}
