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
- **stale-правило** переводит `Running` в recovery state при истёкшем lease,
  если гибель процесса доказана (устройство отозвано, offline > 45 c, или
  heartbeat от другого процесса) **или** lease истёк давно — long-stale:
  более 30 минут (`LongStaleGraceMinutes`) назад, даже если тот же процесс
  жив и heartbeat'ит;
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
| `Running` → `Pending` | recovery (`CommandRecovery.RecoverAsync`) | stale-правило (любой ярус) + idempotent вид + `Attempts < 1`; `Attempts++`, lease и `StartedAt` сбрасываются |
| `Running` → `Failed` | recovery | stale-правило (любой ярус) + (at-most-once вид или `Attempts >= 1`) |

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
  (см. stale-правило). Long-stale-ярус — тоже не продление lease: это
  разрешение давно истёкшего lease, а не новое время жизни.

## Stale-правило

`Running`-строка является **stale** (подлежит восстановлению) только если
выполняются оба условия:

1. `LeaseExpiresAt < now` — lease истёк;
2. хотя бы один из двух ярусов:
   - **ярус 1 — процесс мёртв**, хотя бы одно:
     - устройство отозвано (`RevokedAt != null`);
     - устройство offline (`LastHeartbeatAt < now - 45s`,
       `DaemonDevice.OnlineWindowSeconds`);
     - устройство heartbeat'ит с **другим** process instance id
       (`ActiveInstanceId <> ClaimInstanceId`);
   - **ярус 2 — long-stale**: `LeaseExpiresAt < now - 30 мин`
     (`CommandRecovery.LongStaleGraceMinutes`) — lease истёк давно, и ярус
     работает **даже когда тот же процесс жив и heartbeat'ит**.
     Обоснование: 15-минутный lease многократно превышает клиентские
     лимиты всех видов (LLM/eval — 180 c, review/winget — 120 c, остальные
     — секунды), bounded-ретраи completion (~15 c) к этому моменту
     исчерпаны, поэтому в полёте не может быть никакого легитимного
     исполнения: попытка потеряна (потерян ACK или цикл заклинил).
     Худшее время разрешения: 45 мин (lease + grace) плюс один 60-секундный
     тик Worker.

Все три SQL-точки (запрос кандидатов и оба guarded update) строят строки
через **один** предикат `CommandRecovery.StaleRows` — ложные кандидаты
невозможны, и конкурирующие тики не дают двойного перехода.

Сравнение instance id идёт по обычной SQL-семантике `=`: `NULL = NULL`
**не** считается равным (EF Core переводит `==` в `=`, а не в
`IS NOT DISTINCT FROM`). Для legacy-daemon'ов, не шлющих instance id (оба
поля `NULL`), это означает, что ярус 1 признаёт строку stale сразу после
истечения lease — без 30-минутного grace. Это безопасно: клиентские лимиты
всех видов (≤ 180 s) многократно ниже 15-минутного lease, поэтому lease не
может истечь, пока живое исполнение в полёте; потерянный ACK решается по
тому же пути, что и для daemon'ов с instance id, а поздний дублированный
ACK отклоняется generation guard и терминальным статусом.

**Живой daemon с заклинившим циклом теперь разрешается**: внутри
30-минутного long-stale grace строка не трогается (доказательства гибели
нет, а requeue мог бы задублировать side effect), после grace ярус 2
переводит idempotent-команду в `Pending` (не более одного раза) или
завершает at-most-once `Failed`. Прежний путь (рестарт процесса — новый
instance id, или offline устройства) по-прежнему работает как ярус 1.

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

Ярусы stale-правила влияют только на флаг `stale`; `Decide` от них не
зависит. Лимит суммарный: idempotent-команда переисполняется не более
одного раза — повторный stale после requeue (любым ярусом) даёт `Fail`.

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
- при исчерпании ретраев клиент сдает: команда остаётся `Running`;
  серверная recovery разрешает её по stale-правилу — ярус 1 (доказанная
  гибель процесса) или ярус 2 (long-stale, после 30-минутного grace);
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
  **повторяет весь stale-predicate в той же SQL-выборке** (один общий
  предикат `CommandRecovery.StaleRows`, включая long-stale-ярус): даже если
  два тика (или ручной запуск рядом с тиком) выбрали одну строку, обновится
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
7. **Живой daemon, потерянный completion (long-stale).** Тот же процесс
   жив и heartbeat'ит, но completion не доставлен (или цикл заклинил).
   Внутри 30-минутного grace строка не трогается; после grace ярус 2:
   idempotent → `Pending` (не более одного раза, затем `Failed`),
   at-most-once → `Failed`. Поздний/дублированный ACK утерянной попытки
   отклонён: generation guard — после requeue, терминальный статус — после
   `Failed` (включая legacy `null` generation).

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
  `ReconcileDesired` requeue + heartbeat без дубликата + повторный claim,
  long-stale-ярус (живой daemon: внутри grace не тронут, после grace
  requeue / `Failed` с эффектом, лимит после requeue, поздний completion к
  терминальной строке отклонён — legacy `null` и старый generation).
- `ProjectBeacon.API.Tests/RecoverStaleCommandsPostgresTests.cs` (Postgres,
  `[Collection("postgres-serial")]`) — конкурентные recovery: ровно один
  победитель, ровно один переход (оба яруса); long-stale на живом daemon:
  не восстановлен внутри grace, восстановлен после; конкурентные тики —
  один победитель; at-most-once `Failed` + поздний completion отклонён;
  requeue → claim с generation 2 → `Succeeded`, дублированный completion
  отклонён, `AppliedRevision` продвинулся ровно один раз.
- `ProjectBeacon.Cli.Tests/DaemonCommandLoopTests.cs` (fake
  `HttpMessageHandler`, `TaskCompletionSource` вместо sleep'ов) — 500→200
  ретраит с одним исполнением, 4xx без ретра, transport failure ретраится,
  ретраи исчерпаны (5 попыток, без ре-исполнения), generation в body,
  instance id в heartbeat.
