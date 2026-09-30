# ProjectBeacon — Master Product Roadmap v1

## 0. Назначение документа

Этот документ является **верхнеуровневым execution roadmap** для дальнейшей работы над ProjectBeacon.

Он не заменяет два специализированных документа:

1. `dotnet project docs/ProjectBeacon-code-review-roadmap-v3.md` — backend, security, architecture, runtime, persistence, quality и production readiness.
2. `dotnet project docs/ProjectBeacon-ui-ux-review-roadmap-v1.1.md` — UI/UX, information architecture, design system, interaction patterns, responsive/accessibility и visual regression.

Задача этого документа — отвечать на вопрос:

> **Что делать сейчас, в каком порядке, и в какую секцию какого специализированного roadmap нужно перейти за деталями?**

Агенту не следует искать требования по всему репозиторию. Сначала он определяет текущую фазу этого master roadmap, затем открывает указанные здесь секции backend/UI roadmap и только после этого приступает к реализации.

---

# 1. Source of truth

## 1.1 Master roadmap

Этот документ определяет:

- порядок работ;
- зависимости между UI/UX и backend;
- последовательность фаз;
- точки перехода между фазами;
- какие секции специализированных roadmap необходимо читать.

## 1.2 Backend roadmap

`dotnet project docs/ProjectBeacon-code-review-roadmap-v3.md`

Использовать для:

- security;
- authorization;
- actor model;
- project/workstation boundaries;
- API/handler contracts;
- persistence;
- CI/test infrastructure;
- agent runtime;
- evaluation;
- context compiler;
- production verification.

## 1.3 UI/UX roadmap

`dotnet project docs/ProjectBeacon-ui-ux-review-roadmap-v1.1.md`

Использовать для:

- information architecture;
- screen structure;
- user flows;
- design system;
- shared UI primitives;
- state presentation;
- responsive behavior;
- accessibility;
- localization;
- visual regression.

## 1.4 Правило разрешения конфликтов

Если master roadmap говорит **что и когда делать**, а специализированный roadmap говорит **как и почему**, приоритет такой:

```text
Current code / actual behavior
        ↓
Master roadmap — порядок работ
        ↓
Specialized roadmap — implementation requirements
        ↓
Existing documentation — supporting context
```

При обнаружении противоречия агент не должен молча выбирать один вариант. Он должен зафиксировать конфликт в рабочем результате и придерживаться master roadmap до принятия нового решения.

---

# 2. Ключевая продуктовая модель

ProjectBeacon имеет несколько разных scope. Это принципиально важно и для UI, и для backend.

```text
User
│
├── Account / Security
│
├── Agents & Models                         ← USER SCOPE
│   ├── Agent configuration
│   ├── Models
│   ├── Task kinds / templates
│   └── MCP defaults
│
├── Workstations / Daemons                  ← USER-OWNED RUNTIME SCOPE
│   ├── capabilities
│   ├── installed tools
│   ├── local model runtimes
│   └── runtime state
│
└── Projects
    ├── Project configuration                ← PROJECT SCOPE
    ├── Tasks
    ├── Pipeline
    ├── Context
    ├── Decisions
    ├── Roadmap
    ├── Reports
    └── Project ↔ Workstation/runtime binding
```

### Важное решение по Agents

Agents и их базовая конфигурация являются **user-level**, а не project-level.

Один пользователь может иметь несколько проектов и использовать одну и ту же конфигурацию агентов/моделей через свой локальный Beacon daemon/workstation.

Поэтому:

- Agents **не нужно возвращать в top-level navigation** как project feature;
- `/agents` может оставаться совместимым shortcut на соответствующий раздел Settings;
- Settings должен явно разделять **глобальную конфигурацию пользователя** и **операции над конкретным runtime/workstation/project**.

Это решение является частью master product model и должно считаться заданным при выполнении UI/backend roadmap.

---

# 3. Основной принцип выполнения

Не выполнять две крайности:

### Неправильный вариант A

```text
Сначала полностью backend
→ потом придумать UI
```

### Неправильный вариант B

