using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2HG.Core.Models;

namespace CS2HG.Core.Utilities;

public static class FileNames
{
    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars(), '%', '#', '&', '{', '}', '$', '!', '\'', '"', '@', '`', '='];

    /// <summary>Makes a string safe for Windows file and folder names.</summary>
    public static string Sanitize(string value, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormC))
        {
            if (Invalid.Contains(ch) || char.IsControl(ch)) sb.Append('_');
            else sb.Append(ch);
        }

        var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ', '_');
        if (result.Length == 0) result = "unknown";
        return result.Length > maxLength ? result[..maxLength].TrimEnd('.', ' ') : result;
    }

    /// <summary>ASCII-only name, used for files CS2 must open from its console.</summary>
    public static string AsciiSlug(string value, int maxLength = 48)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }

        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "demo";
        return s.Length > maxLength ? s[..maxLength] : s;
    }
}

public static class Hashing
{
    public static string Sha1Hex(string text) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    public static string HighlightId(string demoHash, ulong steamId, int round) =>
        Sha1Hex($"{demoHash}|{steamId}|{round}")[..16];

    /// <summary>Key identifying "this highlight rendered with these settings".</summary>
    public static string RenderDedupeKey(RenderJob job)
    {
        var payload = JsonSerializer.Serialize(new
        {
            job.HighlightId,
            job.SourceMasterPath,
            job.Export.SecondsBefore,
            job.Export.SecondsAfter,
            job.Export.Width,
            job.Export.Height,
            job.Export.Fps,
            job.Export.Codec,
            job.Export.BitrateKbps,
            job.Export.Pov,
            job.Export.SpecificPlayerSteamId,
            job.Export.Hud,
            job.Export.RecordAudio,
            job.Export.AddTitleOverlay,
            job.Export.FadeTransitions,
            Edit = job.Edit,
        }, Json.Options);
        return Sha1Hex(payload);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public static class TimeFormat
{
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}
