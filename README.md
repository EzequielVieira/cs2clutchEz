<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-white.svg">
    <img src="docs/images/logo-dark.svg" width="560" alt="cs2clutchEz">
  </picture>
</h1>

<p align="center">
  <b>English</b> · <a href="README.pt-BR.md">Português</a> · <a href="README.es.md">Español</a><br>
  <a href="https://cs2clutch.ez.api.br">cs2clutch.ez.api.br</a> ·
  <a href="https://github.com/EzequielVieira/cs2clutchEz/releases/latest">Download</a> ·
  <a href="LICENSE">MIT license</a>
</p>

Free and open-source Windows app that reads **recorded Counter-Strike 2 demos** — a `.dem` / `.dem.bz2` file, or
just the **match share code** (`CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx`) — finds a player's best moments and turns
each one into an **MP4 video**, using CS2 itself to play the demo and recording the game window.

> It is not a live gameplay recorder: it only works with matches that were already recorded as demos.

The interface is available in **Português (Brasil)**, **English** and **Español** and switches live
(Settings → Language; "Automatic" follows the Windows display language).

## What it does

1. **Paste the share code** (or import / drop a demo).
   - The app looks for the demo on disk (CS2's `replays` folder and earlier downloads); otherwise it asks
     **Valve's Game Coordinator** through the Steam client that is already running and signed in (no password
     needed) and downloads the `.dem.bz2` from Valve's replay servers.
   - **Ctrl+V** on any screen opens the code in the clipboard.
   - `cs2clutchez://CSGO-...` links (for example the button on the website) open the match in the app.
2. The demo is parsed **without opening the game** (DemoFile.Net): players, SteamIDs, teams, map, rounds, ticks,
   kills, deaths, assists, headshots, weapons, damage, plant/defuse, round winners, positions, grenades.
3. Pick the player (the signed-in Steam user is suggested) and click **Find highlights**.
4. Independent detectors find and rank: First Blood, Double/Triple/Quad Kill, Ace, Headshot, No-scope, Wallbang,
   knife/Zeus/AWP kills, through smoke, blind, Clutch 1v1…1v5, Retake, clutch defuse, decisive round, kill streak,
   quick kills, high damage and utility damage.
5. Tick the highlights and choose POV, resolution, FPS, codec, bitrate and how many seconds before/after.
6. The queue opens CS2, seeks to the exact tick, sets and **verifies** the POV, records the window in real time and
   exports the MP4 (title, player and round overlay, fades and crossfades between cuts).
7. Finished videos are listed in **Clips**: watch, open the folder, copy to share, or join several into a montage.

## Share codes

