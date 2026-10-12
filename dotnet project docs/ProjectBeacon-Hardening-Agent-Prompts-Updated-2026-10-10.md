# Актуализация prompt pack — 2026-10-10

> Этот документ синхронизирован с `ProjectBeacon-Hardening-Roadmap-Updated-2026-10-10.md`.
> Новый актуальный порядок работ имеет приоритет над исходной нумерацией H-блоков ниже.
> Перед запуском любого prompt агент обязан проверить текущий код и CI: статусы из аудита — ориентир, а не доказательство работоспособности.

## Текущий маршрут выполнения

1. **P0.1 — PostgreSQL privilege boundary:** убрать использование суперпользователя приложением; подтвердить least privilege для runtime role.
2. **P0.2 — API token `TaskWrite` / process execution:** проверить и закрыть возможность запуска произвольной shell-команды через API-токен; не сломать легитимный task workflow.
3. **P1.1 — Production encryption key:** убрать константный fallback ключа шифрования и сделать Production-конфигурацию fail-closed.
4. **P1.2 — CI blockers:** актуализировать фактические failures, исправить path validation / EvalCheck / InitProject contract и security tests, не ослабляя проверки.
5. **P1.3 — RLS verification:** тестировать на реальных миграциях и проверять покрытие таблиц, роль приложения и tenant isolation.
6. **P1.4 — Runtime command recovery:** обработать зависшие `Running`, подтверждение исполнения, retry/reconcile и stale state.
7. **P1.5 — Blazor circuit isolation:** проверить tenant/user scoping при долгоживущих InteractiveServer circuits.
8. **P1.6 — Release hardening:** cookie `Secure`, trusted proxy configuration, конкурентные state transitions и два последовательных green CI-equivalent runs.
9. Затем продолжать только незакрытые пункты H0–H14 по обновлённой Hardening Roadmap.

### Статус уже имеющихся H-блоков

- **H0–H2:** по последнему аудиту считаются реализованными блоками, но перед закрытием цикла выполнить регрессионную проверку gates. Не переписывать заново без конкретного failing test/finding.
- **H3:** почти закрыт; выполнять только подтверждённые оставшиеся path/sandbox/privacy gaps.
- **H4.1–H4.3:** по последнему аудиту реализованы (atomic claim, delivery semantics, versioned command envelope). Не повторять реализацию; проверить concurrency, backward compatibility и recovery tests.
- **H5–H7:** проверять по фактическому коду и CI; не считать закрытыми только из-за наличия типов/документации.
- **H8:** отдельно проверить восстановление зависших `Running` и stale desired/applied state.
- **H9–H14:** статус определяется доказательствами, тестами и gates, а не наличием кода или прошлой формулировкой отчёта.

### Обязательные правила для всех новых prompts

- Начинай с конкретного finding и подтверждай достижимый code path.
- Один prompt = один ограниченный coherent slice; не выполняй соседние пункты.
- Для security findings сначала добавляй/запускай regression test, затем исправляй.
- Не ослабляй, не удаляй и не помечай тесты как skipped только ради green CI.
- Отделяй `CONFIRMED FIXED`, `PARTIAL`, `REGRESSION`, `NOT VERIFIED`, `OUT OF SCOPE`.
- В отчёте указывай файлы/строки, тесты, команды запуска и точные результаты.
- Не утверждай, что исправление проверено, если тест не запускался или инфраструктурная ошибка помешала проверке.
- После каждого P0/P1 fix: targeted tests, build и полный релевантный suite; затем независимый cold review.

---

# P0 — Critical Security Findings

## P0.1 — Remove PostgreSQL superuser from application runtime

```text
ProjectBeacon: выполнить только P0.1 — PostgreSQL privilege boundary.

Finding to verify:
приложение может подключаться к PostgreSQL под ролью с правами суперпользователя. Если это подтверждается текущей конфигурацией, tenant isolation и RLS не являются надёжной границей безопасности.

Сначала исследуй:
- все connection string sources и configuration precedence;
- Development / Test / CI / Production deployment configuration;
- migrations role vs application runtime role;
- PostgreSQL grants, ownership, RLS policies и FORCE ROW LEVEL SECURITY;
- Docker/compose и deployment secrets;
- integration-test database setup.

Требования:
1. Приложение в runtime не должно подключаться под PostgreSQL superuser.
2. Раздели migration/administrative privileges и минимальные runtime privileges, если архитектура этого требует.
3. Не выдавай runtime role права обходить RLS или менять политики.
4. Убедись, что таблицы и sequence имеют необходимые, но минимальные grants.
5. Не выводи secrets в логи и отчёты.
6. Не меняй tenant semantics ради прохождения тестов.

Tests / verification:
- integration tests запускаются под runtime role, а не superuser;
- tenant A не читает и не изменяет данные tenant B;
- RLS включён и реально применяется для нужных таблиц;
- обычные операции приложения работают с минимальными grants;
- миграции выполняются отдельным документированным способом.

Сначала сообщи, подтверждён ли finding и какие deployment environments затронуты.
Затем реализуй минимальное исправление.
Не выполняй P0.2 и не переходи к unrelated DB refactoring.
```

