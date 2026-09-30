# Phase 3 — Information Architecture: План

Дата: 2026-09-30
Статус: draft
Основание: master roadmap §7, UI roadmap §1/§2/§20/§21, baseline (routes-inventory, scope-model)

---

## 1. Целевая структура навигации

### 1.1 Drawer (MainLayout)

Текущая структура (4 группы):

```text
Dashboard
Queue: Board, Backlog, Roadmap
Memory: Context, Decisions, Reports
Runtime: Chat, Settings
```

Целевая структура (5 групп, по user goals):

```text
WORK
├── Dashboard        /dashboard
├── Board            /board
├── Backlog          /backlog
└── Roadmap          /roadmap

KNOWLEDGE
├── Context          /context
├── Decisions        /decisions
└── Reports          /reports

AGENTS
├── Chat             /chat
├── Agents & Models  /settings/agents
└── Workstations     /settings/workstations

PROJECT
└── Project Settings /project/settings

ACCOUNT
└── Settings         /settings
```

### 1.2 Обоснование групп

| Группа | JTBD пользователя | Scope |
|---|---|---|
| WORK | «Что нужно делать и что в работе?» | Project |
| KNOWLEDGE | «Что мы решили и почему?» | Project |
| AGENTS | «Кто выполняет работу и как я с ним общаюсь?» | User + Project (chat) |
| PROJECT | «Как настроен именно этот проект?» | Project |
| ACCOUNT | «Как я вхожу, кто я, какие мои глобальные настройки?» | User |

### 1.3 Изменения относительно текущего

| Элемент | Сейчас | Цель |
|---|---|---|
| Label «Queue» | `NavQueue` | `WORK` |
| Label «Memory» | `NavMemory` | `KNOWLEDGE` |
| Label «Runtime» | `NavRuntime` | `AGENTS` |
| Chat в nav | `/chat` в группе Runtime | `/chat` в группе AGENTS |
| Settings в nav | `/settings` (один пункт) | `/settings` (Account) + `/settings/agents` + `/settings/workstations` |
| Project Settings | отсутствует (ProjectManage в Dashboard) | `/project/settings` отдельный пункт |
| `/agents` redirect | → `/settings` | → `/settings/agents` |

---

## 2. Полная таблица routes

### 2.1 Anonymous (AuthLayout)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/` | — | Понять продукт до входа | Read landing → login/register | `GET /v1/version` |
| `/login` | — | Войти | Submit credentials | `POST /v1/auth/login` |
| `/register` | — | Создать аккаунт (invite-gated) | Submit email+password+invite | `POST /v1/auth/register` |
| `/forgot` | — | Восстановить доступ | Request reset link | `POST /v1/auth/forgot-password` |
| `/reset` | — | Сменить пароль по токену | Submit new password | `POST /v1/auth/reset-password` |
| `/recover` | — | Break-glass admin | Submit bootstrap token | `POST /v1/auth/recover` |
| `/invite` | — | Принять приглашение | Submit invite token | `POST /v1/auth/invite/accept` |
| `/bootstrap` | — | Первый запуск | Create admin | `POST /v1/auth/bootstrap` |

### 2.2 WORK (Project scope)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/dashboard` | Project | Быстрый обзор: что в работе, что заблокировано, статистика | View stats → navigate to task | `GET /v1/projects/{id}/tasks?status=...`, `GET /v1/projects/{id}/milestones` |
| `/board` | Project | Управление задачами в статусе (Kanban) | Drag task between columns | `GET /v1/projects/{id}/tasks`, `PATCH /v1/tasks/{id}` |
| `/backlog` | Project | Просмотр/упорядочивание необработанных задач | Reorder, promote to board | `GET /v1/projects/{id}/tasks?status=Todo` |
| `/roadmap` | Project | Просмотр и управление milestones | Create/close milestone | `GET/POST /v1/projects/{id}/milestones` |
| `/task/{id}` | Project | Глубокая работа над одной задачей (pipeline, steps, comments) | Execute pipeline steps, add comments | `GET /v1/tasks/{id}`, `PATCH /v1/tasks/{id}`, pipeline endpoints |
| `/projects/new` | — | Создать новый проект | Submit name + config | `POST /v1/projects` |

