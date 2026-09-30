# ProjectBeacon UI/UX Review & Roadmap v1

Дата ревью: 30 сентября 2026  
Ревьюируемый commit: `1cce6f4b26c77f283858abd05aabf1c6e6734085`  
Стек UI: ASP.NET Core Blazor + MudBlazor, dark-first shell, локализация через `IStringLocalizer<Web>`

---

## 0. Итог ревью

Текущий UI уже не является «сырой» стандартной MudBlazor-оболочкой. В проекте есть собственный visual language: dark surfaces, blue accent, hairline borders, 8/12px radius tiers, централизованные CSS variables, общий `MainLayout`, `PageHeader`, `ProjectSwitcher`, `ChipPalette`, `ChromeLabels` и повторяемые `beacon-*` классы.

Основная проблема сейчас не в том, что интерфейс «недостаточно красивый». Главная проблема — UX-архитектура ещё не соответствует зрелому control-plane продукту. Несколько рабочих контекстов объединены в слишком большие экраны, а часть функций существует одновременно в нескольких формах.

Ключевые наблюдения:

1. **Settings перегружен**: account/security, TOTP, devices, workstation, model/chat settings и весь Agents UI находятся на одной странице. Фактический `Settings.razor` имеет около 21.6 KB, а `Agents.razor` — около 22.4 KB.
2. **Agents намеренно является user-level настройкой, а не project-level доменом**: `/agents` редиректит в `/settings`, и это само по себе корректно при выбранной модели, где пользователь один раз настраивает свои модели/агенты для общего daemon/runtime и использует их в нескольких проектах. Проблема находится не в самом размещении, а в том, что глобальная конфигурация и project/runtime context сейчас недостаточно явно разделены внутри Settings.
3. **Dashboard смешивает monitoring/overview с project administration**: внутрь Dashboard встроен `ProjectManage`, который содержит folder attachment, members, invites и labels.
4. **Chat имеет две конкурирующие модели**: полноценная страница `/chat` и глобальный `ChatDock`. Пользователь получает два разных места входа в одну систему.
5. **UI design system внедрён частично**: базовые токены есть, но реальные размеры и spacing местами расходятся со спецификацией, а страницы активно используют MudBlazor utility classes (`pa-4`, `mt-4`, `mb-6` и т.п.) рядом с собственными `beacon-*` правилами.
6. **Основной shell расходится с UI-спекой**: документ фиксирует sidebar 168px, а текущая тема/компонент фактически использует 240px.
7. **Task Detail стал слишком большим рабочим экраном**: overview, review notes, phases, steps, pipeline, subtasks, sessions, verdicts, comments и compiled brief находятся в одном вертикальном потоке.
8. **Board хорошо оформлен визуально, но его mobile UX слабее desktop-сценария**: на ширинах до 959px три колонки превращаются в вертикальный список, что ухудшает drag-and-drop Kanban-модель.
9. **Агентный UX недостаточно отражает состояние процесса**: многие операции используют Snackbar после действия, но пользователю не всегда ясно, что именно выполняется, что queued, что applied, что failed и что требует ожидания.
10. **Accessibility присутствует точечно, а не системно**: есть хорошие примеры keyboard/focus поведения, но для сложных интерактивных элементов — прежде всего Kanban и агентных runtime-панелей — нет единой accessibility-модели и явной автоматизированной проверки.

Итого: текущую визуальную основу следует **сохранять**, а следующий большой UI этап должен быть не «ещё один restyle всех страниц», а **информационная архитектура + UX decomposition + design-system consolidation + state/feedback model**.

---

# 1. Приоритеты

## P0 — исправить до дальнейшего большого визуального polish

### P0.1 Settings как перегруженный super-screen

`ProjectBeacon.Web/Features/Settings/Settings.razor` объединяет:

- смену пароля;
- выбор chat model;
- OpenCode connections;
- TOTP;
- workstation devices;
- device creation/revocation;
- workstation paths;
- runtime probe;
- install actions;
- весь `<Agents />` компонент.

Это уже не Settings в обычном смысле. Это отдельные продукты внутри одного URL.

### Рекомендуемая структура

Разделить минимум на четыре UX-контекста:

```text
Settings
├── Account & Security
├── Workstations
├── Agents & Models
└── Connections
```

При этом `/settings` должен быть индексом/landing page раздела, а не бесконечной страницей.

---

### P0.2 Agents должен быть самостоятельным UX-context внутри Settings

Текущий `/agents -> /settings` следует сохранить как допустимое route-решение: конфигурация агентов относится к пользователю, а не к отдельному проекту. Это соответствует модели:

