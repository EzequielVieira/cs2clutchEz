using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.HighlightDetection;
using CS2HG.RenderingQueue;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

/// <summary>Turns selected highlights into render jobs and builds montages from rendered clips.</summary>
public sealed class RenderService(RenderQueue queue, SettingsStore settings, MediaToolkit media, ILogger<RenderService> logger)
{
    public RenderQueue Queue => queue;

    public static string MatchName(MatchData match)
    {
        var date = File.Exists(match.Demo.FilePath) ? File.GetLastWriteTime(match.Demo.FilePath).ToString("yyyy-MM-dd") : "";
        return $"{match.Demo.MapName} {date} {match.FinalScoreCt}-{match.FinalScoreT}".Trim();
    }

    public RenderJob CreateJob(Highlight highlight, MatchData match, ExportSettings export, EditSettings? edit = null)
    {
        var segments = ClipPlanner.Plan(highlight, match, export, settings.Settings.Detection);
        var job = new RenderJob
        {
            HighlightId = highlight.Id,
            DemoHash = match.Demo.FileHash,
            DemoPath = match.Demo.FilePath,
            MatchName = MatchName(match),
            PlayerName = highlight.PlayerName,
            PlayerSteamId = highlight.PlayerSteamId,
            Round = highlight.Round,
            Title = highlight.Title,
            Category = highlight.Category,
            Score = highlight.Score,
            TickRate = match.Demo.TickRate,
            Export = export.Clone(),
            Segments = segments,
            KillTicks = highlight.Kills.Where(k => k.AttackerSteamId == highlight.PlayerSteamId).Select(k => k.Tick).Order().ToList(),
            Edit = edit ?? DefaultEdit(highlight, export),
            Stage = L.T("stage.queued"),
        };
        return job;
    }

    public static EditSettings DefaultEdit(Highlight highlight, ExportSettings export) => new()
    {
        ShowOverlay = export.AddTitleOverlay,
        Title = export.AddTitleOverlay ? highlight.Title : null,
        PlayerLabel = export.AddTitleOverlay ? highlight.PlayerName : null,
        RoundLabel = export.AddTitleOverlay ? L.F("overlay.round", highlight.Round) : null,
        FadeIn = export.FadeTransitions,
        FadeOut = export.FadeTransitions,
        SegmentTransitionSeconds = export.FadeTransitions ? 0.3 : 0,
    };

    public async Task<IReadOnlyList<EnqueueResult>> EnqueueAsync(IEnumerable<Highlight> highlights, MatchData match, ExportSettings export, bool force = false)
    {
        if (export.Codec == VideoCodec.H265 && media.IsAvailable && !await media.Encoders.SupportsH265Async(CancellationToken.None))
            throw new InvalidOperationException(L.T("error.h265Unsupported"));

        var results = new List<EnqueueResult>();
        foreach (var h in highlights.OrderBy(h => h.Round))
        {
            var job = CreateJob(h, match, export);
            results.Add(await queue.EnqueueAsync(job, force));
        }

        logger.LogInformation("Enqueued {New} job(s), {Dup} duplicate(s)", results.Count(r => !r.IsDuplicate), results.Count(r => r.IsDuplicate));
        queue.Start();
        return results;
    }

    /// <summary>Joins rendered clips into a single montage (fragmovie).</summary>
    public async Task<string> BuildMontageAsync(IReadOnlyList<RenderJob> jobs, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var clips = jobs.Where(j => j.OutputPath is not null && File.Exists(j.OutputPath)).Select(j => j.OutputPath!).ToList();
        if (clips.Count < 2) throw new InvalidOperationException(L.T("error.montageTwoClips"));
        await media.Processor.BuildMontageAsync(clips, outputPath, 0.4, progress, ct, settings.Settings.MontageMusicPath, settings.Settings.MontageMusicVolume);
        return outputPath;
    }

    public static string Describe(Highlight h, MatchData match, ExportSettings export, DetectionSettings detection)
    {
        var segments = ClipPlanner.Plan(h, match, export, detection);
        var seconds = ClipPlanner.TotalSeconds(segments, match.Demo.TickRate);
        return L.F("render.describe", segments.Count, seconds);
    }

    public static IEnumerable<string> ComponentLines(Highlight h) =>
        h.Components.GroupBy(c => c.Kind).Select(g => $"{g.Key.DisplayName()} ×{g.Count()} (+{g.Sum(c => c.Score)})");

    public static string WeaponsText(Highlight h) => string.Join(", ", h.Weapons.Select(WeaponInfo.DisplayName));
}
