using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class CsFloatComparableProvider
{
    private readonly HttpClient _http;
    private readonly CbrCurrencyService _currency;
    private readonly ConcurrentDictionary<string, (DateTime LoadedUtc, decimal Rub)> _queryCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queryGates = new(StringComparer.Ordinal);

    public CsFloatComparableProvider(CbrCurrencyService currency)
    {
        _currency = currency;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeaverSearch/0.4.1 (+Windows)");
    }

    public async Task<decimal?> GetComparableRubAsync(
        string apiKey,
        string marketHashName,
        Cs2InspectData inspect,
        decimal baseRub,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || inspect.PaintWear <= 0) return null;

        var minFloat = Math.Max(0, inspect.PaintWear - 0.01f).ToString("0.######", CultureInfo.InvariantCulture);
        var maxFloat = Math.Min(1, inspect.PaintWear + 0.01f).ToString("0.######", CultureInfo.InvariantCulture);
        var query = new List<string>
        {
            "limit=20",
            "sort_by=lowest_price",
            "type=buy_now",
            $"market_hash_name={Uri.EscapeDataString(marketHashName)}",
            $"min_float={minFloat}",
            $"max_float={maxFloat}"
        };
        if (inspect.StickerIds.Count > 0)
            query.Add("stickers=" + Uri.EscapeDataString(string.Join(',', inspect.StickerIds.Distinct().Take(5))));

        var queryString = string.Join('&', query);
        var cacheKey = queryString;
        if (_queryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.LoadedUtc < TimeSpan.FromMinutes(3))
            return BoundComparable(cached.Rub, baseRub);

        var gate = _queryGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_queryCache.TryGetValue(cacheKey, out cached) && DateTime.UtcNow - cached.LoadedUtc < TimeSpan.FromMinutes(3))
                return BoundComparable(cached.Rub, baseRub);

            using var response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://csfloat.com/api/v1/listings?" + queryString);
                // CSFloat documents the API key as the raw Authorization header.
                req.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
                return req;
            }, ct);

            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var cents = doc.RootElement.EnumerateArray()
                .Select(x => x.TryGetProperty("price", out var p) && p.TryGetInt64(out var v) ? v : 0)
                .Where(x => x > 0)
                .Take(8)
                .OrderBy(x => x)
                .ToArray();
            if (cents.Length == 0) return null;

            var medianCents = cents[cents.Length / 2];
            var usdRub = await _currency.GetUsdRubAsync(ct);
            var rub = decimal.Round((medianCents / 100m) * usdRub, 2);
            _queryCache[cacheKey] = (DateTime.UtcNow, rub);
            return BoundComparable(rub, baseRub);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last?.Dispose();
            using var request = requestFactory();
            last = await _http.SendAsync(request, ct);
            var code = (int)last.StatusCode;
            if (code != 429 && code < 500) return last;
            if (attempt == 2) break;

            var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(450 * (attempt + 1));
            if (delay > TimeSpan.FromSeconds(4)) delay = TimeSpan.FromSeconds(4);
            await Task.Delay(delay, ct);
        }
        return last!;
    }

    private static decimal? BoundComparable(decimal rub, decimal baseRub)
    {
        // Active listings can be noisy. Keep the advanced correction bounded.
        if (baseRub > 0 && (rub < baseRub * 0.55m || rub > baseRub * 8m)) return null;
        return rub > 0 ? rub : null;
    }
}
