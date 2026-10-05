# ProjectBeacon — Hardening & Technical Debt Roadmap

**Дата:** 2026-10-02  
**База анализа:** текущий `main`, commit `fd346200b446ae7be6dca87b63390e3996e767b1`  
**Назначение:** единый документ, объединяющий результаты глубокого code/security/runtime/UI-аудита и исполняемый план исправлений.

---

## 0. Статус проекта на момент аудита

ProjectBeacon уже вышел за пределы простого CRUD-приложения. В текущей архитектуре присутствуют:

- control plane API;
- tenant isolation;
- PostgreSQL RLS;
- EF Core query filters;
- workstation daemon;
- project runtime binding;
- filesystem sandbox;
- local MCP;
- OpenCode integration;
- local model backend / llama-swap;
- agent templates;
- task pipeline;
- review/evaluation runs;
- desired/applied state;
- context compiler;
- Web UI с responsive layout;
- Playwright screenshot baseline.

Главный риск на текущей стадии — **не отсутствие отдельных функций, а несогласованность trust boundaries и state contracts**.

Особенно важно привести к единой модели:

```text
Human
ApiToken
Device
Worker
Organization
Project
ProjectRuntime
Workstation
AgentRuntime
OpenCode
MCP
```

Сейчас эти сущности уже существуют, но identity, authorization и runtime state местами передаются через разные наборы `Guid`, claims и request fields. Это увеличивает вероятность security regression и рассинхронизации состояния.

---

# 1. Executive Summary

## Критические направления

| Область | Состояние | Приоритет |
|---|---|---|
| Actor model | неоднородная | **P0** |
| DeviceToken vs Human identity | потенциальное смешение | **P0** |
| API token authorization | capability enforcement неполный | **P0/P1** |
| Cross-platform path security | обнаружен CI failure | **P1** |
| CI | красный | **P1** |
| Command queue claim | возможен race | **P1** |
| OpenCode runtime isolation | слишком глобальная модель | **P1** |
| Chat lifecycle | state может расходиться с runtime | **P1** |
| Eval / review proof | хорошая основа, нужен hardening | **P1** |
| Desired/applied reconciliation | хорошая база, нужно довести контракт | **P1/P2** |
| Runtime permissions | недостаточно формализованы | **P1/P2** |
| Application abstractions | semantic leakage | **P2** |
| UI | сильно улучшен | **P2/P3** |
| Visual regression in CI | отсутствует | **P2** |
| Documentation consistency | есть расхождения | **P2** |

---

# 2. Главные замечания, которые должны быть исправлены

## 2.1 DeviceToken может выглядеть как Human actor

`ApiTokenAuthMiddleware` создаёт для device token claims, среди которых присутствует `ClaimTypes.NameIdentifier = device.UserId`.

В результате endpoint, который просто читает `NameIdentifier`, может принять device token за пользователя.

Это особенно опасно в маршрутах, где identity используется как actor:

- project operations;
- task/work operations;
- chat;
- часть user-oriented API.

### Требование

Ввести единый `ActorContext`:

```text
ActorContext
├── Type: Human | ApiToken | Device | Worker
├── UserId?
├── TokenId?
├── DeviceId?
├── ProjectId?
├── OrgId?
└── Capabilities
```

Device identity не должна автоматически означать human actor.

---

## 2.2 API token capabilities enforcement неполный

Есть `ApiTokenCapability`:

```text
TaskRead
TaskWrite
SessionDrive
ContextRead
Admin
```

Но capability checks применяются не ко всем API routes.

Получается потенциальная модель:

```text
Token(capabilities = TaskRead)
        |
        +--> endpoint with capability check  -> restricted
        |
        +--> endpoint without capability check -> potentially over-privileged
```

### Требование

Каждый API endpoint должен иметь explicit actor/capability classification.

Default должен быть fail-closed.

---

## 2.3 Cross-platform path security

`Path.IsPathRooted()` зависит от ОС процесса. Control plane и workstation могут работать на разных ОС.

