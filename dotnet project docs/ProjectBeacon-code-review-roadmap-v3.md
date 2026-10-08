# ProjectBeacon — Code Review Roadmap v3

**Дата:** 2026-09-29  
**База ревью:** commit `1cce6f4`  
**Цель документа:** превратить результаты архитектурного и кодового ревью в единый исполняемый roadmap и одновременно зафиксировать актуальное состояние документации.

---

## 0. Итог ревью

ProjectBeacon уже имеет рабочее архитектурное ядро: tenant isolation, PostgreSQL RLS, workstation daemon, sandboxed file access, task/pipeline state machines, собственный model backend/swapper, context compiler и локальный MCP.

Основные риски сейчас находятся не в отсутствии отдельных функций, а в разрыве между существующими механизмами и едиными контрактами:

1. **Security / authorization** — есть несколько частично независимых механизмов идентификации и проверки доступа.
2. **Workstation boundary** — file tools уже ограничены root, но Chat/Eval/OpenCode способны обходить тот же контракт через `path`.
3. **API token actor model** — токен знает project scope, но не представлен как полноценный actor.
4. **Application architecture** — Application напрямую зависит от Infrastructure.
5. **CI** — тестовый контур сейчас красный и содержит тесты, завязанные на реальный внешний процесс.
6. **Desired vs applied state** — запись желаемой конфигурации и выполнение команды не образуют надёжного reconciliation-механизма.
7. **Evaluation / review** — часть критериев успеха всё ещё сводится к самоотчёту агента.
8. **Context compiler** — серверная часть имеет placeholders, хотя нужные локальные данные уже умеет получать клиент.
9. **Documentation** — существенная часть roadmap/design документации описывает старое состояние проекта.

Главная задача roadmap — сначала закрыть trust boundary и authorization, затем стабилизировать CI и архитектуру, и только после этого углублять agent runtime / evaluation / context.

---

# 1. P0 — немедленно исправить security-critical проблемы

## 1.1 Bootstrap должен fail closed

**Файл:** `ProjectBeacon.Application/Auth/AuthHandlers.cs`

Проблема: `BootstrapHandler` загружает `BOOTSTRAP_ADMIN_TOKEN`, но проверяет его только если значение непустое. При отсутствии переменной bootstrap становится анонимным.

### Изменение

- В production отсутствие `BOOTSTRAP_ADMIN_TOKEN` должно блокировать bootstrap.
- Разрешить dev/test fallback только через явный environment mode.
- Bootstrap должен быть одноразовым и безопасным при повторном вызове.
- Добавить integration tests:
  - token отсутствует -> отказ;
  - token неверен -> отказ;
  - token верен -> bootstrap успешен;
  - повторный bootstrap после создания admin -> отказ/идемпотентный безопасный ответ.

**Готово, когда:** невозможно создать первого администратора без явно настроенного секрета.

---

## 1.2 Logout должен быть авторизованным

**Файл:** `ProjectBeacon.API/Endpoints/AuthEndpoints.cs`

Проблема: `POST /v1/auth/logout` разрешён anonymous и принимает `UserId`. Это позволяет потенциально удалить активные сессии другого пользователя.

### Изменение

- Убрать `.AllowAnonymous()`.
- Не принимать actor/user identity от клиента.
- UserId получать из authenticated principal / `ActorContext`.
- По умолчанию завершать текущую сессию.
- Отдельно реализовать явный `logout-all`, если такая функция нужна.

**Готово, когда:** пользователь не может завершить чужие сессии простым подставлением чужого ID.

---

## 1.3 API token должен иметь полноценную identity

**Файл:** `ProjectBeacon.API/Auth/ApiTokenAuthMiddleware.cs`

Сейчас middleware добавляет `token_id`, `project_id`, `capabilities`, но не `ClaimTypes.NameIdentifier`.

### Изменение

Ввести единый server-side `ActorContext`, например:

