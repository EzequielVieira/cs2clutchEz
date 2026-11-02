<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-white.svg">
    <img src="docs/images/logo-dark.svg" width="560" alt="cs2clutchEz">
  </picture>
</h1>

<p align="center">
  <a href="README.md">English</a> · <b>Português</b> · <a href="README.es.md">Español</a><br>
  <a href="https://cs2clutch.ez.com.br">cs2clutch.ez.com.br</a> ·
  <a href="https://github.com/EzequielVieira/cs2clutchEz/releases/latest">Download</a> ·
  <a href="LICENSE">Licença MIT</a>
</p>

App para Windows, **gratuito e open source**, que lê **demos já gravadas do Counter-Strike 2** — por arquivo
(`.dem` / `.dem.bz2`) ou só **colando o código de compartilhamento da partida**
(`CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx`) —, encontra os melhores momentos de um jogador e gera um **vídeo MP4** de
cada highlight, usando o próprio CS2 para reproduzir a demo e gravando a janela do jogo.

> Não é um gravador de gameplay ao vivo: funciona exclusivamente com partidas já gravadas em demo.

A interface está em **português (Brasil)**, **inglês** e **espanhol** e troca na hora (Configurações → Idioma;
"Automático" segue o idioma do Windows).

## O que ele faz

1. Você **cola o código de compartilhamento** (ou importa/arrasta uma demo).
   - O app procura a demo no disco (pasta `replays` do CS2 e downloads anteriores); se não achar, pergunta ao
     **Game Coordinator da Valve** pelo Steam que já está aberto e logado (sem pedir senha) e baixa o `.dem.bz2`
     dos servidores de replay da Valve.
   - **Ctrl+V** em qualquer tela abre o código que estiver na área de transferência.
   - Links `cs2clutchez://CSGO-...` (por exemplo, o botão do site) abrem a partida direto no app.
2. O app lê a demo **sem abrir o jogo** (DemoFile.Net): jogadores, SteamIDs, times, mapa, rounds, ticks, kills,
   mortes, assistências, headshots, armas, dano, plant/defuse, vitória do round, posições, granadas.
3. Você escolhe o jogador (o usuário logado no Steam é sugerido) e clica em **Encontrar highlights**.
4. Detectores independentes encontram e ordenam: First Blood, Double/Triple/Quadra Kill, Ace, Headshot, No-scope,
   Wallbang, kill com faca/Zeus/AWP, através da smoke, cego, Clutch 1v1…1v5, Retake, Defuse decisivo, Round
   decisivo, multikill em sequência, eliminações rápidas, muito dano e dano de utilitária.
5. Você marca os highlights e escolhe POV, resolução, FPS, codec, bitrate e os segundos antes/depois.
6. A fila abre o CS2, posiciona a demo no tick exato, trava e **verifica** o POV, grava a janela em tempo real e
   exporta o MP4 (título, jogador e round sobrepostos, fade e crossfade entre cortes).
7. Os vídeos prontos ficam em **Clipes**: assistir, abrir a pasta, copiar para compartilhar ou juntar vários numa
   montagem.

## Código de compartilhamento

| Onde pegar o código | CS2 → **Assistir** → **Suas partidas** → partida → copiar código de compartilhamento |
|---|---|
| Formato | `CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx` (também aceito dentro de links `steam://` ou `cs2clutchez://`) |
| Requisitos | Steam aberto e logado; **CS2 fechado** durante a consulta (o app usa a mesma conexão do jogo com a Valve) |
| Validade | a Valve mantém demos de Matchmaking/Premier por ~30 dias; depois disso o código não tem mais demo |
| Durante a consulta | o Steam mostra você como "jogando CS2" por alguns segundos |

A tela Início também lista **"Suas partidas no CS2"**: as partidas que o CS2 já baixou, com o código de cada uma
(lido do `.dem.info` que o jogo grava ao lado da demo) — um clique abre ou copia o código.

## Requisitos

| Item | Detalhe |
|---|---|
| Windows | 10 2004+ ou 11, 64 bits (Windows Graphics Capture) |
| Counter-Strike 2 | instalado pelo Steam, com o Steam logado |
| GPU | recomendada (NVENC / AMF / Quick Sync). Sem GPU o app usa x264/x265 na CPU |
| FFmpeg | 8.1+ com o filtro `gfxcapture`. O instalador já inclui o FFmpeg 9.0 (build GPL da BtbN) |
| .NET | nada a instalar (self-contained). Para compilar: .NET 10 SDK (LTS) |

