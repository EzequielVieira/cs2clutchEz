# Contributing to cs2clutchEz

Thanks for helping! Issues and pull requests are welcome in English, Portuguese or Spanish.

## Development setup

- Windows 10/11 x64, .NET 10 SDK, Counter-Strike 2 (only needed to render).
- `./build.ps1 -SkipInstaller` builds, runs the tests and publishes `dist/app` (it downloads FFmpeg into
  `tools/ffmpeg` the first time).
- Open `CS2DemoHighlightGenerator.sln` in Visual Studio 2026 / Rider, or use `dotnet build` / `dotnet test`.

### Isolated profile

Set `CS2CLUTCHEZ_HOME` to an empty folder before starting the app or the CLI and it keeps its settings, database,
logs and clips there instead of `%APPDATA%\cs2clutchEz` and `Documents\cs2clutchEz` — handy to test a build without
touching your regular installation:

```powershell
$env:CS2CLUTCHEZ_HOME = "$env:TEMP\cs2clutchez-dev"
dotnet run --project src/CS2HG.UI
```

## Project layout

| Folder | What lives there |
|---|---|
| `src/CS2HG.Core` | models, settings, contracts, **localization** (`Localization/*.json`) |
| `src/CS2HG.DemoParser` | demo import (.dem/.bz2) and parsing (DemoFile.Net) |
| `src/CS2HG.HighlightDetection` | detectors and highlight composition |
| `src/CS2HG.DemoPlayback` | CS2 control (cfg + console.log + GSI), Game Coordinator |
| `src/CS2HG.VideoCapture` / `VideoProcessing` | window capture and FFmpeg processing |
| `src/CS2HG.RenderingQueue` | persistent render queue |
| `src/CS2HG.Application` | services wiring everything together |
| `src/CS2HG.UI` | WPF app (MVVM) |
| `src/CS2HG.Cli` | `cs2clutchez-cli` |
| `website/` | static site published to GitHub Pages (cs2clutch.ez.api.br) |

More in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/RENDERING.md](docs/RENDERING.md).

## Translations

The app's texts live in `src/CS2HG.Core/Localization/<language>.json` — a flat `"key": "text"` map per language
(`pt-BR`, `en`, `es`). In code use `L.T("key")` / `L.F("key", args)`; in XAML use `{l:Tr key}`.

- Every language must have **the same keys** and **the same `{0}` placeholders** — `LocalizationTests` fails otherwise,
  and it also checks that every key used in the code exists.
- Keep format specifiers (`{0:0.0}`, `{1:P0}`) as they are; numbers are formatted with the selected language's culture.
- To add a language: add `<code>.json` with all keys, register it in `Localizer.Languages`
  (`src/CS2HG.Core/Localization/Localizer.cs`) and in `Localizer.Normalize`, and translate the website
  (add `website/i18n/<code>.json` and the language to the list at the top of `website/build.ps1`).

## Website

`website/build.ps1` turns `website/template.html` and `website/i18n/<language>.json` into one static page per
language (`/` Portuguese, `/en/`, `/es/`) with its own title, description, canonical and hreflang links,
structured data (SoftwareApplication + FAQPage), plus `sitemap.xml` and `robots.txt`, in `website/_site`. The FAQ is
built from the `qN`/`aN` keys. The Pages workflow runs the same script on every push to `website/`.

```powershell
./website/build.ps1      # then serve website/_site with any static file server
```

## Pull requests

- Run `dotnet test` before opening a PR; add tests for new detectors or behaviour.
- Match the surrounding code style (nullable enabled, file-scoped namespaces, short comments explaining *why*).
- Don't commit demos or videos with other players' names unless they come from a public, redistributable source
  (see `tests/TestData/demos/README.md`).

## Releases

Pushing a tag like `v1.1.0` runs `.github/workflows/release.yml`, which builds the MSI and the portable zip and
attaches them to a GitHub Release. Update `<Version>` in `Directory.Build.props`, `$version` in `build.ps1` and
`OutputName` in `installer/CS2HG.Installer.wixproj` first.
