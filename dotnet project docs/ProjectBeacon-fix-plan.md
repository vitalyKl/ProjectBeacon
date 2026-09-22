# План доработки ProjectBeacon

На основе анализа: `dotnet project docs/ProjectBeacon-analysis.md`

Способ применения: агент получает промпт из `dotnet project docs/agent-prompt-template.md`
с подстановкой `{STEP_NUMBER}` и читает этот документ сам.

Легенда: 🔴 — критично, 🟡 — среднее, ⚪ — мелкая находка.

Правила для всех шагов:
- Не трогать `AGENTS.md`, `.net project docs/ProjectBeacon-design-doc-v2.md`,
  `ProjectBeacon-dotnet-roadmap-v2.md`, если шаг прямо не об этом.
- Английский текст UI — только через `IStringLocalizer<Web>` (resx, английская ключ first).
- После каждого шага: `dotnet build` и `dotnet test` зелёные.
- Не коммитить, если не просят.

---

## 🔴 Priority 1

### Шаг 1. Песочница путей в `WorkstationActions`

**Проблема.** `WorkstationActions.ListDir / InitProject / ApplyOpencode / ScanGguf` берут `path`
из payload команды control plane и делают только `Path.GetFullPath(path)` — без проверки, что путь
внутри корня проекта. Локальный MCP-путь защищён (`WorkspacePath.ResolveInsideRoot`), server-driven — нет.

**Контекст (прочитать):**
- `ProjectBeacon.Application/Common/WorkspacePath.cs` — паттерн `ResolveInsideRoot`
- `ProjectBeacon.Application/Mcp/FileWorkspace.cs` — как паттерн применён там + его unit-тесты
- `ProjectBeacon.Cli/Client/WorkstationActions.cs` — действия без защиты
- `ProjectBeacon.Cli/Client/WorkstationDaemon.cs` — откуда приходят команды, что есть в payload
- `ProjectBeacon.Domain/Entities/Projects/ProjectRuntime.cs` — `LocalRoot`
- `ProjectBeacon.Application/Devices/DeviceHandlers.cs` — как команды ставятся в очередь

**Меняем:** `ProjectBeacon.Cli/Client/WorkstationActions.cs` (+ payload/команды, если root ещё не передаётся)

**Подшаги:**
1. Определить источник допустимого корня для каждого действия (ожидаемо `ProjectRuntime.LocalRoot`
   для пары `(ProjectId, DeviceId)`). Если в payload команды нет root — добавить его в очередь команд.
2. Вынести общий статический `ResolveInsideRoot` (воспользоваться `WorkspacePath` из Application —
   Cli его уже референсит) либо дублировать в Cli, если зависимость не проходит.
3. Применить валидацию в `ListDir`, `InitProject`, `ApplyOpencode`, `ScanGguf`: полный путь должен
   начинаться с `root + Path.DirectorySeparatorChar`. Отказ — как `Result`-ошибка, не исключение.
4. Тесты в `ProjectBeacon.Cli.Tests/WorkstationActionsTests.cs`:
   - путь внутри root — ок;
   - `../../outside` — отказ;
   - root, не совпадающий с `ProjectRuntime.LocalRoot` — отказ;
   - путь, равный root'у — разрешён (решение: да, корень сам по себе допустим для ListDir).

**Acceptance:** все 4 действия отклоняют пути вне корня проекта; новые тесты проходят;
`dotnet build && dotnet test` зелёные.

---

### Шаг 2. Защита `bcd_`-токена в `ClientStore`

**Проблема.** Токен устройства пишется в `%LOCALAPPDATA%\ProjectBeacon\client.json` (XDG на Unix)
обычным JSON без ACL/шифрования — хотя это самый ценный локальный секрет (демон владеет рабочим деревом).

**Контекст (прочитать):**
- `ProjectBeacon.Cli/Client/ClientStore.cs` — формат файла, чтение/запись
- `ProjectBeacon.Cli/Client/ClientEnrollment.cs` — где токен сохраняется впервые

**Меняем:** `ProjectBeacon.Cli/Client/ClientStore.cs`

**Подшаги:**
1. Windows: шифровать секреты (токен устройства) через DPAPI —
   `System.Security.Cryptography.ProtectedData` (Scope.LocalUser). Пакет уже есть в .NET 9.
2. Unix: создавать файл с режимом `600` (`File.SetUnixFileMode` после записи; порядок: записать
   во временный файл с 600 → атомарный rename).