```text
Сначала полностью переписать UI
→ потом выяснять, какой backend нужен
```

### Целевой вариант

```text
UI/UX intent
      ↓
Scope / domain clarification
      ↓
Backend contract + security
      ↓
UI implementation
      ↓
Backend implementation/refactor
      ↓
Integration
      ↓
Visual + functional verification
```

UI/UX используется как средство уточнения продукта, но security-critical backend не откладывается до завершения визуальной работы.

---

# 4. Phase 0 — Baseline и фиксация модели

## Цель

Создать стабильную точку отсчёта перед большими изменениями.

## Читать

**UI/UX:**

- UI roadmap §21 `Phase 0 — UI baseline`
- UI roadmap §22 `Acceptance criteria`
- UI roadmap §26 `Финальный приоритет`

**Backend:**

- Backend roadmap §0 `Итог ревью`
- Backend roadmap §19 `Production verification matrix`
- Backend roadmap §23 `Definition of Done`

## Сделать

- зафиксировать текущие screenshots desktop/tablet/mobile;
- зафиксировать routes и их назначение;
- зафиксировать текущую scope model;
- подтвердить user-level Agents;
- составить список известных backend P0/P1 дефектов;
- определить authoritative версии двух специализированных документов;
- не начинать массовый visual refactor до завершения baseline.

## Gate

Переход к Phase 1 только после того, как:

- scope User / Workstation / Project / Task понятен;
- список P0 backend security issues известен;
- screenshots baseline сохранён;
- агент знает, какие два specialized roadmap являются source of truth.

---

# 5. Phase 1 — Security foundation

## Цель

Закрыть trust-boundary ошибки до большого изменения пользовательского поведения.

## Читать

**Backend:**

- §1.1 `Bootstrap должен fail closed`
- §1.2 `Logout должен быть авторизованным`
- §1.3 `API token должен иметь полноценную identity`
- §1.4 `Нельзя доверять project scope из route/header при API token auth`
- §1.5 `Не доверять CreatedByUserId, ActorId, UserId в request body`

## Сделать

- bootstrap fail-closed;
- authenticated logout;
- actor identity из trusted auth context;
- корректный API token identity;
- project scope для token;
- negative auth tests;
- убрать client-supplied actor identity там, где сервер уже знает actor.

## UI involvement

Только необходимые изменения UX:

- корректно отражать authentication/session errors;
- не строить новый UI вокруг ещё невалидной identity model.

**UI reference:** UI roadmap §10 `Loading, empty, error и success states`.

## Gate

До перехода дальше не должно оставаться известных P0 security bypass'ов.

---

# 6. Phase 2 — Authorization и scope model

## Цель

Сделать модель полномочий такой же явной, как scope model UI.

## Читать

**Backend:**

- §2.1 `Ввести Actor → Capability/Role → Resource → Action`
- §2.2 `Закрыть project member operations`
- §2.3 `API token CRUD тоже должен проверять caller`

**UI:**

- §2.1 `Перестроить глобальную навигацию по user goals`
- §2.2 `Project context должен быть визуально постоянным`
- §2.3 `Breadcrumbs должны отражать реальную глубину контекста`

## Сделать

Зафиксировать backend scopes:

```text
User scope
Workstation scope
Project scope
Task/Pipeline scope
```

и соответствующие действия:

```text
read
create
update
delete
execute
administer
```

UI при этом должен визуально давать пользователю тот же mental model.

## Gate

Для каждого major screen можно ответить:

- кто actor;
- какой scope;
- какой resource;
- какие actions допустимы.

---

# 7. Phase 3 — Information Architecture

## Цель

До глубокого UI implementation определить окончательную структуру продукта.

## Читать

**UI:**

- §1.1 `Settings как перегруженный super-screen`
- §1.2 `Agents должен быть самостоятельным UX-context внутри Settings`
- §1.3 `Dashboard должен стать overview, а не administration hub`
- §1.4 `Chat: определить одну primary interaction model`
- §2.1 `Перестроить глобальную навигацию по user goals`
- §2.2 `Project context должен быть визуально постоянным`
- §20 `Рекомендуемая структура UI после refactor`
- §21 `Phase 1 — Information architecture`

