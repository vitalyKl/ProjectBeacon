# ProjectBeacon — собственный workstation-native agent harness

Состояние на 2026-10-30: **все изменения, которые можно проверить без запуска реальной модели, реализованы в feature/native-agent-harness**. Остался финальный живой acceptance-прогон на workstation с локальной моделью, настоящим MCP server и полным Planner → Actor → Reviewer pipeline. OpenCode остаётся поддерживаемым альтернативным runtime; переключение default не выполняется автоматически.

GitHub Actions должен быть зелёным на последнем commit этой ветки до начала live acceptance. CI запускается на каждый push; актуальный результат и commit смотрите в PR #5. Результаты CI на более ранних commit не считаются проверкой текущего HEAD.

## Цель и границы

beacon client выполняет агентский цикл на машине пользователя: модель → MCP-инструменты → модель. Моделью может быть локальный llama-server через ClientLlamaSwap или облачный provider. Сервер ProjectBeacon хранит задачи, права, команды и чат-сессии, но не запускает модель и не выполняет код пользователя.

AgentRuntime = "opencode" | "native" хранится на уровне workstation; default остаётся opencode. native не требует OpenCode для chat/agent loop. Существующие beacon mcp инструменты переиспользуются, второй независимый набор инструментов не создаётся.

## Архитектурные решения

- **D0 — место исполнения:** agent loop работает только внутри beacon client на машине пользователя, не на API/Web/Worker.
- **D1 — доступ к API:** короткоживущий bcn_ harness token выпускается сервером только для действующей команды. В БД хранится hash, сырой токен передаётся только в environment дочернего MCP процесса и не попадает в argv, transcript или logs. Для native eval токен выпускается только после сверки eval run, task, project и закреплённого runtime.
- **D2 — команды ОС:** run_command запускается без shell, с аргументами процесса, рабочей директорией в корне workspace, timeout и ограничением вывода. Для pipeline Actor требуются одновременно persisted subtask grant и workstation AllowedCommandPrefixes; default allowlist пуст.
- **D3 — transcript:** native session хранится локальными файлами в sessions/ рядом с OpencodeDataDir; сервер получает части чата, а не локальный transcript/секреты.

## Реализация по шагам

### 1. Runtime seam и выбор

**Готово.**

- IAgentRuntime и IAgentRuntimeCapabilities абстрагируют OpenCode и Native; DaemonServices выбирает реализацию по сохранённому WorkstationSettings.AgentRuntime.
- Настройка доступна на странице Workstations и локализована для поддерживаемых языков. Сервер сохраняет только допустимые opencode/native; неизвестное значение не принимается.
- Тесты проверяют сохранение настройки, выбор Native и загрузку native из файла настроек при создании нового daemon.
- Readiness Native проверяет локальную рабочую директорию и статически диагностирует отсутствие --jinja в командах моделей llama-swap. Диагностика возвращается в runtime status; сетевой запрос к модели не выполняется, поэтому live readiness остаётся частью реального прогона.
- Опрос статуса хода не завершает Native turn только из-за долгого периода без текстовых дельт. Abort отменяет активный provider stream.

### 2. Provider и streaming

**Готово; живые endpoint smoke tests входят в оставшийся acceptance.**

- OpenAI-compatible SSE provider обрабатывает текст, reasoning, indexed tool-call fragments, usage и finish reason. Проверяются несколько индексов, разорванные JSON-аргументы и Unicode, отсутствие terminal [DONE], malformed stream и HTTP 429 retryability.
- Anthropic Messages provider поддерживает thinking, text, tool input, usage и stop reason; ошибки не раскрывают response body/секреты.
- Native runtime повторяет transient network/HTTP 503 ошибки, если до этого не было выдано частичного output; после частичного output повтор не делается, чтобы не дублировать ответ/side effects.
- Для llama-server readiness показывает предупреждение, когда локальные model commands не включают --jinja. Само наличие флага не гарантирует совместимость конкретного chat template — это проверяется живым tool-call прогоном.

### 3. Native agent loop и session state

**Готово.**

- Создание/восстановление локальной сессии, стабильные part IDs, фоновый SendPromptAsync, streaming snapshots, abort, usage и persistence.
- Восстановление незавершённых tool calls не повторяет потенциальный side effect: в transcript записывается явный interrupted result.
- Контекст сжимается безопасно: tool call и связанный result не разделяются; пустой summary не удаляет исходную историю.
- Есть лимит шагов, защита от повторения идентичного tool call и truncation больших tool results.
- Tool calls выполняются параллельно на стороне native runtime и внутри самого MCP stdio server. Каждый входящий MCP request получает собственный workspace/index state; responses записываются атомарно, а клиент маршрутизирует их по JSON-RPC request ID. Результаты tool calls добавляются в transcript в порядке tool index, а не в порядке завершения задач.
- Одновременные stdio requests ограничены сервером (max 32 in-flight), чтобы не допустить неограниченного накопления запросов.
- При timeout/cancellation eval сначала отправляет Abort и ждёт подтверждения завершения. Если runtime не подтвердил остановку, detached worktree сохраняется и путь включается в ошибку; каталог не удаляется под потенциально работающим процессом.

### 4. MCP и permissions

**Готово для автоматической проверки.**

- McpStdioClient запускает существующий beacon mcp --root и передаёт одноразовые credentials только через ограниченный child-process environment.
- Pipeline token проверяется по сохранённым command lease/generation, project, task, session, role и subtask. Список Actor tools берётся из persisted subtask, а не из клиентского payload.
- Native eval token выдаётся только для существующего EvalRun, совпадающего по project/task и закреплённого за native.
- Planner/Reviewer/Actor имеют разные MCP allowlists. Actor не может сменить subtask/session, запускать supervisor tools или записываться за чужую подзадачу.
- run_command требует subtask grant и workstation allowlist; shell не используется.

