using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoPlayback;

/// <summary>
/// Writes the helper cfg files the app needs inside CS2 and protects the user's own configuration:
/// video/convar/key files are backed up before a render session and restored after CS2 closes.
/// </summary>
public sealed partial class Cs2ConfigManager(Cs2Installation install, string backupFolder, int gsiPort, ILogger logger)
{
    /// <summary>Key bound to "exec cs2hg/cmd". F13 exists on virtually no keyboard, so it never clashes.</summary>
    public const string CommandKeyName = "F13";
    public const ushort CommandKeyScanCode = 0x64;
    public const ushort ConsoleKeyScanCode = 0x29;
    public const string GsiFileName = "gamestate_integration_cs2hg.cfg";

    private static readonly string[] GuardedFiles =
    [
        "cs2_video.txt",
        "cs2_machine_convars.vcfg",
        "cs2_user_convars_0_slot0.vcfg",
        "cs2_user_keys_0_slot0.vcfg",
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private string PendingMarker => Path.Combine(backupFolder, "restore-pending.txt");

    public string CommandCfgPath => Path.Combine(install.HelperCfgFolder, "cmd.cfg");

    public void InstallHelperFiles()
    {
        Directory.CreateDirectory(install.HelperCfgFolder);
        Directory.CreateDirectory(install.DemoFolder);
        File.WriteAllText(Path.Combine(install.HelperCfgFolder, "boot.cfg"),
            $"""
             // cs2clutchEz - executed at launch (+exec cs2hg/boot)
             con_enable 1
             bind {CommandKeyName} "exec cs2hg/cmd"
             echo "CS2HG boot"
             """, Utf8NoBom);
        File.WriteAllText(CommandCfgPath, "echo \"CS2HG idle\"\n", Utf8NoBom);

        // Game State Integration: CS2 posts the spectated player to our local listener.
        File.WriteAllText(Path.Combine(install.CfgFolder, GsiFileName),
            $$"""
              "cs2clutchEz"
              {
                "uri" "http://127.0.0.1:{{gsiPort}}/"
                "timeout" "1.0"
                "buffer" "0.0"
                "throttle" "0.0"
                "heartbeat" "2.0"
                "data"
                {
                  "provider" "1"
                  "map" "1"
                  "round" "1"
                  "player_id" "1"
                  "player_state" "1"
                  "allplayers_id" "1"
                }
              }
              """, Utf8NoBom);
    }

    public void RemoveHelperFiles()
    {
        TryDelete(Path.Combine(install.CfgFolder, GsiFileName));
        try
        {
            if (Directory.Exists(install.HelperCfgFolder)) Directory.Delete(install.HelperCfgFolder, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove helper cfg folder");
        }
    }

    public void WriteCommands(IEnumerable<string> commands) =>
        File.WriteAllText(CommandCfgPath, string.Join('\n', commands) + "\n", Utf8NoBom);

    /// <summary>Backs up the user's CS2 config files (only when CS2 is closed).</summary>
    public void BackupUserConfig()
    {
        if (install.UserCfgFolder is null) return;
        if (File.Exists(PendingMarker))
        {
            logger.LogInformation("Previous backup still pending restore; keeping it (it holds the user's original files)");
            return;
        }

        Directory.CreateDirectory(backupFolder);
        foreach (var name in GuardedFiles)
        {
            var src = Path.Combine(install.UserCfgFolder, name);
            var dst = Path.Combine(backupFolder, name);
            if (File.Exists(src)) File.Copy(src, dst, true);
            else TryDelete(dst);
        }

        File.WriteAllText(PendingMarker, install.UserCfgFolder);
        logger.LogInformation("Backed up CS2 user config from {Folder}", install.UserCfgFolder);
    }

    /// <summary>Restores the backup. Must run with CS2 closed, otherwise the game overwrites it on exit.</summary>
    public bool RestoreUserConfig()
    {
        if (!File.Exists(PendingMarker)) return false;
        var folder = File.ReadAllText(PendingMarker).Trim();
        if (!Directory.Exists(folder)) folder = install.UserCfgFolder ?? folder;
        foreach (var name in GuardedFiles)
        {
            var src = Path.Combine(backupFolder, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(folder, name), true);
        }

        TryDelete(PendingMarker);
        logger.LogInformation("Restored CS2 user config to {Folder}", folder);
        return true;
    }

    public bool HasPendingRestore => File.Exists(PendingMarker);

    /// <summary>Sets windowed (borderless) mode and the capture resolution in cs2_video.txt.</summary>
    public void ApplyCaptureVideoSettings(int width, int height)
    {
        var path = install.VideoConfig;
        if (path is null || !File.Exists(path))
        {
            logger.LogWarning("cs2_video.txt not found; CS2 will start with its current video settings");
            return;
        }

        var text = File.ReadAllText(path);
        text = SetSetting(text, "setting.defaultres", width.ToString());
        text = SetSetting(text, "setting.defaultresheight", height.ToString());
        text = SetSetting(text, "setting.fullscreen", "0");
        text = SetSetting(text, "setting.nowindowborder", "1");
        text = SetSetting(text, "setting.fullscreen_min_on_focus_loss", "0");
        File.WriteAllText(path, text, Utf8NoBom);
    }

    private static string SetSetting(string text, string key, string value)
    {
        var regex = new Regex("\"" + Regex.Escape(key) + "\"\\s+\"[^\"]*\"");
        var replacement = $"\"{key}\"\t\t\"{value}\"";
        if (regex.IsMatch(text)) return regex.Replace(text, replacement, 1);
        var idx = text.LastIndexOf('}');
        return idx < 0 ? text : text.Insert(idx, "\t" + replacement + "\n");
    }

    /// <summary>Copies (hard link when possible) a demo under csgo/ so "playdemo" can open it by relative path.</summary>
    public string StageDemo(string demoPath, string slug)
    {
        Directory.CreateDirectory(install.DemoFolder);
        var target = Path.Combine(install.DemoFolder, slug + ".dem");
        var source = new FileInfo(demoPath);
        if (File.Exists(target) && new FileInfo(target).Length == source.Length) return target;
        TryDelete(target);
        if (!Native.CreateHardLink(target, source.FullName, IntPtr.Zero))
        {
            File.Copy(source.FullName, target, true);
        }

        return target;
    }

    public string RelativeDemoPath(string stagedPath)
    {
        var rel = Path.GetRelativePath(install.CsgoFolder, stagedPath).Replace('\\', '/');
        return rel.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ? rel[..^4] : rel;
    }

    public void CleanStagedDemos()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(install.DemoFolder, "*.dem")) TryDelete(f);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