## P0.2 — Close arbitrary process execution through API-token TaskWrite

```text
ProjectBeacon: выполнить только P0.2 — API token process-execution boundary.

Finding to verify:
API token с capability TaskWrite может ли инициировать произвольную shell-команду или другой недопустимо широкий process execution path через task/work API?

Проследи полный путь:
API authentication
→ ActorContext / token capabilities
→ endpoint
→ request DTO
→ handler
→ command/task pipeline
→ workstation daemon
→ process/shell invocation.

Требования:
1. Подтверди конкретный достижимый exploit path; не делай вывод только по имени capability.
2. Раздели право создавать/изменять task и право инициировать privileged execution.
3. API token не должен получать implicit process-execution capability.
4. Если выполнение команд является штатной функцией продукта, ограничь его явным authorization contract, trusted task specification и sandbox boundary.
5. Не полагайся на UI validation; enforcement должен быть server-side.
6. Сохрани легитимный workflow авторизованного пользователя/агента.
7. Не добавляй новую широкую capability без документированного threat model.

Tests:
- TaskWrite token не может обойти execution policy;
- token без нужной capability получает отказ;
- разрешённый actor выполняет легитимный сценарий;
- cross-project и forged scope cases отклоняются;
- попытки передать command/payload, обходящий sandbox, отклоняются.

В отчёте укажи endpoint, handler, execution sink и доказательство до/после.
Не выполняй H1 целиком и не меняй несвязанные task semantics.
```

## P1.1 — Remove encryption-key fallback

```text
ProjectBeacon: выполнить только P1.1 — encryption key configuration.

Проверь фактическую реализацию шифрования и все источники ключа.
Если существует константный/default fallback key, убери возможность незаметно использовать его в Production.

Требования:
- Production startup fail-closed при отсутствующем/некорректном ключе;
- Development/test fallback допустим только при явном безопасном режиме и не должен попасть в Production;
- не логировать ключ или его производные секретные значения;
- описать формат, ротацию и требования к хранению ключа;
- не менять алгоритм/формат ciphertext без migration plan.

Tests:
- Production + отсутствующий ключ → startup/configuration failure;
- Production + некорректный ключ → явная ошибка;
- корректный ключ → encrypt/decrypt round-trip;
- старые зашифрованные данные остаются читаемыми согласно текущему контракту.

Не выполняй общий security refactor.
```

## P1.2 — Current CI blockers without weakening tests

```text
ProjectBeacon: выполнить только P1.2 — актуальные CI blockers.

Сначала изучи актуальный CI run и воспроизведи failures. Не считай исторические failures всё ещё существующими без проверки.

При подтверждении проверь:
- EvalCheck non-zero exit;
- cross-platform path validation;
- InitProject path serialization/test contract;
- security test failures;
- остальные красные тесты из актуального CI.

Для каждого failure:
1. точный test name и stack trace;
2. root cause в коде или тестовом окружении;
3. минимальное исправление;
4. targeted test;
5. полный релевантный suite.

Запрещено:
- удалять или ослаблять тест без отдельного обоснования;
- добавлять общий skip guard;
- маскировать failure как warning;
- объявлять CI green по одному выборочному тесту.

Gate: два последовательных полных CI-equivalent runs green.
Если внешний сервис/инфраструктура мешает запуску, укажи это отдельно и не называй проверку успешной.
```

## P1.3 — Verify RLS on real migrations and runtime role

```text
ProjectBeacon: выполнить только P1.3 — RLS coverage verification.

Цель — проверить фактическую защиту данных, а не только наличие policy definitions.

Исследуй:
- реальные миграции и итоговую схему;
- список tenant-scoped таблиц;
- ENABLE ROW LEVEL SECURITY / FORCE ROW LEVEL SECURITY;
- policies и их predicates;
- владельца таблиц и runtime database role;
- transaction/session context, которым задаётся tenant;
- connection pooling и очистку tenant context между запросами;
- тесты, запускаемые с реальными migrations.

Составь matrix:
table → tenant-scoped? → RLS enabled? → policy → runtime-role behavior → test.

Исправляй только подтверждённые gaps.
Добавь negative integration tests на cross-tenant SELECT/INSERT/UPDATE/DELETE там, где операции поддерживаются.
Проверь отсутствие утечки tenant context между последовательными запросами через pool.

Не считай тесты под superuser доказательством RLS enforcement.
Не переписывай все migrations без необходимости.
```

## P1.4 — Recover stuck Running commands and stale runtime state

