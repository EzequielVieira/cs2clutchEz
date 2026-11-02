using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

/// <param name="Queued">New render jobs.</param>
/// <param name="Duplicates">Plays already rendered or queued with the same settings.</param>
public sealed record AutoResult(string DemoPath, string? PlayerName, int Found, int Queued, int Duplicates, string? Error);

/// <summary>Auto mode: analyses several demos and queues the best plays of each one in a single step.</summary>
public sealed class AutoModeService(AnalysisService analysis, RenderService render, ILogger<AutoModeService> logger)
{
    public static IReadOnlyList<Highlight> PickTop(IReadOnlyList<Highlight> highlights, int count) =>
        highlights.OrderByDescending(h => h.Score).ThenBy(h => h.Round).Take(Math.Max(1, count)).ToList();

    /// <summary>
    /// Opens the demo, picks the player (the signed-in Steam user, else the top fragger), detects the highlights and
    /// queues the <paramref name="top"/> best with <paramref name="export"/>.
    /// </summary>
    public async Task<AutoResult> QueueBestAsync(string demoPath, int top, ExportSettings export, IProgress<AnalysisStep>? progress, CancellationToken ct)
    {
        try
        {
            var demo = await analysis.OpenDemoAsync(demoPath, progress, ct);
            if (AnalysisService.SuggestPlayer(demo.Match) is not { } player) return new AutoResult(demoPath, null, 0, 0, 0, null);
            var name = demo.Match.FindPlayer(player)?.Name;
            var highlights = analysis.DetectHighlights(demo, player);
            if (highlights.Count == 0) return new AutoResult(demoPath, name, 0, 0, 0, null);

            // A specific player only makes sense inside one match.
            var settings = export.Clone();
            if (settings.Pov == PovMode.SpecificPlayer)
            {
                settings.Pov = PovMode.SelectedPlayer;
                settings.SpecificPlayerSteamId = null;
            }

            var results = await render.EnqueueAsync(PickTop(highlights, top), demo.Match, settings);
            var queued = results.Count(r => !r.IsDuplicate);
            logger.LogInformation("Auto mode: {Demo} → {Player}: {Queued} queued, {Dup} duplicate(s)", demoPath, name, queued, results.Count - queued);
            return new AutoResult(demoPath, name, highlights.Count, queued, results.Count - queued, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auto mode failed for {Demo}", demoPath);
            return new AutoResult(demoPath, null, 0, 0, 0, ex.Message);
        }
    }
}