## Instalação

Baixe a versão mais recente em [Releases](https://github.com/EzequielVieira/cs2clutchEz/releases/latest):

- **Instalador MSI** — instala em `Arquivos de Programas\cs2clutchEz`, cria atalhos, registra o protocolo
  `cs2clutchez://` e inclui o FFmpeg.
- **Zip portátil** — extraia em qualquer pasta e execute `cs2clutchEz.exe`.

Na primeira execução o app cria `Documentos\cs2clutchEz\`:

```
cs2clutchEz/
├── Demos/          demos baixadas pelo código e .bz2 descompactadas (cache)
├── Highlights/
│   └── <Jogador>/<Mapa data placar>/Ace | Clutch | MultiKill | Kills | Objective | Utility
├── Thumbnails/
├── Logs/           cs2clutchez-AAAAMMDD.log (14 dias)
├── Database/       cs2hg.db (SQLite) + cache/ (partidas analisadas em JSON gz)
└── Work/           capturas brutas enquanto um job renderiza (apagadas quando o MP4 é exportado)
```

Com o app instalado, qualquer página pode abrir uma partida com
`<a href="cs2clutchez://CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx">Abrir no cs2clutchEz</a>`.

## Como a renderização funciona

Uma demo não é vídeo: são dados de replay que precisam ser reproduzidos pelo CS2. No CS2 atual o `startmovie` não
existe mais e o `-netconport` é ignorado; por isso o app controla a demo com um `.cfg` executado por uma tecla, lê o
`console.log`, confirma o jogador observado pelo Game State Integration e grava a janela com Windows Graphics
Capture (FFmpeg `gfxcapture`). Nada é injetado no jogo. Detalhes em [docs/RENDERING.md](docs/RENDERING.md).

**Limitações honestas**

- A gravação é em **tempo real**: um highlight de 30 s leva ~30 s + carregamento.
- O CS2 precisa ficar em **primeiro plano** enquanto recebe comandos. Não use mouse/teclado durante a renderização.
- O áudio é o **loopback do dispositivo padrão**: sons de outros programas também são gravados.
- Se o CS2 já estiver aberto por você, ele não pode ser controlado: o app oferece reiniciá-lo.
- Para baixar pelo código o CS2 precisa estar fechado; se a fila estiver renderizando, espere ela terminar.

## Detecção

Cada regra é um detector independente (`IHighlightDetector`) que usa **apenas campos presentes na demo**. Cada evento
soma pontos e o total ordena os highlights (limites como "muito dano" podem ser ajustados em Configurações). A tabela
completa de pontos está no [README em inglês](README.md#detection).

## Linha de comando

`cs2clutchez-cli.exe` usa os mesmos serviços do app (saída em inglês; mensagens do app seguem `--lang`):

```
cs2clutchez-cli sharecode CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx --top 10 --lang pt-BR
cs2clutchez-cli sharecode CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx --render --top 3 --res 1080p
cs2clutchez-cli analyze "C:\demos\partida.dem" --player "NomeDoJogador" --top 20
cs2clutchez-cli probe | queue | resume | matches | resolve | install-ffmpeg | restore-config
```

## Compilar, testar e empacotar

```powershell
# .NET 10 SDK
dotnet build CS2DemoHighlightGenerator.sln -c Release
dotnet test tests/CS2HG.Tests/CS2HG.Tests.csproj
./build.ps1            # dist/app (self-contained win-x64), zip portátil e MSI (WiX 5)
```

Traduções e dicas de desenvolvimento: [CONTRIBUTING.md](CONTRIBUTING.md).

## Solução de problemas

| Sintoma | Solução |
|---|---|
| "Feche o CS2 antes de buscar partidas pelo código" | Feche o jogo (ou espere a fila de renderização terminar) |
| "A demo não está mais nos servidores da Valve" | A partida tem mais de ~30 dias; não há como recuperar a demo |
| "O CS2 está aberto, mas não foi iniciado pelo cs2clutchEz" | Feche o CS2 ou aceite o reinício proposto |
| POV não verificado | Porta GSI ocupada (mude em Configurações) — o vídeo é gerado mesmo assim |
| Configuração do CS2 alterada após uma queda | Configurações → **Restaurar configuração original do CS2** |

## Licença

[MIT](LICENSE). Componentes de terceiros: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). O FFmpeg incluído é um
build **GPL** distribuído como programa separado. Counter-Strike 2 é marca da Valve; este projeto não é afiliado à
Valve.