3. Миграция: при чтении старого «голого» файла — считать, при следующем сохранении записать
   в защищённом виде (без ошибки для существующих установок).
4. Тесты в `ProjectBeacon.Cli.Tests`: roundtrip (записать → прочитать) работает; на Unix файл
   имеет mode 600; на Windows содержимое файла не содержит plaintext-токена.

**Acceptance:** токен в файле не хранится в виде plaintext; существующие клиенты мигрируют без
ручного вмешательства; `dotnet build && dotnet test` зелёные.

---

### Шаг 3. Контраст цветов темы (WCAG AA)

**Проблема.** Расчёт по WCAG: `WarningContrastText` на `Warning` (light) = 2.85:1,
`SuccessContrastText` на `Success` (light) = 3.58:1, `PrimaryContrastText` на `Primary` (dark) = 3.52:1.
Нужно ≥4.5:1 для обычного текста.

**Контекст (прочитать):**
- `ProjectBeacon.Web/Theme/BeaconTheme.cs` — точные hex фона и contrast-текстов
- `ProjectBeacon.Web.Tests/ChipPaletteTests.cs` — есть ли ассерты на эти цвета

**Меняем:** `ProjectBeacon.Web/Theme/BeaconTheme.cs`

**Подшаги:**
1. Выписать точные hex-пары (фон / текущий contrast-текст) из темы.
2. Подобрать новые contrast-тексты так, чтобы ratio ≥4.5:1 (WCAG: relative luminance,
   ratio = (L1+0.05)/(L2+0.05); линейные sRGB). Обычно достаточно чёрного/почти чёрного текста
   на светлых chip-фонах — проверить каждую пару.
3. Заменить значения. Если `ChipPaletteTests` или другие тесты ассертят старые hex —
   обновить ожидаемые значения в этом же шаге.

**Acceptance:** все три пары ≥4.5:1; build зелёный; тесты палитры проходят.

---

### Шаг 4. Клавиатурная смена статуса задачи

**Проблема.** Единственный способ сменить статус — drag-and-drop на `Board.razor`. На `TaskDetail.razor`
статус — read-only `MudChip`. Пользователь с клавиатуры/скринридером не может провести задачу по циклу.

**Контекст (прочитать):**
- `ProjectBeacon.Web/Features/Tasks/TaskDetail.razor` — текущий read-only чип
- `ProjectBeacon.Application/Tasks/ChangeTaskStatusHandler.cs` + `TaskItemCommands.cs` — команда
- `ProjectBeacon.Web/Features/Board/Board.razor` — как доска уже вызывает handler
- `ProjectBeacon.Web/Resources/Web.resx` — ключи локализации

**Меняем:** `ProjectBeacon.Web/Features/Tasks/TaskDetail.razor`, `Web.resx` (+ `Web.ru.resx` и др.
по желанию, fallback на English работает)

**Подшаги:**
1. Заменить read-only `MudChip` статуса на `MudSelect<TaskItemStatus>` (все значения enum),
   `OnChange` → тот же `ChangeTaskStatusHandler`, что и Board.
2. Показ ошибки при отказе смены (локализованный).
3. Добавить ключи в `Web.resx` (English first), не хардкодить английский в razor.
4. Проверить `LocalizationTests` — нет захардкоженных строк.

**Acceptance:** статус меняется из UI `TaskDetail` без drag-and-drop; контрол доступен с клавиатуры
(MudSelect — из коробки); локализация не сломана; `dotnet build && dotnet test` зелёные.

---

## 🟡 Priority 2

### Шаг 5. Исправить `AGENTS.md` и `README.md`

**Проблема.** Расхождения с кодом:
- Security-строка про `actor.kind === "worker"` — это TS-синтаксис из старой реализации; C#-код
  сравнивает bearer с `BEACON_WORKER_TOKEN` через `FixedTimeEquals`.
- Пути `apps/mcp`, `packages/ui` — из старого TS-монорепо, в .NET-дереве их нет.
- `typescript/` описан как часть репозитория, но он в `.gitignore` (локальная копия автора).
- Список drawer (9 пунктов) не включает `Chat`, хотя в `MainLayout.razor` их 10.

**Контекст (прочитать):**
- `AGENTS.md`, `README.md`
- `ProjectBeacon.Web/Shared/MainLayout.razor` — реальный список пунктов
- `ProjectBeacon.Application/Tasks/FinishWorkCommand.cs`, `PipelineHandlers.cs` — реальная проверка worker-токена
- `.gitignore`