```text
User
  └── Beacon daemon / workstation runtime
        └── user's agent/model configuration
              ├── Project A
              ├── Project B
              └── Project C
```

То есть пользователь не должен заново создавать один и тот же агент для каждого проекта.

Проблема текущего UX в другом: `Agents.razor` одновременно показывает global user configuration и действия, которые относятся к конкретному runtime/project context. Поэтому экран должен явно разделить два уровня:

```text
Settings
└── Agents & Models
    ├── My agent configuration        <- user-level
    ├── Models / Task Kinds / MCP      <- user-level
    └── Apply to workstation/project  <- contextual operation
```

Route `/agents` можно оставить как совместимый shortcut, который ведёт в соответствующий section Settings. Не требуется делать Agents отдельным top-level пунктом navigation.

---

### P0.3 Dashboard должен стать overview, а не administration hub

Сейчас Dashboard включает `ProjectManage`, а тот содержит:

- подключение project folder;
- runtime device;
- members;
- invites;
- labels.

Это делает Dashboard длинным административным экраном и размывает его основную задачу — дать быстрый operational overview.

Рекомендуемая композиция:

```text
Dashboard
├── Project pulse
├── Active work / queue
├── Agent/runtime health
├── Current milestone
├── Context health / changes
└── Recent activity
```

Административные операции должны находиться в:

```text
Project / Manage
```

или отдельном `Project Settings`.

---

### P0.4 Chat: определить одну primary interaction model

Сейчас одновременно существуют:

```text
/chat       full chat workspace
ChatDock    global floating chat
```

Это не просто два UI-компонента: это две модели поведения.

Рекомендуемая модель:

- `/chat` — основной рабочий интерфейс;
- `ChatDock` — только quick interaction;
- dock не должен дублировать весь функционал страницы;
- dock должен показывать текущую сессию, 1–2 последние части и поле короткого prompt;
- переход в `/chat` — для полноценной работы, истории, выбора модели, просмотра tool calls и streaming state.

На desktop dock допустим как quick-access layer. На mobile он должен преобразовываться в bottom sheet / full-screen panel, а не оставаться fixed 360px desktop-style box.

---

# 2. P1 — информационная архитектура

## P1.1 Перестроить глобальную навигацию по user goals

Текущие группы:

```text
Queue
Memory
Runtime
```

Функционально они понятны разработчику проекта, но хуже читаются как пользовательская IA.

Рекомендуемый вариант:

```text
WORK
  Dashboard
  Board
  Backlog
  Roadmap

KNOWLEDGE
  Context
  Decisions
  Reports

AGENTS
  Chat
  Agents
  Workstations

ACCOUNT
  Settings
```

Названия групп являются UX-предложением, а не обязательным буквальным вариантом. Главное — группировать функции по пользовательской задаче, а не по внутренней архитектуре.

---

## P1.2 Project context должен быть визуально постоянным

`ProjectSwitcher` сейчас находится в sidebar, что правильно по смыслу, однако project context не очень хорошо выражен дальше по интерфейсу.

Нужно визуально закрепить:

```text
Current Project
Current runtime / device
Current agent state
```

как часть глобального рабочего контекста.

Для multi-project control plane пользователь должен практически всегда понимать:

- какой проект активен;
- какой runtime выбран;
- online ли workstation;
- какой agent/model активен.

---

## P1.3 Breadcrumbs должны отражать реальную глубину контекста

Сейчас `MainLayout` вручную строит top-level breadcrumbs, например:

```text
ProjectBeacon > Task Detail
```

Для task screen полезнее:

```text
Project > Board > Task name
```

Для настроек:

```text
Project > Agents > Models
```

Breadcrumbs не должны стать обязательными везде, но там, где page hierarchy реальна, они должны её показывать.

---

# 3. P1 — design system: привести спецификацию и код к одному состоянию

## P1.1 Sidebar geometry сейчас не соответствует спецификации

`UI Design Migration Specification.md` и `ProjectBeacon-ui-spec-corrected-geometry.md` фиксируют:

```text
Sidebar width = 168px
```

Текущий `BeaconTheme`:

```text
DrawerWidthLeft = 240px
```

Это не небольшое отклонение: 72px дают заметно другую композицию всего приложения.

Решение должно быть одно из двух:

1. вернуть 168px и реально следовать reference;
2. осознанно выбрать новый размер и обновить спецификацию.

Оставлять расхождение между code и authoritative spec нельзя.

---

## P1.2 Board card geometry также расходится со спецификацией

Спека задаёт board card padding около `10px`.

Текущий CSS:

```css
.beacon-board-card {
    padding: var(--space-3);
}
```

`--space-3 = 12px`.

