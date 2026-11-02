using System.Security.Cryptography;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Utilities;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoParser;

public sealed class DemoFormatException(string message) : Exception(message);

/// <summary>
/// Validates a CS2 demo, decompresses .dem.bz2 into the demos cache and computes a content hash
/// (the hash identifies the demo in the database, cache and render queue).
/// </summary>
public sealed class DemoImporter(string demosCacheFolder, ILogger<DemoImporter> logger) : IDemoImporter
{
    private static readonly byte[] Cs2Magic = "PBDEMS2\0"u8.ToArray();
    private static readonly byte[] CsgoMagic = "HL2DEMO\0"u8.ToArray();

    public static bool IsSupportedExtension(string path) =>
        path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase);

    public async Task<ImportedDemo> ImportAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("render.demoMissing"), path);
        if (!IsSupportedExtension(path)) throw new DemoFormatException(L.T("demo.unsupportedFormat"));

        var compressed = path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase);
        var demoPath = path;
        if (compressed)
        {
            demoPath = await DecompressAsync(path, progress, cancellationToken);
        }

        ValidateHeader(demoPath);
        var hash = await ComputeHashAsync(demoPath, compressed ? null : progress, cancellationToken);
        var size = new FileInfo(demoPath).Length;
        logger.LogInformation("Imported demo {Path} ({Size:N0} bytes, hash {Hash}, compressed={Compressed})", path, size, hash, compressed);
        return new ImportedDemo(path, demoPath, hash, size, compressed);
    }

    public static void ValidateHeader(string demoPath)
    {
        Span<byte> header = stackalloc byte[8];
        using var fs = File.OpenRead(demoPath);
        if (fs.Length < 16 || fs.Read(header) != 8)
            throw new DemoFormatException(L.T("demo.tooSmall"));
        if (header.SequenceEqual(CsgoMagic))
            throw new DemoFormatException(L.T("demo.csgo"));
        if (!header.SequenceEqual(Cs2Magic))
            throw new DemoFormatException(L.T("demo.notCs2"));
    }

    private async Task<string> DecompressAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(demosCacheFolder);
        var baseName = Path.GetFileName(path);
        baseName = baseName[..^4]; // drop .bz2
        if (!baseName.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)) baseName += ".dem";

        // Cache key: name + size + mtime of the compressed file, so re-imports are instant.
        var info = new FileInfo(path);
        var key = Hashing.Sha1Hex($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")[..10];
        var target = Path.Combine(demosCacheFolder, $"{Path.GetFileNameWithoutExtension(baseName)}_{key}.dem");
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            logger.LogInformation("Using cached decompressed demo {Target}", target);
            progress?.Report(1);
            return target;
        }

        var tmp = target + ".part";
        try
        {
            await using (var input = File.OpenRead(path))
            await using (var bz = new BZip2InputStream(input))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await bz.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(Math.Min(0.99, (double)input.Position / Math.Max(1, input.Length)));
                }
            }

            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(tmp);
            throw new DemoFormatException(L.F("demo.bz2Failed", ex.Message));
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        progress?.Report(1);
        return target;
    }

    public static async Task<string> ComputeHashAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            progress?.Report((double)fs.Position / Math.Max(1, fs.Length));
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset())[..32];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
