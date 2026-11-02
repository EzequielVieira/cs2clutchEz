using System.IO.Compression;
using System.Text.Json;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using CS2HG.Core.Utilities;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CS2HG.Storage;

public sealed class DemoRecord
{
    public string Hash { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string DemoPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Map { get; set; } = "";
    public long Size { get; set; }
    public double Duration { get; set; }
    public int TickRate { get; set; }
    public int Rounds { get; set; }
    public int ScoreCt { get; set; }
    public int ScoreT { get; set; }
    public string TeamCt { get; set; } = "";
    public string TeamT { get; set; } = "";
    public string ImportedAt { get; set; } = "";
    public string? AnalyzedAt { get; set; }
    public string? Parser { get; set; }

    public string DisplayName => $"{Map} • {ScoreCt}:{ScoreT} • {FileName}";
}

public sealed class AnalysisRecord
{
    public long Id { get; set; }
    public string DemoHash { get; set; } = "";
    public string Map { get; set; } = "";
    public string? PlayerSteamId { get; set; }
    public string PlayerName { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public int HighlightCount { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>SQLite database (Database/cs2hg.db). Rich objects are stored as JSON columns.</summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string databaseFolder)
    {
        Directory.CreateDirectory(databaseFolder);
        DatabasePath = Path.Combine(databaseFolder, "cs2hg.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        c.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS demos (
              hash TEXT PRIMARY KEY, original_path TEXT, demo_path TEXT, file_name TEXT, map TEXT, size INTEGER,
              duration REAL, tick_rate INTEGER, rounds INTEGER, score_ct INTEGER, score_t INTEGER, team_ct TEXT, team_t TEXT,
              imported_at TEXT, analyzed_at TEXT, parser TEXT);
            CREATE TABLE IF NOT EXISTS players (
              demo_hash TEXT, steam_id TEXT, name TEXT, team TEXT, kills INTEGER, deaths INTEGER, assists INTEGER,
              headshots INTEGER, damage INTEGER, adr REAL, PRIMARY KEY (demo_hash, steam_id));
            CREATE TABLE IF NOT EXISTS analyses (
              id INTEGER PRIMARY KEY AUTOINCREMENT, demo_hash TEXT, map TEXT, player_steam_id TEXT, player_name TEXT,
              started_at TEXT, finished_at TEXT, highlight_count INTEGER, status TEXT, error TEXT, duration_ms INTEGER);
            CREATE TABLE IF NOT EXISTS highlights (
              id TEXT PRIMARY KEY, demo_hash TEXT, player_steam_id TEXT, round INTEGER, score INTEGER, category TEXT,
              title TEXT, data TEXT);
            CREATE INDEX IF NOT EXISTS ix_highlights_demo ON highlights(demo_hash, player_steam_id);
            CREATE TABLE IF NOT EXISTS render_jobs (
              id TEXT PRIMARY KEY, status TEXT, ord INTEGER, dedupe_key TEXT, highlight_id TEXT, demo_hash TEXT,
              output_path TEXT, created_at TEXT, data TEXT);
            """);
    }
}

public sealed class DemoRepository(AppDatabase db)
{
    public void Upsert(DemoRecord d)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO demos (hash, original_path, demo_path, file_name, map, size, duration, tick_rate, rounds, score_ct, score_t,
                               team_ct, team_t, imported_at, analyzed_at, parser)
            VALUES (@Hash, @OriginalPath, @DemoPath, @FileName, @Map, @Size, @Duration, @TickRate, @Rounds, @ScoreCt, @ScoreT,
                    @TeamCt, @TeamT, @ImportedAt, @AnalyzedAt, @Parser)
            ON CONFLICT(hash) DO UPDATE SET original_path=excluded.original_path, demo_path=excluded.demo_path,
              file_name=excluded.file_name, map=excluded.map, size=excluded.size, duration=excluded.duration,
              tick_rate=excluded.tick_rate, rounds=excluded.rounds, score_ct=excluded.score_ct, score_t=excluded.score_t,
              team_ct=excluded.team_ct, team_t=excluded.team_t, analyzed_at=COALESCE(excluded.analyzed_at, demos.analyzed_at),
              parser=COALESCE(excluded.parser, demos.parser)
            """, d);
    }