### 2.3 KNOWLEDGE (Project scope)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/context` | Project | Поддерживать living brief проекта (sections, constraints) | Edit nodes, compile, import/export | `GET/POST /v1/projects/{id}/context`, `/context/compile`, `/context/import`, `/context/export` |
| `/decisions` | Project | Фиксировать архитектурные решения и их статус | Accept/deprecate/supersede | `GET/POST /v1/projects/{id}/decisions`, `/decisions/{id}/accept|deprecate|supersede` |
| `/reports` | Project | Понять состояние проекта в момент времени | Generate/view board snapshot, context cost | `POST /v1/projects/{id}/reports`, `GET /v1/projects/{id}/reports`, `GET /v1/reports/context-cost/{taskId}` |

### 2.4 AGENTS (User scope + Project chat)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/chat` | Project | Интерактивная работа с агентом в контексте проекта | Send prompt, view streaming, tool calls | `POST /v1/projects/{id}/chat/sessions/{sid}/prompt` (SSE), `GET /v1/projects/{id}/chat/sessions` |
| `/settings/agents` | User | Настроить модели, task kinds, MCP defaults, runtime | CRUD model backends, bind roles, edit task kinds | `GET/POST /v1/projects/{id}/models`, `/models/bind`, user-level model registry |
| `/settings/workstations` | User | Управлять своими устройствами (enroll, probe, paths, install) | Enroll device, save workstation paths, install tools | `GET/POST /v1/devices`, `DELETE /v1/devices/{id}`, `POST /v1/devices/{id}/commands`, `GET /v1/commands/{id}` |

### 2.5 PROJECT (Project scope)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/project/settings` | Project | Настроить проект: members, labels, API tokens, runtime binding | Add member, create token, attach runtime | `POST/DELETE /v1/projects/{id}/members`, `POST/DELETE /v1/projects/{id}/tokens`, `GET/POST /v1/projects/{id}/runtimes`, `GET/POST /v1/projects/{id}/labels` |

### 2.6 ACCOUNT (User scope)

| Route | Owner scope | JTBD | Primary action | Backend contract |
|---|---|---|---|---|
| `/settings` | User | Управлять аккаунтом: пароль, chat model, TOTP | Change password, bind chat model | `ChangePasswordHandler`, `GetChatModel/SetChatModel`, `GetTotpStatus/BeginTotp/ConfirmTotp/DisableTotp` |
| `/settings/agents` | User | (см. AGENTS выше) | — | — |
| `/settings/workstations` | User | (см. AGENTS выше) | — | — |
| `/settings/connections` | User | Настроить OpenCode connections для локального daemon | CRUD OpenCode connections | `GET /v1/devices/me/opencode-connections` (device-token), user-level connection registry |

### 2.7 Compat / Redirect

| Route | Переход | Причина |
|---|---|---|
| `/agents` | → `/settings/agents` | Backward-compatible shortcut (master roadmap §7: «не удалять автоматически») |

---

## 3. Decomposition Settings

### 3.1 Текущее состояние (Settings.razor, 516 строк)

```text
/settings (один route, один компонент):
├── Account: password change, chat model select
├── OpenCodeConnections (embedded component)
├── Danger Zone: TOTP enable/disable
├── Devices: enroll, list, revoke
├── Workstation: paths, probe, install (per device)
└── <Agents /> (embedded, 564 строки)
```

### 3.2 Целевая структура

| Route | Файл | Содержимое | Переезжает из |
|---|---|---|---|
| `/settings` | `Settings.razor` (обрывается до ~120 строк) | Password change, chat model, TOTP | Текущие секции Account + Danger Zone |
| `/settings/agents` | `Agents.razor` (получает `@page`) | Models, Task Kinds, MCP, Runtime/Apply | Текущий embedded `<Agents />` |
| `/settings/workstations` | `Workstations.razor` (новый) | Device list, enroll, revoke, workstation paths, probe, install | Текущие секции Devices + Workstation |
| `/settings/connections` | `OpenCodeConnections.razor` (получает `@page`) | OpenCode connection CRUD | Текущий embedded `<OpenCodeConnections />` |

### 3.3 Принципы

