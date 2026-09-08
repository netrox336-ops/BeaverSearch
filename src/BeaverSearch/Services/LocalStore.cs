using System.Text.Json;
using BeaverSearch.Models;

namespace BeaverSearch.Services;

public sealed class LocalStore
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _cachePath;
    private readonly string _serversPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        await ReadAsync(_settingsPath, new AppSettings());

    public Task SaveSettingsAsync(AppSettings settings) => WriteAsync(_settingsPath, settings);

    public async Task<CacheState> LoadCacheAsync() =>
        await ReadAsync(_cachePath, new CacheState());

    public Task SaveCacheAsync(CacheState cache) => WriteAsync(_cachePath, cache);

    public async Task<List<ServerEntry>> LoadServersAsync() =>
        await ReadAsync(_serversPath, new List<ServerEntry>());

    public Task SaveServersAsync(IEnumerable<ServerEntry> servers) =>
        WriteAsync(_serversPath, servers.Select(s => new ServerEntry
        {
            Address = s.Address,
            Enabled = s.Enabled
        }).ToList());

    private async Task<T> ReadAsync<T>(string path, T fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json) ?? fallback;
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
        await _gate.WaitAsync();
        try
        {
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, value, _json);
            File.Move(temp, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