```text
ProjectBeacon: выполнить только P1.4 — workstation command recovery.

Проверь путь command от enqueue до claim, daemon execution, result acknowledgement и reconciliation.

Ищи достижимые сценарии:
- command остаётся Running после daemon crash;
- process завершился, но acknowledgement потерян;
- command выполняется повторно после reconnect;
- stale lease/claim блокирует очередь;
- AppliedRevision продвигается без подтверждённого application;
- task/session показывает success до подтверждения runtime.

Сначала документируй существующую state machine, lease/timeout contract и recovery path.
Затем исправь только подтверждённые gaps.

Требования:
- различать retryable и non-idempotent command;
- повторная доставка не должна создавать двойной side effect там, где это можно предотвратить;
- desired state остаётся источником истины;
- heartbeat/reconciliation восстанавливает состояние после offline/restart;
- stale Running переводится в recovery state только по явному, проверяемому правилу;
- не объявлять команду успешной без соответствующего доказательства.

Добавь deterministic tests с barriers/channels или управляемыми fake runtime, не sleep-based flaky tests.
Не повторяй реализацию H4.1–H4.3, если их контракт уже работает.
```

## P1.5 — Blazor InteractiveServer circuit isolation

```text
ProjectBeacon: выполнить только P1.5 — Blazor circuit identity/tenant isolation.

Проверь долгоживущие InteractiveServer circuits и scoped services:
- как actor/user/tenant context создаётся;
- может ли authentication измениться после создания circuit;
- может ли пользователь потерять доступ, но продолжить действие через старый circuit;
- кешируются ли project/org resources без tenant/user key;
- как работают reconnect и circuit rehydration;
- как scoped DbContext/services ведут себя при длительном взаимодействии.

Не предполагай уязвимость заранее: докажи достижимую последовательность.

Добавь тесты/проверки для:
- sign-out или revoke во время активного circuit;
- переключение project/tenant;
- запрет доступа после membership removal;
- отсутствие cross-user cache/data leakage;
- reconnect после смены auth state.

Реализуй минимальную коррекцию identity revalidation, authorization checks или cache scope.
Не переписывай UI на другой hosting model.
```

## P1.6 — Production release hardening

```text
ProjectBeacon: выполнить только P1.6 — release security and state-transition gate.

Проверь и исправь подтверждённые gaps:
- auth cookies используют Secure в Production;
- trusted proxy/forwarded headers принимаются только от явно доверенных proxies/networks;
- host/proto forwarding не позволяет подменить security-sensitive URL semantics;
- concurrent state transitions для task, pipeline, review и command имеют ожидаемые concurrency semantics;
- error paths не сообщают success до завершения операции.

Для каждого finding:
- укажи конфигурацию и code path;
- добавь regression test;
- проверь Development/Test/Production различия;
- не вводи небезопасные default values для удобства локального запуска.

Final gate:
- build green;
- полный test suite green;
- security/concurrency matrix green;
- два последовательных полных CI-equivalent runs green.

Если CI не запускается из-за внешней инфраструктуры, отчёт должен явно отметить gate как NOT VERIFIED.
Не выполняй визуальный redesign.
```

---

# ProjectBeacon — Agent Prompts for Hardening Roadmap

Этот документ содержит отдельный рабочий prompt pack для coding agents.
Каждый prompt рассчитан на **один coherent slice** работ.

## Как использовать

Перед запуском конкретного prompt:

1. Убедись, что предыдущий Hardening block закрыт и его tests проходят.
2. Передай агенту **только один prompt ниже**.
3. Агент обязан изучить фактический код текущего `main`, а не доверять старой документации.
4. Агент не должен выполнять следующий H-блок.
5. После реализации агент обязан:
   - выполнить релевантные tests;
   - выполнить build;
   - для UI — выполнить screenshot verification;
   - перечислить изменённые файлы;
   - перечислить оставшиеся проблемы;
   - не объявлять задачу выполненной при красных tests.

---

# H0 — Unified Actor Model

## H0.1 — Introduce ActorContext

```text
Ты работаешь над ProjectBeacon.

Задача: реализовать H0.1 — Unified ActorContext.

Прочитай:
- dotnet project docs/ProjectBeacon-master-roadmap-v1.md
- dotnet project docs/ProjectBeacon-code-review-roadmap-v3.md
- текущую реализацию authentication middleware, endpoint authentication и authorization
- ProjectBeacon.API/Auth/*
- ProjectBeacon.Application/Authorization/*
- связанные tests

Главная проблема:
identity сейчас передаётся через смесь Claims, UserId, ActorId, AuthenticationType, project_id, device_id и capabilities. Из-за этого разные части системы по-разному понимают, кто является caller.

Нужно ввести единый server-side ActorContext минимум с:

- ActorType: Human | ApiToken | Device | Worker
- UserId?
- TokenId?
- DeviceId?
- ProjectId?
- OrgId?
- Capabilities

Требования:

1. ActorContext не должен зависеть от HTTP.
2. Claims/middleware должны конвертироваться в ActorContext на одной границе.
3. Application handlers не должны извлекать actor identity из ClaimsPrincipal.
4. Request DTO не должен использовать client-supplied UserId/ActorId как identity текущего caller.
5. Не делать массовый unrelated refactor.
6. Сохраняй текущую функциональность там, где она корректна.
7. Для переходного периода допустим adapter, но новый код должен использовать ActorContext.

Добавь tests минимум для:
- Human actor;
- ApiToken actor;
- Device actor;
- Worker actor;
- отсутствующая/некорректная identity;
- actor не должен случайно превращаться в Human только потому, что у него есть UserId.

После изменений:
- dotnet build
- релевантные unit/integration tests
- перечисли места, которые ещё используют старую identity model.

Не приступай к H0.2 и не меняй permission matrix.
```

