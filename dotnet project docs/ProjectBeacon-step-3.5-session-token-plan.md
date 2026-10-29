# ProjectBeacon — шаг 3.5 (v2): единый механизм подключения клиентов к `/v1`

> Прогресс по выполненным работам (заполняется после каждого коммита C1–C8): `ProjectBeacon-step-3.5-progress.md`.

Основа: `main`, коммит `91f1061` (28 окт 2026, v0.6.2.4), свежий shallow-клон. Статический разбор кода; `dotnet build` и `dotnet test` не запускались (нет SDK), CI не проверялся. Пути относительно корня репозитория. Версия 2: переработана по решениям владельца от 28 окт (см. раздел 2); версия 1 была завязана на демон и OpenCode.

## 1. Цель

Один способ, которым любой клиент (демон, нативный харнес, внешний агент, будущий локальный API) получает учётные данные к `/v1` и работает через них. `beacon mcp` всегда ходит только в API. Новый тип подключения не требует нового механизма: выбирается вид клиента и набор прав.

## 2. Решения (зафиксированы)

| # | Решение |
|---|---|
| D1 | `beacon mcp` работает только через `/v1`. Режим «локальная БД» убирается. Локальное развёртывание позже станет ещё одним `BEACON_API_URL` (сканирование локальной сети, вход в локальный API); отложено |
| D2 | Ограничиваем только админские действия. Агентское подключение по умолчанию: `ContextRead + TaskRead + TaskWrite` |
| D3 | Модель подключения: токен `bcn_` с видом клиента (`ClientKind`), необязательной привязкой к устройству и команде, мягким отзывом |
| D4 | `SessionDrive` (запуск и остановка сессий пайплайна) не выдаётся по умолчанию и в этом шаге не реализуется: флаг сейчас нигде не проверяется (в коде только объявление в `Domain/Enums/ApiTokenCapability.cs`). Смысл получит вместе с функцией «агент сам запускает субагентов» |
| D5 | Правки агентов через «предложения» (агент предлагает, человек принимает или отклоняет, система применяет патч) это отдельный шаг со своим планом. В коде сейчас есть только статусы предложения у `Decision` и `Constraint`, общей сущности предложения правки и автоприменения нет |
| D7 | Операции над моделями (реестр, привязка ролей, перезагрузка и выгрузка прокси) админские: гейт `Admin`. Подзадачи наследуют привязку ролей основной задачи (решение владельца, 28 окт) |
| D6 | Сервер не применяет `AllowedMcpTools`/`AllowedPaths` подзадач; `SubtaskScopeGuard` остаётся клиентской подсказкой внутри `beacon mcp` и не считается защитой |

Намеренное сужение относительно обсуждения в чате: ручные токены (`Kind=Manual`) продолжают отзываться удалением строки (`RevokeApiTokenHandler`), чтобы не менять поведение UI и тестов. Мягкий отзыв (`RevokedAt`) вводится для токенов-подключений. Перевод ручных токенов на мягкий отзыв оформляется отдельно.

## 3. Что показал код

- `ApiToken` (`Domain/Entities/Identity/ApiToken.cs`) уже имеет `ExpiresAt`, `ProjectId`, `Capabilities`, `CreatedByUserId`. Нет привязки к устройству или команде, нет состояния «отозван». Просроченные удаляет `ExpiredRecordsCleanupService` раз в 30 минут.
- `BeaconApiClient.Resolve` (`Cli/Mcp/BeaconApiClient.cs`): обе переменные → API; ни одной → режим БД; одна → ошибка конфигурации.
- Режим БД это шесть инструментов `McpSessionTools` (`model_bind`, `model_status`, `task_create_subtask`, `subtask_report_result`, `task_review_verdict`, `task_pipeline_status`), которые идут в базу напрямую (`WithDbAsync`) и в ряде случаев действуют от имени владельца проекта. У всех шести уже есть API-варианты в `McpApiTools` (`BindRoleAsync`, `ModelStatusAsync`, `CreateSubtaskAsync`, `ReportResultAsync` и др.), поэтому удаление режима БД не отнимает функций.
- `ApiTokenAuthMiddleware` для токена `bcn_` выставляет `NameIdentifier = CreatedByUserId`, `token_id`, `project_id`, `capabilities`; на каждый запрос делает `RecordUsage` + `SaveChanges`.
- Админские действия для токенов уже закрыты: `AuthorizationService` возвращает `Forbidden` для `ActorType.ApiToken` в `CreateToken`/`RevokeToken`/`AddMember`/`RemoveMember` и `CanManageProject`. Часть маршрутов в матрице помечена `REVIEW REQUIRED`, в том числе `bind/unbind/reload/proxy` моделей: у них нет ни capability, ни проверки роли (`ProjectBeacon-authorization-matrix.md`, строка «Model backends»). После удаления режима БД токены дойдут до них через API, поэтому это нужно классифицировать (C6).
- `WorkstationCommand` имеет `Status`, `Attempts`, `LeaseExpiresAt`, `RequestedByUserId`. `ChatPrompt` и `RunEvalTurn` относятся к at-most-once (`CommandDelivery.IsAtMostOnce`), поэтому повторной выдачи токена на новую попытку нет.
- Контекст команды (`Id`, `ProjectId`) доходит до `DaemonCommandExecutor`, но не до `IDaemonChatSession.PromptAsync(cwd, payload, ct)`.
- Нативного харнеса в коде нет (`NativeAgentRuntime`, `McpStdioClient`, `IChatProvider` отсутствуют).

