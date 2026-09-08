using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

/// <summary>
/// Reads the public yooma.su server monitoring pages.
///
/// 0.4.1 hotfix: yooma is client-rendered, so HttpClient markup is parsed first and
/// Microsoft Edge/Chrome headless DOM is used when the live player cards are absent.
/// SteamID64 is accepted only when it is explicitly present in a yooma profile/card URL,
/// a steam/player data attribute or a player-like hydration object. Nicknames are
/// never used to guess an account.
/// </summary>
public sealed class YoomaClient
{
    private const string BaseUrl = "https://yooma.su";
    private static readonly Regex SteamIdRegex = new(@"(?<!\d)(7656\d{13})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ProfilePathRegex = new("""/(?:[a-z]{2}/)?(?:profile|card)/(?<id>7656\d{13})(?:[/?#\"'\\]|$)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProfileAnchorRegex = new(
        """<a\b[^>]*href\s*=\s*[\"'](?<href>[^\"']*/(?:[a-z]{2}/)?(?:profile|card)/(?<id>7656\d{13})[^\"']*)[\"'][^>]*>(?<name>.*?)</a>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SteamAttributeRegex = new(
        """(?:data[-_:]?(?:steam(?:id|[-_]?id|id64)?|player[-_:]?(?:steam)?id)|(?:steam(?:id|_id|Id|Id64)|steam_id64))\s*=\s*[\"']?(?<id>7656\d{13})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamAccountAttributeRegex = new(
        """(?:data[-_:]?(?:steam(?:id|[-_]?id|id64|[-_:]?account)?|account[-_:]?(?:id|steamid))|(?:steam(?:id|_id|Id|Id64|AccountId)|steam_id64|steam64|account_id))\s*=\s*[\"']?(?<id>(?:7656\d{13}|STEAM_[0-5]:[01]:\d{1,10}|\[?U:1:\d{1,10}\]?|\d{1,10}))""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AddressRegex = new(@"(?<!\d)(?<ip>(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{2,5})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ScriptJsonRegex = new(
        """<script\b[^>]*(?:type\s*=\s*[\"']application/(?:json|ld\+json)[\"']|id\s*=\s*[\"']__NEXT_DATA__[\"'])[^>]*>(?<json>.*?)</script>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly string[] PrimaryPagePaths =
    [
        "/ru/servers/public/", "/ru/servers/awp/", "/ru/servers/jail/",
        "/ru/servers/arena/", "/ru/servers/maniac/", "/ru/servers/minigames/",
        "/ru/servers/5x5/", "/ru/servers/retake/", "/ru/servers/dm/",
        "/ru/duels"
    ];

    private static readonly string[] LegacyPagePaths =
    [
        "/ru/servers/0", "/ru/servers/1", "/ru/servers/2", "/ru/servers/3",
        "/ru/servers/4", "/ru/servers/5", "/ru/servers/6", "/ru/servers/7",
        "/ru/servers/8", "/ru/servers/9", "/ru/servers/10"
    ];

    private readonly HttpClient _http;
    private readonly RenderedDomLoader _dom = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pageGate = new(4, 4);
    private (DateTime LoadedUtc, YoomaLiveSnapshot Snapshot)? _cache;

    public YoomaClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/151 Safari/537.36 BeaverSearch/0.4.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.7");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://yooma.su/ru");
    }

    public async Task<YoomaLiveSnapshot> GetLiveAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache is { } hit && DateTime.UtcNow - hit.LoadedUtc < TimeSpan.FromSeconds(20))
            return hit.Snapshot;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache is { } second && DateTime.UtcNow - second.LoadedUtc < TimeSpan.FromSeconds(20))
                return second.Snapshot;

            var attempted = 0;
            var loaded = new List<PageDocument>();
            var players = new List<YoomaPlayer>();
            var servers = new List<YoomaServerInfo>();
            var renderedPages = 0;
            var profileHits = 0;
            string? renderWarning = null;

            async Task LoadStaticBatchAsync(IEnumerable<string> paths)
            {
                var batch = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                attempted += batch.Length;
                var pages = await Task.WhenAll(batch.Select(path => LoadPageSafeAsync(path, ct)));
                ct.ThrowIfCancellationRequested();
                foreach (var page in pages.Where(x => x is not null).Cast<PageDocument>())
                {
                    loaded.Add(page);
                    var parsed = ParsePage(page.Path, page.Html);
                    players.AddRange(parsed.Players);
                    servers.AddRange(parsed.Servers);
                    profileHits += parsed.ProfileLinksFound;
                }
            }

            await LoadStaticBatchAsync(PrimaryPagePaths);
            if (players.Count == 0)
                await LoadStaticBatchAsync(LegacyPagePaths);

            // The live roster is currently hydrated by JavaScript. A plain HTTP GET can
            // therefore be perfectly healthy while containing zero player cards.
            if (players.Count == 0 && _dom.IsAvailable)
            {
                var rawByPath = loaded
                    .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().Html, StringComparer.OrdinalIgnoreCase);

                var rendered = await Task.WhenAll(PrimaryPagePaths.Select(async path =>
                {
                    rawByPath.TryGetValue(path, out var raw);
                    var result = await _dom.LoadAsync(BaseUrl + path, raw, ct, forceRefresh);
                    return (Path: path, Result: result);
                }));
                ct.ThrowIfCancellationRequested();

                foreach (var item in rendered)
                {
                    if (!item.Result.Rendered)
                    {
                        renderWarning ??= item.Result.Error;
                        continue;
                    }
                    renderedPages++;
                    var parsed = ParsePage(item.Path, item.Result.Html);
                    players.AddRange(parsed.Players);
                    servers.AddRange(parsed.Servers);
                    profileHits += parsed.ProfileLinksFound;
                }
            }
            else if (players.Count == 0 && !_dom.IsAvailable)
            {
                renderWarning = "Edge/Chrome не найден: live roster нельзя отрендерить после JavaScript.";
            }

            if (loaded.Count == 0 && renderedPages == 0)
                throw new HttpRequestException("yooma.su не вернул ни одной доступной страницы мониторинга.");

            var dedupPlayers = players
                .Where(x => IsSteamId64(x.SteamId64))
                .GroupBy(x => $"{x.SteamId64}|{NormalizeServerKey(x.ServerKey)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(x => IsAddress(x.ServerAddress))
                    .ThenByDescending(x => !x.Nickname.Equals(x.SteamId64, StringComparison.Ordinal))
                    .First())
                .ToList();

            var serverRows = servers
                .Concat(dedupPlayers.GroupBy(x => NormalizeServerKey(x.ServerKey), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new YoomaServerInfo(
                            first.ServerKey,
                            first.ServerName,
                            first.ServerAddress,
                            "—",
                            g.Select(x => x.SteamId64).Distinct(StringComparer.Ordinal).Count(),
                            0,
                            first.SourcePage);
                    }))
                .GroupBy(x => NormalizeServerKey(x.Key), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var best = g.OrderByDescending(x => IsAddress(x.Address)).ThenByDescending(x => x.Players).First();
                    var playerCount = Math.Max(best.Players, dedupPlayers.Count(p => NormalizeServerKey(p.ServerKey) == NormalizeServerKey(best.Key)));
                    return best with { Players = playerCount };
                })
                .Where(x => x.Players > 0 || IsAddress(x.Address))
                .OrderByDescending(x => x.Players)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var snapshot = new YoomaLiveSnapshot(
                dedupPlayers,
                serverRows,
                loaded.Count,
                attempted,
                Math.Max(profileHits, dedupPlayers.Count),
                renderedPages,
                _dom.EngineName,
                renderWarning,
                DateTime.UtcNow);
            _cache = (DateTime.UtcNow, snapshot);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<PageDocument?> LoadPageSafeAsync(string path, CancellationToken ct)
    {
        await _pageGate.WaitAsync(ct);
        try
        {
            try
            {
                using var response = await SendWithRetryAsync(BaseUrl + path, ct);
                if (!response.IsSuccessStatusCode) return null;
                var html = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(html) ? null : new PageDocument(path, html);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        finally
        {
            _pageGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last?.Dispose();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            last = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)last.StatusCode;
            if (code != 429 && code < 500) return last;
            if (attempt == 2) break;
            var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(350 * (attempt + 1));
            if (delay > TimeSpan.FromSeconds(3)) delay = TimeSpan.FromSeconds(3);
            await Task.Delay(delay, ct);
        }
        return last!;
    }

    internal static PageParseResult ParsePage(string sourcePage, string html)
    {
        var players = new List<YoomaPlayer>();
        var servers = new List<YoomaServerInfo>();
        var decoded = DecodeMarkup(html);
        var addresses = AddressRegex.Matches(decoded)
            .Cast<Match>()
            .Where(m => IsAddress(m.Value))
            .Select(m => (m.Index, Address: m.Value))
            .Distinct()
            .ToArray();
        var profileHits = 0;

        foreach (Match match in ProfileAnchorRegex.Matches(decoded))
        {
            var steamId = match.Groups["id"].Value;
            if (!IsSteamId64(steamId)) continue;
            profileHits++;
            var nickname = CleanText(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(nickname)) nickname = FindNicknameNear(decoded, match.Index, steamId) ?? steamId;
            var address = FindNearestAddress(addresses, match.Index);
            if (address is null && !IsLikelyRosterContext(decoded, match.Index)) continue;
            var context = BuildServerContext(sourcePage, decoded, match.Index, address);
            AddPlayer(players, new YoomaPlayer(nickname, steamId, BuildProfileUrl(steamId), context.Key, context.Name, context.Address, sourcePage));
        }

        // Some monitoring UIs keep the account id in data-steamid/data-player-id and
        // render a vanity/nickname href. This is the exact DOM-element case that was
        // missing from 0.4.1.
        foreach (Match match in SteamAttributeRegex.Matches(decoded))
        {
            var steamId = match.Groups["id"].Value;
            if (!IsSteamId64(steamId) || !IsLikelyRosterContext(decoded, match.Index)) continue;
            profileHits++;
            var nickname = FindNicknameNear(decoded, match.Index, steamId) ?? steamId;
            var address = FindNearestAddress(addresses, match.Index);
            var context = BuildServerContext(sourcePage, decoded, match.Index, address);
            AddPlayer(players, new YoomaPlayer(nickname, steamId, BuildProfileUrl(steamId), context.Key, context.Name, context.Address, sourcePage));
        }

        // Sites often store Steam AccountID (32-bit), Steam2 or Steam3 in an
        // explicit steam/account attribute instead of a 17-digit SteamID64. Conversion
        // is deterministic; unlike nickname matching it cannot resolve to another user.
        foreach (Match match in SteamAccountAttributeRegex.Matches(decoded))
        {
            if (!SteamIdentityParser.TryNormalizeExplicit(match.Groups["id"].Value, out var steamId) || !IsLikelyRosterContext(decoded, match.Index)) continue;
            profileHits++;
            var nickname = FindNicknameNear(decoded, match.Index, steamId) ?? steamId;
            var address = FindNearestAddress(addresses, match.Index);
            var context = BuildServerContext(sourcePage, decoded, match.Index, address);
            AddPlayer(players, new YoomaPlayer(nickname, steamId, BuildProfileUrl(steamId), context.Key, context.Name, context.Address, sourcePage));
        }

        foreach (Match script in ScriptJsonRegex.Matches(decoded))
        {
            var raw = WebUtility.HtmlDecode(script.Groups["json"].Value).Trim();
            if (raw.Length < 2) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                WalkJson(doc.RootElement, sourcePage, null, players, servers, 0);
            }
            catch (JsonException) { }
        }

        // Catch profile routes embedded in framework state that was not tagged as JSON.
        foreach (Match profile in ProfilePathRegex.Matches(decoded))
        {
            var steamId = profile.Groups["id"].Value;
            if (!IsSteamId64(steamId)) continue;
            profileHits++;
            if (players.Any(x => x.SteamId64.Equals(steamId, StringComparison.Ordinal))) continue;
            var address = FindNearestAddress(addresses, profile.Index);
            if (address is null && !IsLikelyRosterContext(decoded, profile.Index)) continue;
            var nickname = FindNicknameNear(decoded, profile.Index, steamId) ?? steamId;
            var context = BuildServerContext(sourcePage, decoded, profile.Index, address);
            AddPlayer(players, new YoomaPlayer(nickname, steamId, BuildProfileUrl(steamId), context.Key, context.Name, context.Address, sourcePage));
        }

        foreach (var (_, address) in addresses)
        {
            var firstIndex = decoded.IndexOf(address, StringComparison.OrdinalIgnoreCase);
            var context = BuildServerContext(sourcePage, decoded, Math.Max(0, firstIndex), address);
            if (servers.All(x => !x.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                servers.Add(new YoomaServerInfo(context.Key, context.Name, address, context.Map, 0, context.MaxPlayers, sourcePage));
        }

        return new PageParseResult(players, servers, profileHits);
    }

    private static void WalkJson(
        JsonElement element,
        string sourcePage,
        ServerContext? inherited,
        List<YoomaPlayer> players,
        List<YoomaServerInfo> servers,
        int depth)
    {
        if (depth > 20) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) WalkJson(item, sourcePage, inherited, players, servers, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var current = TryGetServerContext(element, sourcePage) ?? inherited;
        if (current is not null && IsAddress(current.Address) && servers.All(x => !x.Address.Equals(current.Address, StringComparison.OrdinalIgnoreCase)))
            servers.Add(new YoomaServerInfo(current.Key, current.Name, current.Address, current.Map, current.Players, current.MaxPlayers, sourcePage));

        var steamId = ExtractSteamIdFromObject(element);
        if (IsSteamId64(steamId ?? string.Empty) && LooksLikePlayerObject(element))
        {
            current ??= BuildServerContext(sourcePage, string.Empty, 0, null);
            var nickname = ExtractString(element, "nickname", "player_name", "username", "display_name", "personaname", "name") ?? steamId!;
            AddPlayer(players, new YoomaPlayer(CleanText(nickname), steamId!, BuildProfileUrl(steamId!), current.Key, current.Name, current.Address, sourcePage));
        }

        foreach (var property in element.EnumerateObject())
            WalkJson(property.Value, sourcePage, current, players, servers, depth + 1);
    }

    private static bool LooksLikePlayerObject(JsonElement element)
    {
        var names = element.EnumerateObject().Select(x => x.Name.ToLowerInvariant()).ToArray();
        if (names.Any(x => x.Contains("owner") || x.Contains("admin") || x.Contains("creator"))) return false;
        var playerSignals = names.Count(x => x.Contains("player") || x.Contains("nickname") || x.Contains("persona") || x.Contains("steam") || x is "username");
        return playerSignals > 0;
    }

    private static ServerContext? TryGetServerContext(JsonElement element, string sourcePage)
    {
        var address = ExtractAddressFromObject(element);
        if (!IsAddress(address ?? string.Empty)) return null;
        var name = ExtractString(element, "server_name", "hostname", "title", "name") ?? $"yooma.su • {address}";
        var map = ExtractString(element, "map", "map_name", "mapname") ?? "—";
        var players = ExtractInt(element, "players", "numplayers", "online", "player_count");
        var maxPlayers = ExtractInt(element, "maxplayers", "max_players", "slots");
        return new ServerContext(address!, name, address!, map, players, maxPlayers);
    }

    private static string? ExtractAddressFromObject(JsonElement element)
    {
        foreach (var key in new[] { "address", "connect", "server_address", "addr", "endpoint" })
        {
            var value = ExtractString(element, key);
            var match = value is null ? Match.Empty : AddressRegex.Match(value);
            if (match.Success && IsAddress(match.Value)) return match.Value;
        }

        var ip = ExtractString(element, "ip", "host", "server_ip");
        var port = ExtractInt(element, "port", "game_port");
        if (!string.IsNullOrWhiteSpace(ip) && port > 0)
        {
            var candidate = $"{ip}:{port}";
            if (IsAddress(candidate)) return candidate;
        }
        return null;
    }

    private static string? ExtractSteamIdFromObject(JsonElement element)
    {
        foreach (var p in element.EnumerateObject())
        {
            var key = p.Name.ToLowerInvariant();
            if (key.Contains("owner") || key.Contains("admin") || key.Contains("creator")) continue;
            var value = JsonScalar(p.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var path = ProfilePathRegex.Match(value);
            if (path.Success) return path.Groups["id"].Value;
            var match = SteamIdRegex.Match(value);
            if (match.Success && (key.Contains("steam") || key.Contains("profile") || key.Contains("player") || key is "id" or "url" or "href"))
                return match.Value;

            // Plain 32-bit ids are only converted for an explicitly named Steam/account field.
            if ((key.Contains("steam") || key.Contains("account")) && SteamIdentityParser.TryNormalizeExplicit(value, out var normalized))
                return normalized;
        }
        return null;
    }

    private static string? ExtractString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var value = JsonScalar(p.Value);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static int ExtractInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (!p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) return n;
                if (int.TryParse(JsonScalar(p.Value), out n)) return n;
            }
        }
        return 0;
    }

    private static string? JsonScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static ServerContext BuildServerContext(string sourcePage, string html, int index, string? address)
    {
        var resolvedAddress = IsAddress(address ?? string.Empty) ? address! : $"yooma.su:{ModeKeyFromPath(sourcePage)}";
        var key = resolvedAddress;
        var name = ModeNameFromPath(sourcePage);
        var map = "—";
        var maxPlayers = 0;

        if (!string.IsNullOrEmpty(html))
        {
            var start = Math.Max(0, index - 2200);
            var length = Math.Min(html.Length - start, 4400);
            var chunk = html.Substring(start, Math.Max(0, length));

            var jsonName = Regex.Match(chunk, """[\"'](?:server_?name|hostname|title)[\"']\s*:\s*[\"'](?<v>[^\"']{2,120})[\"']""", RegexOptions.IgnoreCase);
            if (jsonName.Success)
            {
                var candidate = CleanText(jsonName.Groups["v"].Value);
                if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Contains("profile", StringComparison.OrdinalIgnoreCase)) name = candidate;
            }

            var mapMatch = Regex.Match(chunk, """[\"'](?:map|map_name|mapname)[\"']\s*:\s*[\"'](?<v>[^\"']{2,80})[\"']""", RegexOptions.IgnoreCase);
            if (mapMatch.Success) map = CleanText(mapMatch.Groups["v"].Value);

            var maxMatch = Regex.Match(chunk, """[\"'](?:maxplayers|max_players|slots)[\"']\s*:\s*(?<v>\d{1,3})""", RegexOptions.IgnoreCase);
            if (maxMatch.Success) int.TryParse(maxMatch.Groups["v"].Value, out maxPlayers);
        }

        return new ServerContext(key, name, resolvedAddress, map, 0, maxPlayers);
    }

    private static bool IsLikelyRosterContext(string html, int index)
    {
        var start = Math.Max(0, index - 2400);
        var length = Math.Min(html.Length - start, 4800);
        if (length <= 0) return false;
        var chunk = html.Substring(start, length).ToLowerInvariant();
        var signals = 0;
        foreach (var token in new[] { "server", "player", "roster", "online", "connect", "map", "slot", "steamid", "steam_id", "server-card", "player-card" })
            if (chunk.Contains(token, StringComparison.Ordinal)) signals++;
        return signals >= 2;
    }

    private static string? FindNearestAddress((int Index, string Address)[] addresses, int profileIndex)
    {
        if (addresses.Length == 0) return null;
        var best = addresses.OrderBy(x => Math.Abs(x.Index - profileIndex)).First();
        return Math.Abs(best.Index - profileIndex) <= 24000 ? best.Address : null;
    }

    private static string? FindNicknameNear(string html, int index, string steamId)
    {
        var start = Math.Max(0, index - 900);
        var length = Math.Min(html.Length - start, 1900);
        if (length <= 0) return null;
        var chunk = html.Substring(start, length);

        foreach (var pattern in new[]
        {
            """[\"'](?:nickname|player_name|username|display_name|personaname)[\"']\s*:\s*[\"'](?<v>[^\"']{1,80})[\"']""",
            """(?:data[-_:]?(?:nickname|player[-_:]?name|username|name))\s*=\s*[\"'](?<v>[^\"']{1,80})[\"']""",
            """>\s*(?<v>[^<>]{1,80})\s*</a>"""
        })
        {
            var matches = Regex.Matches(chunk, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0) continue;
            var nearest = matches.Cast<Match>().OrderBy(m => Math.Abs((start + m.Index) - index)).First();
            var value = CleanText(nearest.Groups["v"].Value);
            if (!string.IsNullOrWhiteSpace(value) && value != steamId && value.Length <= 80) return value;
        }
        return null;
    }

    private static void AddPlayer(List<YoomaPlayer> players, YoomaPlayer player)
    {
        if (players.Any(x => x.SteamId64 == player.SteamId64 && NormalizeServerKey(x.ServerKey) == NormalizeServerKey(player.ServerKey)))
            return;
        players.Add(player);
    }

    private static string DecodeMarkup(string html) => WebUtility.HtmlDecode(html)
        .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u003A", ":", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u003D", "=", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u0022", "\"", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u0027", "'", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u003C", "<", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u003E", ">", StringComparison.OrdinalIgnoreCase)
        .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
        .Replace("\\/", "/", StringComparison.Ordinal)
        .Replace("\\\"", "\"", StringComparison.Ordinal);

    private static string CleanText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var stripped = TagRegex.Replace(value, " ");
        stripped = DecodeMarkup(stripped);
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private static string BuildProfileUrl(string steamId) => $"https://yooma.su/card/{steamId}";

    private static string ModeKeyFromPath(string path)
    {
        var tail = path.Trim('/').Split('/').LastOrDefault() ?? "live";
        return tail.ToLowerInvariant();
    }

    private static string ModeNameFromPath(string path)
    {
        var tail = ModeKeyFromPath(path);
        return tail switch
        {
            "0" => "yooma.su • RETAKE",
            "1" => "yooma.su • AWP",
            "2" => "yooma.su • PUBLIC",
            "3" => "yooma.su • MINIGAMES",
            "4" => "yooma.su • MANIAC",
            "5" => "yooma.su • ARENA",
            "8" => "yooma.su • JAIL",
            "10" => "yooma.su • MAFIA",
            "public" => "yooma.su • PUBLIC",
            "awp" => "yooma.su • AWP",
            "5x5" => "yooma.su • 5X5",
            "minigames" => "yooma.su • MINIGAMES",
            "maniac" => "yooma.su • MANIAC",
            "arena" => "yooma.su • ARENA",
            "jail" => "yooma.su • JAIL",
            "retake" => "yooma.su • RETAKE",
            "dm" => "yooma.su • DM",
            "duels" => "yooma.su • DUELS",
            _ => "yooma.su • LIVE"
        };
    }

    private static string NormalizeServerKey(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static bool IsSteamId64(string value) => value.Length == 17 && value.StartsWith("7656", StringComparison.Ordinal) && value.All(char.IsDigit);

    private static bool IsAddress(string value)
    {
        var match = AddressRegex.Match(value);
        if (!match.Success || match.Value.Length != value.Length) return false;
        var octets = match.Groups["ip"].Value.Split('.');
        if (octets.Length != 4 || octets.Any(x => !byte.TryParse(x, out _))) return false;
        return int.TryParse(match.Groups["port"].Value, out var port) && port is > 0 and <= 65535;
    }

    private sealed record PageDocument(string Path, string Html);
    private sealed record ServerContext(string Key, string Name, string Address, string Map, int Players, int MaxPlayers);
    internal sealed record PageParseResult(IReadOnlyList<YoomaPlayer> Players, IReadOnlyList<YoomaServerInfo> Servers, int ProfileLinksFound);
}
