import type { MessageKey } from "./en";

export const fr: Partial<Record<MessageKey, string>> = {
  "nav.home": "Accueil",
  "nav.board": "Tableau",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Feuille de route",
  "nav.context": "Contexte",
  "nav.files": "Fichiers",
  "nav.agents": "Agents",
  "nav.decisions": "Décisions",
  "nav.reports": "Rapports",
  "nav.settings": "Paramètres",
  "nav.learn": "Découvrir",
  "common.loading": "Chargement…",
  "common.selectProject": "Choisissez un projet dans l’en-tête.",
  "common.language": "Langue",
  "common.org": "Org",
  "common.project": "Projet",
  "common.logout": "Se déconnecter",
  "common.failedSession": "échec du chargement de la session",
  "common.failedProjects": "échec du chargement des projets",
  "common.logoutFailed": "échec de la déconnexion",
  "reports.title": "Rapports",
  "reports.intro":
    "Générez un instantané du tableau actuel, ou importez une revue pour que les agents la lisent et créent des tâches de suivi.",
  "reports.generate": "Générer un rapport",
  "reports.generating": "Génération…",
  "reports.snapshots": "Instantanés",
  "reports.emptyReports":
    "Aucun rapport pour l’instant. Générez-en un quand vous voulez un point de contrôle lisible par les agents.",
  "reports.reviews": "Revues importées",
  "reports.reviewsIntro":
    "Collez une revue, un audit ou une note. Les agents peuvent les lister et transformer les constats en tâches.",
  "reports.reviewTitle": "Titre (facultatif)",
  "reports.reviewBody": "Markdown de la revue",
  "reports.import": "Importer la revue",
  "reports.importing": "Import…",
  "reports.emptyReviews": "Aucune revue importée pour l’instant.",
  "reports.loadFailed": "échec du chargement des rapports",
  "reports.generateFailed": "échec de la génération du rapport",
  "reports.importFailed": "échec de l’import de la revue",
  "learn.title": "Découvrir",
  "learn.intro":
    "Beacon est le système d’exploitation autour de vos agents. Les humains tiennent le brief et le tableau. Les agents locaux prennent le travail Ready.",
  "learn.start": "Commencer ici",
  "learn.startBody":
    "Créez ou choisissez un projet dans l’en-tête. Rédigez le brief vivant dans Contexte. Mettez le travail Ready sur le Tableau. Créez un jeton de projet dans Agents, puis lancez setup sur la machine qui héberge l’agent.",
  "learn.screens": "À quoi sert chaque écran",
  "learn.home":
    "Accueil est l’instantané actuel : brief, jalons, état de l’index, et travail prêt ou en cours.",
  "learn.board":
    "Le tableau est la file vivante. Faites glisser une carte pour changer le statut. Ready est ce que les agents locaux peuvent commencer.",
  "learn.backlog":
    "Le backlog est le même travail en liste. Utilisez-le pour parcourir ou changer les statuts rapidement.",
  "learn.roadmap":
    "La feuille de route groupe le travail par jalon. Les dépendances indiquent quelle tâche doit finir avant qu’une autre commence.",
  "learn.context":
    "Contexte est l’AGENTS.md vivant. Compilez un brief de session ici ou depuis une tâche. L’index de code est facultatif.",
  "learn.agents":
    "Dans Agents, vous créez un jeton de projet et suivez les sessions. Beacon n’exécute pas d’agent de code hébergé.",
  "learn.decisions":
    "Les décisions et contraintes sont les règles durables que les agents ne doivent pas contourner.",
  "learn.reports":
    "Rapports est un point de contrôle du tableau plus les revues importées. Les agents peuvent les lister et en faire des tâches.",
  "learn.settings":
    "Paramètres contient l’id du projet, les membres et les libellés de zone. Les zones sont des préfixes, pas des puces libres.",
  "learn.check": "Comment vérifier une tâche terminée",
  "learn.checkBody":
    "Ouvrez la tâche. Lisez How to check. Suivez ces étapes dans l’app. Si les notes sont vides, demandez à l’agent de les écrire dans finish_work.",
  "learn.multi": "Plus d’un projet",
  "learn.multiBody":
    "Chaque projet Beacon a besoin de son propre jeton. Relancez beacon connect ou beacon setup avec cet id. Le MCP local bascule avec --project, BEACON_PROJECT, ou project_id sur un appel d’outil.",
  "learn.guide": "Première heure",
  "learn.guideOne": "1. Écrivez Goals et Definition of Done dans Contexte.",
  "learn.guideTwo": "2. Créez une tâche Ready avec How to check rempli.",
  "learn.guideThree": "3. Créez un jeton dans Agents et lancez setup.cmd ou beacon setup.",
  "learn.guideFour":
    "4. Laissez l’agent appeler start_work, puis confirmez le changement depuis How to check.",
  "settings.language": "Langue",
  "settings.languageHint":
    "Choisissez la langue des libellés, des états vides et de cette page Découvrir.",
  "home.welcome":
    "Créez un projet pour démarrer le tableau. Beacon ajoutera un premier jalon et quelques tâches de départ.",
};