**Меняем:** `AGENTS.md`, `README.md` (только перечисленные расхождения)

**Подшаги:**
1. Переписать security-строку про worker: описать реальный механизм (bearer-токен, сравнение с
   `BEACON_WORKER_TOKEN`), сохранить предупреждение «не печатать root-credential».
2. Заменить `apps/mcp` → `ProjectBeacon.API` (+ `ProjectBeacon.Cli` для MCP stdio); `packages/ui`
   переформулировать без TS-пути.
3. `typescript/` — пометить, что это локальная справочная копия, не часть репозитория.
4. Добавить `Chat` в перечисление drawer'ов в обоих файлах.
5. Прогнать по `AGENTS.md` все упоминаемые пути/имена: каждый должен существовать в дереве.

**Acceptance:** каждое упомянутое в `AGENTS.md` имя файла/пути существует; описание worker-механизма
совпадает с кодом; список drawer = 10 пунктов как в `MainLayout.razor`.

---

### Шаг 6. Явный `ProjectId`-предикат в `ClaimTaskHandler`

**Проблема.** `FROM ... WHERE "Id" = {0} AND "Status" = 'Todo'` без `ProjectId`; изоляция целиком
на глобальном EF-фильтре. Нет теста, что участник проекта A не заберёт задачу проекта B.

**Контекст (прочитать):**
- `ProjectBeacon.Application/Tasks/ClaimTaskHandler.cs`, `ClaimTaskCommand.cs`
- `ProjectBeacon.API/Endpoints/TaskEndpoints.cs` — что приходит с маршрута
- `ProjectBeacon.API.Tests/ClaimTaskPostgresTests.cs` — существующие тесты (unscoped)
- `ProjectBeacon.Infrastructure/Data/TenantScopedQueryFilterConvention.cs` — как работает фильтр

**Меняем:** `ClaimTaskHandler.cs` (+ `ClaimTaskCommand.cs` и endpoint, если `ProjectId` ещё не передаётся)

**Подшаги:**
1. Добавить `AND "ProjectId" = {1}` в `FromSqlRaw` с параметром (не конкатенацией).
2. Источник `ProjectId`: из команды (endpoint должен передавать проект; если в маршруте
   `/v1/tasks/{taskId}/claim` его нет — брать из tenant-контекста запроса, но явно передать
   в SQL-параметр, а не полагаться на фильтр).
3. Тест `NonMember_CannotClaimForeignProjectTask` в `ClaimTaskPostgresTests`:
   - создать 2 проекта, 2 пользователей, задача в B;
   - пользователь A (scoped в проект A) шлёт claim на задачу B → отказ;
   - пользователь B → успех.
4. Существующие concurrency-тесты не должны сломаться (при необходимости адаптировать их
   под явный `ProjectId`).

**Acceptance:** новый тест изоляции проходит; concurrency-тесты зелёные; build зелёный.

---

### Шаг 7. `UseForwardedHeaders` для production-пути (K8s)

**Проблема.** Cookie `SecurePolicy = SameAsRequest`, но `UseForwardedHeaders` нигде нет. За
TLS-terminating ingress под видит plain HTTP → `Request.IsHttps == false` → auth-cookie выпускается
без `Secure`, хотя клиент видит HTTPS.

**Контекст (прочитать):**
- `ProjectBeacon.Web/Program.cs` — порядок пайплайна
- `ProjectBeacon.Web/Extensions/MiddlewareExtensions.cs`
- `deploy/README.md`, `deploy/k8s/ingress.yaml` — production-схема
- `ProjectBeacon.Web.Tests/CookieLoginHttpTests.cs` — есть ли ассерты на Secure-флаг

**Меняем:** `ProjectBeacon.Web/Program.cs` (или `MiddlewareExtensions.cs`)

**Подшаги:**
1. Добавить `app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = XForwardedFor | XForwardedProto })`
   строго до `UseAuthentication`/`UseAuthorization` и cookie-логики.
2. `ForwardedProtoHeader = "X-Forwarded-Proto"`.
3. Trusted proxies — из конфигурации (`ForwardedHeaders__TrustedProxies` / known networks),
   с задокументированным значением для K8s (CIDR ingress/пода) в `deploy/README.md`.
   Dev-режим без заголовков — поведение без изменений.
