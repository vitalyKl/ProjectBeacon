import type { MessageKey } from "./en";

export const it: Partial<Record<MessageKey, string>> = {
  "nav.home": "Home",
  "nav.board": "Bacheca",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Roadmap",
  "nav.context": "Contesto",
  "nav.files": "File",
  "nav.agents": "Agenti",
  "nav.decisions": "Decisioni",
  "nav.reports": "Report",
  "nav.settings": "Impostazioni",
  "nav.learn": "Scopri",
  "common.loading": "Caricamento…",
  "common.selectProject": "Seleziona un progetto dall’intestazione.",
  "common.language": "Lingua",
  "common.org": "Org",
  "common.project": "Progetto",
  "common.logout": "Esci",
  "common.failedSession": "impossibile caricare la sessione",
  "common.failedProjects": "impossibile caricare i progetti",
  "common.logoutFailed": "disconnessione non riuscita",
  "reports.title": "Report",
  "reports.intro":
    "Genera uno snapshot della bacheca attuale oppure importa una review così gli agenti possono leggerla e creare attività di follow-up.",
  "reports.generate": "Genera report",
  "reports.generating": "Generazione…",
  "reports.snapshots": "Snapshot",
  "reports.emptyReports":
    "Nessun report ancora. Generane uno quando vuoi un checkpoint che gli agenti possano leggere.",
  "reports.reviews": "Review importate",
  "reports.reviewsIntro":
    "Incolla una review, un audit o una nota. Gli agenti possono elencarli e trasformare i rilievi in attività.",
  "reports.reviewTitle": "Titolo (facoltativo)",
  "reports.reviewBody": "Markdown della review",
  "reports.import": "Importa review",
  "reports.importing": "Importazione…",
  "reports.emptyReviews": "Nessuna review importata ancora.",
  "reports.loadFailed": "impossibile caricare i report",
  "reports.generateFailed": "impossibile generare il report",
  "reports.importFailed": "impossibile importare la review",
  "learn.title": "Scopri",
  "learn.intro":
    "Beacon è il sistema operativo intorno ai tuoi agenti. Le persone tengono il brief e la bacheca. Gli agenti locali prendono il lavoro Ready.",
  "learn.start": "Inizia qui",
  "learn.startBody":
    "Crea o scegli un progetto nell’intestazione. Scrivi il brief vivo in Contesto. Metti il lavoro Ready sulla Bacheca. Crea un token di progetto in Agenti, poi esegui setup sulla macchina dell’agente.",
  "learn.screens": "A cosa serve ogni schermata",
  "learn.home":
    "Home è lo snapshot attuale: brief, milestone, stato dell’indice e lavoro pronto o in corso.",
  "learn.board":
    "La bacheca è la coda viva. Trascina una scheda per cambiare stato. Ready è ciò che gli agenti locali possono iniziare.",
  "learn.backlog":
    "Il backlog è lo stesso lavoro in elenco. Usalo per scorrere o cambiare stato in fretta.",
  "learn.roadmap":
    "La roadmap raggruppa il lavoro per milestone. Le dipendenze indicano quale attività deve finire prima che un’altra inizi.",
  "learn.context":
    "Contesto è l’AGENTS.md vivo. Compila un brief di sessione da qui o da un’attività. L’indice del codice è facoltativo.",
  "learn.agents":
    "In Agenti crei un token di progetto e osservi le sessioni. Beacon non esegue un agente di codice ospitato.",
  "learn.decisions":
    "Decisioni e vincoli sono le regole durature che gli agenti non devono aggirare.",
  "learn.reports":
    "Report è un checkpoint della bacheca più le review importate. Gli agenti possono elencarli e creare attività.",
  "learn.settings":
    "Impostazioni contiene l’id del progetto, i membri e le etichette di area. Le aree sono prefissi, non chip liberi.",
  "learn.check": "Come verificare un’attività finita",
  "learn.checkBody":
    "Apri l’attività. Leggi How to check. Segui quei passi nell’app. Se le note sono vuote, chiedi all’agente di scriverle in finish_work.",
  "learn.multi": "Più di un progetto",
  "learn.multiBody":
    "Ogni progetto Beacon ha bisogno del proprio token. Esegui di nuovo beacon connect o beacon setup con quell’id. L’MCP locale cambia con --project, BEACON_PROJECT o project_id su una chiamata strumento.",
  "learn.guide": "Prima ora",
  "learn.guideOne": "1. Scrivi Goals e Definition of Done in Contesto.",
  "learn.guideTwo": "2. Crea un’attività Ready con How to check compilato.",
  "learn.guideThree": "3. Crea un token in Agenti ed esegui setup.cmd o beacon setup.",
  "learn.guideFour":
    "4. Lascia che l’agente chiami start_work, poi conferma il cambiamento da How to check.",
  "settings.language": "Lingua",
  "settings.languageHint": "Scegli la lingua di etichette, stati vuoti e di questa pagina Scopri.",
  "home.welcome":
    "Crea un progetto per avviare la bacheca. Beacon aggiungerà una prima milestone e alcune attività iniziali.",
};