Например Windows-style path:

```text
C:\secret
```

может не трактоваться как absolute path Linux-кодом так же, как на Windows.

Это уже подтверждено CI тестом:

```text
ProjectCommand_RejectsAbsolutePath_AndClaimCarriesRuntimeRoot
```

### Требование

Сделать portable path parser/validator, который явно распознаёт:

```text
/
C:\
C:/
\\server\share
//server/share
../
..\
```

и использует один и тот же security contract на сервере и клиенте.

---

## 2.4 EvalCheck может неверно трактовать exit code

Текущий `EvalCheck` строит shell command через строку аргументов.

CI показал failure:

```text
EvalCheckTests.Execute_NonZeroExit_IsFailure
```

Это критично, потому что eval/check result участвует в доказательстве результата agent work.

### Требование

Использовать `ProcessStartInfo.ArgumentList`:

```text
Windows:
  cmd.exe
  /c
  <command>

Unix:
  /bin/sh
  -c
  <command>
```

---

## 2.5 Command queue claim не атомарен

Текущая схема концептуально выглядит как:

```text
SELECT pending command
      ↓
Claim()
      ↓
SaveChanges()
```

Два daemon процесса потенциально могут увидеть один command.

### Требование

Сделать atomic claim через PostgreSQL transaction/locking:

```sql
SELECT ... FOR UPDATE SKIP LOCKED
```

или эквивалентный `UPDATE ... RETURNING`.

---

## 2.6 Один глобальный OpenCode runtime на workstation

`WorkstationDaemon` хранит один `ClientOpenCodeServe`, один lock и один runtime process context.

При этом разные project runtimes имеют разные `LocalRoot`.

Потенциальная проблема:

```text
Project A → cwd A
Heartbeat → ProjectsRoot
Project B → cwd B
```

Один глобальный process/context затрудняет безопасную параллельную работу нескольких проектов.

### Требование

Формализовать один из контрактов:

1. OpenCode действительно поддерживает независимые sessions/workdirs в рамках одного process и это доказано тестами.
2. Каждый `ProjectRuntime` получает отдельный agent runtime.

Для ProjectBeacon второй вариант является более чистой долгосрочной моделью.

---

## 2.7 Chat state может расходиться с runtime state

Пример:

```text
Idle
 ↓
Streaming
 ↓
enqueue ChatPrompt fails
 ↓
Streaming forever
```

Аналогично создание session может оставить DB session без external runtime session.

### Требование

Ввести явную state machine:

```text
Creating
  ↓
Provisioning
  ↓
Ready
  ↓
Streaming
  ↓
Idle
```

и error transitions:

```text
Creating → Failed
Provisioning → Failed
Streaming → Failed
```

---

## 2.8 Abort может сообщать success раньше runtime

Текущая последовательность:

```text
DB session = Aborted
        ↓
enqueue ChatAbort
```

Если enqueue не удался, DB говорит `Aborted`, а OpenCode может продолжить работать.

### Требование

Использовать:

```text
AbortRequested
      ↓
runtime confirms abort
      ↓
Aborted
```

или явно иметь `AbortUnknown/Failed`.

---

## 2.9 LocalRoot раскрывается project members

Текущий runtime DTO может показывать raw path:

```text
C:\Users\...
/home/user/...
```

обычным project members.

### Требование

Разделить:

```text
RuntimeSummary
RuntimeDiagnostics
```

Raw local path должен быть доступен только там, где это действительно требуется.

---

## 2.10 Runtime attach/detach имеет слишком широкий access

`AttachRuntime` / `DetachRuntime` сейчас концептуально опираются на membership.

Нужно явно решить:

```text
Member        → use runtime
Owner/Admin   → attach/detach runtime
Device owner  → manage device
System admin  → override
```

---

## 2.11 Application layer всё ещё знает слишком много об EF/Infrastructure

