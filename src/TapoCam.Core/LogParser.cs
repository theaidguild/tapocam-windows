using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TapoCam.Core;

public static class LogParser
{
    public static string? RecentError(string? text, string host, DateTimeOffset localNow)
    {
        host = host.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(text) || host.Length == 0)
            return null;

        var addressFamily = IPAddress.TryParse(host, out var address) ? address.AddressFamily : AddressFamily.Unspecified;
        var nowSeconds = (int)localNow.TimeOfDay.TotalSeconds;
        string? lastError = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = CredentialMasker.Mask(rawLine.TrimStart());
            var errorIndex = line.IndexOf("error=", StringComparison.Ordinal);
            if (errorIndex < 0 ||
                !MatchesHost(line, host, addressFamily) ||
                line.Length < 8 ||
                !TimeOnly.TryParseExact(line.AsSpan(0, 8), "HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                continue;

            var age = (nowSeconds - (int)time.ToTimeSpan().TotalSeconds + 86_400) % 86_400;
            if (age <= 15)
                lastError = line[(errorIndex + "error=".Length)..].Trim(' ', '\t', '"', '\'').Trim();
        }

        return lastError;
    }

    public static string LastLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";

        var text = output.TrimEnd();
        return CredentialMasker.Mask(text[(text.LastIndexOfAny(['\r', '\n']) + 1)..].Trim());
    }

    private static bool MatchesHost(string line, string host, AddressFamily family)
    {
        var index = line.IndexOf(host, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var end = index + host.Length;
            if ((index == 0 || !IsHostContinuation(line[index - 1], family, before: true)) &&
                (end == line.Length || !IsHostContinuation(line[end], family, before: false)))
                return true;
            index = line.IndexOf(host, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool IsHostContinuation(char character, AddressFamily family, bool before) =>
        family switch
        {
            AddressFamily.InterNetwork => char.IsAsciiDigit(character) || (before && character == '.'),
            AddressFamily.InterNetworkV6 => char.IsAsciiHexDigit(character) || character is ':' or '%',
            _ => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
        };
}
