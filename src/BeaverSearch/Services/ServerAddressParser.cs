using System.Text.RegularExpressions;

namespace BeaverSearch.Services;

public static class ServerAddressParser
{
    private static readonly Regex HostPortRegex = new(
        @"(?<host>(?:\[[0-9a-fA-F:]+\])|(?:[A-Za-z0-9._-]+)):(?<port>\d{1,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Введите IP:PORT или domain:PORT.";
            return false;
        }

        var value = input.Trim().Trim('"', '\'', '`');
        value = value.Replace("steam://connect/", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .Replace("connect://", string.Empty, StringComparison.OrdinalIgnoreCase)
                     .Trim();

        if (value.StartsWith("+connect ", StringComparison.OrdinalIgnoreCase))
            value = value[9..].Trim();
        else if (value.StartsWith("connect ", StringComparison.OrdinalIgnoreCase))
            value = value[8..].Trim();

        // Steam launch strings may contain extra arguments. Pick the first explicit host:port pair.
        var match = HostPortRegex.Match(value);
        if (!match.Success)
        {
            error = "Адрес должен содержать IP:PORT или domain:PORT.";
            return false;
        }

        var host = match.Groups["host"].Value.Trim();
        if (!int.TryParse(match.Groups["port"].Value, out var port) || port is <= 0 or > 65535)
        {
            error = "Порт должен быть числом от 1 до 65535.";
            return false;
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        if (host.Any(char.IsWhiteSpace))
        {
            error = "Имя хоста не должно содержать пробелы.";
            return false;
        }

        normalized = $"{host}:{port}";
        return true;
    }

    public static (string Host, int Port) Parse(string input)
    {
        if (!TryNormalize(input, out var normalized, out var error))
            throw new FormatException(error);

        var idx = normalized.LastIndexOf(':');
        return (normalized[..idx], int.Parse(normalized[(idx + 1)..]));
    }
}
