<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-white.svg">
    <img src="docs/images/logo-dark.svg" width="560" alt="cs2clutchEz">
  </picture>
</h1>

<p align="center">
  <a href="README.md">English</a> · <a href="README.pt-BR.md">Português</a> · <b>Español</b><br>
  <a href="https://cs2clutch.ez.api.br">cs2clutch.ez.api.br</a> ·
  <a href="https://github.com/EzequielVieira/cs2clutchEz/releases/latest">Descargar</a> ·
  <a href="LICENSE">Licencia MIT</a>
</p>

App para Windows, **gratis y de código abierto**, que lee **demos ya grabadas de Counter-Strike 2** — un archivo
`.dem` / `.dem.bz2` o simplemente el **código para compartir de la partida** (`CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx`) —,
encuentra las mejores jugadas de un jugador y convierte cada una en un **video MP4**, usando el propio CS2 para
reproducir la demo y grabando la ventana del juego.

> No es un grabador de partidas en vivo: solo funciona con partidas ya grabadas en demo.

La interfaz está en **portugués (Brasil)**, **inglés** y **español** y cambia al instante (Configuración → Idioma;
"Automático" sigue el idioma de Windows).

## Cómo se usa

1. **Pega el código para compartir** (CS2 → **Ver** → **Tus partidas** → copiar código) o importa/arrastra una demo.
   La app descarga la demo de los servidores de Valve mediante el Steam abierto (CS2 debe estar cerrado durante la
   consulta). Las demos de Matchmaking/Premier están disponibles unos 30 días.
2. La demo se analiza **sin abrir el juego**: jugadores, rondas, kills, daño, bomba, etc.
3. Elige al jugador y haz clic en **Buscar highlights**: aces, clutches 1v1…1v5, multi-kills, no-scopes,
   wallbangs, kills con cuchillo/Zeus/AWP, retakes, defuses decisivos, rondas decisivas, mucho daño…
4. Marca los highlights, elige POV, resolución (720p–4K), FPS (30–120), códec (H.264/H.265), bitrate y segundos
   antes/después.
5. La cola abre CS2, va al tick exacto, verifica el POV, graba la ventana en tiempo real y exporta el MP4. Al terminar
   cierra CS2 y restaura tu configuración (video, convars, binds).

## Requisitos e instalación

- Windows 10 2004+ u 11 (64 bits), Counter-Strike 2 instalado con Steam y la sesión iniciada. GPU recomendada
  (NVENC / AMF / Quick Sync); sin GPU se usa x264/x265.
- Descarga el **instalador MSI** o el **zip portátil** en
  [Releases](https://github.com/EzequielVieira/cs2clutchEz/releases/latest). FFmpeg ya viene incluido.
- Los videos se guardan en `Documentos\cs2clutchEz\Highlights\<Jugador>\<Partida>\<Categoría>`.

**Limitaciones:** la grabación es en tiempo real (un clip de 30 s tarda ~30 s + la carga); no uses el ratón ni el
teclado mientras renderiza, porque CS2 tiene que estar al frente; el audio es el loopback del dispositivo
predeterminado; no se inyecta nada en el juego.

## Compilar

```powershell
# .NET 10 SDK
dotnet build CS2DemoHighlightGenerator.sln -c Release
dotnet test tests/CS2HG.Tests/CS2HG.Tests.csproj
./build.ps1            # dist/app, zip portátil y MSI
```

Detalles técnicos (renderizado, detección, puntos, línea de comandos) en el [README en inglés](README.md).
Traducciones y contribuciones: [CONTRIBUTING.md](CONTRIBUTING.md).

## Licencia

[MIT](LICENSE). Componentes de terceros: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). El FFmpeg incluido es una
compilación **GPL** distribuida como programa aparte. Counter-Strike 2 es una marca de Valve; este proyecto no está
afiliado a Valve.