Несмотря на устранение прямой project dependency, `Application` всё ещё опирается на EF Core abstractions и interfaces в namespace `ProjectBeacon.Infrastructure.*`.

Примеры:

- `IBeaconDb`;
- `ILlamaSwapProxy`;
- `IEmailSender`.

### Требование

Новые application-owned abstractions должны находиться в:

```text
ProjectBeacon.Application.Abstractions.*
```

а Infrastructure должна реализовывать их.

Массовый отказ от EF сейчас не требуется.

---

## 2.12 Desired/Applied state уже хорош, но нужен строгий contract

Текущая модель:

```text
DesiredRevision
AppliedRevision
ConfigRevision
AppliedConfigRevision
```

является правильным фундаментом.

Нужно закрепить правило:

```text
Desired state = source of truth
Command = delivery mechanism
Heartbeat = repair mechanism
```

Если command потерян или daemon был offline, следующая reconciliation должна восстановить desired state автоматически.

---

## 2.13 Review/Eval proof должен быть объективным

`ReviewRun` уже существует, что хорошо.

Нужно считать task complete только при доказанном review/check result:

```text
ReviewRun
TaskId совпадает
ProjectId совпадает
Status = Completed
CheckProof = true
```

`ReviewerRun=true` из request body не должен быть достаточным доказательством.

---

## 2.14 Eval completion != task success

Нужно различать:

```text
Command executed successfully
```

и:

```text
Task outcome passed
```

Целевой pipeline:

```text
Agent execution
      ↓
Artifact produced
      ↓
Build/tests/checks
      ↓
Review
      ↓
Pass
      ↓
Task complete
```

---

## 2.15 Command payload не имеет явной versioned schema

Текущий `PayloadJson` удобен, но при независимом обновлении daemon/server создаёт compatibility risk.

Целевой контракт:

```json
{
  "version": 1,
  "kind": "ApplyOpencode",
  "payload": {}
}
```

---

## 2.16 Client/server protocol version недостаточно формализован

Необходимо явно определить:

```text
client protocol version
minimum supported server
minimum supported client
migration compatibility
```

---

## 2.17 Logout semantics не полностью формализованы

Есть одновременно:

```text
browser/cookie logout
DB session logout
JWT
```

Нужно явно определить:

```text
logout current browser
logout current DB session
logout all sessions
revoke token
JWT invalidation policy
```

---

## 2.18 Break-glass recovery слишком мощный

Bootstrap/recovery используют один и тот же secret boundary.

Рекомендуется разделить:

```text
BOOTSTRAP_ADMIN_TOKEN
ADMIN_RECOVERY_TOKEN
```

либо сделать recovery отдельным runtime/console механизмом.

---

## 2.19 Mail delivery может скрывать реальные сбои

Ошибки email delivery не должны превращать систему в полностью слепую.

Нужны:

- structured logging;
- delivery diagnostics;
- outbox/retry при необходимости.

При этом anti-enumeration semantics для password reset нужно сохранить.

---

## 2.20 Chat polling не использует явный delta cursor

Текущий daemon периодически собирает части и дедуплицирует их локально.

Для длинных sessions лучше иметь:

```text
lastExternalPartId
→ fetch delta
```

вместо повторного чтения уже обработанных данных.

---

## 2.21 Eval usage accounting нужно разделить на raw/provider и Beacon semantics

Нужно хранить raw provider usage отдельно от агрегированных Beacon metrics.

Целевые модели:

```text
ProviderUsage
BeaconUsage
```

Это особенно важно для сравнительных evals.

---

## 2.22 Eval pair должен контролировать все переменные кроме исследуемого фактора

Для `with brief` / `without brief` должны быть одинаковыми:

- model;
- provider/backend;
- model parameters;
- reasoning effort;
- temperature;
- tool permissions;
- repository revision;
- prompt;
- timeout;
- environment;
- MCP set;
- runtime config.

Иначе сравнение не является чистым экспериментом.

---

## 2.23 Visual regression есть, но не встроен в CI