4. Тест (Web.Tests): запрос с `X-Forwarded-Proto: https` от доверенного прокси →
   cookie содержит `Secure`; без заголовка — как раньше.

**Acceptance:** за ingress'ом cookie получает `Secure`; dev-сценарии не изменены; тесты зелёные.

---

### Шаг 8. bUnit-тесты рендеринга для Board / TaskDetail / Settings

**Проблема.** В roadmap «Edge Playwright ✅» по Board/Reports/Decisions, но в репоситории ни bUnit,
ни Playwright нет — сценарии не защищены от регресса.

**Контекст (прочитать):**
- `ProjectBeacon.Web.Tests/*.cs` — стили тестов, `HandlerSqlite.cs` в Application.Tests (SQLite in-memory pattern)
- `ProjectBeacon.Web/ProjectBeacon.Web.csproj` — как зарегистрированы сервисы
- `ProjectBeacon.Web.Tests/ProjectBeacon.Web.Tests.csproj`

**Меняем:** `ProjectBeacon.Web.Tests` (новый пакет bUnit, новые тесты)

**Подшаги:**
1. Добавить `bunit` + `bunit.blazor.web` (или актуальный аналог) в `ProjectBeacon.Web.Tests`.
2. Тестовая фабрика: `TestContext` с MudBlazor (`services.AddMudServices()`), Application-хендлеры
   на SQLite in-memory (паттерн `HandlerSqlite`), `IStringLocalizer` на реальную resx-папку.
3. Тест 1: `TaskDetail` рендерит статус-контрол (связка с шагом 4) и позволяет сменить статус.
4. Тест 2: `Board` рендерит колонки по статусам и карточки задач.
5. Тест 3: `Settings` рендерит основные секции (участники, лейблы, устройства).
6. Fallback, если bUnit не дружит с MudBlazor из коробки: минимальный набор тестов на
   изолированные компоненты + пометка в roadmap, что сценарии Playwright-уровня — manual.
   Не раздувать шаг: максимум 1-2 дня работы.

**Acceptance:** ≥3 новых теста рендеринга проходят в `dotnet test` (CI); roadmap-строки про
Playwright-сценарии покрываются тестами или явно пометаны manual.

---

## ⚪ Priority 3

### Шаг 9. `GenerateRandomPassword` — modulo bias

**Проблема.** `bytes[i] % chars.Length` по алфавиту из 70 символов — классическое смещение.

**Контекст:** поиск `GenerateRandomPassword` (Infrastructure, auth-путь).

**Подшаги:**
1. Заменить на `RandomNumberGenerator.GetInt32(0, chars.Length)` на позицию.
2. Build + существующие auth-тесты зелёные.

**Acceptance:** смещение устранено; тесты зелёные.

---

### Шаг 10. Глобальный exception-handling (ProblemDetails)

**Проблема.** Нет `UseExceptionHandler`/ProblemDetails — нет единого формата ответа и логирования
для необработанных исключений.

**Контекст:** `ProjectBeacon.API/Program.cs`, `ProjectBeacon.Web/Program.cs`,
`ProjectBeacon.API.Tests` (ассерты на формат ошибок, если есть).

**Подшаги:**
1. `AddProblemDetails()` + `UseExceptionHandler` в API-пайплайне (и в Web для `/v1`).
2. Ответ 500: ProblemDetails JSON, без стектрейса (Production); лог полного исключения.
3. Проверить, что существующие API-тесты на ошибки не ломаются (формат должен остаться
   совместимым — сначала посмотреть, что возвращают текущие обработчики ошибок).

**Acceptance:** необработанное исключение → 500 ProblemDetails без stacktrace; тесты зелёные.

---

### Шаг 11. `Result<bool>` → `Result` для команд без значения

**Проблема.** ~40 хендлеров возвращают `Result<bool>` там, где значения нет.

**Контекст:** `grep -r "Result<bool>" ProjectBeacon.Application ProjectBeacon.Web`,
`ProjectBeacon.Application/Common/CQRS.cs` (есть ли non-generic `Result`).

**Подшаги (механическая миграция, по фиче-папкам):**
1. При отсутствии non-generic `Result` + `ICommand<Unit>` — добавить в `CQRS.cs`.
2. Мигрировать по папкам, после каждой — build+test: `Tasks`, `Projects`, `Context`, `Decisions`,
   `Milestones`, `Devices`, `Identity`, `Auth`, `Agents`, `Reports`, `Chat`.