## H0.2 — Prevent Device → Human confusion

```text
ProjectBeacon: выполнить только H0.2.

Прочитай H0.1 implementation и текущий authentication pipeline.

Проблема:
DeviceToken сейчас содержит владельца device как ClaimTypes.NameIdentifier, из-за чего user-oriented endpoints могут воспринимать device как human.

Нужно:

1. Device actor должен иметь:
   - ActorType = Device
   - DeviceId = device.Id
   - UserId = owner id как metadata, но НЕ как evidence of Human actor.
2. Human-only endpoints должны отклонять Device actor.
3. Device-only endpoints должны принимать только Device actor.
4. Не полагайся на отсутствие/наличие NameIdentifier как на actor classification.
5. Не ломай device heartbeat/command polling.

Добавь negative integration tests:
- device token → human-only endpoint = 403;
- device token → device endpoint = success;
- device token → project user operation = 403;
- human token → device-only operation = 401/403 согласно существующему contract.

Проверь все endpoints, использующие TryUserId/ClaimTypes.NameIdentifier.

Не выполняй H1 authorization matrix целиком. Исправь только actor-type boundary.
```

---

# H1 — Authorization Matrix

## H1.1 — Define permission matrix

```text
ProjectBeacon: выполнить только H1.1.

Цель: зафиксировать единую authorization matrix до изменения большого количества handlers.

Прочитай:
- H0 actor model
- существующие MemberRole
- ApiTokenCapability
- ProjectAuthorization
- API endpoint inventory
- current authorization tests

Создай canonical document:
dotnet project docs/ProjectBeacon-authorization-matrix.md

В документе явно определить права для:
- Human Member
- Human Admin
- Human Owner
- System Admin
- ApiToken с каждой capability
- Device
- Worker

Resource groups:
- Project
- Org
- Member
- Invite
- Task
- TaskStep
- Comment
- Context
- Decision
- Milestone
- Label
- Report
- Pipeline
- Review
- Eval
- Chat
- Device
- Runtime
- Model Backend
- Agent Template
- MCP
- Workstation configuration

Для каждой комбинации определить:
- Read
- Create
- Update
- Delete
- Execute/Drive
- Admin

Не изменяй runtime behavior на этом шаге.

Отдельно перечисли intentional broad permissions и почему они нужны.

Результат должен стать источником истины для следующих authorization changes.
```

## H1.2 — Unified authorization service

```text
ProjectBeacon: выполнить только H1.2.

Используй authorization matrix из ProjectBeacon-authorization-matrix.md.

Создай единый authorization subsystem вокруг:

Can(actor, resource, action)
или эквивалентной strongly typed модели.

Требования:
- fail closed;
- не дублировать десятки ad-hoc if;
- поддержать Human roles;
- ApiToken capabilities;
- project/org scope;
- Device restrictions;
- system admin bypass только там, где это явно разрешено.

Endpoint может делать coarse check, но окончательная resource authorization не должна зависеть только от endpoint.

Постепенно мигрируй существующий ProjectAuthorization в новую модель без массового rewrite.

Добавь tests на положительные и отрицательные случаи.

Не меняй unrelated business logic.
```

## H1.3 — Capability enforcement for API tokens

```text
ProjectBeacon: выполнить только H1.3.

Проведи inventory всех /v1 endpoints.

Для каждого endpoint классифицируй:
- Public
- Human only
- Device only
- ApiToken + capability
- Human or ApiToken
- System/admin only

Найди endpoints, где ApiToken сейчас может пройти без explicit capability.

Особое внимание:
- Tasks
- Chat
- Context
- Pipeline
- Work
- Reports
- Decisions
- Milestones
- Labels
- Models
- Runtime
- Devices

Правило:
ApiToken по умолчанию не получает никаких implicit capabilities.

Если endpoint должен быть доступен token'у — capability должна быть явно указана.

Добавь integration tests на каждый ресурсный домен.
Не меняй Human authorization без необходимости.
```

---

# H2 — Identity and Request Contract Hardening

## H2.1 — Remove client-controlled actor identity

```text
ProjectBeacon: выполнить только H2.1.

Проведи аудит request DTO и handlers на:
- UserId
- ActorId
- ActorUserId
- CreatedByUserId

Раздели:
1. caller identity — только server-side ActorContext;
2. target resource — client input;
3. delegated identity — отдельный explicit use case.

Удаляй client-controlled actor identity только там, где поле описывает текущего caller.

Проверь:
- project creation;
- org creation;
- project members;
- tokens;
- sessions;
- tasks;
- pipeline;
- review;
- work;
- invites;
- bootstrap/recovery.

Для каждого изменённого endpoint добавь negative test с подменой user id.

Не ломай legitimate target-user operations.
```

