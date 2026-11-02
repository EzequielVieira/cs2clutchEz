using System.Numerics;
using System.Text.RegularExpressions;

namespace CS2HG.Core.Models;

/// <summary>
/// CS2 "match sharing code" (CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx). It packs the match id, the outcome
/// (reservation) id and a token (low 16 bits of the TV port) — enough for Valve's Game Coordinator to
/// return the demo download URL.
/// </summary>
public readonly partial record struct ShareCode(ulong MatchId, ulong OutcomeId, ushort Token)
{
    private const string Dictionary = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789";

    public static bool TryParse(string? text, out ShareCode code)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = CodeRegex().Match(text);
        if (!m.Success) return false;

        var chars = m.Value.Replace("CSGO", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        var big = BigInteger.Zero;
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var index = Dictionary.IndexOf(chars[i]);
            if (index < 0) return false;
            big = big * Dictionary.Length + index;
        }

        // 18 bytes, big endian; each field is stored little endian inside it.
        var bytes = big.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 18) return false;
        var buffer = new byte[18];
        bytes.CopyTo(buffer, 18 - bytes.Length);
        var matchId = BitConverter.ToUInt64(buffer, 0);
        var outcomeId = BitConverter.ToUInt64(buffer, 8);
        var token = BitConverter.ToUInt16(buffer, 16);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException();
        code = new ShareCode(matchId, outcomeId, token);
        return true;
    }

    public static ShareCode Parse(string text) =>
        TryParse(text, out var code) ? code : throw new FormatException(Localization.L.T("error.shareCodeInvalid"));

    /// <summary>Finds the first share code inside arbitrary text (e.g. a pasted steam:// link).</summary>
    public static string? Find(string? text) => text is null ? null : CodeRegex().Match(text) is { Success: true } m ? m.Value : null;

    public override string ToString()
    {
        var buffer = new byte[18];
        BitConverter.GetBytes(MatchId).CopyTo(buffer, 0);
        BitConverter.GetBytes(OutcomeId).CopyTo(buffer, 8);
        BitConverter.GetBytes(Token).CopyTo(buffer, 16);
        var big = new BigInteger(buffer, isUnsigned: true, isBigEndian: true);
        var chars = new char[25];
        for (var i = 0; i < 25; i++)
        {
            big = BigInteger.DivRem(big, Dictionary.Length, out var rem);
            chars[i] = Dictionary[(int)rem];
        }

        var s = new string(chars);
        return $"CSGO-{s[..5]}-{s[5..10]}-{s[10..15]}-{s[15..20]}-{s[20..]}";
    }

    [GeneratedRegex(@"CSGO(-[ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789]{5}){5}")]
    private static partial Regex CodeRegex();
}

/// <summary>A match as reported by the Game Coordinator (recent games or full game info).</summary>
public sealed class GcMatch
{
    public ulong MatchId { get; set; }
    public ulong OutcomeId { get; set; }
    public ushort Token { get; set; }
    public DateTime? MatchTime { get; set; }
    public string? DemoUrl { get; set; }
    public string? Map { get; set; }
    public int[] TeamScores { get; set; } = [];
    public int DurationSeconds { get; set; }

    public ShareCode ShareCode => new(MatchId, OutcomeId, Token);

    public string ScoreText => TeamScores.Length >= 2 ? $"{TeamScores[0]}:{TeamScores[1]}" : "";
}
