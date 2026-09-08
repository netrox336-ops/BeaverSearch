namespace BeaverSearch.Models;

/// <summary>
/// Internal roster item. In 0.4.1 SteamId64 comes from the yooma.su / CYBERSHOKE live DOM or site state,
/// never from nickname matching.
/// </summary>
public sealed record MonitoringPlayer(string Name, string? SteamId64);