**Backend:**

- §9 `Application architecture`
- §10 `Agent Runtime abstraction`
- §17 `MCP trust model`

## Целевые решения

### Settings

```text
Settings
├── Account & Security
├── Agents & Models
├── Workstations
└── Connections
```

### Project

```text
Project
├── Dashboard
├── Board
├── Backlog
├── Roadmap
├── Context
├── Decisions
├── Reports
└── Chat
```

### Agents & Models

```text
Agents & Models
├── Agent configuration       ← user-level
├── Models                    ← user-level
├── Task kinds / templates    ← user-level
├── MCP                       ← user-level defaults
└── Runtime / Apply           ← contextual
```

## Важно

Не удалять `/agents` автоматически. Оставить backward-compatible shortcut на Settings → Agents & Models, пока отдельный route не перестанет быть нужен.

## Gate

У каждого major route есть:

- owner scope;
- primary job-to-be-done;
- primary action;
- expected backend data contract.

---

# 8. Phase 4 — Design System + Backend contract alignment

## Цель

Сделать UI достаточно структурированным, чтобы backend было удобно развивать по ясным contracts.

## Читать

**UI:**

- §3.1 `Sidebar geometry сейчас не соответствует спецификации`
- §3.2 `Board card geometry также расходится со спецификацией`
- §3.3 `Два design-token источника`
- §3.4 `Light palette сейчас выглядит как dead design system`
- §4.1 `Слишком много spacing-источников`
- §4.2 `Shared primitives`
- §21 `Phase 2 — Design system consolidation`
- §21 `Phase 3 — Shared state/feedback components`

**Backend:**

- §5 `Desired state / applied state`
- §15 `Standard API error contract`
- §16 `Package/version governance`

## Сделать

### UI

- единый source of truth для design tokens;
- shared primitives;
- Loading/Empty/Error/Saving/Queued/Running states;
- единый confirmation pattern;
- единые status chips;
- устранение geometry drift.

### Backend

- standard error/ProblemDetails contract;
- desired/applied state там, где UI показывает операции над runtime;
- version alignment.

## Gate

UI state и backend state должны иметь однозначное отображение:

```text
Queued
Running
Succeeded
Failed
Cancelled
Offline
Degraded
```

UI не должен придумывать собственные статусы, не существующие в backend.

---

# 9. Phase 5 — Workstation security boundary + Workstation UX

## Цель

Свести local filesystem/process operations и их UI в одну модель runtime boundary.

## Читать

**Backend:**

- §3.1 `Один sandbox contract для всех локальных операций`
- §3.2 `Один root boundary для Chat / Eval / OpenCode / File tools`
- §3.3 `Project-rooted commands должны ссылаться на runtime binding`

**UI:**

- §7.1 `Не показывать raw infrastructure model как основной UX`
- §7.2 `Folder selection уже лучше raw path и должен стать стандартом`
- §7.3 `Queue-based workstation operations должны показывать lifecycle`
- §6.2 `Runtime health должен быть observable state`
- §21 `Phase 5 — Agents + Workstations`

## Сделать

Backend:

- ProjectRuntime;
- unified root validation;
- project-relative path model;
- Chat/Eval/OpenCode sandbox;
- command scope enforcement.

UI:

- human-friendly runtime status;
- folder picker как основной способ выбора проекта;
- Advanced/Diagnostics для raw paths;
- lifecycle queued/running/succeeded/failed;
- explicit distinction между:
  - user configuration;
  - workstation runtime;
  - project binding.

## Gate

Пользователь может понять, что происходит на локальном ПК, не зная внутренней архитектуры daemon/OpenCode/llama runtime.

---

# 10. Phase 6 — CI, persistence и delivery correctness

## Цель

Получить стабильную техническую базу перед более глубоким refactor.

## Читать

**Backend:**

- §4.1 `Сначала вернуть зелёный CI`
- §4.2 `Исправить API token tests...`
- §4.3 `RLS tests должны быть полностью повторяемыми`
- §4.4 `Реальный llama-server вынести из обычного CI`

