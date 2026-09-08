using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BeaverSearch.Infrastructure;
using BeaverSearch.Models;
using BeaverSearch.Services;
using Microsoft.Win32;

namespace BeaverSearch.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int CommunityServerBatchSize = 10;
    private const int MaxPlayersPerServerBatch = 64;
    private const int MaxScheduledScansPerBatch = 80;
    private static readonly TimeSpan SteamCheckTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan UnresolvedRetryDelay = TimeSpan.FromSeconds(45);

    private readonly LocalStore _store = new();
    private readonly YoomaClient _yooma = new();
    private readonly CybershokeClient _cybershoke = new();
    private readonly SteamProfileService _profiles = new();
    private readonly InventoryValuationService _valuation;
    private readonly SemaphoreSlim _scanGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _steamScanLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _playerTasks = new();
    private readonly Dictionary<string, HashSet<string>> _presentNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unresolvedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string[]> _confirmedSteamIdsByServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _unresolvedLogTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _sourceWarningTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _sourceDomLogTimes = new(StringComparer.OrdinalIgnoreCase);

    private CacheState _cache = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private AppSettings _settings = new();
    private string _newServerAddress = string.Empty;
    private bool _isMonitoring;
    private string _statusLine = "Готов к запуску";
    private int _checkedSession;
    private string _manualSteamId = string.Empty;
    private string _manualResultText = "Введите SteamID64 и нажмите «Проверить».";
    private PlayerResult? _selectedResult;
    private long _playerTaskSequence;
    private int _inventoryProcessing;
    private int _yoomaLivePlayers;
    private int _yoomaProfilePlayers;
    private int _yoomaPagesLoaded;
    private int _yoomaPagesAttempted;
    private string _yoomaLastError = string.Empty;
    private int _cybershokeLivePlayers;
    private int _cybershokeProfilePlayers;
    private int _cybershokePagesLoaded;
    private int _cybershokePagesAttempted;
    private string _cybershokeLastError = string.Empty;
    private int _serverBatchCursor;
    private int _serverBatchNumber;
    private int _scanBudgetRemaining;
    private long _lastMetricsRefreshTick;
    private int _metricsRefreshQueued;

    public MainViewModel()
    {
        var cbr = new CbrCurrencyService();
        var csFloat = new CsFloatComparableProvider(cbr);
        _valuation = new InventoryValuationService(new SteamInventoryService(), new SkinportPriceProvider(), csFloat);

        AddServerCommand = new RelayCommand(AddServer);
        RemoveServerCommand = new RelayCommand(RemoveServer);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        SaveSettingsCommand = new AsyncRelayCommand(SaveAllAsync);
        ExportCommand = new RelayCommand(ExportResults);
        ClearResultsCommand = new RelayCommand(ClearResults);
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenSelectedSteamCommand = new RelayCommand(OpenSelectedSteam);
        CopySelectedSteamCommand = new RelayCommand(CopySelectedSteam);
        ManualCheckCommand = new AsyncRelayCommand(ManualCheckAsync);
        ImportServersCommand = new RelayCommand(ImportServers);
    }

    public ObservableCollection<ServerEntry> Servers { get; } = [];
    public ObservableCollection<PlayerResult> Results { get; } = [];
    public ObservableCollection<LivePlayerRow> LivePlayers { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public AppSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public string NewServerAddress { get => _newServerAddress; set => SetProperty(ref _newServerAddress, value); }
    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (!SetProperty(ref _isMonitoring, value)) return;
            OnPropertyChanged(nameof(MonitorButtonText));
            OnPropertyChanged(nameof(MonitoringStateText));
            OnPropertyChanged(nameof(InventoryHealthText));
        }
    }

    public string MonitorButtonText => IsMonitoring ? "Остановить мониторинг" : "Запустить мониторинг";
    public string MonitoringStateText => IsMonitoring ? "Monitoring ON" : "Monitoring OFF";
    public string StatusLine { get => _statusLine; private set => SetProperty(ref _statusLine, value); }
    public string ManualSteamId { get => _manualSteamId; set => SetProperty(ref _manualSteamId, value); }
    public string ManualResultText { get => _manualResultText; private set => SetProperty(ref _manualResultText, value); }
    public PlayerResult? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }

    public int ServersOnline => Servers.Count(x => x.Online);
    public int PlayersOnline => Servers.Where(x => x.Online).Sum(x => x.Players);
    public int CheckedSession => _checkedSession;
    public int UnresolvedSession => _unresolvedKeys.Count;
    public int MatchCount => Results.Count;
    public string ServerCountText => $"{ServersOnline} / {Servers.Count}";
    public int UniquePlayers => Results.Count;
    public decimal AverageResultValue => Results.Count == 0 ? 0 : Results.Average(x => x.TotalRub);
    public decimal HighestResultValue => Results.Count == 0 ? 0 : Results.Max(x => x.TotalRub);
    public string HighestResultNickname => Results.OrderByDescending(x => x.TotalRub).FirstOrDefault()?.Nickname ?? "—";
    public int ProcessingCount => IsMonitoring ? Math.Max(0, Volatile.Read(ref _inventoryProcessing)) : 0;
    public int ScanParallelism => 4;
    public int EnabledServerCount => Servers.Count(x => x.Enabled);
    public int ServerProgressValue => Servers.Count(x => x.Enabled && x.Online);
    public int ServerProgressMaximum => Math.Max(1, EnabledServerCount);
    public int YoomaLivePlayers => IsMonitoring ? Math.Max(0, _yoomaLivePlayers) : 0;
    public int YoomaProfilePlayers => IsMonitoring ? Math.Max(0, _yoomaProfilePlayers) : 0;
    public int YoomaPagesLoaded => IsMonitoring ? Math.Max(0, _yoomaPagesLoaded) : 0;
    public int YoomaPagesAttempted => IsMonitoring ? Math.Max(0, _yoomaPagesAttempted) : 0;
    public int CybershokeLivePlayers => IsMonitoring ? Math.Max(0, _cybershokeLivePlayers) : 0;
    public int CybershokeProfilePlayers => IsMonitoring ? Math.Max(0, _cybershokeProfilePlayers) : 0;
    public int CybershokePagesLoaded => IsMonitoring ? Math.Max(0, _cybershokePagesLoaded) : 0;
    public int CybershokePagesAttempted => IsMonitoring ? Math.Max(0, _cybershokePagesAttempted) : 0;
    public int ConfirmedSteamIds => _confirmedSteamIdsByServer.Values
        .SelectMany(x => x)
        .Where(IsSteamId64)
        .Distinct(StringComparer.Ordinal)
        .Count();
    public int CacheRecordCount => _cache.NameResolves.Count + _cache.SteamChecks.Count;
    public string YoomaHealthText => !IsMonitoring ? "Ожидание" : !string.IsNullOrWhiteSpace(_yoomaLastError) ? "Ошибка" : $"{YoomaPagesLoaded}/{Math.Max(1, YoomaPagesAttempted)} страниц";
    public string YoomaProfileHealthText => !IsMonitoring ? "Ожидание" : YoomaProfilePlayers > 0 ? "SteamID получены" : "Ждём live DOM";
    public string CybershokeHealthText => !IsMonitoring ? "Ожидание" : !string.IsNullOrWhiteSpace(_cybershokeLastError) ? "Ошибка" : $"{CybershokePagesLoaded}/{Math.Max(1, CybershokePagesAttempted)} страниц";
    public string CybershokeProfileHealthText => !IsMonitoring ? "Ожидание" : CybershokeProfilePlayers > 0 ? "SteamID получены" : "Ждём live DOM";
    public int CommunityLivePlayers => YoomaLivePlayers + CybershokeLivePlayers;
    public int CommunityProfilePlayers => YoomaProfilePlayers + CybershokeProfilePlayers;
    public string PriceHealthText => "Bulk cache + Steam fallback";
    public string InventoryHealthText => ProcessingCount > 0 ? $"Обработка: {ProcessingCount}" : IsMonitoring ? "Работает" : "Ожидание";
    public string CacheHealthText => "В норме";
    public string DataFolder => _store.DataFolder;

    public RelayCommand AddServerCommand { get; }
    public RelayCommand RemoveServerCommand { get; }
    public RelayCommand ToggleMonitoringCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand ClearResultsCommand { get; }
    public AsyncRelayCommand ClearCacheCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenSelectedSteamCommand { get; }
    public RelayCommand CopySelectedSteamCommand { get; }
    public AsyncRelayCommand ManualCheckCommand { get; }
    public RelayCommand ImportServersCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _store.LoadSettingsAsync();
        Settings.PropertyChanged += Settings_PropertyChanged;
        _cache = await _store.LoadCacheAsync();
        Servers.Clear();
        Log("BeaverSearch v0.4.1 FixSteamID запущен.");
        Log("Мониторинг работает пакетами по 10 серверов: пакет полностью передаётся Inventory Scanner, затем выбираются следующие 10.");
        Log("Источники: yooma.su + CYBERSHOKE; SteamID64 берётся только из live DOM/API/WebSocket, без угадывания по нику.");
        Log("Цены: lazy bulk RUB catalog + bounded Steam Market fallback. Старые ложные 0 ₽ пересканируются автоматически.");
        RefreshMetrics(force: true);
    }

    public async Task ShutdownAsync()
    {
        StopMonitoring();
        try { if (_monitorTask is not null) await _monitorTask; } catch { }
        var remaining = _playerTasks.Values.ToArray();
        if (remaining.Length > 0)
        {
            try { await Task.WhenAll(remaining); } catch { }
        }
        await SaveAllAsync();
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = PersistSettingsSafeAsync();
        RefreshMetrics();
    }

    private async Task PersistSettingsSafeAsync()
    {
        try { await _store.SaveSettingsAsync(Settings); }
        catch (Exception ex) { Log("Не удалось автоматически сохранить settings: " + ex.Message); }
    }

    private void AddServer()
    {
        if (Servers.Count(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)) >= 5)
        {
            MessageBox.Show("Можно добавить не более 5 ручных серверов.", "BeaverSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ServerAddressParser.TryNormalize(NewServerAddress, out var address, out var error))
        {
            MessageBox.Show(error + "\n\nМожно вставить даже строку вида: connect 46.174.48.218:28037", "BeaverSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Servers.Any(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) return;
        var server = new ServerEntry { Address = address, Source = "Ручной" };
        AttachServer(server);
        Servers.Add(server);
        NewServerAddress = string.Empty;
        _ = _store.SaveServersAsync(Servers.Where(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)));
        Log($"Добавлен сервер {address}");
        RefreshMetrics();
    }

    private void ImportServers()
    {
        var dialog = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        var added = 0;
        foreach (var line in File.ReadLines(dialog.FileName))
        {
            if (Servers.Count(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)) >= 5) break;
            if (!ServerAddressParser.TryNormalize(line, out var address, out _)) continue;
            if (Servers.Any(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) continue;
            var server = new ServerEntry { Address = address, Source = "Ручной" };
            AttachServer(server);
            Servers.Add(server);
            added++;
        }
        _ = _store.SaveServersAsync(Servers.Where(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)));
        Log($"Импорт серверов: добавлено {added}.");
        RefreshMetrics();
    }

    private void RemoveServer(object? parameter)
    {
        if (parameter is not ServerEntry server) return;
        server.PropertyChanged -= Server_PropertyChanged;
        Servers.Remove(server);
        _presentNames.Remove(server.Address);
        foreach (var key in _unresolvedKeys.Keys.Where(x => x.StartsWith(server.Address + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _unresolvedKeys.TryRemove(key, out _);
            _unresolvedLogTimes.TryRemove(key, out _);
        }
        _confirmedSteamIdsByServer.TryRemove(server.Address, out _);
        foreach (var row in LivePlayers.Where(x => x.Server == server.Address).ToList()) LivePlayers.Remove(row);
        _ = _store.SaveServersAsync(Servers.Where(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)));
        Log($"Удалён сервер {server.Address}");
        RefreshMetrics();
    }

    private void AttachServer(ServerEntry server)
    {
        server.PropertyChanged -= Server_PropertyChanged;
        server.PropertyChanged += Server_PropertyChanged;
    }

    private void Server_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerEntry.Enabled) or nameof(ServerEntry.Online) or nameof(ServerEntry.Players) or nameof(ServerEntry.MaxPlayers))
            RefreshMetrics();

        if (e.PropertyName == nameof(ServerEntry.Enabled) && sender is ServerEntry server &&
            server.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase))
            _ = _store.SaveServersAsync(Servers.Where(x => x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)));
    }

    private void ToggleMonitoring()
    {
        if (IsMonitoring) StopMonitoring();
        else StartMonitoring();
    }

    private void StartMonitoring()
    {
        if (IsMonitoring) return;

        ResetLiveDiagnostics(clearServerState: false);
        ClearDynamicServerRows();
        _serverBatchCursor = 0;
        _serverBatchNumber = 0;
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        IsMonitoring = true;
        StatusLine = "Подготовка первого пакета из 10 серверов...";
        Log("Мониторинг запущен: пакет 10 серверов → игроки → SteamID64 → Inventory/Price → следующий пакет.");
        Log("Тяжёлые проверки ограничены 4 игроками одновременно; UI больше не должен обрабатывать тысячи server rows.");
        _monitorTask = MonitorLoopAsync(_monitorCts.Token);
    }

    private void StopMonitoring()
    {
        if (!IsMonitoring && _monitorCts is null) return;
        var cts = _monitorCts;
        _monitorCts = null;
        cts?.Cancel();
        IsMonitoring = false;
        StatusLine = "Мониторинг остановлен";
        ResetLiveDiagnostics(clearServerState: true);
        Log("Мониторинг остановлен. Активные inventory-задачи отменяются.");
    }

    private void ResetLiveDiagnostics(bool clearServerState)
    {
        _presentNames.Clear();
        _unresolvedKeys.Clear();
        _unresolvedLogTimes.Clear();
        _confirmedSteamIdsByServer.Clear();
        _yoomaLivePlayers = 0;
        _yoomaProfilePlayers = 0;
        _yoomaPagesLoaded = 0;
        _yoomaPagesAttempted = 0;
        _yoomaLastError = string.Empty;
        _cybershokeLivePlayers = 0;
        _cybershokeProfilePlayers = 0;
        _cybershokePagesLoaded = 0;
        _cybershokePagesAttempted = 0;
        _cybershokeLastError = string.Empty;
        LivePlayers.Clear();

        if (clearServerState)
        {
            foreach (var server in Servers.Where(x => x.Enabled))
            {
                server.Online = false;
                server.Players = 0;
                server.MaxPlayers = 0;
                server.PingMs = 0;
                server.Status = "Остановлен";
            }
        }
        RefreshMetrics(force: true);
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollCommunitySourcesAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    RefreshMetrics(force: true);
                    await Task.Delay(TimeSpan.FromSeconds(Settings.PollSeconds), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log("Ошибка цикла monitoring: " + ex.Message);
                    RefreshMetrics(force: true);
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { break; }
                }
            }
        }
        finally
        {
            if (ct.IsCancellationRequested && !IsMonitoring)
                ResetLiveDiagnostics(clearServerState: true);
        }
    }

    private async Task PollCommunitySourcesAsync(CancellationToken ct)
    {
        // Entire source loaders run on worker threads. Their HTML/JSON parsing can be
        // large, but it must never execute on the WPF dispatcher.
        var yoomaTask = GetSourceSafeAsync("yooma.su", token => _yooma.GetLiveAsync(token), ct);
        var cybershokeTask = GetSourceSafeAsync("CYBERSHOKE", token => _cybershoke.GetLiveAsync(token), ct);
        await Task.WhenAll(yoomaTask, cybershokeTask);
        ct.ThrowIfCancellationRequested();

        var yooma = await yoomaTask;
        var cybershoke = await cybershokeTask;
        UpdateSourceDiagnostics(yooma, cybershoke);

        var candidates = new List<CommunityServerCandidate>();
        if (yooma is not null) candidates.AddRange(BuildCandidates(yooma, "yooma.su", "yooma.su DOM"));
        if (cybershoke is not null) candidates.AddRange(BuildCandidates(cybershoke, "CYBERSHOKE", "CYBERSHOKE DOM"));

        var useful = candidates
            .Where(x => x.Players.Any(p => IsSteamId64(p.SteamId64)) || x.ReportedPlayers > 0)
            .OrderByDescending(x => x.Players.Count)
            .ThenByDescending(x => x.ReportedPlayers)
            .ThenBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (useful.Count == 0)
        {
            useful = candidates
                .OrderBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CanonicalKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (useful.Count == 0)
        {
            ClearDynamicServerRows();
            StatusLine = "Источники пока не вернули серверы с игроками";
            LogSourceWarningThrottled("batch", "Нет доступных server/player rows для текущего пакета.");
            return;
        }

        var count = Math.Min(CommunityServerBatchSize, useful.Count);
        if (_serverBatchCursor >= useful.Count) _serverBatchCursor = 0;
        var selected = new List<CommunityServerCandidate>(count);
        for (var i = 0; i < count; i++)
            selected.Add(useful[(_serverBatchCursor + i) % useful.Count]);
        _serverBatchCursor = (_serverBatchCursor + count) % useful.Count;
        _serverBatchNumber++;
        _scanBudgetRemaining = MaxScheduledScansPerBatch;

        ClearDynamicServerRows();
        LivePlayers.Clear();

        foreach (var sourceGroup in selected.GroupBy(x => x.SourceLabel, StringComparer.OrdinalIgnoreCase))
        {
            var group = sourceGroup.ToList();
            var original = group[0].Snapshot;
            var servers = group.SelectMany(x => x.ServerInfos).Distinct().ToList();
            var players = group
                .SelectMany(x => x.Players.Take(MaxPlayersPerServerBatch))
                .GroupBy(x => $"{NormalizeServerKey(EffectivePlayerServerKey(x))}|{x.SteamId64}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var slice = original with { Servers = servers, Players = players };
            await ApplyCommunitySnapshotAsync(slice, group[0].SourceName, group[0].SourceLabel, ct);
        }

        var playersInBatch = selected.Sum(x => Math.Min(MaxPlayersPerServerBatch, x.Players.Count));
        StatusLine = $"Пакет #{_serverBatchNumber}: {selected.Count} серверов / {playersInBatch} игроков — проверка...";
        Log($"Пакет #{_serverBatchNumber}: {selected.Count} серверов из {useful.Count}, игроков с данными: {playersInBatch}. Ждём завершения проверок перед следующим пакетом.");
        RefreshMetrics(force: true);

        await WaitForCurrentBatchScansAsync(ct);
        StatusLine = $"Пакет #{_serverBatchNumber} завершён. Следующие {CommunityServerBatchSize} серверов через {Settings.PollSeconds}с.";
    }

    private async Task<YoomaLiveSnapshot?> GetSourceSafeAsync(
        string sourceName,
        Func<CancellationToken, Task<YoomaLiveSnapshot>> loader,
        CancellationToken ct)
    {
        try
        {
            // Task.Run is intentional: the clients contain substantial synchronous
            // HTML/regex/JSON parsing after awaits. Keep all of that away from WPF.
            return await Task.Run(() => loader(ct), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (sourceName.Equals("yooma.su", StringComparison.OrdinalIgnoreCase)) _yoomaLastError = ex.Message;
            else _cybershokeLastError = ex.Message;
            LogSourceWarningThrottled(sourceName, $"{sourceName}: ошибка мониторинга: {ex.Message}");
            return null;
        }
    }

    private void UpdateSourceDiagnostics(YoomaLiveSnapshot? yooma, YoomaLiveSnapshot? cybershoke)
    {
        if (yooma is not null)
        {
            _yoomaLastError = string.Empty;
            _yoomaPagesLoaded = yooma.PagesLoaded;
            _yoomaPagesAttempted = yooma.PagesAttempted;
            _yoomaLivePlayers = yooma.Players.Count;
            _yoomaProfilePlayers = yooma.Players.Select(x => x.SteamId64).Where(IsSteamId64).Distinct(StringComparer.Ordinal).Count();
            if (yooma.RenderedPages > 0)
                LogSourceDomThrottled("yooma.su", $"yooma.su: {_yoomaProfilePlayers} SteamID64; render {yooma.RenderedPages}; {yooma.RenderEngine}.");
            if (yooma.Players.Count == 0)
                LogSourceWarningThrottled("yooma.su", $"yooma.su: live SteamID пока нет; HTTP {yooma.PagesLoaded}/{yooma.PagesAttempted}, render {yooma.RenderedPages}.");
        }

        if (cybershoke is not null)
        {
            _cybershokeLastError = string.Empty;
            _cybershokePagesLoaded = cybershoke.PagesLoaded;
            _cybershokePagesAttempted = cybershoke.PagesAttempted;
            _cybershokeLivePlayers = cybershoke.Players.Count;
            _cybershokeProfilePlayers = cybershoke.Players.Select(x => x.SteamId64).Where(IsSteamId64).Distinct(StringComparer.Ordinal).Count();
            if (cybershoke.RenderedPages > 0)
                LogSourceDomThrottled("CYBERSHOKE", $"CYBERSHOKE: {_cybershokeProfilePlayers} SteamID64; render {cybershoke.RenderedPages}; {cybershoke.RenderEngine}.");
            if (cybershoke.Players.Count == 0)
                LogSourceWarningThrottled("CYBERSHOKE", $"CYBERSHOKE: live SteamID пока нет; HTTP {cybershoke.PagesLoaded}/{cybershoke.PagesAttempted}, render {cybershoke.RenderedPages}.");
        }
    }

    private static List<CommunityServerCandidate> BuildCandidates(YoomaLiveSnapshot snapshot, string sourceName, string sourceLabel)
    {
        var candidates = new List<CommunityServerCandidate>();
        var aliasMap = new Dictionary<string, CommunityServerCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in snapshot.Servers)
        {
            var aliases = new[] { info.Key, info.Address }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeServerKey)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (aliases.Length == 0) continue;

            CommunityServerCandidate? candidate = null;
            foreach (var alias in aliases)
            {
                if (aliasMap.TryGetValue(alias, out candidate)) break;
            }
            if (candidate is null)
            {
                candidate = new CommunityServerCandidate(sourceName, sourceLabel, snapshot, aliases[0]);
                candidates.Add(candidate);
            }
            candidate.ServerInfos.Add(info);
            candidate.ReportedPlayers = Math.Max(candidate.ReportedPlayers, Math.Max(0, info.Players));
            foreach (var alias in aliases)
            {
                candidate.Aliases.Add(alias);
                aliasMap[alias] = candidate;
            }
        }

        foreach (var player in snapshot.Players)
        {
            var aliases = new[] { player.ServerKey, player.ServerAddress }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeServerKey)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (aliases.Length == 0) continue;

            CommunityServerCandidate? candidate = null;
            foreach (var alias in aliases)
            {
                if (aliasMap.TryGetValue(alias, out candidate)) break;
            }
            if (candidate is null)
            {
                candidate = new CommunityServerCandidate(sourceName, sourceLabel, snapshot, aliases[0]);
                candidates.Add(candidate);
            }
            candidate.Players.Add(player);
            foreach (var alias in aliases)
            {
                candidate.Aliases.Add(alias);
                aliasMap[alias] = candidate;
            }
        }

        return candidates;
    }

    private void ClearDynamicServerRows()
    {
        foreach (var server in Servers.Where(x => !x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            server.PropertyChanged -= Server_PropertyChanged;
            Servers.Remove(server);
        }
    }

    private async Task ApplyCommunitySnapshotAsync(
        YoomaLiveSnapshot snapshot,
        string sourceName,
        string sourceLabel,
        CancellationToken ct)
    {
        var serverMap = new Dictionary<string, ServerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in snapshot.Servers)
        {
            var key = string.IsNullOrWhiteSpace(info.Address) ? info.Key : info.Address;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var server = Servers.FirstOrDefault(x => x.Address.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (server is null)
            {
                server = new ServerEntry { Address = key, Enabled = true, Source = sourceLabel };
                AttachServer(server);
                Servers.Add(server);
            }
            server.Enabled = true;
            server.Online = true;
            server.Name = string.IsNullOrWhiteSpace(info.Name) ? sourceName : info.Name;
            server.Map = string.IsNullOrWhiteSpace(info.Map) ? "—" : info.Map;
            server.Players = Math.Max(0, info.Players);
            server.MaxPlayers = Math.Max(0, info.MaxPlayers);
            server.PingMs = 0;
            server.LastCheckUtc = snapshot.LoadedUtc;
            server.Status = sourceName + " • пакет";
            server.Source = sourceLabel;
            serverMap[NormalizeServerKey(info.Key)] = server;
            serverMap[NormalizeServerKey(key)] = server;
        }

        foreach (var group in snapshot.Players.GroupBy(x => NormalizeServerKey(EffectivePlayerServerKey(x)), StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (!serverMap.TryGetValue(NormalizeServerKey(first.ServerKey), out var server) &&
                !serverMap.TryGetValue(NormalizeServerKey(EffectivePlayerServerKey(first)), out server))
            {
                var address = EffectivePlayerServerKey(first);
                server = Servers.FirstOrDefault(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    server = new ServerEntry
                    {
                        Address = address,
                        Enabled = true,
                        Online = true,
                        Name = string.IsNullOrWhiteSpace(first.ServerName) ? sourceName + " • LIVE" : first.ServerName,
                        Map = "—",
                        Source = sourceLabel,
                        Status = sourceName + " • пакет",
                        LastCheckUtc = snapshot.LoadedUtc
                    };
                    AttachServer(server);
                    Servers.Add(server);
                }
                serverMap[NormalizeServerKey(address)] = server;
            }

            var roster = group
                .Where(x => IsSteamId64(x.SteamId64))
                .GroupBy(x => x.SteamId64, StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(MaxPlayersPerServerBatch)
                .Select(x => new MonitoringPlayer(string.IsNullOrWhiteSpace(x.Nickname) ? x.SteamId64 : x.Nickname, x.SteamId64))
                .ToList();

            server.Online = true;
            server.Players = Math.Max(server.Players, roster.Count);
            server.LastCheckUtc = snapshot.LoadedUtc;
            server.Source = sourceLabel;
            _confirmedSteamIdsByServer[server.Address] = roster.Select(x => x.SteamId64!).Distinct(StringComparer.Ordinal).ToArray();
            await ProcessRosterAsync(server, roster, ct);
        }

        // Defensive cap: even if one source aliases the same server in several forms,
        // the WPF DataGrid never receives an unbounded community catalog.
        var dynamicRows = Servers.Where(x => !x.Source.Equals("Ручной", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var extra in dynamicRows.Skip(CommunityServerBatchSize))
        {
            extra.PropertyChanged -= Server_PropertyChanged;
            Servers.Remove(extra);
        }
    }

    private async Task ProcessRosterAsync(ServerEntry server, IReadOnlyList<MonitoringPlayer> roster, CancellationToken ct)
    {
        var nowNames = new HashSet<string>(roster.Select(x => Normalize(x.Name)), StringComparer.OrdinalIgnoreCase);
        _presentNames.TryGetValue(server.Address, out var previous);
        previous ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cacheChanged = false;
        foreach (var p in roster)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var normalized = Normalize(p.Name);
            var nameKey = $"{server.Address}|{normalized}";
            UpsertLivePlayer(server.Address, p.Name);

            string? knownSteamId = IsSteamId64(p.SteamId64 ?? string.Empty) ? p.SteamId64 : null;
            if (knownSteamId is null && _cache.NameResolves.TryGetValue(nameKey, out var cachedName) &&
                IsSteamId64(cachedName.SteamId64 ?? string.Empty) &&
                DateTime.UtcNow - cachedName.LastAttemptUtc < SteamCheckTtl)
                knownSteamId = cachedName.SteamId64;

            if (knownSteamId is not null)
            {
                if (string.Equals(knownSteamId, p.SteamId64, StringComparison.Ordinal))
                {
                    _cache.NameResolves[nameKey] = new NameResolveCache { SteamId64 = knownSteamId, LastAttemptUtc = DateTime.UtcNow };
                    cacheChanged = true;
                }
                SetLiveSteam(server.Address, p.Name, knownSteamId);
                _unresolvedKeys.TryRemove(nameKey, out _);
                _unresolvedLogTimes.TryRemove(nameKey, out _);
                AddConfirmedForServer(server.Address, knownSteamId);
            }

            var shouldScan = !previous.Contains(normalized);
            if (!shouldScan)
            {
                if (knownSteamId is not null)
                    shouldScan = !_cache.SteamChecks.TryGetValue(knownSteamId, out var check) || DateTime.UtcNow - check.LastCheckedUtc >= SteamCheckTtl;
                else if (!_cache.NameResolves.TryGetValue(nameKey, out var resolveCache) || DateTime.UtcNow - resolveCache.LastAttemptUtc >= UnresolvedRetryDelay)
                    shouldScan = true;
            }

            if (shouldScan && _scanBudgetRemaining > 0)
            {
                _scanBudgetRemaining--;
                ScheduleScan(server, p.Name, knownSteamId, ct);
            }
            else if (shouldScan)
            {
                SetLiveStatus(server.Address, p.Name, "Отложен до следующего пакета");
            }
        }

        foreach (var left in previous.Where(x => !nowNames.Contains(x)))
        {
            var key = $"{server.Address}|{left}";
            _unresolvedKeys.TryRemove(key, out _);
            _unresolvedLogTimes.TryRemove(key, out _);
        }

        if (cacheChanged) await _store.SaveCacheAsync(_cache);
        _presentNames[server.Address] = nowNames;
    }

    private void ScheduleScan(ServerEntry server, string nickname, string? knownSteamId, CancellationToken monitorCt)
    {
        var task = ScanSafeAsync(server, nickname, knownSteamId, monitorCt);
        var id = Interlocked.Increment(ref _playerTaskSequence);
        _playerTasks[id] = task;
        _ = task.ContinueWith(_ => _playerTasks.TryRemove(id, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForCurrentBatchScansAsync(CancellationToken ct)
    {
        var tasks = _playerTasks.Values.ToArray();
        if (tasks.Length == 0) return;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch (TimeoutException)
        {
            Log("Пакет: часть inventory-проверок превысила 90с. Следующий пакет продолжит работу, зависшие SteamID остаются под per-ID gate.");
        }
    }

    private async Task ScanSafeAsync(ServerEntry server, string nickname, string? knownSteamId, CancellationToken monitorCt)
    {
        var flightKey = $"{server.Address}|{Normalize(nickname)}";
        if (!_inFlight.TryAdd(flightKey, 0)) return;
        try { await ProcessPlayerAsync(server, nickname, knownSteamId, monitorCt); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetLiveStatus(server.Address, nickname, "Ошибка оценки");
            Log($"{nickname}: scan error — {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(flightKey, out _);
            RefreshMetrics();
        }
    }

    private async Task ProcessPlayerAsync(ServerEntry server, string nickname, string? knownSteamId, CancellationToken ct)
    {
        var nameKey = $"{server.Address}|{Normalize(nickname)}";
        string? steamId = IsSteamId64(knownSteamId ?? string.Empty) ? knownSteamId : null;

        if (steamId is null && _cache.NameResolves.TryGetValue(nameKey, out var nameCache))
        {
            var age = DateTime.UtcNow - nameCache.LastAttemptUtc;
            if (IsSteamId64(nameCache.SteamId64 ?? string.Empty) && age < SteamCheckTtl) steamId = nameCache.SteamId64;
            else if (string.IsNullOrWhiteSpace(nameCache.SteamId64) && age < UnresolvedRetryDelay)
            {
                MarkUnresolved(server.Address, nickname, nameKey, log: false);
                return;
            }
        }

        if (steamId is null)
        {
            SetLiveStatus(server.Address, nickname, "Поиск SteamID...");
            _cache.NameResolves[nameKey] = new NameResolveCache { SteamId64 = null, LastAttemptUtc = DateTime.UtcNow };
            await _store.SaveCacheAsync(_cache);
        }

        ct.ThrowIfCancellationRequested();
        if (!IsSteamId64(steamId ?? string.Empty))
        {
            MarkUnresolved(server.Address, nickname, nameKey, log: true);
            return;
        }

        _unresolvedKeys.TryRemove(nameKey, out _);
        _unresolvedLogTimes.TryRemove(nameKey, out _);
        SetLiveSteam(server.Address, nickname, steamId!);
        AddConfirmedForServer(server.Address, steamId!);

        var steamGate = _steamScanLocks.GetOrAdd(steamId!, _ => new SemaphoreSlim(1, 1));
        await steamGate.WaitAsync(ct);
        try
        {
            if (_cache.SteamChecks.TryGetValue(steamId!, out var checkedCache) && DateTime.UtcNow - checkedCache.LastCheckedUtc < SteamCheckTtl)
            {
                AttachServerToExistingResult(steamId!, server.Address);
                SetLiveStatus(server.Address, nickname, "Уже проверен <24ч");
                return;
            }

            await _scanGate.WaitAsync(ct);
            Interlocked.Increment(ref _inventoryProcessing);
            RefreshMetrics();
            try
            {
                SetLiveStatus(server.Address, nickname, "Оценка инвентарей...");
                var profileTask = _profiles.GetAsync(steamId!, nickname, ct);
                var valuationTask = _valuation.ValuePlayerAsync(steamId!, Settings.CsFloatApiKey, ct);
                await Task.WhenAll(profileTask, valuationTask);
                var profile = await profileTask;
                var valuation = await valuationTask;

                _cache.SteamChecks[steamId!] = new SteamCheckCache { LastCheckedUtc = DateTime.UtcNow };
                await _store.SaveCacheAsync(_cache);
                _checkedSession++;

                var matched = MatchGames(valuation);
                var inaccessible = new List<string>();
                if (!valuation.Cs2.Accessible) inaccessible.Add("CS2");
                if (!valuation.Dota2.Accessible) inaccessible.Add("Dota2");
                if (!valuation.Rust.Accessible) inaccessible.Add("Rust");
                var status = inaccessible.Count == 0 ? "OK" : "Закрыто: " + string.Join(", ", inaccessible);

                if (matched.Count > 0)
                {
                    UpsertResult(new PlayerResult
                    {
                        SteamId64 = steamId!,
                        Nickname = profile.Nickname,
                        SteamUrl = profile.ProfileUrl,
                        AvatarUrl = profile.AvatarUrl,
                        Cs2Rub = valuation.Cs2.ValueRub,
                        DotaRub = valuation.Dota2.ValueRub,
                        RustRub = valuation.Rust.ValueRub,
                        MatchedBy = string.Join(", ", matched),
                        Servers = server.Address,
                        FirstSeenUtc = DateTime.UtcNow,
                        LastCheckedUtc = DateTime.UtcNow,
                        Status = status
                    });
                    SetLiveStatus(server.Address, nickname, "MATCH ✓");
                    Log($"MATCH: {profile.Nickname} ({steamId}) — {valuation.Total:N0} ₽ [{string.Join(", ", matched)}]");
                }
                else
                {
                    SetLiveStatus(server.Address, nickname,
                        $"Проверен: CS2 {valuation.Cs2.ValueRub:N0} / D2 {valuation.Dota2.ValueRub:N0} / Rust {valuation.Rust.ValueRub:N0} ₽");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inventoryProcessing);
                _scanGate.Release();
                RefreshMetrics();
            }
        }
        finally { steamGate.Release(); }
    }

    private void MarkUnresolved(string serverAddress, string nickname, string nameKey, bool log)
    {
        _unresolvedKeys.TryAdd(nameKey, 0);
        SetLiveStatus(serverAddress, nickname, "SteamID unresolved · retry ~45с");
        if (log) LogUnresolvedThrottled(serverAddress, nickname, nameKey);
        RefreshMetrics();
    }

    private void AddConfirmedForServer(string serverAddress, string steamId)
    {
        if (!IsSteamId64(steamId)) return;
        _confirmedSteamIdsByServer.AddOrUpdate(serverAddress, _ => [steamId], (_, existing) =>
            existing.Contains(steamId, StringComparer.Ordinal) ? existing : existing.Append(steamId).ToArray());
    }

    private void AttachServerToExistingResult(string steamId, string serverAddress)
    {
        var existing = Results.FirstOrDefault(x => x.SteamId64 == steamId);
        if (existing is null) return;
        var servers = existing.Servers.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (servers.Add(serverAddress)) existing.Servers = string.Join("; ", servers);
    }

    private List<string> MatchGames(PlayerValuation value)
    {
        var result = new List<string>();
        if (value.Cs2.Accessible && value.Cs2.ValueRub > 0 && InRange(value.Cs2.ValueRub, Settings.Cs2Min, Settings.Cs2Max)) result.Add("CS2");
        if (value.Dota2.Accessible && value.Dota2.ValueRub > 0 && InRange(value.Dota2.ValueRub, Settings.DotaMin, Settings.DotaMax)) result.Add("Dota 2");
        if (value.Rust.Accessible && value.Rust.ValueRub > 0 && InRange(value.Rust.ValueRub, Settings.RustMin, Settings.RustMax)) result.Add("Rust");
        return result;
    }

    private static bool InRange(decimal value, decimal min, decimal max) =>
        value >= Math.Min(min, max) && value <= Math.Max(min, max);

    private void UpsertResult(PlayerResult incoming)
    {
        var existing = Results.FirstOrDefault(x => x.SteamId64 == incoming.SteamId64);
        if (existing is null)
        {
            Results.Insert(0, incoming);
            SelectedResult ??= incoming;
            RefreshMetrics(force: true);
            return;
        }

        var servers = existing.Servers.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        servers.Add(incoming.Servers);
        existing.Servers = string.Join("; ", servers);
        existing.Nickname = incoming.Nickname;
        existing.SteamUrl = incoming.SteamUrl;
        existing.AvatarUrl = incoming.AvatarUrl;
        existing.Cs2Rub = incoming.Cs2Rub;
        existing.DotaRub = incoming.DotaRub;
        existing.RustRub = incoming.RustRub;
        existing.MatchedBy = incoming.MatchedBy;
        existing.Status = incoming.Status;
        existing.LastCheckedUtc = incoming.LastCheckedUtc;
        RefreshMetrics(force: true);
    }

    private void UpsertLivePlayer(string server, string nickname)
    {
        var key = $"{server}|{Normalize(nickname)}";
        var existing = LivePlayers.FirstOrDefault(x => x.Key == key);
        if (existing is not null)
        {
            existing.LastSeenUtc = DateTime.UtcNow;
            return;
        }
        LivePlayers.Insert(0, new LivePlayerRow { Key = key, Server = server, Nickname = nickname, LastSeenUtc = DateTime.UtcNow });
        while (LivePlayers.Count > 160) LivePlayers.RemoveAt(LivePlayers.Count - 1);
    }

    private void SetLiveStatus(string server, string nickname, string status)
    {
        var row = LivePlayers.FirstOrDefault(x => x.Key == $"{server}|{Normalize(nickname)}");
        if (row is not null) row.Status = status;
    }

    private void SetLiveSteam(string server, string nickname, string steamId)
    {
        var row = LivePlayers.FirstOrDefault(x => x.Key == $"{server}|{Normalize(nickname)}");
        if (row is not null) row.SteamId64 = steamId;
    }

    private async Task ManualCheckAsync()
    {
        var steamId = ManualSteamId.Trim();
        if (!IsSteamId64(steamId))
        {
            ManualResultText = "Некорректный SteamID64. Ожидается 17 цифр, обычно начиная с 7656.";
            return;
        }

        ManualResultText = "Проверка...";
        try
        {
            var profileTask = _profiles.GetAsync(steamId, steamId, CancellationToken.None);
            var valueTask = _valuation.ValuePlayerAsync(steamId, Settings.CsFloatApiKey, CancellationToken.None);
            await Task.WhenAll(profileTask, valueTask);
            var p = await profileTask;
            var v = await valueTask;
            var match = MatchGames(v);
            ManualResultText =
                $"{p.Nickname}\n" +
                $"CS2: {v.Cs2.ValueRub:N0} ₽ ({InventoryState(v.Cs2)})\n" +
                $"Dota 2: {v.Dota2.ValueRub:N0} ₽ ({InventoryState(v.Dota2)})\n" +
                $"Rust: {v.Rust.ValueRub:N0} ₽ ({InventoryState(v.Rust)})\n" +
                $"Итого: {v.Total:N0} ₽\n" +
                $"Фильтр: {(match.Count > 0 ? string.Join(", ", match) : "не подходит")}";
        }
        catch (Exception ex)
        {
            ManualResultText = "Ошибка проверки: " + ex.Message;
        }
    }

    private static string InventoryState(GameValuation value)
    {
        if (!value.Accessible) return "закрыт/недоступен";
        var pricing = value.PricingAvailable ? "цены OK" : "цены недоступны";
        return $"{value.ItemCount} items, marketable {value.MarketableItems}, {value.UnpricedItems} без цены, {pricing}";
    }

    private async Task SaveAllAsync()
    {
        await _store.SaveSettingsAsync(Settings);
        await _store.SaveCacheAsync(_cache);
        StatusLine = "Настройки сохранены";
        Log("Настройки сохранены.");
    }

    private async Task ClearCacheAsync()
    {
        _cache = new CacheState();
        await _store.SaveCacheAsync(_cache);
        Log("24-часовой cache очищен.");
        MessageBox.Show("Кэш проверок очищен.", "BeaverSearch", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportResults()
    {
        if (Results.Count == 0)
        {
            MessageBox.Show("Список результатов пуст.", "BeaverSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"BeaverSearch_{DateTime.Now:yyyy-MM-dd_HH-mm}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            XlsxExporter.Export(dialog.FileName, Results.ToList());
            StatusLine = "Excel экспортирован";
            Log("Экспорт: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось создать Excel:\n" + ex.Message, "BeaverSearch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearResults()
    {
        Results.Clear();
        SelectedResult = null;
        RefreshMetrics(force: true);
    }

    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", _store.DataFolder) { UseShellExecute = true });

    private void OpenSelectedSteam()
    {
        if (SelectedResult is null || string.IsNullOrWhiteSpace(SelectedResult.SteamUrl)) return;
        Process.Start(new ProcessStartInfo(SelectedResult.SteamUrl) { UseShellExecute = true });
    }

    private void CopySelectedSteam()
    {
        if (SelectedResult is null || string.IsNullOrWhiteSpace(SelectedResult.SteamId64)) return;
        try
        {
            Clipboard.SetText(SelectedResult.SteamId64);
            StatusLine = "SteamID64 скопирован";
        }
        catch (Exception ex)
        {
            Log("Не удалось скопировать SteamID64: " + ex.Message);
        }
    }

    private void RefreshMetrics(bool force = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => RefreshMetrics(force), DispatcherPriority.Background);
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now - _lastMetricsRefreshTick < 140)
        {
            if (Interlocked.Exchange(ref _metricsRefreshQueued, 1) == 0 && dispatcher is not null)
            {
                dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _metricsRefreshQueued, 0);
                    RefreshMetrics(force: true);
                }, DispatcherPriority.Background);
            }
            return;
        }
        _lastMetricsRefreshTick = now;

        OnPropertyChanged(nameof(ServersOnline)); OnPropertyChanged(nameof(PlayersOnline)); OnPropertyChanged(nameof(CheckedSession));
        OnPropertyChanged(nameof(UnresolvedSession)); OnPropertyChanged(nameof(MatchCount)); OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(UniquePlayers)); OnPropertyChanged(nameof(AverageResultValue)); OnPropertyChanged(nameof(HighestResultValue));
        OnPropertyChanged(nameof(HighestResultNickname)); OnPropertyChanged(nameof(ProcessingCount)); OnPropertyChanged(nameof(ScanParallelism));
        OnPropertyChanged(nameof(EnabledServerCount)); OnPropertyChanged(nameof(ServerProgressValue)); OnPropertyChanged(nameof(ServerProgressMaximum));
        OnPropertyChanged(nameof(YoomaLivePlayers)); OnPropertyChanged(nameof(YoomaProfilePlayers)); OnPropertyChanged(nameof(YoomaPagesLoaded));
        OnPropertyChanged(nameof(YoomaPagesAttempted)); OnPropertyChanged(nameof(CybershokeLivePlayers)); OnPropertyChanged(nameof(CybershokeProfilePlayers));
        OnPropertyChanged(nameof(CybershokePagesLoaded)); OnPropertyChanged(nameof(CybershokePagesAttempted)); OnPropertyChanged(nameof(CommunityLivePlayers));
        OnPropertyChanged(nameof(CommunityProfilePlayers)); OnPropertyChanged(nameof(ConfirmedSteamIds)); OnPropertyChanged(nameof(CacheRecordCount));
        OnPropertyChanged(nameof(YoomaHealthText)); OnPropertyChanged(nameof(YoomaProfileHealthText)); OnPropertyChanged(nameof(CybershokeHealthText));
        OnPropertyChanged(nameof(CybershokeProfileHealthText)); OnPropertyChanged(nameof(PriceHealthText)); OnPropertyChanged(nameof(InventoryHealthText));
        OnPropertyChanged(nameof(CacheHealthText)); OnPropertyChanged(nameof(DataFolder));
    }

    private void LogUnresolvedThrottled(string serverAddress, string nickname, string nameKey)
    {
        var now = DateTime.UtcNow;
        if (_unresolvedLogTimes.TryGetValue(nameKey, out var last) && now - last < TimeSpan.FromMinutes(5)) return;
        _unresolvedLogTimes[nameKey] = now;
        Log($"Unresolved: {nickname} @ {serverAddress}. Повтор resolver через ~45с.");
    }

    private void LogSourceWarningThrottled(string source, string message)
    {
        var now = DateTime.UtcNow;
        if (_sourceWarningTimes.TryGetValue(source, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _sourceWarningTimes[source] = now;
        Log(message);
    }

    private void LogSourceDomThrottled(string source, string message)
    {
        var now = DateTime.UtcNow;
        if (_sourceDomLogTimes.TryGetValue(source, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        _sourceDomLogTimes[source] = now;
        Log(message);
    }

    private void Log(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Log(message), DispatcherPriority.Background);
            return;
        }
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }

    private static string EffectivePlayerServerKey(YoomaPlayer player) =>
        string.IsNullOrWhiteSpace(player.ServerAddress) ? player.ServerKey : player.ServerAddress;

    private static string NormalizeServerKey(string value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsSteamId64(string s) =>
        s.Length == 17 && s.All(char.IsDigit) && s.StartsWith("7656", StringComparison.Ordinal);

    private static string Normalize(string s) =>
        string.Join(' ', s.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private sealed class CommunityServerCandidate
    {
        public CommunityServerCandidate(string sourceName, string sourceLabel, YoomaLiveSnapshot snapshot, string canonicalKey)
        {
            SourceName = sourceName;
            SourceLabel = sourceLabel;
            Snapshot = snapshot;
            CanonicalKey = canonicalKey;
        }

        public string SourceName { get; }
        public string SourceLabel { get; }
        public YoomaLiveSnapshot Snapshot { get; }
        public string CanonicalKey { get; }
        public int ReportedPlayers { get; set; }
        public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<YoomaServerInfo> ServerInfos { get; } = [];
        public List<YoomaPlayer> Players { get; } = [];
    }
}
