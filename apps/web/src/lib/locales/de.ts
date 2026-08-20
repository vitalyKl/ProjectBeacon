import type { MessageKey } from "./en";

export const de: Partial<Record<MessageKey, string>> = {
  "nav.home": "Start",
  "nav.board": "Board",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Roadmap",
  "nav.context": "Kontext",
  "nav.files": "Dateien",
  "nav.agents": "Agenten",
  "nav.decisions": "Entscheidungen",
  "nav.reports": "Berichte",
  "nav.settings": "Einstellungen",
  "nav.learn": "Lernen",
  "common.loading": "Laden…",
  "common.selectProject": "Wählen Sie ein Projekt in der Kopfzeile.",
  "common.language": "Sprache",
  "common.org": "Org",
  "common.project": "Projekt",
  "common.logout": "Abmelden",
  "common.failedSession": "Sitzung konnte nicht geladen werden",
  "common.failedProjects": "Projekte konnten nicht geladen werden",
  "common.logoutFailed": "Abmelden fehlgeschlagen",
  "reports.title": "Berichte",
  "reports.intro":
    "Erzeugen Sie einen Snapshot des aktuellen Boards oder importieren Sie eine Review, damit Agenten sie lesen und Folgeaufgaben anlegen können.",
  "reports.generate": "Bericht erzeugen",
  "reports.generating": "Wird erzeugt…",
  "reports.snapshots": "Snapshots",
  "reports.emptyReports":
    "Noch keine Berichte. Erzeugen Sie einen, wenn Sie einen Prüfpunkt für Agenten brauchen.",
  "reports.reviews": "Importierte Reviews",
  "reports.reviewsIntro":
    "Fügen Sie eine Review, ein Audit oder eine Notiz ein. Agenten können sie listen und Erkenntnisse in Aufgaben verwandeln.",
  "reports.reviewTitle": "Titel (optional)",
  "reports.reviewBody": "Review-Markdown",
  "reports.import": "Review importieren",
  "reports.importing": "Import…",
  "reports.emptyReviews": "Noch keine Reviews importiert.",
  "reports.loadFailed": "Berichte konnten nicht geladen werden",
  "reports.generateFailed": "Bericht konnte nicht erzeugt werden",
  "reports.importFailed": "Review konnte nicht importiert werden",
  "learn.title": "Lernen",
  "learn.intro":
    "Beacon ist das Betriebssystem um Ihre Agenten. Menschen halten Brief und Board. Lokale Agenten übernehmen Ready-Arbeit.",
  "learn.start": "Hier starten",
  "learn.startBody":
    "Erstellen oder wählen Sie ein Projekt in der Kopfzeile. Schreiben Sie den lebenden Brief in Kontext. Setzen Sie Arbeit auf dem Board auf Ready. Erzeugen Sie auf Agenten ein Projekttoken und führen Sie Setup auf dem Rechner des Agenten aus.",
  "learn.screens": "Wofür jeder Bildschirm da ist",
  "learn.home":
    "Start ist der aktuelle Snapshot: Brief, Meilensteine, Indexstatus und Arbeit, die bereit oder in Bearbeitung ist.",
  "learn.board":
    "Das Board ist die lebendige Warteschlange. Ziehen Sie eine Karte, um den Status zu ändern. Ready ist, was lokale Agenten starten können.",
  "learn.backlog":
    "Das Backlog ist dieselbe Arbeit als Liste. Nutzen Sie es zum schnellen Durchsehen oder Umstufen.",
  "learn.roadmap":
    "Die Roadmap gruppiert Arbeit nach Meilenstein. Abhängigkeiten zeigen, welche Aufgabe zuerst fertig sein muss.",
  "learn.context":
    "Kontext ist das lebende AGENTS.md. Kompilieren Sie hier oder von einer Aufgabe einen Session-Brief. Der Code-Index ist optional.",
  "learn.agents":
    "Unter Agenten erzeugen Sie ein Projekttoken und sehen Sitzungen. Beacon führt keinen gehosteten Coding-Agenten aus.",
  "learn.decisions":
    "Entscheidungen und Einschränkungen sind die dauerhaften Regeln, um die Agenten nicht herumerfinden sollen.",
  "learn.reports":
    "Berichte sind ein Prüfpunkt des Boards plus importierte Reviews. Agenten können sie listen und Erkenntnisse in Aufgaben verwandeln.",
  "learn.settings":
    "Einstellungen enthalten Projekt-ID, Mitglieder und Bereichslabels. Bereiche sind Präfixe, keine freien Chips.",
  "learn.check": "Wie man eine fertige Aufgabe prüft",
  "learn.checkBody":
    "Öffnen Sie die Aufgabe. Lesen Sie How to check. Folgen Sie diesen Schritten in der App. Wenn die Notizen leer sind, bitten Sie den Agenten, sie bei finish_work zu schreiben.",
  "learn.multi": "Mehr als ein Projekt",
  "learn.multiBody":
    "Jedes Beacon-Projekt braucht ein eigenes Token. Führen Sie beacon connect oder beacon setup erneut mit dieser Projekt-ID aus. Lokales MCP wechselt mit --project, BEACON_PROJECT oder project_id beim Tool-Aufruf.",
  "learn.guide": "Erste Stunde",
  "learn.guideOne": "1. Schreiben Sie Goals und Definition of Done in Kontext.",
  "learn.guideTwo": "2. Legen Sie eine Ready-Aufgabe mit ausgefülltem How to check an.",
  "learn.guideThree":
    "3. Erzeugen Sie auf Agenten ein Token und führen Sie setup.cmd oder beacon setup aus.",
  "learn.guideFour":
    "4. Lassen Sie den Agenten start_work aufrufen und bestätigen Sie die Änderung über How to check.",
  "settings.language": "Sprache",
  "settings.languageHint":
    "Wählen Sie die Sprache für Beschriftungen, leere Zustände und diese Lernseite.",
  "home.welcome":
    "Erstellen Sie ein Projekt, um das Board zu starten. Beacon fügt einen ersten Meilenstein und ein paar Startaufgaben hinzu.",
};
