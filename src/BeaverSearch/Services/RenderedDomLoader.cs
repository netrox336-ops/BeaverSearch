using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BeaverSearch.Services;

/// <summary>
/// Lightweight SPA/browser probe for yooma.su and CYBERSHOKE.
///
/// The browser is only a fallback for data that plain HTTP cannot expose. To keep
/// the WPF application responsive we run at most one Chromium probe process-wide,
/// cache successful pages for 75 seconds and return only server/player fragments
/// plus captured JSON instead of feeding the parsers a multi-megabyte full DOM.
/// </summary>
public sealed class RenderedDomLoader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(75);
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _browserPath;
    private readonly string _browserName;

    public RenderedDomLoader()
    {
        (_browserPath, _browserName) = FindBrowser();
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_browserPath);
    public string EngineName => IsAvailable ? _browserName + " DevTools/Lite" : "не найден";

    public async Task<RenderedDomResult> LoadAsync(string url, string? fallbackHtml, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && TryGetCached(url, out var cached)) return cached;
        if (!IsAvailable)
            return new RenderedDomResult(fallbackHtml ?? string.Empty, false, "HttpClient", "Microsoft Edge/Chrome не найден для JavaScript DOM fallback.");

        // Never queue many Chromium instances. A busy probe is simply deferred to
        // the next source cycle; HTTP data can still be returned immediately.
        if (!await BrowserGate.WaitAsync(TimeSpan.FromMilliseconds(80), ct).ConfigureAwait(false))
            return new RenderedDomResult(fallbackHtml ?? string.Empty, false, EngineName, "Browser probe занят; страница отложена до следующего цикла.");

        try
        {
            if (!forceRefresh && TryGetCached(url, out cached)) return cached;

            var profileDir = Path.Combine(Path.GetTempPath(), "BeaverSearch", "browser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profileDir);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(17));

                var result = await LoadViaDevToolsAsync(url, profileDir, timeout.Token).ConfigureAwait(false);
                if (result.Rendered && result.Html.Length >= 80)
                {
                    _cache[url] = new CacheEntry(DateTime.UtcNow, result.Html, result.Engine, result.Error);
                    return result;
                }

                // Keep the old --dump-dom marker/fallback for browsers where CDP does
                // not start. It is intentionally short and is not run after a good CDP
                // capture.
                ct.ThrowIfCancellationRequested();
                var dumpDir = Path.Combine(profileDir, "dump");
                Directory.CreateDirectory(dumpDir);
                var dump = await LoadViaDumpDomAsync(url, dumpDir, timeout.Token).ConfigureAwait(false);
                if (dump.Rendered && dump.Html.Length >= 80)
                {
                    _cache[url] = new CacheEntry(DateTime.UtcNow, dump.Html, dump.Engine, dump.Error);
                    return dump;
                }

                var error = string.Join(" | ", new[] { result.Error, dump.Error }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, EngineName, string.IsNullOrWhiteSpace(error) ? "Browser probe пуст." : error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, EngineName, "Таймаут browser probe (17с).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, EngineName, ex.Message);
            }
            finally
            {
                try { Directory.Delete(profileDir, true); } catch { }
            }
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    private bool TryGetCached(string url, out RenderedDomResult result)
    {
        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.LoadedUtc < CacheTtl)
        {
            result = new RenderedDomResult(hit.Html, true, hit.Engine, hit.Note);
            return true;
        }
        result = default!;
        return false;
    }

    private async Task<RenderedDomResult> LoadViaDevToolsAsync(string url, string profileDir, CancellationToken ct)
    {
        var port = ReserveTcpPort();
        var psi = NewBrowserStartInfo(profileDir);
        psi.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        psi.ArgumentList.Add("--remote-debugging-port=" + port);
        psi.ArgumentList.Add("about:blank");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new RenderedDomResult(string.Empty, false, EngineName, "Не удалось запустить DevTools browser.");
        TryLowerPriority(process);

        try
        {
            var socketUrl = await WaitForPageSocketAsync(port, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(socketUrl))
                return new RenderedDomResult(string.Empty, false, EngineName, "DevTools endpoint не появился.");

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(socketUrl), ct).ConfigureAwait(false);
            var state = new CdpState();

            await SendCommandAsync(ws, state, "Page.enable", null, ct).ConfigureAwait(false);
            await SendCommandAsync(ws, state, "Runtime.enable", null, ct).ConfigureAwait(false);
            await SendCommandAsync(ws, state, "Network.enable", new { maxTotalBufferSize = 10_000_000, maxResourceBufferSize = 1_200_000 }, ct).ConfigureAwait(false);
            await SendCommandAsync(ws, state, "Page.navigate", new { url }, ct).ConfigureAwait(false);
            await Task.Delay(350, ct).ConfigureAwait(false);

            var interaction = await EvaluateStringWithRetryAsync(ws, state, InteractionScript, ct).ConfigureAwait(false) ?? "{}";
            var fragments = await EvaluateStringWithRetryAsync(ws, state, FragmentScript, ct).ConfigureAwait(false) ?? string.Empty;
            var resourceCapture = await EvaluateStringWithRetryAsync(ws, state, CaptureScript, ct).ConfigureAwait(false) ?? "{}";
            var networkCapture = await CaptureNetworkPayloadsAsync(ws, state, ct).ConfigureAwait(false);

            var html = new StringBuilder(Math.Min(2_000_000, fragments.Length + networkCapture.Markup.Length + 4096));
            html.Append("<html><body data-beaver-lite=\"1\">");
            html.Append(fragments);
            html.Append(BuildCaptureMarkup(resourceCapture, out var resources, out var replayJson));
            html.Append(networkCapture.Markup);
            html.Append("</body></html>");

            var summary = BuildProbeSummary(interaction, resources, replayJson, networkCapture.JsonBodies, networkCapture.WebSocketFrames, html.Length);
            WriteProbeLog(url, summary, resourceCapture, networkCapture.Urls);
            return new RenderedDomResult(html.ToString(), true, EngineName, summary);
        }
        finally
        {
            TryKill(process);
        }
    }

    private async Task<NetworkCapture> CaptureNetworkPayloadsAsync(ClientWebSocket ws, CdpState state, CancellationToken ct)
    {
        var markup = new StringBuilder();
        var urls = new List<string>();
        var jsonBodies = 0;

        var candidates = state.Responses
            .GroupBy(x => x.RequestId, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Where(IsInterestingNetworkResponse)
            .Take(18)
            .ToArray();

        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var response = await SendCommandAsync(ws, state, "Network.getResponseBody", new { requestId = item.RequestId }, ct).ConfigureAwait(false);
            var body = ExtractResponseBody(response);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 1_200_000) continue;
            if (!LooksLikeJson(body) && !LooksLikeIdentityPayload(body)) continue;

            urls.Add(item.Url);
            if (LooksLikeJson(body)) jsonBodies++;
            AppendPayload(markup, "data-beaver-network", item.Url, body);
        }

        var wsFrames = 0;
        foreach (var frame in state.WebSocketFrames.Take(50))
        {
            if (string.IsNullOrWhiteSpace(frame) || frame.Length > 450_000) continue;
            var normalized = NormalizeSocketPayload(frame);
            if (!LooksLikeJson(normalized) && !LooksLikeIdentityPayload(normalized)) continue;
            wsFrames++;
            AppendPayload(markup, "data-beaver-websocket", "1", normalized);
        }

        return new NetworkCapture(markup.ToString(), jsonBodies, wsFrames, urls);
    }

    private static void AppendPayload(StringBuilder target, string attribute, string source, string body)
    {
        target.Append("<script type=\"application/json\" ")
            .Append(attribute).Append("=\"")
            .Append(WebUtility.HtmlEncode(source)).Append("\">")
            .Append(WebUtility.HtmlEncode(body)).Append("</script>");
    }

    private static bool IsInterestingNetworkResponse(NetworkResponseRef item)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var suffix in new[] { ".js", ".css", ".png", ".jpg", ".jpeg", ".webp", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".mp4", ".webm" })
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return false;

        return item.Type.Equals("XHR", StringComparison.OrdinalIgnoreCase) ||
               item.Type.Equals("Fetch", StringComparison.OrdinalIgnoreCase) ||
               item.Type.Equals("EventSource", StringComparison.OrdinalIgnoreCase) ||
               item.MimeType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               new[] { "api", "player", "server", "online", "public-read", "roster", "live" }
                   .Any(x => (uri.AbsolutePath + uri.Query).Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<RenderedDomResult> LoadViaDumpDomAsync(string url, string profileDir, CancellationToken ct)
    {
        var psi = NewBrowserStartInfo(profileDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.ArgumentList.Add("--run-all-compositor-stages-before-draw");
        psi.ArgumentList.Add("--virtual-time-budget=3200");
        psi.ArgumentList.Add("--dump-dom");
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) return new RenderedDomResult(string.Empty, false, EngineName, "Не удалось запустить --dump-dom fallback.");
        TryLowerPriority(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch { TryKill(process); throw; }

        var raw = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new RenderedDomResult(string.Empty, false, _browserName + " dump-dom", string.IsNullOrWhiteSpace(stderr) ? "dump-dom пуст." : FirstLine(stderr));

        // Limit fallback size too. Identity/server markers are usually in the first
        // portion and a hard cap avoids a giant string hitting the UI parser.
        if (raw.Length > 1_500_000) raw = raw[..1_500_000];
        return new RenderedDomResult(raw, true, _browserName + " dump-dom", "DevTools fallback.");
    }

    private ProcessStartInfo NewBrowserStartInfo(string profileDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _browserPath!,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("--headless=new");
        psi.ArgumentList.Add("--disable-gpu");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-extensions");
        psi.ArgumentList.Add("--disable-popup-blocking");
        psi.ArgumentList.Add("--window-size=1366,768");
        psi.ArgumentList.Add("--lang=ru-RU");
        psi.ArgumentList.Add("--user-data-dir=" + profileDir);
        return psi;
    }

    private static async Task<string?> WaitForPageSocketAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(550) };
        var endpoint = $"http://127.0.0.1:{port}/json/list";
        for (var i = 0; i < 36; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync(endpoint, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetString() != "page") continue;
                    if (item.TryGetProperty("webSocketDebuggerUrl", out var socket) && !string.IsNullOrWhiteSpace(socket.GetString()))
                        return socket.GetString();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<JsonElement?> SendCommandAsync(ClientWebSocket ws, CdpState state, string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref state.NextId);
        var payload = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[48 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.Count > 0) ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (ms.Length == 0) continue;
            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                return root.Clone();
            CaptureEvent(root, state);
        }
        return null;
    }

    private static void CaptureEvent(JsonElement root, CdpState state)
    {
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String) return;
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return;
        var method = methodElement.GetString();

        if (method == "Network.responseReceived" && state.Responses.Count < 120)
        {
            if (!parameters.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String) return;
            if (!parameters.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return;
            var url = response.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return;
            var mime = response.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var type = parameters.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            state.Responses.Add(new NetworkResponseRef(requestId.GetString()!, url!, mime, type));
        }
        else if (method == "Network.webSocketFrameReceived" && state.WebSocketFrames.Count < 80)
        {
            if (parameters.TryGetProperty("response", out var response) && response.TryGetProperty("payloadData", out var data) && data.ValueKind == JsonValueKind.String)
            {
                var value = data.GetString();
                if (!string.IsNullOrWhiteSpace(value)) state.WebSocketFrames.Add(value!);
            }
        }
    }

    private static async Task<string?> EvaluateStringWithRetryAsync(ClientWebSocket ws, CdpState state, string expression, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await SendCommandAsync(ws, state, "Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture = true
            }, ct).ConfigureAwait(false);
            var value = ExtractEvaluateString(response);
            if (value is not null) return value;
            if (attempt == 0) await Task.Delay(450, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static string? ExtractEvaluateString(JsonElement? response)
    {
        if (response is null) return null;
        var root = response.Value;
        if (!root.TryGetProperty("result", out var outer) || !outer.TryGetProperty("result", out var inner)) return null;
        if (!inner.TryGetProperty("value", out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? ExtractResponseBody(JsonElement? response)
    {
        if (response is null) return null;
        var root = response.Value;
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String) return null;
        var body = bodyElement.GetString();
        if (string.IsNullOrEmpty(body)) return body;
        if (result.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(body)); } catch { return null; }
        }
        return body;
    }

    private static string NormalizeSocketPayload(string payload)
    {
        var value = payload.Trim();
        for (var i = 0; i < Math.Min(8, value.Length); i++)
            if (value[i] is '{' or '[') return value[i..];
        return value;
    }

    private static bool LooksLikeIdentityPayload(string value)
    {
        if (value.Contains("/card/", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("/profile/", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("steamid", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("steam_id", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("account_id", StringComparison.OrdinalIgnoreCase)) return true;

        for (var i = 0; i + 17 <= value.Length; i++)
            if (value.AsSpan(i, 4).SequenceEqual("7656".AsSpan()) && value.AsSpan(i, 17).ToString().All(char.IsDigit)) return true;
        return false;
    }

    private static bool LooksLikeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var span = value.AsSpan().TrimStart();
        return span.Length > 1 && (span[0] == '{' || span[0] == '[');
    }

    private static string BuildCaptureMarkup(string captureJson, out int resourceCount, out int payloadCount)
    {
        resourceCount = 0;
        payloadCount = 0;
        if (string.IsNullOrWhiteSpace(captureJson)) return string.Empty;
        var output = new StringBuilder();
        try
        {
            using var doc = JsonDocument.Parse(captureJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
                resourceCount = resources.GetArrayLength();
            if (!root.TryGetProperty("payloads", out var payloads) || payloads.ValueKind != JsonValueKind.Array) return output.ToString();
            foreach (var payload in payloads.EnumerateArray())
            {
                if (!payload.TryGetProperty("body", out var bodyNode) || bodyNode.ValueKind != JsonValueKind.String) continue;
                var body = bodyNode.GetString();
                if (!LooksLikeJson(body)) continue;
                payloadCount++;
                var source = payload.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                AppendPayload(output, "data-beaver-source", source, body!);
            }
        }
        catch (JsonException) { }
        return output.ToString();
    }

    private static string BuildProbeSummary(string interactionJson, int resources, int replayJson, int networkJson, int wsFrames, int liteChars)
    {
        var cards = 0;
        var clicks = 0;
        var identities = 0;
        var loading = 0;
        try
        {
            using var doc = JsonDocument.Parse(interactionJson);
            var root = doc.RootElement;
            cards = ReadInt(root, "cards");
            clicks = ReadInt(root, "clicks");
            identities = ReadInt(root, "identities");
            loading = ReadInt(root, "loading");
        }
        catch (JsonException) { }
        return $"probe cards={cards}, loading={loading}, clicks={clicks}, identities={identities}, resources={resources}, replayJson={replayJson}, networkJson={networkJson}, wsFrames={wsFrames}, liteChars={liteChars}";
    }

    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var n) ? n : 0;

    private static void WriteProbeLog(string pageUrl, string summary, string captureJson, IReadOnlyList<string> networkUrls)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeaverSearch");
            Directory.CreateDirectory(dir);
            var urls = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(captureJson);
                if (doc.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
                    urls.AddRange(resources.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());
            }
            catch (JsonException) { }
            urls.AddRange(networkUrls);
            var lines = new List<string> { $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {pageUrl}", "  " + summary };
            lines.AddRange(urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).Select(x => "  resource: " + x));
            File.AppendAllLines(Path.Combine(dir, "source-probe.log"), lines);
        }
        catch { }
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static (string? Path, string Name) FindBrowser()
    {
        var candidates = new[]
        {
            (Expand(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge"),
            (Expand(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge"),
            (Expand(@"%LOCALAPPDATA%\Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge"),
            (Expand(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"), "Google Chrome"),
            (Expand(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"), "Google Chrome"),
            (Expand(@"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"), "Google Chrome")
        };
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate.Item1) && File.Exists(candidate.Item1)) return candidate;
        return (null, string.Empty);
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);
    private static void TryLowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
    }
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
    private static string FirstLine(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Browser error";

    private const string InteractionScript = """
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const deadline = Date.now() + 6500;
  while (Date.now() < deadline) {
    const loading = document.querySelectorAll('.server.loading').length;
    const cards = document.querySelectorAll('.server:not(.loading), [class*=\"server-card\"]').length;
    if (document.readyState === 'complete' && (cards > 0 || loading === 0)) break;
    await sleep(220);
  }
  const cards = [...document.querySelectorAll('.server:not(.loading), [class*=\"server-card\"]')].slice(0, 16);
  let clicks = 0;
  for (const card of cards) {
    const nodes = [...card.querySelectorAll('button,[role=\"button\"],[class*=\"player\"],[class*=\"online\"]')];
    const target = nodes.find(el => {
      const cls = typeof el.className === 'string' ? el.className : '';
      const text = `${cls} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('title') || ''} ${el.textContent || ''}`.toLowerCase();
      return !/connect|подключ|steam:\/\//.test(text) && /player|players|игрок|онлайн|online/.test(text);
    });
    if (!target) continue;
    try { target.click(); clicks++; await sleep(100); } catch (_) {}
  }
  await sleep(700);
  const markup = document.documentElement ? document.documentElement.outerHTML : '';
  const identities = new Set();
  for (const m of markup.matchAll(/\/(?:[a-z]{2}\/)?(?:profile|card)\/(7656\d{13})/gi)) identities.add(m[1]);
  for (const m of markup.matchAll(/(?:steam(?:id|_id|id64)|data-steamid)[^0-9]{0,12}(7656\d{13})/gi)) identities.add(m[1]);
  return JSON.stringify({ cards: cards.length, loading: document.querySelectorAll('.server.loading').length, clicks, identities: identities.size });
})()
""";

    private const string FragmentScript = """
(() => {
  const selectors = [
    '.server:not(.loading)', '[class*=\"server-card\"]',
    'a[href*=\"/card/\"]', 'a[href*=\"/profile/\"]',
    'a[href*=\"steamcommunity.com/profiles/\"]',
    '[data-steamid]', '[data-steam-id]', '[data-steam_id]',
    '[class*=\"player\"]', '[class*=\"online\"]'
  ];
  const seen = new Set();
  const out = [];
  for (const el of document.querySelectorAll(selectors.join(','))) {
    if (out.length >= 260) break;
    let html = el.outerHTML || '';
    if (!html || html.length > 120000) html = html.slice(0, 120000);
    const key = html.slice(0, 500);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(html);
  }
  return out.join('\n');
})()
""";

    private const string CaptureScript = """
(async () => {
  const interesting = /(api|player|players|server|servers|online|public-read|roster|live)/i;
  const parts = location.hostname.toLowerCase().replace(/^www\./, '').split('.');
  const rootHost = parts.slice(-2).join('.');
  const entries = performance.getEntriesByType('resource');
  const resources = [...new Set(entries.filter(e => {
    try {
      const x = new URL(e.name, location.href);
      const h = x.hostname.toLowerCase();
      if (!(h === rootHost || h.endsWith('.' + rootHost))) return false;
      if (/\/assets\/|\/fonts\/|\.(?:png|jpe?g|webp|svg|ico|woff2?|ttf|css|js|mp4|webm)(?:\?|$)/i.test(x.pathname)) return false;
      const initiator = (e.initiatorType || '').toLowerCase();
      return initiator === 'fetch' || initiator === 'xmlhttprequest' || interesting.test(x.pathname + x.search);
    } catch (_) { return false; }
  }).map(e => e.name))].slice(0, 24);
  const payloads = [];
  for (const u of resources.slice(0, 12)) {
    try {
      const r = await fetch(u, { credentials: 'include', cache: 'no-store' });
      if (!r.ok) continue;
      const text = await r.text();
      const t = text.trimStart();
      if (text.length <= 900000 && (t.startsWith('{') || t.startsWith('['))) payloads.push({ url: u, body: text });
    } catch (_) {}
  }
  return JSON.stringify({ resources, payloads });
})()
""";

    private sealed class CdpState
    {
        public int NextId;
        public List<NetworkResponseRef> Responses { get; } = [];
        public List<string> WebSocketFrames { get; } = [];
    }
    private sealed record NetworkResponseRef(string RequestId, string Url, string MimeType, string Type);
    private sealed record NetworkCapture(string Markup, int JsonBodies, int WebSocketFrames, IReadOnlyList<string> Urls);
    private sealed record CacheEntry(DateTime LoadedUtc, string Html, string Engine, string? Note);
}

public sealed record RenderedDomResult(string Html, bool Rendered, string Engine, string? Error);