Playwright matrix уже охватывает desktop/tablet/mobile, но текущий основной CI его не выполняет.

Нужно сделать отдельный visual regression pipeline с pinned browser/fonts/environment.

Exact PNG hash без контролируемой среды использовать осторожно.

---

## 2.24 UI polish review требует синхронизации статусов

Последний UI polish review уже содержит `Fixed` по всем семи находкам, но документ исторически обновлялся после разных состояний.

Он должен отражать реальное состояние кода и CI.

---

# 3. Hardening Roadmap

---

# Phase H0 — Actor & Trust Boundary

## Цель

Сделать actor identity единой частью модели приложения.

### H0.1 ActorContext

Создать:

```text
Application/Authorization/
  ActorType.cs
  ActorContext.cs
  IActorContextAccessor.cs
```

### H0.2 Authentication adapters

Каждый authentication mechanism строит `ActorContext`:

```text
Human
ApiToken
Device
Worker
```

### H0.3 DeviceToken isolation

Device token никогда не должен автоматически считаться human actor.

### H0.4 API token scope

Для API token:

```text
effective ProjectId = token.ProjectId
```

Route/header override → `403`.

### Gate H0

```text
[ ] ActorContext используется во всех security-sensitive use cases
[ ] Device → Human confusion невозможен
[ ] Token scope не расширяется route/header
[ ] Unit + integration tests проходят
```

---

# Phase H1 — Authorization Matrix

## Цель

Формализовать и централизовать access policy.

### H1.1 Permission matrix

Создать документ:

```text
docs/authorization-matrix.md
```

Зафиксировать доступ для:

- Human;
- Owner;
- Admin;
- Member;
- ApiToken;
- Device;
- Worker.

### H1.2 Authorization service

Целевая форма:

```text
Can(actor, resource, action)
```

### H1.3 Endpoint classification

Каждый API endpoint обязан явно указывать:

```text
Public
Human only
ApiToken + capability
Device only
Human + ApiToken
```

### H1.4 Capability wall

Применить capability checks ко всем relevant API token routes.

### Gate H1

```text
[ ] Permission matrix approved
[ ] No unclassified API routes
[ ] API token defaults fail-closed
[ ] Negative cross-project tests pass
```

---

# Phase H2 — Identity Contract Hardening

## Цель

Убрать client-controlled actor identity.

Проверить и устранить:

```text
UserId
ActorId
ActorUserId
CreatedByUserId
```

из request DTO там, где значение означает текущего caller.

### Правило

```text
Request = target data
Actor = server-side
```

### Gate H2

```text
[ ] Actor identity cannot be forged through request body
[ ] CreatedBy semantics are server-derived
[ ] Session/work/task actor fields are server-derived
```

---

# Phase H3 — Workstation Trust Boundary

## H3.1 ProjectRuntime resolver

Все project-bound local operations проходят через:

```text
ProjectId
→ ProjectRuntime
→ trusted LocalRoot
→ relative path
```

## H3.2 Portable WorkspacePath

Единый cross-platform validator.

### Intentional hardening (implemented with H3.1)