```text
ActorContext
  Type: Human | ApiToken | Device | Worker
  UserId?
  TokenId?
  DeviceId?
  ProjectId?
  OrganizationId?
  Capabilities
```

Middleware должен создавать его независимо от типа authentication.

**Готово, когда:** application handlers больше не вынуждены угадывать identity по разным наборам claims.

---

## 1.4 Нельзя доверять project scope из route/header при API token auth

### Проблема

`TenantIsolationMiddleware` выбирает project из route/header/claim. Для API token membership validation фактически обходится, когда нет `NameIdentifier`.

В результате token, созданный для project A, потенциально может использовать route/header для обращения к project B.

### Изменение

Для API token:

- effective project = project, записанный в token;
- route/header project должен совпадать с token project;
- mismatch -> `403 Forbidden`;
- нельзя расширять scope через client-supplied header.

### Тесты

Обязательно добавить negative integration tests:

- token A -> project A: `200`;
- token A -> project B: `403`;
- token A + forged project header B -> `403`;
- token A без project route там, где он обязателен -> отказ.

---

## 1.5 Не доверять `CreatedByUserId`, `ActorId`, `UserId` в request body

Если значение является identity текущего caller'а, оно должно вычисляться сервером.

Разделить:

- **Actor** — только server-side;
- **Target resource** — client supplied;
- **Delegated identity** — отдельный явно авторизованный use case.

В первую очередь проверить:

- create API token;
- create/remove project member;
- session operations;
- task/pipeline actor operations;
- bootstrap/invite flows.

---

# 2. P0/P1 — единая authorization model

## 2.1 Ввести Actor → Capability/Role → Resource → Action

Сейчас одновременно существуют `MemberRole` и `ApiTokenCapability`, но проверки распределены по handlers и endpoint'ам.

Целевая модель:

```text
Actor
  -> Role / Capability
      -> Resource
          -> Action
```

Пример:

```text
ApiToken
  project = A
  capabilities = TaskRead

can(Task.Read, task in project A) = true
can(Task.Write, task in project A) = false
can(Task.Read, task in project B) = false
```

### Изменение

Создать единый authorization subsystem:

- `ActorContext`;
- resource authorization policies;
- capability checks;
- role mapping;
- project scoping;
- fail-closed default.

Endpoint может объявлять coarse requirement, handler/resource policy — окончательное решение.

---

## 2.2 Закрыть project member operations

**Файл:** `ProjectMemberHandlers.cs`

Операции add/remove member сейчас принимают project/user/role, но actor authorization недостаточно выражен.

Нужно определить минимум:

- кто может добавлять пользователя;
- кто может удалять пользователя;
- кто может выдавать Owner/Admin;
- может ли пользователь удалить сам себя;
- можно ли убрать последнего Owner;
- кто может изменять project settings.

Все правила должны быть в одном authorization model, а не в отдельных ad-hoc `if` в handlers.

---

## 2.3 API token CRUD тоже должен проверять caller

Создание, отзыв, просмотр и изменение token должны быть привязаны к actor и project scope.

Особенно важно исключить сценарий:

```text
caller -> project route -> token id from another project
```

---

# 3. P1 — workstation security boundary

## 3.1 Один sandbox contract для всех локальных операций

**Ключевой файл:** `ProjectBeacon.Cli/Client/WorkstationDaemon.cs`

File operations уже используют `WorkspacePath`, но `ChatEnsureAsync` / `ChatPromptAsync` могут принимать путь напрямую и передавать его в OpenCode.

Это создаёт второй, менее защищённый путь к filesystem/process capabilities.

### Целевой контракт

Control plane не должен говорить:

```text
path = C:\\some\\arbitrary\\directory
```

Он должен говорить:

```text
projectId = ...
relativePath = src/foo.cs
```

Клиент:

1. resolve projectId -> trusted `ProjectRuntime.LocalRoot`;
2. resolve relative path;
3. validate through `WorkspacePath`;
4. only then call OpenCode / process / file tool.

---

## 3.2 Один root boundary для Chat / Eval / OpenCode / File tools

