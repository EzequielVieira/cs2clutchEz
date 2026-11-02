using System.Diagnostics;
using System.Text;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.VideoProcessing;
using Microsoft.Extensions.Logging;

namespace CS2HG.VideoCapture;

/// <summary>
/// Records the CS2 window in real time with FFmpeg's Windows Graphics Capture source (gfxcapture, FFmpeg 8.1+).
/// WGC captures the window's own surface, so other windows on top do not leak into the video.
/// Game audio is recorded in parallel by <see cref="LoopbackAudioRecorder"/>.
/// </summary>
public sealed class WindowCaptureRecorder(FfmpegPaths ffmpeg, ILoggerFactory loggerFactory) : IGameRecorder
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WindowCaptureRecorder>();

    public string BackendName => "Windows Graphics Capture (FFmpeg gfxcapture)";

    public static async Task<bool> IsSupportedAsync(FfmpegPaths ffmpeg, CancellationToken ct)
    {
        try
        {
            using var p = FfmpegRunner.Start(ffmpeg.Ffmpeg, ["-hide_banner", "-h", "filter=gfxcapture"], redirectInput: false);
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return output.Contains("window_title", StringComparison.Ordinal) || output.Contains("hwnd", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task<ICaptureSession> StartAsync(CaptureOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.VideoPath)!);
        var source = $"gfxcapture=hwnd={options.Window.ToInt64()}:max_framerate={options.Fps}:capture_cursor=0," +
                     "hwdownload,format=bgra,format=yuv420p";
        var args = new List<string>
        {
            "-hide_banner", "-y", "-progress", "pipe:1", "-stats_period", "0.1",
            "-filter_complex", source,
        };
        args.AddRange(EncoderCatalog.IntermediateArgs(options.Encoder));
        args.AddRange(["-r", options.Fps.ToString(), "-pix_fmt", "yuv420p", options.VideoPath]);

        var process = FfmpegRunner.Start(ffmpeg.Ffmpeg, args, redirectInput: true);
        LoopbackAudioRecorder? audio = null;
        if (options.AudioPath is not null)
        {
            try
            {
                audio = LoopbackAudioRecorder.Start(options.AudioPath, loggerFactory.CreateLogger<LoopbackAudioRecorder>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio capture unavailable; clip will be silent");
            }
        }

        var session = new CaptureSession(process, options.VideoPath, audio, _logger);
        _logger.LogInformation("Capture started: {Video} (encoder {Encoder}, {Fps} fps, hwnd {Hwnd})",
            options.VideoPath, options.Encoder, options.Fps, options.Window);
        await Task.Yield();
        return session;
    }

    private sealed class CaptureSession : ICaptureSession
    {
        private readonly Process _process;
        private readonly LoopbackAudioRecorder? _audio;
        private readonly ILogger _logger;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private readonly TaskCompletionSource _recording = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _origin = long.MaxValue;
        private bool _stopped;

        public CaptureSession(Process process, string videoPath, LoopbackAudioRecorder? audio, ILogger logger)
        {
            _process = process;
            _audio = audio;
            _logger = logger;
            VideoPath = videoPath;
            _stdoutTask = Task.Run(ReadProgressAsync);
            _stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync()) is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                        if (_stderr.Length > 20000) _stderr.Remove(0, 10000);
                    }
                }
            });
        }

        public string VideoPath { get; }

        public string? AudioPath => _audio?.Path;

        /// <summary>Best (earliest) estimate of when video t=0 happened: min(wall clock − encoded time).</summary>
        public long VideoOrigin => Interlocked.Read(ref _origin);

        public long? AudioOrigin => _audio?.Origin;

        private async Task ReadProgressAsync()
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), out var us) || us <= 0)
                    continue;
                var now = Stopwatch.GetTimestamp();
                var estimate = now - (long)(us / 1e6 * Stopwatch.Frequency);
                long current;
                do
                {
                    current = Interlocked.Read(ref _origin);
                    if (estimate >= current) break;
                } while (Interlocked.CompareExchange(ref _origin, estimate, current) != current);

                if (us > 400_000) _recording.TrySetResult();
            }

            _recording.TrySetResult();
        }

        public async Task WaitUntilRecordingAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var done = await Task.WhenAny(_recording.Task, Task.Delay(timeout, cancellationToken));
            if (done != _recording.Task || _process.HasExited)
            {
                string tail;
                lock (_stderr) tail = FfmpegRunner.Tail(_stderr.ToString(), 10);
                throw new FfmpegException(L.T("error.captureNotStarted"), tail);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_stopped) return;
            _stopped = true;
            _audio?.Stop();
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteAsync('q');
                    await _process.StandardInput.FlushAsync(cancellationToken);
                }
            }
            catch (IOException)
            {
                // ffmpeg already closing
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("ffmpeg capture did not stop; killing it");
                try { _process.Kill(true); } catch { /* ignore */ }
            }

            await Task.WhenAll(_stdoutTask, _stderrTask);
            if (_process.ExitCode != 0)
            {
                string tail;
                lock (_stderr) tail = FfmpegRunner.Tail(_stderr.ToString(), 10);
                throw new FfmpegException(L.F("error.processExit", "gfxcapture", _process.ExitCode), tail);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                try { await StopAsync(CancellationToken.None); } catch { /* ignore */ }
            }

            _audio?.Dispose();
            _process.Dispose();
        }
    }
}