- Каждый sub-route — отдельный `.razor` с `@page` directive, `@layout MainLayout`, `@attribute [Authorize]`.
- Общий `SettingsLayout` (опционально, Phase 4): sidebar внутри Settings с подпунктами. В Phase 3 достаточно flat routes — навигация через drawer + breadcrumbs.
- `Settings.razor` сохраняет `@page "/settings"` — это default-цель drawer-пункта «Settings».
- `Agents.razor` получает `@page "/settings/agents"` и перестаёт быть embedded-компонентом.
- `OpenCodeConnections.razor` получает `@page "/settings/connections"`.
- `Workstations.razor` — новый файл, код переносится из `Settings.razor` (devices + workstation sections).

### 3.4 Что остаётся в `/settings` (Account)

- Смена пароля (ChangePasswordHandler)
- Chat model select (GetChatModel/SetChatModel/NudgeModels)
- TOTP (GetTotpStatus/BeginTotp/ConfirmTotp/DisableTotp)
- Danger Zone заголовок + TOTP-блок

Это ~120 строк — приемлемый размер.

---

## 4. Dashboard decomposition

### 4.1 Текущее состояние

`Dashboard.razor` (298 строк) содержит:
- Статистика (counters: open tasks, in progress, done)
- `<ProjectManage />` (embedded, 377 строк): folder/runtime, members, invites, labels
- MudPaper карточки-ссылки на /board, /roadmap, /settings
- Recent tasks list

### 4.2 Целевое состояние

`Dashboard.razor` (цель: ~150 строк):
- Статистика (counters)
- Recent tasks list (top 5)
- Быстрые ссылки: Board, Roadmap, Chat
- **НЕ содержит** ProjectManage

`/project/settings` (новый route):
- ProjectManage.razor переезжает сюда как основная страница
- Файл: `ProjectBeacon.Web/Features/Projects/ProjectSettings.razor` (новый)
- Содержимое = текущий ProjectManage.razor + `@page "/project/settings"`

### 4.3 Обоснование

UI roadmap P0.1: «Dashboard должен стать overview, а не administration hub».
ProjectManage — это administration (members, tokens, labels, runtime), не overview.
Dashboard отвечает на вопрос «что происходит?», Project Settings — «как настроен проект?».

---

## 5. Chat: единая primary interaction model

### 5.1 Текущее состояние

- `/chat` (Chat.razor, 263 строки) — полноценная страница: список sessions, prompt, streaming, tool calls
- `ChatDock.razor` (196 строк) — FAB + slide-out panel в MainLayout, быстрый доступ

### 5.2 Решение

**Primary model: `/chat` (full page).** ChatDock — secondary quick-access (FAB остаётся).

| Элемент | Роль |
|---|---|
| `/chat` | Полная работа с агентом: sessions, history, streaming, tool calls, abort |
| ChatDock (FAB) | Быстрый одно-промпт в текущий active session; кнопка «Open full chat» → `/chat` |

Обоснование: UI roadmap §1.4 — «определить одну primary interaction model». Full page — primary, потому что содержит все состояния (sessions list, streaming, tool calls). ChatDock — convenience shortcut, не конкурирует.

В drawer: `/chat` в группе AGENTS.

---

## 6. Project Switcher и context

### 6.1 Текущее поведение

- `ProjectSwitcher.razor` в drawer (выше навигации)
- Вызов `ListMyProjectsHandler`
- Выбранный проект сохраняется в claim `"project_id"`
- При смене — `NavigationManager.NavigateTo` текущего URI (reload)

### 6.2 Целевое поведение

Без изменений в Phase 3. Project Switcher остаётся:
- Визуально постоянным (верх drawer)
- Source of truth для project-scoped routes
- При отсутствии активного проекта — project-scoped routes показывают empty state с CTA «Create project»

### 6.3 Breadcrumbs

Текущая логика `RebuildCrumbs()` в MainLayout. Изменения:

| Route | Breadcrumbs |
|---|---|
| `/dashboard` | ProjectBeacon › Dashboard |
| `/board` | ProjectBeacon › Board |
| `/backlog` | ProjectBeacon › Backlog |
| `/roadmap` | ProjectBeacon › Roadmap |
| `/task/{id}` | Board › Task |
| `/context` | ProjectBeacon › Context |
| `/decisions` | ProjectBeacon › Decisions |
| `/reports` | ProjectBeacon › Reports |
| `/chat` | ProjectBeacon › Chat |
| `/settings` | ProjectBeacon › Settings › Account |
| `/settings/agents` | ProjectBeacon › Settings › Agents & Models |
| `/settings/workstations` | ProjectBeacon › Settings › Workstations |
| `/settings/connections` | ProjectBeacon › Settings › Connections |
| `/project/settings` | ProjectBeacon › Project Settings |
| `/agents` | → redirect, breadcrumbs не нужны |

