using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BeaverSearch.Services;

/// <summary>
/// Loads monitoring pages after client-side JavaScript has finished.
///
/// v0.4.1 FixSteamID uses Chromium DevTools first: SPA hydration, player-card
/// interaction, XHR/fetch response capture and WebSocket frame capture are merged
/// back into markup that the existing source parsers understand. The old
/// --dump-dom path remains a fallback.
///
/// A disposable browser profile is always used. BeaverSearch never reads the
/// user's Edge/Chrome profile, cookies or logged-in sessions.
/// </summary>
public sealed class RenderedDomLoader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(24);
    private static readonly SemaphoreSlim BrowserGate = new(2, 2);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _browserPath;
    private readonly string _browserName;

    public RenderedDomLoader()
    {
        (_browserPath, _browserName) = FindBrowser();
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_browserPath);
    public string EngineName => IsAvailable ? _browserName + " DevTools" : "не найден";

    public async Task<RenderedDomResult> LoadAsync(string url, string? fallbackHtml, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.LoadedUtc < CacheTtl)
            return new RenderedDomResult(cached.Html, true, cached.Engine, cached.Note);

        if (!IsAvailable)
            return new RenderedDomResult(fallbackHtml ?? string.Empty, false, "HttpClient", "Microsoft Edge/Chrome не найден для JavaScript DOM fallback.");

        // Never queue every monitoring mode behind two Chromium instances. Two pages
        // are probed now; the rest are deferred to the next monitoring cycle. Pages
        // already completed are served from the cache above, so probing rolls forward.
        if (!await BrowserGate.WaitAsync(TimeSpan.FromMilliseconds(180), ct))
            return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName + " DevTools", "Browser probe занят; страница отложена до следующего цикла.");

        try
        {
            if (!forceRefresh && _cache.TryGetValue(url, out cached) && DateTime.UtcNow - cached.LoadedUtc < CacheTtl)
                return new RenderedDomResult(cached.Html, true, cached.Engine, cached.Note);

            var profileDir = Path.Combine(Path.GetTempPath(), "BeaverSearch", "browser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profileDir);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(22));

                var cdp = await LoadViaDevToolsAsync(url, profileDir, timeout.Token);
                if (cdp.Rendered && cdp.Html.Length >= 300)
                {
                    _cache[url] = new CacheEntry(DateTime.UtcNow, cdp.Html, cdp.Engine, cdp.Error);
                    return cdp;
                }

                ct.ThrowIfCancellationRequested();
                var dumpProfileDir = Path.Combine(profileDir, "dump-profile");
                Directory.CreateDirectory(dumpProfileDir);
                var dump = await LoadViaDumpDomAsync(url, dumpProfileDir, timeout.Token);
                if (dump.Rendered && dump.Html.Length >= 300)
                {
                    var note = string.IsNullOrWhiteSpace(cdp.Error)
                        ? dump.Error
                        : "DevTools fallback: " + cdp.Error;
                    var result = dump with { Error = note };
                    _cache[url] = new CacheEntry(DateTime.UtcNow, result.Html, result.Engine, result.Error);
                    return result;
                }

                var error = string.Join(" | ", new[] { cdp.Error, dump.Error }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName, string.IsNullOrWhiteSpace(error) ? "Browser DOM пуст." : error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName, "Таймаут JavaScript/browser probe (22с).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName, ex.Message);
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

    private async Task<RenderedDomResult> LoadViaDevToolsAsync(string url, string profileDir, CancellationToken ct)
    {
        var port = ReserveTcpPort();
        var psi = NewBrowserStartInfo(profileDir);
        psi.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        psi.ArgumentList.Add("--remote-debugging-port=" + port);
        psi.ArgumentList.Add("about:blank");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new RenderedDomResult(string.Empty, false, _browserName, "Не удалось запустить DevTools browser.");

        try
        {
            var socketUrl = await WaitForPageSocketAsync(port, ct);
            if (string.IsNullOrWhiteSpace(socketUrl))
                return new RenderedDomResult(string.Empty, false, _browserName, "DevTools endpoint не появился.");

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(socketUrl), ct);
            var state = new CdpState();

            await SendCommandAsync(ws, state, "Page.enable", null, ct);
            await SendCommandAsync(ws, state, "Runtime.enable", null, ct);
            await SendCommandAsync(ws, state, "Network.enable", new { maxTotalBufferSize = 20_000_000, maxResourceBufferSize = 2_000_000 }, ct);
            await SendCommandAsync(ws, state, "Page.navigate", new { url }, ct);

            // Let the new execution context replace about:blank before evaluating JS.
            await Task.Delay(450, ct);

            var interactionJson = await EvaluateStringWithRetryAsync(ws, state, InteractionScript, ct) ?? "{}";
            var captureJson = await EvaluateStringWithRetryAsync(ws, state, CaptureScript, ct) ?? "{}";

            // Network.responseReceived events were collected while the async scripts
            // above were running. Reading their bodies through CDP works even when a
            // public endpoint lives on a project subdomain and normal page re-fetch is
            // restricted by CORS.
            var networkCapture = await CaptureNetworkPayloadsAsync(ws, state, ct);
            var html = await EvaluateStringWithRetryAsync(ws, state, "document.documentElement ? document.documentElement.outerHTML : ''", ct) ?? string.Empty;

            var enrichment = BuildCaptureMarkup(captureJson, out var resources, out var replayPayloads);
            html += enrichment;
            html += networkCapture.Markup;

            var summary = BuildProbeSummary(
                interactionJson,
                resources,
                replayPayloads,
                networkCapture.JsonBodies,
                networkCapture.WebSocketFrames);
            WriteProbeLog(url, summary, captureJson, networkCapture.Urls);

            if (string.IsNullOrWhiteSpace(html) || html.Length < 300)
                return new RenderedDomResult(string.Empty, false, _browserName + " DevTools", "DevTools вернул пустой DOM. " + summary);

            return new RenderedDomResult(html, true, _browserName + " DevTools", summary);
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
            .Take(28)
            .ToArray();

        foreach (var responseRef in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var response = await SendCommandAsync(ws, state, "Network.getResponseBody", new { requestId = responseRef.RequestId }, ct);
            var body = ExtractResponseBody(response);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 1_800_000) continue;
            if (!LooksLikeJson(body) && !LooksLikeIdentityPayload(body)) continue;

            urls.Add(responseRef.Url);
            if (LooksLikeJson(body))
            {
                jsonBodies++;
                markup.Append("\n<script type=\"application/json\" data-beaver-network=\"")
                    .Append(WebUtility.HtmlEncode(responseRef.Url))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(body))
                    .Append("</script>");
            }
            else
            {
                markup.Append("\n<script data-beaver-network=\"")
                    .Append(WebUtility.HtmlEncode(responseRef.Url))
                    .Append("\">")
                    .Append(WebUtility.HtmlEncode(body))
                    .Append("</script>");
            }
        }

        var wsFrames = 0;
        foreach (var rawFrame in state.WebSocketFrames.Take(80))
        {
            if (string.IsNullOrWhiteSpace(rawFrame) || rawFrame.Length > 700_000) continue;
            var normalized = NormalizeSocketPayload(rawFrame);
            if (string.IsNullOrWhiteSpace(normalized) || (!LooksLikeJson(normalized) && !LooksLikeIdentityPayload(normalized))) continue;
            wsFrames++;
            if (LooksLikeJson(normalized))
            {
                markup.Append("\n<script type=\"application/json\" data-beaver-websocket=\"1\">")
                    .Append(WebUtility.HtmlEncode(normalized))
                    .Append("</script>");
            }
            else
            {
                markup.Append("\n<script data-beaver-websocket=\"1\">")
                    .Append(WebUtility.HtmlEncode(normalized))
                    .Append("</script>");
            }
        }

        return new NetworkCapture(markup.ToString(), jsonBodies, wsFrames, urls);
    }

    private static bool IsInterestingNetworkResponse(NetworkResponseRef item)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var suffix in new[] { ".js", ".css", ".png", ".jpg", ".jpeg", ".webp", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".mp4", ".webm" })
            if (path.EndsWith(suffix, StringComparison.Ordinal)) return false;

        if (item.Type.Equals("XHR", StringComparison.OrdinalIgnoreCase) ||
            item.Type.Equals("Fetch", StringComparison.OrdinalIgnoreCase) ||
            item.Type.Equals("EventSource", StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.MimeType.Contains("json", StringComparison.OrdinalIgnoreCase)) return true;
        var text = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        return new[] { "api", "player", "server", "online", "monitor", "public-read", "roster", "live" }.Any(text.Contains);
    }

    private static string? ExtractResponseBody(JsonElement? response)
    {
        if (response is null) return null;
        var root = response.Value;
        if (!root.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String) return null;
        var body = bodyElement.GetString();
        if (string.IsNullOrEmpty(body)) return body;

        if (result.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(body)); }
            catch { return null; }
        }
        return body;
    }

    private static string NormalizeSocketPayload(string payload)
    {
        var value = payload.Trim();
        // Socket.IO frames often prefix JSON with 42 (event), 0 (open) or 4.
        for (var i = 0; i < Math.Min(6, value.Length); i++)
        {
            if (value[i] is '{' or '[') return value[i..];
        }
        return value;
    }

    private static bool LooksLikeIdentityPayload(string value)
    {
        if (value.Contains("/card/", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("/profile/", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("steamid", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("steam_id", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("account_id", StringComparison.OrdinalIgnoreCase))
            return true;

        for (var i = 0; i + 17 <= value.Length; i++)
        {
            if (!value.AsSpan(i, 4).SequenceEqual("7656".AsSpan())) continue;
            if (value.AsSpan(i, 17).ToString().All(char.IsDigit)) return true;
        }
        return false;
    }

    private async Task<RenderedDomResult> LoadViaDumpDomAsync(string url, string profileDir, CancellationToken ct)
    {
        var psi = NewBrowserStartInfo(profileDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.ArgumentList.Add("--run-all-compositor-stages-before-draw");
        psi.ArgumentList.Add("--virtual-time-budget=4500");
        psi.ArgumentList.Add("--dump-dom");
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            return new RenderedDomResult(string.Empty, false, _browserName, "Не удалось запустить --dump-dom fallback.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var html = await stdoutTask;
        var stderr = await stderrTask;
        if (string.IsNullOrWhiteSpace(html) || html.Length < 300)
        {
            var error = string.IsNullOrWhiteSpace(stderr)
                ? $"Headless fallback завершился с кодом {process.ExitCode}, DOM пуст."
                : FirstLine(stderr);
            return new RenderedDomResult(string.Empty, false, _browserName + " dump-dom", error);
        }

        return new RenderedDomResult(html, true, _browserName + " dump-dom", "DevTools не сработал; использован --dump-dom fallback.");
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
        psi.ArgumentList.Add("--window-size=1920,1080");
        psi.ArgumentList.Add("--lang=ru-RU");
        psi.ArgumentList.Add("--user-data-dir=" + profileDir);
        return psi;
    }

    private static async Task<string?> WaitForPageSocketAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        var endpoint = $"http://127.0.0.1:{port}/json/list";
        for (var i = 0; i < 45; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync(endpoint, ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "page", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (item.TryGetProperty("webSocketDebuggerUrl", out var socket) && !string.IsNullOrWhiteSpace(socket.GetString()))
                        return socket.GetString();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch { }
            await Task.Delay(120, ct);
        }
        return null;
    }

    private static async Task<JsonElement?> SendCommandAsync(ClientWebSocket ws, CdpState state, string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref state.NextId);
        var payload = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.Count > 0) ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

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
        var method = methodElement.GetString();
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return;

        if (method == "Network.responseReceived" && state.Responses.Count < 180)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdElement) || requestIdElement.ValueKind != JsonValueKind.String) return;
            if (!parameters.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return;
            var requestId = requestIdElement.GetString();
            var url = response.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(url)) return;
            var mime = response.TryGetProperty("mimeType", out var mimeElement) && mimeElement.ValueKind == JsonValueKind.String ? mimeElement.GetString() ?? string.Empty : string.Empty;
            var type = parameters.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() ?? string.Empty : string.Empty;
            state.Responses.Add(new NetworkResponseRef(requestId!, url!, mime, type));
            return;
        }

        if (method == "Network.webSocketFrameReceived" && state.WebSocketFrames.Count < 120)
        {
            if (!parameters.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return;
            if (!response.TryGetProperty("payloadData", out var payload) || payload.ValueKind != JsonValueKind.String) return;
            var value = payload.GetString();
            if (!string.IsNullOrWhiteSpace(value)) state.WebSocketFrames.Add(value!);
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
            }, ct);

            var value = ExtractEvaluateString(response);
            if (value is not null) return value;
            if (attempt == 0) await Task.Delay(700, ct);
        }
        return null;
    }

    private static string? ExtractEvaluateString(JsonElement? response)
    {
        if (response is null) return null;
        var root = response.Value;
        if (!root.TryGetProperty("result", out var outer)) return null;
        if (!outer.TryGetProperty("result", out var inner)) return null;
        if (inner.TryGetProperty("value", out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return null;
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

            if (root.TryGetProperty("payloads", out var payloads) && payloads.ValueKind == JsonValueKind.Array)
            {
                foreach (var payload in payloads.EnumerateArray())
                {
                    if (!payload.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String) continue;
                    var body = bodyElement.GetString();
                    if (!LooksLikeJson(body)) continue;
                    payloadCount++;
                    var source = payload.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : string.Empty;
                    output.Append("\n<script type=\"application/json\" data-beaver-source=\"")
                        .Append(WebUtility.HtmlEncode(source ?? string.Empty))
                        .Append("\">")
                        .Append(WebUtility.HtmlEncode(body!))
                        .Append("</script>");
                }
            }

            foreach (var storageName in new[] { "localState", "sessionState" })
            {
                if (!root.TryGetProperty(storageName, out var storage) || storage.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in storage.EnumerateArray())
                {
                    if (!entry.TryGetProperty("value", out var valueElement) || valueElement.ValueKind != JsonValueKind.String) continue;
                    var value = valueElement.GetString();
                    if (!LooksLikeJson(value)) continue;
                    output.Append("\n<script type=\"application/json\" data-beaver-storage=\"")
                        .Append(storageName)
                        .Append("\">")
                        .Append(WebUtility.HtmlEncode(value!))
                        .Append("</script>");
                }
            }
        }
        catch (JsonException) { }
        return output.ToString();
    }

    private static string BuildProbeSummary(string interactionJson, int resources, int replayPayloads, int networkBodies, int wsFrames)
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
        return $"probe cards={cards}, loading={loading}, clicks={clicks}, identities={identities}, resources={resources}, replayJson={replayPayloads}, networkJson={networkBodies}, wsFrames={wsFrames}";
    }

    private static int ReadInt(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.TryGetInt32(out var n) ? n : 0;
    }

    private static bool LooksLikeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var span = value.AsSpan().TrimStart();
        return span.Length > 1 && (span[0] == '{' || span[0] == '[');
    }

    private static void WriteProbeLog(string pageUrl, string summary, string captureJson, IReadOnlyList<string> networkUrls)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeaverSearch");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "source-probe.log");
            var urls = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(captureJson);
                if (doc.RootElement.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
                {
                    urls.AddRange(resources.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Cast<string>()
                        .Take(30));
                }
            }
            catch (JsonException) { }

            urls.AddRange(networkUrls);
            var lines = new List<string>
            {
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {pageUrl}",
                "  " + summary
            };
            lines.AddRange(urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).Select(x => "  resource: " + x));
            File.AppendAllLines(path, lines);
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
            (Path: Expand(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"), Name: "Microsoft Edge"),
            (Path: Expand(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"), Name: "Microsoft Edge"),
            (Path: Expand(@"%LOCALAPPDATA%\Microsoft\Edge\Application\msedge.exe"), Name: "Microsoft Edge"),
            (Path: Expand(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"), Name: "Google Chrome"),
            (Path: Expand(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"), Name: "Google Chrome"),
            (Path: Expand(@"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"), Name: "Google Chrome")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && File.Exists(candidate.Path))
                return candidate;
        }
        return (null, "");
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "Headless browser не вернул DOM." : line.Trim();
    }

    private const string InteractionScript = """
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const deadline = Date.now() + 9000;
  while (Date.now() < deadline) {
    const loading = document.querySelectorAll('.server.loading').length;
    const cards = document.querySelectorAll('.server:not(.loading), [class*=\"server-card\"]').length;
    if (document.readyState === 'complete' && (cards > 0 || loading === 0)) break;
    await sleep(250);
  }

  const cards = [...document.querySelectorAll('.server:not(.loading), [class*=\"server-card\"]')].slice(0, 24);
  let clicks = 0;
  for (const card of cards) {
    const nodes = [...card.querySelectorAll('button,[role=\"button\"],[class*=\"player\"],[class*=\"online\"]')];
    const target = nodes.find(el => {
      const cls = typeof el.className === 'string' ? el.className : '';
      const text = `${cls} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('title') || ''} ${el.textContent || ''}`.toLowerCase();
      if (/connect|подключ|steam:\/\//.test(text)) return false;
      return /player|players|игрок|онлайн|online/.test(text);
    });
    if (!target) continue;
    try { target.click(); clicks++; await sleep(160); } catch (_) {}
  }
  await sleep(1100);

  const markup = document.documentElement ? document.documentElement.outerHTML : '';
  const identities = new Set();
  for (const m of markup.matchAll(/\/(?:[a-z]{2}\/)?(?:profile|card)\/(7656\d{13})/gi)) identities.add(m[1]);
  for (const m of markup.matchAll(/(?:steam(?:id|_id|id64)|data-steamid)[^0-9]{0,12}(7656\d{13})/gi)) identities.add(m[1]);
  return JSON.stringify({
    cards: cards.length,
    loading: document.querySelectorAll('.server.loading').length,
    clicks,
    identities: identities.size
  });
})()
""";

    private const string CaptureScript = """
(async () => {
  const interesting = /(api|player|players|server|servers|online|monitor|public-read|roster|live)/i;
  const hostParts = location.hostname.toLowerCase().replace(/^www\./, '').split('.');
  const rootHost = hostParts.slice(-2).join('.');
  const entries = performance.getEntriesByType('resource');
  const resources = [...new Set(entries
    .filter(e => {
      try {
        const x = new URL(e.name, location.href);
        const h = x.hostname.toLowerCase();
        const sameProject = h === rootHost || h.endsWith('.' + rootHost);
        if (!sameProject) return false;
        if (/\/assets\/|\/fonts\/|\.(?:png|jpe?g|webp|svg|ico|woff2?|ttf|css|js|mp4|webm)(?:\?|$)/i.test(x.pathname)) return false;
        const initiator = (e.initiatorType || '').toLowerCase();
        return initiator === 'fetch' || initiator === 'xmlhttprequest' || initiator === 'beacon' || interesting.test(x.pathname + x.search);
      } catch (_) { return false; }
    })
    .map(e => e.name))].slice(0, 40);

  const payloads = [];
  for (const u of resources.slice(0, 22)) {
    try {
      const r = await fetch(u, { credentials: 'include', cache: 'no-store' });
      if (!r.ok) continue;
      const type = (r.headers.get('content-type') || '').toLowerCase();
      const text = await r.text();
      const trimmed = text.trimStart();
      if (text.length > 1800000) continue;
      if (type.includes('json') || trimmed.startsWith('{') || trimmed.startsWith('['))
        payloads.push({ url: u, body: text });
    } catch (_) {}
  }

  const collectStorage = storage => {
    const out = [];
    try {
      for (let i = 0; i < storage.length && out.length < 20; i++) {
        const key = storage.key(i);
        if (!key || !interesting.test(key)) continue;
        const value = storage.getItem(key) || '';
        if (value.length <= 400000) out.push({ key, value });
      }
    } catch (_) {}
    return out;
  };

  return JSON.stringify({
    resources,
    payloads,
    localState: collectStorage(localStorage),
    sessionState: collectStorage(sessionStorage)
  });
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