Проверить и привести к одному механизму:

- file read/write;
- apply patch;
- tree/search;
- chat session creation;
- chat prompt;
- evaluation execution;
- OpenCode process cwd;
- future agent runtime adapters.

Нельзя иметь отдельную "trusted" ветку только потому, что операция называется Chat/Eval.

---

## 3.3 Project-rooted commands должны ссылаться на runtime binding

Для project-bound операций ввести концепцию:

```text
ProjectRuntime
  ProjectId
  DeviceId
  LocalRoot
  RuntimeState
```

Операция сначала разрешается через ProjectRuntime, затем через relative path.

Global workstation commands могут использовать `ProjectsRoot`.

---

# 4. P1 — CI стабилизация

## 4.1 Сначала вернуть зелёный CI

Текущий CI run `36577015869` содержит 7 failures.

Порядок исправления:

1. Model backend fixtures и validation.
2. API token tests.
3. LlamaServer real-process test.
4. RLS cleanup/role leakage.
5. Повторный полный test run.

---

## 4.2 Исправить API token tests под реальный security contract

Правильный lifecycle:

```text
Create
  -> raw token returned once
  -> hash + prefix persisted

Get/List
  -> prefix only
```

Production contract не должен предполагать, что prefix является raw token.

`ProjectEndpoints.MapTokenResponse` сейчас убирает `tokenValue`, поэтому контракт create response необходимо выровнять: secret должен возвращаться ровно один раз.

---

## 4.3 RLS tests должны быть полностью повторяемыми

Проблема: test cleanup оставляет database roles/privileges.

Использовать:

- `REVOKE` перед drop;
- `DROP ROLE IF EXISTS`;
- уникальные роли на test fixture;
- deterministic teardown;
- независимость тестов от порядка выполнения.

**Acceptance:** два полных последовательных прогона тестов дают одинаковый результат.

---

## 4.4 Реальный llama-server вынести из обычного CI

Обычный CI:

- fake process;
- fake health endpoint;
- deterministic backend behavior.

Отдельный self-hosted/GPU integration job:

- настоящий `llama-server`;
- VRAM behavior;
- process restart;
- real health check.

---

# 5. P1 — desired state / applied state

Текущая схема может сделать:

```text
DB desired state updated
        ↓
command enqueued
        ↓
command lost / rejected / device offline
```

В результате control plane думает, что состояние применено, хотя клиент его не применил.

## Целевая модель

```text
DesiredRevision = 17
AppliedRevision = 16
```

Workstation heartbeat/reconcile видит mismatch и повторно применяет desired state.

Применить как минимум к:

- OpenCode config;
- model runtime/config;
- workstation settings;
- будущим agent runtime settings.

Команда становится механизмом доставки/ускорения, а не единственным источником истины.

---

# 6. P1 — review proof должен быть объективным

`FinishWorkCommand` требует `Review` с `ReviewerRun=true`, но это всё ещё частично self-reported.

## Целевая модель

Создать сущность уровня:

```text
ReviewRun
  Id
  TargetRunId
  ReviewerType
  Status
  Findings
  ArtifactRef
  StartedAt
  CompletedAt
```

`finish_work(done)` должен ссылаться на зарегистрированный ReviewRun со статусом, подтверждающим завершение проверки.

Нельзя считать `ReviewerRun=true` достаточным доказательством сам по себе.

---

# 7. P1 — evaluation semantics

Сейчас `RunEvalTurnAsync` по сути проверяет, что модель выдала output и достигла idle.

Это completion, но не task success.

## Целевой pipeline

```text
Eval definition
   ↓
Agent execution
   ↓
Produced artifact
   ↓
Build / tests / checks
   ↓
Optional reviewer
   ↓
Pass / Fail
```

## Eval with brief / without brief

Эксперимент должен контролировать всё кроме brief:

- model;
- model params;
- reasoning effort;
- temperature;
- tool permissions;
- repository state;
- task prompt;
- timeout.