---

## 7. Deep-links

### 7.1 Dashboard → Agents & Models

UI roadmap Phase 1: «Сделать deep-link непосредственно в Agents & Models section, не только в Settings root».

- Card/ссылка на Dashboard: `href="/settings/agents"`, label «Agents & Models»
- Текущая ссылка на Dashboard ведёт в `/settings` (Agents embedded) — меняется на `/settings/agents`

### 7.2 Drawer → Settings sub-routes

- Пункт «Settings» в drawer → `/settings` (Account)
- Пункт «Agents & Models» в группе AGENTS → `/settings/agents`
- Пункт «Workstations» в группе AGENTS → `/settings/workstations`
- Пункт «Connections» — НЕ в drawer (слишком глубокий), доступен через `/settings` → link или `/settings/agents` → link

Альтернатива: в группе ACCOUNT в drawer:
```text
ACCOUNT
├── Settings (Account)     /settings
└── Connections            /settings/connections
```

Решение: **Connections не выносится в drawer** — это低频 operation (настраивается один раз). Доступ через `/settings` → «OpenCode Connections» секция или отдельный link.

### 7.3 `/agents` compat

- `/agents` → `NavigationManager.NavigateTo("/settings/agents")`
- `AgentsRedirect.razor` обновляется: `NavigateTo("/settings/agents")` вместо `NavigateTo("/settings")`

---

## 8. Локализация (resx keys)

Новые ключи (en, остальные fallback):

| Key | EN value |
|---|---|
| `NavWork` | Work |
| `NavKnowledge` | Knowledge |
| `NavAgents` | Agents |
| `NavProject` | Project |
| `NavAccount` | Account |
| `AgentsModels` | Agents & Models |
| `Workstations` | Workstations |
| `Connections` | Connections |
| `ProjectSettings` | Project Settings |
| `SettingsAccount` | Account |
| `SettingsSecurity` | Security |
| `ChatModelHint` | (existing) |

Удаляемые/переименованные:
- `NavQueue` → `NavWork`
- `NavMemory` → `NavKnowledge`
- `NavRuntime` → `NavAgents`

---

## 9. Пошаговый план реализации

### Шаг 1 — Settings split (foundation)

**Цель:** Разделить Settings.razor на 4 sub-routes.

| Файл | Действие |
|---|---|
| `Features/Settings/Settings.razor` | Убрать Devices + Workstation + `<Agents />` + `<OpenCodeConnections />`. Оставить Account (password, chat model) + TOTP. ~120 строк. |
| `Features/Agents/Agents.razor` | Добавить `@page "/settings/agents"`, `@layout MainLayout`, `@attribute [Authorize]`. Убрать из embedded-использования в Settings. |
| `Features/Settings/Workstations.razor` | Новый файл. Перенести Device list + Workstation paths + probe + install из Settings.razor. `@page "/settings/workstations"`. |
| `Features/Settings/OpenCodeConnections.razor` | Добавить `@page "/settings/connections"`, `@layout MainLayout`, `@attribute [Authorize]`. |
| `Features/Agents/AgentsRedirect.razor` | Изменить redirect: `NavigateTo("/settings/agents")`. |

**Верификация:** `dotnet build` + ручная проверка всех 4 routes.

### Шаг 2 — Project Settings route

**Цель:** Вынести ProjectManage из Dashboard в отдельный route.

| Файл | Действие |
|---|---|
| `Features/Projects/ProjectSettings.razor` | Новый файл. `@page "/project/settings"`. Содержимое = текущий ProjectManage.razor (folder, members, invites, labels, runtimes). |
| `Features/Dashboard/Dashboard.razor` | Убрать `<ProjectManage />`. Оставить stats + recent tasks + quick links. Обновить quick link на «Project Settings» → `/project/settings`. |
| `Features/Projects/ProjectManage.razor` | Удалить (код переехал в ProjectSettings.razor) или сохранить как embedded в ProjectSettings.razor. |

