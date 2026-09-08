using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BeaverSearch.Services;

public sealed class SkinportPriceProvider
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<int, (DateTime LoadedUtc, Dictionary<string, decimal> Prices)> _cache = new();

    public SkinportPriceProvider()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(24) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeaverSearch/0.4.1 (+Windows)");
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("br");
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetPricesAsync(int appId, CancellationToken ct)
    {
        if (_cache.TryGetValue(appId, out var hit) && DateTime.UtcNow - hit.LoadedUtc < TimeSpan.FromMinutes(5))
            return hit.Prices;

        var gate = _gates.GetOrAdd(appId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(appId, out hit) && DateTime.UtcNow - hit.LoadedUtc < TimeSpan.FromMinutes(5))
                return hit.Prices;

            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var response = await _http.GetAsync($"https://api.skinport.com/v1/items?app_id={appId}&currency=RUB&tradable=0", ct);
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    {
                        if (attempt < 2)
                        {
                            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(450 * (attempt + 1));
                            if (delay > TimeSpan.FromSeconds(4)) delay = TimeSpan.FromSeconds(4);
                            await Task.Delay(delay, ct);
                        }
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetProperty("market_hash_name", out var n)) continue;
                        var name = n.GetString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var price = FirstPositive(item, "median_price", "mean_price", "suggested_price", "min_price");
                        if (price > 0) prices[name] = price;
                    }
                    _cache[appId] = (DateTime.UtcNow, prices);
                    return prices;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(350 * (attempt + 1)), ct);
                }
            }

            // A stale catalog is better than dropping an entire player scan during a brief provider outage.
            if (_cache.TryGetValue(appId, out hit)) return hit.Prices;
            if (last is not null) throw last;
            return new Dictionary<string, decimal>();
        }
        finally { gate.Release(); }
    }

    private static decimal FirstPositive(JsonElement e, params string[] properties)
    {
        foreach (var p in properties)
        {
            if (!e.TryGetProperty(p, out var v) || v.ValueKind == JsonValueKind.Null) continue;
            if (v.TryGetDecimal(out var d) && d > 0) return d;
        }
        return 0;
    }
}
