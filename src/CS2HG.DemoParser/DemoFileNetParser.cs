using System.Diagnostics;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using DemoFile;
using DemoFile.Game.Cs;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoParser;

/// <summary>
/// <see cref="IDemoParser"/> backed by DemoFile.Net (MIT, CS2 / Source 2). All library types stay inside
/// this module; the rest of the app only sees <see cref="MatchData"/>.
/// </summary>
public sealed class DemoFileNetParser(ILogger<DemoFileNetParser> logger) : IDemoParser
{
    public string ParserName => "DemoFile.Net 0.44";

    public Task<MatchData> ParseAsync(string demoPath, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var demo = new CsDemoParser();
            var collector = new MatchCollector(demo);
            collector.Attach();

            await using var stream = new FileStream(demoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var reader = DemoFileReader.Create(demo, stream);
            var lastReport = 0f;
            reader.OnProgress = e =>
            {
                if (e.ProgressRatio - lastReport >= 0.01f)
                {
                    lastReport = e.ProgressRatio;
                    progress?.Report(e.ProgressRatio);
                }
            };

            await reader.ReadAllAsync(cancellationToken);
            var match = collector.Build(demoPath);
            progress?.Report(1);
            logger.LogInformation(
                "Parsed {Demo} in {Elapsed:0.0}s: map {Map}, {Rounds} rounds, {Kills} kills, {Players} players",
                Path.GetFileName(demoPath), sw.Elapsed.TotalSeconds, match.Demo.MapName, match.Rounds.Count,
                match.Kills.Count, match.Players.Count);
            return match;
        }, cancellationToken);

    /// <summary>Accumulates game events while the demo is read.</summary>
    private sealed class MatchCollector(CsDemoParser demo)
    {
        private readonly Dictionary<ulong, PlayerInfo> _players = [];
        private readonly List<RoundInfo> _rounds = [];
        private readonly List<KillEvent> _kills = [];
        private readonly List<DamageEvent> _damages = [];
        private readonly List<GrenadeEvent> _grenades = [];
        private readonly List<BombEvent> _bombs = [];
        private readonly HashSet<ulong> _victimsThisRound = [];
        private readonly Dictionary<ulong, int> _healthThisRound = [];
        private readonly List<string> _warnings = [];
        private readonly DemoInfo _info = new();
        private RoundInfo? _current;
        private bool _sawBots;
        private int _lastTick;
        private int? _gameTickOffset;

        private int Tick => demo.CurrentDemoTick.Value;

        private bool IsWarmup => demo.GameRules?.WarmupPeriod == true;

        public void Attach()
        {
            demo.DemoEvents.DemoFileHeader += h =>
            {
                _info.MapName = h.MapName;
                _info.ServerName = h.ServerName;
                _info.ClientName = h.ClientName;
                _info.BuildNumber = h.BuildNum;
                _info.NetworkProtocol = h.NetworkProtocol;
            };
            demo.DemoEvents.DemoFileInfo += fi =>
            {
                if (fi.PlaybackTicks > 0) _info.TotalDemoTicks = fi.PlaybackTicks;
                if (fi.PlaybackTime > 0) _info.DurationSeconds = fi.PlaybackTime;
            };
            demo.PacketEvents.SvcServerInfo += si =>
            {
                _info.IsGotv = si.IsHltv;
                if (!string.IsNullOrEmpty(si.MapName)) _info.MapName = si.MapName;
                if (si.TickInterval > 0) _info.TickRate = (int)Math.Round(1.0 / si.TickInterval);
            };
            demo.PacketEvents.NetSetConVar += msg =>
            {
                if (msg.Convars is null) return;
                foreach (var cvar in msg.Convars.Cvars)
                {
                    switch (cvar.Name)
                    {
                        case "mp_maxrounds" when int.TryParse(cvar.Value, out var mr):
                            _info.MaxRounds = mr;
                            break;
                        case "game_mode":
                            _info.GameMode = cvar.Value;
                            break;
                    }
                }
            };

            var ev = demo.Source1GameEvents;
            ev.RoundAnnounceMatchStart += _ => OnMatchStart();
            ev.RoundStart += _ => OnRoundStart();
            ev.RoundFreezeEnd += _ => OnFreezeEnd();
            ev.RoundEnd += e => OnRoundEnd(e.Winner, e.Reason);
            ev.RoundOfficiallyEnded += _ =>
            {
                if (_current is { OfficialEndTick: 0 }) _current.OfficialEndTick = Tick;
            };
            ev.PlayerDeath += OnPlayerDeath;
            ev.PlayerHurt += OnPlayerHurt;
            ev.HegrenadeDetonate += e => AddGrenade(e.Player, GrenadeType.HighExplosive, e.X, e.Y, e.Z);
            ev.FlashbangDetonate += e => AddGrenade(e.Player, GrenadeType.Flashbang, e.X, e.Y, e.Z);
            ev.SmokegrenadeDetonate += e => AddGrenade(e.Player, GrenadeType.Smoke, e.X, e.Y, e.Z);
            ev.MolotovDetonate += e => AddGrenade(e.Player, GrenadeType.Molotov, e.X, e.Y, e.Z);
            ev.DecoyStarted += e => AddGrenade(e.PlayerPawn?.Controller, GrenadeType.Decoy, e.X, e.Y, e.Z);
            ev.BombBeginplant += e => AddBomb(BombEventType.BeginPlant, e.Player);
            ev.BombPlanted += e => AddBomb(BombEventType.Planted, e.Player);
            ev.BombBegindefuse += e => AddBomb(BombEventType.BeginDefuse, e.Player, e.Haskit);
            ev.BombDefused += e => AddBomb(BombEventType.Defused, e.Player);
            ev.BombExploded += e => AddBomb(BombEventType.Exploded, e.Player);
        }

