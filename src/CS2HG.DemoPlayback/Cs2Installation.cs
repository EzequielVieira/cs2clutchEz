using System.Text.RegularExpressions;
using CS2HG.Core.Localization;
using Microsoft.Win32;

namespace CS2HG.DemoPlayback;

/// <summary>Paths of a Counter-Strike 2 installation and the active Steam user's config folder.</summary>
public sealed record Cs2Installation(
    string SteamFolder,
    string SteamExe,
    string GameFolder,
    string CsgoFolder,
    string Cs2Exe,
    string? UserCfgFolder)
{
    public string CfgFolder => Path.Combine(CsgoFolder, "cfg");
    public string HelperCfgFolder => Path.Combine(CfgFolder, "cs2hg");
    public string ConsoleLog => Path.Combine(CsgoFolder, "console.log");
    public string DemoFolder => Path.Combine(CsgoFolder, "cs2hg", "demos");
    public string? VideoConfig => UserCfgFolder is null ? null : Path.Combine(UserCfgFolder, "cs2_video.txt");
}

public sealed class Cs2NotFoundException(string message) : Exception(message);

public static partial class SteamLocator
{
    public const string Cs2AppId = "730";

    /// <summary>Finds Steam and CS2. Overrides (from settings) win when they point to valid folders.</summary>
    public static Cs2Installation Locate(string? steamOverride = null, string? gameOverride = null)
    {
        var steam = steamOverride;
        if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam))
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }

        if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam))
            throw new Cs2NotFoundException(L.T("cs2.steamNotFound"));

        steam = Path.GetFullPath(steam.Replace('/', '\\'));
        var steamExe = Path.Combine(steam, "steam.exe");

        string? game = null;
        if (!string.IsNullOrWhiteSpace(gameOverride))
        {
            var candidate = gameOverride.Replace('/', '\\');
            if (File.Exists(Path.Combine(candidate, "game", "bin", "win64", "cs2.exe"))) game = candidate;
        }

        if (game is null)
        {
            foreach (var lib in LibraryFolders(steam))
            {
                var candidate = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive");
                if (File.Exists(Path.Combine(candidate, "game", "bin", "win64", "cs2.exe")))
                {
                    game = candidate;
                    break;
                }
            }
        }

        if (game is null)
            throw new Cs2NotFoundException(L.T("cs2.gameNotFound"));

        var csgo = Path.Combine(game, "game", "csgo");
        var exe = Path.Combine(game, "game", "bin", "win64", "cs2.exe");
        return new Cs2Installation(steam, steamExe, game, csgo, exe, FindUserCfgFolder(steam));
    }

    public static IEnumerable<string> LibraryFolders(string steamFolder)
    {
        yield return steamFolder;
        var vdf = Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (Match m in PathRegex().Matches(File.ReadAllText(vdf)))
        {
            var path = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(path) && !string.Equals(Path.GetFullPath(path), steamFolder, StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    /// <summary>userdata/&lt;accountId&gt;/730/local/cfg of the logged in (or most recent) Steam user.</summary>
    public static string? FindUserCfgFolder(string steamFolder)
    {
        var userdata = Path.Combine(steamFolder, "userdata");
        if (!Directory.Exists(userdata)) return null;

        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int active && active != 0)
        {
            var dir = Path.Combine(userdata, ((uint)active).ToString(), Cs2AppId, "local", "cfg");
            if (Directory.Exists(dir)) return dir;
        }

        return Directory.EnumerateDirectories(userdata)
            .Select(d => Path.Combine(d, Cs2AppId, "local", "cfg"))
            .Where(Directory.Exists)
            .OrderByDescending(d => File.Exists(Path.Combine(d, "cs2_video.txt")) ? File.GetLastWriteTimeUtc(Path.Combine(d, "cs2_video.txt")) : DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>SteamID64 of the logged in Steam user, when Steam is running.</summary>
    public static ulong? ActiveSteamId64()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int active && active != 0)
            return 76561197960265728UL + (uint)active;
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