| Where to get it | CS2 → **Watch** → **Your Matches** → match → copy the share code |
|---|---|
| Format | `CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx` (also found inside `steam://` or `cs2clutchez://` links) |
| Requirements | Steam open and signed in; **CS2 closed** during the lookup (the app uses the game's own connection to Valve) |
| Availability | Valve keeps Matchmaking/Premier demos for ~30 days; after that the code has no demo |
| During the lookup | Steam shows you as "playing CS2" for a few seconds |

The Home screen also lists **"Your CS2 matches"**: the matches CS2 already downloaded, each with its code (read from
the `.dem.info` file the game writes next to the demo) — one click opens it or copies the code.

How it works: the code packs *matchId*, *outcomeId* and *token*; the app sends `MatchListRequestFullGameInfo` to the
Game Coordinator through the `steam_api64.dll` of the CS2 installation and receives
`http://replayNNN.valve.net/730/<outcomeId>_<id>.dem.bz2`. Valve no longer answers the "recent matches" request
(`MatchListRequestRecentUserGames`) for external clients, so that list is built from CS2's local files.

## Requirements

| Item | Details |
|---|---|
| Windows | 10 2004+ or 11, 64-bit (Windows Graphics Capture) |
| Counter-Strike 2 | installed through Steam, with Steam signed in |
| GPU | recommended (NVENC / AMF / Quick Sync). Without one the app uses x264/x265 on the CPU |
| FFmpeg | 8.1+ with the `gfxcapture` filter. The installer ships FFmpeg 9.0 (BtbN GPL build) |
| .NET | nothing to install (self-contained). To build: .NET 10 SDK (LTS) |

## Install

Get the latest version from [Releases](https://github.com/EzequielVieira/cs2clutchEz/releases/latest):

- **MSI installer** — installs to `Program Files\cs2clutchEz`, adds Start menu / desktop shortcuts, registers the
  `cs2clutchez://` protocol and includes FFmpeg.
- **Portable zip** — extract anywhere and run `cs2clutchEz.exe`.

On first run the app creates `Documents\cs2clutchEz\`:

```
cs2clutchEz/
├── Demos/          demos downloaded by share code and decompressed .bz2 files (cache)
├── Highlights/
│   └── <Player>/<Map date score>/Ace | Clutch | MultiKill | Kills | Objective | Utility
├── Thumbnails/
├── Logs/           cs2clutchez-YYYYMMDD.log (14 days)
├── Database/       cs2hg.db (SQLite) + cache/ (analysed matches as gzipped JSON)
└── Work/           raw captures while a job renders (deleted once the MP4 is exported)
```

### "Open in cs2clutchEz" links

With the app installed, any page can open a match:

```html
<a href="cs2clutchez://CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx">Open in cs2clutchEz</a>
```

The website also accepts `https://cs2clutch.ez.api.br/?code=CSGO-...` to prefill its share-code box.

## How rendering works (and why)

A demo is not a video: it is replay data that has to be played by CS2. What was verified on current CS2
(build 14194, Nov 2026) and shaped the approach:

| Approach | Status in current CS2 |
|---|---|
| `startmovie` (frame-by-frame recording) | **gone** from CS2 — tested, no file is written |
| Remote console `-netconport` | **ignored** by CS2 — no port is opened |
| Injection (HLAE etc.) | avoided on purpose (needs `-insecure`, risk, maintenance) |
| Cfg run by a key + `console.log` + GSI | **works** — this is what the app uses |
| Windows Graphics Capture (FFmpeg `gfxcapture`) | **works**, records the window even with others on top |

Pipeline for each highlight:

1. The action ticks become segments (5/10/15/20/30 s before and 5/10/15/20 s after). Distant actions become separate
   cuts joined with a crossfade.
2. The app starts CS2 through Steam with `-condebug +exec cs2hg/boot`, in a borderless window at the capture
   resolution.
3. The demo is hard-linked into `csgo/cs2hg/demos` and opened with `playdemo` from the console.
4. For each segment: `demo_gototick` → `demo_pause` → wait for `console.log` to confirm *"Demo Skipping finished at
   tick N"* → `spec_player` + `spec_mode` → **confirm the observed player through Game State Integration** → start
   the capture → `demo_resume` → record in real time → `demo_pause`.
5. Segments are cut at the exact `demo_resume` instant, assembled into a master clip and exported.
6. When the queue finishes the app closes CS2 and **restores the user's settings** (video, convars, binds).

Details in [docs/RENDERING.md](docs/RENDERING.md) (Portuguese).

**Honest limitations**

- Recording is **real time**: a 30 s highlight takes ~30 s plus loading.
- CS2 must stay **in front** while it receives commands (`SendInput`). Don't use the mouse/keyboard while rendering.
  Windows on top of the game don't show up in the video (WGC).
- Audio is the **default device loopback** (WASAPI): sounds from other programs are recorded too.
- The first `playdemo` is typed into the developer console (binds don't work in the main menu).
- If you opened CS2 yourself, it can't be controlled: the app offers to restart it.
- Downloading by share code needs CS2 closed; if the queue is rendering, wait for it to finish.
- Opening a `cs2clutchez://` link while the app is open starts a second window (no single-instance mode yet).

## Detection

Each rule is an independent detector (`IHighlightDetector`) that only uses **fields present in the demo**. Each event
adds points; the total ranks the highlights (thresholds such as "high damage" can be changed in Settings).

| Event | Points | How the demo proves it |
|---|---:|---|
| Kill | 10 | `player_death` of an enemy (team kills / suicides ignored) |
| Headshot | +10 | `player_death.headshot` |
| Double / Triple / Quad / Ace | +20 / +40 / +60 / +100 | 2/3/4/5+ enemy kills in the round |
| Clutch 1v1 … 1v5 | +15 / +30 / +50 / +80 / +100 | last one alive with N enemies alive, and the team won |
| Knife kill | +30 | weapon `knife*` / `bayonet` |
| No-scope | +30 | `player_death.noscope` |
| Decisive round | +25 | round that won the match or saved a match point (MR12 + MR3 OT), with participation |
| First Blood | +10 | first enemy kill of the round |
| Wallbang | +20 | `player_death.penetrated > 0` (*the game doesn't tell walls from thin objects*) |
| Through smoke / blind | +15 / +15 | `thrusmoke` / `attackerblind` |
| Zeus / AWP kill | +25 / +5 | weapon `taser` / `awp` |
| Retake | +20 | CT killed after the plant and the CTs won the round |
| Clutch defuse | +30 | defuse with enemies alive, ≤3 s on the timer, or as the last CT alive |
| Kill streak | +15 | 3+ kills within 10 s (configurable) |
| Quick kills | +15 | 2+ kills at most 1.5 s apart (configurable) |
| High damage | +15 | ≥300 health damage in the round |
| Utility damage | +15 | HE (0.5 s) or molotov (7 s) with ≥80 damage |

## POV

| Option | Behaviour |
|---|---|
| Selected player | first person of the analysed player (default) |
| Killer / Victim | first person of the killer / victim of each cut's first kill |
| Specific player | first person of any player in the match |
| Third person | third-person camera following the player (`spec_mode 3`) |
| Director camera | CS2's automatic director (`spec_mode 5`) |

## Command line

`cs2clutchez-cli.exe` uses the same services as the app (output in English; app messages follow `--lang`):

```
cs2clutchez-cli sharecode CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx --top 10
cs2clutchez-cli sharecode CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx --render --top 3 --res 1080p
cs2clutchez-cli resolve   CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx      # only prints Valve's demo URL
cs2clutchez-cli matches                                            # CS2 matches with their codes
cs2clutchez-cli analyze "C:\demos\match.dem" --player "PlayerName" --top 20 --lang en
cs2clutchez-cli render  "C:\demos\match.dem.bz2" --rounds 3,5,8,12 --codec h265 --pov victim
cs2clutchez-cli probe | queue | resume | install-ffmpeg | restore-config
```

## Build, test and package

```powershell
# .NET 10 SDK
dotnet build CS2DemoHighlightGenerator.sln -c Release
dotnet test tests/CS2HG.Tests/CS2HG.Tests.csproj
./build.ps1            # dist/app (self-contained win-x64), portable zip and MSI (WiX 5)
```

The tests cover share codes (checked against Valve's real answer), `.dem`/`.dem.bz2` import, players, kills,
multi-kills, aces, clutches, scoring, POV, ticks and windows, export/rendering with real FFmpeg (H.264 and H.265),
the queue (pause, cancel, failure, retry, recovery), SQLite, and the translations (every key used in the code exists
in all three languages with the same placeholders).

Tagging `v*` runs the release workflow (MSI + portable zip attached to a GitHub Release); `website/` is published to
GitHub Pages. See [CONTRIBUTING.md](CONTRIBUTING.md) for translations and development tips.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Close CS2 before looking up matches by share code" | Close the game (or wait for the render queue to finish) |
| "The demo is no longer on Valve's servers" | The match is older than ~30 days; the demo can't be recovered |
| "CS2 is open but was not started by cs2clutchEz" | Close CS2 or accept the suggested restart |
| POV not verified | GSI port in use (change it in Settings) — the video is still produced |
| CS2 settings changed after a crash | Settings → **Restore the original CS2 configuration** |

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The bundled FFmpeg is a
**GPL** build distributed as a separate program. Counter-Strike 2 is a trademark of Valve; this project is not
affiliated with Valve.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — modules, dependencies and data flow
- [docs/RENDERING.md](docs/RENDERING.md) — CS2 control protocol, sync and recovery
