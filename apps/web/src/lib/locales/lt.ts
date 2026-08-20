import type { MessageKey } from "./en";

export const lt: Partial<Record<MessageKey, string>> = {
  "nav.home": "Pradžia",
  "nav.board": "Lenta",
  "nav.backlog": "Backlogas",
  "nav.roadmap": "Planas",
  "nav.context": "Kontekstas",
  "nav.files": "Failai",
  "nav.agents": "Agentai",
  "nav.decisions": "Sprendimai",
  "nav.reports": "Ataskaitos",
  "nav.settings": "Nustatymai",
  "nav.learn": "Sužinoti",
  "common.loading": "Kraunama…",
  "common.selectProject": "Pasirinkite projektą antraštėje.",
  "common.language": "Kalba",
  "common.org": "Org",
  "common.project": "Projektas",
  "common.logout": "Atsijungti",
  "common.failedSession": "nepavyko įkelti sesijos",
  "common.failedProjects": "nepavyko įkelti projektų",
  "common.logoutFailed": "nepavyko atsijungti",
  "reports.title": "Ataskaitos",
  "reports.intro":
    "Sukurkite dabartinės lentos momentinę kopiją arba importuokite peržiūrą, kad agentai ją perskaitytų ir sukurtų tolesnes užduotis.",
  "reports.generate": "Kurti ataskaitą",
  "reports.generating": "Kuriama…",
  "reports.snapshots": "Momentinės kopijos",
  "reports.emptyReports":
    "Ataskaitų dar nėra. Sukurkite vieną, kai reikia kontrolinio taško, kurį agentai gali skaityti.",
  "reports.reviews": "Importuotos peržiūros",
  "reports.reviewsIntro":
    "Įklijuokite peržiūrą, auditą ar pastabą. Agentai gali jas išvardyti ir paversti radinius užduotimis.",
  "reports.reviewTitle": "Pavadinimas (neprivaloma)",
  "reports.reviewBody": "Peržiūros „Markdown“",
  "reports.import": "Importuoti peržiūrą",
  "reports.importing": "Importuojama…",
  "reports.emptyReviews": "Importuotų peržiūrų dar nėra.",
  "reports.loadFailed": "nepavyko įkelti ataskaitų",
  "reports.generateFailed": "nepavyko sukurti ataskaitos",
  "reports.importFailed": "nepavyko importuoti peržiūros",
  "learn.title": "Sužinoti",
  "learn.intro":
    "Beacon yra operacinė sistema aplink jūsų agentus. Žmonės prižiūri brifą ir lentą. Vietiniai agentai ima Ready darbą.",
  "learn.start": "Pradėkite čia",
  "learn.startBody":
    "Sukurkite arba pasirinkite projektą antraštėje. Parašykite gyvą brifą Kontekste. Padėkite darbą Lentoje kaip Ready. Agentuose išduokite projekto žetoną ir paleiskite setup agento mašinoje.",
  "learn.screens": "Kam skirtas kiekvienas ekranas",
  "learn.home":
    "Pradžia – dabartinė momentinė kopija: brifas, etapai, indekso būsena ir darbas, kuris paruoštas arba vykdomas.",
  "learn.board":
    "Lenta – gyva eilė. Vilkite kortelę, kad pakeistumėte būseną. Ready – tai, ką gali pradėti vietiniai agentai.",
  "learn.backlog":
    "Backlogas – tas pats darbas sąrašu. Naudokite, kai reikia greitai peržiūrėti ar pakeisti būseną.",
  "learn.roadmap":
    "Planas grupuoja darbą pagal etapus. Priklausomybės rodo, kuri užduotis turi baigtis anksčiau.",
  "learn.context":
    "Kontekstas – gyvas AGENTS.md. Sudarykite sesijos brifą čia arba iš užduoties. Kodo indeksas neprivalomas.",
  "learn.agents":
    "Agentuose išduodate projekto žetoną ir stebite sesijas. Beacon nepaleidžia talpinamo kodo agento.",
  "learn.decisions":
    "Sprendimai ir apribojimai – tvarios taisyklės, kurių agentai neturėtų apeiti.",
  "learn.reports":
    "Ataskaitos – lentos kontrolinis taškas ir importuotos peržiūros. Agentai gali jas skaityti ir kurti užduotis.",
  "learn.settings":
    "Nustatymai saugo projekto id, narius ir sričių žymas. Sritys – kelio priešdėliai, ne laisvi žymekliai.",
  "learn.check": "Kaip patikrinti baigtą užduotį",
  "learn.checkBody":
    "Atidarykite užduotį. Perskaitykite How to check. Atlikite tuos veiksmus programoje. Jei pastabos tuščios, paprašykite agento jas įrašyti finish_work.",
  "learn.multi": "Daugiau nei vienas projektas",
  "learn.multiBody":
    "Kiekvienam Beacon projektui reikia savo žetono. Vėl paleiskite beacon connect arba beacon setup su tuo id. Vietinis MCP persijungia su --project, BEACON_PROJECT arba project_id įrankio kvietime.",
  "learn.guide": "Pirma valanda",
  "learn.guideOne": "1. Kontekste parašykite Goals ir Definition of Done.",
  "learn.guideTwo": "2. Sukurkite Ready užduotį su užpildytu How to check.",
  "learn.guideThree": "3. Agentuose išduokite žetoną ir paleiskite setup.cmd arba beacon setup.",
  "learn.guideFour":
    "4. Tegul agentas kviečia start_work, tada patvirtinkite pakeitimą pagal How to check.",
  "settings.language": "Kalba",
  "settings.languageHint": "Pasirinkite etikečių, tuščių būsenų ir šio Sužinoti puslapio kalbą.",
  "home.welcome":
    "Sukurkite projektą, kad pradėtumėte lentą. Beacon pridės pirmą etapą ir kelias pradines užduotis.",
};
