# Восстановление зависших workstation-команд (P1.4 / H4)

Эта страница фиксирует контракт восстановления: как команда в состоянии
`Running` теряется, по какому правилу её можно вернуть в очередь или
завершить, и как повторная доставка не создаёт двойных side effects.
Сопутствующий документ: `command-delivery-semantics.md` (классификация
`AtMostOnce` / `Idempotent` / `ReconcileBacked` по видам команд). Код —
`CommandRecovery` (`ProjectBeacon.Application/Devices/CommandRecovery.cs`).

## Исполнительная сводка

До P1.4 застрявшая в `Running` команда никогда не разрешалась: claim
атомарный (H4.1), но реопера не было, и команда, чей daemon умер между
claim и completion, оставалась `Running` вечно, а повторный POST
completion делал один fire-and-forget запрос. Теперь:

- claim выдаёт команде **lease** (по виду, 15 минут) и запоминает
  **идентификатор процесса** (instance id) заявившего daemon'а;
- **stale-правило** переводит `Running` в recovery state только при явных
  признаках гибели процесса (устройство отозвано, offline > 45 c, или
  heartbeat от другого процесса);
- **at-most-once** виды завершаются как `Failed` и **никогда не
  повторляются** (at-most-once-with-loss сохраняется); **idempotent** виды
  возвращаются в `Pending` один раз (`Attempts++`), затем `Failed`;
- **generation protocol** гарантирует, что поздний completion от мёртвой
  попытки не может завершить новую попытку той же команды;
- клиент ретраит только **ACK** (completion POST) при transport failure /
  5xx, bounded backoff, и никогда не переисполняет тело команды;
- **Worker** (`StaleCommandRecoveryService`) каждый 60 c применяет правило,
  unscoped; каждый переход — guarded update, конкурирующие тики меняют
  строку не более одного раза.

## State machine

Состояния: `Pending`, `Running`, `Succeeded`, `Failed`, `Cancelled`
(`WorkstationCommandStatus`).

| Переход | Кто | Условие |
|---|---|---|
| `Create` → `Pending` | `EnqueueCommandHandler` | всегда |
| `Pending` → `Running` | claim (`ClaimNextCommandHandler`) | guarded `UPDATE … WHERE Status='Pending'`; выдаёт lease, записывает `ClaimInstanceId` |
| `Pending` → `Failed` | claim (guarded fail) | project-kind команда без `LocalRoot` (`CommandSandbox.RuntimeRequired`) |
| `Pending`/`Running` → `Succeeded` | completion POST | guarded: строка не в терминальном статусе; generation сходится; эффекты completion применяются |
| `Pending`/`Running` → `Failed` | completion POST | то же, `success=false` |
| `Pending`/`Running` → `Cancelled` | `Cancel` | только пока не в терминальном |
| `Running` → `Pending` | recovery (`CommandRecovery.RecoverAsync`) | stale-правило + idempotent вид + `Attempts < 1`; `Attempts++`, lease и `StartedAt` сбрасываются |
| `Running` → `Failed` | recovery | stale-правило + (at-most-once вид или `Attempts >= 1`) |

`Succeed`/`Fail` в домене разрешены из `Pending` и `Running`; защита от
двойного применения — в completion handler (guarded terminal transition,
H4.1-стиль). `LeaseExpiresAt` сбрасывается при requeue и при любом
терминальном переходе.

## Lease contract

- Lease выдаётся в claim: `LeaseExpiresAt = StartedAt + CommandLease.For(kind)`.
- `CommandLease.For(kind)` — исчерпывающий switch по всем 17 видам, каждый
  вид имеет явный arm (15 минут). Новый вид без явного arm не компилируется
  без осознанного решения.
- **Достаточность 15 минут.** Все виды жёстко ограничены клиентом:
  LLM/eval-ходы — 180 c (`ChatPrompt`, `RunEvalTurn`, `ChatEnsureSession`),
  review check и winget-установка — 120 c, остальные — файловые операции и
  HTTP-вызовы, завершающиеся за секунды. 15 минут больше любого возможного
  времени живого исполнения с большим запасом, поэтому **продление lease
  не нужно**: lease существует, чтобы отличить «процесс жив и исполняет» от
  «процесс мёртв», а не для точного учёта времени.
- Lease — не TTL исполнения. Живой daemon может завершить команду в любой
  момент до конца lease; истечение lease само по себе ничего не означает
  (см. stale-правило).

## Stale-правило

`Running`-строка является **stale** (подлежит восстановлению) только если
выполняются оба условия:

1. `LeaseExpiresAt < now` — lease истёк;
2. процесс, заявивший claim, признан неживым — хотя бы одно:
   - устройство отозвано (`RevokedAt != null`);
   - устройство offline (`LastHeartbeatAt < now - 45s`,
     `DaemonDevice.OnlineWindowSeconds`);
   - устройство heartbeat'ит с **другим** process instance id
     (`ActiveInstanceId <> ClaimInstanceId`).

Сравнение instance id идёт по SQL-семантике: `NULL = NULL` считается
равным, что сохраняет совместимость с legacy-daemon'ами, не шлющими
instance id (оба поля `NULL`).

**Живой daemon с заклинившим циклом намеренно не восстанавливается**: если
тот же процесс всё ещё heartbeat'ит, нет доказательства гибели, и перевод
`Running` в `Pending` мог бы задублировать side effect. Такие команды
разрешаются сами собой при перезапуске процесса (новый instance id) или
при offline устройства (> 45 c без heartbeat'а).

Instance id: клиент генерирует `Guid.ToString("N")` на процесс
(`DaemonInstance`, не персистится) и шлёт его в heartbeat; claim забирает
текущий `ActiveInstanceId` устройства. После рестарта процесса heartbeat
несёт новый id — это и есть детектор рестарта.

## Правило восстановления

Чистая функция `CommandRecovery.Decide(kind, attempts, stale)`:

| Входы | Действие |
|---|---|
| не stale | `None` |
| stale + at-most-once (`CommandDelivery.IsAtMostOnce`) | `Fail` — ошибка `AtMostOnceExpiredError`; повторять запрещено |
| stale + idempotent + `Attempts >= 1` | `Fail` — ошибка `RequeueLimitExpiredError` |
| stale + idempotent + `Attempts < 1` | `Requeue` — `Pending`, `Attempts++`, lease/`StartedAt`/`Error` сбрасываются |

Последствия (effects) применяются **только** при `Fail` и идут через
стандартные `CommandCompletions`:

- `RunEvalTurn` → `EvalRun.Complete(...)` с ошибкой (run завершён как
  проваленный);
- `RunReviewCheck` → `ReviewRun.Fail(...)`;
- подтверждённый сбой `ChatPrompt` → стриминг-сессия переводится в `Idle`
  (`ChatSessionCompletion`), только если сессия в `Streaming`; success
  никогда не декларируется;
- `ApplyOpencode`/`SaveWorkstation`/`ReconcileDesired` — desired-state
  эффекты не применяются на failure (desired state остаётся источником
  истины; reconciliation при следующем heartbeat'е повторит применение).

**Успех без доказательства запрещён.** Recovery никогда не пишет
`Succeeded`; `Succeeded` возможен только по completion POST с
`resultJson`. Recovery пишет только `Failed` (с текстом причины) или
`Pending` (повтор исполнения idempotent-команды).

## Generation protocol

Цель — поздний completion от мёртвой попытки не может завершить строку,
которая уже на новой попытке.

- Текущая попытка строки = `Attempts + 1`.
- Claim-ответ несёт `Generation = Attempts + 1`
  (`WorkstationCommandDto.Generation`).
- Completion POST несёт `generation`; `CompleteCommandHandler` проверяет
  `CommandCompletions.AcceptsGeneration`:

  ```
  generation is null ? row.Attempts == 0 : generation == row.Attempts + 1
  ```

- `null` (legacy-клиент, который не знает generation) принимается только
  для строк, которые никогда не переqueue'ивались (`Attempts == 0`).
- После requeue (`Attempts = 1`) completion со старым generation (1) и
  legacy-`null` отклоняются (`400`, "Command generation mismatch."); новый
  claim несёт generation 2.

Так повторная доставка idempotent-команды безопасна: дублированный поздний
ACK от первой попытки не может задублировать эффекты второй.

## ACK retry на клиенте

`DaemonCommandLoop.CompleteAsync` (Cli):

- тело команды исполняется **ровно один раз** на попытку;
- completion POST ретраится только при **transport failure** (исключение)
  или **5xx**; 4xx финален (терминальный статус или generation mismatch);
- bounded: первый + не более 4 повторных попыток, backoff 1 c × 2
  (1, 2, 4, 8 c) через `DaemonTiming.DelayAsync`;
- при исчерпании ретраев клиент сдает: команда остаётся `Running` с
  истёкшим lease, и серверная recovery-правилка забирает её;
- **никогда** нет клиентского ре-исполнения команды.

Это и есть «acknowledgement retry» из `command-delivery-semantics.md`.

## Worker reaper

`ProjectBeacon.Worker/Services/StaleCommandRecoveryService.cs`:

- `BackgroundService`, тик 60 c, зарегистрирован в `Program.cs`;
- работает **unscoped**: `TenantScope.EnterUnscoped()` +
  `TenantRlsSession.ApplyAsync(db, null, null, unscoped: true)`
  (`WorkstationCommand`/`DaemonDevice` не tenant-filtered, но
  project-scoped эффекты читают через `IgnoreQueryFilters`);
- каждый переход — отдельный guarded `ExecuteUpdateAsync`, который
  **повторяет весь stale-predicate в той же SQL-выборке**: даже если два
  тика (или ручной запуск рядом с тиком) выбрали одну строку, обновится
  ровно тот, кому `WHERE` отдал строку; проигравший получает `0` строк и
  не применяет эффекты. Одновременно Postgres-тест
  (`RecoverStaleCommandsPostgresTests`) гоняет три конкурентных
  `RecoverAsync` и требует ровно один победителя.
- ошибка тика логируется, сервис не падает.

## Сценарии P1.4 (приёмка)

1. **Daemon crash в `Running`.** Lease истекает, device offline ≤ 45 c →
   recovery: idempotent → `Pending` (повторно claimed live daemon'ом),
   at-most-once → `Failed` (at-most-once-with-loss — задуманное поведение).
2. **Процесс завершился, ACK потерян.** Клиент ретраит completion (bounded);
   если все попытки провалились — lease истекает, recovery забирает строку.
   At-most-once при этом **не** исполняется повторно.
3. **Повтор после reconnect.** Requeue → claim с новым instance id →
   новый generation → поздний ACK старой попытки отклонён. Side effects
   не дублируются: idempotent повторно сходится к тому же состоянию,
   at-most-once не повторяется вовсе.
4. **Stale lease/claim не блокирует очередь.** Claim-очередь всегда берёт
   старейшую `Pending` независимо от `Running`-строк; recovery снимает
   блокировку heartbeat-reconciliation: застрявший `ReconcileDesired`
   переводится в `Pending`, и следующий heartbeat не создаёт дубликат
   (in-flight check видит его), а live daemon забирает команду.
5. **AppliedRevision.** Продвигается только успешным completion
   `ApplyOpencode`/`SaveWorkstation`/`ReconcileDesired`
   (`DesiredStateCompletion`) — evidence-based; gap не найден.
6. **Преждевременный success.** Не найден: ни completion, ни recovery не
   пишут `Succeeded` без подтверждения runtime.

## Что НЕ изменено (H4.1–H4.3)

Атомарный claim, `CommandDelivery` (классификация) и versioned
`CommandEnvelope` уже работали и не переосуществлялись. H4.1
(atomic claim) — без изменений; H4.2 (delivery contract) — расширен
только ссылкой на recovery; H4.3 (envelope) — без изменений.

## Миграция

`20261013090525_AddCommandRecoveryFields`: `WorkstationCommands.Attempts`
(int, default 0), `WorkstationCommands.LeaseExpiresAt` (timestamptz, null),
`WorkstationCommands.ClaimInstanceId` (varchar 64, null),
`DaemonDevices.ActiveInstanceId` (varchar 64, null).

## Тесты

- `ProjectBeacon.Domain.Tests/DaemonDeviceTests.cs` — claim/lease/instance,
  requeue (только из `Running`, `Attempts++`, lease сброс), `Fail`/`Succeed`
  чистят lease, heartbeat'ы с instance id (≤ 64 c, blank не меняет).
- `ProjectBeacon.Application.Tests/Devices/CommandRecoveryTests.cs`
  (Sqlite, управляемое `now` — без sleep'ов) — все ветки stale-правила
  (offline, revoked, другой instance, живой daemon не тронут), requeue-лимит,
  at-most-once не переqueue'ивается, эффекты (ChatPrompt → `Idle`,
  RunEvalTurn → eval завершён), generation (legacy null, current, stale,
  после requeue), claim DTO (`Generation`, lease, instance),
  `ReconcileDesired` requeue + heartbeat без дубликата + повторный claim.
- `ProjectBeacon.API.Tests/RecoverStaleCommandsPostgresTests.cs` (Postgres,
  `[Collection("postgres-serial")]`) — конкурентные recovery: ровно один
  победитель, ровно один переход.
- `ProjectBeacon.Cli.Tests/DaemonCommandLoopTests.cs` (fake
  `HttpMessageHandler`, `TaskCompletionSource` вместо sleep'ов) — 500→200
  ретраит с одним исполнением, 4xx без ретра, transport failure ретраится,
  ретраи исчерпаны (5 попыток, без ре-исполнения), generation в body,
  instance id в heartbeat.