        private ulong IdOf(CCSPlayerController? c)
        {
            if (c is null) return 0;
            if (c.SteamID != 0) return c.SteamID;
            // Bots have no SteamID: derive a stable synthetic id from the entity slot.
            if (TeamOf(c).IsPlaying()) _sawBots = true;
            return 1000UL + (ulong)c.EntityIndex.Value;
        }

        private static Team TeamOf(CCSPlayerController? c) => c is null ? Team.Unassigned : (Team)(int)c.CSTeamNum;

        private static Vec3? Pos(CCSPlayerPawn? pawn)
        {
            if (pawn is null) return null;
            var o = pawn.Origin;
            return new Vec3(o.X, o.Y, o.Z);
        }

        private PlayerInfo Touch(CCSPlayerController c)
        {
            var id = IdOf(c);
            if (!_players.TryGetValue(id, out var p))
            {
                p = new PlayerInfo { SteamId = id, IsBot = c.SteamID == 0 };
                _players[id] = p;
            }

            if (!string.IsNullOrWhiteSpace(c.PlayerName)) p.Name = c.PlayerName;
            var clan = c.Clan.ToString();
            if (!string.IsNullOrWhiteSpace(clan)) p.ClanTag = clan;
            var team = TeamOf(c);
            if (team.IsPlaying())
            {
                p.Team = team;
                if (p.StartingTeam == Team.Unassigned) p.StartingTeam = team;
            }

            return p;
        }

        private void OnMatchStart()
        {
            // Anything recorded before the match went live (knife round, restarts) is discarded.
            // The announcement can arrive during the first live round, so that round is kept.
            var cutoff = _current?.StartTick ?? Tick;
            if (!_rounds.Any(r => r.StartTick < cutoff)) return;
            _rounds.RemoveAll(r => r.StartTick < cutoff);
            _kills.RemoveAll(k => k.Tick < cutoff);
            _damages.RemoveAll(k => k.Tick < cutoff);
            _grenades.RemoveAll(k => k.Tick < cutoff);
            _bombs.RemoveAll(k => k.Tick < cutoff);
        }
        private void OnRoundStart()
        {
            if (IsWarmup) return;
            CloseCurrentRound();
            _gameTickOffset ??= (int)demo.CurrentGameTick.Value - Tick;

            var number = (demo.GameRules?.TotalRoundsPlayed ?? _rounds.Count) + 1;
            var existing = _rounds.Where(r => r.Number == number).ToList();
            if (existing.Count > 0)
            {
                // Round restarted (mp_restartgame / backup restore): drop the previous attempt.
                var ticks = existing.Select(r => r.StartTick).ToHashSet();
                _rounds.RemoveAll(r => ticks.Contains(r.StartTick) && r.Number == number);
                var first = existing.Min(r => r.StartTick);
                _kills.RemoveAll(k => k.Round >= number && k.Tick >= first);
                _damages.RemoveAll(k => k.Round >= number && k.Tick >= first);
                _grenades.RemoveAll(k => k.Round >= number && k.Tick >= first);
                _bombs.RemoveAll(k => k.Round >= number && k.Tick >= first);
                _rounds.RemoveAll(r => r.Number > number);
            }

            _current = new RoundInfo { Number = number, StartTick = Tick };
            _rounds.Add(_current);
            _victimsThisRound.Clear();
            _healthThisRound.Clear();
        }

