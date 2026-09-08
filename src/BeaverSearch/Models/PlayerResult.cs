using BeaverSearch.Infrastructure;

namespace BeaverSearch.Models;

public sealed class PlayerResult : ObservableObject
{
    private string _nickname = string.Empty;
    private string _steamUrl = string.Empty;
    private string _avatarUrl = string.Empty;
    private decimal _cs2Rub;
    private decimal _dotaRub;
    private decimal _rustRub;
    private string _matchedBy = string.Empty;
    private string _servers = string.Empty;
    private DateTime _lastCheckedUtc;
    private string _status = "OK";

    public string SteamId64 { get; init; } = string.Empty;
    public string Nickname { get => _nickname; set => SetProperty(ref _nickname, value); }
    public string SteamUrl { get => _steamUrl; set => SetProperty(ref _steamUrl, value); }
    public string AvatarUrl { get => _avatarUrl; set => SetProperty(ref _avatarUrl, value); }
    public decimal Cs2Rub { get => _cs2Rub; set { if (SetProperty(ref _cs2Rub, value)) OnPropertyChanged(nameof(TotalRub)); } }
    public decimal DotaRub { get => _dotaRub; set { if (SetProperty(ref _dotaRub, value)) OnPropertyChanged(nameof(TotalRub)); } }
    public decimal RustRub { get => _rustRub; set { if (SetProperty(ref _rustRub, value)) OnPropertyChanged(nameof(TotalRub)); } }
    public decimal TotalRub => Cs2Rub + DotaRub + RustRub;
    public string MatchedBy { get => _matchedBy; set => SetProperty(ref _matchedBy, value); }
    public string Servers { get => _servers; set => SetProperty(ref _servers, value); }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastCheckedUtc { get => _lastCheckedUtc; set => SetProperty(ref _lastCheckedUtc, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
}
