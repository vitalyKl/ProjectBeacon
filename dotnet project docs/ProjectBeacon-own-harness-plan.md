# ProjectBeacon — workstation-native agent harness

**Состояние на 2026-11-07:** NativeAgentRuntime — единственная поддерживаемая реализация agent runtime. Legacy process integration, server API adapter, config writer, runtime selector и parity mode удалены. Исторические данные и зашифрованные provider credentials сохраняются миграциями.

CI для feature-ветки удаления должен быть зелёным на Windows, Linux и macOS до слияния. После этого требуется live acceptance на реальной workstation с выбранной локальной моделью и облачными provider credentials; CI сам по себе не заменяет этот прогон.

## Цель и границы

Workstation client выполняет агентский цикл на машине пользователя: модель → MCP-инструменты → модель. Поддерживаются локальные модели через llama-server/ClientLlamaSwap и облачные provider через OpenAI-compatible и Anthropic Messages API.

ProjectBeacon API/Web/Worker хранят задачи, права, команды, чат-сессии и зашифрованные provider credentials, но не запускают модель и не выполняют код пользователя. Существующие beacon MCP tools используются повторно.

## Архитектурные решения

- **D0 — место исполнения:** agent loop выполняется внутри workstation client, не в API/Web/Worker.
- **D1 — доступ к API:** короткоживущий `bcn_` harness token выдаётся сервером только для действующей команды. В БД хранится hash; сырой токен передаётся только в environment дочернего MCP-процесса и не попадает в argv, transcript или logs. Eval token сверяется с persisted EvalRun, task, project и command generation.
- **D2 — команды ОС:** `run_command` запускается без shell, с аргументами процесса, timeout и ограничением вывода; `run_shell` поддерживает обычные команды и цепочки. В интерактивном чате оба инструмента блокируются до явного ответа пользователя: один запуск, текущий чат или постоянно для workstation. Постоянный grant по умолчанию выключен и отзывается в локальных настройках. Команды работают с правами учётной записи workstation и не являются OS sandbox. Для pipeline Actor требуются persisted subtask grant и workstation AllowedCommandPrefixes; pipeline/eval не ждут интерактивного ответа.
- **D3 — transcript:** сессии Native хранятся локальными файлами под `agentDataDir/sessions`; старый `opencodeDataDir` считывается только как миграционный alias. Сервер получает части чата, а не локальный transcript или секреты.
- **D4 — credentials:** provider connections имеют нейтральную модель `AgentProviderConnection`. Секреты шифруются в БД и доставляются только авторизованному устройству через device-only provider connections API. Native runtime загружает credentials при необходимости; ключи не сохраняются в transcript, argv, logs или локальных config-файлах.

## Реализация

### 1. Runtime и daemon

**Native-only реализация подготовлена к выпуску в основной ветке.**

- `IAgentRuntime` и `IAgentRuntimeCapabilities` остаются контрактом runtime; production composition создаёт только `NativeAgentRuntime`.
- Runtime selector в Workstation settings удаляется. Старый `agentRuntime` JSON key игнорируется; workstation всегда запускает Native.
- Legacy server process, HTTP/event adapter, runtime-specific gate и fallback paths удалены. Остаётся нейтральный `DaemonAgentRuntimeGate`.
- Readiness проверяет рабочую директорию и возвращает предупреждения локального runtime без сетевого обращения к provider.
- Heartbeat не запускает модель и не переключает runtime.

### 2. Provider и streaming

- OpenAI-compatible SSE provider обрабатывает text, reasoning, indexed tool-call fragments, usage и finish reason.
- Anthropic Messages provider поддерживает thinking, text, tool input, usage и stop reason; ошибки не раскрывают response body/секреты.
- Повтор transient network/HTTP 503 разрешён только до выдачи частичного output, чтобы не дублировать ответ и side effects.
- Для llama-server readiness показывает предупреждение, когда локальная model command не включает `--jinja`. Сам флаг не гарантирует совместимость chat template: tool-call template проверяется live acceptance.

### 3. Native agent loop и session state

- Создание/восстановление локальной сессии, стабильные part IDs, streaming snapshots, abort, usage и persistence.
- Восстановление незавершённых tool calls не повторяет потенциальный side effect: в transcript записывается interrupted result.
- Контекст сжимается безопасно: tool call и связанный result не разделяются; пустой summary не удаляет исходную историю.
- Есть лимит шагов, защита от повторения идентичного tool call и truncation больших tool results.
- Tool calls выполняются параллельно; каждый MCP request получает собственный workspace/index state. Ответы сохраняются атомарно и маршрутизируются по JSON-RPC request ID. Результаты добавляются в transcript в порядке tool index.
- Native tool calls сохраняются отдельными структурированными chat parts со стабильным external ID, именем инструмента, input, result, статусом и длительностью.
- Одновременные stdio requests ограничены сервером (max 32 in-flight).
- При timeout/cancellation eval сначала отправляет Abort и ждёт завершения. Если runtime не подтвердил остановку, detached worktree сохраняется; каталог не удаляется под потенциально работающим процессом.

### 4. MCP и permissions

