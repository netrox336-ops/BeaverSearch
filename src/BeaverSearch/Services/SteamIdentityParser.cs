using System.Globalization;
using System.Text.RegularExpressions;

namespace BeaverSearch.Services;

/// <summary>
/// Deterministic conversion of Steam identity formats exposed by monitoring sites.
/// No nickname lookup is performed. AccountID/Steam2/Steam3 values are accepted only
/// by callers that already established an explicit steam/account field context.
/// </summary>
public static class SteamIdentityParser
{
    private const ulong IndividualUniverseBase = 76561197960265728UL;
    private static readonly Regex Steam2Regex = new(
        @"^STEAM_[0-5]:(?<y>[01]):(?<z>\d{1,10})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Steam3Regex = new(
        @"^\[?U:1:(?<account>\d{1,10})\]?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsSteamId64(string? value) =>
        value is { Length: 17 } &&
        value.StartsWith("7656", StringComparison.Ordinal) &&
        value.All(char.IsDigit);

    public static bool TryNormalizeExplicit(string? value, out string steamId64)
    {
        steamId64 = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().Trim('"', '\'', ' ', '\t', '\r', '\n');

        if (IsSteamId64(text))
        {
            steamId64 = text;
            return true;
        }

        var steam2 = Steam2Regex.Match(text);
        if (steam2.Success &&
            ulong.TryParse(steam2.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y) &&
            ulong.TryParse(steam2.Groups["z"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var z))
        {
            var accountId = checked(z * 2UL + y);
            return TryFromAccountId(accountId, out steamId64);
        }

        var steam3 = Steam3Regex.Match(text);
        if (steam3.Success &&
            ulong.TryParse(steam3.Groups["account"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var steam3Account))
            return TryFromAccountId(steam3Account, out steamId64);

        // A plain integer here means Steam AccountID. Callers must only use this
        // method after proving the surrounding key/attribute is explicitly Steam/account data.
        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var accountIdOnly))
            return TryFromAccountId(accountIdOnly, out steamId64);

        return false;
    }

    private static bool TryFromAccountId(ulong accountId, out string steamId64)
    {
        steamId64 = string.Empty;
        if (accountId == 0 || accountId > uint.MaxValue) return false;
        steamId64 = (IndividualUniverseBase + accountId).ToString(CultureInfo.InvariantCulture);
        return IsSteamId64(steamId64);
    }
}