**UI:**

- §22 `Acceptance criteria`
- §21 `Phase 10 — Visual regression`

## Сделать

- зелёный ordinary CI;
- deterministic RLS cleanup;
- fake process tests;
- GPU/real-process integration lane;
- API token lifecycle tests;
- migrations verification.

Параллельно подготовить screenshot regression harness для UI.

## Gate

CI зелёный, а visual regression имеет зафиксированный baseline.

---

# 11. Phase 7 — Application architecture + Agent Runtime

## Цель

Устранить инфраструктурные зависимости из domain/application contracts и одновременно сделать agent runtime независимым от OpenCode.

## Читать

**Backend:**

- §9 `Application architecture`
- §10 `Agent Runtime abstraction`
- §11 `Pipeline refactor`
- §14 `Process lifecycle audit`

**UI:**

- §6.1 `Agents & Models...`
- §6.2 `Runtime health...`
- §6.3 `Model configuration должна быть progressive disclosure`
- §5.3 `Task Detail необходимо декомпозировать`

## Сделать

Backend:

```text
IAgentRuntime
 ├── CreateSession
 ├── SendPrompt
 ├── StreamParts
 ├── Abort
 ├── ReadUsage
 └── Dispose
```

и OpenCode adapter поверх него.

UI:

- не использовать OpenCode terminology там, где пользователю нужен generic agent concept;
- технические runtime details показывать только там, где они помогают диагностике.

## Gate

UI не зависит от конкретного runtime provider как от основной mental model.

---

# 12. Phase 8 — Board + Task Detail + Pipeline

## Цель

Сделать core work-management flow главным UX эталоном продукта.

## Читать

**UI:**

- §5.1 `Board — сильная сторона, которую нужно сохранить`
- §5.2 `Kanban mobile mode требует отдельного UX`
- §5.3 `Task Detail необходимо декомпозировать`
- §5.4 `Action hierarchy на Task Detail нужно усилить`
- §21 `Phase 4 — Board + Task Detail`

**Backend:**

- §6 `Review proof должен быть объективным`
- §7 `Evaluation semantics`
- §11 `Pipeline refactor`
- §12 `Subtask tool/path restrictions`

## Сделать

Task screen разделить концептуально:

```text
Task header
Overview
Work
Pipeline
Context
Discussion
```

Pipeline actions разделить на:

```text
Primary
Secondary
Danger / override
```

Backend pipeline:

- split handlers;
- stronger ReviewRun;
- actual subtask restrictions;
- real review/eval outcomes.

## Gate

Task Detail больше не является длинным монолитным scroll без явной hierarchy.

---

# 13. Phase 9 — Agents & Models UX

## Цель

Сделать user-level agent configuration понятной и не смешивать её с project administration.

## Читать

**UI:**

- §1.2 `Agents должен быть самостоятельным UX-context внутри Settings`
- §6.1 `Agents & Models следует разделить по mental model...`
- §6.2 `Runtime health должен быть observable state...`
- §6.3 `Model configuration должна быть progressive disclosure`
- §21 `Phase 5 — Agents + Workstations`

**Backend:**

- §10 `Agent Runtime abstraction`
- §13 `VRAM telemetry`
- §17 `MCP trust model`
- §5 `Desired state / applied state`

## Целевой UX

```text
Settings
└── Agents & Models
    ├── Overview
    ├── Models
    ├── Agent templates
    ├── Task kinds
    ├── MCP
    └── Runtime / Diagnostics
```

При этом:

- configuration сохраняется на user level;
- apply/reload/unload относится к конкретному runtime;
- проект не владеет копией user agent configuration.

## Gate

Пользователь понимает разницу между:

```text
My agent setup
My workstation
Current project
```

---

# 14. Phase 10 — Chat и agent interaction

## Цель

Превратить Chat в понятный agent workspace, а не generic text chat.

## Читать

**UI:**

- §1.4 `Chat: определить одну primary interaction model`
- §8.1 `Message model слишком примитивен для agent UI`
- §8.2 `Tool calls должны быть структурированными`
- §8.3 `Streaming state`
- §21 `Phase 6 — Chat`