## H2.2 — Explicit actor requirements at API layer

```text
ProjectBeacon: выполнить только H2.2.

Создай lightweight endpoint authorization helpers, например:
- RequireHuman
- RequireDevice
- RequireApiToken(capability)
- RequireHumanOrApiToken(capability)

Названия и API подбери в стиле существующего проекта.

Цель:
endpoint должен явно описывать допустимый actor type.

Уменьши повторяющиеся конструкции:
- TryUserId
- IsDevice
- AuthenticationType string comparisons

Не удаляй их, пока все usages не мигрированы.

После миграции добавь tests на matrix actor type × endpoint class.
```

---

# H3 — Workstation Trust Boundary

## H3.1 — ProjectRuntime resolver

```text
ProjectBeacon: выполнить только H3.1.

Цель:
все project-bound workstation operations должны разрешаться через trusted ProjectRuntime.

Новая логика:

ProjectId
→ ProjectRuntime
→ trusted LocalRoot
→ relative path
→ WorkspacePath validation
→ runtime operation

Control plane не должен доверять arbitrary local path как authority.

Проверь:
- Chat
- Eval
- Review
- OpenCode
- InitProject
- ApplyOpencode
- file operations

Сохрани текущую sandbox behavior.

Добавь tests:
- valid runtime;
- missing runtime;
- wrong device;
- wrong project;
- wrong LocalRoot;
- path traversal.

Не переходи к H3.2, если можно отделить изменения.
```

## H3.2 — Cross-platform path validation

```text
ProjectBeacon: выполнить только H3.2.

Исправь path validation так, чтобы protocol semantics не зависели от OS control plane.

Распознавай минимум:
- /
- C:\
- C:/
- \\server\share
- //server/share
- ../
- ..\

Создай deterministic portable helper, а не набор OS-specific if по всему проекту.

Проверь:
- WorkspacePath
- CommandSandbox
- enqueue validation
- daemon-side validation

Добавь matrix tests, которые запускаются и на Windows, и на Linux.

Не меняй unrelated filesystem behavior.
Цель: одинаковый security contract на всех supported OS.
```

## H3.3 — One sandbox contract

```text
ProjectBeacon: выполнить только H3.3.

Найди все local filesystem/process entry points.

Для каждого ответь:
- какой root используется?
- кто его определяет?
- проходит ли operation через WorkspacePath?
- может ли payload подменить root?
- может ли абсолютный path обойти restriction?

Унифицируй:
Read
Write
Patch
Tree
Search
Chat
Eval
Review
OpenCode
Project init

Операции должны получать trusted runtime root и относительный path.

Добавь negative tests на traversal, absolute path, symlink/reparse point и mismatched runtime.
```

## H3.4 — Hide LocalRoot

```text
ProjectBeacon: выполнить только H3.4.

Найди UI/API, где LocalRoot показывается project member'у.

Раздели DTO:
- RuntimeSummary
- RuntimeDiagnostics

Обычный project member должен видеть:
- device;
- online/offline;
- runtime status;
- useful health data.

Raw:
- C:\...
- /home/...
- internal executable paths

не показывать обычному member.

Owner/admin/self-device diagnostics могут иметь расширенный доступ согласно authorization matrix.

Добавь API tests и UI verification.
```

---

# H4 — Command Queue Correctness

> **Status from 2026-10-10 audit: H4.1–H4.3 reported implemented.** Treat the prompts below as verification/recovery-only unless current code or tests prove a gap. Do not reimplement completed work.

## H4.1 — Atomic claim

```text
ProjectBeacon: выполнить только H4.1.

Проблема:
ClaimNextCommandHandler сначала читает Pending, затем отдельно переводит запись в Running.

Нужна атомарная claim semantics.

Выбери корректный PostgreSQL механизм:
- SELECT FOR UPDATE SKIP LOCKED
или
- UPDATE ... RETURNING.

Требование:
несколько daemon workers не могут выполнить один command одновременно.

Добавь concurrency integration test:
- создать один pending command;
- запустить много claim attempts;
- ровно один claimant получает command;
- остальные получают no command.

Проверь transaction boundaries и rollback.
```

## H4.2 — Command delivery semantics

```text
ProjectBeacon: выполнить только H4.2.

Для каждого WorkstationCommandKind классифицируй:
- AtMostOnce
- Retryable
- Idempotent
- ReconcileBacked

Минимум рассмотри:
ApplyOpencode
ReconcileDesired
SaveWorkstation
ChatEnsureSession
ChatPrompt
ChatAbort
RunEvalTurn
RunReviewCheck
Install
SwapModel

Зафиксируй контракт в code/docs.

Не добавляй retry blindly.
Для non-idempotent commands защити от duplicate execution.
```

## H4.3 — Versioned CommandEnvelope

```text
ProjectBeacon: выполнить только H4.3.

Заменить implicit PayloadJson contract на versioned command envelope:

{
  version,
  kind,
  payload
}

Сохрани backward compatibility для уже существующих command records, если это необходимо.

Daemon должен явно понимать unsupported version и завершать command понятной ошибкой.

Добавь tests:
- version 1 accepted;
- unsupported version rejected;
- malformed envelope rejected;
- legacy payload migration/compatibility если требуется.
```

