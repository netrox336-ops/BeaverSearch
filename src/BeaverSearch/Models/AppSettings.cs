using BeaverSearch.Infrastructure;

namespace BeaverSearch.Models;

public sealed class AppSettings : ObservableObject
{
    private decimal _cs2Min = 3500;
    private decimal _cs2Max = 30000;
    private decimal _dotaMin = 1000;
    private decimal _dotaMax = 5000;
    private decimal _rustMin = 4000;
    private decimal _rustMax = 10000;
    private int _pollSeconds = 12;
    private string _csFloatApiKey = string.Empty;
    private int _requestTimeoutSeconds = 10;
    private bool _minimizeToTray;
    private string _language = "Русский";

    public decimal Cs2Min
    {
        get => _cs2Min;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _cs2Min, value)) return;
            if (_cs2Max < value) Cs2Max = value;
        }
    }

    public decimal Cs2Max
    {
        get => _cs2Max;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _cs2Max, value)) return;
            if (_cs2Min > value) Cs2Min = value;
        }
    }

    public decimal DotaMin
    {
        get => _dotaMin;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _dotaMin, value)) return;
            if (_dotaMax < value) DotaMax = value;
        }
    }

    public decimal DotaMax
    {
        get => _dotaMax;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _dotaMax, value)) return;
            if (_dotaMin > value) DotaMin = value;
        }
    }

    public decimal RustMin
    {
        get => _rustMin;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _rustMin, value)) return;
            if (_rustMax < value) RustMax = value;
        }
    }

    public decimal RustMax
    {
        get => _rustMax;
        set
        {
            value = Math.Max(0, value);
            if (!SetProperty(ref _rustMax, value)) return;
            if (_rustMin > value) RustMin = value;
        }
    }

    public int PollSeconds { get => _pollSeconds; set => SetProperty(ref _pollSeconds, Math.Clamp(value, 8, 120)); }
    public string CsFloatApiKey { get => _csFloatApiKey; set => SetProperty(ref _csFloatApiKey, value ?? string.Empty); }
    public int RequestTimeoutSeconds { get => _requestTimeoutSeconds; set => SetProperty(ref _requestTimeoutSeconds, Math.Clamp(value, 5, 30)); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public string Language { get => _language; set => SetProperty(ref _language, string.IsNullOrWhiteSpace(value) ? "Русский" : value); }
}