### 5. Cloud providers и credentials

**Реализация и unit coverage готовы; живые запросы к реальным endpoints остаются частью live acceptance.**

- Anthropic и OpenAI-compatible provider выбираются по настройке подключения.
- Ключи берутся из существующего workstation connection delivery, живут в памяти runtime и не включаются в transcript, файлы конфигурации, argv или logs.
- Существующая сущность OpenCodeConnection используется повторно; отдельная миграция секретов не нужна.

### 6. Pipeline orchestration

**Код, permissions, транзакционные ограничения и тесты готовы. Остаётся только end-to-end запуск на реальной модели.**

- RunPipelineSession и очередь команд используют lease/generation и at-most-once delivery.
- Native runtime получает short-lived token и scoped authorization; command completion сохраняет external session id и переводит pipeline session в корректное terminal state.
- Actor stage claim и создание session выполняются атомарно с учётом EF execution strategy; тестами покрыты rollback, повторная доставка, завершение/ошибка/aborted и malformed result.
- MCP allowlists, command grants и task/session/subtask scope покрыты тестами.
- **Не выполнено только live acceptance:** реальная модель должна выполнить Planner → task_create_subtask → Actor → файловые/разрешённые инструменты и subtask_report_result → Reviewer verdict.

### 7. Eval parity и runtime migration

**Готово.**

- Runtime selector сохранён локализованным UI, миграция AgentRuntime additive и default opencode.
- EvalRun.Runtime добавлен отдельной миграцией с backfill/default opencode для существующих записей.
- Существующий A/B eval WithBrief / WithoutBrief сохранён без изменения семантики.
- CompareRuntimes=true создаёт пару OpenCode / Native на одинаковом prompt (с одним и тем же compiled brief), revision, model, temperature, reasoning effort, tools, timeout и check command. Каждый run выполняется в своём detached git worktree на закреплённой ревизии; рабочая копия пользователя остаётся чистой и результаты двух запусков не смешиваются. OpenCode eval использует отдельный временный OpenCode server, не перезапускает интерактивную сессию пользователя. EvalRun хранит runtime, а payload каждой команды закрепляет конкретный runtime.
- RunEvalTurn выбирает runtime по payload, а не по глобальной настройке workstation. Native eval получает отдельный scoped token. Transcript ref использует соответствующий native: или opencode: prefix.
- Добавлены application tests на одинаковые controls/prompt обоих run, invalid runtime, persisted runtime pin, token scope и CLI test на передачу native authorization в runtime.

## Обязательные автоматические проверки

Эти проверки должны оставаться зелёными в CI на Windows, Linux и macOS:

- provider stream: reasoning + text + tool calls; multiple tool indexes; Unicode/fragmented arguments; отсутствие [DONE]; retryable HTTP 429/503; ошибки без утечки тела ответа;
- runtime: создание и восстановление session, stable parts, truncation, compaction, защита от повторных вызовов, abort, transient retry до output и отсутствие retry после частичного output;
- tool loop: параллельное выполнение, сохранение результатов по индексу и полный набор результатов до следующего model step;
- MCP: allowlists, scope и выдача token только действующей команде;
- eval: OpenCode/Native pin, миграция и транскрипт правильного runtime;
- daemon: persisted runtime selection после reload настроек.

## Последний оставшийся этап: live acceptance на workstation

До начала прогона CI на актуальном HEAD должен быть зелёным. Далее требуется один контролируемый сценарий на реальной workstation:

1. Установить AgentRuntime=native, выбрать локальную модель и убедиться, что llama-server запускается с --jinja и поддерживает tool-call template.
2. Выполнить обычный chat-turn и turn с реальными MCP tools; проверить streaming text/reasoning, tool results, usage и отсутствие повторного выполнения side effects.
3. Запустить pipeline на тестовой задаче: Planner создаёт subtask; Actor выполняет разрешённую работу, при необходимости запускает только allowlisted command и отправляет subtask_report_result; Reviewer выставляет verdict. Проверить завершение command/session и отсутствие повторного запуска.
4. Запустить runtime parity eval на одном revision: OpenCode и Native получают одинаковые закреплённые controls и prompt; сравнить check exit code/output, tokens и transcript refs.
5. Проверить, что короткоживущий token отсутствует в transcript, логах, argv и файлах конфигурации.
6. Если на workstation настроены облачные подключения, сделать по одному smoke turn через Anthropic и OpenAI-compatible provider; проверить отсутствие записи ключей на диск.

Результат этого прогона нужно записать в PR: выбранная модель/provider, revision, успешность pipeline стадий, eval comparison и найденные ограничения. До него native остаётся opt-in; opencode остаётся default.

## Не является блокером

- Таблица цен и расчёт TotalCost для облачных моделей: token usage уже учитывается; точное ценообразование требует отдельного версионируемого источника тарифов, поэтому не подменяется предположительными значениями.
- Swarm/research, interactive approval risky commands, HTTP MCP и server-hosted agents — вне текущего объёма.

## Важные риски

- --jinja необходим для llama-server tool calling в принятой конфигурации, но сам по себе не гарантирует корректный chat template.
- Native runtime пока не становится default автоматически: окончательное решение принимается только после parity eval на одинаковых revision и controls.
- Любая новая возможность MCP должна проходить через действующие server-side actor, project, task, session и subtask проверки.
