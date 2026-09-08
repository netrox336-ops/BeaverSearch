using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeaverSearch.Services;

/// <summary>
/// Lightweight Steam Community Market price reader.
///
/// Prices are fetched only for market_hash_name values that actually occur in a
/// scanned inventory. Results are cached globally across players so common items
/// do not cause repeated requests. Currency id 5 is RUB on the Community Market.
/// </summary>
public sealed class SteamMarketPriceProvider
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
    private static readonly Regex NumberRegex = new(@"\d[\d\s\u00A0\u202F]*(?:[\.,]\d{1,2})?", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requestGate = new(4, 4);
    private readonly ConcurrentDictionary<PriceKey, PriceCacheEntry> _cache = new();
    private readonly ConcurrentDictionary<PriceKey, SemaphoreSlim> _itemGates = new();
    private readonly object _paceLock = new();
    private DateTime _nextRequestUtc = DateTime.MinValue;

    public SteamMarketPriceProvider()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36 BeaverSearch/0.4.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.6");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://steamcommunity.com/market/");
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

        var result = new ConcurrentDictionary<string, decimal>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            names,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (name, token) =>
            {
                var price = await GetPriceAsync(appId, name, token).ConfigureAwait(false);
                if (price > 0) result[name] = price;
            }).ConfigureAwait(false);

        return result;
    }

    private async Task<decimal> GetPriceAsync(int appId, string marketHashName, CancellationToken ct)
    {
        var key = new PriceKey(appId, marketHashName);
        if (TryGetCached(key, out var cached)) return cached;

        var itemGate = _itemGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await itemGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(key, out cached)) return cached;

            decimal price = 0;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await _requestGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await PaceRequestsAsync(ct).ConfigureAwait(false);
                    var url = "https://steamcommunity.com/market/priceoverview/" +
                              $"?currency=5&country=RU&appid={appId}&market_hash_name={Uri.EscapeDataString(marketHashName)}";
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    var code = (int)response.StatusCode;
                    if (code == 429 || code >= 500)
                    {
                        if (attempt < 2)
                        {
                            var retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(700 * (attempt + 1));
                            if (retry > TimeSpan.FromSeconds(5)) retry = TimeSpan.FromSeconds(5);
                            await Task.Delay(retry, ct).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (!response.IsSuccessStatusCode) break;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                        break;

                    price = ReadPrice(root, "lowest_price");
                    if (price <= 0) price = ReadPrice(root, "median_price");
                    if (price > 0) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 2)
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), ct).ConfigureAwait(false);
                }
                finally
                {
                    _requestGate.Release();
                }
            }

            var ttl = price > 0 ? PositiveTtl : NegativeTtl;
            _cache[key] = new PriceCacheEntry(DateTime.UtcNow + ttl, price, lastError?.Message);
            return price;
        }
        finally
        {
            itemGate.Release();
        }
    }

    private bool TryGetCached(PriceKey key, out decimal price)
    {
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
        {
            price = hit.PriceRub;
            return true;
        }
        price = 0;
        return false;
    }

    private async Task PaceRequestsAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_paceLock)
        {
            var now = DateTime.UtcNow;
            delay = _nextRequestUtc > now ? _nextRequestUtc - now : TimeSpan.Zero;
            var baseTime = _nextRequestUtc > now ? _nextRequestUtc : now;
            _nextRequestUtc = baseTime + TimeSpan.FromMilliseconds(180);
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static decimal ReadPrice(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return Math.Max(0, number);

        if (value.ValueKind != JsonValueKind.String) return 0;
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var match = NumberRegex.Match(text);
        if (!match.Success) return 0;

        var normalized = match.Value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\u00A0", string.Empty, StringComparison.Ordinal)
            .Replace("\u202F", string.Empty, StringComparison.Ordinal)
            .Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private readonly record struct PriceKey(int AppId, string MarketHashName);
    private sealed record PriceCacheEntry(DateTime ExpiresUtc, decimal PriceRub, string? Error);
}
