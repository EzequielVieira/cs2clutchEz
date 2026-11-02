(function () {
  "use strict";

  var LANGS = ["pt-BR", "en", "es"];
  var STORAGE_KEY = "cs2clutchez.lang";

  // Portuguese is the page's own HTML; it is read from the DOM at start-up so it has a single source.
  var META = {
    "pt-BR": {
      title: document.title,
      description: document.querySelector('meta[name="description"]').content,
      invalid: "Código inválido. O formato é CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx.",
      opening: "Abrindo o cs2clutchEz… Se nada acontecer, instale o app pelo botão “Baixar para Windows”."
    },
    en: {
      title: "cs2clutchEz — CS2 highlights to video, ez",
      description: "Import a CS2 demo or paste the match share code: cs2clutchEz finds aces, clutches and multi-kills and records each one to MP4 straight from the game. Free and open source.",
      invalid: "Invalid code. The format is CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx.",
      opening: "Opening cs2clutchEz… If nothing happens, install the app with the “Download for Windows” button."
    },
    es: {
      title: "cs2clutchEz — highlights de CS2 en video, ez",
      description: "Importa una demo de CS2 o pega el código de la partida: cs2clutchEz encuentra aces, clutches y multi-kills y graba cada uno en MP4 directamente desde el juego. Gratis y de código abierto.",
      invalid: "Código no válido. El formato es CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx.",
      opening: "Abriendo cs2clutchEz… Si no pasa nada, instala la app con el botón “Descargar para Windows”."
    }
  };

  var TEXT = {
    en: {
      "nav.skip": "Skip to content",
      "nav.features": "Features",
      "nav.how": "How it works",
      "nav.faq": "FAQ",
      "hero.badge": "Free and open source · Windows",
      "hero.title": "Your best CS2 rounds, turned into videos.",
      "hero.lead": "Import a demo or paste the match share code. cs2clutchEz finds the aces, clutches and multi-kills of the player you pick and records each one to MP4 straight from CS2.",
      "hero.download": "Download for Windows",
      "hero.github": "View the source on GitHub",
      "mock.title": "Highlights",
      "mock.summary": "12 highlights · 1 clutch · best: Clutch 1v3",
      "mock.row1": "Clutch 1v3 + Quad Kill — MAC-10",
      "mock.row2": "Triple Kill — M4A4",
      "mock.row3": "Double Kill + No-scope — AWP",
      "mock.row4": "Knife kill — Knife",
      "mock.render": "Recording segment 2/3 in CS2",
      "share.title": "Open a match by its code",
      "share.text": "In CS2, go to <strong>Watch → Your Matches</strong>, copy the share code and paste it here. The app downloads the demo from Valve's servers and opens the analysis.",
      "share.label": "Share code",
      "share.button": "Open in cs2clutchEz",
      "share.note": "Requires the app (the installer registers cs2clutchez:// links). Matchmaking and Premier demos stay available for about 30 days.",
      "features.title": "What it does",
      "f1.title": "Finds the plays",
      "f1.text": "Aces, 1v1 to 1v5 clutches, multi-kills, no-scopes, wallbangs, knife and Zeus kills, retakes, clutch defuses, decisive rounds and high damage — all read from the demo's events.",
      "f2.title": "Records in CS2 itself",
      "f2.text": "The app opens the demo in the game, seeks to each moment, sets the camera and captures the window with audio. The video is the real game, not a recreation.",
      "f3.title": "Batch queue",
      "f3.text": "Pick several highlights and let them render. Pause, resume or cancel — and the same clip is never recorded twice.",
      "f4.title": "Export your way",
      "f4.text": "720p to 4K, 30 to 120 FPS, H.264 or H.265 on NVENC, AMF, Quick Sync or CPU, bitrate, seconds before and after, and the camera of the player, the killer, the victim, third person or director.",
      "f5.title": "Share codes",
      "f5.text": "Paste a CSGO-… code and the app downloads the demo from Valve. Matches CS2 already downloaded show up with their code ready to copy.",
      "f6.title": "Free, open, in 3 languages",
      "f6.text": "MIT license, no ads and no account. Interface in Portuguese, English and Spanish — new translations are welcome on GitHub.",
      "how.title": "How it works",
      "s1.title": "Import",
      "s1.text": "Drop a .dem or .dem.bz2 (Matchmaking, Premier, FACEIT, Gamers Club, HLTV…) or paste the match code.",
      "s2.title": "Pick the player",
      "s2.text": "See the score, the rounds and every player's stats, and choose who gets the spotlight.",
      "s3.title": "Tick the highlights",
      "s3.text": "The list is sorted by the best plays, with category filters. Tick the ones you want.",
      "s4.title": "Make the videos",
      "s4.text": "CS2 opens by itself, records each clip, and the MP4s come out sorted by player, match and category.",
      "notes.title": "Good to know",
      "notes.1": "Recording happens in real time: a 15 s clip takes about 15 s, plus loading the demo. Don't use the PC while rendering — the game window has to stay in front.",
      "notes.2": "Requires Counter-Strike 2 installed through Steam and Windows 10 (1903 or newer) or 11. FFmpeg comes with the installer.",
      "notes.3": "Nothing is injected into the game: the app uses the console, a .cfg file and demo playback, just like you would by hand. Your video settings and binds are saved first and restored at the end.",
      "notes.4": "Matchmaking and Premier demos stay on Valve's servers for about 30 days. CS:GO (Source 1) demos are not supported.",
      "faq.title": "FAQ",
      "q1": "Is it really free?",
      "a1": "Yes. It's open source under the MIT license, with no ads and no account. All the code is on GitHub.",
      "q2": "Does it work with FACEIT, Gamers Club and HLTV demos?",
      "a2": "Yes, with any Counter-Strike 2 .dem or .dem.bz2. To record, CS2 has to be able to open the demo — very old demos may not open after game updates.",
      "q3": "Where are the videos saved?",
      "a3": "In Documents\\cs2clutchEz\\Highlights, split by player, match and category (Ace, Clutch, MultiKill…). You can change the folder in the settings and on the export page.",
      "q4": "Can I use the PC while it records?",
      "a4": "It's not recommended: the CS2 window is captured in real time and the app uses the keyboard to control the demo. Let it render and come back when the queue is done.",
      "q5": "Is it affiliated with Valve?",
      "a5": "No. cs2clutchEz is an independent project. Counter-Strike, CS2 and Steam are trademarks of Valve Corporation.",
      "q6": "How can I contribute or translate?",
      "a6": "Open an issue or a pull request on GitHub. The app's translations live in <code>src/CS2HG.Core/Localization</code>, one JSON file per language.",
      "oss.title": "Open source, for real",
      "oss.text": "Demo parsing, detection, CS2 control, capture and queue: it's all on GitHub under the MIT license. Use it, study it, improve it.",
      "oss.star": "Star it on GitHub",
      "oss.issues": "Report a problem",
      "footer.license": "MIT license",
      "footer.valve": "Not affiliated with Valve Corporation. Counter-Strike, CS2 and Steam are trademarks of Valve."
    },
    es: {
      "nav.skip": "Saltar al contenido",
      "nav.features": "Funciones",
      "nav.how": "Cómo funciona",
      "nav.faq": "Preguntas",
      "hero.badge": "Gratis y de código abierto · Windows",
      "hero.title": "Tus mejores rondas de CS2, convertidas en video.",
      "hero.lead": "Importa una demo o pega el código para compartir de la partida. cs2clutchEz encuentra los aces, clutches y multi-kills del jugador que elijas y graba cada uno en MP4 directamente desde CS2.",
      "hero.download": "Descargar para Windows",
      "hero.github": "Ver el código en GitHub",
      "mock.title": "Highlights",
      "mock.summary": "12 highlights · 1 clutch · mejor: Clutch 1v3",
      "mock.row1": "Clutch 1v3 + Quadra Kill — MAC-10",
      "mock.row2": "Triple Kill — M4A4",
      "mock.row3": "Double Kill + No-scope — AWP",
      "mock.row4": "Kill con cuchillo — Cuchillo",
      "mock.render": "Grabando el segmento 2/3 en CS2",
      "share.title": "Abre una partida con su código",
      "share.text": "En CS2, ve a <strong>Ver → Tus partidas</strong>, copia el código para compartir y pégalo aquí. La app descarga la demo de los servidores de Valve y abre el análisis.",
      "share.label": "Código para compartir",
      "share.button": "Abrir en cs2clutchEz",
      "share.note": "Necesita la app instalada (el instalador registra los enlaces cs2clutchez://). Las demos de Matchmaking y Premier están disponibles unos 30 días.",
      "features.title": "Qué hace",
      "f1.title": "Encuentra las jugadas",
      "f1.text": "Aces, clutches de 1v1 a 1v5, multi-kills, no-scopes, wallbangs, kills con cuchillo y Zeus, retakes, defuses decisivos, rondas decisivas y mucho daño — todo leído de los eventos de la demo.",
      "f2.title": "Graba en el propio CS2",
      "f2.text": "La app abre la demo en el juego, va a cada momento, elige la cámara y captura la ventana con audio. El video es el juego real, no una recreación.",
      "f3.title": "Cola por lotes",
      "f3.text": "Elige varios highlights y déjalos renderizando. Pausa, reanuda o cancela — y el mismo clip nunca se graba dos veces.",
      "f4.title": "Exporta a tu manera",
      "f4.text": "De 720p a 4K, de 30 a 120 FPS, H.264 o H.265 con NVENC, AMF, Quick Sync o CPU, bitrate, segundos antes y después, y la cámara del jugador, de quien mató, de la víctima, en tercera persona o dirigida.",
      "f5.title": "Códigos para compartir",
      "f5.text": "Pega un código CSGO-… y la app descarga la demo de Valve. Las partidas que CS2 ya descargó aparecen con su código listo para copiar.",
      "f6.title": "Gratis, abierto y en 3 idiomas",
      "f6.text": "Licencia MIT, sin anuncios y sin cuenta. Interfaz en portugués, inglés y español — las nuevas traducciones son bienvenidas en GitHub.",
      "how.title": "Cómo funciona",
      "s1.title": "Importa",
      "s1.text": "Arrastra un .dem o .dem.bz2 (Matchmaking, Premier, FACEIT, Gamers Club, HLTV…) o pega el código de la partida.",
      "s2.title": "Elige al jugador",
      "s2.text": "Mira el marcador, las rondas y las estadísticas de cada jugador, y elige quién será el protagonista.",
      "s3.title": "Marca los highlights",
      "s3.text": "La lista viene ordenada por las mejores jugadas, con filtros por categoría. Marca las que quieras.",
      "s4.title": "Genera los videos",
      "s4.text": "CS2 se abre solo, graba cada clip y los MP4 salen organizados por jugador, partida y categoría.",
      "notes.title": "Conviene saber",
      "notes.1": "La grabación es en tiempo real: un clip de 15 s tarda unos 15 s, más la carga de la demo. No uses el PC mientras renderiza — la ventana del juego tiene que estar al frente.",
      "notes.2": "Necesita Counter-Strike 2 instalado con Steam y Windows 10 (1903 o posterior) u 11. FFmpeg viene con el instalador.",
      "notes.3": "No se inyecta nada en el juego: la app usa la consola, un archivo .cfg y la reproducción de demos, como lo harías a mano. Tu configuración de video y tus binds se guardan antes y se restauran al final.",
      "notes.4": "Las demos de Matchmaking y Premier están en los servidores de Valve unos 30 días. Las demos de CS:GO (Source 1) no son compatibles.",
      "faq.title": "Preguntas frecuentes",
      "q1": "¿De verdad es gratis?",
      "a1": "Sí. Es de código abierto con licencia MIT, sin anuncios y sin cuenta. Todo el código está en GitHub.",
      "q2": "¿Funciona con demos de FACEIT, Gamers Club y HLTV?",
      "a2": "Sí, con cualquier demo .dem o .dem.bz2 de Counter-Strike 2. Para grabar, CS2 tiene que poder abrir la demo — las demos muy antiguas pueden no abrirse tras las actualizaciones del juego.",
      "q3": "¿Dónde se guardan los videos?",
      "a3": "En Documentos\\cs2clutchEz\\Highlights, separados por jugador, partida y categoría (Ace, Clutch, MultiKill…). Puedes cambiar la carpeta en la configuración y en la exportación.",
      "q4": "¿Puedo usar el PC mientras graba?",
      "a4": "No se recomienda: la ventana de CS2 se captura en tiempo real y la app usa el teclado para controlar la demo. Déjalo renderizando y vuelve cuando termine la cola.",
      "q5": "¿Está afiliado a Valve?",
      "a5": "No. cs2clutchEz es un proyecto independiente. Counter-Strike, CS2 y Steam son marcas de Valve Corporation.",
      "q6": "¿Cómo puedo contribuir o traducir?",
      "a6": "Abre un issue o un pull request en GitHub. Las traducciones de la app están en <code>src/CS2HG.Core/Localization</code>, un JSON por idioma.",
      "oss.title": "Código abierto de verdad",
      "oss.text": "Lectura de demos, detección, control de CS2, captura y cola: todo está en GitHub con licencia MIT. Úsalo, estúdialo y mejóralo.",
      "oss.star": "Dale una estrella en GitHub",
      "oss.issues": "Reportar un problema",
      "footer.license": "Licencia MIT",
      "footer.valve": "No afiliado a Valve Corporation. Counter-Strike, CS2 y Steam son marcas de Valve."
    }
  };

  var nodes = Array.prototype.slice.call(document.querySelectorAll("[data-i18n]"));
  TEXT["pt-BR"] = {};
  nodes.forEach(function (el) { TEXT["pt-BR"][el.getAttribute("data-i18n")] = el.innerHTML.trim(); });

  var current = "pt-BR";

  function normalize(code) {
    if (!code) return null;
    code = String(code).toLowerCase();
    if (code.indexOf("pt") === 0) return "pt-BR";
    if (code.indexOf("es") === 0) return "es";
    if (code.indexOf("en") === 0) return "en";
    return null;
  }

  function stored() {
    try { return localStorage.getItem(STORAGE_KEY); } catch (e) { return null; }
  }

  function remember(lang) {
    try { localStorage.setItem(STORAGE_KEY, lang); } catch (e) { /* private mode: language just isn't remembered */ }
  }

  function initialLanguage() {
    var fromUrl = normalize(new URLSearchParams(location.search).get("lang"));
    if (fromUrl) return fromUrl;
    var saved = normalize(stored());
    if (saved) return saved;
    var prefs = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language];
    for (var i = 0; i < prefs.length; i++) {
      var n = normalize(prefs[i]);
      if (n) return n;
    }
    return "en";
  }

  function apply(lang) {
    current = lang;
    var dict = TEXT[lang];
    nodes.forEach(function (el) {
      var value = dict[el.getAttribute("data-i18n")];
      if (value !== undefined) el.innerHTML = value;
    });
    document.documentElement.lang = lang;
    document.title = META[lang].title;
    document.querySelector('meta[name="description"]').content = META[lang].description;
    document.querySelectorAll(".lang button").forEach(function (b) {
      b.setAttribute("aria-pressed", String(b.getAttribute("data-lang") === lang));
    });
  }

  document.querySelectorAll(".lang button").forEach(function (button) {
    button.addEventListener("click", function () {
      var lang = button.getAttribute("data-lang");
      remember(lang);
      apply(lang);
      var url = new URL(location.href);
      url.searchParams.set("lang", lang);
      history.replaceState(null, "", url);
    });
  });

  apply(initialLanguage());

  // Share code → cs2clutchez://CSGO-... (handled by the installed app). Accepts any text that contains a code,
  // e.g. a steam:// link copied from the game.
  var CODE = /CSGO(-[ABCDEFGHJKLMNOPQRSTUVWXYZabcdefhijkmnopqrstuvwxyz23456789]{5}){5}/;
  var form = document.getElementById("share-form");
  var input = document.getElementById("share-code");
  var hint = document.getElementById("share-hint");

  var preset = new URLSearchParams(location.search).get("code") || decodeURIComponent(location.hash.slice(1));
  var presetMatch = preset && preset.match(CODE);
  if (presetMatch) input.value = presetMatch[0];

  function showHint(text, isError) {
    hint.textContent = text;
    hint.classList.toggle("error", isError);
    input.setAttribute("aria-invalid", String(isError));
  }

  input.addEventListener("input", function () {
    if (input.getAttribute("aria-invalid") === "true") {
      input.setAttribute("aria-invalid", "false");
      hint.classList.remove("error");
      hint.innerHTML = TEXT[current]["share.note"];
    }
  });

  form.addEventListener("submit", function (e) {
    e.preventDefault();
    var match = input.value.match(CODE);
    if (!match) {
      showHint(META[current].invalid, true);
      input.focus();
      return;
    }
    input.value = match[0];
    showHint(META[current].opening, false);
    location.href = "cs2clutchez://" + match[0];
  });
})();