Иначе сравнение измеряет несколько факторов одновременно.

---

# 8. P1 — context compiler

`CompileBriefHandler` уже содержит полезные секции и token budget, но `tree_capsule` / `changed_scope` пока частично являются placeholders.

При этом client-side `CodeIndex` уже умеет:

- `get_tree`;
- `search_code`;
- `get_changed_scope`.

## Следующий шаг

Клиент после получения brief должен локально обогащать placeholders реальными результатами `CodeIndex`.

Важно:

- соблюдать token budget;
- сохранить never-drop sections;
- пересчитать итоговый token estimate после client enrichment;
- не пересылать весь repository без необходимости.

Результат должен быть единым `CompiledBrief`, а не серверной заготовкой + неявным клиентским текстом.

---

# 9. P1/P2 — application architecture

Сейчас `ProjectBeacon.Application.csproj` напрямую ссылается на `ProjectBeacon.Infrastructure`.

Кроме того, Application handlers используют конкретные infrastructure types, например password/JWT/mail abstractions и `BeaconDbContext`.

## Целевое направление

Концептуально:

```text
Hosts
  ↓
Infrastructure
  ↓
Application
  ↓
Domain
```

То есть infrastructure реализует application abstractions, а не наоборот.

### Вынести в Application

Например:

- `IPasswordHasher`;
- `ITokenIssuer`;
- `ISecretProtector`;
- `IEmailSender`;
- model execution abstraction;
- agent runtime abstraction.

Infrastructure предоставляет реализации.

### EF Core

Полностью убрать EF из Application сразу необязательно: это более крупный refactor. Но concrete Infrastructure project reference из Application следует удалить как архитектурный приоритет.

---

# 10. P1/P2 — Agent Runtime abstraction

Сейчас OpenCode заметно протекает в application/client architecture.

Ввести:

```text
IAgentRuntime
  CreateSession
  SendPrompt
  StreamParts
  Abort
  ReadUsage
  Dispose
```

Первая реализация:

```text
OpenCodeAgentRuntime
```

Потенциальные будущие реализации:

```text
CodexAgentRuntime
GrokAgentRuntime
LocalAgentRuntime
```

Pipeline/Task domain не должны знать OpenCode-specific protocol или request shape.

---

# 11. P2 — pipeline refactor

`PipelineHandlers.cs` слишком велик и смешивает несколько типов поведения.

Разделить как минимум:

```text
PipelineLifecycleHandlers
PipelineActorHandlers
PipelineReviewHandlers
PipelineQueryHandlers
```

Разделение проводить по invariant/behavior, а не произвольному количеству файлов.

---

# 12. P2 — subtask tool/path restrictions должны стать реальными

Сейчас `AllowedMcpTools` / `AllowedPaths` в значительной степени являются metadata/будущим контрактом.

Необходимо реально enforce-ить:

- MCP tool allowlist;
- filesystem path allowlist;
- project scope;
- task scope;
- model/runtime restrictions.

Это особенно важно, когда появится несколько параллельных agent executions.

---

# 13. P2 — VRAM telemetry

`LlamaServerBackend` использует `Process.WorkingSet64` как `VramFootprintMb`.

Это RAM working set, а не VRAM.

Разделить показатели:

```text
WorkingSetMb
ActualVramMb
EstimatedVramMb
```

Scheduler должен принимать решение по фактическому или явно объявленному GPU metric, а не по RAM working set.

---

# 14. P2 — process lifecycle audit

Проверить:

- `WorkstationDaemon`;
- `ClientLlamaSwap`;
- `LlamaServerBackend`;
- `ClientOpenCodeServe`.

Для каждого процесса проверить:

- cancellation;
- timeout;
- graceful shutdown;
- abnormal exit;
- stdout/stderr draining;
- disposal;
- fire-and-forget tasks;
- retry/backoff;
- sync-over-async;
- orphan process prevention.

Особое внимание — async operations, которые могут пережить request/command lifetime.

---