    private const string Select = """
        SELECT hash AS Hash, original_path AS OriginalPath, demo_path AS DemoPath, file_name AS FileName, map AS Map,
               size AS Size, duration AS Duration, tick_rate AS TickRate, rounds AS Rounds, score_ct AS ScoreCt,
               score_t AS ScoreT, team_ct AS TeamCt, team_t AS TeamT, imported_at AS ImportedAt, analyzed_at AS AnalyzedAt,
               parser AS Parser FROM demos
        """;

    public IReadOnlyList<DemoRecord> GetRecent(int limit = 50)
    {
        using var c = db.Open();
        return c.Query<DemoRecord>(Select + " ORDER BY COALESCE(analyzed_at, imported_at) DESC LIMIT @limit", new { limit }).ToList();
    }

    public DemoRecord? Get(string hash)
    {
        using var c = db.Open();
        return c.QueryFirstOrDefault<DemoRecord>(Select + " WHERE hash = @hash", new { hash });
    }

    public void Delete(string hash)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM demos WHERE hash=@hash; DELETE FROM players WHERE demo_hash=@hash; DELETE FROM highlights WHERE demo_hash=@hash;", new { hash });
    }

    public void SavePlayers(string hash, IEnumerable<PlayerInfo> players)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM players WHERE demo_hash=@hash", new { hash }, tx);
        foreach (var p in players)
        {
            c.Execute("""
                INSERT INTO players (demo_hash, steam_id, name, team, kills, deaths, assists, headshots, damage, adr)
                VALUES (@hash, @sid, @Name, @team, @k, @d, @a, @hs, @dmg, @adr)
                """, new
            {
                hash, sid = p.SteamId.ToString(), p.Name, team = p.Team.ToString(), k = p.Stats.Kills, d = p.Stats.Deaths,
                a = p.Stats.Assists, hs = p.Stats.Headshots, dmg = p.Stats.Damage, adr = p.Stats.Adr,
            }, tx);
        }

        tx.Commit();
    }
}

