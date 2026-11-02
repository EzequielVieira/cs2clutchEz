using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoPlayback;

public sealed class GameCoordinatorException(string message) : Exception(message);

/// <summary>
/// Talks to the CS2 Game Coordinator through the Steam client that is already running and logged in
/// (Steamworks API, loaded from CS2's own steam_api64.dll) — no credentials are handled by the app.
/// While connected, Steam shows the user as "playing CS2"; CS2 itself must be closed.
/// All calls run on one dedicated thread, as the Steamworks API requires.
/// </summary>
public sealed unsafe partial class GameCoordinatorClient : IDisposable
{
    private const uint ProtoMask = 0x80000000;
    private const uint MsgClientHello = 4006;
    private const uint MsgClientWelcome = 4004;
    private const uint MsgMatchmakingGc2ClientHello = 9110;
    private const uint MsgMatchmakingClient2GcHello = 9109;
    private const uint MsgMatchList = 9139;
    private const uint MsgMatchListRequestFullGameInfo = 9147;
    private const ulong SteamId64Base = 76561197960265728UL;

    private readonly ILogger _logger;
    private readonly IntPtr _lib;
    private readonly IntPtr _gc;
    private readonly delegate* unmanaged<IntPtr, uint, byte*, uint, int> _send;
    private readonly delegate* unmanaged<IntPtr, uint*, byte> _isAvailable;
    private readonly delegate* unmanaged<IntPtr, uint*, byte*, uint, uint*, int> _retrieve;
    private readonly delegate* unmanaged<void> _runCallbacks;
    private readonly delegate* unmanaged<void> _shutdown;
    private bool _disposed;

    public ulong SteamId { get; }

    public uint AccountId => (uint)(SteamId - SteamId64Base);

    private readonly uint _clientVersion;