# 15. P2 — standard API error contract

У API должен быть единый `ProblemDetails` contract.

Требования:

- predictable status codes;
- stable error shape;
- correlation/trace id;
- production не отдаёт stack trace клиенту;
- полный exception остаётся в logs;
- client-side handlers могут программно различать validation/auth/conflict/internal errors.

---

# 16. P2 — package/version governance

Сейчас есть version drift между проектами:

- EF Core versions;
- Npgsql EF;
- JwtBearer;
- EF Design;
- .NET SDK 10.0.401 при `net9.0` target.

Ввести central package management или единый version source и зафиксировать policy:

- framework target;
- SDK policy;
- package update cadence;
- security updates;
- allowed exceptions.

---

# 17. P2 — MCP trust model

Локальный `beacon mcp` может в некоторых конфигурациях использовать DB directly, если API URL/token отсутствуют.

Это допустимо только как сознательная trust boundary:

```text
Local MCP process = trusted local user process
```

Нужно явно документировать различие между:

- local DB-backed MCP;
- API-backed control plane access;
- workstation permissions;
- project scope;
- actor identity.

Нельзя создавать впечатление, что local MCP имеет ту же security model, что remote API.

---

# 18. P2 — Worker

В repository уже есть `ProjectBeacon.Worker`, включая cleanup service.

Старые документы всё ещё утверждают, что Worker "ещё не начат".

Roadmap должен зафиксировать:

- что Worker делает сейчас;
- какие background jobs планируются;
- какие операции принадлежат Worker, а какие workstation client;
- как Worker аутентифицируется;
- какие project scopes он может обслуживать.

---

# 19. P2 — production verification matrix

После завершения core refactors нужна отдельная matrix для staging/self-host verification:

| Area | Verification |
|---|---|
| Bootstrap | token required / replay protected |
| Auth | login/logout/session revocation |
| Invite | invite -> password setup |
| API token | create once / scope / revoke |
| Tenant isolation | cross-project negative tests |
| RLS | isolation + repeatable cleanup |
| Device | enrollment / auth / heartbeat |
| Command queue | enqueue / delivery / retry |
| Workstation | root sandbox / project binding |
| OpenCode | start / prompt / stop / crash recovery |
| Model runtime | health / swap / restart |
| Context | tree / changed scope / budget |
| MCP | local and API-backed semantics |
| Migrations | fresh DB + upgrade DB |
| Proxy | forwarded headers / HTTPS behavior |
| Cookies | secure / same-site / expiry |
| Docker | production startup |
| Kubernetes | if supported later |

---

# 20. Документация — что устарело и что делать

## 20.1 ARCHIVE / DELETE

### `dotnet project docs/ProjectBeacon-analysis.md`

**Статус:** ✅ архивировано (2026-09-30).

Причины:

- прямо говорит, что tests/build не запускались;
- содержит уже исправленные или устаревшие утверждения;
- описывает ClientStore как plaintext;
- считает отсутствующим workstation sandbox;
- содержит старую оценку ClaimTask SQL.

Сохранить только как historical audit, если нужен журнал эволюции.

---

### `archive/docs/ProjectBeacon-fix-plan.md`

**Статус:** ✅ архивировано (2026-09-30).

Это старый execution backlog. Большая часть задач уже выполнена либо изменилась.

---

### `dotnet project docs/ProjectBeacon-forward-plan.md`

**Статус:** ✅ архивировано (2026-09-30).

Особенно полезные пункты, которые уже поглощены текущим roadmap:

- eval;
- CodeIndex -> brief;
- tool discipline;
- review proof;
- Worker;
- RLS;
- design-doc v3.

---

### `dotnet project docs/ProjectBeacon-own-swapper-plan.md`

**Статус:** ✅ архивировано (2026-09-30).

Большая часть запланированного уже присутствует в коде:

- `IModelBackend`;
- `LlamaServerBackend`;
- FSM;
- crash recovery;
- VRAM coordination/checking;
- logging/observability.

