using BeaverSearch.Infrastructure;

namespace BeaverSearch.Models;

public sealed class ServerEntry : ObservableObject
{
    private string _address = string.Empty;
    private bool _enabled = true;
    private bool _online;
    private string _name = "Не проверен";
    private string _map = "—";
    private int _players;
    private int _maxPlayers;
    private string _status = "Ожидание";
    private int _pingMs;
    private DateTime? _lastCheckUtc;
    private string _source = "community DOM";

    public string Address { get => _address; set => SetProperty(ref _address, value ?? string.Empty); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool Online { get => _online; set => SetProperty(ref _online, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Map { get => _map; set => SetProperty(ref _map, value); }
    public int Players { get => _players; set { if (SetProperty(ref _players, value)) OnPropertyChanged(nameof(PlayerCountText)); } }
    public int MaxPlayers { get => _maxPlayers; set { if (SetProperty(ref _maxPlayers, value)) OnPropertyChanged(nameof(PlayerCountText)); } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public int PingMs { get => _pingMs; set { if (SetProperty(ref _pingMs, value)) OnPropertyChanged(nameof(PingText)); } }
    public DateTime? LastCheckUtc { get => _lastCheckUtc; set { if (SetProperty(ref _lastCheckUtc, value)) OnPropertyChanged(nameof(LastCheckText)); } }
    public string Source { get => _source; set => SetProperty(ref _source, value); }

    public string PlayerCountText => MaxPlayers > 0 ? $"{Players}/{MaxPlayers}" : Players.ToString();
    public string PingText => Online && PingMs > 0 ? $"{PingMs} мс" : "—";
    public string LastCheckText => LastCheckUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
}