    private GameCoordinatorClient(string steamApiDll, uint clientVersion, ILogger logger)
    {
        _logger = logger;
        _clientVersion = clientVersion;
        // Steamworks reads the app id from the environment when steam_appid.txt is absent.
        Environment.SetEnvironmentVariable("SteamAppId", "730");
        Environment.SetEnvironmentVariable("SteamGameId", "730");
        _lib = NativeLibrary.Load(steamApiDll);

        var errBuffer = stackalloc byte[1024];
        if (NativeLibrary.TryGetExport(_lib, "SteamAPI_InitFlat", out var initFlat))
        {
            var result = ((delegate* unmanaged<byte*, int>)initFlat)(errBuffer);
            if (result != 0)
            {
                var msg = Marshal.PtrToStringUTF8((IntPtr)errBuffer) ?? "";
                throw new GameCoordinatorException(L.F("gc.steamInitCode", result, msg));
            }
        }
        else if (((delegate* unmanaged<byte>)Export("SteamAPI_Init"))() == 0)
        {
            throw new GameCoordinatorException(L.T("gc.steamInit"));
        }

        _runCallbacks = (delegate* unmanaged<void>)Export("SteamAPI_RunCallbacks");
        _shutdown = (delegate* unmanaged<void>)Export("SteamAPI_Shutdown");
        var user = ((delegate* unmanaged<int>)Export("SteamAPI_GetHSteamUser"))();
        var pipe = ((delegate* unmanaged<int>)Export("SteamAPI_GetHSteamPipe"))();
        var client = ((delegate* unmanaged<IntPtr>)Export("SteamClient"))();
        var getGeneric = (delegate* unmanaged<IntPtr, int, int, byte*, IntPtr>)Export("SteamAPI_ISteamClient_GetISteamGenericInterface");
        var name = "SteamGameCoordinator001\0"u8;
        fixed (byte* pName = name) _gc = getGeneric(client, user, pipe, pName);
        if (_gc == IntPtr.Zero) throw new GameCoordinatorException(L.T("gc.noInterface"));

        // ISteamGameCoordinator vtable: SendMessage, IsMessageAvailable, RetrieveMessage.
        var vtable = *(IntPtr**)_gc;
        _send = (delegate* unmanaged<IntPtr, uint, byte*, uint, int>)vtable[0];
        _isAvailable = (delegate* unmanaged<IntPtr, uint*, byte>)vtable[1];
        _retrieve = (delegate* unmanaged<IntPtr, uint*, byte*, uint, uint*, int>)vtable[2];

        if (NativeLibrary.TryGetExport(_lib, "SteamAPI_SteamUser_v023", out var steamUserFn) &&
            NativeLibrary.TryGetExport(_lib, "SteamAPI_ISteamUser_GetSteamID", out var getSteamId))
        {
            var steamUser = ((delegate* unmanaged<IntPtr>)steamUserFn)();
            SteamId = ((delegate* unmanaged<IntPtr, ulong>)getSteamId)(steamUser);
        }
    }

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_lib, name, out var p) ? p : throw new GameCoordinatorException(L.F("gc.missingExport", name));

    /// <summary>Opens a GC session on a dedicated thread, runs <paramref name="work"/>, then shuts Steamworks down.</summary>
    public static Task<T> RunAsync<T>(string cs2BinFolder, ILogger logger, Func<GameCoordinatorClient, CancellationToken, T> work, CancellationToken ct)
    {
        var dll = Path.Combine(cs2BinFolder, "steam_api64.dll");
        if (!File.Exists(dll)) throw new GameCoordinatorException(L.T("gc.noSteamApi"));
        if (Process.GetProcessesByName("cs2").Length > 0)
            throw new GameCoordinatorException(L.T("gc.closeCs2"));
        if (Process.GetProcessesByName("steam").Length == 0)
            throw new GameCoordinatorException(L.T("gc.steamNotRunning"));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var gc = new GameCoordinatorClient(dll, ReadClientVersion(cs2BinFolder), logger);
                gc.Connect(ct);
                tcs.TrySetResult(work(gc, ct));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "Steam GC" };
        thread.Start();
        return tcs.Task;
    }

    /// <summary>ClientVersion from game/csgo/steam.inf (the GC expects the current client build in the hello).</summary>
    private static uint ReadClientVersion(string cs2BinFolder)
    {
        try
        {
            var inf = Path.GetFullPath(Path.Combine(cs2BinFolder, "..", "..", "csgo", "steam.inf"));
            var line = File.ReadLines(inf).FirstOrDefault(l => l.StartsWith("ClientVersion=", StringComparison.OrdinalIgnoreCase));
            if (line is not null && uint.TryParse(line["ClientVersion=".Length..].Trim(), out var v)) return v;
        }
        catch
        {
            // fall through
        }

        return 0;
    }

    private void Send(uint type, byte[] body)
    {
        var data = new byte[8 + body.Length];
        BitConverter.GetBytes(type | ProtoMask).CopyTo(data, 0);
        BitConverter.GetBytes(0u).CopyTo(data, 4);
        body.CopyTo(data, 8);
        fixed (byte* p = data)
        {
            var result = _send(_gc, type | ProtoMask, p, (uint)data.Length);
            if (result != 0) throw new GameCoordinatorException(L.F("gc.sendFailed", result));
        }
    }

    private (uint Type, byte[] Body)? Poll()
    {
        _runCallbacks();
        uint size;
        if (_isAvailable(_gc, &size) == 0) return null;
        var buffer = new byte[size];
        uint type, got;
        fixed (byte* p = buffer)
        {
            if (_retrieve(_gc, &type, p, size, &got) != 0) return null;
        }

        if ((type & ProtoMask) == 0) return (type, buffer[..(int)got]);
        var headerLength = BitConverter.ToInt32(buffer, 4);
        var start = 8 + headerLength;
        return (type & ~ProtoMask, start <= got ? buffer[start..(int)got] : []);
    }

    private byte[]? WaitFor(uint wanted, TimeSpan timeout, CancellationToken ct, Action? resend = null)
    {
        var sw = Stopwatch.StartNew();
        var lastSend = sw.Elapsed;
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var msg = Poll();
            if (msg is { } m)
            {
                _logger.LogDebug("GC message {Type} ({Bytes} bytes)", m.Type, m.Body.Length);
                if (m.Type == wanted) return m.Body;
                continue;
            }

            if (resend is not null && sw.Elapsed - lastSend > TimeSpan.FromSeconds(3))
            {
                resend();
                lastSend = sw.Elapsed;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private void Connect(CancellationToken ct)
    {
        // CMsgClientHello: version = 1, client_session_need = 3.
        var hello = new ProtoWriter();
        if (_clientVersion > 0) hello.Varint(1, _clientVersion);
        var helloBody = hello.Varint(3, 1).ToArray();
        void Hello() => Send(MsgClientHello, helloBody);
        Hello();
        var sw = Stopwatch.StartNew();
        var lastHello = sw.Elapsed;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            ct.ThrowIfCancellationRequested();
            if (Poll() is { } m)
            {
                // Any GC traffic (welcome, matchmaking hello, global stats...) means the session is up.
                _logger.LogInformation("Connected to the CS2 Game Coordinator as {SteamId} (first message {Type})", SteamId, m.Type);
                if (m.Type != MsgMatchmakingGc2ClientHello)
                {
                    // CS-specific hello (k_EMsgGCCStrike15_v2_MatchmakingClient2GCHello); requests are served after it.
                    Send(MsgMatchmakingClient2GcHello, []);
                    WaitFor(MsgMatchmakingGc2ClientHello, TimeSpan.FromSeconds(6), ct);
                }

                return;
            }
            else
            {
                Thread.Sleep(50);
                if (sw.Elapsed - lastHello > TimeSpan.FromSeconds(4))
                {
                    Hello();
                    lastHello = sw.Elapsed;
                }
            }
        }

        throw new GameCoordinatorException(L.T("gc.noHello"));
    }

    public GcMatch? FullGameInfo(ShareCode code, CancellationToken ct)
    {
        var body = new ProtoWriter().Varint(1, code.MatchId).Varint(2, code.OutcomeId).Varint(3, code.Token).ToArray();
        Send(MsgMatchListRequestFullGameInfo, body);
        var reply = WaitFor(MsgMatchList, TimeSpan.FromSeconds(20), ct, () => Send(MsgMatchListRequestFullGameInfo, body));
        if (reply is null) throw new GameCoordinatorException(L.T("gc.noMatchReply"));
        var match = MatchListParser.Parse(reply).FirstOrDefault(m => m.MatchId == code.MatchId) ?? MatchListParser.Parse(reply).FirstOrDefault();
        if (match is not null && match.Token == 0) match.Token = code.Token;
        return match;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); } catch { /* ignore */ }
    }
}

