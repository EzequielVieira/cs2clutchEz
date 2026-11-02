# Renderização: da demo ao MP4

## Investigação no CS2 real (build 14194)

| Teste | Resultado |
|---|---|
| `-netconport 2121` | o processo não abre nenhuma porta TCP → console remoto indisponível |
| `startmovie <nome> jpg` + `endmovie` | nenhum arquivo gerado em `game/` → gravação interna indisponível |
| `steam://run/730//+exec ...` com o jogo aberto | comando não repassado ao jogo |
| Bind executando cfg (`bind F13 "exec cs2hg/cmd"`) | funciona **durante a demo** com o console fechado; **não** funciona no menu principal |
| Digitar no console (tecla de scancode 0x29 + texto Unicode via `SendInput`) | funciona no menu e na demo |
| `console.log` (`-condebug`) | registra `[InputService] execing ...`, `[Demo] Demo Skipping finished at tick N`, carregamento da demo; **não** registra a saída de `echo` |
| Game State Integration durante a demo | envia `player.steamid` do jogador observado (~10×/s) |
| `demo_gototick N` | N é *demo tick* (o log mostra o game tick correspondente); a busca leva ~0,1–1 s |
| `spec_player "<nome>"` / `spec_mode 1/3/5` | funcionam **depois** que a busca termina (antes disso a busca reseta o alvo) |
| `cl_draw_only_deathnotices 1`, `cl_drawhud 0`, `demoui`, `r_show_build_info 0`, `spec_show_xray 0` | funcionam na demo |
| FFmpeg 9 `gfxcapture=hwnd=...` | captura a janela do CS2 (DX11) a 60 fps com NVENC |

## Protocolo de controle

```
boot.cfg (+exec no lançamento): con_enable 1; bind F13 "exec cs2hg/cmd"
cmd.cfg  (reescrito a cada comando): ex. "demo_gototick 137353\ndemo_pause"

Comando = escreve cmd.cfg → foca a janela → SendInput(F13) → espera "execing cs2hg/cmd" no console.log
          (até 3 tentativas). Sem confirmação → erro claro ("console aberto ou janela sem foco").
```

Primeira demo: `playdemo cs2hg/demos/<nome>` digitado no console (toggle → texto → Enter → toggle).
Como o estado do console não é observável, até 4 tentativas alternam console e bind; o sucesso é confirmado
por `Signon traffic "DEMO"` / `Demo Skipping finished` no log.

## Sincronização áudio/vídeo/tick

Todos os tempos usam `Stopwatch` (monotônico):

- **Origem do vídeo**: o FFmpeg reporta `out_time_us` a cada 0,1 s (`-progress pipe:1`);
  `origem = min(agora − out_time)` (o menor valor elimina a latência do encoder).
- **Origem do áudio**: primeiro pacote WASAPI − duração do pacote; lacunas de silêncio do dispositivo são
  preenchidas para manter o WAV alinhado ao relógio.
- **Início útil**: instante em que a tecla do `demo_resume` foi enviada + latência de entrada (50 ms, configurável).

`offset_vídeo = resume − origem_vídeo` e `offset_áudio = resume − origem_áudio`; o master corta cada segmento a
partir desses offsets pela duração exata em ticks (`(fim − início) / 64`). Antes do `demo_resume` a demo está
pausada no tick inicial, então um erro de alguns milissegundos só acrescenta quadros parados.

## Verificação do POV

1. `spec_player "<nome>"` + `spec_mode` após a busca.
2. Aguarda até 1,5 s o GSI reportar o SteamID esperado.
3. Se não confirmar (nome com aspas, duplicado, Unicode), envia `spec_next` até 12 vezes, conferindo o GSI a cada passo.
4. O resultado (`PovVerified`, jogador observado) fica no job e aparece na fila.

## Proteção da configuração do usuário

Antes de abrir o CS2 o app copia `cs2_video.txt`, `cs2_machine_convars.vcfg`, `cs2_user_convars_0_slot0.vcfg` e
`cs2_user_keys_0_slot0.vcfg` de `Steam/userdata/<conta>/730/local/cfg` e grava a resolução de captura em janela
sem borda. No fim: reseta HUD/xray/build info, remove o bind `F13` (para a cópia do Steam Cloud ficar limpa),
fecha o jogo e restaura os arquivos. Um marcador garante a restauração mesmo após uma queda.

## Fila, pausa e recuperação

- Um job por vez (o CS2 reproduz uma demo por vez); demos iguais em jobs seguidos não são recarregadas.
- **Pausar** interrompe o segmento em gravação; os segmentos já capturados ficam salvos e o job continua do próximo.
- **Cancelar** encerra o job; **Tentar novamente** retoma a partir dos segmentos salvos.
- Ao reabrir o app, jobs que estavam *Renderizando* voltam como *Pendente* e são retomados ao clicar em Iniciar.
- Após o master ser montado os brutos são apagados; depois da exportação a pasta `Work/<job>` inteira é apagada.
  Se a exportação falhar, a nova tentativa reaproveita o master (sem gravar de novo).

## Export

`trim → cortes → slow motion (setpts/atempo) → concat → fps → scale+pad → efeitos → textos (drawtext com fonte
escolhida pelo alfabeto do texto) → fade` e encoder por disponibilidade: `h264_nvenc → h264_amf → h264_qsv →
libx264` (ou HEVC equivalente, com `-tag:v hvc1`), bitrate VBR com `maxrate` 1,5× e `+faststart`.
