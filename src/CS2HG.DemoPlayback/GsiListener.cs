using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoPlayback;

public sealed record GsiSnapshot(DateTime ReceivedAt, ulong? ObservedSteamId, string? ObservedName, string? Map, int? Round, string? Phase);

/// <summary>
/// Minimal HTTP endpoint for CS2 Game State Integration. During demo playback CS2 reports which
/// player the camera is following, which lets the renderer verify the POV before capturing.
/// </summary>
public sealed class GsiListener : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Task? _loop;

    public GsiListener(int port, ILogger logger)
    {
        _logger = logger;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public GsiSnapshot? Latest { get; private set; }

    public bool IsRunning => _listener.IsListening;

    public void Start()
    {
        if (_listener.IsListening) return;
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                break;
            }

            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync(ct);
                Latest = Parse(body) ?? Latest;
                ctx.Response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Invalid GSI payload");
                ctx.Response.StatusCode = 400;
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    public static GsiSnapshot? Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        ulong? steamId = null;
        string? name = null;
        if (root.TryGetProperty("player", out var player))
        {
            if (player.TryGetProperty("steamid", out var sid) && ulong.TryParse(sid.GetString(), out var parsed)) steamId = parsed;
            if (player.TryGetProperty("name", out var n)) name = WebUtility.HtmlDecode(n.GetString());
        }

        string? map = null;
        int? round = null;
        string? phase = null;
        if (root.TryGetProperty("map", out var m))
        {
            if (m.TryGetProperty("name", out var mn)) map = mn.GetString();
            if (m.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number) round = r.GetInt32();
        }

        if (root.TryGetProperty("round", out var rr) && rr.TryGetProperty("phase", out var ph)) phase = ph.GetString();
        return new GsiSnapshot(DateTime.UtcNow, steamId, name, map, round, phase);
    }

    /// <summary>Waits until CS2 reports it is spectating <paramref name="steamId"/>.</summary>
    public async Task<bool> WaitForObservedAsync(ulong steamId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Latest?.ObservedSteamId == steamId) return true;
            await Task.Delay(50, ct);
        }

        return Latest?.ObservedSteamId == steamId;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }

        _cts.Dispose();
    }
}
