using System.Diagnostics;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.Core.Utilities;
using CS2HG.VideoProcessing;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

/// <summary>
/// Demo → video: for each planned segment, CS2 seeks to the tick, the POV is set and verified, the window is
/// captured in real time while the demo plays, then segments are cut/aligned into a master clip and exported.
/// Captured segments (and then the master) are saved on the job immediately, so an interrupted job resumes where it
/// stopped. Once the MP4 is exported the job's work folder is deleted.
/// </summary>
public sealed class HighlightRenderPipeline(
    IDemoPlaybackController playback,
    MediaToolkit media,
    SettingsStore settings,
    ILogger<HighlightRenderPipeline> logger) : IRenderPipeline
{
    private AppSettings S => settings.Settings;

    public async Task RenderAsync(RenderJob job, Func<RenderJob, Task> saveState, IProgress<RenderProgress> progress, CancellationToken ct)
    {
        void Report(double fraction, string stage) => progress.Report(new RenderProgress(job.Id, Math.Clamp(fraction, 0, 1), stage));

        var processor = media.Processor;
        var workDir = Path.Combine(S.Paths.Work, job.Id);
        Directory.CreateDirectory(workDir);

        if (job.SourceMasterPath is not null && !File.Exists(job.SourceMasterPath))
        {
            // The master of an interrupted export is gone (raw captures were already deleted): capture again.
            logger.LogWarning("Master {Path} of job {Job} is missing; capturing again", job.SourceMasterPath, job.Id);
            job.SourceMasterPath = null;
            job.Captured.Clear();
        }

        string masterPath;
        if (job.SourceMasterPath is not null)
        {
            masterPath = job.SourceMasterPath;
        }
        else
        {
            if (!File.Exists(job.DemoPath)) throw new FileNotFoundException(L.T("render.demoMissing"), job.DemoPath);
            if (job.Segments.Count == 0) throw new InvalidOperationException(L.T("render.noSegments"));
            await CaptureSegmentsAsync(job, saveState, workDir, Report, ct);

            Report(0.86, L.T("render.buildingMaster"));
            masterPath = Path.Combine(workDir, "master.mp4");
            var inputs = job.Captured.OrderBy(c => c.Index).Select(c => new SegmentInput(
                c.VideoPath, c.VideoOffsetSeconds, c.AudioPath, c.AudioOffsetSeconds, c.DurationSeconds)).ToList();
            await processor.BuildMasterAsync(inputs, masterPath, job.Edit.SegmentTransitionSeconds,
                new Progress<double>(p => Report(0.86 + p * 0.05, L.T("render.buildingMaster"))), ct);
            job.SourceMasterPath = masterPath;
            await saveState(job);

            // Raw captures are no longer needed once the master exists.
            foreach (var c in job.Captured)
            {
                TryDelete(c.VideoPath);
                if (c.AudioPath is not null) TryDelete(c.AudioPath);
            }
        }

        Report(0.91, L.T("render.exporting"));
        var output = job.OutputPath ??= BuildOutputPath(job);
        await processor.ExportAsync(masterPath, output, job.Export, job.Edit,
            new Progress<double>(p => Report(0.91 + p * 0.07, L.T("render.exporting"))), ct);

        Report(0.985, L.T("render.thumbnail"));
        try
        {
            var thumb = Path.Combine(S.Paths.Thumbnails, job.Id + ".jpg");
            await processor.CreateThumbnailAsync(output, ThumbnailTime(job), thumb, ct);
            job.ThumbnailPath = thumb;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Thumbnail failed for {Job}", job.Id);
        }

        // The final MP4 is all that is kept; the master and any leftovers go away with the work folder.
        job.SourceMasterPath = null;
        TryDeleteDirectory(workDir);
        await saveState(job);
        Report(1, L.T("stage.done"));
    }

    private async Task CaptureSegmentsAsync(RenderJob job, Func<RenderJob, Task> saveState, string workDir,
        Action<double, string> report, CancellationToken ct)
    {
        var pending = job.Segments.Where(s => job.Captured.All(c => c.Index != s.Index)).OrderBy(s => s.Index).ToList();
        if (pending.Count == 0) return;

        var tickRate = job.TickRate <= 0 ? 64 : job.TickRate;
        var totalSeconds = job.Segments.Sum(s => s.DurationSeconds(tickRate));
        double DoneSeconds() => job.Captured.Sum(c => c.DurationSeconds);
        double Fraction(double extra) => 0.15 + 0.7 * Math.Min(1, (DoneSeconds() + extra) / Math.Max(1, totalSeconds));

        report(0.02, L.T("render.preparingCs2"));
        await playback.EnsureSessionAsync(new Progress<string>(m => report(0.04, m)), ct);
        report(0.10, L.T("render.loadingDemo"));
        await playback.LoadDemoAsync(job.DemoPath, ct);
        await playback.ApplyHudAsync(job.Export.Hud, ct);

        var encoder = await media.Encoders.BestAsync(VideoCodec.H264, ct);
        foreach (var segment in pending)
        {
            ct.ThrowIfCancellationRequested();
            var label = L.F("render.segment", segment.Index + 1, job.Segments.Count);
            report(Fraction(0), L.F("render.seeking", label, segment.StartTick));
            await playback.SeekAsync(segment.StartTick, ct);

            var (verified, observed) = await playback.SetPovAsync(new PovTarget(segment.PovSteamId, segment.PovName, job.Export.Pov), ct);
            if (!verified) logger.LogWarning("POV not verified for {Job} segment {Index}: wanted {Wanted}, got {Observed}", job.Id, segment.Index, segment.PovName, observed);

            var video = Path.Combine(workDir, $"seg{segment.Index:00}.mp4");
            var audio = job.Export.RecordAudio ? Path.Combine(workDir, $"seg{segment.Index:00}.wav") : null;
            var duration = segment.DurationSeconds(tickRate);
            var latency = S.Cs2.InputLatencyMs / 1000.0;

            report(Fraction(0), L.F("render.recordingStart", label, duration));
            await using var capture = await media.Recorder.StartAsync(
                new CaptureOptions(playback.GameWindow, playback.GameProcessId, S.Cs2.CaptureFps, video, audio, encoder), ct);
            await capture.WaitUntilRecordingAsync(TimeSpan.FromSeconds(15), ct);
            await Task.Delay(250, ct);

            var resumedAt = await playback.ResumeAsync(ct);
            var sw = Stopwatch.StartNew();
            // Playback runs in real time; keep reporting while it plays.
            while (sw.Elapsed.TotalSeconds < duration)
            {
                var left = duration - sw.Elapsed.TotalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.25, Math.Max(0.01, left))), ct);
                report(Fraction(Math.Min(duration, sw.Elapsed.TotalSeconds)), L.F("render.recording", label, sw.Elapsed.TotalSeconds, duration));
            }

            await playback.PauseAsync(ct);
            await Task.Delay(300, ct);
            await capture.StopAsync(ct);

            var freq = (double)Stopwatch.Frequency;
            var videoOffset = (resumedAt - capture.VideoOrigin) / freq + latency;
            var audioOffset = capture.AudioOrigin is { } ao ? (resumedAt - ao) / freq + latency : 0;
            job.Captured.Add(new CapturedSegment
            {
                Index = segment.Index,
                VideoPath = video,
                AudioPath = capture.AudioPath is not null && File.Exists(capture.AudioPath) ? capture.AudioPath : null,
                VideoOffsetSeconds = Math.Max(0, videoOffset),
                AudioOffsetSeconds = audioOffset,
                DurationSeconds = duration,
                PovVerified = verified,
                PovObserved = observed,
            });
            logger.LogInformation("Captured {Job} segment {Index}: {Duration:0.0}s, video offset {VOff:0.000}s, audio offset {AOff:0.000}s, POV {Pov} verified={Verified}",
                job.Id, segment.Index, duration, videoOffset, audioOffset, observed, verified);
            await saveState(job);
        }
    }

    public async Task OnQueueIdleAsync(CancellationToken cancellationToken)
    {
        if (!S.Cs2.CloseGameWhenDone) return;
        if (playback.State.GameRunning)
        {
            logger.LogInformation("Queue idle: closing CS2 and restoring the user's configuration");
            await playback.EndSessionAsync(closeGame: true, cancellationToken);
        }
    }

    private string BuildOutputPath(RenderJob job)
    {
        var root = string.IsNullOrWhiteSpace(job.Export.OutputFolder) ? S.Paths.Highlights : job.Export.OutputFolder!;
        var folder = Path.Combine(root, FileNames.Sanitize(job.PlayerName), FileNames.Sanitize(job.MatchName, 80), CategoryFolder(job.Category));
        Directory.CreateDirectory(folder);
        var baseName = FileNames.Sanitize($"R{job.Round:00} - {job.Title} - {job.Score}pts", 100);
        var path = Path.Combine(folder, baseName + ".mp4");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName} ({i}).mp4");
        return path;
    }

    public static string CategoryFolder(HighlightCategory category) => category switch
    {
        HighlightCategory.Ace => "Ace",
        HighlightCategory.Clutch => "Clutch",
        HighlightCategory.MultiKill => "MultiKill",
        HighlightCategory.Objective => "Objective",
        HighlightCategory.Utility => "Utility",
        _ => "Kills",
    };

    private static double ThumbnailTime(RenderJob job)
    {
        var tickRate = job.TickRate <= 0 ? 64 : job.TickRate;
        var first = job.Segments.OrderBy(s => s.Index).FirstOrDefault();
        if (first is null) return 1;
        // Key moment of the first segment ≈ its last second of pre-roll ends.
        var t = job.Export.SecondsBefore + 0.2 - job.Edit.TrimStartSeconds;
        return Math.Clamp(t, 0.5, Math.Max(0.5, first.DurationSeconds(tickRate) - 0.5));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete work folder {Path}", path);
        }
    }
}