**Backend:**

- §3.2 `Chat/Eval/OpenCode sandboxing`
- §10 `Agent Runtime abstraction`
- §12 `Subtask tool/path restrictions`
- §14 `Process lifecycle audit`
- §17 `MCP trust model`

## Сделать

- единая primary/full chat model;
- structured tool-call presentation;
- streaming lifecycle;
- abort/retry/error states;
- runtime/tool permissions reflected in UI;
- mobile full-screen/bottom-sheet behavior.

## Gate

Chat отображает реальный backend lifecycle и permissions, не маскируя их под обычные сообщения.

---

# 15. Phase 11 — Context / Decisions / Reports / Roadmap

## Цель

Перевести knowledge layer из generic CRUD в domain-specific workspace.

## Читать

**UI:**

- §15 `Context / Decisions / Reports`
- §16 `Roadmap UX`
- §21 `Phase 7 — Context / Decisions / Reports / Roadmap`

**Backend:**

- §8 `Context compiler`
- §7 `Evaluation semantics`
- §6 `Review proof должен быть объективным`

## Сделать

### Context

- master/detail;
- compile/cache state;
- token budget;
- changed scope/tree capsule enrichment.

### Decisions

- decision detail;
- explicit lifecycle;
- relation/supersede visualization.

### Reports

- metadata;
- report detail;
- context/eval results.

### Roadmap

- milestone overview;
- task preview;
- detail/edit states.

## Gate

Каждый knowledge screen имеет собственную domain-specific information hierarchy.

---

# 16. Phase 12 — Evaluation, Review и quality feedback loop

## Цель

Связать backend quality model с тем, что пользователь реально видит.

## Читать

**Backend:**

- §6 `Review proof должен быть объективным`
- §7 `Evaluation semantics`
- §8 `Context compiler`
- §19 `Production verification matrix`

**UI:**

- §10 `Loading, empty, error и success states`
- §15 `Context / Decisions / Reports`
- §19 `UX state model`
- §22 `Acceptance criteria`

## Сделать

Backend:

```text
Agent execution
   ↓
Artifact
   ↓
Build / tests / checks
   ↓
Reviewer
   ↓
Pass / Fail
```

UI должен различать:

```text
Generated
Running
Verified
Reviewed
Passed
Failed
```

Нельзя показывать `Done` там, где backend фактически имеет только `generation completed`.

## Gate

UI status semantics отражают реальные backend quality semantics.

---

# 17. Phase 13 — Responsive, Accessibility, Localization

## Читать

**UI:**

- §11 `Responsive UX`
- §12 `Accessibility`
- §13 `Localization`
- §21 `Phase 8 — Responsive + Accessibility`
- §21 `Phase 9 — Localization`

## Сделать

- desktop/tablet/mobile regression;
- keyboard-only;
- screen-reader;
- focus management;
- reduced motion;
- touch target audit;
- hard-coded strings audit;
- localization diff.

## Gate

Responsive/accessibility/localization не являются page-by-page исключениями, а работают системно.

---

# 18. Phase 14 — Runtime observability и operational polish

## Читать

**Backend:**

- §13 `VRAM telemetry`
- §14 `Process lifecycle audit`
- §18 `Worker`
- §19 `Production verification matrix`

**UI:**

- §6.2 `Runtime health`
- §7.3 `Queue-based workstation operations`
- §17 `Visual polish`
- §19 `UX state model`

## Сделать

Backend:

- actual/estimated VRAM separation;
- process cancellation/timeouts/disposal;
- Worker contract;
- production verification.

UI:

- runtime health;
- queue progress;
- actionable diagnostics;
- no misleading "healthy" state based only on connectivity.

---

# 19. Phase 15 — Final visual system consolidation

## Читать

**UI:**

- §3 `Design system`
- §4 `Component language`
- §17 `Visual polish`
- §18 `Componentization`
- §20 `Рекомендуемая структура UI после refactor`
- §22 `Acceptance criteria`
- §24 `Рекомендуемый порядок спринтов`

