import type { MessageKey } from "./en";

export const pt: Partial<Record<MessageKey, string>> = {
  "nav.home": "Início",
  "nav.board": "Quadro",
  "nav.backlog": "Backlog",
  "nav.roadmap": "Roteiro",
  "nav.context": "Contexto",
  "nav.files": "Arquivos",
  "nav.agents": "Agentes",
  "nav.decisions": "Decisões",
  "nav.reports": "Relatórios",
  "nav.settings": "Definições",
  "nav.learn": "Aprender",
  "common.loading": "A carregar…",
  "common.selectProject": "Selecione um projeto no cabeçalho.",
  "common.language": "Idioma",
  "common.org": "Org",
  "common.project": "Projeto",
  "common.logout": "Sair",
  "common.failedSession": "falha ao carregar a sessão",
  "common.failedProjects": "falha ao carregar os projetos",
  "common.logoutFailed": "falha ao sair",
  "reports.title": "Relatórios",
  "reports.intro":
    "Gere um instantâneo do quadro atual ou importe uma revisão para os agentes lerem e criarem tarefas de seguimento.",
  "reports.generate": "Gerar relatório",
  "reports.generating": "A gerar…",
  "reports.snapshots": "Instantâneos",
  "reports.emptyReports":
    "Ainda não há relatórios. Gere um quando quiser um ponto de controlo que os agentes possam ler.",
  "reports.reviews": "Revisões importadas",
  "reports.reviewsIntro":
    "Cole uma revisão, auditoria ou nota. Os agentes podem listá-las e transformar achados em tarefas.",
  "reports.reviewTitle": "Título (opcional)",
  "reports.reviewBody": "Markdown da revisão",
  "reports.import": "Importar revisão",
  "reports.importing": "A importar…",
  "reports.emptyReviews": "Ainda não há revisões importadas.",
  "reports.loadFailed": "falha ao carregar os relatórios",
  "reports.generateFailed": "falha ao gerar o relatório",
  "reports.importFailed": "falha ao importar a revisão",
  "learn.title": "Aprender",
  "learn.intro":
    "Beacon é o sistema operativo à volta dos seus agentes. As pessoas mantêm o brief e o quadro. Os agentes locais pegam no trabalho Ready.",
  "learn.start": "Comece aqui",
  "learn.startBody":
    "Crie ou escolha um projeto no cabeçalho. Escreva o brief vivo em Contexto. Ponha o trabalho Ready no Quadro. Crie um token de projeto em Agentes e execute o setup na máquina do agente.",
  "learn.screens": "Para que serve cada ecrã",
  "learn.home":
    "Início é o instantâneo atual: brief, marcos, estado do índice e trabalho pronto ou em curso.",
  "learn.board":
    "O quadro é a fila viva. Arraste um cartão para mudar o estado. Ready é o que os agentes locais podem começar.",
  "learn.backlog":
    "O backlog é o mesmo trabalho em lista. Use-o para rever ou mudar estados depressa.",
  "learn.roadmap":
    "O roteiro agrupa o trabalho por marco. As dependências mostram que tarefa tem de terminar antes de outra começar.",
  "learn.context":
    "Contexto é o AGENTS.md vivo. Compile um brief de sessão daqui ou de uma tarefa. O índice de código é opcional.",
  "learn.agents":
    "Em Agentes cria um token de projeto e vê sessões. O Beacon não executa um agente de código alojado.",
  "learn.decisions":
    "Decisões e restrições são as regras duradouras que os agentes não devem contornar.",
  "learn.reports":
    "Relatórios é um ponto de controlo do quadro mais revisões importadas. Os agentes podem listá-las e criar tarefas.",
  "learn.settings":
    "Definições guarda o id do projeto, membros e etiquetas de área. As áreas são prefixos, não chips livres.",
  "learn.check": "Como verificar uma tarefa concluída",
  "learn.checkBody":
    "Abra a tarefa. Leia How to check. Siga esses passos na app. Se as notas estiverem vazias, peça ao agente que as escreva em finish_work.",
  "learn.multi": "Mais do que um projeto",
  "learn.multiBody":
    "Cada projeto Beacon precisa do seu próprio token. Volte a executar beacon connect ou beacon setup com esse id. O MCP local muda com --project, BEACON_PROJECT ou project_id numa chamada de ferramenta.",
  "learn.guide": "Primeira hora",
  "learn.guideOne": "1. Escreva Goals e Definition of Done em Contexto.",
  "learn.guideTwo": "2. Crie uma tarefa Ready com How to check preenchido.",
  "learn.guideThree": "3. Crie um token em Agentes e execute setup.cmd ou beacon setup.",
  "learn.guideFour": "4. Deixe o agente chamar start_work e confirme a alteração em How to check.",
  "settings.language": "Idioma",
  "settings.languageHint":
    "Escolha o idioma das etiquetas, estados vazios e desta página Aprender.",
  "home.welcome":
    "Crie um projeto para começar o quadro. O Beacon adicionará um primeiro marco e algumas tarefas iniciais.",
};
