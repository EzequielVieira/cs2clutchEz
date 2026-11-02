using System.Globalization;
using System.Text;
using System.Text.Json;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CS2HG.VideoProcessing;

public sealed class FfmpegVideoProcessor(FfmpegRunner runner, EncoderCatalog encoders, EffectRegistry effects, ILogger<FfmpegVideoProcessor> logger)
    : IVideoProcessor
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string F(double v) => FfmpegRunner.F(v);

    public FfmpegRunner Runner => runner;

    public EncoderCatalog Encoders => encoders;

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var json = await runner.CaptureOutputAsync(runner.Paths.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        int width = 0, height = 0;
        double fps = 0;
        var codec = "";
        var hasAudio = false;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            if (type == "audio") hasAudio = true;
            if (type != "video" || codec.Length > 0) continue;
            codec = s.GetProperty("codec_name").GetString() ?? "";
            width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            if (s.TryGetProperty("avg_frame_rate", out var fr) && fr.GetString() is { } rate && rate.Contains('/'))
            {
                var parts = rate.Split('/');
                if (double.TryParse(parts[0], CultureInfo.InvariantCulture, out var num) &&
                    double.TryParse(parts[1], CultureInfo.InvariantCulture, out var den) && den > 0)
                    fps = num / den;
            }
        }

        return new MediaInfo(duration, width, height, fps, codec, hasAudio);
    }

    /// <summary>Cuts the useful part of each raw capture, aligns audio and joins segments (optional crossfade).</summary>
    public async Task BuildMasterAsync(IReadOnlyList<SegmentInput> segments, string outputPath, double transitionSeconds,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (segments.Count == 0) throw new ArgumentException("Nenhum segmento capturado.", nameof(segments));
        var first = await ProbeAsync(segments[0].VideoPath, cancellationToken);
        var width = Even(first.Width > 0 ? first.Width : 1920);
        var height = Even(first.Height > 0 ? first.Height : 1080);
        var fps = first.Fps is > 1 and < 400 ? Math.Round(first.Fps) : 60;

        var args = new List<string>();
        var filters = new List<string>();
        var inputIndex = 0;
        var videoLabels = new List<string>();
        var audioLabels = new List<string>();
        int? silenceInput = null;
        var silenceUses = segments.Count(s => s.AudioPath is null);

        var transition = MasterTimeline.EffectiveTransition(segments.Select(s => s.Duration).ToList(), transitionSeconds);

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            args.AddRange(["-i", seg.VideoPath]);
            var v = inputIndex++;
            filters.Add($"[{v}:v]trim=start={F(Math.Max(0, seg.VideoStart))}:duration={F(seg.Duration)},setpts=PTS-STARTPTS," +
                        $"fps={F(fps)},scale={width}:{height}:flags=lanczos,setsar=1,format=yuv420p,settb=AVTB[v{i}]");
            videoLabels.Add($"[v{i}]");

            if (seg.AudioPath is not null)
            {
                args.AddRange(["-i", seg.AudioPath]);
                var a = inputIndex++;
                var start = seg.AudioStart;
                var pre = start < 0 ? $"adelay=delays={(int)(-start * 1000)}:all=1," : "";
                filters.Add($"[{a}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,{pre}" +
                            $"atrim=start={F(Math.Max(0, start))}:duration={F(seg.Duration)},asetpts=PTS-STARTPTS," +
                            $"apad=whole_dur={F(seg.Duration)}{Mute(seg.MuteWindows)}[a{i}]");
            }
            else
            {
                if (silenceInput is null)
                {
                    args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
                    silenceInput = inputIndex++;
                    if (silenceUses > 1)
                        filters.Add($"[{silenceInput}:a]asplit={silenceUses}" + string.Concat(Enumerable.Range(0, silenceUses).Select(k => $"[sil{k}]")));
                }

                var src = silenceUses > 1 ? $"[sil{segments.Take(i).Count(s => s.AudioPath is null)}]" : $"[{silenceInput}:a]";
                filters.Add($"{src}aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=duration={F(seg.Duration)},asetpts=PTS-STARTPTS[a{i}]");
            }

            audioLabels.Add($"[a{i}]");
        }

        double total;
        if (segments.Count == 1)
        {
            filters.Add("[v0]null[vout]");
            filters.Add("[a0]anull[aout]");
            total = segments[0].Duration;
        }
        else if (transition > 0)
        {
            var vPrev = "[v0]";
            var aPrev = "[a0]";
            var elapsed = segments[0].Duration;
            for (var i = 1; i < segments.Count; i++)
            {
                var vOut = i == segments.Count - 1 ? "[vout]" : $"[vx{i}]";
                var aOut = i == segments.Count - 1 ? "[aout]" : $"[ax{i}]";
                var offset = elapsed - transition;
                filters.Add($"{vPrev}[v{i}]xfade=transition=fade:duration={F(transition)}:offset={F(offset)}{vOut}");
                filters.Add($"{aPrev}[a{i}]acrossfade=d={F(transition)}{aOut}");
                elapsed = offset + segments[i].Duration;
                vPrev = vOut;
                aPrev = aOut;
            }

            total = elapsed;
        }
        else
        {
            filters.Add(string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[v{i}][a{i}]")) +
                        $"concat=n={segments.Count}:v=1:a=1[vout][aout]");
            total = segments.Sum(s => s.Duration);
        }

        var encoder = await encoders.BestAsync(VideoCodec.H264, cancellationToken);
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(EncoderCatalog.IntermediateArgs(encoder));
        args.AddRange(["-pix_fmt", "yuv420p", "-r", F(fps), "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart", "-t", F(total), outputPath]);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        logger.LogInformation("Building master {Output} from {Count} segment(s), {Duration:0.0}s", outputPath, segments.Count, total);
        await runner.RunAsync(args, total, progress, cancellationToken);
    }

    /// <summary>Output frame size: the export resolution, or its 9:16 counterpart (1080p → 1080×1920).</summary>
    public static (int Width, int Height) OutputSize(ExportSettings export) => export.Layout == VideoLayout.Vertical
        ? (Even(export.Height), Even((int)Math.Round(export.Height * 16 / 9.0)))
        : (Even(export.Width), Even(export.Height));

    public async Task ExportAsync(string masterPath, string outputPath, ExportSettings export, EditSettings edit,
        IProgress<double>? progress, CancellationToken cancellationToken, ClipTimeline? timeline = null)
    {
        timeline ??= ClipTimeline.Empty;
        var info = await ProbeAsync(masterPath, cancellationToken);
        if (export.SlowMotionKills && timeline.KillTimes.Count > 0)
        {
            edit = edit.Clone();
            edit.SpeedRanges.AddRange(EditPlanner.KillSlowMotion(timeline.KillTimes, timeline.FastWindows));
        }

        var pieces = EditPlanner.BuildPieces(info.DurationSeconds, edit);
        if (pieces.Count == 0) throw new InvalidOperationException(L.T("error.cutsRemoveAll"));
        var outDuration = EditPlanner.OutputDuration(pieces);

        var (width, height) = OutputSize(export);
        var fps = export.Fps <= 0 ? 60 : export.Fps;
        var args = new List<string> { "-i", masterPath };
        var filters = new List<string>();
        var n = pieces.Count;
        var hasAudio = info.HasAudio;
        var nextInput = 1;
        if (!hasAudio)
        {
            args.AddRange(["-f", "lavfi", "-t", F(outDuration + 1), "-i", "anullsrc=r=48000:cl=stereo"]);
            nextInput++;
        }

        int? musicInput = null;
        if (!string.IsNullOrWhiteSpace(export.MusicPath))
        {
            if (File.Exists(export.MusicPath))
            {
                args.AddRange(["-stream_loop", "-1", "-i", export.MusicPath]);
                musicInput = nextInput++;
            }
            else
            {
                logger.LogWarning("Music file {Path} not found; exporting without music", export.MusicPath);
            }
        }

        if (n == 1)
        {
            var p = pieces[0];
            filters.Add($"[0:v]trim=start={F(p.Start)}:end={F(p.End)},setpts=(PTS-STARTPTS)/{F(p.Speed)}[vc]");
            if (hasAudio) filters.Add($"[0:a]atrim=start={F(p.Start)}:end={F(p.End)},asetpts=PTS-STARTPTS,{Atempo(p.Speed)}[ac]");
        }
        else
        {
            filters.Add($"[0:v]split={n}" + string.Concat(Enumerable.Range(0, n).Select(i => $"[vs{i}]")));
            if (hasAudio) filters.Add($"[0:a]asplit={n}" + string.Concat(Enumerable.Range(0, n).Select(i => $"[as{i}]")));
            for (var i = 0; i < n; i++)
            {
                var p = pieces[i];
                filters.Add($"[vs{i}]trim=start={F(p.Start)}:end={F(p.End)},setpts=(PTS-STARTPTS)/{F(p.Speed)}[vp{i}]");
                if (hasAudio) filters.Add($"[as{i}]atrim=start={F(p.Start)}:end={F(p.End)},asetpts=PTS-STARTPTS,{Atempo(p.Speed)}[ap{i}]");
            }

            filters.Add(hasAudio
                ? string.Concat(Enumerable.Range(0, n).Select(i => $"[vp{i}][ap{i}]")) + $"concat=n={n}:v=1:a=1[vc][ac]"
                : string.Concat(Enumerable.Range(0, n).Select(i => $"[vp{i}]")) + $"concat=n={n}:v=1:a=0[vc]");
        }

        // Vertical: the whole game frame on top of a zoomed, blurred copy that fills 9:16.
        filters.Add(export.Layout == VideoLayout.Vertical
            ? $"[vc]fps={fps},split=2[bg0][fg0];" +
              $"[bg0]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},boxblur=24:2,eq=brightness=-0.12[bg];" +
              $"[fg0]scale={width}:-2:flags=lanczos[fg];[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1[vl]"
            : $"[vc]fps={fps},scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos," +
              $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1[vl]");

        var basis = Math.Min(width, height);
        var video = new List<string>();
        video.AddRange(effects.BuildFilters(edit.Effects, new EffectContext(width, height, fps, outDuration)));
        if (edit.ShowOverlay) video.AddRange(Overlays(edit, basis));
        video.AddRange(FastForwardBadges(timeline.FastWindows, pieces, basis));
        if (export.KillCounter) video.AddRange(KillCounter(timeline.KillTimes, pieces, outDuration, basis, export.Layout));
        if (!string.IsNullOrWhiteSpace(export.Watermark) && Watermark(export.Watermark.Trim(), basis) is { } mark) video.Add(mark);
        var fade = Math.Clamp(edit.FadeSeconds, 0.1, Math.Max(0.1, outDuration / 4));
        if (edit.FadeIn) video.Add($"fade=t=in:st=0:d={F(fade)}");
        if (edit.FadeOut) video.Add($"fade=t=out:st={F(Math.Max(0, outDuration - fade))}:d={F(fade)}");
        video.Add("format=yuv420p");
        filters.Add($"[vl]{string.Join(',', video)}[vout]");

        var audio = new List<string> { "aresample=48000" };
        if (edit.FadeIn) audio.Add($"afade=t=in:st=0:d={F(fade)}");
        if (edit.FadeOut) audio.Add($"afade=t=out:st={F(Math.Max(0, outDuration - fade))}:d={F(fade)}");
        var gameAudio = hasAudio ? "[ac]" : "[1:a]";
        if (musicInput is { } m)
        {
            // Game audio a bit lower under the music; the music loops and fades out with the clip.
            filters.Add($"{gameAudio}{string.Join(',', audio)},volume=0.8[game]");
            filters.Add($"[{m}:a]{MusicChain(export.MusicVolume, outDuration)}[music]");
            filters.Add("[game][music]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        }
        else
        {
            filters.Add($"{gameAudio}{string.Join(',', audio)}[aout]");
        }

        var encoder = await encoders.BestAsync(export.Codec, cancellationToken);
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(EncoderCatalog.DeliveryArgs(encoder, export.BitrateKbps));
        args.AddRange(["-r", fps.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", "-t", F(outDuration), outputPath]);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        logger.LogInformation("Exporting {Output}: {W}x{H}@{Fps} {Encoder} {Bitrate}kbps, {Pieces} piece(s), {Duration:0.0}s, {Kills} kill(s), {Fast} fast-forward(s), music={Music}",
            outputPath, width, height, fps, encoder, export.BitrateKbps, n, outDuration, timeline.KillTimes.Count, timeline.FastWindows.Count, musicInput is not null);
        await runner.RunAsync(args, outDuration, progress, cancellationToken);
    }

    public async Task CreateThumbnailAsync(string videoPath, double atSeconds, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await runner.RunAsync(["-ss", F(Math.Max(0, atSeconds)), "-i", videoPath, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "3", "-update", "1", outputPath],
            null, null, cancellationToken);
    }

    /// <summary>Joins several exported clips into one video (montage / fragmovie), optionally over background music.</summary>
    public async Task BuildMontageAsync(IReadOnlyList<string> clips, string outputPath, double transitionSeconds, IProgress<double>? progress,
        CancellationToken ct, string? musicPath = null, int musicVolume = 35)
    {
        var segments = new List<SegmentInput>();
        foreach (var clip in clips)
        {
            var info = await ProbeAsync(clip, ct);
            segments.Add(new SegmentInput(clip, 0, info.HasAudio ? clip : null, 0, info.DurationSeconds));
        }

        if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
        {
            await BuildMasterAsync(segments, outputPath, transitionSeconds, progress, ct);
            return;
        }

        var joined = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $".{Path.GetFileNameWithoutExtension(outputPath)}.joined.mp4");
        try
        {
            await BuildMasterAsync(segments, joined, transitionSeconds, new Progress<double>(p => progress?.Report(p * 0.85)), ct);
            await MixMusicAsync(joined, musicPath, musicVolume, outputPath, new Progress<double>(p => progress?.Report(0.85 + p * 0.15)), ct);
        }
        finally
        {
            try { File.Delete(joined); } catch { /* ignore */ }
        }
    }

    /// <summary>Lays background music under a finished video (the video stream is copied, not re-encoded).</summary>
    public async Task MixMusicAsync(string videoPath, string musicPath, int musicVolume, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await ProbeAsync(videoPath, ct);
        var duration = info.DurationSeconds;
        var game = info.HasAudio ? "[0:a]aresample=48000,volume=0.8[game];" : "";
        var mix = info.HasAudio
            ? "[game][music]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]"
            : "[music]anull[aout]";
        string[] args =
        [
            "-i", videoPath, "-stream_loop", "-1", "-i", musicPath,
            "-filter_complex", $"{game}[1:a]{MusicChain(musicVolume, duration)}[music];{mix}",
            "-map", "0:v", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", F(duration), outputPath,
        ];
        logger.LogInformation("Adding music {Music} to {Video} ({Duration:0.0}s)", musicPath, videoPath, duration);
        await runner.RunAsync(args, duration, progress, ct);
    }

    private static string MusicChain(int volumePercent, double duration) =>
        $"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={F(Math.Clamp(volumePercent, 0, 200) / 100.0)}," +
        $"atrim=duration={F(duration)},asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.5," +
        $"afade=t=out:st={F(Math.Max(0, duration - 1.5))}:d={F(Math.Min(1.5, duration))}";

    /// <summary>Silences parts of a segment's audio (volume filter with an enable expression).</summary>
    private static string Mute(IReadOnlyList<TimeWindow>? windows)
    {
        if (windows is not { Count: > 0 }) return "";
        return $",volume=volume=0:enable='{string.Join('+', windows.Select(w => $"between(t,{F(w.Start)},{F(w.End)})"))}'";
    }

    private static string Atempo(double speed) =>
        string.Join(',', EditPlanner.AtempoChain(speed).Select(s => $"atempo={F(s)}"));

    private IEnumerable<string> Overlays(EditSettings edit, int basis)
    {
        const string alpha = "'if(lt(t,0.3),0,if(lt(t,0.8),(t-0.3)/0.5,if(lt(t,4.2),1,if(lt(t,4.7),(4.7-t)/0.5,0))))'";

        if (!string.IsNullOrWhiteSpace(edit.Title))
        {
            var file = WriteTextFile(edit.Title);
            if (FontFor(edit.Title) is not { } font) yield break;
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={basis / 20}:fontcolor=white:" +
                         $"box=1:boxcolor=black@0.45:boxborderw={basis / 60}:x=(w-text_w)/2:y=h*0.08:alpha={alpha}";
        }

        var label = string.Join("  •  ", new[] { edit.PlayerLabel, edit.RoundLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (label.Length > 0)
        {
            var file = WriteTextFile(label);
            if (FontFor(label) is not { } font) yield break;
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={basis / 32}:fontcolor=white:" +
                         $"box=1:boxcolor=black@0.45:boxborderw={basis / 90}:x=w*0.04:y=h*0.86:alpha={alpha}";
        }
    }

    /// <summary>"» 4x" in the top-right corner while a fast-forward stretch plays.</summary>
    private static IEnumerable<string> FastForwardBadges(IReadOnlyList<TimeWindow> windows, IReadOnlyList<EditPiece> pieces, int basis)
    {
        if (windows.Count == 0 || FontFor("x") is not { } font) yield break;
        foreach (var group in windows.GroupBy(w => w.Speed))
        {
            var enable = string.Join('+', group.Select(w =>
                $"between(t,{F(EditPlanner.MapToOutput(pieces, w.Start))},{F(EditPlanner.MapToOutput(pieces, w.End))})"));
            var file = WriteTextFile($"» {group.Key.ToString("0.#", CultureInfo.InvariantCulture)}x");
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={basis / 16}:fontcolor=white:" +
                         $"box=1:boxcolor=black@0.5:boxborderw={basis / 70}:x=w-text_w-{basis / 22}:y={basis / 22}:enable='{enable}'";
        }
    }

    /// <summary>"1/3", "2/3", "3/3" as each of the player's kills happens (plays with 2+ kills).</summary>
    private static IEnumerable<string> KillCounter(IReadOnlyList<double> killTimes, IReadOnlyList<EditPiece> pieces, double duration, int basis, VideoLayout layout)
    {
        if (killTimes.Count < 2 || FontFor("0") is not { } font) yield break;
        var times = killTimes.Select(k => EditPlanner.MapToOutput(pieces, k)).Order().ToList();
        var y = layout == VideoLayout.Vertical ? "h*0.74" : "h*0.64";
        for (var i = 0; i < times.Count; i++)
        {
            var from = times[i];
            var to = i + 1 < times.Count ? times[i + 1] : duration;
            if (to <= from) continue;
            var file = WriteTextFile($"{i + 1}/{times.Count}");
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={basis / 9}:fontcolor=white:" +
                         $"borderw={Math.Max(2, basis / 160)}:bordercolor=0xF15A24:x=w-text_w-{basis / 18}:y={y}:" +
                         $"alpha='min(1,(t-{F(from)})/0.12)':enable='between(t,{F(from)},{F(to)})'";
        }
    }

    /// <summary>The user's nick or channel, small, bottom-right, for the whole clip.</summary>
    private static string? Watermark(string text, int basis)
    {
        if (FontFor(text) is not { } font) return null;
        var file = WriteTextFile(text);
        return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={basis / 34}:fontcolor=white@0.85:" +
               $"shadowcolor=black@0.6:shadowx=2:shadowy=2:x=w-text_w-{basis / 30}:y=h-text_h-{basis / 30}";
    }

    /// <summary>
    /// drawtext has no font fallback, so pick a font that covers the text's script
    /// (player names often use Devanagari, CJK, Hangul, Thai...).
    /// </summary>
    public static string? FontFor(string text)
    {
        static bool Any(string s, int from, int to) => s.Any(c => c >= from && c <= to);
        string[] names =
            Any(text, 0x0900, 0x0DFF) ? ["Nirmala.ttc", "Nirmala.ttf"] :
            Any(text, 0x0E00, 0x0E7F) ? ["LeelaUIb.ttf", "LeelawUI.ttf"] :
            Any(text, 0xAC00, 0xD7AF) || Any(text, 0x1100, 0x11FF) ? ["malgunbd.ttf", "malgun.ttf"] :
            Any(text, 0x3040, 0x30FF) ? ["YuGothB.ttc", "msyhbd.ttc", "msyh.ttc"] :
            Any(text, 0x4E00, 0x9FFF) || Any(text, 0x3400, 0x4DBF) ? ["msyhbd.ttc", "msyh.ttc", "YuGothB.ttc"] :
            [];
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in names.Concat(["segoeuib.ttf", "arialbd.ttf", "segoeui.ttf", "arial.ttf"]))
        {
            var p = Path.Combine(fonts, name);
            if (File.Exists(p)) return p;
        }

        return null;
    }
    private static string WriteTextFile(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2hg-text");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, text, Utf8NoBom);
        return path;
    }

    /// <summary>Escapes a Windows path for use inside a quoted filter option.</summary>
    public static string EscapePath(string path) => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''");

    private static int Even(int v) => Math.Max(2, v - v % 2);
}
