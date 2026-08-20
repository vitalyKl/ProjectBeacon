import type { MessageKey } from "./en";

export const es: Partial<Record<MessageKey, string>> = {
  "nav.home": "Inicio",
  "nav.board": "Tablero",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Hoja de ruta",
  "nav.context": "Contexto",
  "nav.files": "Archivos",
  "nav.agents": "Agentes",
  "nav.decisions": "Decisiones",
  "nav.reports": "Informes",
  "nav.settings": "Ajustes",
  "nav.learn": "Aprender",
  "common.loading": "Cargando…",
  "common.selectProject": "Elige un proyecto en la cabecera.",
  "common.language": "Idioma",
  "common.org": "Org",
  "common.project": "Proyecto",
  "common.logout": "Salir",
  "common.failedSession": "no se pudo cargar la sesión",
  "common.failedProjects": "no se pudieron cargar los proyectos",
  "common.logoutFailed": "no se pudo cerrar la sesión",
  "reports.title": "Informes",
  "reports.intro":
    "Genera una instantánea del tablero o importa una revisión para que los agentes la lean y creen tareas.",
  "reports.generate": "Generar informe",
  "reports.generating": "Generando…",
  "reports.snapshots": "Instantáneas",
  "reports.emptyReports": "Aún no hay informes. Genera uno cuando quieras un punto de control.",
  "reports.reviews": "Revisiones importadas",
  "reports.reviewsIntro":
    "Pega una revisión, auditoría o nota. Los agentes pueden listarlas y convertir hallazgos en tareas.",
  "reports.reviewTitle": "Título (opcional)",
  "reports.reviewBody": "Markdown de la revisión",
  "reports.import": "Importar revisión",
  "reports.importing": "Importando…",
  "reports.emptyReviews": "Aún no hay revisiones importadas.",
  "reports.loadFailed": "no se pudieron cargar los informes",
  "reports.generateFailed": "no se pudo generar el informe",
  "reports.importFailed": "no se pudo importar la revisión",
  "learn.title": "Aprender",
  "learn.intro":
    "Beacon es el sistema operativo alrededor de tus agentes. Las personas mantienen el brief y el tablero. Los agentes locales toman el trabajo Ready.",
  "learn.start": "Empieza aquí",
  "learn.startBody":
    "Crea o elige un proyecto en la cabecera. Escribe el brief en Contexto. Pon el trabajo en Ready. Crea un token en Agentes y ejecuta setup en la máquina del agente.",
  "learn.screens": "Para qué sirve cada pantalla",
  "learn.home": "Inicio es el resumen: brief, hitos, índice y el trabajo listo o en curso.",
  "learn.board":
    "El tablero es la cola viva. Arrastra una tarjeta para cambiar el estado. Ready es lo que pueden empezar los agentes locales.",
  "learn.backlog":
    "El backlog es el mismo trabajo en lista. Úsalo para revisar o cambiar estados rápido.",
  "learn.roadmap":
    "La hoja de ruta agrupa por hito. Las dependencias dicen qué tarea debe terminar antes de otra.",
  "learn.context":
    "Contexto es el AGENTS.md vivo. Compila un brief de sesión desde aquí o desde una tarea. El índice es opcional.",
  "learn.agents":
    "En Agentes creas un token y ves sesiones. Beacon no ejecuta un agente de código alojado.",
  "learn.decisions": "Decisiones y restricciones son las reglas que los agentes no deben inventar.",
  "learn.reports":
    "Informes es un punto de control del tablero más revisiones importadas. Los agentes pueden listarlas y crear tareas.",
  "learn.settings":
    "Ajustes guarda el id del proyecto, miembros y áreas. Las áreas son prefijos, no chips libres.",
  "learn.check": "Cómo comprobar una tarea terminada",
  "learn.checkBody":
    "Abre la tarea. Lee How to check. Sigue esos pasos en la app. Si está vacío, pide al agente que lo escriba en finish_work.",
  "learn.multi": "Más de un proyecto",
  "learn.multiBody":
    "Cada proyecto necesita su propio token. Vuelve a ejecutar beacon connect o beacon setup con ese id. El MCP local cambia con --project, BEACON_PROJECT o project_id en la herramienta.",
  "learn.guide": "Primera hora",
  "learn.guideOne": "1. Escribe Goals y Definition of Done en Contexto.",
  "learn.guideTwo": "2. Crea una tarea Ready con How to check.",
  "learn.guideThree": "3. Crea un token en Agentes y ejecuta setup.cmd o beacon setup.",
  "learn.guideFour":
    "4. Deja que el agente llame start_work y comprueba el cambio con How to check.",
  "settings.language": "Idioma",
  "settings.languageHint": "Elige el idioma de las etiquetas, estados vacíos y esta página.",
  "home.welcome":
    "Crea un proyecto para empezar el tablero. Beacon añadirá un primer hito y unas tareas.",
};
