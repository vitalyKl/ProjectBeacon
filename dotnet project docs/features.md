# ТЗ: Task-пайплайн (Planner → Actor → Cold Review) и оркестрация локальных моделей в ProjectBeacon

## 0. Контекст и цель

ProjectBeacon — control plane для управления AI-кодинг-агентами (.NET 9 / Blazor Server / MudBlazor, API как MCP tool surface, Application-слой вызывается Blazor-слоем in-process).

Цель этого ТЗ — добавить в Beacon два связанных, но разделимых по реализации блока:

1. **Task-пайплайн** — многофазный процесс выполнения задачи через несколько ролей (planner → actor(s) → review) с изоляцией контекста между фазами.
2. **Model Orchestration** — управление жизненным циклом локальных LLM-бэкендов (FreeToken, llama.cpp), чтобы разные фазы пайплайна могли использовать разные модели без ручного управления процессами.

Блоки независимы по реализации (Model Orchestration можно сделать без Task-пайплайна и наоборот), но Task-пайплайн предполагает Model Orchestration как зависимость для полноценной работы с разнородными локальными моделями.

---

## 1. Блок A — Model Orchestration

### 1.1 Назначение
Beacon должен управлять внешним прокси `llama-swap` (или аналогом) как supervised-процессом, вместо того чтобы пользователь вручную поднимал `ft serve`/`llama-server.exe` в разных консолях.

### 1.2 Функциональные требования

- **FR-A1.** Beacon хранит реестр локальных моделей (backend type: `freetoken` | `llama.cpp`| `любой другой OpenAPI совместимыый оператор`; connection params: команда запуска, порт, доп. флаги) как часть своей constraint/config-системы (не в отдельном YAML, который пользователь редактирует руками).
- **FR-A2.** Beacon генерирует конфигурацию `llama-swap` (`config.yaml`) из этого реестра и (пере)запускает/reload'ит `llama-swap` при изменении реестра.
- **FR-A3.** Beacon управляет процессом `llama-swap` (старт при запуске Beacon / остановка при выходе), проверяет его здоровье через `GET /health`.
- **FR-A4.** Beacon предоставляет MCP-тул `beacon.model.bind(role: string, modelId: string)` — привязка роли пайплайна (`planner`/`actor`/`review`) к конкретной модели из реестра.
- **FR-A5.** Beacon предоставляет MCP-тул `beacon.model.status()` — возвращает текущую загруженную модель, использование VRAM/RAM (через `GET /metrics` у llama-swap, если доступно), время последнего свапа.
- **FR-A6.** UI-страница в Blazor: список моделей реестра, текущий активный бэкенд, кнопка ручного unload/reload, привязка ролей (то же самое, что FR-A4, но через интерфейс).

### 1.3 Нефункциональные требования

- **NFR-A1.** Beacon **не переопределяет** low-level логику свапа (queueing запросов, health-check перед проксированием) — это ответственность `llama-swap`. Beacon — оркестратор конфигурации и наблюдатель, не замена прокси-движка.
- **NFR-A2.** Взаимодействие Beacon ↔ llama-swap — через HTTP (его собственный API), не через парсинг stdout процесса.
- **NFR-A3.** Ошибка/недоступность llama-swap не должна приводить к падению Beacon — деградация до "оркестрация моделей недоступна", остальной функционал работает.

### 1.4 Данные

```
LocalModelBackend
 ├─ Id: string
 ├─ BackendType: enum { FreeToken, LlamaCpp }
 ├─ LaunchCommand: string (шаблон с ${PORT})
 ├─ ContextSize: int
 ├─ ExtraFlags: string[]
 └─ Ttl: int (сек. до автовыгрузки простоя)

RoleBinding
 ├─ Role: enum { Planner, Actor, Review }
 └─ ModelBackendId: string (FK → LocalModelBackend)
```

---

## 2. Блок B — Task-пайплайн (Planner → Actor → Cold Review)

### 2.1 Назначение

Пользователь выбирает задачу в Beacon → создаётся пайплайн из фаз: планирование → выполнение подзадач → холодное ревью перед закрытием.

### 2.2 Модель данных

```
Task
 ├─ Id, Title, Description (вход от пользователя)
 ├─ Status: enum { Planning, Executing, Reviewing, Approved, ReopenedForRevision, Closed }
 ├─ PlannerSession: Session (1)
 ├─ Subtasks: Subtask[]
 └─ ReviewSession: Session? (создаётся на фазе Reviewing)

Subtask
 ├─ Id, ParentTaskId
 ├─ Instructions: string          // конкретная, недвусмысленная инструкция от planner'а
 ├─ AllowedMcpTools: string[]     // scope, задаётся planner'ом при создании
 ├─ AllowedPaths: string[]        // scope файловой системы
 ├─ ActorSession: Session (1)
 ├─ Status: enum { Pending, InProgress, Done, Failed }
 └─ Result: SubtaskResult
      ├─ DiffRef: string          // ссылка на diff (git patch / commit range)
      └─ Summary: string          // финальный текстовый отчёт actor'а

Session                            // обёртка над сессией OpenCode (или иного харнесса)
 ├─ ExternalSessionId: string      // id в OpenCode
 ├─ Role: enum { Planner, Actor, Review }
 └─ ModelBackendId: string
```