Разница небольшая сама по себе, но это хороший пример того, что design system пока не является полностью authoritative: source of truth существует одновременно в документации и в runtime CSS.

---

## P1.3 Два design-token источника

В `BeaconTheme.cs` и `app.css` дублируются значения цветов и surface hierarchy.

Например dark palette и CSS variables содержат одну и ту же цветовую систему.

Цель:

```text
Design tokens
      ↓
CSS / MudTheme
      ↓
shared components
      ↓
pages
```

а не:

```text
MudTheme values
CSS variables
Mud utility classes
page-local overrides
```

Для текущего MudBlazor проекта допустимо оставить два технических слоя, но значения должны генерироваться/синхронизироваться из одного semantic token definition.

---

## P1.4 Light palette сейчас выглядит как dead design system

`BeaconTheme` определяет `PaletteLight`, но `MainLayout` принудительно использует:

```razor
IsDarkMode="true"
ObserveSystemDarkModeChange="false"
```

То есть light palette не является реальным пользовательским режимом.

Нужно принять решение:

- либо приложение официально dark-only — тогда light palette удалить/не поддерживать как ложный feature;
- либо добавить theme switch и действительно поддерживать light mode.

Для текущей концепции control plane dark-only выглядит логично, но это должно быть сознательным продуктовым решением.

---

# 4. P1 — унификация component language

## P1.1 Слишком много spacing-источников

Вместе используются:

```text
beacon-* classes
CSS variables
MudBlazor pa-* / mt-* / mb-* utilities
inline Style
page-local spacing
```

Например Task Detail и Dashboard активно используют `pa-4`, `mt-4`, `mb-6`, одновременно применяя `beacon-panel` и `beacon-card`.

Внешне это может выглядеть нормально, но со временем spacing drift неизбежен.

Рекомендуется:

- сохранить Mud utility classes там, где они действительно являются стандартом layout;
- для visual component geometry перейти на semantic classes/tokens;
- не смешивать несколько разных способов задать одну и ту же геометрию.

---

## P1.2 Shared primitives

Сейчас есть хорошие foundation helpers:

- `PageHeader`;
- `ChipPalette`;
- `ChromeLabels`;
- `ProjectSwitcher`.

Но отсутствует полноценный набор application-level primitives.

Добавить только действительно повторяющиеся:

```text
BeaconPanel
BeaconSectionHeader
BeaconStatusChip
BeaconEmptyState
BeaconLoadingState
BeaconErrorState
BeaconConfirmAction
BeaconDataTable
BeaconActionBar
```

Не нужно строить собственный design framework поверх MudBlazor. Цель — только централизовать project-specific semantics.

---

# 5. P1 — Task / Board UX

## P1.1 Board — сильная сторона, которую нужно сохранить

Текущий Board уже хорошо соответствует общей идее:

- три понятных состояния;
- drag-and-drop;
- компактные cards;
- semantic chips;
- progress track;
- keyboard interaction на task title.

TaskCard отдельно реализует `tabindex="0"` и обработку Enter/Space. Это хороший паттерн и его стоит распространить на остальные интерактивные элементы.

---

## P1.2 Kanban mobile mode требует отдельного UX

Сейчас CSS при `<960px` переводит board из horizontal layout в:

```text
column
column
column
```

Это сохраняет доступность контента, но разрушает главное преимущество Kanban — одновременное сравнение стадий и быстрый перенос задач между ними.

Рекомендуемая стратегия:

Desktop:

```text
Todo | In Progress | Done
```

Mobile:

```text
[ Todo ] [ In Progress ] [ Done ]

< swipe / horizontal columns >
```

или:

```text
[Status filter]

Task list
```

Отдельно нужно предоставить keyboard alternative для drag-and-drop.

---

## P1.3 Task Detail необходимо декомпозировать

`TaskDetail.razor` около 34 KB / 760 строк и объединяет:

- task overview;
- status;
- review notes;
- phases;
- steps;
- pipeline;
- subtasks;
- sessions;
- verdicts;
- comments;
- compiled brief.

Сейчас это один длинный scroll.

Для control-plane UX лучше модель:

```text
Task header
  title / status / priority / type / primary actions

Overview
  description / progress / review state

Work
  steps / active subtask / sessions

Pipeline
  lifecycle / review / verdicts

Context
  compiled brief / context cost

Discussion
  comments
```

При большом объёме контента использовать tabs или collapsible sections, но не прятать основной pipeline state слишком глубоко.

---

## P1.4 Action hierarchy на Task Detail нужно усилить

В pipeline одновременно присутствуют несколько `Outlined` кнопок, включая:

- CreateSubtask;
- StartReview;
- ForceClose;
- Approve;
- Reopen;
- ConfirmClose.