        private void OnFreezeEnd()
        {
            if (IsWarmup || _current is null) return;
            _current.FreezeEndTick = Tick;
            _current.CtPlayers.Clear();
            _current.TPlayers.Clear();
            foreach (var c in demo.Players)
            {
                var team = TeamOf(c);
                if (!team.IsPlaying()) continue;
                var p = Touch(c);
                if (!c.PawnIsAlive) continue;
                (team == Team.CounterTerrorist ? _current.CtPlayers : _current.TPlayers).Add(p.SteamId);
            }
        }

        private void OnRoundEnd(int winner, int reason)
        {
            if (IsWarmup || _current is null || _current.EndTick != 0) return;
            _current.EndTick = Tick;
            _current.Winner = (Team)winner;
            _current.Reason = Enum.IsDefined(typeof(RoundEndReason), reason) ? (RoundEndReason)reason : RoundEndReason.Unknown;
            var round = _current;
            // Team scores are networked a few ticks after round_end.
            demo.CreateTimer(new DemoTick(Tick + 8), () =>
            {
                round.ScoreCtAfter = demo.TeamCounterTerrorist?.Score ?? 0;
                round.ScoreTAfter = demo.TeamTerrorist?.Score ?? 0;
            });
        }

        private void CloseCurrentRound()
        {
            if (_current is null) return;
            if (_current.EndTick == 0) _current.EndTick = Tick;
            if (_current.OfficialEndTick == 0) _current.OfficialEndTick = Tick;
            if (_current.FreezeEndTick == 0) _current.FreezeEndTick = _current.StartTick;
        }

        private List<ulong> Alive(Team team)
        {
            var list = new List<ulong>();
            foreach (var c in demo.Players)
            {
                if (TeamOf(c) != team || !c.PawnIsAlive) continue;
                var id = IdOf(c);
                if (!_victimsThisRound.Contains(id)) list.Add(id);
            }

            return list;
        }

        private void OnPlayerDeath(Source1PlayerDeathEvent e)
        {
            _lastTick = Tick;
            if (IsWarmup || _current is null || e.Player is null) return;

            var victim = Touch(e.Player);
            var attacker = e.Attacker is null ? null : Touch(e.Attacker);
            var assister = e.Assister is null ? null : Touch(e.Assister);
            _victimsThisRound.Add(victim.SteamId);

            var kill = new KillEvent
            {
                Tick = Tick,
                Round = _current.Number,
                AttackerSteamId = attacker?.SteamId,
                AttackerName = attacker?.Name ?? "World",
                AttackerTeam = TeamOf(e.Attacker),
                VictimSteamId = victim.SteamId,
                VictimName = victim.Name,
                VictimTeam = TeamOf(e.Player),
                AssisterSteamId = assister?.SteamId,
                AssisterName = assister?.Name,
                AssistedFlash = e.Assistedflash,
                Weapon = NormalizeWeapon(e.Weapon),
                Headshot = e.Headshot,
                NoScope = e.Noscope,
                Penetrated = e.Penetrated,
                ThroughSmoke = e.Thrusmoke,
                AttackerBlind = e.Attackerblind,
                AttackerInAir = e.Attackerinair,
                Distance = e.Distance,
                AttackerPosition = Pos(e.AttackerPawn),
                VictimPosition = Pos(e.PlayerPawn),
                AliveCtAfter = Alive(Team.CounterTerrorist),
                AliveTAfter = Alive(Team.Terrorist),
            };
            _kills.Add(kill);
        }

