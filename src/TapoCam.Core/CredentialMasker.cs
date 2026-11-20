using System.Text.RegularExpressions;

namespace TapoCam.Core;

public static partial class CredentialMasker
{
    [GeneratedRegex(@"(rtsps?://)[^@\s/""]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    public static string Mask(string? text) =>
        text is null
            ? ""
            : CredentialPattern().Replace(text, "$1***@");
}
