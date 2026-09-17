# План: Task-пайплайн (Planner → Actor → Cold Review) + оркестрация локальных моделей

Исходники: `dotnet project docs/features.md` (Блок A — Model Orchestration, Блок B — Task-пайплайн).
Документ — рабочий: к нему возвращаемся на каждом шаге имплементации.

---

## 0. Ограничения и исходные факты

### 0.1 Жёсткое ограничение пользователя
- **Не создавать новые модули (.csproj).** Всё — дополнения к существующим проектам:
  - Task получает этапы пайплайна + подзадачи (расширение `TaskItem` и `Application/Tasks/`).
  - Страница Agents — работа с локальными моделями/агентами (расширение заглушки `Agents.razor` + новая feature-папка `Application/Agents/`).

### 0.2 Конвенции репо (проверено по коду)
- CQRS: `ICommand<Result<T>>` / `IQuery<T>`, хэндлеры в feature-папках Application, регистрация в `ProjectBeacon.Application/DependencyInjection.cs` (`AddApplicationHandlers`).
- Сущности: `Entity.New<T>()` + параметрless-конструктор (EF Core), фабрики `Create(...)`, private-setter'ы.
- Enums в `ProjectBeacon.Domain/Enums` (status-enum'ы отделены от BCL: прецедент `TaskItemStatus`).
- Tenant: все сущности `IProjectScoped` → fail-closed фильтр `FilterProjectId` (`TenantScopedQueryFilterConvention`); null scope = 0 строк.
- UI: Blazor Server + MudBlazor, `Features/{Feature}/`, текст только через `IStringLocalizer<Web>` (сначала en-ключи в `Web.resx`), Razor не инспектирует `BeaconDbContext` — только хэндлеры.
- API: тонкие endpoint-файлы в `ProjectBeacon.API/Endpoints/` по образцу `TaskEndpoints.cs`.
- MCP: только stdio (`beacon mcp`, `ProjectBeacon.Cli/McpStdioServer.cs`), HTTP/SSE MCP запрещён (`mcp-host.md`). Имена tool'ов — snake_case **без префикса** (существуют: `read_file`, `write_file`, `apply_patch`, `get_tree`, `search_code`, `get_changed_scope`).
- Миграции: только `dotnet ef migrations add` + регенерация ModelSnapshot. Никогда руками.

### 0.3 Non-goals, которые не трогаем
- Beacon-hosted coding agent, local agent daemon (Phase 7) и worker (Phase 8) — не начаты.
- Hosted clone, outbound WSS, `write_handoff`, GitHub two-way sync — flagged off.
- `TaskItemStatus {Todo, InProgress, Done}`, `TaskSubStage` и cold-diff гейт (`ReviewNotes` обязательны для `Done` — `TaskItem.MoveInProgressToDone`/`TransitionTo`) — **не ломаем**.
- `design-doc-v2.md` не редактируем (AGENTS.md).

### 0.4 Факты по коду, на которые опирается план
- `TaskItem` (`Domain/Entities/Projects/TaskItem.cs`): поля Status/SubStage/ReviewNotes, `TransitionTo(TaskItemStatus)` бросает без ReviewNotes — cold-diff гейт.
- `BeaconDbContext`: DbSets `Tasks`, `Constraints` и т.д.; `FilterProjectId`/`FilterUnscoped`; `TenantScopedQueryFilterConvention.Apply` в `OnModelCreating`. DbSet `Sessions` уже занят `UserSession` — отсюда имя `PipelineSession`.
- `ProjectBeacon.Cli` ссылается **только** на `Application` → `Infrastructure` (Npgsql) достижима транзитивно; `EnvFile` и `PostgresConnection` живут в `Infrastructure/Data/` — доступны из CLI без новых ссылок.
- `McpStdioServer.RunAsync(root)` — статический stdio-сервер, сейчас только file-тулзы (`FileWorkspace` из `Application/Mcp`, `CodeIndex` из `Application/CodeIndex`), без DB.
- `Agents.razor` — заглушка (alert `AgentsEmpty`), ссылка в `MainLayout.razor` (/agents).
- `TaskDetail.razor` — существующая страница задачи (chips, SubStage-progress, comments).
- Тесты хэндлеров: `Application.Tests/HandlerSqlite.cs` (SQLite in-memory).

---

## 1. Ключевые design-решения

**D1. Пайплайн — отдельное nullable-поле `TaskItem.PipelineStage?`, а НЕ замена `TaskItemStatus`.**
- Новый enum `TaskPipelineStage { None, Planning, Executing, Reviewing, Approved, ReopenedForRevision, Closed }`.
- Доска, substage-workflow и cold-diff гейт работают как раньше. Маппинг на доску: Planning/Executing/Reviewing/ReopenedForRevision ↔ `InProgress`, Approved/Closed ↔ `Done`, `None` — обычный task без пайплайна.
- Закрытие пайплайна (approve / force-close) → `TaskItem.TransitionTo(Done)` + `ReviewNotes` = note последнего вердикта → cold-diff гейт проходит.
- `Status: enum {Planning, ...}` из §2.2 ТЗ реализуется именно так.

**D2. Новые сущности — в существующих проектах, все `IProjectScoped`:**
- Блок A: `LocalModelBackend`, `RoleBinding` (`Domain/Entities/Projects/`).
- Блок B: `Subtask`, `PipelineSession`, `ReviewVerdict`.
- `PipelineSession` (не `Session`): DbSet `Sessions` занят, харнес не зафиксирован.

**D3. `string[]` поля храним как JSON-текст внутри сущности** (`AllowedMcpTools`, `AllowedPaths`, `ExtraFlags`): в репо нет JSON-колонок; приватный string backing + публичное `IReadOnlyList<string>`. Текстовый JSON работает и в Postgres, и в SQLite-тестах (без Npgsql-JSONB конвертера).

**D4. Хэндлеры:** Блок A — новая feature-папка `Application/Agents/` (зеркалит страницу Agents); Блок B — расширение `Application/Tasks/` (отдельные файлы `PipelineCommands.cs` / `PipelineHandlers.cs`). Регистрация — `Application/DependencyInjection.cs`.

**D5. Спавн сессий: `ISessionSpawner` + v1 = `ManualSessionSpawner`.**
- Создаёт `PipelineSession` (Status=Ready) + собирает PromptContext по §2.3/FR-B1 (изоляция контекста: actor получает только `Instructions` + scope; review — только артефакты: `Task.Description`, финальные `Instructions`, `DiffRef`+`Summary`; сырые транскрипты не передаются).
- Спавнер заполняет `PipelineSession.LaunchSpec`: `LaunchCommand` бэкенда с подстановкой `${PORT}` + для llama.cpp — флаг `--no-reasoning-preserve` (FR-B2). В v1 spec только хранится; потребителем станет harness из Phase 7. `ExternalSessionId` зарезервирован за harness'ом.
- Реальный спавн (OpenCode/daemon) — вне скоупа.

**D6. MCP-тулзы с DB — в `ProjectBeacon.Cli`, без новых csproj.**
- `Application → Infrastructure → Npgsql` транзитивно; connection: `EnvFile.Load()` (читает `.env` из repo root) + `PostgresConnection.Resolve(config)`; scope: `TenantScope.EnterProjectScope(projectId)` (fail-closed).
- Env: `BEACON_PROJECT_ID` (обязателен для DB-тулз), опционально `BEACON_TASK_ID`, `BEACON_ACTOR_ID`.
- File-тулзы работают без DB. DB недоступен / env не задан — MCP `isError` с ясным сообщением (деградация, не падение).
- Точное имя tool'а из ТЗ (напр. `beacon.model.bind`) — логическое; реальное имя — snake_case без префикса, как у существующих.
- Набор tool'ов: `model_bind(role, modelId)`, `model_status()`, `task_create_subtask(instructions, allowedMcpTools, allowedPaths)`, `subtask_report_result(subtaskId, diffRef, summary)`, `task_review_verdict(verdict, subtaskId?, note?)`, `task_pipeline_status()`.

**D7. llama-swap супервизор — `IHostedService` в Web-хосте.**
- `Infrastructure/LlamaSwap/`: `LLamaSwapOptions` (env: `BEACON_LLAMASWAP_BIN`, `BEACON_LLAMASWAP_PORT`, `BEACON_LLAMASWAP_CONFIG`), `LlamaSwapConfigGenerator` (config.yaml из реестра), `LlamaSwapSupervisor` (процесс-жизнь, `GET /health`, `GET /metrics`, graceful stop). Интерфейс для хэндлеров — `Application/Agents/ILlamaSwapProxy` (status/reload/unload).
- Изменение реестра — поллинг `MAX(LocalModelBackend.UpdatedAt)` (внешние агенты не могут стучать в память Web; Web — единственный хост).
- NFR-A1: не переопределяем свап-логику — только config + наблюдение. NFR-A2: только HTTP. NFR-A3: нет бинарника / процесс упал → статус «unavailable», остальной Beacon работает.

**D8. Ответы на открытые вопросы ТЗ §2.6:**
- `DiffRef`: через MCP `subtask_report_result` (actor вызывает из промпта) + ручной ввод в UI (FR-B5). Git-хуки/поллинг ФС — не v1.
- История вердиктов: полная цепочка — по строке в `ReviewVerdict` на каждый вердикт (NFR-B2).
- Зависание/падение актора: v1 — ручной `FailSubtask` (кнопка в UI + хэндлер). Авто-timeout — Phase 8 (worker).

**D9. Лимит `ReopenSubtask`:** env `BEACON_MAX_REOPEN_CYCLES` (default 3). Превышен → subtask `Failed`, task остаётся в `ReopenedForRevision` с ожиданием ручного решения (NFR-B1).

**D10. `ModelBackendType { FreeToken, LlamaCpp, OpenAiCompatible }`** — в §1.4 ТЗ два значения, но текст FR-A1 прямо допускает «любой другой OpenAI-совместимый». Добавляем третье.

**D11. `PipelineRole { Planner, Actor, Review }`** — один enum на `RoleBinding` (Блок A) и сессии (Блок B): роли в обоих блоках совпадают по смыслу.

**D12. Scope подтаска (`AllowedMcpTools`/`AllowedPaths`) — в v1 декларативная метаданные, а не активная защита (явное допущение).**
- Существующие file-тулзы `McpStdioServer` не знают о Subtask/scope, а `ManualSessionSpawner` — не реальный харнес (Phase 7). Физически ограничить актора в v1-потоке некем: поля работают как (а) данные для будущего харнеса (он будет enforcing их при спавне — FR-B3), (б) информация для пользователя в UI.
- `ManualSessionSpawner` пишет scope в `LaunchSpec`/`PromptContext`, чтобы харнесу не было нужно перечитывать DB.
- Осознанное упрощение v1, не баг: enforcement приходит с Phase 7.

**D13. `ForceClosePipelineCommand` — самая привилегированная операция пайплайна: только `Admin`.**
- API: `RequireCapability(ApiTokenCapability.Admin)` (остальные pipeline-endpoint'ы — `TaskWrite`).
- Хэндлер: проверка `ActorId` по паттерну `FinishWorkHandler.ActorHasAccess`, но строже: только `User.IsAdmin` или worker-токен (`BEACON_WORKER_TOKEN`); обычный `ProjectMember` недостаточно.
- UI: кнопка force-close видна/доступна только админам; confirm-диалог явно предупреждает «закрытие без ревью, обходит cold review».

**D14. Конкурентность переходов: guarded domain-переходы + fail-loud; RowVersion в v1 не вводится.**
- В репо нет EF `RowVersion`/`ConcurrencyToken`; прецедент DB-locking'а — `ClaimTaskHandler` (`FOR UPDATE SKIP LOCKED`).
- Пайплайн-переходы идут через доменные методы `TaskItem`, которые валидируют текущий `PipelineStage` и бросают на невалидном переходе → хэндлер возвращает `Result.Failure` вместо тихого перезаписывания более свежего состояния. Двойной `EnterExecuting` — либо идемпотентен, либо ошибка; гонка `StartReview` vs `Approve` → один выигрывает, второй получает ошибку.
- Полный optimistic concurrency (RowVersion на `TaskItem`/`Subtask`) — вместе с Phase 7, когда появятся параллельные акторы-спавнеры.

---

## 2. Итоговая модель данных

| Сущность | Поля | Заметки |
|---|---|---|
| `LocalModelBackend` (A) | `Name`, `BackendType`, `LaunchCommand` (шаблон `${PORT}`), `ContextSize`, `ExtraFlags`(JSON), `Ttl` (сек), `UpdatedAt` | `IProjectScoped`, фабрика `Create` |
| `RoleBinding` (A) | `Role (PipelineRole)`, `ModelBackendId` FK | `IProjectScoped`, unique `(ProjectId, Role)`, cascade от backend |
| `TaskItem` (изм.) | `+ PipelineStage?`, `+ ICollection<Subtask> Subtasks` | методы `StartPipeline/EnterExecuting/EnterReview/SetApproved/Reopen/ClosePipeline(notes)` (D1) |
| `Subtask` (B) | `TaskId`, `Instructions`, `AllowedMcpTools`(JSON), `AllowedPaths`(JSON), `Status (SubtaskStatus)`, `DiffRef?`, `Summary?`, `ReopenCount`, `CreatedAt/UpdatedAt` | `IProjectScoped`, cascade от Task |
| `PipelineSession` (B) | `TaskId`, `SubtaskId?`, `Role`, `Status (SessionStatus: Ready/Active/Closed/Failed)`, `ExternalSessionId?`, `ModelBackendId?`, `LaunchSpec?`, `PromptContext`, `LaunchedAt?/ClosedAt?` | `IProjectScoped`, cascade от Task |
| `ReviewVerdict` (B) | `TaskId`, `SubtaskId?`, `Kind (Approve/ReopenSubtask)`, `Note`, `CreatedAt` | `IProjectScoped`, полный аудит (NFR-B2) |

Новые enum'ы: `ModelBackendType`, `PipelineRole`, `TaskPipelineStage`, `SubtaskStatus`, `SessionStatus`, `ReviewVerdictKind` — все в `Domain/Enums`.

---

## 3. Блок A — Model Orchestration

### A1. Domain — выполнено
- `Domain/Enums/ModelBackendType.cs`, `PipelineRole.cs` (D10, D11).
- `Domain/Entities/Projects/LocalModelBackend.cs`, `RoleBinding.cs` (паттерн `TaskItem`/`Constraint`: `Entity.New<T>()`, private-setter, фабрика).
- Проверка: `dotnet build`.

### A2. Infrastructure: миграция — выполнено
- `BeaconDbContext`: DbSets `LocalModelBackends`, `RoleBindings`; конфигурация (enum'ы `HasConversion<string>()`, unique-индекс `(ProjectId, Role)`, индекс `(ProjectId)` по backends, FK cascade `RoleBinding → LocalModelBackend`).
- `dotnet ef migrations add AddLocalModelRegistry` (ModelSnapshot регенерируется).
- Проверка: миграция applies на локальном Postgres, `dotnet test` не краснеет.

### A3. Application/Agents (хэндлеры) — выполнено
Файл: `Application/Agents/ModelBackendCommands.cs` (команды/запросы) + `ModelBackendHandlers.cs`.
- `UpsertLocalModelBackendCommand` (create+update одним), `DeleteLocalModelBackendCommand` (если задан `RoleBinding` → ошибка с указанием роли).
- `SetRoleBindingCommand(role, backendId)`, `RemoveRoleBindingCommand(role)`.
- `GetModelRegistryQuery` — backends + bindings одним ответом.
- `GetProxyStatusQuery` — через `ILlamaSwapProxy` (A4): healthy, loaded model, VRAM/RAM, last swap, деградация → `Result` с флагом unavailable, не исключение (NFR-A3).
- `ReloadProxyCommand`, `UnloadProxyCommand` (ручной, FR-A6).
- Интерфейс `ILlamaSwapProxy` (status/reload/unload) — здесь, реализация — A4.
- Регистрация в `DependencyInjection.cs`.
- Проверка: юнит-тесты на SQLite (паттерн `HandlerSqlite`).

### A4. Супервизор llama-swap — выполнено
**Шаг 0 (закрыт): API llama-swap проверен по исходникам/документации** — `config.yaml` (`models.<id>`: `cmd` обязателен, `${PORT}`, `ttl` = idle-unload сек), `GET /health` → `OK`, `GET /running` (массив с `model`/`state`), `GET /metrics` (Prometheus, может 503), `POST /api/models/unload`. CLI: `llama-swap -config <path> -listen <host:port>`.
- `Infrastructure/LlamaSwap/LlamaSwapOptions.cs` — env `BEACON_LLAMASWAP_BIN`, `BEACON_LLAMASWAP_PORT` (8080), `BEACON_LLAMASWAP_CONFIG` (default `%LOCALAPPDATA%/ProjectBeacon/llama-swap/config.yaml`), `BEACON_PROJECT_ID`; `IsConfigured` = бинарник + проект.
- `LlamaSwapConfigGenerator.cs` — чистый генератор config.yaml из реестра: `Ttl` → idle unload, `ContextSize` → `--ctx-size` (не дублируется, если уже в команде), `ExtraFlags` дописываются в `cmd`; ключ модели = имя, дубли суффиксом `-2`; детерминированный `\n`.
- Контракт `ILlamaSwapProxy` + `LlamaSwapStatusDto` + `UnavailableLlamaSwapProxy` — в `Infrastructure/LlamaSwap/LlamaSwapProxy.cs` (Application ссылается на Infrastructure, обратный reference не введён).
- `LlamaSwapSupervisor : BackgroundService, ILlamaSwapProxy` — старт при старте Web, стоп (kill process tree) при выходе; поллинг реестра каждые 5 c (hash реестра → перегенерация config, атомарная запись через `.tmp`); crash-loop guard 5/60 с; health-поллинг `/health`, `/running`, `/metrics`; `LastSwap` при смене набора загруженных моделей; `ReloadAsync` — перегенерация config; `UnloadAsync` — `POST /api/models/unload`.
- Регистрация в `ProjectBeacon.Web/Extensions/ServiceCollectionExtensions.cs` (переопределяет fallback из `AddApplicationHandlers`).
- Проверка (живой смоук с фейковым бинарником, .NET-заглушка `/health`+`/running`+`/metrics`+`/api/models/unload`): процесс стартует с `-config`/`-listen 127.0.0.1:<port>`; config.yaml сгенерирован корректно (ctx-size, ttl, extra-флаги, сортировка); `GET /v1/models/proxy/status` — healthy, loaded model, memory, lastSwap; `POST .../reload` перегенерирует config; `POST .../unload` → `loadedModel=null`; изменение реестра через API → авто-перегенерация config на следующем тике; килл процесса → авто-рестарт; без конфигурации — Web работает, статус `available=false`, reload/unload → 503 (NFR-A3). Юнит-тесты генератора — `Infrastructure.Tests/LlamaSwapConfigGeneratorTests.cs`.

### A5. API — выполнено
- `ProjectBeacon.API/Endpoints/ModelEndpoints.cs` (тонко, по образцу `TaskEndpoints.cs`, `RequireCapability`):
  - `GET /v1/models`, `POST /v1/models` (upsert), `DELETE /v1/models/{id}`
  - `POST /v1/models/bind`, `DELETE /v1/models/bind/{role}`
  - `GET /v1/models/proxy/status`, `POST /v1/models/proxy/reload`, `POST /v1/models/proxy/unload`
- Проверка: `dotnet test` (API.Tests), curl по живому хосту.

### A6. Web UI — страница Agents — выполнено
- Перезапись `ProjectBeacon.Web/Features/Agents/Agents.razor`:
  - MudTable реестра (name, type, command, context, ttl) + диалог add/edit + delete.
  - Блок role bindings: 3 селекта planner/actor/review → backend.
  - Карточка прокси: healthy, loaded model, VRAM/RAM, last swap, кнопки Reload/Unload (FR-A6).
- `BackendDialog.razor` — диалог add/edit (MudBlazor 9.9: без `MudDialogContent`/`MudDialogActions`, без `For=`-строк).
- Resx: en-ключи первыми (`ModelsAdd`, `ModelsEdit`, `RoleBindingPlanner`, `ProxyStatus`, …).
- Проверка: `dotnet build` (Web, 0 ошибок), `dotnet test` 306/306 (Domain, Application, Infrastructure, API, Web; Cli.Tests исключён — файл-лок beacon MCP-процесса), HTTP-прогон по живому хосту :5083 (bootstrap → login JWT → org/project → полный CRUD `/v1/models` + bind + proxy status — 13/13 PASS). Браузерного CRUD в UI нет (нет browser-инструмента) — UI-часть прогнана на уровне сборки + API.

### A7. CLI MCP — model-тулзы — выполнено
- `McpStdioServer.cs`: добавляем `model_bind`, `model_status` + DB-bootstrap (EnvFile → PostgresConnection → scoped `BeaconDbContext` → `TenantScope.EnterProjectScope`).
- Без `BEACON_PROJECT_ID`/DB → `isError` (D6).
- Проверка: `dotnet run --project ProjectBeacon.Cli -- mcp --root .` — `tools/list` содержит новые тулзы; `tools/call` по live Postgres.
- Сделано: live-прогон stdio MCP: `model_status` — реальный реестр (пустые backends/bindings, proxy «unavailable» без супервизора); write-path `model_bind` с несуществующим backend → `isError` «Model backend not found.», без мутации; без `BEACON_PROJECT_ID`/DB → `isError` (процесс не падает). `dotnet build` 0/0, `dotnet test` 333/333.

### A8. Тесты Блока A
- `Application.Tests`: CRUD бэкендов, валидация биндингов, реестр (SQLite).
- `Infrastructure.Tests`: генерация config.yaml (юнит), супервизор против fake HTTP listener (health/metrics/reload/деградация).
- Acceptance: реестр CRUD-ится из UI, config.yaml регенерируется при изменении, процесс управляется, статус виден, при отсутствии бинарника — деградация без падения Web.

---

## 4. Блок B — Task-пайплайн

### B1. Domain — выполнено
- Enums: `TaskPipelineStage`, `SubtaskStatus { Pending, InProgress, Done, Failed }`, `SessionStatus`, `ReviewVerdictKind { Approve, ReopenSubtask }`.
- Сущности: `Subtask.cs`, `PipelineSession.cs`, `ReviewVerdict.cs` (D2, D3).
- `TaskItem`: `+ PipelineStage?` + методы переходов (D1): `StartPipeline()` (→Planning), `EnterExecuting()`, `EnterReview()`, `SetApproved()`, `ReopenForRevision()`, `ClosePipeline(string reviewNotes)` (→Closed, `TransitionTo(Done)` + ReviewNotes).
- Проверка: `dotnet build`; домен-тесты переходов (включая: `None` не мешает обычному workflow, cold-diff гейт не сломан).

### B2. Infrastructure: миграция — выполнено
- DbSets `Subtasks`, `PipelineSessions`, `ReviewVerdicts`; конфигурация (enum'ы → string, индексы `(TaskId)`, `(TaskId, CreatedAt)` по verdict'ам, cascade от `TaskItem`).
- `dotnet ef migrations add AddTaskPipeline`.
- Проверка: apply на локальной БД, `dotnet test` зелёный.

### B3. Application/Tasks — пайплайн-хэндлеры — выполнено
Файлы: `PipelineCommands.cs`, `PipelineHandlers.cs`, `ISessionSpawner.cs` (+`ManualSessionSpawner`), `SessionPrompts.cs` (сборка PromptContext'ов, FR-B1).
- `StartPipelineCommand` — task → Planning; `PlannerSession` (Ready, модель из `RoleBinding.planner`, PromptContext: задача + проект; обзор кода — через file-тулзы агента, §2.3.1).
- `CreateSubtaskCommand` (planner, через MCP) — валидация stage=Planning; subtask Pending; task → Executing (первый subtask).
- `StartActorSessionCommand(subtaskId)` — новая `PipelineSession` (actor, Ready, модель из `RoleBinding.actor`, PromptContext = только `Instructions` + scope; `LaunchSpec` по D5/FR-B2).
- `LaunchPipelineSessionCommand(sessionId)` — Ready → Active (v1: ручной; Phase 7: harness).
- `ReportSubtaskResultCommand` (actor, через MCP; D8) — `DiffRef`+`Summary`, subtask → Done, сессия → Closed.
- `FailSubtaskCommand` (вручную, FR-B5/D8) — subtask → Failed, note в `Summary`.
- `StartReviewCommand` — guard: все subtasks в Done/Failed; task → Reviewing; новая `ReviewSession` (PromptContext по §2.3.3: Description + финальные Instructions + все DiffRef/Summary). **Каждый subtask в review-контексте явно помечен статусом `Done`/`Failed`**: Failed-подтаск без `DiffRef` — это «работа не сделана», а не «без изменений»; `Summary` из `FailSubtaskCommand` помечается как причина фейла. Ревьюер не должен гадать, что часть работы не выполнена.
- `RecordReviewVerdictCommand` (review, через MCP):
  - `Approve` → task → Approved;
  - `ReopenSubtask(subtaskId, note)` → subtask Pending, `Instructions += note`, `ReopenCount++`; при превышении `BEACON_MAX_REOPEN_CYCLES` (D9) → subtask Failed; task → ReopenedForRevision.
  - Каждый вердикт → строка `ReviewVerdict` (NFR-B2).
- `ApprovePipelineCommand` — пользовательское подтверждение (Approved → Closed + `TaskItem` Done/ReviewNotes из note вердикта).
- `ForceClosePipelineCommand` — FR-B5, явное предупреждение в UI; закрывает без ревью, ReviewNotes помечает «force-closed без ревью».
- `GetPipelineQuery` — task + subtasks + sessions + verdicts (UI и MCP `task_pipeline_status`).
- Регистрация в `DependencyInjection.cs`; `ManualSessionSpawner` — scoped/transient.
- Проверка: тесты на SQLite — happy path, reopen+лимит, force close, изоляция контекста (в PromptContext review нет транскрипта planner'а), fail-closed tenancy.

### B4. API — выполнено
- `ProjectBeacon.API/Endpoints/PipelineEndpoints.cs` (тонкие, `RequireCapability`):
  - `GET /v1/tasks/{id}/pipeline`
  - `POST /v1/tasks/{id}/pipeline/start`
  - `POST /v1/tasks/{id}/subtasks`
  - `POST /v1/tasks/{id}/subtasks/{subtaskId}/session` (spawn actor)
  - `POST /v1/sessions/{id}/launch`
  - `POST /v1/tasks/{id}/subtasks/{subtaskId}/result`
  - `POST /v1/tasks/{id}/subtasks/{subtaskId}/fail`
  - `POST /v1/tasks/{id}/pipeline/review/start`
  - `POST /v1/tasks/{id}/pipeline/verdict`
  - `POST /v1/tasks/{id}/pipeline/approve`
  - `POST /v1/tasks/{id}/pipeline/force-close` — `RequireCapability(Admin)` + проверка `IsAdmin`/worker в хэндлере (D13); остальные pipeline-endpoint'ы — `TaskWrite`
- Проверка: curl-прогон всего флоу по живому хосту.

### B5. CLI MCP — pipeline-тулзы — выполнено
- `McpStdioServer.cs`: `task_create_subtask`, `subtask_report_result`, `task_review_verdict`, `task_pipeline_status` (D6, env `BEACON_PROJECT_ID`/`BEACON_TASK_ID`/`BEACON_ACTOR_ID`).
- Обновить `dotnet project docs/mcp-host.md`: список tool'ов + env (HTTP MCP по-прежнему запрещён).
- Проверка: stdio-прогон `tools/list` + `tools/call` против live Postgres; без DB — `isError`.
- Сделано: live-прогон stdio MCP: `tools/list` = 12 тулз (6 file + 6 новых); `task_pipeline_status` — реальные данные live Postgres; `task_create_subtask` для задачи вне пайплайна → `isError` «Pipeline is not started.», без мутации; file-тулзы не пострадали (`read_file` работает); env `BEACON_TASK_ID`/DB отсутствуют → `isError`. `dotnet build` 0/0, `dotnet test` 333/333.

### B6. Web UI — пайплайн в TaskDetail — выполнено
- `ProjectBeacon.Web/Features/Tasks/TaskDetail.razor`: секция Pipeline:
  - Степпер стадий (Planning → Executing [N subtasks] → Reviewing → Approved → Closed), текущая стадия подсвечена.
  - Таблица subtasks: статус, instructions, diff/summary (expandable), reopen count.
  - Действия по стадиям (FR-B5): start pipeline; create subtask вручную; start actor session; launch session; report result вручную; start review; approve; **force close — виден только админам (`User.IsAdmin`/worker), confirm-диалог «закрытие без ревью» (D13)**; reopen subtask вручную.
  - История вердиктов (список `ReviewVerdict`).
- Resx: en-ключи первыми.
- Проверка: браузерный прогон полного флоу end-to-end (AGENTS.md: один скриншот — не верификация).
- Сделано: `dotnet build` 0/0; `dotnet test` зелёный (Web 44, Application 116, API 32, Infrastructure 17, CLI 7). Браузерного инструмента нет — по AGENTS.md применён ближайший суррогат: live-прогон полного флоу через curl на работающем хосте (admin cookie) против тех же Application-хэндлеров, которые инжектит UI: `GET /v1/tasks/{id}/pipeline` (stage пуст) → `pipeline/start` (Planning, авто Planner-session Ready) → `subtasks` (Pending) → `subtasks/{id}/session` (Actor Ready, prompt только о subtask) → `sessions/{id}/launch` (Active) → `subtasks/{id}/result` (subtask Done, task → InProgress/Executing, actor-сессия автозакрыта) → `pipeline/review/start` (Reviewing + Review-session, prompt только артефакты) → `pipeline/verdict` Approve (Approved, task → Done) → `pipeline/force-close` (Closed, ReviewNotes «force-closed without review», все сессии закрыты). UI: 7 режимов диалога (create subtask, report result, fail subtask, approve, reopen, confirm-close, force-close c confirm) + степпер + таблицы subtasks/sessions/verdicts; текст — только resx; Razor без DbContext. Не верифицировано браузером: визуальный рендер степпера и диалогов.

### B7. Тесты Блока B
- `Application.Tests`: полный happy path (start → 2 subtasks → results → review → approve → task `Done` + ReviewNotes), reopen-цикл и лимит 3, force close, force close **отклонён для обычного `ProjectMember` (D13)**, **двойной переход (`EnterExecuting` дважды / `StartReview` vs `Approve`) → второй получает ошибку, а не перезапись (D14)**, изоляция контекста (Failed-subtasks помечены статусом в review-контексте), tenancy (null scope → пусто), валидация переходов (verdict без review-сессии и т.п.).
- `Cli.Tests`: `tools/list` содержит 6 новых тулз; DB-тулзы без env → `isError`.
- Проверка: `dotnet test` полностью зелёный.

### B8. Документация и roadmap
- `dotnet project docs/ProjectBeacon-dotnet-roadmap-v2.md` — строки на M1–M6 с acceptance (AGENTS.md: изменение не «done», пока acceptance строки не выполнена).
- `dotnet project docs/mcp-host.md` — новые tool'ы и env (см. B5).
- `design-doc-v2.md` — не трогаем.

---

## 5. Вехи и acceptance

| Веха | Состав | Acceptance |
|---|---|---|
| **M1. Fundamentals** | A1, A2, B1, B2 | `dotnet build` зелёный; обе миграции применяются; домен-тесты переходов пайплайна зелёные |
| **M2. Block A: app+API+UI** | A3, A5, A6 | В UI: создание/редактирование/удаление бэкенда, биндинг ролей, реестр переживает перезагрузку; API отвечает |
| **M3. llama-swap supervisor** | A4 | Фейковый бинарник: процесс стартует, `/health` ОК, статус в UI, reload при изменении реестра; без бинарника — Web работает, статус «unavailable» (NFR-A3) |
| **M4. Block B: app+API** | B3, B4 | curl-флоу: start → 2 subtasks → actor sessions → results → review → approve → task `Done` + ReviewNotes из вердикта; reopen ≤ `BEACON_MAX_REOPEN_CYCLES` |
| **M5. MCP tools** | A7, B5 | `beacon mcp`: `tools/list` = 6 старых + 6 новых; вызовы с `BEACON_PROJECT_ID` против live Postgres; без DB — `isError`, file-тулзы не пострадали |
| **M6. UI + тесты + доки** | B6, B7, B8, A8 | `dotnet test` зелёный; браузерный прогон полного флоу пайплайна; строки roadmap с acceptance; mcp-host.md актуален |

Порядок: M1 → M2 → M3 → M4 → M5 → M6. Блоки A и B независимы, но B использует `RoleBinding` из A — A идёт первым.

---

## 6. Out of scope (не делаем, не выдумываем)

- Реальный harness/daemon (Phase 7), auto-timeout и worker (Phase 8) — `ManualSessionSpawner` + ручной Fail.
- Параллельное выполнение subtasks (последовательно, §4 ТЗ).
- UI для прямого редактирования `config.yaml` llama-swap (только через реестр).
- Автоопределение MoE/dense (тип задаётся вручную).
- Замена `TaskItemStatus`/доски/cold-diff гейта — только дополнение.
- Non-goals AGENTS.md (hosted clone, WSS, `write_handoff`, hosted agent и т.д.) — не трогаются.

## 7. Риски и открытые пункты

- ~~**Схема config.yaml и reload endpoint'ы llama-swap**~~ — закрыто в A4 (шаг 0: контракт подтверждён по исходникам; смоук с фейковым бинарником).
- **JSON-текст для `string[]`** — проверить на SQLite-тестах (B7/A8): без Npgsql-JSONB-конвертера текст работает на обоих провайдерах.
- ~~**Поллинт реестра**~~ — интервал зафиксирован в A4: 5 c (hash реестра → перегенерация config).
- Порт Web-хоста для внешнего MCP-клиента не нужен: CLI ходит в Postgres напрямую (D6).