3. Обновить вызывающих в Web/API (места `.Value`/`bool`-проверки).

**Acceptance:** `grep "Result<bool>"` в Application пуст (или остаются только осознанные случаи
с комментарием); все тесты зелёные.

---

### Шаг 12. `TenantIsolationMiddleware` — разбивка на шаги

**Проблема.** ~90 строк вложенных условий в самом security-критичном месте; риск для аудита.

**Контекст:** `ProjectBeacon.Infrastructure/Http/TenantIsolationMiddleware.cs`,
`ProjectBeacon.Web.Tests/TenantIsolationMiddlewareTests.cs`.

**Подшаги:**
1. Выделить именованные частные методы/чистые функции: resolve из route, из header, из claims,
   membership-проверка, admin-fallback.
2. `InvokeAsync` становится последовательностью вызовов + единым fail-closed выходом.
3. Unit-тесты на каждый resolver отдельно (те же входные условия, что и сейчас, но точечно).
4. Поведение должно остаться идентичным: все существующие middleware-тесты зелёные без правок.

**Acceptance:** `InvokeAsync` читается как последовательность именованных шагов; поведенческие
тесты не менялись + добавлены точечные; `dotnet test` зелёный.

---

### Шаг 13. `ChatPromptAsync` — настраиваемое завершение генерации

**Проблема.** «Конец» = 6 пустых поллингов (1.5с) или потолок 240 итераций (60с). Для CPU-инференса
пауза 1.5с между токенами нормальна — длинная генерация молча обрезается.

**Контекст:** `ProjectBeacon.Cli/Client/WorkstationDaemon.cs` (`ChatPromptAsync`), `ClientOptions.cs`.

**Подшаги:**
1. Именованные параметры/опции: `PollInterval` (250мс), `IdleTimeout` (default 10s),
   `MaxDuration` (default 180s).
2. При обрыве по любому из условий — лог (reason: idle/timeout) и явный сигнал, что генерация
   прервана, а не «idle по плану».
3. Если `ClientOptions` уже несёт настройки демона — подключить туда; иначе константы с
   комментариями про выбор дефолтов.

**Acceptance:** пороговые значения именованы и настраиваемы; обрыв логируется; тесты зелёные.

---

### Шаг 14. `ErrorDelay` — переименование и backoff

**Проблема.** Поле `ErrorDelay` фактически — обычный heartbeat-интервал; настоящий error-backoff
— захардкоженный `TimeSpan.FromSeconds(3)`.

**Контекст:** `ProjectBeacon.Cli/Client/WorkstationDaemon.cs` (паттерн `CommandErrorDelay` — образец).

**Подшаги:**
1. `ErrorDelay` → `HeartbeatInterval`.
2. Хардкод 3с → именованное поле/константа `ErrorBackoffInterval` (аналог `CommandErrorDelay`).

**Acceptance:** имена соответствуют смыслу; хардкода интервалов без имени нет; тесты зелёные.

---

### Шаг 15. Синхронный `Dispose()` поверх async

**Проблема.** `_openCode.DisposeAsync().AsTask().GetAwaiter().GetResult()` — блокирующий анти-паттерн.

**Контекст:** `ProjectBeacon.Cli/Client/WorkstationDaemon.cs` (место вызова).

**Подшаги:**
1. Сделать включающий метод `async`, заменить на `await _openCode.DisposeAsync()`.
2. Если метод в цепочке синхронных — поднять async на один уровень вверх (минимально).

**Acceptance:** `.GetAwaiter().GetResult()` в Cli/Client больше нет; тесты зелёные.

---

### Шаг 16. Доступность и responsive

**Проблема.** (а) 2 icon-only кнопки без `Title`; (б) фокус-индикация едва заметна;
(в) нет ни одного брейкпоинта для приложения (drawer 168px фикс, chat-dock 360px fixed).

**Контекст:** `ProjectBeacon.Web/Features/Backlog/Backlog.razor`,
`ProjectBeacon.Web/Features/Chat/ChatDock.razor`, `ProjectBeacon.Web/wwwroot/app.css`,
`ProjectBeacon.Web/Shared/MainLayout.razor`.

**Подшаги:**
1. `Title` (локализованный) на icon-кнопках удаления задачи (Backlog) и закрытия чата (ChatDock).
2. `.beacon-card-interactive:focus-visible` — заметный `outline`/`box-shadow`.
3. Media queries: на узких экранах drawer схлопывается (или становится overlay),
   `.beacon-chat-dock` на <480px занимает всю ширину с margin'ами; горизонтальный скролл Kanban —
   осознанный, зафиксировать `overflow-x`'ом и `min-width` колонок.
