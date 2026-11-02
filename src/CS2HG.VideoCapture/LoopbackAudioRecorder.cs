using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CS2HG.VideoCapture;

/// <summary>
/// Records the default output device (WASAPI loopback) to WAV, keeping the file aligned to wall-clock
/// time: WASAPI delivers nothing while the device is silent, so gaps are filled with silence.
/// Limitation: this is system-wide audio (other apps playing sound are recorded too).
/// </summary>
public sealed class LoopbackAudioRecorder : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly WaveFileWriter _writer;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly int _blockAlign;
    private readonly int _sampleRate;
    private readonly long _start;
    private long _framesWritten;
    private long _origin = -1;
    private bool _stopped;

    private LoopbackAudioRecorder(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
        _capture = new WasapiLoopbackCapture();
        _blockAlign = _capture.WaveFormat.BlockAlign;
        _sampleRate = _capture.WaveFormat.SampleRate;
        _writer = new WaveFileWriter(path, _capture.WaveFormat);
        _capture.DataAvailable += OnData;
        _start = Stopwatch.GetTimestamp();
        _capture.StartRecording();
    }

    public string Path { get; }

    /// <summary>Stopwatch timestamp of the first sample in the WAV.</summary>
    public long? Origin => Interlocked.Read(ref _origin) is var o && o >= 0 ? o : null;

    public static LoopbackAudioRecorder Start(string path, ILogger logger)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return new LoopbackAudioRecorder(path, logger);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_stopped) return;
            var frames = e.BytesRecorded / _blockAlign;
            if (Interlocked.Read(ref _origin) < 0)
            {
                // First packet: the earliest sample in it was captured ~packet duration ago.
                var packet = (long)((double)frames / _sampleRate * Stopwatch.Frequency);
                Interlocked.Exchange(ref _origin, Math.Max(_start, now - packet));
            }

            // Expected position of the end of this packet on the wall clock timeline.
            var expectedEnd = (long)((now - _origin) / (double)Stopwatch.Frequency * _sampleRate);
            var gap = expectedEnd - (_framesWritten + frames);
            if (gap > _sampleRate / 20)
            {
                WriteSilence(gap);
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _framesWritten += frames;
        }
    }

    private void WriteSilence(long frames)
    {
        var bytes = new byte[Math.Min(frames * _blockAlign, 1 << 20)];
        var remaining = frames * _blockAlign;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(bytes.Length, remaining);
            _writer.Write(bytes, 0, chunk);
            remaining -= chunk;
        }

        _framesWritten += frames;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (Interlocked.Read(ref _origin) >= 0)
            {
                // Pad trailing silence up to "now" so the audio is as long as the video.
                var expected = (long)((Stopwatch.GetTimestamp() - _origin) / (double)Stopwatch.Frequency * _sampleRate);
                if (expected > _framesWritten) WriteSilence(expected - _framesWritten);
            }
            else
            {
                // No audio at all (silent game): produce a silent track starting at recorder start.
                Interlocked.Exchange(ref _origin, _start);
                var expected = (long)((Stopwatch.GetTimestamp() - _start) / (double)Stopwatch.Frequency * _sampleRate);
                WriteSilence(Math.Max(expected, _sampleRate / 10));
            }

            _stopped = true;
        }

        try { _capture.StopRecording(); } catch (Exception ex) { _logger.LogDebug(ex, "StopRecording"); }
        _writer.Flush();
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _writer.Dispose();
    }
}