В такой зоне цвет и visual weight перестают однозначно показывать primary action.

Нужно разделить:

```text
Primary action
Secondary actions
Danger / override actions
```

и убрать ощущение, что все действия равнозначны.

Особенно `ForceClose` должен быть визуально и поведенчески отделён от обычного lifecycle.

---

# 6. P1 — Agents / Runtime UX

## P1.1 Agents & Models следует разделить по mental model, но сохранить внутри Settings

`Agents.razor` одновременно содержит:

- model registry;
- task kinds;
- templates / role bindings;
- device/runtime selection;
- MCP server config;
- proxy status;
- reload/unload actions.

Само соседство этих функций допустимо на уровне user settings, но они должны быть визуально разделены на секции/табы:

```text
Agents & Models
├── Agent setup
├── Models
├── Task kinds / templates
├── MCP
└── Runtime diagnostics
```

При этом важно явно маркировать scope каждого блока:

```text
User-level
  Model registry
  Task kinds / templates
  MCP defaults

Contextual
  Workstation selection
  Apply configuration
  Runtime/proxy status
```

Overview должен отвечать на вопрос «что настроено у пользователя и доступно его daemon/runtime», а не смешивать это с операциями над текущим project/runtime.

---

## P1.2 Runtime health должен быть observable state, а не набором кнопок

Сейчас рядом находятся:

```text
Refresh
Reload
Unload
Status
Memory
LastSwap
Error
```

Нужен единый runtime state model:

```text
Offline
Starting
Ready
Busy
Degraded
Stopping
Failed
```

с timestamp, текущей моделью и понятным explanation.

Кнопки должны зависеть от state.

Например:

```text
Ready   -> Reload
Busy    -> Unload disabled
Failed  -> Retry / Diagnostics
Offline -> Connect / Refresh
```

---

## P1.3 Model configuration должна быть progressive disclosure

`BackendDialog` показывает сразу много технических полей:

- launch command;
- OpenCode model;
- GGUF;
- context size;
- TTL;
- extra flags;
- notes;
- concurrent loading.

Для advanced users это нормально, но default screen должен сначала показать:

```text
Name
Backend
Model / source
Context
```

а advanced launch/runtime controls убрать под `Advanced`.

---

# 7. P1 — Workstation UX

## P1.1 Не показывать raw infrastructure model как основной UX

Settings сейчас показывает поля:

```text
ModelsRoot
HistoryDir
ProjectsRoot
LlamaSwapBin
LlamaCppBin
```

Для control plane это implementation detail.

Основной UX должен оперировать понятиями:

```text
Models location
Projects location
Agent data location
Runtime executable
```

а path-level details должны быть Advanced/Diagnostics.

---

## P1.2 Folder selection уже лучше raw path и должен стать стандартом

В project management уже существует `FolderPicker`.

Его следует использовать как primary interaction и для остальных path-related operations там, где возможно.

Raw text path должен оставаться fallback/advanced mode.

---

## P1.3 Queue-based workstation operations должны показывать lifecycle

Например `SaveWorkstation` и `Install` фактически:

```text
enqueue command
wait/poll
success/failure
```

Пользовательский UX должен отражать это буквально:

```text
Queued
Running
Completed
Failed
Timed out
```

а не просто блокировать кнопку и затем выдавать Snackbar.

---

# 8. P1 — Chat UX

## P1.1 Message model слишком примитивен для agent UI

Сейчас chat выводит примерно:

```text
Role
Body
```

Для agent runtime этого недостаточно.

В перспективе message block должен различать:

```text
User message
Assistant message
Thinking / reasoning state
Tool call
Tool result
System event
Error
Abort
Completed
```

Не обязательно всё показывать всегда. Но data model UX должен позволять эту типизацию.

---

## P1.2 Tool calls должны быть структурированными

Не стоит выводить технические действия просто текстом внутри `pre-wrap`.

Лучше:

```text
▸ Read files
  14 files
  3.2k tokens

▸ Run tests
  passed 42 / failed 0
```

с раскрытием подробностей.

Это особенно важно для продукта, ориентированного на human + agent workflow.

---

## P1.3 Streaming state

`Chat.razor` сейчас использует `MudProgressLinear Indeterminate`.

Для agent UX лучше показывать:

```text
Generating
Using tool
Waiting for runtime
Applying changes
Completed
Aborted
Failed
```

Это одновременно уменьшает ощущение «интерфейс завис» и объясняет происходящее.

---

# 9. P1 — Forms, dialogs и confirmations

## P1.1 Inline edit forms используются слишком часто

Roadmap, Decisions и другие screens используют forms прямо внутри страницы.