Финальные архитектурные решения перенести в актуальную architecture doc.

---

### `dotnet project docs/ProjectBeacon-dotnet-roadmap-v2.md`

**Статус:** ✅ заменено и архивировано (2026-09-30).

Не продолжать использовать v2 как активный execution plan.

---

## 20.2 UPDATE

### `features.md`

**Статус:** ✅ сокращено до индекса (2026-10-01).

Процесс модели описан как клиентский (`UseOwnSwapper` или внешний llama-swap). Типы бэкендов: `FreeToken`, `LlamaCpp`, `OpenAiCompatible`. Детали — в `ProjectBeacon-design-doc-v3.md`.

---

### `task-pipeline-local-agents.md`

**Статус:** ✅ контракт обновлён (2026-10-01). Журнал шагов — `archive/docs/task-pipeline-local-agents-log.md`.

Зафиксировано по коду: `ManualSessionSpawner` остаётся pipeline spawn; `IAgentRuntime` — клиентский chat harness; Worker пайплайн не исполняет; `AllowedMcpTools` / `AllowedPaths` попадают в prompt и не являются harness deny. Enforcement — открытый gap, не описание текущего поведения.

---

### `agent-prompt-template.md`

**Статус:** ✅ обновлено (2026-09-30).

Шаблон переписан: теперь агент получает инструкции из master roadmap v1 и specialized roadmaps.

---

### `README.md`

**Статус:** ✅ обновлено (2026-09-30).

Обновлены ссылки: design-doc-v2 + roadmap-v2 → master roadmap v1 + code-review-roadmap v3 + ui-ux-roadmap v1.1.

---

### `AGENTS.md`

**Статус:** ✅ обновлено (2026-09-30).

Обновлено: Worker status (ProjectBeacon.Worker существует), references to roadmap v2 → master roadmap v1, design-doc-v2 → current architecture.

---

### `mcp-host.md`

**Статус:** ✅ обновлено (2026-10-01).

Добавлены `ActorContext` и capabilities, разделение local database vs control plane, граница workstation/chat, и то, что `IAgentRuntime` не является MCP.

---

### `cold-diff-review.md`

**Статус:** ✅ обновлено (2026-10-01).

Skill явно отделён от pipeline review session. Один не заменяет другой.

---

## 20.3 REVIEW, а не автоматически obsolete

### `ProjectBeacon-ui-spec-corrected-geometry.md`

**Статус:** ✅ архивировано (2026-10-01). Значения, которые совпали с `DesignTokens.cs`, живут в UI spec. Отдельной geometry-таблицы нет.

### `UI Design Migration Specification.md`

**Статус:** ✅ сверено с `MainLayout`, `BeaconTheme` и `DesignTokens.cs` (2026-10-01). Это единственный UI spec. Ширина drawer — `DrawerWidthLeft` `168px`.

---

# 21. Authoritative документы

**Статус:** ✅ (2026-10-01), с зафиксированным конфликтом.

`ProjectBeacon-design-doc-v3.md` — архитектура.

Отдельный `ProjectBeacon-roadmap-v3.md` не создаётся. Master roadmap §20 и cleanup-критерий запрещают второй активный execution plan. Точка входа — `ProjectBeacon-master-roadmap-v1.md`. Backend-детали — этот файл. UI — `ProjectBeacon-ui-ux-review-roadmap-v1.1.md`.

Мелкие `architecture.md` / `security.md` / `workstation.md` / `agent-runtime.md` / `context.md` / `pipeline.md` / `evaluation.md` / `deployment.md` / `mcp.md` не заводились. Их содержание покрывают design-doc-v3, `mcp-host.md`, `task-pipeline-local-agents.md`, `deploy/README.md`. Новый файл — только если домен реально живёт отдельно.

---

# 22. Предлагаемый порядок реализации

## Sprint 1 — Security emergency

- Bootstrap fail-closed.
- Authenticated logout.
- ActorContext.
- API-token project scope.
- Удаление client-supplied actor identity.
- Negative auth tests.

