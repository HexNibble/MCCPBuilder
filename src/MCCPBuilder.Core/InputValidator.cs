using System.Text.RegularExpressions;

namespace MCCPBuilder.Core;

public static partial class InputValidator
{
    public const int MaximumLauncherTitleLength = 128;

    public static bool IsValidVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && VersionPattern().IsMatch(value);

    public static bool IsValidFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return false;
        }

        return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !value.EndsWith(' ') &&
               !value.EndsWith('.');
    }

    public static bool IsPathInside(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidMinecraftServerAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            return false;
        }

        string host;
        string? portText = null;
        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1)
            {
                return false;
            }

            host = value[1..closingBracket];
            var suffix = value[(closingBracket + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':'))
                {
                    return false;
                }

                portText = suffix[1..];
            }
        }
        else
        {
            var colonCount = value.Count(character => character == ':');
            if (colonCount > 1)
            {
                return false;
            }

            var separator = value.LastIndexOf(':');
            host = separator > 0 ? value[..separator] : value;
            portText = separator > 0 ? value[(separator + 1)..] : null;
        }

        if (string.IsNullOrWhiteSpace(host) ||
            host.Any(character =>
                !char.IsLetterOrDigit(character) &&
                character is not '.' and not '-' and not '_' and not ':' and not '%'))
        {
            return false;
        }

        return portText is null ||
               int.TryParse(portText, out var port) && port is >= 1 and <= 65535;
    }

    public static bool IsValidForgeMcpBrandingText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 48 &&
        value.All(character => !char.IsControl(character));

    public static bool IsValidOptionalGameWindowTitle(string? value) =>
        string.IsNullOrEmpty(value) ||
        value.Length <= GameWindowTitleService.MaximumTitleLength &&
        value.All(character => !char.IsControl(character));

    public static bool IsValidOptionalLauncherTitle(string? value) =>
        string.IsNullOrEmpty(value) ||
        value.Length <= MaximumLauncherTitleLength &&
        value.All(character => !char.IsControl(character));

    public static bool IsSupportedLauncherBackgroundImagePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        Path.GetExtension(value).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(value).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(value).Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(value).Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
