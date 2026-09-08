namespace BeaverSearch.Models;

// Kept under the historical Yooma* names to avoid a risky model migration in the
// 0.4.1 hotfix.  The same neutral snapshot shape is also used by CYBERSHOKE.
public sealed record YoomaPlayer(
    string Nickname,
    string SteamId64,
    string YoomaProfileUrl,
    string ServerKey,
    string ServerName,
    string ServerAddress,
    string SourcePage);

public sealed record YoomaServerInfo(
    string Key,
    string Name,
    string Address,
    string Map,
    int Players,
    int MaxPlayers,
    string SourcePage);

public sealed record YoomaLiveSnapshot(
    IReadOnlyList<YoomaPlayer> Players,
    IReadOnlyList<YoomaServerInfo> Servers,
    int PagesLoaded,
    int PagesAttempted,
    int ProfileLinksFound,
    int RenderedPages,
    string RenderEngine,
    string? RenderWarning,
    DateTime LoadedUtc);