## 4. Модель подключения

Токен `bcn_` с дополнительными полями:
- `ClientKind`: `Manual` (0, существующие), `Daemon`, `Harness`, `External`. Нужен для аудита, фильтрации списка в UI и будущих различий в лимитах; на проверку прав сам по себе не влияет.
- `DeviceId?`, `CommandId?`: если заданы, токен живёт, пока жива команда (отзывается при её завершении, отмене, recovery, отзыве устройства).
- `RevokedAt?`: мягкий отзыв для токенов-подключений.
- Потолок прав для токенов, выдаваемых устройством: `ContextRead | TaskRead | TaskWrite`, константа сервера, не читается из тела запроса. Человек через существующий `POST` токенов выдаёт права как сейчас (включая `Admin` по правилам `CanCreateTokenAsync`).

Пути создания (общий обработчик, разные входы):
1. Человек (UI или API, существующий маршрут): `External`, срок и права на выбор.
2. Устройство (`bcd_`) для своей команды: `Harness` или `Daemon`, привязан к команде, срок не позднее `LeaseExpiresAt`.
3. Позже: локальный API, вход в который тоже выдаёт `External`.

## 5. План по коммитам

Каждый коммит оставляет существующие тесты зелёными. Postgres-тесты запускаются без `BEACON_TEST_SKIP_POSTGRES` (правило `AGENTS.md`). Миграции только через `dotnet ef`, затем `dotnet ef migrations has-pending-model-changes`.

### C1. `beacon mcp` только через API (без серверных правок)

Файлы: `Cli/Mcp/McpStdioServer.cs`, `McpSessionTools.cs`, `McpApiTools.cs`, `BeaconApiClient.cs`, `Cli.Tests/McpStdioServerTests.cs`, `McpApiToolsTests.cs`.
- Шесть инструментов `McpSessionTools` вызывают существующие API-варианты. Ветка `WithDbAsync`, `GetDbServiceProvider` и `OwnerUserIdAsync` удаляются.
- Без учётных данных: `beacon mcp` стартует, файловые инструменты работают, control-plane инструменты возвращают одно понятное сообщение: «нужны оба `BEACON_API_URL` и `BEACON_API_TOKEN`» (позже: или `beacon login`); значения переменных в тексте не печатаются. Инструменты не скрываются.
- «Задана только одна переменная» остаётся отдельным сообщением.
- Проверить, остаются ли зависимости `Cli → Infrastructure`, нужные только режиму БД; удалять зависимость только если она больше нигде не используется.

Acceptance: все шесть инструментов проходят через `/v1` в тестах; нет ссылок на `TenantScope`/`BeaconDbContext` в `Cli/Mcp`; текст ошибки без значений.

### C2. Схема

Файлы: `Domain/Entities/Identity/ApiToken.cs`, `Domain/Enums/` (новый `ApiClientKind`), `Infrastructure/Data/Configurations/ApiTokenConfiguration.cs`, миграция в `Infrastructure/Data/Migrations/`, `Application/Projects/ApiTokenHandlers.cs`.
- Аддитивные колонки `ClientKind` (по умолчанию `Manual`), `DeviceId?`, `CommandId?`, `RevokedAt?`; индекс по `CommandId`.
- `ApiToken.CreateConnection(...)`, `Revoke()`. Существующий `Create` не меняется.
- `ListApiTokensHandler`/`GetApiTokenHandler` показывают только `Manual` и `External` (токены демона и харнеса не засоряют список).
- Очистка: `ExpiredRecordsCleanupService` удаляет просроченные и отозванные подключения старше суток.

Acceptance: миграция применяется на живом Postgres; тест покрытия RLS зелёный без правок; существующие токены получают `Manual`.

### C3. Выдача токена устройству

