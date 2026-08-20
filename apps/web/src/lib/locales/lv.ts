import type { MessageKey } from "./en";

export const lv: Partial<Record<MessageKey, string>> = {
  "nav.home": "Sākums",
  "nav.board": "Dēlis",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Ceļa karte",
  "nav.context": "Konteksts",
  "nav.files": "Faili",
  "nav.agents": "Aģenti",
  "nav.decisions": "Lēmumi",
  "nav.reports": "Pārskati",
  "nav.settings": "Iestatījumi",
  "nav.learn": "Uzzināt",
  "common.loading": "Ielādē…",
  "common.selectProject": "Izvēlieties projektu galvenē.",
  "common.language": "Valoda",
  "common.org": "Org",
  "common.project": "Projekts",
  "common.logout": "Iziet",
  "common.failedSession": "neizdevās ielādēt sesiju",
  "common.failedProjects": "neizdevās ielādēt projektus",
  "common.logoutFailed": "neizdevās iziet",
  "reports.title": "Pārskati",
  "reports.intro":
    "Izveidojiet pašreizējā dēļa momentuzņēmumu vai importējiet pārskatu, lai aģenti to izlasītu un izveidotu turpmākos uzdevumus.",
  "reports.generate": "Ģenerēt pārskatu",
  "reports.generating": "Ģenerē…",
  "reports.snapshots": "Momentuzņēmumi",
  "reports.emptyReports":
    "Pārskatu vēl nav. Ģenerējiet vienu, kad vajadzīgs kontrolpunkts, ko aģenti var lasīt.",
  "reports.reviews": "Importētās recenzijas",
  "reports.reviewsIntro":
    "Ielīmējiet recenziju, auditu vai piezīmi. Aģenti var tās uzskaitīt un pārvērst atklājumus uzdevumos.",
  "reports.reviewTitle": "Virsraksts (neobligāti)",
  "reports.reviewBody": "Recenzijas Markdown",
  "reports.import": "Importēt recenziju",
  "reports.importing": "Importē…",
  "reports.emptyReviews": "Importētu recenziju vēl nav.",
  "reports.loadFailed": "neizdevās ielādēt pārskatus",
  "reports.generateFailed": "neizdevās ģenerēt pārskatu",
  "reports.importFailed": "neizdevās importēt recenziju",
  "learn.title": "Uzzināt",
  "learn.intro":
    "Beacon ir operētājsistēma ap jūsu aģentiem. Cilvēki uztur brīfu un dēli. Vietējie aģenti ņem Ready darbu.",
  "learn.start": "Sāciet šeit",
  "learn.startBody":
    "Izveidojiet vai izvēlieties projektu galvenē. Uzrakstiet dzīvo brīfu Kontekstā. Ielieciet darbu Dēlī kā Ready. Aģentos izsniedziet projekta marķieri un palaidiet setup aģenta mašīnā.",
  "learn.screens": "Kam paredzēts katrs ekrāns",
  "learn.home":
    "Sākums ir pašreizējais momentuzņēmums: brīfs, atskaites punkti, indeksa stāvoklis un darbs, kas gatavs vai jau notiek.",
  "learn.board":
    "Dēlis ir dzīvā rinda. Velciet kartīti, lai mainītu statusu. Ready ir tas, ko vietējie aģenti var sākt.",
  "learn.backlog":
    "Backlog ir tas pats darbs sarakstā. Izmantojiet, lai ātri pārskatītu vai mainītu statusu.",
  "learn.roadmap":
    "Ceļa karte grupē darbu pēc atskaites punktiem. Atkarības rāda, kuram uzdevumam jābeidzas agrāk.",
  "learn.context":
    "Konteksts ir dzīvais AGENTS.md. Kompilējiet sesijas brīfu šeit vai no uzdevuma. Koda indekss nav obligāts.",
  "learn.agents":
    "Aģentos jūs izsniedzat projekta marķieri un skatāt sesijas. Beacon nepalaiz mitinātu koda aģentu.",
  "learn.decisions":
    "Lēmumi un ierobežojumi ir noturīgi noteikumi, kurus aģentiem nevajadzētu apiet.",
  "learn.reports":
    "Pārskati ir dēļa kontrolpunkts plus importētās recenzijas. Aģenti var tās lasīt un veidot uzdevumus.",
  "learn.settings":
    "Iestatījumi glabā projekta id, dalībniekus un apgabalu iezīmes. Apgabali ir prefiksi, nevis brīvas birkas.",
  "learn.check": "Kā pārbaudīt pabeigtu uzdevumu",
  "learn.checkBody":
    "Atveriet uzdevumu. Izlasiet How to check. Izpildiet šos soļus lietotnē. Ja piezīmes ir tukšas, lūdziet aģentam tās ierakstīt finish_work.",
  "learn.multi": "Vairāk nekā viens projekts",
  "learn.multiBody":
    "Katram Beacon projektam vajadzīgs savs marķieris. Atkārtoti palaidiet beacon connect vai beacon setup ar šo id. Vietējais MCP pārslēdzas ar --project, BEACON_PROJECT vai project_id rīka izsaukumā.",
  "learn.guide": "Pirmā stunda",
  "learn.guideOne": "1. Kontekstā uzrakstiet Goals un Definition of Done.",
  "learn.guideTwo": "2. Izveidojiet Ready uzdevumu ar aizpildītu How to check.",
  "learn.guideThree": "3. Aģentos izsniedziet marķieri un palaidiet setup.cmd vai beacon setup.",
  "learn.guideFour":
    "4. Ļaujiet aģentam izsaukt start_work, tad apstipriniet izmaiņu pēc How to check.",
  "settings.language": "Valoda",
  "settings.languageHint": "Izvēlieties etiķešu, tukšo stāvokļu un šīs Uzzināt lapas valodu.",
  "home.welcome":
    "Izveidojiet projektu, lai sāktu dēli. Beacon pievienos pirmo atskaites punktu un dažus sākuma uzdevumus.",
};