public sealed class AnalysisRepository(AppDatabase db)
{
    public long Add(AnalysisRecord r)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO analyses (demo_hash, map, player_steam_id, player_name, started_at, finished_at, highlight_count, status, error, duration_ms)
            VALUES (@DemoHash, @Map, @PlayerSteamId, @PlayerName, @StartedAt, @FinishedAt, @HighlightCount, @Status, @Error, @DurationMs);
            SELECT last_insert_rowid();
            """, r);
    }

    public void Update(AnalysisRecord r)
    {
        using var c = db.Open();
        c.Execute("""
            UPDATE analyses SET finished_at=@FinishedAt, highlight_count=@HighlightCount, status=@Status, error=@Error,
              duration_ms=@DurationMs, player_name=@PlayerName, player_steam_id=@PlayerSteamId, map=@Map WHERE id=@Id
            """, r);
    }

    public IReadOnlyList<AnalysisRecord> GetRecent(int limit = 50)
    {
        using var c = db.Open();
        return c.Query<AnalysisRecord>("""
            SELECT id AS Id, demo_hash AS DemoHash, map AS Map, player_steam_id AS PlayerSteamId, player_name AS PlayerName,
                   started_at AS StartedAt, finished_at AS FinishedAt, highlight_count AS HighlightCount, status AS Status,
                   error AS Error, duration_ms AS DurationMs
            FROM analyses ORDER BY id DESC LIMIT @limit
            """, new { limit }).ToList();
    }
}

public sealed class HighlightRepository(AppDatabase db)
{
    public void Replace(string demoHash, ulong? playerSteamId, IEnumerable<Highlight> highlights)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (playerSteamId is { } pid)
            c.Execute("DELETE FROM highlights WHERE demo_hash=@demoHash AND player_steam_id=@pid", new { demoHash, pid = pid.ToString() }, tx);
        else
            c.Execute("DELETE FROM highlights WHERE demo_hash=@demoHash", new { demoHash }, tx);

        foreach (var h in highlights)
        {
            c.Execute("""
                INSERT OR REPLACE INTO highlights (id, demo_hash, player_steam_id, round, score, category, title, data)
                VALUES (@Id, @DemoHash, @pid, @Round, @Score, @cat, @Title, @data)
                """, new { h.Id, h.DemoHash, pid = h.PlayerSteamId.ToString(), h.Round, h.Score, cat = h.Category.ToString(), h.Title, data = JsonSerializer.Serialize(h, Json.Options) }, tx);
        }

        tx.Commit();
    }

    public IReadOnlyList<Highlight> Get(string demoHash, ulong? playerSteamId = null)
    {
        using var c = db.Open();
        var rows = playerSteamId is { } pid
            ? c.Query<string>("SELECT data FROM highlights WHERE demo_hash=@demoHash AND player_steam_id=@pid ORDER BY score DESC", new { demoHash, pid = pid.ToString() })
            : c.Query<string>("SELECT data FROM highlights WHERE demo_hash=@demoHash ORDER BY score DESC", new { demoHash });
        return rows.Select(r => JsonSerializer.Deserialize<Highlight>(r, Json.Options)!).ToList();
    }

    public Highlight? GetById(string id)
    {
        using var c = db.Open();
        var data = c.QueryFirstOrDefault<string>("SELECT data FROM highlights WHERE id=@id", new { id });
        return data is null ? null : JsonSerializer.Deserialize<Highlight>(data, Json.Options);
    }

    public void Update(Highlight h)
    {
        using var c = db.Open();
        c.Execute("UPDATE highlights SET data=@data, score=@Score, title=@Title WHERE id=@Id",
            new { data = JsonSerializer.Serialize(h, Json.Options), h.Score, h.Title, h.Id });
    }
}

public sealed class RenderJobRepository(AppDatabase db) : IRenderJobRepository
{
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<RenderJob>> GetAllAsync()
    {
        using var c = db.Open();
        IReadOnlyList<RenderJob> list = c.Query<string>("SELECT data FROM render_jobs ORDER BY ord")
            .Select(d => JsonSerializer.Deserialize<RenderJob>(d, Json.Options)!).ToList();
        return Task.FromResult(list);
    }

    public Task<RenderJob?> GetAsync(string id)
    {
        using var c = db.Open();
        var data = c.QueryFirstOrDefault<string>("SELECT data FROM render_jobs WHERE id=@id", new { id });
        return Task.FromResult(data is null ? null : JsonSerializer.Deserialize<RenderJob>(data, Json.Options));
    }

    public Task SaveAsync(RenderJob job)
    {
        string data;
        lock (_gate) data = JsonSerializer.Serialize(job, Json.Options);
        using var c = db.Open();
        c.Execute("""
            INSERT INTO render_jobs (id, status, ord, dedupe_key, highlight_id, demo_hash, output_path, created_at, data)
            VALUES (@Id, @status, @Order, @DedupeKey, @HighlightId, @DemoHash, @OutputPath, @created, @data)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status, ord=excluded.ord, dedupe_key=excluded.dedupe_key,
              output_path=excluded.output_path, data=excluded.data
            """, new { job.Id, status = job.Status.ToString(), job.Order, job.DedupeKey, job.HighlightId, job.DemoHash, job.OutputPath, created = job.CreatedAt.ToString("O"), data });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM render_jobs WHERE id=@id", new { id });
        return Task.CompletedTask;
    }
}

/// <summary>Full parsed match data cached as gzip JSON, so re-opening a demo doesn't require re-parsing.</summary>
public sealed class MatchCache(string cacheFolder)
{
    private string PathFor(string hash) => System.IO.Path.Combine(cacheFolder, hash + ".json.gz");

    public bool Exists(string hash) => File.Exists(PathFor(hash));

    public async Task SaveAsync(MatchData match, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheFolder);
        var tmp = PathFor(match.Demo.FileHash) + ".tmp";
        await using (var fs = File.Create(tmp))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            await JsonSerializer.SerializeAsync(gz, match, Json.Options, ct);
        }

        File.Move(tmp, PathFor(match.Demo.FileHash), true);
    }

    public async Task<MatchData?> LoadAsync(string hash, CancellationToken ct)
    {
        var path = PathFor(hash);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var match = await JsonSerializer.DeserializeAsync<MatchData>(gz, Json.Options, ct);
            return match?.SchemaVersion == new MatchData().SchemaVersion ? match : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            File.Delete(path);
            return null;
        }
    }

    public void Delete(string hash)
    {
        if (File.Exists(PathFor(hash))) File.Delete(PathFor(hash));
    }
}