---

# H5 — Runtime Architecture

## H5.1 — Define runtime isolation

```text
ProjectBeacon: выполнить только H5.1.

Проведи code-level анализ текущего OpenCode lifecycle.

Ответь на вопрос:
может ли один OpenCode serve process безопасно обслуживать одновременно несколько ProjectRuntime с независимыми cwd?

Рассмотри:
- ClientOpenCodeServe
- WorkstationDaemon
- TickAsync
- RestartAsync
- cwd
- port
- sessions
- concurrent projects

Если модель multi-project safe — зафиксируй доказанный contract и напиши concurrency tests.

Если нет — зафиксируй requirements для ProjectRuntime-specific runtime isolation.

На этом шаге допустим design/architecture change, но не делай H5.2 полностью.
```

## H5.2 — RuntimeManager

```text
ProjectBeacon: выполнить только H5.2.

Создай runtime manager abstraction:

IAgentRuntimeManager

Он должен отвечать за:
- Start
- Stop
- Restart
- Status
- session ownership

Runtime должен быть scoped к ProjectRuntime.

Не допускай, чтобы WorkstationDaemon напрямую переключал глобальный cwd как часть обычного chat request.

Добавь lifecycle tests:
- project A + project B;
- independent sessions;
- stop A does not affect B;
- restart A does not affect B;
- daemon restart can reconstruct runtime state.
```

---

# H6 — Chat State Machine

## H6.1 — Session creation lifecycle

```text
ProjectBeacon: выполнить только H6.1.

Сделай ChatSession state machine explicit.

Минимум:
Creating
Starting/Provisioning
Ready
Streaming
Idle
Failed
AbortRequested
Aborted

Нельзя сохранять session как Ready до фактического создания external OpenCode session.

Если remote creation failed:
- session должна стать Failed;
- error должен быть сохранён;
- повторное создание должно быть предсказуемым.

Добавь tests на timeout/failure/retry.
```

## H6.2 — Prompt lifecycle

```text
ProjectBeacon: выполнить только H6.2.

Исправь SendChatPrompt lifecycle.

Текущая проблема:
session.SetStreaming() сохраняется до enqueue, и enqueue failure может оставить session в Streaming forever.

Целевой контракт:
PromptRequested → Streaming → Idle
или
PromptRequested → Failed

Ensure:
- command failure;
- OpenCode unavailable;
- timeout;
- cancellation

никогда не оставляют ложный Streaming state.

Добавь tests.
```

## H6.3 — Abort lifecycle

```text
ProjectBeacon: выполнить только H6.3.

Abort должен отражать реальное состояние runtime.

Новая semantics:
AbortRequested
→ runtime abort command
→ runtime confirms
→ Aborted

Если enqueue/abort fails:
- не скрывать failure;
- состояние должно быть Recoverable/Failed или equivalent.

Добавь tests, включая daemon offline.
```

## H6.4 — Incremental chat streaming

```text
ProjectBeacon: выполнить только H6.4.

Проанализируй StreamPartsAsync/CollectPartsAsync.

Цель:
не перечитывать уже полученные части на каждом 250ms poll.

Ввести cursor:
- last external part id;
- sequence;
- timestamp/offset,
в зависимости от OpenCode contract.

Получать только delta.

Проверить:
- duplicate suppression;
- out-of-order parts;
- reconnect;
- lost poll;
- session restart.

Измерь, что длинные sessions не деградируют линейно от количества старых сообщений.
```

---

# H7 — Eval / Review Hardening

## H7.1 — Fix EvalCheck

```text
ProjectBeacon: выполнить только H7.1.

Исправь EvalCheck cross-platform process invocation.

Нельзя строить shell invocation через небезопасную конкатенацию Arguments.

Используй ProcessStartInfo.ArgumentList.

Unix:
  /bin/sh
  -c
  <command>

Windows:
  cmd.exe
  /c
  <command>

Tests:
- exit 0;
- exit 1;
- exit 3;
- stderr;
- timeout;
- invalid command;
- non-zero exit must never report success.

Не ослабляй tests ради green CI.
```

## H7.2 — Review proof

```text
ProjectBeacon: выполнить только H7.2.

ReviewRun должен быть единственным authoritative proof object для finish_work(done).

Условие done:
- ReviewRun exists;
- TaskId совпадает;
- ProjectId совпадает;
- status = completed/pass;
- check proof valid;
- regressions resolved.

Поля:
ReviewerRun
RegressionsFound
RegressionsFixed

из client request не должны самостоятельно создавать proof.

Добавь negative tests на:
- чужой ReviewRun;
- failed ReviewRun;
- wrong task;
- wrong project;
- missing ReviewRun;
- forged request review metadata.
```

## H7.3 — Execution vs task success