Это допустимо для коротких forms, но плохо масштабируется.

Принцип:

```text
Short create -> inline
Complex edit -> dialog / dedicated editor
Long settings -> dedicated subpage
```

---

## P1.2 Delete / dangerous actions должны иметь единый pattern

В проекте уже используется `ShowMessageBoxAsync`, что хорошо.

Но визуально destructive action и обычный secondary action пока часто выглядят как соседние `MudButton`.

Для destructive actions нужен единый confirmation language:

```text
Title
Consequence
Explicit destructive button
Cancel
```

Для irreversible runtime actions добавить пояснение последствий.

---

# 10. P1 — Loading, empty, error и success states

Сейчас основной набор:

- `MudProgressLinear`;
- `MudAlert`;
- Snackbar.

Это рабочий foundation, но state design стоит сделать системным.

Для каждого экранного типа определить:

```text
Loading
Empty
Ready
Saving
Queued
Running
Success
Recoverable error
Fatal error
Offline
Permission denied
```

Особенно для workstation/agent operations.

---

# 11. P1 — Responsive UX

Текущая responsive CSS есть и это сильная сторона:

- mobile landing layout;
- mobile chat dock;
- Kanban collapse;
- page header stacking;
- drawer responsive.

Но responsive сейчас в основном означает `stack/resize`, а не изменение interaction model.

Нужно проектировать три режима:

```text
Desktop
Tablet
Mobile
```

и отдельно определить:

- navigation;
- board;
- chat;
- tables;
- task detail;
- dialogs;
- runtime controls.

---

# 12. P1 — Accessibility

Положительные примеры уже есть:

- `aria-label` у icon buttons;
- keyboard open для TaskCard;
- focus-visible styling;
- TOTP QR имеет `alt`;
- кнопки используют стандартные MudBlazor controls.

Однако необходим системный audit.

## Обязательные проверки

- keyboard-only navigation;
- visible focus;
- logical tab order;
- dialogs focus trap;
- screen-reader names для icon actions;
- table headers / mobile alternatives;
- drag-and-drop alternative;
- color contrast;
- reduced motion;
- error messages connected to inputs;
- disabled vs readonly semantics.

Особенно проверить Kanban, ChatDock, ProjectSwitcher, task pipeline controls и Settings.

---

# 13. P1 — Localization

Основной UI использует `IStringLocalizer<Web>`, что правильно.

Но в UI уже видны hard-coded user-facing values, например в Agents:

```text
local
remote
URL
Command
```

а также технические identifiers:

```text
git
node
docker
```

Часть последней группы может оставаться literal product/tool names, но display labels и explanatory text должны идти через resource layer.

Нужно провести отдельный i18n audit:

```text
all visible strings
all helper text
all empty states
all button labels
all state labels
all error messages
```

и проверить полноту ресурсов `ru/de/ja/zh` относительно base resource.

---

# 14. P2 — Dashboard UX polish

После разборки administration из Dashboard его стоит превратить в operational pulse.

Рекомендуемая иерархия:

```text
Project / current state

[Active work] [Agents] [Runtime] [Context]

Current queue

Current milestone

Recent activity
```

Dashboard не должен повторять содержимое Board, Roadmap и Settings. Его задача — краткая сводка + переход.

---

# 15. P2 — Context / Decisions / Reports

## Context

Сейчас layout редактор + preview уже близок к подходящему master-detail UX.

Следующий шаг — явное разделение:

```text
Sections
Editor
Compiled result
```

и показать состояние compile/cache/version.

## Decisions

Current table-centric UI можно улучшить до:

```text
Decision title
Status
Context summary
Consequences
Actions
```

с отдельным details surface для длинного текста.

## Reports

Reports должны показывать:

```text
Report type
Generated at
Scope
Status
Open
```

а не только markdown body в таблице.

---

# 16. P2 — Roadmap UX

Текущий Roadmap отображает milestones карточками и перечисляет все task buttons внутри каждой карточки.

При росте количества задач карточка станет длинной.

Будущий UX лучше строить как:

```text
Milestone header
Progress
Task count
Short task preview

[Open milestone]
```

С детализацией в отдельный surface.

---

# 17. P2 — Visual polish

После IA refactor выполнить единый visual pass:

### Typography

Зафиксировать application-level hierarchy:

```text
Page title
Section title
Card title
Body
Secondary
Caption
```

Сейчас часть страницы опирается на MudBlazor `h4/h6/subtitle*`, часть — на CSS размеры. Нужна semantic mapping.

### Borders

Сохранить hairline border концепцию.

### Shadows

Основной UI должен оставаться border-driven. `ChatDock` использует `Elevation="8"`; это должно быть осознанным исключением для floating layer.

