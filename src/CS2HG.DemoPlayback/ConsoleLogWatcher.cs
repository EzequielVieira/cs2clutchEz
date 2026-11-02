using System.Text;
using System.Text.RegularExpressions;

namespace CS2HG.DemoPlayback;

/// <summary>
/// Tails CS2's console.log (written because the game is launched with -condebug).
/// This is the feedback channel: seeks, demo loads and cfg execs are confirmed here.
/// </summary>
public sealed class ConsoleLogWatcher : IAsyncDisposable
{
    private readonly string _path;
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private long _position;
    private string _partial = "";

    public ConsoleLogWatcher(string path, bool fromEnd = true)
    {
        _path = path;
        if (fromEnd && File.Exists(path)) _position = new FileInfo(path).Length;
        _loop = Task.Run(() => PollAsync(_cts.Token));
    }

    public event Action<string>? LineReceived;

    /// <summary>Sequence number of the next line; use it to wait only for lines produced after an action.</summary>
    public int Mark
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    public IReadOnlyList<string> LinesSince(int mark)
    {
        lock (_gate) return _lines.Skip(mark).ToList();
    }

    public async Task<Match?> WaitForAsync(Regex pattern, int sinceMark, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var index = sinceMark;
        while (true)
        {
            lock (_gate)
            {
                for (; index < _lines.Count; index++)
                {
                    var m = pattern.Match(_lines[index]);
                    if (m.Success) return m;
                }
            }

            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(40, cancellationToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_path))
                {
                    await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length < _position) _position = 0; // file was recreated by a new game session
                    if (fs.Length > _position)
                    {
                        fs.Seek(_position, SeekOrigin.Begin);
                        int read;
                        var sb = new StringBuilder(_partial);
                        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
                        {
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            _position += read;
                        }

                        var text = sb.ToString();
                        var lastNewline = text.LastIndexOf('\n');
                        _partial = lastNewline < 0 ? text : text[(lastNewline + 1)..];
                        if (lastNewline >= 0)
                        {
                            var newLines = text[..lastNewline].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                            lock (_gate) _lines.AddRange(newLines);
                            foreach (var l in newLines) LineReceived?.Invoke(l);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // the game holds the file briefly; retry on the next tick
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _loop; } catch { /* ignore */ }
        _cts.Dispose();
    }
}
