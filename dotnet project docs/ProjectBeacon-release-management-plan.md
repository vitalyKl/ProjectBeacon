# ProjectBeacon — управление релизами и версиями: исправленные замечания и план (24 нояб 2026)

Дополняет предложение ChatGPT «универсальная система Release Management» и мои предыдущие замечания в чате, с учётом комментария владельца. Репозиторий: `vitalyKl/ProjectBeacon`, `main` на 24 нояб.

## Границы

- **Прочитано:** `Milestone.cs`, `.github/workflows/release.yml`, `GitSyncService.cs`, `DaemonHeartbeatLoop.cs`, `ProjectGitSyncPreflight.cs`, `LaunchSessionHandler`, `StartPipelineHandler`, `PipelineSupport`, описания PR #169–#194. Документ ChatGPT изучен целиком.
- **Не прочитано:** реализация `git_publish`/`git_pr` (`AgentTaskGitHubPullRequestOperations.cs`, `AgentTaskGitOperations.cs`), `ci.yml`, настройки GitHub (защита тегов, правила окружения `production`), `Directory.Build.props`, UI Roadmap. Привязка задачи к станции в прочитанных файлах не обнаружена.
- Сборка и тесты не запускались. Размеры и порядок срезов моя оценка, не измерение.

## 1. Цели владельца (24 нояб)

| # | Цель | Следствие |
|---|---|---|
| V1 | Уйти от версионирования «где-то в проекте, неясно кто ему следует» | Версия и её правила становятся объектом Beacon с проверкой, а не соглашением |
| V2 | Дать агентам встроенный инструмент самоконтроля версии | Структурные инструменты и серверная проверка, а не инструкция в промпте |
| V3 | Агенты нормально работают с изменениями из GitHub | Автоматическая синхронизация (промпт `claude/prompt-git-autosync.md`) и сверка с удалённым состоянием |
| V4 | Не превращать пользователя в оператора Git-кнопок | Ручное остаётся для подтверждения опасных действий, всё прочее детерминированная автоматика |

## 2. Что я проверил, и что это меняет