```text
ProjectBeacon: выполнить только H7.3.

Раздели:
Command succeeded
от
Eval passed
от
Review passed
от
Task completed.

Создай явный state transition pipeline.

Убедись, что:
successful process execution
не означает автоматически successful task.

Добавь tests для:
- command success + check failure;
- command failure;
- check success + review fail;
- review pass;
- partial result;
- interrupted run.
```

---

# H8 — Desired / Applied State

```text
ProjectBeacon: выполнить только H8.

Текущая модель DesiredRevision/AppliedRevision уже существует. Не переписывай её без необходимости.

Цель:
Desired state должен быть source of truth.
Command — delivery mechanism.
Heartbeat/reconciliation — repair mechanism.

Проверь и доведи до deterministic behavior:
- command lost;
- command failed;
- daemon restart;
- server restart;
- device offline;
- device reconnect;
- duplicate command;
- partial application.

Правило:
AppliedRevision может подняться только после подтверждения фактического application.

Добавь integration tests для каждого failure mode.

Не смешивай с новым command envelope, если можно разделить изменения.
```

---

# H9 — Application Architecture

## H9.1 — Move Application abstractions

```text
ProjectBeacon: выполнить только H9.1.

Найди interfaces, концептуально принадлежащие Application, но размещённые в namespace ProjectBeacon.Infrastructure.*.

Перенеси namespace/путь в:
ProjectBeacon.Application.Abstractions.*

Минимум:
- Data
- Security
- Mail
- Runtime

Infrastructure должно содержать implementations.

Не делай полный EF abstraction rewrite.

Цель:
новый Application code не должен выглядеть так, будто оно принадлежит Infrastructure.
```

## H9.2 — Prevent new infrastructure leakage

```text
ProjectBeacon: выполнить только H9.2.

Введи архитектурный guardrail.

Проверь:
Application → Infrastructure direct references / namespaces.

Устрани новые leakage points.

Если полный removal EF невозможен сейчас — документируй accepted dependency и boundary.

Добавь architecture test, если это разумно:
Application не может ссылаться на конкретные Infrastructure implementations.
```

---

# H10 — Runtime / Device Authorization

```text
ProjectBeacon: выполнить только H10.

Используй authorization matrix.

Явно определить:
- who can create device;
- who can revoke device;
- who can attach runtime;
- who can detach runtime;
- who can use runtime;
- who can view runtime diagnostics;
- whether device owner can act on a project;
- whether project member can attach their own workstation.

Ожидаемая модель должна быть предсказуемой:
Member = use
Owner/Admin = manage binding
Device owner = manage own device
System admin = break glass

Реализуй policy централизованно.

Добавь integration tests на:
- non-member;
- member;
- project admin;
- project owner;
- device owner;
- system admin;
- another user's device.
```

---

# H11 — Authentication Hardening

## H11.1 — Bootstrap tests and race

```text
ProjectBeacon: выполнить только H11.1.

Bootstrap должен:
- fail if token absent;
- fail for wrong token;
- succeed once;
- fail safely after bootstrap;
- be safe under two concurrent first-bootstrap requests.

Добавь integration test на concurrent bootstrap race.

Нельзя допустить создания двух admins.
```

## H11.2 — Recovery separation

```text
ProjectBeacon: выполнить только H11.2.

Оценить текущий break-glass recovery.

Цель:
bootstrap secret и admin recovery secret не должны автоматически быть одним и тем же privilege key.

Предложи и реализуй один explicit contract:
- separate recovery secret
или
- out-of-band recovery only.

Не ломай local development ergonomics без documented fallback.
```

## H11.3 — Logout semantics

```text
ProjectBeacon: выполнить только H11.3.

Зафиксируй semantics:
- browser logout;
- current session logout;
- logout all sessions;
- API token revocation;
- JWT invalidation.

Проверь cookie auth, DB sessions и JWT.

Не называй операцию logout, если она фактически удаляет только cookie.

Добавь tests для каждого semantics.
```

---

# H12 — CI / Test Hardening

> **Updated routing:** prioritize current P0/P1 findings above. H12.1 is a verification gate, not permission to weaken tests or blindly replay historical fixes.

## H12.1 — Green CI

```text
ProjectBeacon: выполнить только H12.1.

Используй текущий CI как источник фактических failures.

Сначала проверь актуальный CI и воспроизведи failures. Если подтверждены, исправь:
1. EvalCheck non-zero exit;
2. cross-platform path validation;
3. InitProject path serialization/test contract;
4. remaining security test failures.

После каждого fix:
- targeted tests;
- полный test suite.

В конце:
- два последовательных полных CI-equivalent runs;
- оба должны быть green.

Не удаляй и не ослабляй failing test без отдельного архитектурного обоснования.
```

## H12.2 — Security matrix

```text
ProjectBeacon: выполнить только H12.2.

Создай систематическую HTTP integration matrix:

Actors:
- Human Member
- Human Admin
- Human Owner
- System Admin
- ApiToken
- Device

Scopes:
- Project A
- Project B
- Org A
- Org B

Actions:
- Read
- Write
- Admin
- Execute
- Runtime

Покрыть cross-project and cross-org negative cases.

Цель:
security regression должен ловиться тестами до merge.
```

