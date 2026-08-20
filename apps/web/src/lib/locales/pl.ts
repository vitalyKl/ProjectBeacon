import type { MessageKey } from "./en";

export const pl: Partial<Record<MessageKey, string>> = {
  "nav.home": "Start",
  "nav.board": "Tablica",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Mapa drogowa",
  "nav.context": "Kontekst",
  "nav.files": "Pliki",
  "nav.agents": "Agenci",
  "nav.decisions": "Decyzje",
  "nav.reports": "Raporty",
  "nav.settings": "Ustawienia",
  "nav.learn": "Poznaj",
  "common.loading": "Ładowanie…",
  "common.selectProject": "Wybierz projekt w nagłówku.",
  "common.language": "Język",
  "common.org": "Org",
  "common.project": "Projekt",
  "common.logout": "Wyloguj",
  "common.failedSession": "nie udało się wczytać sesji",
  "common.failedProjects": "nie udało się wczytać projektów",
  "common.logoutFailed": "nie udało się wylogować",
  "reports.title": "Raporty",
  "reports.intro":
    "Wygeneruj migawkę bieżącej tablicy albo zaimportuj recenzję, żeby agenci mogli ją przeczytać i utworzyć zadania.",
  "reports.generate": "Wygeneruj raport",
  "reports.generating": "Generowanie…",
  "reports.snapshots": "Migawki",
  "reports.emptyReports":
    "Nie ma jeszcze raportów. Wygeneruj jeden, gdy chcesz punkt kontrolny czytelny dla agentów.",
  "reports.reviews": "Zaimportowane recenzje",
  "reports.reviewsIntro":
    "Wklej recenzję, audyt lub notatkę. Agenci mogą je listować i zamieniać ustalenia na zadania.",
  "reports.reviewTitle": "Tytuł (opcjonalnie)",
  "reports.reviewBody": "Markdown recenzji",
  "reports.import": "Importuj recenzję",
  "reports.importing": "Import…",
  "reports.emptyReviews": "Nie zaimportowano jeszcze recenzji.",
  "reports.loadFailed": "nie udało się wczytać raportów",
  "reports.generateFailed": "nie udało się wygenerować raportu",
  "reports.importFailed": "nie udało się zaimportować recenzji",
  "learn.title": "Poznaj",
  "learn.intro":
    "Beacon to system operacyjny wokół Twoich agentów. Ludzie utrzymują brief i tablicę. Lokalni agenci biorą pracę Ready.",
  "learn.start": "Zacznij tutaj",
  "learn.startBody":
    "Utwórz lub wybierz projekt w nagłówku. Napisz żywy brief w Kontekście. Ustaw pracę na Tablicy jako Ready. Wystaw token projektu w Agentach i uruchom setup na maszynie agenta.",
  "learn.screens": "Do czego służy każdy ekran",
  "learn.home":
    "Start to bieżąca migawka: brief, kamienie milowe, stan indeksu oraz praca gotowa lub w toku.",
  "learn.board":
    "Tablica to żywa kolejka. Przeciągnij kartę, aby zmienić status. Ready to to, co mogą zacząć lokalni agenci.",
  "learn.backlog":
    "Backlog to ta sama praca na liście. Używaj go do szybkiego przeglądu lub zmiany statusu.",
  "learn.roadmap":
    "Mapa drogowa grupuje pracę według kamieni milowych. Zależności pokazują, które zadanie musi skończyć się wcześniej.",
  "learn.context":
    "Kontekst to żywy AGENTS.md. Skompiluj brief sesji stąd lub z zadania. Indeks kodu jest opcjonalny.",
  "learn.agents":
    "W Agentach wystawiasz token projektu i oglądasz sesje. Beacon nie uruchamia hostowanego agenta kodującego.",
  "learn.decisions":
    "Decyzje i ograniczenia to trwałe reguły, których agenci nie powinni obchodzić.",
  "learn.reports":
    "Raporty to punkt kontrolny tablicy plus zaimportowane recenzje. Agenci mogą je listować i tworzyć zadania.",
  "learn.settings":
    "Ustawienia trzymają id projektu, członków i etykiety obszarów. Obszary to prefiksy, nie swobodne chipy.",
  "learn.check": "Jak sprawdzić ukończone zadanie",
  "learn.checkBody":
    "Otwórz zadanie. Przeczytaj How to check. Wykonaj te kroki w aplikacji. Jeśli notatki są puste, poproś agenta, żeby zapisał je w finish_work.",
  "learn.multi": "Więcej niż jeden projekt",
  "learn.multiBody":
    "Każdy projekt Beacon potrzebuje własnego tokenu. Uruchom ponownie beacon connect lub beacon setup z tym id. Lokalne MCP przełącza się przez --project, BEACON_PROJECT lub project_id przy wywołaniu narzędzia.",
  "learn.guide": "Pierwsza godzina",
  "learn.guideOne": "1. Napisz Goals i Definition of Done w Kontekście.",
  "learn.guideTwo": "2. Utwórz zadanie Ready z wypełnionym How to check.",
  "learn.guideThree": "3. Wystaw token w Agentach i uruchom setup.cmd albo beacon setup.",
  "learn.guideFour":
    "4. Niech agent wywoła start_work, potem potwierdź zmianę według How to check.",
  "settings.language": "Język",
  "settings.languageHint": "Wybierz język etykiet, pustych stanów i tej strony Poznaj.",
  "home.welcome":
    "Utwórz projekt, aby zacząć tablicę. Beacon doda pierwszy kamień milowy i kilka zadań startowych.",
};
