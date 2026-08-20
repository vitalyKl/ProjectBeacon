import type { MessageKey } from "./en";

export const ko: Partial<Record<MessageKey, string>> = {
  "nav.home": "홈",
  "nav.board": "보드",
  "nav.backlog": "백로그",
  "nav.roadmap": "로드맵",
  "nav.context": "컨텍스트",
  "nav.files": "파일",
  "nav.agents": "에이전트",
  "nav.decisions": "결정",
  "nav.reports": "보고서",
  "nav.settings": "설정",
  "nav.learn": "배우기",
  "common.loading": "불러오는 중…",
  "common.selectProject": "헤더에서 프로젝트를 선택하세요.",
  "common.language": "언어",
  "common.org": "조직",
  "common.project": "프로젝트",
  "common.logout": "로그아웃",
  "common.failedSession": "세션을 불러오지 못했습니다",
  "common.failedProjects": "프로젝트를 불러오지 못했습니다",
  "common.logoutFailed": "로그아웃에 실패했습니다",
  "reports.title": "보고서",
  "reports.intro":
    "현재 보드의 스냅샷을 만들거나 리뷰를 가져와 에이전트가 읽고 후속 작업을 만들 수 있게 하세요.",
  "reports.generate": "보고서 생성",
  "reports.generating": "생성 중…",
  "reports.snapshots": "스냅샷",
  "reports.emptyReports":
    "아직 보고서가 없습니다. 에이전트가 읽을 수 있는 점검 지점이 필요할 때 생성하세요.",
  "reports.reviews": "가져온 리뷰",
  "reports.reviewsIntro":
    "리뷰, 감사 또는 메모를 붙여 넣으세요. 에이전트가 목록을 보고 발견 사항을 작업으로 바꿀 수 있습니다.",
  "reports.reviewTitle": "제목(선택)",
  "reports.reviewBody": "리뷰 Markdown",
  "reports.import": "리뷰 가져오기",
  "reports.importing": "가져오는 중…",
  "reports.emptyReviews": "아직 가져온 리뷰가 없습니다.",
  "reports.loadFailed": "보고서를 불러오지 못했습니다",
  "reports.generateFailed": "보고서를 생성하지 못했습니다",
  "reports.importFailed": "리뷰를 가져오지 못했습니다",
  "learn.title": "배우기",
  "learn.intro":
    "Beacon은 에이전트 주변의 운영 체제입니다. 사람이 브리프와 보드를 유지하고, 로컬 에이전트가 Ready 작업을 가져갑니다.",
  "learn.start": "여기서 시작",
  "learn.startBody":
    "헤더에서 프로젝트를 만들거나 고르세요. 컨텍스트에 살아있는 브리프를 쓰세요. 보드에 Ready 작업을 올리세요. 에이전트에서 프로젝트 토큰을 발급하고 에이전트가 있는 기기에서 setup을 실행하세요.",
  "learn.screens": "각 화면의 용도",
  "learn.home":
    "홈은 현재 스냅샷입니다. 브리프, 마일스톤, 인덱스 상태, 준비되었거나 진행 중인 작업.",
  "learn.board":
    "보드는 살아있는 대기열입니다. 카드를 끌어 상태를 바꿉니다. Ready는 로컬 에이전트가 시작할 수 있는 일입니다.",
  "learn.backlog": "백로그는 같은 일의 목록입니다. 빠르게 훑거나 상태를 바꿀 때 사용하세요.",
  "learn.roadmap":
    "로드맵은 마일스톤별로 일을 묶습니다. 의존성은 어떤 작업이 먼저 끝나야 하는지 보여 줍니다.",
  "learn.context":
    "컨텍스트는 살아있는 AGENTS.md입니다. 여기 또는 작업에서 세션 브리프를 컴파일하세요. 코드 인덱스는 선택입니다.",
  "learn.agents":
    "에이전트에서 프로젝트 토큰을 발급하고 세션을 봅니다. Beacon은 호스팅된 코딩 에이전트를 실행하지 않습니다.",
  "learn.decisions": "결정과 제약은 에이전트가 둘러서 만들지 말아야 할 지속적인 규칙입니다.",
  "learn.reports":
    "보고서는 보드 점검 지점과 가져온 리뷰입니다. 에이전트가 목록을 보고 발견 사항을 작업으로 바꿀 수 있습니다.",
  "learn.settings":
    "설정에는 프로젝트 id, 구성원, 영역 레이블이 있습니다. 영역은 접두사이며 자유 칩이 아닙니다.",
  "learn.check": "완료된 작업을 확인하는 방법",
  "learn.checkBody":
    "작업을 엽니다. How to check를 읽습니다. 앱에서 그 단계를 따릅니다. 메모가 비어 있으면 에이전트에게 finish_work에 쓰라고 요청하세요.",
  "learn.multi": "프로젝트가 둘 이상일 때",
  "learn.multiBody":
    "각 Beacon 프로젝트에는 자체 토큰이 필요합니다. 해당 프로젝트 id로 beacon connect 또는 beacon setup을 다시 실행하세요. 로컬 MCP는 --project, BEACON_PROJECT 또는 도구 호출의 project_id로 전환합니다.",
  "learn.guide": "첫 한 시간",
  "learn.guideOne": "1. 컨텍스트에 Goals와 Definition of Done을 씁니다.",
  "learn.guideTwo": "2. How to check가 채워진 Ready 작업을 만듭니다.",
  "learn.guideThree": "3. 에이전트에서 토큰을 발급하고 setup.cmd 또는 beacon setup을 실행합니다.",
  "learn.guideFour": "4. 에이전트가 start_work를 호출하게 한 뒤 How to check로 변경을 확인합니다.",
  "settings.language": "언어",
  "settings.languageHint": "레이블, 빈 상태, 이 배우기 페이지의 언어를 선택하세요.",
  "home.welcome":
    "보드를 시작하려면 프로젝트를 만드세요. Beacon이 첫 마일스톤과 몇 가지 시작 작업을 추가합니다.",
};