`CommandSandbox.SanitizeProjectPayload` rejects any `path` value containing a `..` segment (both `/` and `\` separators) at the control-plane boundary. This is intentional security hardening, not strictly behavior-preserving: a client that previously could send `a/../b` (which would normalize back inside the workspace) now receives `WorkspacePath.RelativePathRequired`. Existing valid relative paths are unaffected. Filenames with embedded dots (e.g. `file..name.cs`) are not rejected.

## H3.3 Unified sandbox

Один contract для:

- file read/write;
- patch;
- tree/search;
- Chat;
- Eval;
- Review;
- OpenCode;
- project init.

## H3.4 Runtime path privacy

Разделить runtime summary и diagnostics.

### Gate H3

```text
[ ] All project commands require ProjectRuntime
[ ] No arbitrary absolute local path crosses control plane boundary
[ ] Linux and Windows path tests pass
[ ] Reparse/symlink escape tests pass
[ ] Raw LocalRoot hidden from unauthorized members
```

---

# Phase H4 — Command Queue Correctness

## H4.1 Atomic claim

Использовать transaction + locking (`FOR UPDATE SKIP LOCKED`) либо atomic update/returning.

## H4.2 Delivery semantics

Для каждой command kind определить:

```text
Idempotent
Retryable
At-most-once
Reconcile-backed
```

## H4.3 Versioned CommandEnvelope

Ввести explicit command version.

### Gate H4

```text
[ ] No duplicate claims under concurrency
[ ] Retry semantics defined
[ ] Command version persisted/understood
[ ] Old/new daemon compatibility tested
```

---

# Phase H5 — Agent Runtime Isolation

## H5.1 Runtime contract

Формально доказать либо:

```text
one process / multiple independent workdirs
```

либо перейти к:

```text
ProjectRuntime A → AgentRuntime A
ProjectRuntime B → AgentRuntime B
```

## H5.2 RuntimeManager

Ввести:

```text
IAgentRuntimeManager
```

с операциями:

- Get;
- Start;
- Stop;
- Restart;
- Status.

### Gate H5

```text
[ ] Two projects can run independently
[ ] cwd changes cannot affect another project
[ ] Runtime ownership is explicit
[ ] Runtime state is observable
```

---

# Phase H6 — Chat State Machine

## H6.1 Session creation

```text
Creating → Provisioning → Ready
                     └→ Failed
```

## H6.2 Prompt lifecycle

```text
Ready → Streaming → Idle
              └→ Failed
```

## H6.3 Abort lifecycle

```text
Streaming → AbortRequested → Aborted
                         └→ AbortFailed
```

## H6.4 Delta streaming

Использовать incremental part cursor.

### Gate H6

```text
[ ] DB session state matches runtime state
[ ] Failed commands do not leave Streaming state forever
[ ] Abort does not report success before runtime confirmation
[ ] Long chats do not repeatedly re-read full transcript
```

---

# Phase H7 — Evaluation & Review Proof

## H7.1 EvalCheck correctness

Исправить exit-code handling через `ArgumentList`.

## H7.2 ReviewRun as source of proof

`finish_work(done)` требует валидный ReviewRun.

## H7.3 Objective task success

```text
Execution
→ Artifact
→ Check
→ Review
→ Pass
→ Done
```

## H7.4 Experimental control

Eval pair фиксирует все переменные кроме исследуемого фактора.

### Gate H7

```text
[ ] exit 0/1/2/3 tests pass
[ ] timeout is failure
[ ] review proof cannot be forged
[ ] done cannot be reached through self-report
[ ] eval pairs are reproducible
```

---

# Phase H8 — Desired / Applied Reconciliation

## Contract

```text
Desired state = truth
Command = delivery
Heartbeat = repair
```

### Test matrix

```text
command lost
command failed
server restart
daemon restart
device offline
device reconnect
duplicate command
partial application
```

### Gate H8

```text
[ ] AppliedRevision never advances without actual success
[ ] Reconnect repairs stale state
[ ] Lost command is recoverable
[ ] Duplicate reconciliation is harmless
```

---

# Phase H9 — Application Architecture

## H9.1 Application-owned abstractions

Новые interfaces размещать в:

```text
ProjectBeacon.Application.Abstractions.*
```

## H9.2 Infrastructure implementations

Infrastructure реализует application abstractions.

## H9.3 EF boundary

Не делать тотальный rewrite EF сейчас.

Цель — не допустить дальнейшего leakage.

### Gate H9

```text
[ ] No new Application → Infrastructure namespace dependencies
[ ] Runtime/security/mail abstractions are Application-owned
[ ] Domain remains infrastructure-free
```

---

# Phase H10 — Runtime / Device Permissions

Определить access contract:

```text
Member
  → use runtime

Owner/Admin
  → attach/detach runtime

Device owner
  → manage own device

System admin
  → override where explicitly allowed
```

### Gate H10

```text
[ ] Attach/detach protected
[ ] Device ownership explicit
[ ] Runtime use separate from runtime management
[ ] Device actor cannot become Human actor
```

---

# Phase H11 — Authentication Hardening

## H11.1 Bootstrap

Добавить race-safe tests:

```text
missing token
wrong token
correct token
second bootstrap
parallel bootstrap
```

## H11.2 Recovery

Разделить bootstrap/recovery secret или реализовать explicit break-glass mechanism.

## H11.3 Logout

Документировать и реализовать:

```text
current browser logout
current session logout
logout all
JWT policy
```

### Gate H11

```text
[ ] Bootstrap is fail-closed
[ ] Bootstrap is race-safe
[ ] Recovery is explicit
[ ] Logout semantics are documented and tested
```

---

# Phase H12 — CI, Security Matrix & Concurrency

## H12.1 Green CI

Исправить текущие failures:

1. `EvalCheckTests.Execute_NonZeroExit_IsFailure`
2. `DeviceHandlerTests.ProjectCommand_RejectsAbsolutePath_AndClaimCarriesRuntimeRoot`
3. `WorkstationActionsTests.InitProject_WritesGitignoreAndOpencode`

После исправлений выполнить минимум два полных последовательных CI run.

## H12.2 Security matrix

Комбинации:

```text
Human
ApiToken
Device
Admin
Owner
Member
```

×

```text
Project A
Project B
Org A
Org B
```

×

```text
Read
Write
Admin
Runtime
```

## H12.3 Concurrency

Тестировать:

- double claim;
- duplicate reconcile;
- concurrent runtime operations;
- concurrent chat operations;
- duplicate completion;
- abort races.

### Gate H12

```text
[ ] CI green
[ ] 2 consecutive green runs
[ ] Security integration matrix green
[ ] Concurrency tests green
```

---

# Phase H13 — Visual Regression & UI Stabilization

Большой UI redesign не требуется.

## H13.1 CI integration

Подключить Playwright screenshot matrix к CI.

## H13.2 Environment pinning

Зафиксировать:

- Chromium version;
- fonts;
- viewport;
- OS/container.

## H13.3 Visual threshold

Предпочтительно использовать perceptual/pixel threshold вместо абсолютного SHA256 PNG equality.

## H13.4 UI-specific remaining checks

Проверить:

- mobile Chat safe area;
- Board density;
- Task Detail tab discoverability;
- card/border density;
- Beacon-specific runtime/pipeline visual language.

### Gate H13

```text
[ ] Playwright matrix runs in CI
[ ] Desktop 1440 verified
[ ] Desktop 1280 verified
[ ] Tablet 1024/768 verified
[ ] Mobile 390/360 verified
[ ] No unexplained visual regressions
```

---

# Phase H14 — Documentation Reconciliation

Создать четыре canonical documents:

```text
Architecture.md
SecurityModel.md
RuntimeModel.md
ExecutionRoadmap.md
```

## Architecture.md

Описать:

```text
Control Plane
Application
Domain
Infrastructure
CLI
Web
MCP
Runtime
```

## SecurityModel.md

Описать:

```text
Actor
Tenant
Role
Capability
RLS
Filesystem boundary
Device trust
API token
```

## RuntimeModel.md

Описать:

```text
User
Project
Device
ProjectRuntime
AgentRuntime
OpenCode
Model Backend
Desired/Applied
```

## ExecutionRoadmap.md

Только operational state:

```text
Current hardening block
Done
In progress
Blocked
Next
```

### Gate H14

```text
[ ] Documentation reflects code
[ ] No obsolete contradictory architecture rules
[ ] Master execution state is unambiguous
[ ] Specialized documents are implementation references, not competing roadmaps
```

---

# 4. Suggested Execution Order

Не начинать новые крупные features до прохождения H12.

```text
H0  Actor & Trust Boundary
 ↓
H1  Authorization Matrix
 ↓
H2  Identity Contract Hardening
 ↓
H3  Workstation Trust Boundary
 ↓
H4  Command Queue Correctness
 ↓
H5  Agent Runtime Isolation
 ↓
H6  Chat State Machine
 ↓
H7  Evaluation & Review Proof
 ↓
H8  Desired / Applied Reconciliation
 ↓
H9  Application Architecture
 ↓
H10 Runtime / Device Permissions
 ↓
H11 Authentication Hardening
 ↓
H12 CI / Security / Concurrency
 ↓
H13 Visual Regression
 ↓
H14 Documentation Reconciliation
 ↓
Production Readiness Verification
```

---

# 5. Gates

## Gate H0

```text
ActorContext unified
Human / ApiToken / Device / Worker separated
```

## Gate H3

```text
No arbitrary local path crossing project boundary
```

## Gate H4

```text
One command → one claimant
```

## Gate H5

```text
Project runtimes are independent
```

## Gate H7

```text
Task cannot become Done from self-report
```

## Gate H8

```text
Desired state is always recoverable
```

## Gate H12

```text
Two consecutive green CI runs
```

## Gate H14

```text
Documentation matches implementation
```

---

# 6. Definition of Done — весь Hardening Cycle

```text
[ ] Actor model unified
[ ] Human / ApiToken / Device / Worker separated
[ ] Authorization matrix enforced
[ ] API token capabilities fail closed
[ ] Cross-platform path validation passes
[ ] Single sandbox contract for local operations
[ ] Atomic workstation command claim
[ ] Runtime isolation defined and tested
[ ] Chat lifecycle matches actual runtime
[ ] Review proof objective
[ ] Eval exit codes reliable
[ ] Desired/applied reconciliation verified
[ ] Runtime/device permissions explicit
[ ] Authentication/recovery semantics explicit
[ ] Security integration matrix passes
[ ] Concurrency tests pass
[ ] Two consecutive green CI runs
[ ] Visual regression runs in CI
[ ] Architecture/security/runtime docs synchronized
```

---

# 7. Execution Rules for Agents

Этот документ — **единый Hardening Roadmap**, но он не отменяет специализированные implementation documents.

Правильный workflow:

```text
Current Hardening Block
        ↓
Read referenced implementation docs
        ↓
Inspect actual code
        ↓
Implement minimal coherent slice
        ↓
Run tests / verification
        ↓
Update status
        ↓
Proceed to next block only after Gate
```

Не использовать `Phase` специализированного документа как самостоятельную execution unit.

Для команд агентам использовать именно:

```text
Execute Hardening H0
Execute Hardening H1
...
Execute Hardening H14
```

а не:

```text
Execute UI Phase 7
Execute Backend Phase 13
```

---

# 8. Priority Rule

Если во время hardening обнаружена новая проблема:

### P0
Security boundary, tenant escape, identity confusion, privilege escalation, arbitrary filesystem/process escape.

→ Исправить до продолжения.

### P1
Data/state correctness, runtime correctness, command delivery, eval/review correctness, CI blocker.

→ Завершить в текущем hardening block.

### P2
Architecture debt, observability, maintainability, performance, documentation inconsistency.

→ Планировать после trust/runtime correctness.

### P3
Visual polish, non-critical UX refinements, cosmetic improvements.

→ Только после H12, если они не блокируют product usability.

---

# 9. Final Product Principle

ProjectBeacon должен в конечном состоянии соблюдать следующие инварианты:

```text
1. Identity is explicit.
2. Authorization is fail-closed.
3. Tenant scope cannot be forged.
4. A project cannot escape its workstation root.
5. One command has one claimant.
6. Runtime state is not declared successful before runtime confirmation.
7. Desired state can always be reconciled.
8. Agent success is proven, not self-reported.
9. Control plane and workstation have versioned contracts.
10. Documentation describes the actual system.
```

Это является главным критерием hardening-цикла.
