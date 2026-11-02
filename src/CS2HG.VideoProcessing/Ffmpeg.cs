using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using CS2HG.Core.Localization;
using Microsoft.Extensions.Logging;

namespace CS2HG.VideoProcessing;

public sealed class FfmpegNotFoundException(string message) : Exception(message);

public sealed class FfmpegException(string message, string stderrTail) : Exception(message + (stderrTail.Length > 0 ? "\n" + stderrTail : ""))
{
    public string StderrTail { get; } = stderrTail;
}

public sealed record FfmpegPaths(string Ffmpeg, string Ffprobe);

public static class FfmpegLocator
{
    /// <summary>Looks for ffmpeg in: configured path, app tools folder, user tools folder, PATH.</summary>
    public static FfmpegPaths? Find(string? configured, string userToolsFolder)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Directory.Exists(configured) ? configured : Path.GetDirectoryName(configured) ?? "");
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"));
        candidates.Add(userToolsFolder);
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in candidates.Where(d => d.Length > 0))
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) continue;
            return ffmpeg.Length > MaxSafePathLength ? MirrorToShortPath(dir, userToolsFolder) : new FfmpegPaths(ffmpeg, ffprobe);
        }

        return null;
    }

    /// <summary>FFmpeg fails to parse its arguments when its own path exceeds MAX_PATH (260).</summary>
    public const int MaxSafePathLength = 200;

    private static FfmpegPaths MirrorToShortPath(string sourceDir, string shortDir)
    {
        Directory.CreateDirectory(shortDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(shortDir, Path.GetFileName(file));
            var src = new FileInfo(file);
            if (!File.Exists(target) || new FileInfo(target).Length != src.Length) File.Copy(file, target, true);
        }

        return new FfmpegPaths(Path.Combine(shortDir, "ffmpeg.exe"), Path.Combine(shortDir, "ffprobe.exe"));
    }
}

/// <summary>Downloads a GPL FFmpeg build (BtbN, GitHub releases) into the user's tools folder.</summary>
public static class FfmpegInstaller
{
    public const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip";

    public static async Task<FfmpegPaths> InstallAsync(string targetFolder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetFolder);
        var zip = Path.Combine(Path.GetTempPath(), $"cs2hg-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(zip))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report(0.95 * done / total);
                }
            }

            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    var parts = entry.FullName.Replace('\\', '/').Split('/');
                    if (parts.Length < 2 || parts[^2] != "bin" || string.IsNullOrEmpty(entry.Name)) continue;
                    entry.ExtractToFile(Path.Combine(targetFolder, entry.Name), overwrite: true);
                }

                var license = archive.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase));
                license?.ExtractToFile(Path.Combine(targetFolder, "LICENSE.txt"), overwrite: true);
            }

            progress?.Report(1);
            var paths = new FfmpegPaths(Path.Combine(targetFolder, "ffmpeg.exe"), Path.Combine(targetFolder, "ffprobe.exe"));
            if (!File.Exists(paths.Ffmpeg)) throw new FfmpegNotFoundException(L.T("error.ffmpegPackage"));
            return paths;
        }
        finally
        {
            try { File.Delete(zip); } catch { /* ignore */ }
        }
    }
}

/// <summary>Runs ffmpeg/ffprobe with argument lists (no shell quoting), progress parsing and cancellation.</summary>
public sealed class FfmpegRunner(FfmpegPaths paths, ILogger logger)
{
    public FfmpegPaths Paths => paths;

    public async Task RunAsync(IReadOnlyList<string> args, double? expectedDurationSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var full = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-stats_period", "0.25" };
        full.AddRange(args);
        using var process = Start(paths.Ffmpeg, full, redirectInput: false);
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                    if (stderr.Length > 32_000) stderr.Remove(0, stderr.Length - 16_000);
                }
            }
        }, CancellationToken.None);

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (expectedDurationSeconds is > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out var us))
                {
                    progress?.Report(Math.Clamp(us / 1e6 / expectedDurationSeconds.Value, 0, 1));
                }
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = Tail(stderr.ToString(), 12);
            logger.LogError("ffmpeg failed ({Code}): {Args}\n{Tail}", process.ExitCode, string.Join(' ', full), tail);
            throw new FfmpegException(L.F("error.processExit", "FFmpeg", process.ExitCode), tail);
        }

        progress?.Report(1);
    }

    public async Task<string> CaptureOutputAsync(string exe, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var process = Start(exe, args, redirectInput: false);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* ignore */ }
            throw;
        }

        var output = await stdout;
        var err = await stderr;
        if (process.ExitCode != 0) throw new FfmpegException(L.F("error.processExit", Path.GetFileName(exe), process.ExitCode), Tail(err, 8));
        return output;
    }

    public static Process Start(string exe, IEnumerable<string> args, bool redirectInput)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new FfmpegException(L.T("error.ffmpegStart"), "");
    }

    public static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines)).Trim();

    public static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
