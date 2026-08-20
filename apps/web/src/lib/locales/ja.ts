import type { MessageKey } from "./en";

export const ja: Partial<Record<MessageKey, string>> = {
  "nav.home": "ホーム",
  "nav.board": "ボード",
  "nav.backlog": "バックログ",
  "nav.roadmap": "ロードマップ",
  "nav.context": "コンテキスト",
  "nav.files": "ファイル",
  "nav.agents": "エージェント",
  "nav.decisions": "決定",
  "nav.reports": "レポート",
  "nav.settings": "設定",
  "nav.learn": "学ぶ",
  "common.loading": "読み込み中…",
  "common.selectProject": "ヘッダーからプロジェクトを選んでください。",
  "common.language": "言語",
  "common.org": "組織",
  "common.project": "プロジェクト",
  "common.logout": "ログアウト",
  "common.failedSession": "セッションを読み込めませんでした",
  "common.failedProjects": "プロジェクトを読み込めませんでした",
  "common.logoutFailed": "ログアウトに失敗しました",
  "reports.title": "レポート",
  "reports.intro":
    "現在のボードのスナップショットを生成するか、レビューを取り込んでエージェントが読み、フォローアップタスクを作れるようにします。",
  "reports.generate": "レポートを生成",
  "reports.generating": "生成中…",
  "reports.snapshots": "スナップショット",
  "reports.emptyReports":
    "まだレポートはありません。エージェントが読めるチェックポイントが必要なときに生成してください。",
  "reports.reviews": "取り込んだレビュー",
  "reports.reviewsIntro":
    "レビュー、監査、メモを貼り付けます。エージェントは一覧し、指摘をタスクにできます。",
  "reports.reviewTitle": "タイトル（任意）",
  "reports.reviewBody": "レビューの Markdown",
  "reports.import": "レビューを取り込む",
  "reports.importing": "取り込み中…",
  "reports.emptyReviews": "まだ取り込んだレビューはありません。",
  "reports.loadFailed": "レポートを読み込めませんでした",
  "reports.generateFailed": "レポートを生成できませんでした",
  "reports.importFailed": "レビューを取り込めませんでした",
  "learn.title": "学ぶ",
  "learn.intro":
    "Beacon はエージェントの周りのオペレーティングシステムです。人がブリーフとボードを保ち、ローカルエージェントが Ready の仕事を取ります。",
  "learn.start": "ここから始める",
  "learn.startBody":
    "ヘッダーでプロジェクトを作成または選択します。コンテキストに生きたブリーフを書きます。ボードで仕事を Ready にします。エージェントでプロジェクトトークンを発行し、エージェントがあるマシンで setup を実行します。",
  "learn.screens": "各画面の役割",
  "learn.home":
    "ホームは現在のスナップショットです。ブリーフ、マイルストーン、インデックス状態、Ready または進行中の仕事。",
  "learn.board":
    "ボードは生きたキューです。カードをドラッグして状態を変えます。Ready はローカルエージェントが開始できる仕事です。",
  "learn.backlog":
    "バックログは同じ仕事のリストです。素早く眺めたり状態を変えたりするときに使います。",
  "learn.roadmap":
    "ロードマップはマイルストーンごとに仕事をまとめます。依存関係は、どのタスクが先に終わるべきかを示します。",
  "learn.context":
    "コンテキストは生きた AGENTS.md です。ここまたはタスクからセッションブリーフをコンパイルします。コードインデックスは任意です。",
  "learn.agents":
    "エージェントではプロジェクトトークンを発行し、セッションを見ます。Beacon はホスト型コーディングエージェントを実行しません。",
  "learn.decisions": "決定と制約は、エージェントが勝手に迂回してはいけない持続的なルールです。",
  "learn.reports":
    "レポートはボードのチェックポイントと取り込んだレビューです。エージェントは一覧し、指摘をタスクにできます。",
  "learn.settings":
    "設定にはプロジェクト ID、メンバー、エリアラベルがあります。エリアは接頭辞であり、自由なチップではありません。",
  "learn.check": "完了したタスクの確認方法",
  "learn.checkBody":
    "タスクを開きます。How to check を読み、アプリでその手順を実行します。メモが空なら、エージェントに finish_work で書いてもらってください。",
  "learn.multi": "複数のプロジェクト",
  "learn.multiBody":
    "Beacon の各プロジェクトには独自のトークンが必要です。そのプロジェクト ID で beacon connect または beacon setup を再実行します。ローカル MCP は --project、BEACON_PROJECT、またはツール呼び出しの project_id で切り替えます。",
  "learn.guide": "最初の1時間",
  "learn.guideOne": "1. コンテキストに Goals と Definition of Done を書く。",
  "learn.guideTwo": "2. How to check を埋めた Ready タスクを作る。",
  "learn.guideThree":
    "3. エージェントでトークンを発行し、setup.cmd または beacon setup を実行する。",
  "learn.guideFour": "4. エージェントに start_work を呼ばせ、How to check で変更を確認する。",
  "settings.language": "言語",
  "settings.languageHint": "ラベル、空の状態、この学ぶページの言語を選びます。",
  "home.welcome":
    "プロジェクトを作成してボードを始めます。Beacon が最初のマイルストーンといくつかの開始タスクを追加します。",
};