**Результат:** закрыты наиболее опасные trust-boundary ошибки.

## Sprint 2 — Authorization

- Unified role/capability model.
- Project member authorization.
- Token CRUD authorization.
- Resource/action policies.

**Результат:** authorization становится централизованным subsystem, а не набором локальных проверок.

## Sprint 3 — Workstation boundary

- ProjectRuntime.
- unified root validation.
- Chat/Eval/OpenCode sandboxing.
- project-relative paths.
- command scope enforcement.

**Результат:** любой локальный agent operation проходит через один trust boundary.

## Sprint 4 — CI + persistence correctness

- fix all current CI failures;
- API token lifecycle tests;
- idempotent RLS cleanup;
- fake process tests;
- separate GPU integration tests;
- migrations verification.

**Результат:** стабильный зелёный CI.

## Sprint 5 — State + architecture

- Desired/Applied revisions.
- Application/Infrastructure inversion.
- application abstractions.
- package version alignment.
- ProblemDetails contract.

## Sprint 6 — Agent runtime / pipeline

- `IAgentRuntime`.
- OpenCode adapter.
- PipelineHandlers split.
- subtask tool/path enforcement.
- process lifecycle audit.

## Sprint 7 — Quality/evaluation/context

- ReviewRun.
- stronger eval pass/fail.
- with/without brief experiment isolation.
- CodeIndex -> brief enrichment.
- final token accounting.

## Sprint 8 — Production readiness + docs

- VRAM telemetry correction.
- Worker documentation/contract.
- production verification matrix.
- design-doc-v3. Сделано в master Sprint 10: `ProjectBeacon-design-doc-v3.md`. Отдельный `ProjectBeacon-roadmap-v3.md` не создаётся.
- README/AGENTS/mcp/docs synchronization. Сделано в master Sprint 10.
- archive obsolete docs. Сделано: `archive/docs/`.

---

# 23. Definition of Done для roadmap

Roadmap можно считать завершённым, когда выполнены все следующие условия:

### Security

- bootstrap fail-closed;
- logout не позволяет атакующему завершать чужие сессии;
- API token нельзя использовать вне его project scope;
- actor identity нигде не берётся из недоверенного request body без специального delegation flow;
- project/member/token operations проверяют actor authorization;
- workstation filesystem/process operations имеют общий root boundary.

### Architecture

- Application не зависит напрямую от Infrastructure;
- OpenCode не является application-level contract;
- agent runtime представлен через abstraction;
- desired/applied state различаются.

### Quality

- ordinary CI зелёный;
- real-process/GPU tests вынесены в отдельный integration lane;
- RLS tests повторяемы;
- evaluation проверяет outcome, а не только generation completion;
- review proof не является self-reported boolean.

### Documentation

- [x] `ProjectBeacon-design-doc-v3.md` — authoritative architecture;
- [x] execution roadmap — master roadmap v1, не отдельный `ProjectBeacon-roadmap-v3.md` (конфликт §21 закрыт в пользу master);
- [x] README и AGENTS ссылаются только на актуальные документы;
- [x] stale plans архивированы;
- [x] worker/mcp/workstation/security semantics описаны актуально.

---

# 24. Короткий приоритетный список для следующего execution cycle

Если выполнять работу строго по критичности:

```text
1. Bootstrap fail-closed
2. Logout IDOR
3. ActorContext
4. API token project binding
5. Unified authorization
6. Workstation root boundary for Chat/Eval/OpenCode
7. Green CI
8. RLS cleanup isolation
9. Desired/Applied revisions
10. Application -> Infrastructure inversion
11. IAgentRuntime
12. ReviewRun + real eval semantics
13. CodeIndex -> context brief
14. Subtask capability/path enforcement
15. VRAM telemetry correction
16. Production verification
17. Documentation consolidation
```

Этот порядок минимизирует ситуацию, когда новые agent features строятся поверх неустранённых security и architecture inconsistencies.
