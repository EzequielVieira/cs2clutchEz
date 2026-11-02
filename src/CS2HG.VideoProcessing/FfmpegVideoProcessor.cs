using System.Globalization;
using System.Text;
using System.Text.Json;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
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

        var transition = segments.Count > 1 ? Math.Min(transitionSeconds, segments.Min(s => s.Duration) / 3) : 0;
        if (transition < 0.05) transition = 0;

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
                            $"apad=whole_dur={F(seg.Duration)}[a{i}]");
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

    public async Task ExportAsync(string masterPath, string outputPath, ExportSettings export, EditSettings edit,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(masterPath, cancellationToken);
        var pieces = EditPlanner.BuildPieces(info.DurationSeconds, edit);
        if (pieces.Count == 0) throw new InvalidOperationException(L.T("error.cutsRemoveAll"));
        var outDuration = EditPlanner.OutputDuration(pieces);

        var width = Even(export.Width);
        var height = Even(export.Height);
        var fps = export.Fps <= 0 ? 60 : export.Fps;
        var args = new List<string> { "-i", masterPath };
        var filters = new List<string>();
        var n = pieces.Count;
        var hasAudio = info.HasAudio;
        if (!hasAudio) args.AddRange(["-f", "lavfi", "-t", F(outDuration + 1), "-i", "anullsrc=r=48000:cl=stereo"]);

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

        var video = new List<string>
        {
            $"fps={fps}",
            $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=lanczos",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "setsar=1",
        };
        video.AddRange(effects.BuildFilters(edit.Effects, new EffectContext(width, height, fps, outDuration)));
        if (edit.ShowOverlay) video.AddRange(Overlays(edit, height));
        var fade = Math.Clamp(edit.FadeSeconds, 0.1, Math.Max(0.1, outDuration / 4));
        if (edit.FadeIn) video.Add($"fade=t=in:st=0:d={F(fade)}");
        if (edit.FadeOut) video.Add($"fade=t=out:st={F(Math.Max(0, outDuration - fade))}:d={F(fade)}");
        video.Add("format=yuv420p");
        filters.Add($"[vc]{string.Join(',', video)}[vout]");

        var audio = new List<string> { "aresample=48000" };
        if (edit.FadeIn) audio.Add($"afade=t=in:st=0:d={F(fade)}");
        if (edit.FadeOut) audio.Add($"afade=t=out:st={F(Math.Max(0, outDuration - fade))}:d={F(fade)}");
        filters.Add($"{(hasAudio ? "[ac]" : "[1:a]")}{string.Join(',', audio)}[aout]");

        var encoder = await encoders.BestAsync(export.Codec, cancellationToken);
        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[vout]", "-map", "[aout]"]);
        args.AddRange(EncoderCatalog.DeliveryArgs(encoder, export.BitrateKbps));
        args.AddRange(["-r", fps.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart", "-t", F(outDuration), outputPath]);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        logger.LogInformation("Exporting {Output}: {W}x{H}@{Fps} {Encoder} {Bitrate}kbps, {Pieces} piece(s), {Duration:0.0}s",
            outputPath, width, height, fps, encoder, export.BitrateKbps, n, outDuration);
        await runner.RunAsync(args, outDuration, progress, cancellationToken);
    }

    public async Task CreateThumbnailAsync(string videoPath, double atSeconds, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await runner.RunAsync(["-ss", F(Math.Max(0, atSeconds)), "-i", videoPath, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "3", "-update", "1", outputPath],
            null, null, cancellationToken);
    }

    /// <summary>Joins several exported clips into one video (montage / fragmovie).</summary>
    public async Task BuildMontageAsync(IReadOnlyList<string> clips, string outputPath, double transitionSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var segments = new List<SegmentInput>();
        foreach (var clip in clips)
        {
            var info = await ProbeAsync(clip, ct);
            segments.Add(new SegmentInput(clip, 0, info.HasAudio ? clip : null, 0, info.DurationSeconds));
        }

        await BuildMasterAsync(segments, outputPath, transitionSeconds, progress, ct);
    }

    private static string Atempo(double speed) =>
        string.Join(',', EditPlanner.AtempoChain(speed).Select(s => $"atempo={F(s)}"));

    private IEnumerable<string> Overlays(EditSettings edit, int height)
    {
        const string alpha = "'if(lt(t,0.3),0,if(lt(t,0.8),(t-0.3)/0.5,if(lt(t,4.2),1,if(lt(t,4.7),(4.7-t)/0.5,0))))'";

        if (!string.IsNullOrWhiteSpace(edit.Title))
        {
            var file = WriteTextFile(edit.Title);
            if (FontFor(edit.Title) is not { } font) yield break;
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={height / 20}:fontcolor=white:" +
                         $"box=1:boxcolor=black@0.45:boxborderw={height / 60}:x=(w-text_w)/2:y=h*0.08:alpha={alpha}";
        }

        var label = string.Join("  •  ", new[] { edit.PlayerLabel, edit.RoundLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (label.Length > 0)
        {
            var file = WriteTextFile(label);
            if (FontFor(label) is not { } font) yield break;
            yield return $"drawtext=fontfile='{EscapePath(font)}':textfile='{EscapePath(file)}':fontsize={height / 32}:fontcolor=white:" +
                         $"box=1:boxcolor=black@0.45:boxborderw={height / 90}:x=w*0.04:y=h*0.86:alpha={alpha}";
        }
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