## H12.3 — Concurrency tests

```text
ProjectBeacon: выполнить только H12.3.

Добавить integration/concurrency tests для:
- command claim;
- duplicate reconcile;
- parallel runtime operations;
- double abort;
- concurrent chat prompt where relevant.

Тесты должны моделировать реальные interleavings, а не только последовательные вызовы.

Не делать flaky sleep-based tests.
Используй barriers/channels/tasks для deterministic synchronization.
```

---

# H13 — Visual Verification

## H13.1 — Playwright in CI

```text
ProjectBeacon: выполнить только H13.1.

Встроить screenshot matrix в CI.

Использовать:
- pinned Chromium;
- pinned fonts/environment;
- deterministic seed/fixture;
- fixed viewport matrix.

Не сравнивать raw PNG hash, если среда не полностью детерминирована.

Сделать meaningful visual threshold.

При mismatch CI должен:
- сохранить actual screenshot;
- сохранить diff;
- указать route + viewport.
```

## H13.2 — UI regression quality

```text
ProjectBeacon: выполнить только H13.2.

Проверить current visual baseline after hardening changes.

Особое внимание:
- mobile Chat FAB;
- Task Detail;
- Board;
- Agents;
- Settings;
- Workstations;
- Context.

Не делай новый redesign.
Цель — отсутствие regressions и соответствие текущей design system.
```

---

# H14 — Documentation Reconciliation

## H14.1 — Canonical architecture docs

```text
ProjectBeacon: выполнить только H14.1.

Создай или приведи в порядок четыре canonical docs:

Architecture.md
SecurityModel.md
RuntimeModel.md
ExecutionRoadmap.md

Документы должны описывать ACTUAL current code.

Не копируй старые roadmap section verbatim.

Для каждого утверждения вида:
"система делает X"
проверь соответствующий code path.

Особенно описать:
- actor types;
- tenant isolation;
- RLS;
- workstation trust boundary;
- ProjectRuntime;
- OpenCode;
- Desired/Applied state;
- pipeline;
- review/eval;
- MCP.
```

## H14.2 — Documentation drift audit

```text
ProjectBeacon: выполнить только H14.2.

Проведи поиск документационных утверждений, противоречащих current code.

Категории:
- old routes;
- old IA;
- old architecture;
- old auth semantics;
- old runtime model;
- old UI states;
- completed roadmap items shown as pending;
- fixed issues shown as open.

Каждое расхождение:
- исправить;
- либо явно пометить historical/archive.

Не удаляй исторические документы без необходимости.
```

---

# Universal Completion Prompt

После каждого hardening block можно отдельным запуском использовать этот verification prompt.

```text
Проведи строгую post-implementation verification текущего hardening block.

Не меняй код.

Проверь:

1. Фактические изменения соответствуют только заданному hardening block.
2. Нет ли unrelated refactor.
3. Все acceptance criteria выполнены.
4. Нет ли новых security regressions.
5. Нет ли state transitions, которые сообщают success без фактического результата.
6. Нет ли silent exception swallowing.
7. Нет ли race condition.
8. Нет ли cross-project/cross-user leakage.
9. Все relevant tests проходят.
10. Build проходит.
11. Для UI — baseline/screenshots проверены.
12. Documentation отражает реальное поведение.

Сформируй результат:

CONFIRMED FIXED
PARTIAL
REGRESSION
NOT VERIFIED
OUT OF SCOPE

Для каждого пункта укажи:
- file;
- relevant code path;
- test;
- конкретное доказательство.

Не исправляй найденные проблемы в этом запуске.
```

# Cold Review Prompt

Перед merge для security/runtime/persistence изменений используй отдельный cold review:

```text
Проведи независимый cold review текущего diff.

Не используй контекст предыдущих обсуждений и не предполагай намерение автора.

Ищи:

1. failure reported as success;
2. state written but repair path cannot see it;
3. wait that cannot complete;
4. resource acquired but not released;
5. race between workers;
6. stale desired/applied state;
7. authorization bypass;
8. tenant/project scope confusion;
9. filesystem boundary bypass;
10. Device/ApiToken/Human actor confusion;
11. cross-platform behavior differences.

Для каждого finding:
- file + line;
- concrete sequence;
- observable result;
- severity.

Не предлагай стилистические улучшения.
Не придумывай баги без достижимой sequence.
```

# Правило выполнения Hardening Roadmap

```text
H0 → H1 → H2 → H3 → H4 → H5 → H6 → H7 → H8 → H9 → H10 → H11 → H12 → H13 → H14
```

Не отдавать агенту сразу весь этот документ как задачу.

Правильный вызов:

```text
Current hardening block: H3.2
Implement only H3.2.
Read current code.
Do not execute H3.3+.
Run targeted tests.
Run build.
Report exact changed files and remaining gaps.
```

После завершения блока:

```text
implementation
→ targeted tests
→ full tests
→ verification prompt
→ cold review when applicable
→ commit
→ next H-block
```