**Backend:**

- §15 `Standard API error contract`
- §23 `Definition of Done`

## Сделать

- final token cleanup;
- obsolete CSS removal;
- shared primitive consolidation;
- visual regression;
- remove temporary migration styles;
- verify all state presentations.

## Gate

Продукт выглядит как единая система, а не как набор независимо переписанных страниц.

---

# 20. Phase 16 — Documentation consolidation

## Читать

**Backend:**

- §20 `Документация — что устарело и что делать`
- §21 `Создать новые authoritative документы`

**UI:**

- §23 `Документация: что устарело и что делать`

## Сделать

После существенного refactor синхронизировать:

- `README.md`
- `AGENTS.md`
- authoritative design/architecture docs;
- backend roadmap;
- UI/UX specification;
- agent prompt template.

Архивировать старые execution plans, если они больше не являются источником актуальных решений.

## Gate

Новый агент, получив только актуальные docs, должен понимать:

- architecture;
- scope model;
- UI structure;
- execution order;
- security boundaries;
- runtime model.

---

# 21. Итоговый порядок спринтов

Ниже execution sequence. Это не означает, что внутри каждого спринта работа выполняется строго линейно.

## Sprint 1 — Baseline + Security

**Читать:**

- Backend §0, §1.1–1.5, §19
- UI §21 Phase 0, §22

**Результат:** baseline + закрытые P0 security issues.

---

## Sprint 2 — Authorization + Scope + IA

**Читать:**

- Backend §2.1–2.3
- UI §1.1–1.4, §2.1–2.3, §20, §21 Phase 1

**Результат:** единая mental model User / Workstation / Project.

---

## Sprint 3 — Workstation boundary + Design system

**Читать:**

- Backend §3.1–3.3
- UI §3.1–3.4, §4.1–4.2, §7.1–7.3

**Результат:** безопасный runtime boundary + единая visual foundation.

---

## Sprint 4 — CI + State model

**Читать:**

- Backend §4.1–4.4, §5, §15, §16
- UI §10, §19, §21 Phase 3

**Результат:** стабильная delivery/test base и единый state presentation.

---

## Sprint 5 — Application architecture + Agent Runtime

**Читать:**

- Backend §9–§11, §14
- UI §5.3, §6.1–6.3

**Результат:** backend/runtime decoupling + понятный task/agent architecture.

---

## Sprint 6 — Board / Task / Pipeline

**Читать:**

- Backend §6–§7, §11–§12
- UI §5.1–5.4, §21 Phase 4

**Результат:** core development workflow.

---

## Sprint 7 — Agents / Workstations / Chat

**Читать:**

- Backend §3, §10, §13, §17
- UI §6, §7, §8, §21 Phase 5–6

**Результат:** complete local-agent control plane.

---

## Sprint 8 — Knowledge + Quality + Responsive

**Читать:**

- Backend §6–§8, §19
- UI §10–§16, §21 Phase 7–9

**Результат:** context/evaluation/review + responsive/accessibility/localization.

---

## Sprint 9 — Operational readiness + visual regression

**Читать:**

- Backend §13–§19, §23
- UI §17–§22, §24

**Результат:** production verification + stable visual system.

---

## Sprint 10 — Documentation

**Читать:**

- Backend §20–§21
- UI §23

**Результат:** documentation reflects actual product rather than historical implementation state.

---

# 22. Execution protocol для coding agent

Перед началом каждой задачи агент должен пройти следующий алгоритм.

## Step 1 — Определи текущую фазу

Ответь себе:

```text
Какой Phase/Sprint master roadmap сейчас выполняется?
```

Не перескакивай на более позднюю фазу без явной причины.

## Step 2 — Найди primary source

Если задача преимущественно backend:

```text
Read Backend roadmap section X
```

Если задача преимущественно UI:

```text
Read UI/UX roadmap section Y
```

Если задача cross-cutting:

```text
Read both referenced sections
```

## Step 3 — Проверь scope

Перед изменением кода явно установи:

```text
Actor:
Scope:
Resource:
Action:
```

