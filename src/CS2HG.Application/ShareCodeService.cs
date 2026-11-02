using System.Net;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.DemoPlayback;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

/// <summary>
/// "Copia e cola" de partidas: share code → demo. Uses a demo already on disk when possible
/// (CS2 replays folder or previous downloads); otherwise asks the Game Coordinator for the
/// download URL and fetches the .dem.bz2 from Valve's replay servers.
/// </summary>
public sealed class ShareCodeService(SettingsStore settings, Cs2PlaybackController playback, AnalysisService analysis, ILogger<ShareCodeService> logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private string Cs2BinFolder => Path.Combine(playback.Installation.GameFolder, "game", "bin", "win64");

    /// <summary>
    /// Matches CS2 already downloaded (Watch → Your Matches): each replays/match730_*.dem.info holds the
    /// match info protobuf, from which the share code is rebuilt. Works offline.
    /// </summary>
    public IReadOnlyList<(GcMatch Match, string? DemoPath)> LocalMatches()
    {
        var result = new List<(GcMatch, string?)>();
        string replays;
        try
        {
            replays = Path.Combine(playback.Installation.CsgoFolder, "replays");
        }
        catch (Cs2NotFoundException)
        {
            return result;
        }

        if (!Directory.Exists(replays)) return result;
        foreach (var info in Directory.EnumerateFiles(replays, "match730_*.dem.info").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var match = MatchListParser.ParseMatch(File.ReadAllBytes(info));
                if (match.MatchId == 0) continue;
                var demo = info[..^".info".Length];
                result.Add((match, File.Exists(demo) ? demo : null));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Unreadable {Info}", info);
            }
        }

        return result;
    }

    public async Task<GcMatch> ResolveAsync(ShareCode code, CancellationToken ct)
    {
        var match = await GameCoordinatorClient.RunAsync(Cs2BinFolder, logger, (gc, c) => gc.FullGameInfo(code, c), ct);
        if (match?.DemoUrl is null)
            throw new GameCoordinatorException(L.T("sharecode.noDemo"));
        return match;
    }

    /// <summary>
    /// Demo for this match already on disk (CS2 replays folder or app downloads), if any.
    /// Valve names replay files after the outcome (reservation) id: match730_&lt;outcomeId:21&gt;_&lt;n&gt;_&lt;server&gt;.dem.
    /// </summary>
    public string? FindLocalDemo(ShareCode code)
    {
        var prefix = $"match730_{code.OutcomeId:D21}";
        var folders = new List<string> { settings.Settings.Paths.Demos };
        try
        {
            folders.Add(Path.Combine(playback.Installation.CsgoFolder, "replays"));
        }
        catch (Cs2NotFoundException)
        {
            // no CS2 installed
        }

        foreach (var folder in folders.Where(Directory.Exists))
        {
            var hit = Directory.EnumerateFiles(folder, prefix + "*")
                .Where(f => f.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => new FileInfo(f).Length > 0);
            if (hit is not null) return hit;
        }

        return null;
    }

    public async Task<string> DownloadAsync(GcMatch match, IProgress<double>? progress, CancellationToken ct)
    {
        var url = match.DemoUrl ?? throw new InvalidOperationException(L.T("sharecode.noDemo"));
        Directory.CreateDirectory(settings.Settings.Paths.Demos);
        var target = Path.Combine(settings.Settings.Paths.Demos, "match730_" + Path.GetFileName(new Uri(url).LocalPath));
        if (File.Exists(target) && new FileInfo(target).Length > 0) return target;

        logger.LogInformation("Downloading demo {Url}", url);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GameCoordinatorException(L.T("sharecode.expired"));
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        var part = target + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(part))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        File.Move(part, target, true);
        return target;
    }

    /// <summary>Share code (or any text containing one) → analysed demo.</summary>
    public async Task<OpenedDemo> OpenAsync(string text, IProgress<AnalysisStep>? progress, CancellationToken ct, bool forceDownload = false)
    {
        var code = ShareCode.Parse(ShareCode.Find(text) ?? text.Trim());
        progress?.Report(new AnalysisStep(L.T("sharecode.searching"), 0));
        var path = forceDownload ? null : FindLocalDemo(code);
        if (path is null)
        {
            progress?.Report(new AnalysisStep(L.T("sharecode.querying"), 0.02));
            var match = await ResolveAsync(code, ct);
            progress?.Report(new AnalysisStep(L.T("sharecode.downloading"), 0.05));
            path = await DownloadAsync(match, new Progress<double>(p => progress?.Report(new AnalysisStep(L.F("sharecode.downloadingPct", p), 0.05 + p * 0.15))), ct);
        }
        else
        {
            logger.LogInformation("Share code {Code} matched local demo {Path}", code, path);
        }

        return await analysis.OpenDemoAsync(path, progress, ct);
    }
}
