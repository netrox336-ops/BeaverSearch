using System.Collections.Concurrent;
using System.Diagnostics;

namespace BeaverSearch.Services;

/// <summary>
/// Loads the DOM after client-side JavaScript has rendered a monitoring page.
/// yooma.su and CYBERSHOKE are SPAs: a normal HttpClient request can contain the
/// shell but not the live player cards.  BeaverSearch therefore uses the browser
/// already present on Windows (Edge first, then Chrome) in headless --dump-dom mode.
/// No browser extension, injected game code or login session is used.
/// </summary>
public sealed class RenderedDomLoader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(28);
    private static readonly SemaphoreSlim BrowserGate = new(2, 2);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _browserPath;
    private readonly string _browserName;

    public RenderedDomLoader()
    {
        (_browserPath, _browserName) = FindBrowser();
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_browserPath);
    public string EngineName => IsAvailable ? _browserName : "не найден";

    public async Task<RenderedDomResult> LoadAsync(string url, string? fallbackHtml, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.LoadedUtc < CacheTtl)
            return new RenderedDomResult(cached.Html, true, _browserName, null);

        if (!IsAvailable)
            return new RenderedDomResult(fallbackHtml ?? string.Empty, false, "HttpClient", "Microsoft Edge/Chrome не найден для JavaScript DOM fallback.");

        await BrowserGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache.TryGetValue(url, out cached) && DateTime.UtcNow - cached.LoadedUtc < CacheTtl)
                return new RenderedDomResult(cached.Html, true, _browserName, null);

            var profileDir = Path.Combine(Path.GetTempPath(), "BeaverSearch", "browser", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profileDir);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(18));

                var psi = new ProcessStartInfo
                {
                    FileName = _browserPath!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                // Use a disposable profile so BeaverSearch never touches the user's
                // cookies or logged-in browser profile. virtual-time-budget gives SPA
                // fetch/XHR code time to populate the player cards before --dump-dom.
                psi.ArgumentList.Add("--headless=new");
                psi.ArgumentList.Add("--disable-gpu");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--disable-extensions");
                psi.ArgumentList.Add("--disable-popup-blocking");
                psi.ArgumentList.Add("--run-all-compositor-stages-before-draw");
                psi.ArgumentList.Add("--virtual-time-budget=5500");
                psi.ArgumentList.Add("--window-size=1920,1080");
                psi.ArgumentList.Add("--lang=ru-RU");
                psi.ArgumentList.Add("--user-data-dir=" + profileDir);
                psi.ArgumentList.Add("--dump-dom");
                psi.ArgumentList.Add(url);

                using var process = new Process { StartInfo = psi };
                if (!process.Start())
                    return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName, "Не удалось запустить headless browser.");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TryKill(process);
                    var fallback = fallbackHtml ?? string.Empty;
                    return new RenderedDomResult(fallback, false, _browserName, "Таймаут JavaScript DOM (18с).");
                }

                ct.ThrowIfCancellationRequested();
                var html = await stdoutTask;
                var stderr = await stderrTask;
                if (string.IsNullOrWhiteSpace(html) || html.Length < 300)
                {
                    var error = string.IsNullOrWhiteSpace(stderr)
                        ? $"Headless browser завершился с кодом {process.ExitCode}, DOM пуст."
                        : FirstLine(stderr);
                    return new RenderedDomResult(fallbackHtml ?? string.Empty, false, _browserName, error);
                }

                _cache[url] = new CacheEntry(DateTime.UtcNow, html);
                return new RenderedDomResult(html, true, _browserName, null);
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

    private sealed record CacheEntry(DateTime LoadedUtc, string Html);
}

public sealed record RenderedDomResult(string Html, bool Rendered, string Engine, string? Error);