        private void OnPlayerHurt(Source1PlayerHurtEvent e)
        {
            if (IsWarmup || _current is null || e.Player is null) return;
            var victim = Touch(e.Player);
            var attacker = e.Attacker is null ? null : Touch(e.Attacker);
            var before = _healthThisRound.TryGetValue(victim.SteamId, out var h) ? h : 100;
            var dealt = Math.Clamp(Math.Min(e.DmgHealth, before), 0, 100);
            _healthThisRound[victim.SteamId] = Math.Max(0, e.Health);
            _damages.Add(new DamageEvent
            {
                Tick = Tick,
                Round = _current.Number,
                AttackerSteamId = attacker?.SteamId,
                AttackerTeam = TeamOf(e.Attacker),
                VictimSteamId = victim.SteamId,
                VictimTeam = TeamOf(e.Player),
                Weapon = NormalizeWeapon(e.Weapon),
                HealthDamage = dealt,
                ArmorDamage = e.DmgArmor,
                HitGroup = e.Hitgroup,
                VictimHealthAfter = e.Health,
            });
        }

        private void AddGrenade(CCSPlayerController? thrower, GrenadeType type, float x, float y, float z)
        {
            if (IsWarmup || _current is null) return;
            var p = thrower is null ? null : Touch(thrower);
            _grenades.Add(new GrenadeEvent
            {
                Tick = Tick,
                Round = _current.Number,
                ThrowerSteamId = p?.SteamId,
                ThrowerName = p?.Name ?? "",
                Type = type,
                Position = new Vec3(x, y, z),
            });
        }

        private void AddBomb(BombEventType type, CCSPlayerController? player, bool hasKit = false)
        {
            if (IsWarmup || _current is null) return;
            var p = player is null ? null : Touch(player);
            string? site = null;
            if (type is BombEventType.Planted or BombEventType.BeginPlant && player?.PlayerPawn is { } pawn)
                site = ResolveSite(pawn.Origin);

            _bombs.Add(new BombEvent
            {
                Tick = Tick,
                Round = _current.Number,
                Type = type,
                PlayerSteamId = p?.SteamId,
                PlayerName = p?.Name ?? "",
                Site = site,
                HasKit = hasKit,
            });

            switch (type)
            {
                case BombEventType.Planted:
                    _current.BombPlantTick = Tick;
                    _current.BombSite = site;
                    break;
                case BombEventType.Defused:
                    _current.BombDefuseTick = Tick;
                    break;
                case BombEventType.Exploded:
                    _current.BombExplodeTick = Tick;
                    break;
            }
        }

        private string? ResolveSite(Vector origin)
        {
            var res = demo.PlayerResource;
            if (res is null) return null;
            var a = res.BombsiteCenterA;
            var b = res.BombsiteCenterB;
            if (a.X == 0 && a.Y == 0 && b.X == 0 && b.Y == 0) return null;
            var p = new Vec3(origin.X, origin.Y, origin.Z);
            var da = p.DistanceTo(new Vec3(a.X, a.Y, a.Z));
            var db = p.DistanceTo(new Vec3(b.X, b.Y, b.Z));
            return da <= db ? "A" : "B";
        }

        public static string NormalizeWeapon(string? weapon)
        {
            if (string.IsNullOrWhiteSpace(weapon)) return "world";
            var w = weapon.Trim().ToLowerInvariant();
            if (w.StartsWith("weapon_", StringComparison.Ordinal)) w = w["weapon_".Length..];
            return w;
        }