**Верификация:** Dashboard не содержит management-секций. `/project/settings` работает.

### Шаг 3 — Drawer restructure

**Цель:** Новая навигационная структура.

| Файл | Действие |
|---|---|
| `Shared/MainLayout.razor` | Переписать `MudNavMenu` блок (строки 59–90). Новая структура: WORK / KNOWLEDGE / AGENTS / PROJECT / ACCOUNT. |
| `Resources/Web.resx` | Добавить новые nav labels, переименовать существующие. |
| `Shared/MainLayout.razor` `RebuildCrumbs()` | Обновить switch для новых routes. |

**Верификация:** Все nav links ведут на корректные routes. Breadcrumbs обновлены.

### Шаг 4 — Deep-links + compat

**Цель:** Deep-link Dashboard → Agents, `/agents` redirect.

| Файл | Действие |
|---|---|
| `Features/Dashboard/Dashboard.razor` | Ссылка «Agents & Models» → `/settings/agents` (не `/settings`). |
| `Features/Agents/AgentsRedirect.razor` | Redirect → `/settings/agents`. |

**Верификация:** `/agents` редиректит на `/settings/agents`. Dashboard-карточка ведёт правильно.

### Шаг 5 — Breadcrumbs + resx

**Цель:** Полные breadcrumbs для всех новых routes.

| Файл | Действие |
|---|---|
| `Shared/MainLayout.razor` `RebuildCrumbs()` | Добавить случаи: `settings/agents`, `settings/workstations`, `settings/connections`, `project/settings`. |
| `Resources/Web.resx` | Добавить все новые ключи. |

**Верификация:** Breadcrumbs корректны на каждом route.

### Шаг 6 — Cleanup + tests

| Действие |
|---|
| Удалить мёртвый код из Settings.razor (fields, handlers для devices/workstation/agents) |
| `dotnet build` — 0 errors, 0 warnings |
| `dotnet test` — все Web.Tests pass |
| Ручная проверка: все routes, breadcrumbs, nav links, redirect `/agents` |

---

## 10. Что НЕ входит в Phase 3

| Элемент | Когда |
|---|---|
| SettingsLayout (sidebar внутри Settings) | Phase 4 (Design System) |
| Shared state components (LoadingState, EmptyState, ErrorState) | Phase 4/5 |
| Board/Task Detail decomposition | Phase 5 |
| Runtime health observable state | Phase 5 |
| Light mode | Phase 4 (если решим) |
| Mobile-first board (keyboard DnD) | Phase 5 |
| API token UI (`bcn_` management в `/project/settings`) | Phase 5 (backend готов, UI не создан) |

---

## 11. Риски

| Риск | Митигция |
|---|---|
| Agents.razor (564 строки) как route — большой компонент | В Phase 3 допустимо. Decomposition (tabs: Models/TaskKinds/MCP/Runtime) — Phase 5. |
| Workstations.razor перенос — device probe + command queue logic сложная | Копируем код как есть, без рефакторинга. Рефакторинг — Phase 5. |
| ProjectSettings.razor дублирует ProjectManage.razor | Удаляем ProjectManage.razor после переноса. Один источник. |
| `NavLinkMatch.Prefix` для `/settings` захватывает `/settings/agents` | Использовать `NavLinkMatch.All` для `/settings` или порядок ссылок (sub-routes до parent). MudBlazor NavLink с Prefix: `/settings` будет active на `/settings/agents` — это корректное поведение (Settings group active). |

---

## 12. Acceptance criteria (Phase 3 gate)

- [ ] Каждый major route имеет: owner scope, JTBD, primary action, backend data contract (таблица §2).
- [ ] Settings разбит на ≥3 sub-routes (Account, Agents, Workstations, Connections).
- [ ] Dashboard не содержит ProjectManage.
- [ ] Project Settings — отдельный route.
- [ ] `/agents` редиректит на `/settings/agents`.
- [ ] Drawer содержит 5 групп: WORK, KNOWLEDGE, AGENTS, PROJECT, ACCOUNT.
- [ ] Breadcrumbs корректны для всех routes.
- [ ] `dotnet build` + `dotnet test` — 0 errors.
- [ ] Resx keys добавлены (en).