### Radius

Сохранить:

```text
12px panel
8px small
pill
```

и не вводить дополнительные tiers без необходимости.

---

# 18. P2 — Componentization

Кандидаты на decomposition:

```text
TaskDetail.razor
Settings.razor
Agents.razor
ProjectManage.razor
```

Цель не в уменьшении количества строк любой ценой.

Разделять по UX responsibility:

```text
TaskHeader
TaskOverview
TaskSteps
TaskPipeline
TaskSubtasks
TaskSessions
TaskVerdicts
TaskComments
TaskCompiledBrief
```

и аналогично для Settings/Agents.

Это улучшит не только maintainability, но и позволит строить разные desktop/mobile layouts без копирования business logic.

---

# 19. UX state model

Для agent control plane нужен общий визуальный vocabulary.

Рекомендуемые state families:

```text
Human
Agent
Runtime
Task
Pipeline
Command
Connection
```

Например:

```text
Runtime:
Offline / Starting / Ready / Busy / Degraded / Failed

Task:
Todo / In Progress / Review / Done / Blocked

Command:
Queued / Running / Succeeded / Failed / Cancelled

Connection:
Online / Degraded / Offline
```

Эти состояния должны отображаться одинаково в:

- dashboard;
- agents;
- settings;
- chat;
- task detail;
- workstation management.

---

# 20. Рекомендуемая структура UI после refactor

```text
Main shell
├── Project switcher
├── Global nav
├── Runtime status
└── Account

WORK
├── Dashboard
├── Board
├── Backlog
└── Roadmap

KNOWLEDGE
├── Context
├── Decisions
└── Reports

AGENTS
├── Chat
├── Agents
│   ├── Overview
│   ├── Models
│   ├── Task Kinds
│   ├── Runtime
│   └── MCP
└── Workstations

PROJECT
└── Project Settings

ACCOUNT
└── Settings
    ├── Account
    ├── Security
    └── Connections
```

Это не требует полностью менять routing сразу. Часть разделов может использовать tabs/secondary navigation внутри существующих routes.

---

# 21. Roadmap реализации

## Phase 0 — UI baseline

- [ ] Зафиксировать актуальные screenshots desktop/tablet/mobile.
- [ ] Зафиксировать список routes и primary purpose каждого route.
- [ ] Зафиксировать current design tokens.
- [ ] Принять решение по sidebar width.
- [ ] Принять решение dark-only vs light mode.
- [ ] Обозначить authoritative UI spec.

**Результат:** frozen baseline, от которого можно измерять изменения.

---

## Phase 1 — Information architecture

- [ ] Убрать embedded ProjectManage из Dashboard.
- [ ] Сохранить Agents как user-level settings context, без привязки к отдельному проекту.
- [ ] Сделать deep-link непосредственно в Agents & Models section, не только в Settings root.
- [ ] Явно отделить global agent configuration от contextual workstation/project operations.
- [ ] Разделить Settings на смысловые subsections.
- [ ] Определить единый primary Chat model.
- [ ] Обновить Dashboard links.
- [ ] Обновить breadcrumbs.

**Результат:** у каждого основного домена есть понятное место в приложении.

---

## Phase 2 — Design system consolidation

- [ ] Выбрать единый source of truth для semantic tokens.
- [ ] Синхронизировать `BeaconTheme` и `app.css`.
- [ ] Устранить geometry drift со спецификацией.
- [ ] Выравнять sidebar/card/panel geometry.
- [ ] Создать минимальный набор shared UI primitives.
- [ ] Сократить page-specific CSS.

**Результат:** новые страницы получают правильный visual language автоматически.

---

## Phase 3 — Shared state/feedback components

- [ ] `LoadingState`.
- [ ] `EmptyState`.
- [ ] `ErrorState`.
- [ ] `SavingState`.
- [ ] `QueuedState`.
- [ ] `RunningState`.
- [ ] `RuntimeStatus`.
- [ ] Unified confirmation pattern.
- [ ] Unified status chips.

**Результат:** одинаковое поведение UX across product.

---

## Phase 4 — Board + Task Detail

- [ ] Улучшить Task header.
- [ ] Ввести явную action hierarchy.
- [ ] Декомпозировать Task Detail.
- [ ] Сохранить текущий сильный TaskCard.
- [ ] Спроектировать mobile board interaction.
- [ ] Добавить keyboard alternative drag/drop.

**Результат:** core work-management flow становится главным UX эталоном продукта.

---

## Phase 5 — Agents + Workstations

