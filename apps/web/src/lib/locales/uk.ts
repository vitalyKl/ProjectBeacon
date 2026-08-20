import type { MessageKey } from "./en";

export const uk: Partial<Record<MessageKey, string>> = {
  "nav.home": "Головна",
  "nav.board": "Дошка",
  "nav.backlog": "Беклог",
  "nav.roadmap": "Дорожня карта",
  "nav.context": "Контекст",
  "nav.files": "Файли",
  "nav.agents": "Агенти",
  "nav.decisions": "Рішення",
  "nav.reports": "Звіти",
  "nav.settings": "Налаштування",
  "nav.learn": "Довідка",
  "common.loading": "Завантаження…",
  "common.selectProject": "Оберіть проєкт у шапці.",
  "common.language": "Мова",
  "common.org": "Орг",
  "common.project": "Проєкт",
  "common.logout": "Вийти",
  "common.failedSession": "не вдалося завантажити сесію",
  "common.failedProjects": "не вдалося завантажити проєкти",
  "common.logoutFailed": "не вдалося вийти",
  "reports.title": "Звіти",
  "reports.intro":
    "Знімок поточної дошки або імпорт рев’ю, яке агенти можуть прочитати й перетворити на задачі.",
  "reports.generate": "Згенерувати звіт",
  "reports.generating": "Генерація…",
  "reports.snapshots": "Знімки",
  "reports.emptyReports": "Звітів ще немає. Згенеруйте контрольний знімок, коли будете готові.",
  "reports.reviews": "Імпортовані рев’ю",
  "reports.reviewsIntro":
    "Вставте рев’ю, аудит або нотатку. Агенти можуть читати їх і створювати задачі.",
  "reports.reviewTitle": "Заголовок (необов’язково)",
  "reports.reviewBody": "Markdown рев’ю",
  "reports.import": "Імпортувати рев’ю",
  "reports.importing": "Імпорт…",
  "reports.emptyReviews": "Імпортованих рев’ю ще немає.",
  "reports.loadFailed": "не вдалося завантажити звіти",
  "reports.generateFailed": "не вдалося згенерувати звіт",
  "reports.importFailed": "не вдалося імпортувати рев’ю",
  "learn.title": "Довідка",
  "learn.intro":
    "Beacon — операційна система навколо ваших агентів. Люди тримають бриф і дошку. Локальні агенти беруть Ready-задачі.",
  "learn.start": "Почніть тут",
  "learn.startBody":
    "Створіть або оберіть проєкт у шапці. Напишіть бриф у Контексті. Поставте роботу в Ready. Згенеруйте токен на Агентах і запустіть setup на машині агента.",
  "learn.screens": "Навіщо кожен екран",
  "learn.home": "Головна — поточний знімок: бриф, віхи, індекс і робота, що готова або в роботі.",
  "learn.board":
    "Дошка — жива черга. Перетягніть картку, щоб змінити статус. Ready — те, що можуть брати локальні агенти.",
  "learn.backlog": "Беклог — та сама робота списком. Зручно швидко переглянути або змінити статус.",
  "learn.roadmap":
    "Дорожня карта групує роботу за віхами. Залежності показують, яка задача має завершитися раніше.",
  "learn.context":
    "Контекст — живий AGENTS.md. Компілюйте сесійний бриф звідси або з задачі. Індекс коду необов’язковий.",
  "learn.agents":
    "На Агентах випускають токен проєкту і дивляться сесії. Beacon не запускає хостованого кодувального агента.",
  "learn.decisions": "Рішення і обмеження — стійкі правила, які агенти не повинні вигадувати.",
  "learn.reports":
    "Звіти — контрольний знімок дошки плюс імпортовані рев’ю. Агенти можуть читати їх і створювати задачі.",
  "learn.settings":
    "Налаштування зберігають id проєкту, учасників і області. Області — префікси шляхів, не вільні чіпи.",
  "learn.check": "Як перевірити завершену задачу",
  "learn.checkBody":
    "Відкрийте задачу. Прочитайте How to check. Виконайте ці кроки в застосунку. Якщо поле порожнє, попросіть агента записати його в finish_work.",
  "learn.multi": "Кілька проєктів",
  "learn.multiBody":
    "Кожному проєкту потрібен свій токен. Знову запустіть beacon connect або beacon setup з цим id. Локальний MCP перемикається через --project, BEACON_PROJECT або project_id у виклику інструмента.",
  "learn.guide": "Перша година",
  "learn.guideOne": "1. Напишіть Goals і Definition of Done у Контексті.",
  "learn.guideTwo": "2. Створіть Ready-задачу з How to check.",
  "learn.guideThree": "3. Згенеруйте токен на Агентах і запустіть setup.cmd або beacon setup.",
  "learn.guideFour": "4. Нехай агент викличе start_work, потім перевірте зміну за How to check.",
  "settings.language": "Мова",
  "settings.languageHint": "Оберіть мову підписів, порожніх станів і цієї сторінки.",
  "home.welcome":
    "Створіть проєкт, щоб почати дошку. Beacon додасть першу віху і кілька стартових задач.",
};