Файлы: новый `Application/Devices/IssueConnectionTokenHandler.cs` (+ команда в `*Commands.cs`), `API/Endpoints/DeviceEndpoints.cs`, `Application/DependencyInjection.cs`.
- `POST /v1/devices/me/connection-tokens`, `RequireDevice()`. Тело: `commandId`, `projectId`.
- Одна транзакция (стратегия выполнения + `BeginTransaction`, как в `SendChatPromptHandler`):
  1. guarded-обновление строки команды (`WHERE Id, DeviceId, Status='Running'`), которое берёт блокировку; ноль строк → 409;
  2. проверки: вид команды в наборе (`ChatPrompt`, `RunEvalTurn`; `RunPipelineSession` добавляется вместе с шагом 6 харнеса); `command.ProjectId == projectId`; `RequestedByUserId` задан и член проекта; `device.UserId == RequestedByUserId`; устройство не отозвано;
  3. повторный запрос по той же команде отзывает предыдущий токен и выдаёт новый;
  4. вставка токена: права по D3, `ExpiresAt = LeaseExpiresAt`, `CreatedByUserId = RequestedByUserId`.
- Сырой токен возвращается один раз, в БД хеш.
- Привязку тенанта для запросов устройства с `projectId` повторить по существующему приёму (его использует `PostChatAsync` в `DaemonChatSession`); как это сделано в API-слое, я не прослеживала, подтвердить чтением при реализации.

Тесты (Application): чужое устройство; команда не `Running`; вид вне набора; несовпадение `ProjectId`; нет `RequestedByUserId`; не член проекта; отозванное устройство; права из тела игнорируются; повторный запрос отзывает прежний токен.

### C4. Аутентификация и отзыв

Файлы: `API/Auth/ApiTokenAuthMiddleware.cs`, `Application/Devices/CommandCompletions.cs`, `CommandRecovery.cs`, `WorkstationCommandHandlers.cs`, обработчик отзыва устройства.
- Middleware отклоняет токен с `RevokedAt != null`. Для токенов-подключений не делает `RecordUsage` + `SaveChanges` на каждый запрос.
- Отзыв токенов команды в той же транзакции, что и переход: `Succeeded`/`Failed` (completion), `Cancelled`, `RecoverAsync` (`Running → Failed` и `Running → Pending`), отзыв устройства. Через guarded SQL (`UPDATE ApiTokens SET RevokedAt = now WHERE CommandId = @id AND RevokedAt IS NULL`).
- Гонка «выдача против завершения»: выдача блокирует строку команды (C3 п.1), завершение, ждущее эту строку, видит токен и отзывает его; выдача после завершения получает 409.

Тесты (Postgres, по образцу `RecoverStaleCommandsPostgresTests`, `ConcurrencyPostgresTests`): токен отклоняется (401) после completion, recovery-`Failed`, requeue, отмены и отзыва устройства; параллельные «выдача + completion» не оставляют живого токена у завершённой команды.

### C5. Клиент демона

Файлы: новый `Cli/Client/ConnectionTokenProvider.cs`, `DaemonChatSession.cs`, `DaemonCommandExecutor.cs`, `DaemonCommandLoop.cs`.
- `AcquireAsync(commandId, projectId, ct)` возвращает окружение `{BEACON_API_URL, BEACON_API_TOKEN, BEACON_PROJECT_ID}`; токен только в памяти, после команды обнуляется.
- `CommandContext` (`Id`, `ProjectId`) протягивается через `DaemonCommandExecutor` в `IDaemonChatSession`; меняет подписи и заглушки в `Cli.Tests` (`DaemonCommandLoopTests`, `ClientDaemonTests`).
- Путь OpenCode не меняется: `beacon mcp` запускает OpenCode один раз на процесс `serve`, поэтому токен команды ему не передать. Он работает на учётных данных пользователя (окружение клиента или C7). Провайдер используется только харнесом (шаг 4 харнеса).
- Маска в логах: строки через `_state.Log` и `Log?.Invoke` проходят замену `bcn_[0-9a-f]{48}` (формат из `CreateApiTokenHandler.GenerateToken`).

Тесты (Cli): токен не попадает в лог при успехе и ошибке; окружение содержит три переменные и только их; 4xx/409 превращается в понятную ошибку команды без повторов.

### C6. Классификация админских маршрутов