        public MatchData Build(string demoPath)
        {
            CloseCurrentRound();

            // Final roster / names from the last known controller state.
            foreach (var c in demo.PlayersIncludingDisconnected)
            {
                if (TeamOf(c).IsPlaying() || _players.ContainsKey(IdOf(c))) Touch(c);
            }

            var tickRate = _info.TickRate > 0 ? _info.TickRate : CsDemoParser.TickRate;
            _info.TickRate = tickRate;
            if (_info.TotalDemoTicks <= 0) _info.TotalDemoTicks = Math.Max(Tick, demo.TickCount.Value);
            if (_info.DurationSeconds <= 0) _info.DurationSeconds = (double)_info.TotalDemoTicks / tickRate;
            _info.GameTickOffset = _gameTickOffset ?? 0;
            _info.FilePath = demoPath;
            _info.FileName = Path.GetFileName(demoPath);
            _info.FileSize = new FileInfo(demoPath).Length;

            var lastTick = _info.TotalDemoTicks;
            foreach (var r in _rounds)
            {
                if (r.EndTick == 0) r.EndTick = lastTick;
                if (r.OfficialEndTick == 0 || r.OfficialEndTick < r.EndTick)
                    r.OfficialEndTick = Math.Min(lastTick, r.EndTick + 5 * tickRate);
            }

            // Final scores: last round's networked score, else team entities.
            var lastRound = _rounds.LastOrDefault();
            var scoreCt = demo.TeamCounterTerrorist?.Score ?? lastRound?.ScoreCtAfter ?? 0;
            var scoreT = demo.TeamTerrorist?.Score ?? lastRound?.ScoreTAfter ?? 0;
            if (lastRound is not null && lastRound.ScoreCtAfter == 0 && lastRound.ScoreTAfter == 0)
            {
                lastRound.ScoreCtAfter = scoreCt;
                lastRound.ScoreTAfter = scoreT;
            }

            if (_sawBots) _warnings.Add("warn.bots");
            if (_rounds.Count == 0) _warnings.Add("warn.noRounds");

            var match = new MatchData
            {
                Demo = _info,
                Players = _players.Values.Where(p => p.StartingTeam.IsPlaying() || p.Team.IsPlaying()).OrderBy(p => p.Team).ThenBy(p => p.Name).ToList(),
                Rounds = _rounds.OrderBy(r => r.Number).ToList(),
                Kills = _kills.OrderBy(k => k.Tick).ToList(),
                Damages = _damages,
                Grenades = _grenades,
                BombEvents = _bombs,
                TeamNameCt = demo.TeamCounterTerrorist?.ClanTeamname is { Length: > 0 } ct ? ct : "CT",
                TeamNameT = demo.TeamTerrorist?.ClanTeamname is { Length: > 0 } t ? t : "T",
                FinalScoreCt = scoreCt,
                FinalScoreT = scoreT,
                Warnings = _warnings,
            };
            BasicStats.Compute(match);
            return match;
        }
    }
}

/// <summary>Per-player counters derivable directly from events.</summary>
public static class BasicStats
{
    public static void Compute(MatchData match)
    {
        var byId = match.Players.ToDictionary(p => p.SteamId);
        foreach (var p in match.Players)
        {
            p.Stats = new PlayerStats
            {
                RoundsPlayed = match.Rounds.Count(r => r.CtPlayers.Contains(p.SteamId) || r.TPlayers.Contains(p.SteamId)),
            };
        }

        foreach (var k in match.Kills)
        {
            if (byId.TryGetValue(k.VictimSteamId, out var victim)) victim.Stats.Deaths++;
            if (k.AssisterSteamId is { } a && byId.TryGetValue(a, out var assister) && k.IsEnemyKill) assister.Stats.Assists++;
            if (k.AttackerSteamId is not { } att || !byId.TryGetValue(att, out var attacker)) continue;
            if (k.IsTeamKill) { attacker.Stats.TeamKills++; continue; }
            if (k.IsSuicide) continue;
            attacker.Stats.Kills++;
            if (k.Headshot) attacker.Stats.Headshots++;
        }

        foreach (var round in match.Kills.GroupBy(k => k.Round))
        {
            var first = round.Where(k => k.IsEnemyKill).OrderBy(k => k.Tick).FirstOrDefault();
            if (first?.AttackerSteamId is { } fk && byId.TryGetValue(fk, out var fp)) fp.Stats.FirstKills++;
            foreach (var g in round.Where(k => k.IsEnemyKill).GroupBy(k => k.AttackerSteamId!.Value))
            {
                if (!byId.TryGetValue(g.Key, out var p)) continue;
                switch (g.Count())
                {
                    case 2: p.Stats.TwoKills++; break;
                    case 3: p.Stats.ThreeKills++; break;
                    case 4: p.Stats.FourKills++; break;
                    case >= 5: p.Stats.Aces++; break;
                }
            }
        }

        foreach (var d in match.Damages)
        {
            if (d.AttackerSteamId is not { } att || !byId.TryGetValue(att, out var p)) continue;
            if (d.AttackerTeam == d.VictimTeam) continue;
            p.Stats.Damage += d.HealthDamage;
            if (d.Weapon is "hegrenade" or "inferno" or "molotov" or "incgrenade") p.Stats.UtilityDamage += d.HealthDamage;
        }

        foreach (var b in match.BombEvents)
        {
            if (b.PlayerSteamId is not { } id || !byId.TryGetValue(id, out var p)) continue;
            if (b.Type == BombEventType.Planted) p.Stats.BombPlants++;
            if (b.Type == BombEventType.Defused) p.Stats.BombDefuses++;
        }

        foreach (var g in match.Grenades)
        {
            if (g.ThrowerSteamId is { } id && byId.TryGetValue(id, out var p)) p.Stats.GrenadesThrown++;
        }
    }
}
