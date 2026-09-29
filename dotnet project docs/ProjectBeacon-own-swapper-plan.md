# ProjectBeacon — свой model-swapper (замена внешнего llama-swap)

## Принципы неразрушения

1. Публичный контракт `ClientLlamaSwap` не меняется: `TickAsync(yaml, port, binPath, ct)`,
   `ReloadAsync`, `UnloadAsync`, `Status`, `StatusWire()`. Всё выше по стеку
   (`WorkstationDaemon.ExecuteAsync`, `DeviceLlamaSwapProxy`, `WorkstationCommandKind.ReloadProxy/UnloadProxy`,
   heartbeat/`ProbeJson`) не знает и не должно узнать о замене реализации.
2. Формат YAML-конфига (`LlamaSwapConfigGenerator`, секции `swap:`/`concurrent`) не меняется —
   он остаётся контрактом между сервером (генерирует конфиг) и клиентом (исполняет его).
3. `SkipRealProcess`-флаг для тестов сохраняется в новой реализации — существующие
   `ClientLlamaSwapTests` (если такое имя есть) не переписываются, только дополняются.
4. Внешний бинарь `llama-swap` не удаляется из проекта как опция — переключение
   "свой swapper / внешний llama-swap" делается конфигом (`WorkstationSettings`), чтобы можно было
   откатиться без правок кода.

---

## Шаг 1. Абстракция backend'а внутри `ClientLlamaSwap`

**Не трогать:** сигнатуры публичных методов `ClientLlamaSwap`; `LlamaSwapConfigGenerator`;
всё серверное (`DeviceLlamaSwapProxy`, `EnqueueCommandHandler`).

**Контекст.** Сейчас `EnsureProcess`/`KillProcess` внутри `ClientLlamaSwap.cs` управляют одним
внешним процессом `llama-swap`, который сам решает, какую модель держать в VRAM.

**Меняем.** Внутри `ClientLlamaSwap` — новый внутренний слой `IModelBackend` (Start/Stop/HealthCheck/Endpoint/
VramFootprintMb/Status), реализация `LlamaServerBackend` — прямой запуск `llama-server` (не `llama-swap`)
с параметрами из `LlamaSwapModelSpec`. Разбор YAML, который сейчас генерируется под `llama-swap`,
не меняется — `LlamaServerBackend` читает из уже распарсенных `LlamaSwapModelSpec`, а не из самого YAML,
чтобы не тащить парсер конфига в две реализации.

**Подшаги.**
1. Интерфейс `IModelBackend` + `LlamaServerBackend` — новые файлы в `ProjectBeacon.Cli/Client/`.
2. Флаг в `WorkstationSettings` (`UseOwnSwapper: bool`, default `false`) — additive поле.
3. В `ClientLlamaSwap` — ветвление: `UseOwnSwapper == true` → новый путь через `IModelBackend`,
   иначе прежний `Process`-обёртка над внешним `llama-swap` без изменений.

**Acceptance.** При `UseOwnSwapper=false` поведение и тесты идентичны текущим. При `UseOwnSwapper=true`
`llama-server` поднимается напрямую, `StatusWire()` отдаёт тот же формат, что и раньше.

## Шаг 2. FSM, VRAM-семафор, crash recovery

**Не трогать:** формат `swap:`/`concurrent` в конфиге — семантика групп сохраняется.

**Меняем.**
- Конечный автомат `Idle → Starting → Ready → Stopping → Faulted` на каждый `LlamaServerBackend`.
- Проверка свободной VRAM (обёртка над `nvidia-smi` или аналогом) перед `Start`; повторная проверка
  после `Stop` перед разрешением следующей загрузки (драйверная задержка освобождения памяти).
- Семафор per swap-group: модели из одной `swap:`-группы (из конфига) — не более одной `Ready`
  одновременно; модели с `concurrent: true` — без этого ограничения. Конкурентные запросы к разным
  моделям одной группы — в очередь, не в ошибку занятости.
- Crash recovery: мониторинг кода выхода процесса, restart с exponential backoff и лимитом попыток;
  после лимита — `Faulted` без бесконечного цикла.

**Подшаги.**
1. FSM-класс, unit-тесты на переходы без реального процесса (через `SkipRealProcess`-аналог).
2. VRAM-чек — новый небольшой класс, мокается в тестах.
3. Семафор по группам — извлечение групп из уже сгенерированного `LlamaSwapModelSpec`, не из YAML напрямую.
4. Crash recovery — тест через `SkipRealProcess` с симуляцией ненулевого exit code.

**Acceptance.** Две модели одной swap-группы никогда не в `Ready` одновременно в тесте; после
`kill -9` симулированного процесса — restart с backoff, не более N попыток.

## Шаг 3. Graceful disconnect и совместимость с существующим heartbeat

**Не трогать:** `RunHeartbeatAsync`, формат `ProbeJson`.

**Меняем.** Перед сном/шатдауном — явный `Stop` всех `Ready` backend'ов (через уже существующий
`DisposeAsync` в `ClientLlamaSwap`, который уже вызывает `KillProcess` — расширяется на новый путь).
`ProbeJson` для `UseOwnSwapper=true` заполняется из состояния `IModelBackend`, в том же формате,
что сейчас парсит `DeviceLlamaSwapProxy.ParseProbe` на сервере — **формат JSON не меняется**,
только источник данных внутри клиента.

**Acceptance.** `DeviceLlamaSwapProxy.ParseProbe` на сервере не требует изменений; статус в панели
выглядит одинаково независимо от `UseOwnSwapper`.

## Шаг 4. Наблюдаемость

**Не трогать:** существующий `Log()` в `WorkstationDaemon`.

**Меняем.** В `LlamaServerBackend`/FSM — логирование каждого старта/стопа/свопа (длительность,
успех/провал) через тот же `Log()`-канал, что уже льётся в панель. Ничего нового на сервере не нужно —
эти строки уже идут в существующий лог-стрим устройства.

**Acceptance.** В существующем UI-логе устройства видны события свопа с длительностью, без новых
эндпоинтов.

---

## Порядок

Шаги строго последовательны (1 → 2 → 3 → 4), каждый — небольшой и полностью откатываемый флагом
`UseOwnSwapper`. Внешний `llama-swap` остаётся рабочим путём по умолчанию до тех пор, пока свой
swapper не пройдёт шаги 1–4 на реальном железе.

## Что не входит в этот план (уже есть, трогать не нужно)

Session-store, heartbeat/poll-канал, токены устройств, чат через OpenCode, UI статуса устройства —
всё это уже реализовано и покрыто отдельно в `ProjectBeacon-forward-plan.md`. Этот документ касается
только внутренней замены `ClientLlamaSwap`.