- [ ] Разделить Models / Task Kinds / Runtime / MCP внутри Agents & Models settings.
- [ ] Явно показать user-level vs contextual scope для каждого блока.
- [ ] Сделать runtime state first-class.
- [ ] Progressive disclosure в model configuration.
- [ ] Скрыть infrastructure-level paths за Advanced/Diagnostics.
- [ ] Показать command lifecycle.
- [ ] Сделать Agents Overview operational.

**Результат:** control-plane UX становится понятным даже без знания внутренней архитектуры.

---

## Phase 6 — Chat

- [ ] Уточнить primary/full chat model.
- [ ] Перестроить message blocks.
- [ ] Structured tool calls.
- [ ] Streaming lifecycle.
- [ ] Better abort/error/retry state.
- [ ] Mobile full-screen/bottom-sheet dock.

**Результат:** Chat начинает выглядеть как agent workspace, а не как generic text chat.

---

## Phase 7 — Context / Decisions / Reports / Roadmap

- [ ] Context master-detail.
- [ ] Compile/cache state.
- [ ] Decision detail surface.
- [ ] Report metadata + detail.
- [ ] Roadmap milestone preview/detail split.

**Результат:** knowledge layer перестаёт быть набором generic CRUD screens.

---

## Phase 8 — Responsive + Accessibility

- [ ] Desktop regression.
- [ ] Tablet regression.
- [ ] Mobile regression.
- [ ] Keyboard-only pass.
- [ ] Screen-reader pass.
- [ ] Contrast audit.
- [ ] Reduced-motion behavior.
- [ ] Touch targets.
- [ ] Dialog focus behavior.

**Результат:** responsive/accessibility становятся свойством системы, а не отдельных компонентов.

---

## Phase 9 — Localization

- [ ] Hard-coded UI string audit.
- [ ] Base vs localized resource diff.
- [ ] State labels localization.
- [ ] Validation/error/help text localization.
- [ ] Dynamic label localization.

**Результат:** locale switch не ломает информационную иерархию.

---

## Phase 10 — Visual regression

Минимальный screenshot matrix:

```text
Desktop 1440+
Desktop 1280
Tablet 1024
Tablet 768
Mobile 390
Mobile 360
```

Для routes:

```text
/login
/dashboard
/board
/task/{id}
/chat
/agents
/settings
/context
/decisions
/roadmap
/reports
/projects/new
```

Снимать также состояния:

```text
loading
empty
error
offline
busy
success
```

---

# 22. Acceptance criteria

## IA

- [ ] Каждый крупный домен имеет одно primary место в navigation.
- [ ] Dashboard не содержит длинные administration sections.
- [ ] Agents находится внутри Settings осознанно и явно помечен как user-level configuration.
- [ ] Из Dashboard можно открыть Agents & Models напрямую через deep-link.
- [ ] Workstation operations не смешаны с account/security.
- [ ] Chat имеет чёткий full-vs-quick режим.

## Visual

- [ ] Один semantic token source.
- [ ] Sidebar geometry согласована со спецификацией.
- [ ] Panel/card/radius hierarchy единообразна.
- [ ] Typography hierarchy единообразна.
- [ ] Actions имеют предсказуемую visual priority.

## Interaction

- [ ] Все длинные операции показывают lifecycle.
- [ ] Нет action без feedback.
- [ ] Destructive actions требуют понятного подтверждения.
- [ ] Mobile не является просто «desktop stacked vertically» для critical workflows.

## Accessibility

- [ ] Все основные workflows проходят keyboard-only.
- [ ] Kanban имеет альтернативу drag/drop.
- [ ] Dialogs и menus корректно управляют focus.
- [ ] Color не является единственным carrier состояния.

## Localization

- [ ] User-facing strings не захардкожены без причины.
- [ ] Все state labels локализованы.
- [ ] Все основные языки имеют достаточное покрытие ресурсов.

---

# 23. Документация: что устарело и что делать

## UPDATE / сделать authoritative

### `dotnet project docs/UI Design Migration Specification.md`

Статус: **главный UI документ, но сейчас частично устаревший**.

Нужно обновить:

- sidebar geometry;
- actual MudBlazor mapping;
- реальную структуру pages/routes;
- current shell;
- Agent/Workstation information architecture;
- ChatDock behavior;
- responsive behavior;
- accessibility requirements;
- current token source.

После обновления это должен быть единственный authoritative UI specification.

---

### `AGENTS.md`

Добавить/обновить:

- authoritative UI spec;
- правило не делать page-specific design decisions без design tokens;
- shared component policy;
- responsive/accessibility validation;
- screenshot-based review flow.

---

### `README.md`

Обновить только то, что реально меняется после IA refactor:

- navigation;
- main UI sections;
- Agents/Workstations;
- Chat model;
- current authoritative UI documentation.