/// <summary>Minimal protobuf encoder for the few GC request messages.</summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _bytes = [];

    public ProtoWriter Varint(int field, ulong value)
    {
        WriteRaw((ulong)(field << 3));
        WriteRaw(value);
        return this;
    }

    private void WriteRaw(ulong v)
    {
        while (v >= 0x80)
        {
            _bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }

        _bytes.Add((byte)v);
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>Minimal protobuf reader (wire format only).</summary>
public ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly bool HasMore => _pos < _data.Length;

    public bool Next(out int field, out int wireType)
    {
        field = 0;
        wireType = 0;
        if (_pos >= _data.Length) return false;
        var tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64 && _pos < _data.Length; shift += 7)
        {
            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }

        return result;
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarint();
        var span = _data.Slice(_pos, Math.Min(length, _data.Length - _pos));
        _pos += span.Length;
        return span;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _pos += 8; break;
            case 2: ReadBytes(); break;
            case 5: _pos += 4; break;
            default: _pos = _data.Length; break;
        }
    }
}

/// <summary>Parses CMsgGCCStrike15_v2_MatchList (field numbers from GameTracking-CS2 protobufs).</summary>
public static partial class MatchListParser
{
    public static List<GcMatch> Parse(ReadOnlySpan<byte> data)
    {
        var list = new List<GcMatch>();
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 4 && wire == 2) list.Add(ParseMatch(reader.ReadBytes()));
            else reader.Skip(wire);
        }

        return list;
    }

    /// <summary>Parses a CDataGCCStrike15_v2_MatchInfo (also the content of CS2's match730_*.dem.info files).</summary>
    public static GcMatch ParseMatch(ReadOnlySpan<byte> data)
    {
        var match = new GcMatch();
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0: match.MatchId = reader.ReadVarint(); break;
                case 2 when wire == 0: match.MatchTime = DateTimeOffset.FromUnixTimeSeconds((long)reader.ReadVarint()).LocalDateTime; break;
                case 3 when wire == 2: ParseWatchable(reader.ReadBytes(), match); break;
                case 4 or 5 when wire == 2: ParseRoundStats(reader.ReadBytes(), match); break;
                default: reader.Skip(wire); break;
            }
        }

        return match;
    }

    private static void ParseWatchable(ReadOnlySpan<byte> data, GcMatch match)
    {
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 2 && wire == 0) match.Token = (ushort)(reader.ReadVarint() & 0xFFFF);
            else if (field == 10 && wire == 2 && Encoding.UTF8.GetString(reader.ReadBytes()) is { Length: > 0 } map) match.Map = map;
            else reader.Skip(wire);
        }
    }

    private static void ParseRoundStats(ReadOnlySpan<byte> data, GcMatch match)
    {
        var reader = new ProtoReader(data);
        ulong reservation = 0;
        string? url = null;
        var scores = new List<int>();
        var duration = 0;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0: reservation = reader.ReadVarint(); break;
                case 3 when wire == 2:
                    var s = Encoding.UTF8.GetString(reader.ReadBytes());
                    if (DemoUrlRegex().IsMatch(s)) url = s;
                    break;
                case 12 when wire == 0: scores.Add((int)reader.ReadVarint()); break;
                case 12 when wire == 2:
                    var packed = new ProtoReader(reader.ReadBytes());
                    while (packed.HasMore) scores.Add((int)packed.ReadVarint());
                    break;
                case 15 when wire == 0: duration = (int)reader.ReadVarint(); break;
                default: reader.Skip(wire); break;
            }
        }

        // The last round stats entry carries the final outcome id and the demo URL.
        if (reservation != 0) match.OutcomeId = reservation;
        if (url is not null) match.DemoUrl = url;
        if (scores.Count >= 2) match.TeamScores = [.. scores];
        if (duration > 0) match.DurationSeconds = duration;
    }

    [GeneratedRegex(@"^https?://replay\d+\.valve\.net/730/\d+_\d+\.dem(\.bz2)?$")]
    public static partial Regex DemoUrlRegex();
}