4. Проверить визуально: `dotnet run --project ProjectBeacon.Web`, окно ~375px и ~1400px.

**Acceptance:** обе кнопки имеют доступные имена; фокус виден без цвета рамки; на 375px чат-док
не вылезает за край; тесты локализации зелёные.

---

### Шаг 17. Единая система отступов и ширины

**Проблема.** Шкала `--space-1…16` в `:root` не используется нигде; сырые числа, часть вне 4px-сетки
(`7px 8px`, `10px`, `2px 8px`); `Width="168px"` задан и в теме, и в `MainLayout.razor`;
`.beacon-form` применён в одном месте из всех форм.

**Контекст:** `ProjectBeacon.Web/wwwroot/app.css`, `ProjectBeacon.Web/Theme/BeaconTheme.cs`,
`ProjectBeacon.Web/Shared/MainLayout.razor`, `ProjectBeacon.Web/Features/Settings/Settings.razor`.

**Подшаги:**
1. Заменить сырые padding/margin в `app.css` на `var(--space-*)`; значения вне сетки
   прижать к ней (7px→8px, 2px→4px) — визуально проверить diff.
2. Убрать `Width="168px"` из `MainLayout.razor` — использовать значение темы.
3. Ограничить ширину контента: в `MainLayout.razor` wrapper с `max-width` (например, 1440px,
   центрирован) вокруг `@Body`, `pa-8` сохранить.
4. Применить `.beacon-form` к форме `Settings.razor` (обернуть секции).

**Acceptance:** в `app.css` нет сырых padding/margin чисел (всё через `--space-*`);
drawer-width задан в одном месте; контент ограничен по ширине; тесты зелёные.

---

### Шаг 18. Актуализировать тестовые числа в roadmap

**Проблема.** Roadmap M6: «Domain 117, Application 120, Infrastructure 26, API 32, Web 44»;
фактически 128/153/28/38/40. Web упал — проверить, не потеряны ли тесты.

**Подшаги:**
1. `git log` по `ProjectBeacon.Web.Tests` — что произошло с 44→40 (удаление vs объединение).
2. Посчитать актуальные `[Fact]/[Theory]` (через `dotnet test --list-tests` или grep).
3. Обновить строку M6 в `ProjectBeacon-dotnet-roadmap-v2.md` + пометить Web-уменьшение
   (объединение тестов / осознанное удаление — с чем согласовано).

**Acceptance:** числа в roadmap совпадают с реальностью; причина падения Web-счётчика задокументирована.

---

### Шаг 19. `.env.example` — обязательный `JWT__Secret`

**Проблема.** `JWT__Secret` предзаполнен «на вид готовым» значением, тогда как `POSTGRES_PASSWORD`
и `BOOTSTRAP_ADMIN_TOKEN` оставлены пустыми — самовольный self-host скопирует и поедет с чужим секретом.

**Контекст:** `.env.example`, `README.md` (раздел Self-host), где читается `JWT__Secret`
(Infra/Security — `JwtTokenService`).

**Подшаги:**
1. Заменить предзаполненное значение на пустое + комментарий
   `# REQUIRED: generate a random string of at least 32 characters`.
2. Если код молча принимает дефолт/короткий секрет — добавить fail-fast валидацию
   (мин. 32 символа) при старте в Production-режиме.
3. README-строка «set … and JWT__Secret» теперь согласована с файлом — не менять.

**Acceptance:** `.env.example` не содержит готовых секретов; старт с пустым `JWT__Secret`
в Production — явная ошибка с понятным сообщением; тесты зелёные.

---

## Порядок выполнения

1. **Шаги 1–4** (🔴) — безопасность демона и базовая функциональность.
2. **Шаги 5–8** (🟡) — документация, tenant-изоляция, production-настройки, тесты UI.
3. **Шаги 9–19** (⚪) — качество кода, UX, polish.

Шаги 1–4 и 6–7 независимы друг от друга и могут выполняться параллельно разными агентами
(нет пересечений по файлам). Шаг 11 лучше после шага 8 (чтобы bUnit-тесты не мигрировать дважды).
Шаг 4 — до шага 8 (bUnit-тест на статус-контрол покрывает новый селект).
