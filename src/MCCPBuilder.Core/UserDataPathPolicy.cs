namespace MCCPBuilder.Core;

public static class UserDataPathPolicy
{
    private static readonly string[] ProtectedDirectoryPrefixes =
    [
        ".minecraft/saves/",
        ".minecraft/screenshots/",
        ".minecraft/resourcepacks/",
        ".minecraft/shaderpacks/",
        ".minecraft/config/",
        ".minecraft/defaultconfigs/",
        ".minecraft/journeymap/",
        ".minecraft/xaerominimap/",
        ".minecraft/xaeroworldmap/",
        ".minecraft/xaerowaypoints/",
        ".minecraft/maps/",
        ".minecraft/schematics/"
    ];

    private static readonly HashSet<string> ProtectedFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".minecraft/options.txt",
            ".minecraft/optionsof.txt",
            ".minecraft/servers.dat",
            ".minecraft/servers.dat_old",
            ".minecraft/usercache.json",
            ".minecraft/launcher_profiles.json",
            ".minecraft/launcher_accounts.json",
            ".minecraft/launcher_msa_credentials.bin",
            ".minecraft/accounts.json"
        };

    public static bool IsProtected(string relativePath)
    {
        var normalized = (relativePath ?? "")
            .Replace('\\', '/')
            .TrimStart('/');
        if (ProtectedFiles.Contains(normalized))
        {
            return true;
        }

        return ProtectedDirectoryPrefixes.Any(prefix =>
            normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
