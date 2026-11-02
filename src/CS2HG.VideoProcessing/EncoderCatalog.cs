using CS2HG.Core.Localization;
using CS2HG.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CS2HG.VideoProcessing;

/// <summary>Detects which H.264/H.265 encoders actually work on this machine (GPU first, CPU fallback).</summary>
public sealed class EncoderCatalog(FfmpegRunner runner, ILogger logger)
{
    private static readonly string[] H264Candidates = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];
    private static readonly string[] H265Candidates = ["hevc_nvenc", "hevc_amf", "hevc_qsv", "libx265"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, bool>? _available;

    public async Task<IReadOnlyDictionary<string, bool>> ProbeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_available is not null) return _available;
            var result = new Dictionary<string, bool>();
            foreach (var encoder in H264Candidates.Concat(H265Candidates))
            {
                try
                {
                    await runner.CaptureOutputAsync(runner.Paths.Ffmpeg,
                    [
                        "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.2",
                        "-c:v", encoder, "-pix_fmt", "yuv420p", "-f", "null", "-",
                    ], ct, TimeSpan.FromSeconds(20));
                    result[encoder] = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result[encoder] = false;
                }
            }

            logger.LogInformation("Encoders: {Encoders}", string.Join(", ", result.Select(kv => $"{kv.Key}={(kv.Value ? "ok" : "no")}")));
            _available = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> BestAsync(VideoCodec codec, CancellationToken ct)
    {
        var available = await ProbeAsync(ct);
        var list = codec == VideoCodec.H265 ? H265Candidates : H264Candidates;
        var pick = list.FirstOrDefault(e => available.GetValueOrDefault(e));
        if (pick is null && codec == VideoCodec.H265)
            throw new FfmpegException(L.T("error.h265Unsupported"), "");
        return pick ?? throw new FfmpegException(L.T("error.noH264"), "");
    }

    public async Task<bool> SupportsH265Async(CancellationToken ct)
    {
        var available = await ProbeAsync(ct);
        return H265Candidates.Any(e => available.GetValueOrDefault(e));
    }

    /// <summary>Near-lossless settings for the raw capture / master (fast, high quality).</summary>
    public static IEnumerable<string> IntermediateArgs(string encoder) => encoder switch
    {
        "h264_nvenc" or "hevc_nvenc" => ["-c:v", encoder, "-preset", "p4", "-rc", "constqp", "-qp", "17"],
        "h264_amf" or "hevc_amf" => ["-c:v", encoder, "-quality", "speed", "-rc", "cqp", "-qp_i", "17", "-qp_p", "17"],
        "h264_qsv" or "hevc_qsv" => ["-c:v", encoder, "-preset", "veryfast", "-global_quality", "18"],
        "libx265" => ["-c:v", encoder, "-preset", "ultrafast", "-crf", "18"],
        _ => ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "17"],
    };

    /// <summary>Delivery settings honoring the requested bitrate.</summary>
    public static IEnumerable<string> DeliveryArgs(string encoder, int bitrateKbps)
    {
        var b = $"{bitrateKbps}k";
        var max = $"{(int)(bitrateKbps * 1.5)}k";
        var buf = $"{bitrateKbps * 2}k";
        IEnumerable<string> args = encoder switch
        {
            "h264_nvenc" or "hevc_nvenc" => ["-c:v", encoder, "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            "h264_amf" or "hevc_amf" => ["-c:v", encoder, "-quality", "quality", "-rc", "vbr_peak", "-b:v", b, "-maxrate", max],
            "h264_qsv" or "hevc_qsv" => ["-c:v", encoder, "-preset", "slow", "-b:v", b, "-maxrate", max],
            "libx265" => ["-c:v", encoder, "-preset", "medium", "-b:v", b, "-maxrate", max, "-bufsize", buf],
            _ => ["-c:v", "libx264", "-preset", "medium", "-b:v", b, "-maxrate", max, "-bufsize", buf],
        };
        if (encoder.StartsWith("hevc", StringComparison.Ordinal) || encoder == "libx265") args = args.Concat(["-tag:v", "hvc1"]);
        return args;
    }
}