---

### `dotnet project docs/agent-prompt-template.md`

Если агенты продолжают применять UI migration tasks, шаблон должен ссылаться на новый authoritative UI spec и этот roadmap, а не на старые fix-plan документы.

---

## MERGE INTO SPEC, THEN ARCHIVE/DELETE

### `dotnet project docs/ProjectBeacon-ui-spec-corrected-geometry.md`

Этот документ содержит полезные исправленные geometry values, но по смыслу он уже является correction layer над основным UI spec.

Рекомендуемое действие:

1. перенести окончательные значения в `UI Design Migration Specification.md`;
2. пометить correction file как historical;
3. после проверки удалить/archive.

Не держать два одновременно authoritative geometry documents.

---

## REVIEW / сохранить, но обновить по мере необходимости

### `dotnet project docs/ProjectBeacon-design-doc-v2.md`

Historical - archived to `archive/docs/`. UI shell и information architecture синхронизированы с `ProjectBeacon-master-roadmap-v1.md`.

### `dotnet project docs/features.md`

Проверить UI feature descriptions и route assumptions после IA refactor.

### `dotnet project docs/task-pipeline-local-agents.md`

Синхронизировать UI flow task/pipeline/session/review с новой Task Detail моделью.

---

# 24. Рекомендуемый порядок спринтов

### Sprint 1

```text
IA baseline
+ Dashboard cleanup
+ Agents route
+ Settings split
```

### Sprint 2

```text
Design tokens
+ shell geometry
+ shared primitives
```

### Sprint 3

```text
Board
+ Task Detail
+ lifecycle feedback
```

### Sprint 4

```text
Agents
+ Models
+ Runtime
+ Workstations
```

### Sprint 5

```text
Chat
+ tool-call UX
+ streaming state
+ dock/full-page separation
```

### Sprint 6

```text
Context / Decisions / Reports / Roadmap
```

### Sprint 7

```text
Responsive
+ accessibility
+ localization
```

### Sprint 8

```text
Screenshot regression
+ cleanup
+ documentation consolidation
```

---

# 25. Что уже хорошо и не следует ломать

1. `MainLayout` уже даёт единый application shell.
2. `PageHeader` — правильный shared primitive.
3. `ProjectSwitcher` находится в глобальном shell и поддерживает multi-project context.
4. CSS variables уже образуют хороший foundation для semantic colors/spacings.
5. Dark palette с restrained borders хорошо соответствует выбранному visual direction.
6. `ChipPalette` и `ChromeLabels` правильно централизуют semantic presentation.
7. `TaskCard` имеет keyboard handling и visible focus behavior — хороший accessibility baseline.
8. Board имеет понятную визуальную модель и не перегружен декоративными элементами.
9. Chat уже вынесен в отдельный component и имеет quick-access концепцию; проблема только в том, что её нужно формально определить и ограничить.
10. Localization infrastructure уже присутствует и должна быть расширена, а не заменена.
11. `FolderPicker` уже показывает правильное направление UX для filesystem-oriented operations.

---

# 26. Финальный приоритет

Не следует начинать следующий UI этап с косметического прохода по всем `.razor`.

Правильная последовательность:

```text
Information Architecture
        ↓
Page decomposition
        ↓
State / feedback model
        ↓
Design system consolidation
        ↓
Core workflow UX
        ↓
Responsive + accessibility
        ↓
Visual polish
        ↓
Screenshot regression
```

Иначе получится визуально аккуратный интерфейс, в котором по-прежнему останутся те же UX-проблемы: Settings как super-screen, Dashboard как смесь monitoring и administration, global agent configuration без явного scope и двойной Chat.

---

# 27. Definition of Done

UI/UX overhaul считается завершённым, когда:

- [ ] IA отражает реальные user workflows.
- [ ] Dashboard является overview, а не administration hub.
- [ ] Agents и Workstations имеют собственные UX contexts внутри user-level settings.
- [ ] User-level agent configuration визуально отделена от project/runtime context.
- [ ] Settings разделён по смыслу.
- [ ] Chat имеет одну primary interaction model.
- [ ] Task Detail декомпозирован.
- [ ] Design tokens имеют один authoritative source.
- [ ] Code и UI specification совпадают по geometry.
- [ ] Loading/empty/error/busy/queued/success states унифицированы.
- [ ] Responsive behavior определён для desktop/tablet/mobile.
- [ ] Keyboard and accessibility checks пройдены.
- [ ] Localization audit завершён.
- [ ] Screenshot regression matrix пройдена.
- [ ] Старые/конфликтующие UI rules удалены.
- [ ] UI documentation актуализирована.