- `McpStdioClient` запускает существующий `beacon mcp --root` и передаёт одноразовые credentials только через child-process environment.
- Pipeline token проверяется по persisted command lease/generation, project, task, session, role и subtask. Actor tools берутся из persisted subtask, не из клиентского payload.
- Planner/Reviewer/Actor имеют разные MCP allowlists. Actor не может сменить subtask/session, запускать supervisor tools или записываться за чужую подзадачу.
- `run_command` требует subtask grant и workstation allowlist; pipeline shell требует отдельного subtask grant.
- Интерактивный chat shell и run_command показывают точную команду и рабочую директорию, не запускаются до подтверждения и поддерживают grants once/session/workstation.

### 5. Pipeline orchestration

- RunPipelineSession и очередь команд используют lease/generation и at-most-once delivery.
- Native runtime получает short-lived token и scoped authorization; command completion сохраняет external session ID и переводит pipeline session в terminal state.
- Actor stage claim и создание session выполняются атомарно с учётом EF execution strategy.
- MCP allowlists, command grants и task/session/subtask scope покрываются тестами.
- **Live acceptance:** реальная модель должна выполнить Planner → task_create_subtask → Actor → разрешённые файловые/командные инструменты → subtask_report_result → Reviewer verdict.

### 6. Eval

- Остаётся существующий A/B eval WithBrief / WithoutBrief с одинаковыми закреплёнными model controls; оба запуска выполняются Native.
- Удалён runtime parity comparison, поскольку поддерживается один runtime.
- Старые EvalRun runtime labels нормализуются миграцией, исторические comparison conditions сохраняются под нейтральными enum values.
- EvalRun закрепляет model, temperature, reasoning effort, permissions, timeout, repository revision и check command.
- Каждый run выполняется в отдельном detached worktree на закреплённой ревизии; рабочая копия пользователя остаётся чистой.
- Transcript refs имеют Native prefix. Pending eval commands от прежнего runtime должны быть завершены миграцией, а не повторно выданы Native runtime.

### 7. Миграция данных и очистка

- Legacy provider credentials переносятся в `AgentProviderConnection`; таблица и индекс переименовываются без удаления строк или ciphertext.
- Поле модели переименовано в `ProviderModelId` без изменения значений.
- Существующие workstation transcript paths переносятся из legacy-ключа каталога в `agentDataDir` без перемещения файлов.
- Старые команды внешней конфигурации выводятся из эксплуатации и не должны переисполняться новым клиентом.
- Исторические EF migrations с прежними именами остаются в цепочке миграций; новые migrations выполняют безопасное переименование и backfill.

## Обязательные автоматические проверки

CI должен проходить на Windows, Linux и macOS:

- provider stream: reasoning + text + tool calls; multiple tool indexes; Unicode/fragmented arguments; malformed stream; retryable HTTP 429/503; ошибки без утечки тела ответа;
- runtime: создание и восстановление session, stable parts, truncation, compaction, защита от повторных вызовов, abort, retry до output и отсутствие retry после частичного output;
- tool loop: параллельное выполнение, сохранение результатов по индексу и получение полного набора результатов до следующего model step;
- tool UI parts: раздельные Native tool cards, streaming → final metadata и защита завершённой карточки от запоздавшей streaming-дельты;
- MCP: allowlists, scope и выдача token только действующей команде;
- eval: WithBrief/WithoutBrief на Native, scoped token, миграция и transcript refs;
- daemon: Native-only composition, settings migration и отсутствие зависимости от внешнего agent executable/process/API;
- database: сохранность provider credentials при переименовании таблицы, индекса и model column; безопасное прекращение pending legacy commands.

## Последний оставшийся этап: live acceptance на workstation

После зелёного CI на точном merge candidate выполнить контролируемый прогон:

1. Запустить свежий workstation client на чистой машине, без стороннего agent runtime. Убедиться, что daemon стартует, readiness отображается, а никакие внешние executable/ports/processes не проверяются.
2. Выполнить обычный chat turn и turn с реальными MCP tools; проверить streaming text/reasoning, tool results, usage и отсутствие повторного выполнения side effects.
3. Запустить pipeline на тестовой задаче: Planner создаёт subtask; Actor выполняет разрешённую работу и отправляет subtask_report_result; Reviewer выставляет verdict. Проверить завершение command/session и отсутствие повторного запуска.
4. Запустить WithBrief/WithoutBrief eval на одной revision, сравнить check exit code/output и token usage.
5. Проверить, что short-lived token отсутствует в transcript, logs, argv и файлах конфигурации.
6. Проверить подключение Anthropic и OpenAI-compatible provider через нейтральную provider connections страницу и доставку credentials на устройство. Убедиться, что ключи не записываются на диск.

Live acceptance требует реальной workstation и provider/model endpoints, поэтому он не заменяется unit tests или GitHub CI. Его результат следует зафиксировать в PR: модель/provider, revision, пройденные pipeline stages, eval results и найденные ограничения.

## Не является блокером

- Таблица цен и расчёт TotalCost для облачных моделей: token usage учитывается; точное ценообразование требует отдельного версионируемого источника тарифов.
- Swarm/research, HTTP MCP и server-hosted agents вне текущего объёма. Интерактивное подтверждение workstation-команд работает только в native chat; pipeline/eval не запрашивают пользовательский ввод.

## Важные риски

- `--jinja` необходим для принятой конфигурации llama-server tool calling, но не гарантирует корректный chat template.
- Provider credentials передаются только авторизованному device actor. Любая новая возможность MCP должна проходить через server-side actor, project, task, session и subtask проверки.
- Исторические миграции и записи нельзя удалять ради чистоты исходников: совместимость существующих БД и сохранность секретов важнее переименования старых идентификаторов в git history.
