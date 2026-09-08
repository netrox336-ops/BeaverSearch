using BeaverSearch.Infrastructure;

namespace BeaverSearch.Models;

public sealed class LivePlayerRow : ObservableObject
{
    private string _steamId64 = "—";
    private string _status = "Обнаружен";
    private DateTime _lastSeenUtc = DateTime.UtcNow;

    public string Key { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string SteamId64 { get => _steamId64; set => SetProperty(ref _steamId64, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public DateTime LastSeenUtc
    {
        get => _lastSeenUtc;
        set
        {
            if (SetProperty(ref _lastSeenUtc, value)) OnPropertyChanged(nameof(LastSeenText));
        }
    }
    public string LastSeenText => LastSeenUtc.ToLocalTime().ToString("HH:mm:ss");
}
