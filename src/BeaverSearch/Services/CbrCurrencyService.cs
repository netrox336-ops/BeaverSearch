using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Xml.Linq;

namespace BeaverSearch.Services;

public sealed class CbrCurrencyService
{
    private readonly HttpClient _http;
    private (DateTime LoadedUtc, decimal UsdRub) _cache;

    public CbrCurrencyService()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeaverSearch/0.4.1");
    }

    public async Task<decimal> GetUsdRubAsync(CancellationToken ct)
    {
        if (_cache.UsdRub > 0 && DateTime.UtcNow - _cache.LoadedUtc < TimeSpan.FromHours(12)) return _cache.UsdRub;
        try
        {
            var xml = await _http.GetStringAsync("https://www.cbr.ru/scripts/XML_daily.asp", ct);
            var doc = XDocument.Parse(xml);
            var usd = doc.Descendants("Valute").FirstOrDefault(x => (string?)x.Element("CharCode") == "USD");
            var raw = usd?.Element("Value")?.Value?.Replace(',', '.');
            var nominalRaw = usd?.Element("Nominal")?.Value;
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
                decimal.TryParse(nominalRaw, out var nominal) && nominal > 0)
            {
                _cache = (DateTime.UtcNow, value / nominal);
                return _cache.UsdRub;
            }
        }
        catch { }
        return _cache.UsdRub > 0 ? _cache.UsdRub : 90m; // fallback only when CBR is unreachable
    }
}
