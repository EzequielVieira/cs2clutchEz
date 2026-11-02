using System.Diagnostics;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.DemoParser;
using CS2HG.DemoPlayback;
using CS2HG.HighlightDetection;
using CS2HG.Storage;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

public sealed record AnalysisStep(string Stage, double Fraction);

/// <summary>Status codes stored in the analyses table (translated by the UI).</summary>
public static class AnalysisStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

public sealed class OpenedDemo
{
    public required DemoRecord Record { get; init; }
    public required MatchData Match { get; init; }
    public bool FromCache { get; init; }
}

/// <summary>Import → parse (or cache) → statistics → highlight detection → persistence.</summary>
public sealed class AnalysisService(
    IDemoImporter importer,
    IDemoParser parser,
    IHighlightAnalyzer analyzer,
    MatchCache cache,
    DemoRepository demos,
    AnalysisRepository analyses,
    HighlightRepository highlights,
    SettingsStore settings,
    ILogger<AnalysisService> logger)
{
    public async Task<OpenedDemo> OpenDemoAsync(string path, IProgress<AnalysisStep>? progress, CancellationToken ct)
    {
        progress?.Report(new AnalysisStep(path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) ? L.T("analysis.stageDecompress") : L.T("analysis.stageVerify"), 0));
        var imported = await importer.ImportAsync(path, new Progress<double>(p => progress?.Report(new AnalysisStep(L.T("analysis.stageVerify"), p * 0.15))), ct);
        settings.AddRecentDemo(path);

        var match = await cache.LoadAsync(imported.Hash, ct);
        var fromCache = match is not null;
        if (match is null)
        {
            var sw = Stopwatch.StartNew();
            match = await parser.ParseAsync(imported.DemoPath, new Progress<double>(p => progress?.Report(new AnalysisStep(L.T("analysis.stageRead"), 0.15 + p * 0.8))), ct);
            match.Demo.FileHash = imported.Hash;
            MatchAnalysis.FillClutchStats(match);
            await cache.SaveAsync(match, ct);
            logger.LogInformation("Parsed {File} in {Ms} ms", imported.DemoPath, sw.ElapsedMilliseconds);
        }
        else
        {
            match.Demo.FilePath = imported.DemoPath;
            logger.LogInformation("Loaded {File} from cache", imported.DemoPath);
        }

        var record = new DemoRecord
        {
            Hash = imported.Hash,
            OriginalPath = imported.OriginalPath,
            DemoPath = imported.DemoPath,
            FileName = Path.GetFileName(imported.OriginalPath),
            Map = match.Demo.MapName,
            Size = imported.Size,
            Duration = match.Demo.DurationSeconds,
            TickRate = match.Demo.TickRate,
            Rounds = match.Rounds.Count,
            ScoreCt = match.FinalScoreCt,
            ScoreT = match.FinalScoreT,
            TeamCt = match.TeamNameCt,
            TeamT = match.TeamNameT,
            ImportedAt = DateTime.UtcNow.ToString("O"),
            Parser = parser.ParserName,
        };
        demos.Upsert(record);
        demos.SavePlayers(imported.Hash, match.Players);
        progress?.Report(new AnalysisStep(L.T("analysis.stageLoaded"), 1));
        return new OpenedDemo { Record = record, Match = match, FromCache = fromCache };
    }

    /// <summary>Runs every detector for one player (or all players when null) and stores the result.</summary>
    public IReadOnlyList<Highlight> DetectHighlights(OpenedDemo demo, ulong? playerSteamId)
    {
        var sw = Stopwatch.StartNew();
        var player = playerSteamId is { } id ? demo.Match.FindPlayer(id) : null;
        var record = new AnalysisRecord
        {
            DemoHash = demo.Record.Hash,
            Map = demo.Match.Demo.MapName,
            PlayerSteamId = playerSteamId?.ToString(),
            PlayerName = player?.Name ?? "",
            StartedAt = DateTime.UtcNow.ToString("O"),
            Status = AnalysisStatus.Running,
        };
        record.Id = analyses.Add(record);
        try
        {
            var s = settings.Settings;
            var result = analyzer.Analyze(demo.Match, playerSteamId, s.Detection, s.Scores);
            highlights.Replace(demo.Record.Hash, playerSteamId, result);
            record.HighlightCount = result.Count;
            record.Status = AnalysisStatus.Done;
            demo.Record.AnalyzedAt = DateTime.UtcNow.ToString("O");
            demos.Upsert(demo.Record);
            return result;
        }
        catch (Exception ex)
        {
            record.Status = AnalysisStatus.Failed;
            record.Error = ex.Message;
            throw;
        }
        finally
        {
            record.FinishedAt = DateTime.UtcNow.ToString("O");
            record.DurationMs = sw.ElapsedMilliseconds;
            analyses.Update(record);
        }
    }

    /// <summary>Default player: the logged-in Steam user when present in the demo, otherwise the top fragger.</summary>
    public static ulong? SuggestPlayer(MatchData match)
    {
        if (SteamLocator.ActiveSteamId64() is { } me && match.FindPlayer(me) is not null) return me;
        return match.Players.OrderByDescending(p => p.Stats.Kills).FirstOrDefault()?.SteamId;
    }

    public IReadOnlyList<DemoRecord> RecentDemos() => demos.GetRecent();

    public IReadOnlyList<AnalysisRecord> History() => analyses.GetRecent();

    public IReadOnlyList<Highlight> StoredHighlights(string hash, ulong? player) => highlights.Get(hash, player);
}
