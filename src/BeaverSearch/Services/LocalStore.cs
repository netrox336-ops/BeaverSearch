using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class LocalStore
{
    private const int CurrentPriceEngineVersion = 2;
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _cachePath;
    private readonly string _serversPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheSaveSync = new();
    private CacheState? _pendingCache;
    private int _cacheSaveRevision;
    private int _cacheSaveWorkerActive;
    private Task _cacheSaveTask = Task.CompletedTask;

    public LocalStore()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeaverSearch");
        Directory.CreateDirectory(_root);
        _settingsPath = Path.Combine(_root, "settings.json");
        _cachePath = Path.Combine(_root, "cache.json");
        _serversPath = Path.Combine(_root, "servers.json");
    }

    public string DataFolder => _root;

    public async Task<AppSettings> LoadSettingsAsync() =>
        await ReadAsync(_settingsPath, new AppSettings()).ConfigureAwait(false);

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(_settingsPath, settings);

    public async Task<CacheState> LoadCacheAsync()
    {
        var hasVersionMarker = false;
        try
        {
            if (File.Exists(_cachePath))
            {
                var raw = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
                hasVersionMarker = raw.Contains("\"PriceEngineVersion\"", StringComparison.Ordinal);
            }
        }
        catch { }

        var cache = await ReadAsync(_cachePath, new CacheState()).ConfigureAwait(false);
        if (!hasVersionMarker || cache.PriceEngineVersion != CurrentPriceEngineVersion)
        {
            // Preserve exact SteamID/name discoveries, but force valuation to run again.
            // Old v0.4.1 builds could store a successful 24h check with a false 0 ₽ total.
            cache.SteamChecks.Clear();
            cache.PriceEngineVersion = CurrentPriceEngineVersion;
            await WriteAsync(_cachePath, cache).ConfigureAwait(false);
        }
        return cache;
    }

    public Task SaveCacheAsync(CacheState cache)
    {
        lock (_cacheSaveSync)
        {
            _pendingCache = cache;
            _cacheSaveRevision++;
            if (_cacheSaveWorkerActive == 0)
            {
                _cacheSaveWorkerActive = 1;
                _cacheSaveTask = FlushCacheLoopAsync();
            }
            return _cacheSaveTask;
        }
    }

    public async Task<List<ServerEntry>> LoadServersAsync() =>
        await ReadAsync(_serversPath, new List<ServerEntry>()).ConfigureAwait(false);

    public Task SaveServersAsync(IEnumerable<ServerEntry> servers) =>
        WriteAsync(_serversPath, servers.Select(s => new ServerEntry
        {
            Address = s.Address,
            Enabled = s.Enabled
        }).ToList());

    private async Task FlushCacheLoopAsync()
    {
        try
        {
            while (true)
            {
                // Collapse the burst produced when dozens of newly discovered players
                // finish almost together into one physical JSON serialization/write.
                await Task.Delay(550).ConfigureAwait(false);

                CacheState? cache;
                int revision;
                lock (_cacheSaveSync)
                {
                    cache = _pendingCache;
                    revision = _cacheSaveRevision;
                }

                if (cache is not null)
                    await WriteAsync(_cachePath, cache).ConfigureAwait(false);

                lock (_cacheSaveSync)
                {
                    if (revision == _cacheSaveRevision)
                    {
                        _pendingCache = null;
                        _cacheSaveWorkerActive = 0;
                        return;
                    }
                }
            }
        }
        catch
        {
            lock (_cacheSaveSync) _cacheSaveWorkerActive = 0;
            throw;
        }
    }

    private async Task<T> ReadAsync<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json).ConfigureAwait(false) ?? fallback;
        }
        catch
        {
            TryBackupCorruptFile(path);
            return fallback;
        }
    }

    private static void TryBackupCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup, true);
        }
        catch
        {
            // Recovery must never block startup.
        }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, value, _json).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