Файлы: `ProjectBeacon-authorization-matrix.md`, `ProjectBeacon-endpoint-classification.md`, `API.Tests/ActorRequirementMatrixHttpTests.cs`.
- Составить список админских маршрутов, закрытых для токенов: участники, токены, обновление и удаление проекта, устройства и постановка команд (`RequireHuman` уже есть), прочее по матрице.
- Решение владельца (28 окт): подзадачи используют тот же шаблон, что и основная задача, привязка моделей к ролям в подзадачах не меняется. Значит агенту `model_bind` не нужен, и операции над моделями админские.
- Код расходится с матрицей: в `API/Endpoints/ModelEndpoints.cs` у этих маршрутов гейт есть, `RequireHumanOrApiToken(TaskWrite)` (матрица пишет «no capability»). Следовательно, сейчас токен с `TaskWrite` может менять реестр моделей, привязки ролей и перезагружать прокси.
- Меняем гейт на `RequireHumanOrApiToken(Admin)` для: `POST /v1/models`, `DELETE /v1/models/{id}`, `POST /v1/models/bind`, `DELETE /v1/models/bind/{role}`, `POST /v1/models/proxy/reload`, `POST /v1/models/proxy/unload`. Человек проходит как раньше; токен проходит только с `Admin` (по правилу «Admin удовлетворяет любому требованию»). Чтения (`GET /v1/models`, `GET /v1/models/proxy/status`) остаются на `TaskRead`.
- Инструмент `model_bind` в `beacon mcp` остаётся (его вызовет человеческий или `Admin`-токен), для токена по умолчанию сервер вернёт 403; `model_status` работает. В описании инструмента указать требование `Admin`.
- Обновить строки «Model backends» в матрице и классификации по фактическому коду, снять с них `REVIEW REQUIRED`.
- Это ломающее изменение для уже выданных ручных токенов с `TaskWrite`, которые вызывают эти маршруты: записать в заметки к релизу.
- Тест: токен с правами по умолчанию получает 403 на каждый маршрут из админского списка; он же не может `POST /v1/devices/me/*`, выдавать токены, вызывать `RequireHuman`-маршруты.

### C7. Вход пользователя (`beacon login`), необязательный

Файлы: `Cli/Mcp/BeaconApiClient.cs`, новая команда `beacon login`, `Cli.Tests`.
- Порядок: оба `BEACON_*` из окружения → файл учётных данных → ошибка C1.
- `beacon login` входит, создаёт через существующий `CreateApiTokenHandler` подключение `External` (`ContextRead|TaskRead|TaskWrite`, срок 30 дней) и сохраняет в профиле с правами 0600 (Windows: ACL только для текущего пользователя). Это единый вход и для будущего локального API.
- Защита файла равна защите профиля: любой процесс того же пользователя его прочитает; записать в `SECURITY.md`.

### C8. Документы

- `SECURITY.md`: строка в таблице токенов, раздел про токены-подключения (кто выдаёт, привязка, потолок прав, отзыв, где живёт токен), ограничение: процесс того же пользователя на рабочей станции читает окружение дочернего `beacon mcp`.
- `dotnet project docs/mcp-host.md` (убрать режим БД), `ProjectBeacon-design-doc-v3.md` (строка о локальной БД), `command-recovery.md` (отзыв токенов при переходах), `AGENTS.md` (Pitfalls: одна строка), `ProjectBeacon-own-harness-plan.md` (D1 → ссылка на этот шаг; требование шага 4: дочерние процессы `run_command` запускаются с очищенным окружением, иначе агент прочтёт токен).

## 6. Приёмка шага 3.5

- `beacon mcp` без учётных данных: понятная ошибка, без значений переменных; режима БД нет.
- Токен после прогона не находится grep-ом в логах демона и папке данных клиента.
- Токен отклоняется после completion, recovery, отмены и отзыва устройства.
- Токен по умолчанию получает 403 на админских маршрутах и не может выдавать токены и работать как устройство.
- Эндпоинт выдачи отвергает не-устройства (401) и чужие команды (409/404).
- Позже, когда появится `McpStdioClient`: `context_compile` и `finish_work` из нативного харнеса проходят на живом `/v1`. До этого в CI то же проверяется через `McpStdioServer.RunAsync` с `McpEnvironment.Of(...)` против тестового хоста API.

## 7. Вне шага

- Предложения правок (D5): отдельный план.
- `SessionDrive` (D4), серверная привязка к подзадаче (D6).
- Локальный API и его обнаружение в сети.
- Мягкий отзыв ручных токенов; локальный брокер токенов для пути OpenCode.

## 8. Риски и непроверенное

- Гонка выдачи и завершения закрывается блокировкой строки и Postgres-тестами C4; без них считать решение непроверенным.
- C4 затрагивает контракт доставки; обязателен прогон `CommandDeliveryTests`, `CommandRecoveryTests`, `RecoverStaleCommandsPostgresTests`.
- C1 удаляет рабочий сейчас режим; пользователи с настроенным окружением БД для `beacon mcp` потеряют его. Записать в заметки к релизу.
- Смена гейтов моделей на `Admin` ломает существующих клиентов с `TaskWrite`; прогнать `API.Tests` по моделям и `ActorRequirementMatrixHttpTests`.
- Не проверено: сборка, тесты, CI на `91f1061`; механизм привязки тенанта для запросов устройства; какой capability требует маршрут `pipeline_start`; зависимости `Cli → Infrastructure`, нужные только режиму БД.