### 2.3 Поток выполнения (state machine)

1. **Planning.** Пользователь создаёт `Task`. Beacon спавнит `PlannerSession` (роль `planner`, модель — по `RoleBinding`). Planner получает задачу + доступ к обзору кодовой базы (glob/read/grep) и создаёт один или несколько `Subtask` через MCP-тул `beacon.task.create_subtask(instructions, allowedMcpTools, allowedPaths)`.
2. **Executing.** На каждый `Subtask` Beacon спавнит **новую** `ActorSession` (роль `actor`, отдельная модель по `RoleBinding`). Actor получает **только**: `Instructions` этого подтаска + разрешённые MCP/пути. Он **не получает** сырой transcript planner-сессии. По завершении — `SubtaskResult` (diff + summary) сохраняется, `ActorSession` закрывается.
3. **Reviewing.** Когда все `Subtask` завершены (`Done` или `Failed`), Beacon спавнит **новую** `ReviewSession` (может быть та же модель, что у planner, но **обязательно новая сессия**, не продолжение `PlannerSession`). Контекст review-сессии собирается Beacon'ом из артефактов:
   - исходная `Task.Description`
   - финальные `Instructions` каждого `Subtask` (то, что planner решил как финальный план — без промежуточных черновых рассуждений)
   - все `SubtaskResult.DiffRef` + `Summary`
4. **Verdict.** Review-сессия выносит вердикт через MCP-тул `beacon.task.review_verdict(verdict: Approve | ReopenSubtask, subtaskId?, note?)`.
   - `Approve` → `Task.Status = Approved` → пользователь подтверждает закрытие → `Closed`.
   - `ReopenSubtask` → указанный `Subtask.Status = Pending`, `Instructions` дополняются `note` от review, возврат к шагу 2 для этого подтаска.

### 2.4 Функциональные требования

- **FR-B1.** Изоляция контекста между фазами — обязательна. Ни одна `ActorSession`/`ReviewSession` не должна получать сырой transcript другой сессии; только явно перечисленные артефакты (см. 2.3).
- **FR-B2.** При спавне любой сессии через локальный backend Beacon должен передавать флаг подавления сохранения reasoning в истории (`--no-reasoning-preserve` для llama.cpp-бэкендов) — черновые рассуждения фазы не должны утекать в контекст следующей фазы через размер истории.
- **FR-B3.** `AllowedMcpTools`/`AllowedPaths` на уровне `Subtask` транслируются в scope-конфигурацию сессии харнесса (в терминах OpenCode — поле `permission`) при спавне `ActorSession`.
- **FR-B4.** UI: страница задачи показывает пайплайн визуально (Planning → Executing [N подтасков с их статусами] → Reviewing → Approved/Closed), с возможностью открыть diff/summary каждого подтаска и вердикт ревью.
- **FR-B5.** Ручное вмешательство пользователя возможно на любой стадии: отредактировать `Instructions` подтаска до запуска, принудительно завершить `Task` без ревью (с явным предупреждением), вручную вызвать `ReopenSubtask`.

### 2.5 Нефункциональные требования

- **NFR-B1.** Число циклов `ReopenSubtask` на один `Subtask` должно быть ограничено конфигурируемым лимитом (защита от бесконечного цикла план→ревью→доработка при слабой actor-модели).
- **NFR-B2.** Persistence всех артефактов (`Instructions`, `DiffRef`, `Summary`, вердикты) — обязательна для аудита, независимо от того, жива ли исходная сессия харнесса.

### 2.6 Открытые вопросы (требуют решения на этапе дизайна, не входят в объём этого ТЗ)

- Каким образом Beacon получает `DiffRef` от ActorSession — через git-хуки, через прямой вызов MCP-тула `report_result` из actor-промпта, или через полинг файловой системы репозитория.
- Формат хранения истории вердиктов при повторных `ReopenSubtask` (полная цепочка правок или только последняя итерация).
- Поведение при падении/зависании `ActorSession` (тот же класс проблемы, что зависший `read` на директории, разобранный ранее) — таймаут и автоматический перевод `Subtask` в `Failed`.

---

## 3. Зависимости между блоками

Task-пайплайн (Блок B) использует `RoleBinding` из Model Orchestration (Блок A) для определения, какая модель обслуживает каждую роль сессии. Блок A может быть реализован и сдан отдельно, до начала работы над Блоком B.

## 4. Вне объёма (Out of scope)

- Параллельное выполнение нескольких `Subtask` одновременно (в текущей аппаратной конфигурации — последовательное выполнение через один активный backend за раз).
- UI для редактирования `config.yaml` llama-swap напрямую — вся конфигурация идёт только через реестр Beacon.
- Автоматическое определение MoE/dense архитектуры модели при добавлении в реестр — тип задаётся пользователем вручную при создании `LocalModelBackend`.