## Step 4 — Проверь существующую реализацию

Не предполагай, что roadmap описывает текущее состояние точно. Проверь code path, который собираешься менять.

## Step 5 — Выполняй минимальный coherent slice

Не смешивай в одном изменении:

- unrelated UI polish;
- unrelated backend refactor;
- documentation cleanup;
- архитектурные изменения, не требующиеся текущей фазой.

## Step 6 — Verify

Проверка должна соответствовать типу изменения:

```text
Backend:
  tests / integration / auth / persistence

UI:
  build / interaction / screenshot / responsive

Cross-cutting:
  backend contract + UI behavior
```

## Step 7 — Обнови execution state

После завершения задачи зафиксируй:

- какие пункты master roadmap выполнены;
- какие пункты specialised roadmap выполнены;
- какие gaps обнаружены;
- какая следующая recommended section.

---

# 23. Быстрый routing table для агента

| Если задача про... | Сначала читать Backend | Сначала читать UI/UX |
|---|---|---|
| Login / bootstrap / logout | §1 | §10 |
| API token / permissions | §1–2 | §19–20 |
| User / project scope | §2 | §2 |
| Filesystem / daemon / local path | §3 | §7 |
| Runtime state | §5, §10, §13–14 | §6–7, §19 |
| Models | §10, §13 | §6 |
| Agents | §10, §17 | §1.2, §6 |
| MCP | §17 | §6.1 |
| Tasks / Board | §11–12 | §5 |
| Reviews | §6–7 | §5, §15 |
| Eval | §7–8 | §15, §19 |
| Context compiler | §8 | §15 |
| Chat | §3.2, §10, §14, §17 | §1.4, §8 |
| Workstation | §3, §5, §13–14 | §7 |
| Dashboard | §9 | §1.3, §14 |
| Settings | §2, §9 | §1.1–1.2 |
| Error states | §15 | §10 |
| Responsive | §19 | §11–12 |
| Localization | — | §13 |
| Visual polish | §15 | §3–4, §17–18 |
| CI / tests | §4 | §22 |
| Production | §19 | §22 |
| Documentation | §20–21 | §23 |

---

# 24. Definition of Done для master roadmap

Master roadmap считается выполненным, когда одновременно выполнены backend и UI/UX условия.

## Product model

- [ ] User / Workstation / Project / Task scopes согласованы.
- [ ] Agents закреплены как user-level configuration.
- [ ] Runtime/project operations явно отделены от global agent configuration.

## Security

- [ ] Backend P0 security issues закрыты.
- [ ] Authorization централизована.
- [ ] Workstation filesystem/process boundary единый.

## Backend

- [ ] Application architecture выровнена.
- [ ] Agent runtime abstraction существует.
- [ ] Desired/applied state существует там, где нужен reconciliation.
- [ ] Review/eval semantics проверяют реальный outcome.
- [ ] CI стабилен.
- [ ] Production verification matrix пройдена.

## UI/UX

- [ ] Information architecture согласована.
- [ ] Settings структурирован.
- [ ] Agents & Models явно user-level.
- [ ] Dashboard является overview.
- [ ] Task/Board имеют понятную hierarchy.
- [ ] Chat имеет единую interaction model.
- [ ] Design system имеет один source of truth.
- [ ] Loading/empty/error/queued/running states унифицированы.
- [ ] Responsive/accessibility/localization regression пройдены.
- [ ] Visual regression matrix зафиксирована и проверена.

## Documentation

- [ ] Этот master roadmap остаётся актуальным execution entrypoint.
- [ ] Backend roadmap актуален.
- [ ] UI/UX roadmap актуален.
- [ ] README/AGENTS не содержат старую архитектуру.
- [ ] Historical plans архивированы.

---

# 25. Правило для новых задач

Любая новая задача должна начинаться с добавления или определения одной строки вида:

```text
Master phase: Phase N
Backend reference: §X.Y
UI reference: §A.B
Scope: User / Workstation / Project / Task
```

Если задача не имеет понятной ссылки на master phase и scope, её нельзя считать достаточно сформулированной для агентной реализации.
