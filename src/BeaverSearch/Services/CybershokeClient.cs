using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

/// <summary>
/// Public CYBERSHOKE CS2 monitoring reader.
///
/// Player identity is taken only from SteamID64 that CYBERSHOKE itself places in
/// the rendered element/state (data-steamid, steamId, /profile/7656..., Steam
/// profile URL, etc). We never map a nickname to a Steam account heuristically.
/// The site is client-rendered, so a rotating batch of mode pages is rendered with
/// Edge/Chrome and recent batches are merged to cover the whole server network.
/// </summary>
public sealed class CybershokeClient
{
    private const string BaseUrl = "https://cybershoke.net";
    private static readonly Regex SteamIdRegex = new(@"(?<!\d)(7656\d{13})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ProfilePathRegex = new("""/(?:[a-z]{2}/)?profile/(?<id>7656\d{13})(?:[/?#\"'\\]|$)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamProfileRegex = new("""steamcommunity\.com/profiles/(?<id>7656\d{13})""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamAttributeRegex = new(
        """(?:data[-_:]?(?:steam(?:id|[-_]?id|id64)?|player[-_:]?(?:steam)?id|account[-_:]?id)|(?:steam(?:id|_id|Id|Id64)|steam_id64|steam64))\s*=\s*[\"']?(?<id>7656\d{13})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamAccountAttributeRegex = new(
        """(?:data[-_:]?(?:steam(?:id|[-_]?id|id64|[-_:]?account)?|account[-_:]?(?:id|steamid))|(?:steam(?:id|_id|Id|Id64|AccountId)|steam_id64|steam64|account_id))\s*=\s*[\"']?(?<id>(?:7656\d{13}|STEAM_[0-5]:[01]:\d{1,10}|\[?U:1:\d{1,10}\]?|\d{1,10}))""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ServerLabelRegex = new(@"#(?<num>\d{1,5})\s+(?<mode>[A-Za-z0-9][A-Za-z0-9 _+\-]{0,28})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlotsMapRegex = new(@"(?<p>\d{1,3})\s*/\s*(?<m>\d{1,3})\s*\|\s*(?<map>[A-Za-z0-9_\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ScriptJsonRegex = new(
        """<script\b[^>]*(?:type\s*=\s*[\"']application/(?:json|ld\+json)[\"']|id\s*=\s*[\"']__NEXT_DATA__[\"'])[^>]*>(?<json>.*?)</script>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly string[] ModePaths =
    [
        "/ru/cs2/servers/dm", "/ru/cs2/servers/duels", "/ru/cs2/servers/retake",
        "/ru/cs2/servers/retake-cards", "/ru/cs2/servers/5x5", "/ru/cs2/servers/2x2",
        "/ru/cs2/servers/bhop", "/ru/cs2/servers/surf", "/ru/cs2/servers/kz",
        "/ru/cs2/servers/public", "/ru/cs2/servers/awp", "/ru/cs2/servers/duels-2x2",
        "/ru/cs2/servers/execute", "/ru/cs2/servers/clutch", "/ru/cs2/servers/pistol-retake",
        "/ru/cs2/servers/arena", "/ru/cs2/servers/multicfg", "/ru/cs2/servers/pistoldm",
        "/ru/cs2/servers/hsdm", "/ru/cs2/servers/awpdm", "/ru/cs2/servers/aimdm",
        "/ru/cs2/servers/jail", "/ru/cs2/servers/maniac", "/ru/cs2/servers/deathrun",
        "/ru/cs2/servers/minigames", "/ru/cs2/servers/hns", "/ru/cs2/servers/surf-combat",
        "/ru/cs2/servers/knife", "/ru/cs2/servers/skin-inspect"
    ];

    private const int RenderBatchSize = 8;
    private static readonly TimeSpan PathSnapshotTtl = TimeSpan.FromSeconds(95);

    private readonly HttpClient _http;
    private readonly RenderedDomLoader _dom = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pageGate = new(6, 6);
    private readonly ConcurrentDictionary<string, PathSnapshot> _pathSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private int _renderCursor;
    private (DateTime LoadedUtc, YoomaLiveSnapshot Snapshot)? _cache;

    public CybershokeClient()
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
        _http.DefaultRequestHeaders.Referrer = new Uri("https://cybershoke.net/ru/cs2");
    }

    public async Task<YoomaLiveSnapshot> GetLiveAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache is { } hit && DateTime.UtcNow - hit.LoadedUtc < TimeSpan.FromSeconds(18))
            return hit.Snapshot;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache is { } second && DateTime.UtcNow - second.LoadedUtc < TimeSpan.FromSeconds(18))
                return second.Snapshot;

            var batch = NextRenderBatch();
            var staticPages = await Task.WhenAll(batch.Select(path => LoadPageSafeAsync(path, ct)));
            ct.ThrowIfCancellationRequested();
            var loaded = staticPages.Where(x => x is not null).Cast<PageDocument>().ToArray();

            // Parse static/hydration markup first. It is cheap and sometimes already
            // includes the player state even if the visible text does not.
            foreach (var page in loaded)
            {
                var parsed = ParsePage(page.Path, page.Html);
                if (parsed.Players.Count > 0 || parsed.Servers.Count > 0)
                    _pathSnapshots[page.Path] = new PathSnapshot(DateTime.UtcNow, parsed, false);
            }

            var renderedPages = 0;
            string? renderWarning = null;
            if (_dom.IsAvailable)
            {
                var rawByPath = loaded.ToDictionary(x => x.Path, x => x.Html, StringComparer.OrdinalIgnoreCase);
                var rendered = await Task.WhenAll(batch.Select(async path =>
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
                    _pathSnapshots[item.Path] = new PathSnapshot(DateTime.UtcNow, parsed, true);
                }
            }
            else
            {
                renderWarning = "Edge/Chrome не найден: CYBERSHOKE live DOM нельзя отрендерить.";
            }

            var cutoff = DateTime.UtcNow - PathSnapshotTtl;
            foreach (var stale in _pathSnapshots.Where(x => x.Value.LoadedUtc < cutoff).Select(x => x.Key).ToArray())
                _pathSnapshots.TryRemove(stale, out _);

            var recent = _pathSnapshots.Values.Where(x => x.LoadedUtc >= cutoff).ToArray();
            var players = recent.SelectMany(x => x.Parse.Players)
                .Where(x => IsSteamId64(x.SteamId64))
                .GroupBy(x => $"{x.SteamId64}|{NormalizeKey(x.ServerKey)}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => !x.Nickname.Equals(x.SteamId64, StringComparison.Ordinal)).First())
                .ToList();

            var servers = recent.SelectMany(x => x.Parse.Servers)
                .Concat(players.GroupBy(x => NormalizeKey(x.ServerKey), StringComparer.OrdinalIgnoreCase).Select(g =>
                {
                    var first = g.First();
                    return new YoomaServerInfo(first.ServerKey, first.ServerName, first.ServerAddress, "—", g.Select(x => x.SteamId64).Distinct().Count(), 0, first.SourcePage);
                }))
                .GroupBy(x => NormalizeKey(x.Key), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var best = g.OrderByDescending(x => x.Players).ThenByDescending(x => x.MaxPlayers).First();
                    var count = Math.Max(best.Players, players.Count(p => NormalizeKey(p.ServerKey) == NormalizeKey(best.Key)));
                    return best with { Players = count };
                })
                .Where(x => x.Players > 0 || x.MaxPlayers > 0)
                .OrderByDescending(x => x.Players)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var profileHits = recent.Sum(x => x.Parse.ProfileLinksFound);
            var snapshot = new YoomaLiveSnapshot(
                players,
                servers,
                loaded.Length,
                batch.Length,
                Math.Max(profileHits, players.Count),
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

    private string[] NextRenderBatch()
    {
        var batch = new string[Math.Min(RenderBatchSize, ModePaths.Length)];
        var start = Math.Abs(Interlocked.Add(ref _renderCursor, RenderBatchSize) - RenderBatchSize) % ModePaths.Length;
        for (var i = 0; i < batch.Length; i++)
            batch[i] = ModePaths[(start + i) % ModePaths.Length];
        return batch;
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
            var delay = last.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(400 * (attempt + 1));
            if (delay > TimeSpan.FromSeconds(3)) delay = TimeSpan.FromSeconds(3);
            await Task.Delay(delay, ct);
        }
        return last!;
    }

    internal static PageParseResult ParsePage(string sourcePage, string html)
    {
        var decoded = DecodeMarkup(html);
        var players = new List<YoomaPlayer>();
        var servers = ParseServerCards(sourcePage, decoded);
        var hits = 0;

        // Direct profile routes (when the site chooses the numeric route).
        foreach (Match match in ProfilePathRegex.Matches(decoded))
        {
            var id = match.Groups["id"].Value;
            if (!IsSteamId64(id) || !IsLikelyPlayerContext(decoded, match.Index)) continue;
            hits++;
            AddPlayerFromDom(players, sourcePage, decoded, match.Index, id, BaseUrl + match.Value.TrimEnd('"', '\'', '\\'));
        }

        // Direct steamcommunity links are even stronger identity evidence.
        foreach (Match match in SteamProfileRegex.Matches(decoded))
        {
            var id = match.Groups["id"].Value;
            if (!IsSteamId64(id) || !IsLikelyPlayerContext(decoded, match.Index)) continue;
            hits++;
            AddPlayerFromDom(players, sourcePage, decoded, match.Index, id, $"https://steamcommunity.com/profiles/{id}");
        }

        // Main path for current CYBERSHOKE markup: account id is often in the player
        // component's data/state even when href itself is a vanity slug.
        foreach (Match match in SteamAttributeRegex.Matches(decoded))
        {
            var id = match.Groups["id"].Value;
            if (!IsSteamId64(id) || !IsLikelyPlayerContext(decoded, match.Index)) continue;
            hits++;
            AddPlayerFromDom(players, sourcePage, decoded, match.Index, id, $"https://cybershoke.net/ru/profile/{id}");
        }

        // CYBERSHOKE components can expose a 32-bit Steam AccountID instead of
        // SteamID64. Accept it only from an explicit steam/account attribute and convert
        // deterministically to the individual SteamID64 namespace.
        foreach (Match match in SteamAccountAttributeRegex.Matches(decoded))
        {
            if (!SteamIdentityParser.TryNormalizeExplicit(match.Groups["id"].Value, out var id) || !IsLikelyPlayerContext(decoded, match.Index)) continue;
            hits++;
            AddPlayerFromDom(players, sourcePage, decoded, match.Index, id, $"https://cybershoke.net/ru/profile/{id}");
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

        // Some bundlers serialize state inside ordinary script attributes/text. Only
        // accept a bare 7656... when nearby keys make it clearly player/Steam data.
        foreach (Match match in SteamIdRegex.Matches(decoded))
        {
            var id = match.Value;
            if (players.Any(x => x.SteamId64 == id)) continue;
            if (!IsExplicitSteamContext(decoded, match.Index)) continue;
            hits++;
            AddPlayerFromDom(players, sourcePage, decoded, match.Index, id, $"https://cybershoke.net/ru/profile/{id}");
        }

        return new PageParseResult(players, servers, hits);
    }

    private static List<YoomaServerInfo> ParseServerCards(string sourcePage, string html)
    {
        var result = new List<YoomaServerInfo>();
        foreach (Match label in ServerLabelRegex.Matches(html))
        {
            var mode = CleanText(label.Groups["mode"].Value).Trim();
            if (mode.Length == 0) continue;
            var number = label.Groups["num"].Value;
            var chunkStart = label.Index;
            var chunkLength = Math.Min(900, html.Length - chunkStart);
            var chunk = chunkLength > 0 ? html.Substring(chunkStart, chunkLength) : string.Empty;
            var slots = SlotsMapRegex.Match(CleanText(chunk));
            var players = slots.Success && int.TryParse(slots.Groups["p"].Value, out var p) ? p : 0;
            var max = slots.Success && int.TryParse(slots.Groups["m"].Value, out var m) ? m : 0;
            var map = slots.Success ? slots.Groups["map"].Value : "—";
            var key = $"cybershoke.net:{ModeKey(sourcePage)}:{number}";
            if (result.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new YoomaServerInfo(key, $"CYBERSHOKE • #{number} {mode}", key, map, players, max, sourcePage));
        }
        return result;
    }

    private static void AddPlayerFromDom(List<YoomaPlayer> players, string sourcePage, string html, int index, string steamId, string profileUrl)
    {
        var context = FindServerContext(sourcePage, html, index);
        var nickname = FindNicknameNear(html, index, steamId) ?? steamId;
        var player = new YoomaPlayer(nickname, steamId, profileUrl, context.Key, context.Name, context.Key, sourcePage);
        if (players.Any(x => x.SteamId64 == steamId && NormalizeKey(x.ServerKey) == NormalizeKey(context.Key))) return;
        players.Add(player);
    }

    private static void WalkJson(
        JsonElement element,
        string sourcePage,
        ServerContext? inherited,
        List<YoomaPlayer> players,
        List<YoomaServerInfo> servers,
        int depth)
    {
        if (depth > 22) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) WalkJson(item, sourcePage, inherited, players, servers, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var current = TryGetServerContext(element, sourcePage) ?? inherited;
        if (current is not null && servers.All(x => !x.Key.Equals(current.Key, StringComparison.OrdinalIgnoreCase)))
            servers.Add(new YoomaServerInfo(current.Key, current.Name, current.Key, current.Map, current.Players, current.MaxPlayers, sourcePage));

        var steamId = ExtractSteamIdFromObject(element);
        if (IsSteamId64(steamId ?? string.Empty) && LooksLikePlayerObject(element))
        {
            current ??= DefaultContext(sourcePage);
            var nick = ExtractString(element, "nickname", "nick", "player_name", "username", "display_name", "personaname", "name", "slug") ?? steamId!;
            var url = ExtractString(element, "profile_url", "profileUrl", "url", "href") ?? $"https://cybershoke.net/ru/profile/{steamId}";
            var player = new YoomaPlayer(CleanText(nick), steamId!, url, current.Key, current.Name, current.Key, sourcePage);
            if (!players.Any(x => x.SteamId64 == steamId && NormalizeKey(x.ServerKey) == NormalizeKey(current.Key)))
                players.Add(player);
        }

        foreach (var property in element.EnumerateObject())
            WalkJson(property.Value, sourcePage, current, players, servers, depth + 1);
    }

    private static ServerContext? TryGetServerContext(JsonElement element, string sourcePage)
    {
        var number = ExtractString(element, "server_number", "serverNumber", "number", "server_id", "serverId", "id");
        var mode = ExtractString(element, "mode", "mode_name", "modeName", "gamemode", "type") ?? ModeName(sourcePage);
        var map = ExtractString(element, "map", "map_name", "mapName") ?? "—";
        var players = ExtractInt(element, "players", "online", "player_count", "playerCount");
        var max = ExtractInt(element, "max_players", "maxPlayers", "slots");

        if (string.IsNullOrWhiteSpace(number)) return null;
        if (!number.All(char.IsDigit) || number.Length > 8) return null;
        // Avoid treating a Steam account id or unrelated small object id as a server
        // unless the same object contains server-specific signals.
        var names = element.EnumerateObject().Select(x => x.Name.ToLowerInvariant()).ToArray();
        if (!names.Any(x => x.Contains("server") || x.Contains("map") || x.Contains("slot") || x.Contains("mode"))) return null;
        var key = $"cybershoke.net:{ModeKey(sourcePage)}:{number}";
        return new ServerContext(key, $"CYBERSHOKE • #{number} {CleanText(mode)}", map, players, max);
    }

    private static string? ExtractSteamIdFromObject(JsonElement element)
    {
        foreach (var p in element.EnumerateObject())
        {
            var key = p.Name.ToLowerInvariant();
            if (key.Contains("server") && !key.Contains("player")) continue;
            if (key.Contains("owner") || key.Contains("admin") || key.Contains("creator")) continue;
            var value = JsonScalar(p.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var profile = ProfilePathRegex.Match(value);
            if (profile.Success) return profile.Groups["id"].Value;
            var steamProfile = SteamProfileRegex.Match(value);
            if (steamProfile.Success) return steamProfile.Groups["id"].Value;
            var match = SteamIdRegex.Match(value);
            if (match.Success && (key.Contains("steam") || key.Contains("account") || key.Contains("profile") || key.Contains("player")))
                return match.Value;

            if ((key.Contains("steam") || key.Contains("account")) && SteamIdentityParser.TryNormalizeExplicit(value, out var normalized))
                return normalized;
        }
        return null;
    }

    private static bool LooksLikePlayerObject(JsonElement element)
    {
        var names = element.EnumerateObject().Select(x => x.Name.ToLowerInvariant()).ToArray();
        if (names.Any(x => x.Contains("owner") || x.Contains("admin") || x.Contains("creator"))) return false;
        var signals = names.Count(x => x.Contains("player") || x.Contains("nickname") || x.Contains("steam") || x.Contains("account") || x.Contains("persona") || x is "username" or "nick");
        return signals > 0;
    }

    private static ServerContext FindServerContext(string sourcePage, string html, int index)
    {
        var start = Math.Max(0, index - 8500);
        var length = Math.Min(html.Length - start, 17000);
        var chunk = length > 0 ? html.Substring(start, length) : string.Empty;
        var labels = ServerLabelRegex.Matches(chunk).Cast<Match>().ToArray();
        if (labels.Length == 0) return DefaultContext(sourcePage);
        var nearest = labels.OrderBy(x => Math.Abs((start + x.Index) - index)).First();
        var number = nearest.Groups["num"].Value;
        var mode = CleanText(nearest.Groups["mode"].Value).Trim();
        var key = $"cybershoke.net:{ModeKey(sourcePage)}:{number}";

        var after = chunk.Substring(nearest.Index, Math.Min(1000, chunk.Length - nearest.Index));
        var slots = SlotsMapRegex.Match(CleanText(after));
        var players = slots.Success && int.TryParse(slots.Groups["p"].Value, out var p) ? p : 0;
        var max = slots.Success && int.TryParse(slots.Groups["m"].Value, out var m) ? m : 0;
        var map = slots.Success ? slots.Groups["map"].Value : "—";
        return new ServerContext(key, $"CYBERSHOKE • #{number} {mode}", map, players, max);
    }

    private static ServerContext DefaultContext(string sourcePage)
    {
        var key = $"cybershoke.net:{ModeKey(sourcePage)}";
        return new ServerContext(key, $"CYBERSHOKE • {ModeName(sourcePage)}", "—", 0, 0);
    }

    private static bool IsLikelyPlayerContext(string html, int index)
    {
        var start = Math.Max(0, index - 2600);
        var length = Math.Min(html.Length - start, 5200);
        if (length <= 0) return false;
        var chunk = html.Substring(start, length).ToLowerInvariant();
        var signals = 0;
        foreach (var token in new[] { "player", "steam", "server", "online", "nickname", "avatar", "profile", "slot", "map", "join" })
            if (chunk.Contains(token, StringComparison.Ordinal)) signals++;
        return signals >= 3;
    }

    private static bool IsExplicitSteamContext(string html, int index)
    {
        var start = Math.Max(0, index - 220);
        var length = Math.Min(html.Length - start, 440);
        if (length <= 0) return false;
        var chunk = html.Substring(start, length).ToLowerInvariant();
        return chunk.Contains("steam", StringComparison.Ordinal) ||
               chunk.Contains("account", StringComparison.Ordinal) ||
               chunk.Contains("profile", StringComparison.Ordinal) ||
               chunk.Contains("player", StringComparison.Ordinal);
    }

    private static string? FindNicknameNear(string html, int index, string steamId)
    {
        var start = Math.Max(0, index - 1200);
        var length = Math.Min(html.Length - start, 2500);
        if (length <= 0) return null;
        var chunk = html.Substring(start, length);
        foreach (var pattern in new[]
        {
            """(?:data[-_:]?(?:nickname|player[-_:]?name|username|name|slug))\s*=\s*[\"'](?<v>[^\"']{1,80})[\"']""",
            """[\"'](?:nickname|nick|player_name|username|display_name|personaname)[\"']\s*:\s*[\"'](?<v>[^\"']{1,80})[\"']""",
            """<a\b[^>]*href\s*=\s*[\"'][^\"']+[\"'][^>]*>\s*(?<v>[^<>]{1,80})\s*</a>"""
        })
        {
            var matches = Regex.Matches(chunk, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (matches.Count == 0) continue;
            var nearest = matches.Cast<Match>().OrderBy(m => Math.Abs((start + m.Index) - index)).First();
            var value = CleanText(nearest.Groups["v"].Value);
            if (value.Length is > 0 and <= 80 && value != steamId && !value.Contains("Присоедин", StringComparison.OrdinalIgnoreCase) && !value.Contains("Copy", StringComparison.OrdinalIgnoreCase))
                return value;
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

    private static string ModeKey(string path)
    {
        var tail = path.Trim('/').Split('/').LastOrDefault() ?? "live";
        return tail.ToLowerInvariant();
    }

    private static string ModeName(string path) => ModeKey(path).Replace('-', ' ').ToUpperInvariant();
    private static string NormalizeKey(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static bool IsSteamId64(string value) => value.Length == 17 && value.StartsWith("7656", StringComparison.Ordinal) && value.All(char.IsDigit);

    private sealed record PageDocument(string Path, string Html);
    private sealed record ServerContext(string Key, string Name, string Map, int Players, int MaxPlayers);
    private sealed record PathSnapshot(DateTime LoadedUtc, PageParseResult Parse, bool Rendered);
    internal sealed record PageParseResult(IReadOnlyList<YoomaPlayer> Players, IReadOnlyList<YoomaServerInfo> Servers, int ProfileLinksFound);
}