| Факт | Значение для плана |
|---|---|
| `Milestone` содержит только имя, описание, порядок, `ClosedAt`, `Revision`; версии, ветки, SHA нет | Согласен с ChatGPT: Milestone не превращать в Release |
| Выпуск запускает push тега `v*` (`release.yml`): тесты, образ, Trivy, клиентские архивы, создание GitHub Release, деплой в окружение `production` по SSH, сверка `/v1/version` с версией и SHA | Тег, GitHub Release и деплой у вас одна цепочка: Beacon не должен дублировать сборку, а должен создать тег на проверенном SHA и наблюдать результат |
| Тесты в `release.yml` перезапускаются на теге (`ci.yml` на теги не срабатывает) | Зелёный CI на ветке не гарантирует зелёный релиз; после тега провал нужно уметь записать как «публикация не удалась» |
| Деплой-задача использует `environment: production` | В GitHub к окружению можно добавить обязательных рецензентов; возможно, готовый ручной шаг подтверждения (настройки не видел) |
| Версия четырёхкомпонентная (0.7.5.x), её повышают отдельными PR (#169, #172) | Политика версии должна поддерживать любое число компонентов; SemVer только значение по умолчанию |
| Посадка (GitLand) отклоняет правки манифестов сборки как защищённых путей | Файл версии нельзя менять обычной задачей; нужна отдельная серверная операция |
| `GitSync` и `repo_sync` ручные; фонового цикла нет; статус берётся из последней команды | Часть V3 закрывается промптом AutoSync |

## 3. Исправления к моим предыдущим замечаниям

| Что я говорил | Исправление |
|---|---|
| «Beacon только создаёт тег и наблюдает за workflow» | Верно для сборки и деплоя, но этого мало. Владелец хочет, чтобы Beacon владел версией: считал следующую, готовил файл версии и проверял согласованность. Наблюдение за workflow остаётся, но к нему добавляются политика версии и операция `ReleasePrepare` |
| «Политика версии должна уметь N чисел и путь к файлу» | Недостаточно. Нужен общий источник версии «файл и селектор» (XML, JSON, TOML, регулярное выражение) и шаблон тега: это покрывает .NET, npm, Python и другие стеки без отдельного адаптера на каждый |
| «Агент пишет файл версии через ReleasePrepare» | Агент файл не пишет вообще. Он только объявляет влияние (`versionImpact`) своей задачи; файл меняет серверно проверенная операция на станции |
| «Validating = проверка зелёного CI» | Нужно разделить: (а) CI на SHA кандидата перед тегом, (б) результат workflow выпуска после тега. Они могут расходиться |
| «Опасение про CI с секретами менее серьёзно» | Подтверждено только для `release.yml` (запускается тегом). `ci.yml` на push веток `beacon/*` я не читал; остаётся проверить, есть ли в нём секреты |
| «Фаза закрепления задачи за станцией реализована» (из документа ChatGPT) | В прочитанных мной файлах (`LaunchSessionHandler`, `ProjectGitSyncPreflight`) выбор станции при каждом запуске по самому свежему heartbeat, привязки нет. Нужно сверить |

## 4. Оценка предложения ChatGPT

**Принять:**
- Release отдельная сущность поверх Milestone, необязательная связь `Milestone.ReleaseId`.
- Неизменяемый состав кандидата; доказательство включения это запись о посадке, а не PR или push.
- Hotfix от развёрнутого SHA; forward-port обязателен.
- Универсальность: никакого «если репозиторий равен ProjectBeacon», настройки на проект.
- Агентам нельзя менять официальную версию и создавать теги выпуска.

**Изменить или сократить:**
- **Девять состояний жизненного цикла избыточны.** Достаточно `Planned → InProgress → Candidate → Published` и `Cancelled`. «Validating», «Ready», «Publishing» это состояния проверок и попыток публикации. Мы уже видели, как лишние жёсткие барьеры ломают базовую работу.
- **Не строить адаптеры без потребителей.** Оставить интерфейсы `IVersionSource` и `IReleasePublisher` с одной реализацией каждый: «файл и селектор» и «тег плюс наблюдение за GitHub Actions». Остальные стеки подключаются конфигурацией или появляются при втором реальном потребителе.
- **Productization нельзя оставлять 8-м этапом.** Режем вертикально: каждый срез выпускается в production за флагом проекта и сразу виден в интерфейсе.
- **Не поручать LLM проверку ворот.** Готовность, соответствие версий, CI и SHA проверяет детерминированный код на сервере. Агенту остаются суждения: влияние на версию, release notes, анализ конфликтов.
- **Forward-port это обычная задача**, автоматически создаваемая после Hotfix, с обычными Review и посадкой, а не особая машина состояний.
- **Не вводить Release Coordinator как новую роль на первом этапе.** Использовать режим существующего Planner с набором инструментов выпуска.

## 5. Версионирование под управлением Beacon (ответ на V1 и V2)

### 5.1 Модель

- **Политика версии проекта** (`VersionPolicy`): схема (числовая с N компонентами, SemVer или календарная), список источников версии (путь, формат, селектор), шаблон тега (`v{version}`), правила предварительных версий.
- **Состояние версии:** последняя выпущенная, следующая запланированная, происхождение.
- **Авторитет.** Решения о версии принимает Beacon (БД). Файлы версии в репозитории это **проекция** этих решений. Чтобы CI мог проверять без доступа к серверу, `ReleasePrepare` записывает в репозиторий маленький файл (например `.beacon/release.json`: политика, версия, идентификатор выпуска) и коммит с трейлером `Beacon-Release: <id>`.

### 5.2 Как агенты следят за версией сами

| Инструмент | Что делает |
|---|---|
| `version_check` | Только чтение. Возвращает политику, текущую и следующую версию, объявленные влияния задач, расхождения (файл против политики, тег против файла, развёрнутая версия против выпущенной, если задан адрес сверки вроде `/v1/version`) |
| `version_impact` | Агент объявляет влияние задачи (`none`, `patch`, `minor`, `major`) с обоснованием при завершении подзадачи. Файлов не меняет |
| `release_context` | Только чтение. Релиз, целевая версия, целевая ветка, база, обязательные проверки |

- **Ревьюер подтверждает объявленное влияние** вердиктом; расхождение это замечание ревью, а не молчаливая правка.
- **Версия выпуска считается кодом:** максимум по влияниям входящих задач с применением политики. Неоднозначные и несовместимые изменения требуют решения человека.
- **По умолчанию это предупреждение, а не запрет**, чтобы не повторить ошибку с жёсткими барьерами GitSync.

### 5.3 Защита файла версии

- Обычный Actor менять файлы версии не может (защищённые пути посадки уже это делают для манифестов; список берётся из политики проекта, а не из названия файла).
- Единственный путь изменения это `ReleasePrepare`: коммит на ветке выпуска, который проверяют сервер и станция (изменены только настроенные файлы, версия равна назначенной).
- Шаг CI (его создаёт Beacon) падает, если файл версии менялся не коммитом с трейлером выпуска или не совпадает с `.beacon/release.json`.

### 5.4 Тег и публикация

- Создание тега `v*` это граница опасности: он запускает деплой в production. Агент его не создаёт. По умолчанию создание тега подтверждает человек; на стороне GitHub стоит закрепить защиту тегов `v*` и обязательных рецензентов окружения `production` (проверить, настроено ли).
- Beacon создаёт тег на SHA кандидата, затем **наблюдает** за запуском workflow и записывает результат: успех, провал тестов, провал сканирования, провал деплоя, расхождение версии после деплоя.
- Опубликованный и неудачный тег не переписываются; исправление это новая версия.

## 6. Агенты и GitHub (ответ на V3)

1. **AutoSync** (готов промпт): фоновый fetch и безопасный fast-forward без кнопок.
2. **Синхронизация тегов и веток выпуска:** небольшое расширение AutoSync (отдельная задача), потому что выпуск зависит от тегов.
3. **Сверка после слияния на GitHub.** После fetch Beacon сам определяет, что ветка задачи уже входит в `origin/<база>`, закрывает запись о посадке и обновляет задачу. Там, где есть remote, авторитетной считается ветка на GitHub, локальная следует за ней.
4. **Чтение удалённого состояния (`forge_read`):** статус CI и запусков workflow, состояние PR, комментарии ревью. Токен хранится на станции, модель видит только ограниченный результат.
5. **Обратная связь по PR:** комментарии ревью на GitHub превращаются в подзадачи или заметки к задаче, а не просьбой к вам пересказать.
6. **Реакция на ушедшую базу:** контракт сессии сообщает, что база задачи отстаёт, агент вызывает `git_integrate`.

## 7. План по вертикальным срезам

Каждый срез отдельный PR или серия, за флагом проекта, с интерфейсом, локализацией, RLS и тестами.

| Срез | Содержание | Результат для пользователя |
|---|---|---|
| **S0** | AutoSync (промпт готов), синхронизация тегов | Репозиторий сам в курсе удалённого |
| **S1** | `VersionPolicy`, `version_check`, `version_impact`, проверка ревьюером, интерфейс политики и страница «Версия проекта», серверная защита файлов версии. **Без сущности Release** | Версия перестаёт быть соглашением; агенты контролируют её сами |
| **S2** | `Release`, `Milestone.ReleaseId`, Roadmap UI, релизный контекст в промптах, целевая ветка релиза для посадки и PR | Работа сгруппирована по выпускам |
| **S3** | `ReleasePrepare`, `.beacon/release.json` и шаг CI, тег по подтверждению, наблюдение за workflow, запись публикации | Версию больше не повышают вручную |
| **S4** | Сверка с GitHub после слияния, `forge_read`, обратная связь по PR | Агенты работают с изменениями с GitHub |
| **S5** | Hotfix от развёрнутого SHA, forward-port как обычная задача | Срочные исправления |
| **S6** | Режим Planner для выпусков, продуктовые функции, onboarding, независимый тестовый проект с другой схемой версии (например `package.json`) | Функция доступна другим пользователям |

Порядок: S0 и S1 можно делать параллельно, S1 даёт главную ценность («версия под контролем») раньше всего остального.

## 8. Что проверить до начала

1. Привязка задачи к станции: есть ли в `main` (не нашёл в прочитанных файлах).
2. Где определяется база PR в `git_publish`: должна быть одна функция, иначе релизный контекст придётся внедрять в нескольких местах.
3. Настройки GitHub: защита тегов `v*`, рецензенты окружения `production`, права `ci.yml` и секреты на push веток `beacon/*`.
4. Версия git на станциях (для Branch нужен 2.38+).
5. Где реально живёт версия сейчас (`Directory.Build.props`, возможно в нескольких местах) и кто её повышает.

## 9. Открытые решения владельца

1. Создание тега: по умолчанию с подтверждением человека (рекомендую) или автоматически при выполнении условий.
2. Объявление влияния на версию: предупреждение (рекомендую) или блокирующее условие.
3. Модель авторитета: БД Beacon решает, репозиторий хранит проекцию с проверкой в CI (рекомендую) или версия остаётся только в репозитории.
4. Где авторитетна посадка при наличии remote: GitHub (рекомендую) или локальный GitLand.
5. Релизы обязательны для всех проектов или это необязательный режим проекта (рекомендую второе, иначе ежедневная работа замедлится).

## 10. Что не следует делать (дополнение к списку ChatGPT)

- Не ставить периодические команды `GitSync` в очередь: они включают барьер запуска.
- Не делать предупреждения блокирующими условиями по умолчанию.
- Не доверять LLM проверку готовности и соответствия версий.
- Не строить адаптеры под несуществующих потребителей.
- Не публиковать теги и не менять файл версии из обычной сессии агента.
- Не откладывать интерфейс и production на последний этап.

## 11. Что не проверено

Сборка и тесты; реализация `git_publish` и `git_pr`; настройки GitHub; `ci.yml`; текущий вид Roadmap и страницы проекта; точный состав защищённых путей посадки; расхождения между документом ChatGPT и фактическим кодом, которые я не успел сверить.

## Актуализация на начало реализации — 24 ноября 2026

Исходный текст плана выше сохранён без переписывания. Ниже зафиксированы уточнения после проверки актуального `main`.

- Базовый commit: `22a1a9e559a97124f939b6da2de2e4f9612f4d94` (commit #228, ветка `main` на момент старта).
- Документационная ветка: `docs/release-management-plan`.
- Рабочая ветка реализации: `feat/release-management`, создаётся поверх документационной ветки, поэтому сам план присутствует и в ней.
- PR #194 уже включён в историю проекта: безопасные операции `git_publish`/`git_pr` доступны, но прямое слияние PR не входит в контракт этого PR; в его описании GitLand остаётся исполнителем посадки. Повторно эти операции не реализуем.
- PR #227 уже включён в `main`: у задачи закрепляется одна workstation для persistent task branch workspace и review/landing proofs. Пункт 8.1 исходного текста более не является открытым блокером.
- PR #228 уже включён в `main`: для GitSync реализованы PendingSince lifecycle, истечение срока и безопасная отмена ожидающей команды. Периодический AutoSync всё равно требует отдельной реализации и не должен превращаться в бесконтрольное добавление команд в очередь.
- Ветка `feat/git-autosync` совпадает с `main` (0 ahead / 0 behind). В текущем дереве `main` не найден отдельный файл `claude/prompt-git-autosync.md` или фоновый AutoSync loop; это проверяется в реализации, а не считается выполненным по одному имени ветки.
- `ProjectBeacon.Cli/Client/GitSyncService.cs` выполняет контролируемый fetch/fast-forward, но текущие fetch-команды используют `--no-tags`; синхронизация тегов не реализована этим путём.
- В текущем дереве не обнаружены сущности/контракты `Release` или `VersionPolicy`. `Milestone` остаётся самостоятельной roadmap-сущностью.
- `Directory.Build.props` задаёт `0.7.5.8`, то есть четыре числовых компонента. При этом production validation в `.github/workflows/release.yml` пока допускает только три компонента (`X.Y.Z`) до суффикса. Это конкретное расхождение нужно устранить до включения управляемой публикации.
- Настройки tag protection / rulesets и reviewers для GitHub Environment `production` не считаются проверенными на старте; их проверка остаётся отдельным пунктом перед включением публикации.

### Рабочее правило

Все продуктовые изменения по этому плану вносятся только в `feat/release-management`. Ветка `docs/release-management-plan` содержит зафиксированный план и является его отдельной документационной точкой. Новые завершённые этапы, результаты проверок, отклонения от плана и блокеры отражаются в корневом `DEVELOPMENT_LOG.md` рабочей ветки. Ни одна из этих веток автоматически не сливается в `main`.


## Актуализация реализации — 25 ноября 2026

План остаётся базовым требованием. Фактическая разработка ведётся отдельно в `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230); документационная ветка не сливается автоматически в `main`.

### Текущий статус реализации

- В PR #230 присутствуют продуктовые срезы S0–S6: фоновый AutoSync и синхронизация тегов; VersionPolicy и VersionImpact; Release и связь с Milestone; подготовка неизменяемого кандидата, проверка посадки и публикация; GitHub-aware сверка удалённой посадки; hotfix/forward-port; release-aware инструменты Planner.
- Настройки и UI включены в существующие страницы проекта и Roadmap; Release Management выключен по умолчанию на проекте. Изменения включают API, модель/миграции, RLS/authorization tests, локализацию и UI. Наличие кода не означает, что срезы приняты: требуется зелёный CI на точной вершине ветки и проверка оставшихся архитектурных/безопасностных условий.
- Закрепление task/workspace за станцией и жизненный цикл GitSync уже были приняты в main (PR #227 и #228); повторно не реализуются.

### Проверка CI на 25 ноября

- Для вершины `079ffaf7aad9427a1a26f4962e4b1fa41dbc4efa` CI [#45560509847](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45560509847) выполнил Restore, Build, проверку EF model drift и семь тестовых модулей. Ошибка на всех трёх платформах была только в `dotnet format`: лишний уровень отступа в `ReleasePublishExecutor.cs`, строки 481–499.
- Исправление отступов внесено отдельным коммитом; подробности и точные SHA записаны в `DEVELOPMENT_LOG.md` рабочей ветки. Новый запуск CI на пост-fix head обязателен; результат предыдущего запуска не переносится на новый commit.
- Linux CI lane является основной проверкой PostgreSQL; Windows/macOS явно пропускают три PostgreSQL persistence tests из-за отсутствия сервисного PostgreSQL, и эти пропуски должны оставаться видимыми в отчёте.

### Блокеры перед принятием и публикацией

1. **CI:** дождаться/проверить свежий CI на точной post-fix вершине; устранить любые новые ошибки.
2. **Landing authority:** текущая GitHub reconciliation только фиксирует факт уже выполненного merge. Она не даёт агенту самостоятельно завершить проверенный GitHub PR. Нужен один server-authorized remote merge workflow с проверкой base/head, required checks, reviews и branch protection; обход защиты запрещён. Локальный GitLand не должен стать второй конкурирующей authority.
3. **Публикационная защита:** tag protection/rulesets для `v*` и required reviewers GitHub Environment `production` остаются непроверенными через текущую интеграцию. До подтверждения настроек не создавать production tag и не запускать deploy.
4. **Полное product acceptance:** отдельно проверить на пользовательском проекте, что Release UI, настройки политики, разрешения, RLS, локализация, агентские инструменты и все состояния отказа согласованы и работают без ручного вмешательства пользователя там, где это безопасно.

### Следующий шаг

Сначала добиться зелёного CI на точной post-fix вершине PR #230. После этого продолжать реализацию и тестирование единого GitHub-authoritative landing path. План не меняет исходные продуктовые границы: без SaaS Control Plane, биллинга и обязательного Release Management для всех проектов.


## Дополнение — согласование валидаторов политики версии и CI (25 ноября 2026)

В ходе ревизии S1/S3 обнаружено, что API принимал часть XML XPath и календарных форматов, которые tag-triggered release workflow не мог обработать тем же способом. Кроме того, Beacon хранит .NET regex syntax, а Python release gate изначально не понимал именованные группы .NET.

Исправления находятся в рабочей ветке `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230):

- XML: policy validation и Python release gate теперь согласованы на переносимом XPath location-path subset: абсолютные пути (`/Project/PropertyGroup/Version`) и descendant paths (`//Version`). Предикаты, атрибуты, функции, axes, wildcard и namespace prefixes отклоняются при сохранении policy.
- Calendar: ограничен переносимым набором токенов `yyyy`, `MM`, `dd`, необязательными `HH`, `mm`, `ss` и literal separators `.`, `-`, `/`, `_`, пробел, `:`, `T`, `Z`. Остальные .NET custom date tokens отклоняются до ReleasePrepare.
- Regex: release gate преобразует обычные .NET named groups, named backreferences и абсолютный конец строки в соответствующий Python syntax.

Domain/Python tests добавлены. Это сознательное ограничение общего переносимого подмножества, а не заявление о поддержке полного XPath/.NET Regex/.NET DateTime grammar в Python workflow. Полная проверка parity для TOML и расширенного regex dialect остаётся отдельным пунктом.

**Состояние проверки:** focused Python smoke check прошёл на абсолютном/descendant XML path и .NET named group/backreference. CI [#45580698958](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45580698958) проверяет предыдущий head и не покрывает эти коммиты. Текущий implementation head после обновления журнала: `9878489da6c71a8f337c7cab8989524666298b68`. Срез не считается принятым до нового CI на точном head.

**Оставшиеся блокеры:** (1) GitHub-authoritative landing пока read-only и не умеет инициировать server-authorized merge с сохранением branch protection; (2) tag rulesets `v*` и required reviewers для Environment `production` не подтверждены; (3) после CI требуется полный user-project acceptance, включая localization/RLS/error states. Не создавать production tag и не запускать deployment до снятия блокера (2).


## Дополнение — результаты кроссплатформенного CI и hardening (25 ноября 2026)

### Подтверждение предыдущего среза

CI [#45582326452](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45582326452), run 1636, успешно завершился на точном implementation commit `9878489da6c71a8f337c7cab8989524666298b68`:

- Linux: Build, EF model check, Python release-manifest tests, все семь .NET test modules, TRX guards и `dotnet format` — success.
- Windows: Build, EF model check, все семь .NET test modules и `dotnet format` — success.
- macOS: Build, EF model check, все семь .NET test modules и `dotnet format` — success.

Это подтверждает XML/Calendar selector parity fixes и добавленные к ним Domain/Python regression tests. Не переносить этот результат на последующие коммиты.

### Последующие изменения, требующие отдельного CI

После проверенного commit реализовано:

- Python Regex version selector исполняется в отдельном изолированном child process с fail-closed timeout 2 секунды, без shell. Это защищает tag-triggered release workflow от неограниченного catastrophic backtracking.
- subprocess использует явный UTF-8, чтобы поведение не зависело от локали Windows/macOS.
- Workflow error annotation `::error::` выводится только при прямом запуске release-gate script; импортированные unit tests не создают ложные красные аннотации для ожидаемых негативных сценариев.
- Добавлены unit tests на обработку timeout и отсутствие workflow-команд в stderr unit tests.
- Project settings получили точные локализованные (en/ru/de/ja/zh) подсказки для поддерживаемых XML/Calendar форматов и требований к source перед Candidate. Пустой список source остаётся допустимым для предварительного планирования Release, но Prepare явно требует минимум один source.
- Calendar-поле показывает выделенную локализованную подсказку.

Текущая реализационная вершина на момент обновления плана: `3eed9283cb5997ada0d31138903e27fdf922979b` (журнал последнего состояния); source/test head перед journal-only commit: `65f6eae80afd9b947b63ac6476dfdd7958ee1810`. Новый CI должен быть запущен для полного текущего head.

### Release protection: что установлено, а что неизвестно

- GitHub API для repository rulesets, включая `includes_parents=true`, вернул пустой список.
- Чтение классической branch protection недоступно текущей интеграции (403); legacy tag protection и настройки GitHub Environment `production` коннектор не предоставляет.
- Следовательно, наличие классической защиты `v*` и required reviewers для `production` не установлено. Это остаётся ручным административным блокером. Production tag/deploy запрещён до проверки через GitHub Settings.

### Остаток

1. Запустить CI на актуальной вершине ветки и отдельно проверить timeout/encoding/локализационные изменения.
2. Разработать один server-authorized GitHub landing executor, который уважает branch protection, reviews и required checks. Текущая reconciliation функция read-only и может только записать уже выполненное внешнее merge.
3. Администратору проверить legacy tag protection, branch protection и reviewers Production Environment.
4. Продолжить user-project acceptance: права, RLS, локализация, ошибки workflow и настройки проекта.


## Дополнение — GitHub-authoritative landing и исправление CI (26 ноября 2026)

В актуальной реализации PR #230 добавлен серверно-авторизованный remote merge path:

- Только доверенная команда `GitLand Execute`, поставленная сервером после LandingAttempt/Review proof, может инициировать GitHub merge. Агентская команда `git_pr` не получила действие `merge`; Actor не может самостоятельно вызвать merge.
- Для GitHub-origin executor повторно строит точный Candidate; проверяет task worktree, review base, origin identity, уникальный PR, точный PR head, approvals, mergeability, required checks и clean merge state; перед mutation ещё раз сверяет PR/task/base.
- Вызов GitHub PR merge API закреплён за ожидаемым SHA (`sha=...`) и не использует admin/bypass. GitHub branch protection остаётся финальным серверным gate.
- После merge проверяются статус PR, merge commit, достижимость из обновлённой удалённой target-ветки и неизменность reviewed task head. Затем GitLand сверяет tree с immutable Preview и исполняет post-merge check. Неопределённый результат остаётся recoverable; локальный GitLand не переписывает GitHub protected refs.
- Revert GitHub-origin намеренно не реализуется через force/ref mutation: исправление должно быть новой reviewed forward-fix задачей.

CI [#45626583688](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45626583688) на HEAD `d05a71b9` выявил две одинаковые на трёх ОС тестовые ошибки:
1. Allowlist ожидал 8, в `DeviceRemoteLandingReconciliationHandler.cs` фактически 9 ограниченных `IgnoreQueryFilters` вызовов.
2. GitLand тест не передал доказательства Preview (`targetHeadSha`, `candidateTreeSha`) и проверял ошибку до intended GitHub remote check.

Обе причины исправлены после failed run: count обновлён до 9; Execute test передаёт точные значения из Preview. Этот запуск остаётся failed; нужен новый CI для актуального HEAD.

Оставшийся acceptance gap: сейчас нет успешного live integration test на отдельном protected GitHub repository; следует усилить deterministic API seam/tests для stale PR/base, approvals, required checks, duplicates, API rejection, lost-response recovery и mismatch. Production release protections (classic `v*` tag protection и required reviewers Environment `production`) всё ещё не подтверждены; не публиковать production tag и не запускать deploy до административной проверки.


## Дополнение — Landing не должен зависеть от включения AutoSync (26 ноября 2026)

В ходе проверки GitHub-authoritative landing обнаружена независимая настройка, ошибочно влиявшая на завершение задач. AutoSync по умолчанию выключен и отвечает за фоновый fetch/fast-forward и синхронизацию тегов. Он не должен блокировать фиксацию уже выполненного Beacon-managed landing.

Исправления находятся в feat/release-management, PR #230:

- Discovery endpoint теперь возвращает device-bound project root при включённом AutoSync либо при наличии точной Beacon LandingAttempt в verified Landed state, совпадающей с последним успешным Review (ReviewRunId, workstation, source SHA, target branch).
- DTO передаёт AutoSyncEnabled явно. Daemon пропускает обычный GitSync и tag sync при false, но продолжает отдельную remote-landing verification для возвращённых LandingAttempt.
- Reconciliation API при AutoSync=false принимает только Beacon-managed LandingAttempt в точном Landed state с совпадающим Review, source SHA, target branch и merge commit. Внешний PR без LandingAttempt по-прежнему требует AutoSync=true.
- LandingAttempt в ExecutePending, LandedUnverified, Failed либо с несовпадающими данными не может закрыть задачу. Сохраняются привязка к workstation, Approve verdict, последняя Review proof, отсутствие активной pipeline session и idempotent RemoteLandingProof.
- Security allowlist обновлён: DeviceAutoSyncTargetsHandler.cs — 8 IgnoreQueryFilters call sites; DeviceRemoteLandingReconciliationHandler.cs — 9.
- Добавлены тесты: target discovery при выключенном AutoSync и exact verified Landed Attempt; успешная reconciliation для Beacon-managed landing с AutoSync=false. Существующий отрицательный тест сохраняет отказ внешнего landing без LandingAttempt при AutoSync=false.

**Текущее состояние проверки:** CI #45712282443 (run 1641) запущен на более ранней вершине bdd46d1a и не покрывает этот срез. На момент записи все три платформы продолжали тесты без ошибок; нужен новый cross-platform CI на полном текущем implementation head после фиксации журнала.

Это исправление не включает обычную синхронизацию при AutoSync=false. Оно только позволяет довести Beacon-managed удалённый merge до подтверждённого RemoteLandingProof и закрытия задачи. Production tag/deploy не запускался.

### Оставшееся

1. Запустить полный CI на точной вершине после этого изменения и обработать возможные ошибки.
2. Добавить/проверить daemon loop test для remote-landing-only target при AutoSync=false: отсутствие вызовов GitSync и успешный POST reconciliation.
3. Завершить deterministic GitHub merge executor coverage и protected-repository acceptance.
4. До production публикации администратор должен подтвердить classic v* tag protection и required reviewers Environment production.


### Daemon-loop coverage for AutoSync-off landing reconciliation

After the server and target-discovery regression tests, the workstation loop now also has deterministic coverage:

- The remote-landing verifier is an injectable dependency with the production implementation as its default; this provides a deterministic test seam without changing production verification behavior.
- Added a daemon-loop test with an AutoSync-disabled target, a fake verifier and HTTP handler. It asserts target discovery and remote reconciliation POST occur once, the exact merge SHA is posted, and no normal GitSync path is invoked.
- The test intentionally passes no GitSync service; if the AutoSync-off branch accidentally starts fetch/fast-forward or tag synchronization, the test fails.
- Current implementation/test head before the journal update: `33ceaa7e5989f52d66883d4feecdf125c33df073`. The prior CI #45712282443 covers only `bdd46d1a`, so it cannot validate this slice. A new Linux/Windows/macOS run is required.


### Negative discovery coverage when AutoSync is disabled

Обновлённый target-discovery regression test создаёт два Approved task на одном устройстве и проекте с AutoSync=false:
- Beacon-managed task с точным LandingAttempt=Landed и последним Review proof должен быть возвращён для reconciliation;
- внешний/ручной task с успешным Review и Approve, но без LandingAttempt, должен быть исключён.

Тест проверяет, что AutoSync-off discovery не расширяет право на закрытие задач для любого Approved PR. Дополнительно DaemonAutoSyncLoop test подтверждает, что для AutoSync-off target вызывается remote-landing POST и не вызываются GitSync/tag-sync операции.

Точная source/test вершина до обновления журнала: `7b0c811ebdc3040c0de7dfcddb0f8d8028efaf94`. Предыдущие CI #1641/#1642 не покрывают эту вершину; требуется новый полный Linux/Windows/macOS CI после фиксации журнала.


### GitHub Merge Queue / stacked PR support is not complete

Текущий GitHubLandingMergeExecutor использует синхронный REST endpoint PUT /repos/{owner}/{repo}/pulls/{number}/merge с точным expected head SHA. Он сохраняет GitHub branch protection, но синхронный endpoint не поддерживает Merge Queue и stacked PR.

Актуальная документация GitHub рекомендует asynchronous merge API. Его merge_action=default выбирает Merge Queue, если она настроена, иначе выполняет прямой merge. Но ответ enqueued не означает завершённый merge; фактический статус нужно получить позднее через PR state. Источник: https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request-asynchronously.

Следовательно:
- Прямой GitHub merge путь реализован.
- Репозитории, где branch rules требуют Merge Queue, пока не поддержаны; stacked PR тоже не поддержан.
- Нельзя принимать enqueued за Landed или создавать RemoteLandingProof до реального состояния MERGED и проверки merge commit/tree/post-merge command.
- Для поддержки async/queue нужны durable merge request UUID/recovery, состояние ожидания в LandingAttempt/UI, poller на workstation или сервере, и финальная проверка точного PR/source/base/merge commit. Нужны тесты pending → enqueued → merged, rejected/failed и lost response.


### Backward-compatible protocol capability for AutoSync-off landing targets

Обнаружен риск для старых workstation daemon: старый клиент с git-auto-sync-v1 может получить новую форму target DTO, проигнорировать неизвестное поле AutoSyncEnabled и выполнить старый безусловный GitSync loop.

Добавлена отдельная capability git-remote-landing-reconcile-v1:
- Новая версия WorkstationProbe рекламирует её только при наличии поддерживаемого task-branch Git workflow.
- Discovery выдаёт AutoSync-off remote-landing-only targets только устройствам, рекламирующим эту capability.
- Reconciliation API независимо отклоняет AutoSync-off запросы от клиентов без неё.
- Обычные AutoSync-enabled targets по-прежнему доступны по прежней AutoSyncV1 capability.
- Тест сначала выполняет discovery со старым git-auto-sync-v1-only probe и ожидает пустой список, затем обновляет capabilities и ожидает ровно один Beacon-managed Landed target. Внешний Approved task без LandingAttempt не возвращается.
- Добавлен Domain test capability negotiation и обновлены reconciliation test fixtures.

Так AutoSync=false не может случайно привести к фоновой синхронизации у старого daemon. Обычный fetch/fast-forward и tag sync остаются отключёнными; новая capability разрешает только отдельный proof/reconciliation path.

Точный source/test head до обновления журнала: `eb3de7a5121e52fbc01aba134c3f493d0cbb5082`. CI #45714185084 выполнялся на предыдущем e0fb1f07: Linux завершился успешно, Windows/macOS дошли до CLI без ошибок на момент последней проверки, однако новый capability code/tests этим запуском не покрыты. После фиксации журнала нужен новый полный Linux/Windows/macOS CI.


### Capability negotiation protects older workstation daemons

Добавлена отдельная workstation capability `git-remote-landing-reconcile-v1` для AutoSync-independent remote-landing path.

- Новая WorkstationProbe рекламирует capability только при поддерживаемом task-branch Git workflow и установленном GitHub CLI `gh`.
- При AutoSync=false discovery выдаёт remote-landing-only target только устройству с новой capability. Старые daemon с одним `git-auto-sync-v1` не увидят такую цель и не смогут обработать неизвестное поле `AutoSyncEnabled=false` по старому безусловному Sync path.
- Reconciliation API повторно требует capability при AutoSync=false, даже если клиент обошёл discovery endpoint и прислал прямой запрос. Дополнительно остаётся требование exact verified `LandingAttemptStatus.Landed`.
- Target-discovery test проверяет legacy probe → пустой target list, затем новую capability → ровно один Beacon-managed Landed task; внешний Approved task без LandingAttempt остаётся исключён.
- API test проверяет, что legacy workstation не может закрыть задачу при AutoSync=false даже с валидным LandingAttempt; задача остаётся InProgress/Approved и RemoteLandingProof не создаётся.
- Добавлен unit test для Supports(RemoteLandingReconcileV1).

**Проверка:** CI #45714185084 на предыдущем source/test head `e0fb1f07` не включает capability gate. Linux завершился успешно, Windows/macOS находились в более поздних CLI/Web тестах без видимых ошибок. Для этого compatibility slice обязателен новый полный CI на текущем implementation head после обновления журнала.


## Дополнение — асинхронный GitHub landing и merge queue (26 ноября 2026)

Реализация находится в `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230). Этот срез закрывает архитектурный разрыв между GitLand Execute и GitHub-authoritative merge: Beacon теперь отправляет разрешённый merge request, но не считает принятие запроса фактом слияния.

### Что реализовано

- GitHub merge вызывается через асинхронный endpoint `PUT /repos/{owner}/{repo}/pulls/{number}/merge-async` с точным reviewed head SHA и `merge_action=default`. GitHub выбирает прямой merge или merge queue по настройкам репозитория. `bypass_rules` не задаётся.
- `pending` и `enqueued` — состояния ожидания, не `Landed`. Beacon сохраняет очередь в `LandingAttempt`, оставляет её `ExecutePending` и не закрывает задачу.
- Повторный HTTP 409 может вернуть ранее созданный async request UUID; код извлекает его из structured response body. Если GitHub сообщил `enqueued` без UUID, Beacon продолжает опрашивать сам PR. Потерянное подтверждение workstation также допускает только read-only recovery по тому же PR.
- AutoSync target discovery видит queued Beacon attempts, даже когда обычный repository AutoSync выключен, но только у устройств с capability `git-remote-landing-reconcile-v1`. Старые daemon без capability не получают remote-landing-only target; reconciliation API повторно проверяет этот capability.
- Переход к `Landed` требует реального PR `MERGED`, точного source/head/base, fetch актуального target, достижимости merge commit и совпадения дерева merge commit с неизменяемым candidate tree из Preview.
- Закреплённый workstation запускает тот же project `ReviewCheckCommand` на detached temporary worktree, созданном от merge commit. Проверяются HEAD, tree, отсутствие незакоммиченных изменений; результат передаётся как exit code + SHA-256 output hash.
- Server reconciliation принимает только точные LandingAttempt ID, ReviewRun, workstation, source SHA, base branch, async merge UUID (когда он есть), candidate tree и post-merge check proof. В этой же серверной операции queued attempt переводится в `Landed` и задача закрывается.
- Хеш `ReviewCheckCommand` сравнивается между ReviewRun, LandingAttempt и текущими настройками проекта. Если Owner меняет команду пока PR ожидает в очереди, проверка не подменяется новой: задача остаётся открытой до восстановления исходной конфигурации.

### Проверки

Добавлены/расширены Domain, CLI, Application и workstation tests: очередь с UUID и без него, восстановление после неизвестного результата Execute, 409 response, pending/enqueued/merged/failed parsing, target discovery при AutoSync=false, запрет закрытия по неправильной LandingAttempt/UUID, command drift и успешное reconciliation только по полному доказательству.

Полностью зелёный CI [#45715872864](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45715872864) подтверждён на более раннем head `22fc248c7a616db0805d18b97273572c4e9db72a`, до async merge queue реализации. Текущие async changes не покрыты этим run. Последний source/test head до обновления журнала/плана: `64268979e60ea6f932673c21b87dc37c6fad3ba8`. Не считать slice принятым до полного Linux/Windows/macOS CI на точном текущем HEAD.

### Границы безопасности и блокеры

- GitHub branch/repository rules оцениваются при фактическом прямом merge или при исполнении merge queue, а не в момент первоначального async request. Beacon не использует `bypass_rules` и не предоставляет обычному Actor отдельный merge tool.
- Rulesets API вернул пустой список. Classic branch/tag protection и required reviewers Environment `production` через текущую интеграцию не подтверждены (403/unsupported). До ручной проверки администратором production tag/deploy запрещён.
- Нужен end-to-end acceptance на пользовательском проекте с обязательной merge queue, включая отсутствие/истечение UUID и отказ merge queue.
- PR #230 остаётся открытым; ничего не слито в `main`, production tag/deploy не создавались.

### Следующий этап

1. Завершить полный CI на итоговом implementation head и исправить все обнаруженные ошибки.
2. Пройти end-to-end acceptance с реальным GitHub merge queue и verify PostMergeCheck.
3. Доделать product acceptance: release/settings UI, localization, RLS, ошибки workflow и проект с независимой схемой версии.


## Дополнение — терминальный отказ GitHub async merge (26 ноября 2026)

Реализован пропущенный failure path для GitHub-authoritative landing в `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230):

- Workstation через AutoSync опрашивает exact async merge request. Только подтверждённый GitHub status `failed` вместе с повторно прочитанным неизменившимся OPEN PR (тот же repository/PR URL, source HEAD SHA и target branch) формирует failure proof.
- `pending`, `enqueued`, недоступность/истечение async request endpoint, смена PR head/base, закрытый или уже merged PR не переводят attempt в Failed.
- Device endpoint `POST /v1/devices/me/remote-landings/failure` передаёт доказательство в серверный `ReportFailureAsync`. Сервер повторно сверяет pinned workstation, project runtime, LandingAttempt ID, ReviewRun, UUID, source/base, ReviewCheckCommand hash, latest Approve и отсутствие активного pipeline session.
- После точного совпадения Beacon записывает `GitHubAsyncMergeFailed` в FailureCode/EscalationCode и пишет audit event. Task остаётся InProgress/Approved, proof merge не создаётся; пользователю показывается локализованная причина и требуется новый Preview/Review после исправления причины отказа.
- Удалено дублирующее API объявление и alias: единственный клиентский endpoint — `/failure`.
- Application test проверяет wrong UUID vs exact UUID и отсутствие закрытия задачи. AutoSync loop test проверяет вызов failure endpoint и отсутствие вызова successful reconciliation.

**Текущее состояние CI:** старый run [#45763146032](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) выполняется на более раннем head `8ff19877` и не проверяет эти изменения. Реализационный head перед последним journal-only commit: `c5982175ec5040cdafdb2a05dace1f8e2fe9dd2b`; CI на нём не зарегистрирован. Текущий journal-only commit: `4c4bd0baa6beaf14c9af49521986a35f36056568`. Полный Linux/Windows/macOS CI нужно запустить на итоговом HEAD после синхронизации плана и журнала.

**Нерешённое:** end-to-end acceptance на пользовательском проекте с реальным merge queue (pending/enqueued/merged/failed, UUID missing/expired, ReviewCheckCommand drift). Rulesets API вернул пустой список; classic branch/tag protection и production reviewers недоступны текущей интеграции, поэтому production tag/deploy запрещены до ручной проверки администратором.

### UI regression coverage and latest-head correction (26 ноября 2026)

После реализации failure endpoint добавлен BUnit-тест FailedGitHubMerge_ShowsLocalizedFailureReason в ProjectBeacon.Web.Tests/Bunit/TaskPipelinePanelTests.cs. Он проверяет, что пользователь видит понятное локализованное сообщение при LandingAttemptStatus.Failed и GitHubAsyncMergeFailed, а не только machine code.

Повторная проверка implementation branch подтвердила:

- ровно один device endpoint POST /v1/devices/me/remote-landings/failure;
- AutoSync вызывает его только для терминального async failed после повторной проверки exact OPEN PR/head/base/URL;
- успешная reconciliation не вызывается для отказа;
- сервер сверяет UUID и неизменность project ReviewCheckCommand, записывает audit/failure state, но оставляет Task InProgress/Approved;
- Application, AutoSync loop и UI regression tests покрывают этот поток.

Implementation head на момент этого обновления плана: 43747d09cd31bd3adcc1fb9ed7ee7b26965f0e0e; журнал в feat/release-management обновлён после этого source commit и запуска CI на окончательной вершине ещё нет. Run #45763146032 (https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) относится к более раннему head 8ff19877 и не считается проверкой текущих изменений. После journal update необходимо получить новый точный HEAD и запустить Linux/Windows/macOS matrix.


## Дополнение — обработка окончательного отказа merge queue (26 ноября 2026)

К async GitHub landing добавлен отдельный Device-authenticated failure-report endpoint. Он не запускает merge и не изменяет Git refs: станция сообщает только о статусе failed, полученном от GitHub для конкретного async merge request, после повторной проверки, что PR остаётся открытым и имеет тот же base/head/URL.

Сервер принимает report только от активной закреплённой станции и только при совпадении:
- последней LandingAttempt и её ID;
- task/project/device pin;
- точного async request UUID;
- ReviewRun, source SHA, target branch и последнего Approve;
- текущего и reviewed hash ReviewCheckCommand;
- отсутствия активной/ожидающей pipeline session.

После подтверждения только attempt переводится в Failed с кодом GitHubAsyncMergeFailed и audit event. Задача остаётся InProgress/Approved, remote proof не создаётся, пользователь может исправить причину и запустить новый Preview. Старые attempt и чужой UUID отвергаются. UI показывает локализованную понятную причину (en/ru/de/ja/zh). AutoSync использует отдельный POST endpoint и не вызывает reconciliation успеха.

Добавлены Application, AutoSync loop и BUnit tests на отказ UUID, успешную регистрацию, сохранение задачи открытой и передачу failure-report без вызова merge reconciliation.

### CI

CI #45761895374 (https://github.com/vitalyKl/ProjectBeacon/actions/runs/45761895374) выполнял семь тестовых модулей успешно до format step; Linux/macOS упали на три xUnit2031 в AutoSync target discovery tests, Windows был отменён fail-fast. Три Assert.Single выражения заменены на predicate overload.

CI #45763146032 (https://github.com/vitalyKl/ProjectBeacon/actions/runs/45763146032) запущен на раннем head 8ff19877, до Device endpoint/client failure-report path и новых UI/Application/loop tests. Его результат не покрывает окончательную реализацию. Implementation head перед этим обновлением: faf9f8e9ab96f17cc8e220d289d1c2089379c881. Обязателен свежий full matrix на точном финальном HEAD.

### Следующий шаг

1. Проверить CI #45763146032 на соответствие именно его commit.
2. Запустить полный CI на финальной вершине implementation branch.
3. Провести end-to-end acceptance с реальной GitHub merge queue, включая окончательный отказ и retry через новый Preview.
4. Не создавать production tag/deploy до ручной проверки tag/branch protection и production Environment reviewers администратором.


## Дополнение — исправление Application CI для async merge failure (26 ноября 2026)

На CI [#45764313765](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45764313765), run 1648, Linux TRX выявил две конкретные Application failures на pre-fix head `eb76ae90`:

1. Новый тест `RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen` завершался исключением audit allowlist: anonymous object сериализовал `request.Repository` как PascalCase `Repository`, который отклоняется `AuditEvent.ValidateDetails`. Исправлено: failure audit payload теперь использует только существующие allowlisted scalar keys `deviceId`, `sourceBranch`, `targetBranch`, `sourceSha`, `reviewRunId`, `policyCode`, `resultCode`. LandingAttempt ID остаётся TargetId; audit не хранит лишний свободный PR/request metadata.
2. `IgnoreQueryFiltersAllowlistTests.ProductionTenantFilterBypasses_MatchReviewedPathAndCountAllowlist` ожидал 9 call sites, фактическое число стало 18 в `DeviceRemoteLandingReconciliationHandler.cs`. Дополнительные 9 относятся к новому ReportFailureAsync. Все 18 call sites проверены: каждая выборка device/project/runtime/task/attempt/review/verdict/session ограничена authenticated deviceId и точными project/task/attempt identity. Manifest `docs/ignore-query-filters.allowlist` обновлён до 18 с раздельным обоснованием успешного reconciliation и failure report.

Остальные Linux Application test failures и TRX details:
- #45764313765 выявил ровно эти два теста.
- Linux CLI, Domain, Infrastructure, Web и Worker прошли; TRX guard завершился с 2,663 результатами, 7 модулями и одним разрешённым platform skip.
- Windows/macOS Application step также был помечен failed до перезапуска; после исправлений требуется повторная полная матрица.

Source fixes находятся в `feat/release-management`; исходный post-fix HEAD `2260a7c8c99a6f488286a784751e67f97a57966a`. Журнал implementation branch обновлён последующим commit; требуется новый CI на текущем post-journal HEAD. До зелёного результата не считать async landing slice принятым.

Отдельная функциональная граница остаётся намеренной: при отсутствии async request UUID AutoSync продолжает PR polling и может подтвердить exact eventual MERGED proof, но не может безопасно связать terminal failure с конкретной попыткой. Такой outcome не переводится в Failed без UUID; требуется manual/retryable diagnosis. Не делать автоматический вывод о Failed по одному лишь открытому/blocked PR.


## Дополнение — исправление Windows CLI CI после async landing (26 ноября 2026)

CI [#45765744398](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45765744398), run 1649, выполнялся на точном implementation head `a1f277731f66ccadc319f204eede3db3632b6251`:

- Linux и macOS завершились успешно.
- Windows Build, EF model check, API/Application/Domain/Infrastructure/Web/Worker завершились успешно; CLI test module выявил три ошибки:
  - `NativeCommandRunnerTests.ArgvCommandRunsWithoutShellAndReturnsStructuredResultAndLogId`: `dotnet --version` истёк по 15-секундному тестовому timeout, поэтому корректный результат runner имел `timedOut=true` и `exitCode=null`; тест ошибочно пытался читать null как число.
  - `NativeAgentRuntimeTests.NativeRuntimeWaitsForCommandApprovalBeforeRunningArgvAndResumesAfterReply` для решений once и always: polling ждал только 4 секунды, тогда как процесс команды ещё выполнялся.

Исправления в `feat/release-management`:
- Изолированный command environment задаёт `DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1`. User CLI config/credentials не разделяются.
- Direct runner test получает 60-секундный timeout, проверяет `timedOut=false` до чтения exit code и показывает duration/outputTail при сбое.
- Approval/resume test ожидает завершение ограниченного 60-секундного process timeout до 50 секунд и выводит последний tool result при ошибке.

Source/test commits: `90b6990815beb12c44bcf26c84e16784c6b1366e`, `6b3669b2339a3081fd34bebe53b1471d561bdcd9`, `fd953347cc89b6c4e07b9722850b1e6dc97ab8e5`. Журнал implementation branch обновлён commit `0854badef909e0b85b6b8479f8a390f48a403ce1`. Текущий full head после journal update должен быть проверен новым cross-platform CI; прежние Linux/macOS success из #45765744398 не проверяют post-fix Windows код.

Остаток не меняется: real project acceptance GitHub merge queue (pending/enqueued/merged/failed и неопределённый no-UUID), плюс ручная проверка классических tag/branch protection и required reviewers Production Environment. Не создавать production tag/deploy до административной проверки.
