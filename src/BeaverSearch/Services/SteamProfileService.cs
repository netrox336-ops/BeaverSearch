using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class SteamProfileService
{
    private readonly HttpClient _http;

    public SteamProfileService()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BeaverSearch/0.4.1 (+Windows)");
    }

    public async Task<SteamProfile> GetAsync(string steamId64, string fallbackNick, CancellationToken ct)
    {
        var url = $"https://steamcommunity.com/profiles/{steamId64}?xml=1";
        try
        {
            var xml = await _http.GetStringAsync(url, ct);
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            var nick = root?.Element("steamID")?.Value?.Trim();
            var avatar = root?.Element("avatarFull")?.Value?.Trim();
            return new SteamProfile(steamId64,
                string.IsNullOrWhiteSpace(nick) ? fallbackNick : WebUtility.HtmlDecode(nick),
                $"https://steamcommunity.com/profiles/{steamId64}",
                avatar ?? string.Empty);
        }
        catch
        {
            return new SteamProfile(steamId64, fallbackNick,
                $"https://steamcommunity.com/profiles/{steamId64}", string.Empty);
        }
    }
}
