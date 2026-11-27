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


## Статус GitHub-authoritative landing и Windows CI fix — 26 ноября 2026

Реализация S4 продвинулась от read-only reconciliation к доверенному исполнению landing:

- Для GitHub-origin GitLand Execute повторно строит точный candidate и запускает pinned ReviewCheck, затем вызывает GitHub asynchronous merge API с exact reviewed head SHA и merge_action=default. Нет bypass_rules, --admin, force-push, произвольного base/head или отдельного merge tool для Actor. Решение о немедленном merge/merge queue принимает GitHub по настроенным правилам репозитория.
- pending/enqueued не трактуется как успешное слияние. Beacon сохраняет async request ID и ждёт подтверждение точного PR/request.
- Закреплённая станция через AutoSync проверяет итоговый PR, merge commit, fetched target ancestry, совпадение candidate tree и post-merge ReviewCheck. Сервер повторно проверяет task/workstation/Review/attempt/request identity и закрывает задачу только после полного доказательства.
- Терминальное failed состояние передаётся отдельным device-authenticated failure endpoint; при совпадении exact UUID attempt переводится в Failed, но задача остаётся открытой. Без request UUID терминальный отказ не записывается как Failed — это остаётся неопределённым/ручным состоянием.

CI #45765744398 на head a1f277731f66ccadc319f204eede3db3632b6251: Linux и macOS прошли; Windows прошёл Build, EF, API, Application, Domain, Infrastructure, Web и Worker, но CLI module упал на три timeout-sensitive tests. Исправления:
- isolated NativeCommandRunner устанавливает DOTNET_SKIP_FIRST_TIME_EXPERIENCE, DOTNET_CLI_TELEMETRY_OPTOUT, DOTNET_NOLOGO;
- runner test теперь использует 60 секунд и выводит duration/output при timeout;
- approval/resume test ждёт завершения bounded command до 50 секунд;
- отдельные assertions проверяют три environment flags.

Source/test commits: 90b6990815beb12c44bcf26c84e16784c6b1366e, 6b3669b2339a3081fd34bebe53b1471d561bdcd9, fd953347cc89b6c4e07b9722850b1e6dc97ab8e5, 4457acb742cb5bf2448399a4d4c5bb39eede0e1c. Журнал implementation branch содержит точную диагностику. Не считать fix принятым до нового CI на post-journal HEAD.

End-to-end acceptance с реальной GitHub merge queue всё ещё необходим: enqueued→merged, definitive failed, no/expired UUID, PR identity drift и ReviewCheckCommand drift. Tag/branch protection и production Environment reviewers остаются отдельным административным блокером.


## CI cancellation incident и исправление test gating — 26 ноября 2026

PR CI #45775401460 (run 1650) был отменён новым запуском #45775571535 (run 1651) на том же SHA `43b611406c9370e9dd022b4b61c4cffed2672127`. Поскольку workflow использовал `if: always()` для `dotnet test --no-build`, отменённый Build всё равно запускал API/остальные тесты при отсутствующих бинарниках. В Linux API завершился `Win32Exception (2)` / `Zero tests ran`, а TRX guard обнаружил 6 вместо 7 отчётов. На Windows сборка была отменена до создания test executable, последующие команды сообщали `No test projects were found` или отсутствие DLL. Это ложные статусы отменённого запуска, не подтверждённые падения тестов.

Run #45775571535 (run 1651) — текущая проверка source head `43b61140`, однако он использует старую CI-конфигурацию. Его результаты будут учитываться только для кода, не для новых workflow changes.

Исправление в implementation branch:
- `.github/workflows/ci.yml`: 16 условий тестовых модулей, TRX report и TRX guard заменены с `always()` на `!cancelled()`.
- `.github/workflows/release.yml`: восемь условий тестовых модулей и TRX guard заменены на `!cancelled()`; scan/SBOM artifact upload сохраняет `always()`.
- При обычном Build/test failure остальные модули продолжают выполняться, чтобы собирать полный набор ошибок. При отмене или замене workflow run `--no-build` тесты больше не стартуют на неполном checkout/build output.

Implementation commits: `14a8b606b99fdb96c678dd4d04873e99565a5f10`, `216901136fbd16da006b04da59910f776227d652`; implementation journal commit: `71439aae751c7ccb4804269b79458bcfeb096815`. После завершения run 1651 требуется отдельная полная CI-матрица на текущей вершине branch, чтобы проверить новые условия вместе с Windows timeout fix.


## Кроссплатформенный CI после исправления Windows CLI — 26 ноября 2026

CI [#45775571535](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45775571535), run 1651, успешно завершился на exact source head `43b611406c9370e9dd022b4b61c4cffed2672127`:

- Linux: Build, EF model check, все 7 test modules, TRX report/guards и dotnet format — success. TRX guard: 2,663 results, 0 failed, 1 разрешённый platform-conditional skip.
- Windows: Build, EF model check, все 7 test modules, dotnet format — success. CLI — 668 tests, Errors 0, Failed 0, Skipped 0. Это подтверждает исправление Windows cold-start / approval-resume timeout, ранее сломавшего три CLI tests.
- macOS: Build, EF model check, все 7 test modules и dotnet format — success.
- Windows/macOS lanes используют `BEACON_TEST_SKIP_POSTGRES=1`; их PostgreSQL integration skips не являются успешным запуском PostgreSQL tests. Основная PostgreSQL lane — Linux.

Важно: run 1651 выполнялся на source head `43b61140` и не включает два более поздних workflow commits (`14a8b606` для ci.yml и `21690113` для release.yml) с `if: ${{ !cancelled() }}`. Эти изменения устраняют ложные missing-binary/zero-tests failure при concurrency cancel, но требуют новой полной матрицы на текущем implementation HEAD после записи журнала.

Журнал implementation branch зафиксировал run 1651 отдельным commit `13a157373004b49be000fb735c83f5fff4dbb7ef`. Дальнейший шаг: exact-head CI с cancellation-safe test gating, затем merge-queue user-project acceptance. Не смешивать эту проверку с production tag/deploy; classic protection/reviewers ещё не подтверждены.


## Windows bUnit / SQLite teardown race — 26 ноября 2026

CI [#45778443033](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45778443033), run 1652, выявил один Windows-only Web failure:

- `ProjectBeacon.Web.Tests.Bunit.ChatPollRecoveryTests.ChatDock_PollSurvivesTransientFailure` прошёл свой функциональный assertion, но teardown упал с `InvalidOperationException: Collection was modified` из `Microsoft.Data.Sqlite.SqliteConnection.Close()`.
- На Linux и macOS полные jobs завершились успешно; на Windows все остальные test modules прошли. Web: 249 passed, 3 PostgreSQL skips и 1 failure.
- Причина — общий fixture `BUnitRenderBase.Dispose()` закрывал in-memory SQLite connection раньше, чем bUnit renderer освобождал компоненты, их poll loops и scoped DbContexts.
- Исправление в implementation branch: сначала dispose bUnit renderer, затем root BeaconDbContext, затем SQLite connection. Тест оставлен без retries/timeouts и без подавления исключения.
- Source fix: `41cf2f82ea6e3adb3db74793e0262775c10160a5`; journal commit: `a37e6f5a8115650b579cc756eddc148db76ad4f8`.

**Проверка:** CI #45778443033 относится к предыдущему source head `13a15737`. Новый полный Linux/Windows/macOS CI обязателен для текущего post-journal head. Считать teardown fix принятым только после прохождения Windows Web и общей матрицы.

Состояние GitHub-authoritative landing не меняется: executor использует GitHub async merge API, не включает bypass rules, а merge queue остаётся авторитетным; задача закрывается только после сверки точного PR/head, merge commit, target ancestry, candidate tree и pinned post-merge check. End-to-end сценарии merge queue ещё нужно проверить. Не запускать production tags/deploy до подтверждения классической tag protection и Production Environment reviewers.


## CI result: Windows SQLite teardown fix accepted — 26 ноября 2026

CI [#45783053165](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45783053165), run 1653, completed successfully on exact implementation head `a37e6f5a8115650b579cc756eddc148db76ad4f8`:

- Linux, Windows and macOS Restore, Build, EF model check, all seven test modules and formatting passed.
- Linux additionally passed release-manifest Python tests and TRX guards.
- Windows Web passed the former failing `ChatDock_PollSurvivesTransientFailure`; disposing the bUnit renderer and scoped DbContexts before shared SQLite removed the teardown race.
- The implementation/journal branch then received journal-only commit `401a7dd4a0da6ec264a123981be8d55a4c1fffca`; this does not change application source.

### Additional S4 proof propagation issue found after the green run

For an immediate GitHub merge, GitLand already completes the exact post-merge check and writes `LandingAttempt.Status = Landed`, including candidate tree SHA, check exit code and output hash. However `GetDeviceAutoSyncTargetsHandler` currently sends only `LandingAttemptId` for this state. The daemon's remote verifier requires the candidate tree and post-merge proof before it submits reconciliation, so it refuses to close the task even though the server already has immutable successful evidence.

Next implementation must:

1. Include the persisted candidate tree SHA, post-merge exit code and output hash in the remote-landing target for an already-`Landed` attempt.
2. Have the daemon verify that the actual GitHub merge commit tree equals the persisted candidate tree, then forward the existing post-merge proof. Do not rerun the mutable project `ReviewCheckCommand` for an attempt already marked `Landed`.
3. For an async attempt still pending/enqueued, continue to run the pinned ReviewCheck against the exact eventual GitHub merge commit and pass that fresh result to reconciliation.
4. Make `ReconcileDeviceRemoteLandingHandler` compare the `Landed` request's candidate tree and post-merge check exit/hash against the persisted attempt, not merely the merge commit SHA.
5. Add regression tests for immediate `Landed` evidence and a changed project ReviewCheckCommand after landing; then run a new full CI matrix on the new exact source HEAD.

The new proof-propagation source change is not yet implemented or validated. No production tag/deploy or PR merge occurred.

## AutoSync: preserve immutable proof for immediate GitHub merges — 26 November 2026

The previous green CI run [#45783053165](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45783053165) validated the Windows SQLite teardown correction on exact source head `a37e6f5a8115650b579cc756eddc148db76ad4f8`.

A follow-up review found that immediate GitHub merges (`merged` returned by the asynchronous merge request) were already fully checked by GitLand and persisted as `LandingAttempt.Status = Landed`, but AutoSync target discovery forwarded only the attempt ID. The daemon verifier therefore lacked the candidate tree and check evidence it requires before closing the task. Rerunning the current project ReviewCheckCommand would also incorrectly make later project-setting edits invalidate a completed landing.

Implementation in `feat/release-management`:

- `DeviceAutoSyncTargetsHandler` exports the persisted candidate tree, post-merge exit code and output hash for an already-`Landed` attempt.
- The pinned workstation compares the actual GitHub merge commit tree to the immutable candidate tree. For a `Landed` attempt it forwards the stored successful check proof and does not execute the current mutable project setting again.
- For an async merge still pending/enqueued, AutoSync still has no post-merge evidence; it runs only the ReviewCheckCommand whose hash matches the immutable Review proof after the exact GitHub merge is observed.
- The server now compares the candidate tree and post-merge exit/hash in the reconciliation request with the values persisted in the `Landed` attempt. Missing or mismatched evidence is rejected.
- Added tests for target payload proof fields, later ReviewCheckCommand edits, rejection of missing evidence and mismatched candidate tree, and successful reconciliation with the exact immutable proof.

Source/test head before the journal-only update: `1c9cfdd43477c46375cecf9a809a386f06d9a48a`; journal commit: `bd4378bb09d883df6d64f80542e198df775b5aef`.

**Verification:** the new source/tests have not yet been covered by CI. Full Linux/Windows/macOS CI on the latest branch head is required. No live GitHub merge-queue acceptance, production tag or deployment is claimed.


## Дополнение — GitHub merge queue и merge-group tree (26 ноября 2026)

### Найденное расхождение

GitHub merge queue формирует временную merge-group ветку из актуальной base и PR, стоящих перед текущим PR в очереди. Поэтому итоговый merge commit может иметь другое дерево, чем локальный Preview candidate Beacon, даже когда GitHub слил ровно тот PR head, который проходил Review. Простое сравнение `mergeCommit^{tree}` с Preview tree ошибочно отвергало бы корректное слияние из очереди.

### Изменение в реализации

В `feat/release-management` добавлен общий валидатор `GitHubLandingMergeExecutor.IsPostMergeTreeAcceptable`:

- локальный GitLand передаёт ожидаемое candidate tree и продолжает требовать точного совпадения;
- GitHub-authoritative merge path проверяет валидность фактического merge tree, но не требует равенства локальному Preview tree;
- точность GitHub-пути обеспечивается pinned source SHA при merge API, проверками PR/base/review/checks до merge, подтверждением что exact merge commit появился в fetched remote target branch, повторной проверкой PR identity/state и запуском неизменённого ReviewCheckCommand на фактическом GitHub merge commit;
- AutoSync передаёт серверу exact merge commit и post-merge evidence; сервер сравнивает их с LandingAttempt и только после этого закрывает задачу.

Добавлены тесты на строгий режим, валидное отличие дерева в merge queue и некорректные SHA. Изменения в текущей вершине `9fce7b94c699ac8313bc9f149347035c32404ddd`; журнал обновлён отдельным commit `c0c1b7b93a1223e5188beded69fd8325b277054c`.

### Проверка и ограничения

Предыдущий полный CI [#45786178874](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45786178874) был зелёным на head `bd4378bb`, но он не включает merge-queue tree изменения. Требуется отдельный CI на актуальной вершине, все результаты до него остаются непроверенными.

Live merge queue не тестировалась. End-to-end тестировать на disposable GitHub repository с настроенными required checks для события `merge_group`; если CI не публикует проверки merge group, очередь не сможет подтвердить candidate. Не считать queue workflow принятым только по unit tests.

### Следующие шаги

1. Запустить full Linux/Windows/macOS CI на актуальном implementation head после обновления журнала.
2. Проверить прямой merge и merge queue: queued → merged, включение earlier queued PR, base advanced, failed queue entry, lost response/expired request ID, изменение PR head/state и неуспешный pinned post-merge check.
3. Убедиться, что локальный GitLand продолжает требовать точного candidate tree.
4. Сохранить административный блокер: classic tag protection `v*` и required reviewers для GitHub Environment `production` не подтверждены. Не создавать production tag и не запускать deploy.
5. Оставить PR #230 открытым для review; слияние development PR не входит в это поручение.


## Уточнение — послабление дерева только для подтверждённой merge queue (26 ноября 2026)

Первоначальное исправление сравнения merge tree было дополнительно сужено, чтобы не ослаблять обычный прямой merge.

### Итоговая матрица

| Сценарий | Проверка фактического merge tree |
|---|---|
| GitHub async сразу вернул `merged` | Строгое равенство Preview candidate tree. Несовпадение → `LandedUnverified`, без автоматического принятия |
| GitHub async вернул `pending` или `enqueued` | Ожидать фактический PR `MERGED`; merge-group tree может отличаться от Preview из-за более новой base или PR перед ним в очереди |
| Обычный локальный GitLand | Строгое равенство Preview candidate tree; поведение не ослаблено |

Для queued merge AutoSync подтверждает точный PR/source SHA, merge commit ancestry в фактически полученной удалённой целевой ветке, валидность actual merge-group tree и выполняет pinned ReviewCheck на самом итоговом merge commit. Candidate SHA остаётся частью неизменяемого Preview/LandingAttempt доказательства; он не заменяет подтверждение фактического queue merge.

Для немедленного `merged` GitLand по-прежнему требует точного Preview tree, выполняет pinned post-merge check и сохраняет неизменяемый результат. AutoSync распознаёт этот завершённый путь по сохранённому post-merge proof и также использует строгую tree-проверку. Для queued попытки proof ещё отсутствует, поэтому применяется queue-only проверка дерева и последующий pinned check.

Эта граница важна: обычное изменение base между проверкой и непосредственным merge не должно быть принято как merge-group сценарий, если GitHub не подтвердил queued state.

Изменения находятся в `feat/release-management`; последняя implementation/journal вершина на момент записи: `cf6410071c6df28dbda627faa69b05ad556958df`. Run [#45789363602](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45789363602) был запущен на более ранней вершине `c0c1b7b9` и не валидирует это уточнение. Нужен новый полный CI на точной окончательной вершине и live acceptance на disposable репозитории с merge-group checks. До этого feature slice остаётся непроверенным; production tags/deploy запрещены до отдельной административной проверки защиты.


## Дополнение — защита от повторной отправки уже поставленного в очередь PR (26 ноября 2026)

Повторный ручной Execute после того, как GitHub уже подтвердил `pending` или `enqueued`, не должен перезаписывать queue marker и ставить новую workstation-команду.

В реализации `feat/release-management`:

- Добавлен явный `LandingAttempt.IsGitHubMergeQueued`, распознающий только `GitHubMergeQueued` / `GitHubMergeQueued:{uuid}`.
- `ExecuteLandingHandler` возвращает текущую попытку без повторного dispatch, если GitHub уже принял PR для фоновой обработки/merge queue. AutoSync остаётся единственным polling owner до фактического `MERGED` либо definitive async failure.
- Общий `CommandCompletionUnknown` не считается явным подтверждением очереди: такое состояние может относиться к локальному GitLand и должно оставаться retryable. Для uncertain состояния разрешена только безопасная попытка восстановления; GitHub async endpoint возвращает уже существующий запрос или текущее состояние PR.
- Добавлены Domain и Application tests: UUID/no-UUID queue marker, отличие от generic unknown, отсутствие второй GitLand команды при повторном Execute.

Текущий implementation source/test head: `77b73cf187022c39b072e4ca50a2fa0434931fa4`; журнал следующего commit должен быть добавлен до нового exact-head CI. Предыдущий run [#45789363602](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45789363602) стартовал на более старом `c0c1b7b9` и не валидирует этот guard и queue-only narrowing. Нужен новый CI на окончательном implementation head; live merge queue всё ещё не тестировалась. Production tag/deploy и merge PR #230 не выполнялись.


## Дополнение — закрытый без merge PR после постановки в merge queue (26 ноября 2026)

### Найденный крайний сценарий

AutoSync ранее ожидал PR в состояниях `OPEN` или `MERGED`. Если владелец/maintainer закрывал уже поставленный в GitHub merge queue PR без слияния, PR исчезал из набора `OPEN` и не попадал в `MERGED`. Beacon переставал предпринимать действия, а `LandingAttempt` оставался в `ExecutePending` бесконечно.

### Исправление в `feat/release-management`, PR #230

- Для exact task branch + review base AutoSync теперь обрабатывает `CLOSED` PR, если сам Beacon ранее сохранил явное подтверждение queue request: `GitHubMergeQueued` или UUID async merge request.
- Workstation повторно запрашивает конкретный PR и подтверждает точное совпадение URL, base, head SHA и source identity; для закрытого PR дополнительно требует отсутствие `mergedAt` и merge commit SHA.
- В серверный device failure endpoint отправляется отдельный машинный код `GitHubPullRequestClosedWithoutMerge` с состоянием `CLOSED`. Обычная async/queue removal failure остаётся `GitHubAsyncMergeFailed` с состоянием `OPEN`.
- Сервер принимает только эти две фиксированные пары code/state и проверяет точную queued LandingAttempt, latest successful Review proof, pinned workstation, source SHA, base branch, project/device binding и отсутствие активной pipeline session.
- Failure оставляет задачу `InProgress/Approved`, записывает audit event и переводит attempt в `Failed`; RemoteLandingProof не создаётся и task не закрывается. После разбора отказа можно запросить свежие Preview/Review.
- Добавлены CLI unit tests для state classification/неприемлемых состояний и Application/SQLite test для failure report, mismatch state, сохранения task state, отсутствия merge proof и audit actor.

Изменения source/tests сохранены в `feat/release-management` коммитами `53c72915`–`561fbd82`; журнал продолжен в commit `0c56ffbc234ebf57a984b8a7ce80c53440d93eb1`.

### Проверка и остаток

Новый полный CI на точной implementation вершине после журнала ещё не запускался; до него эта часть не считается принятой. Live merge queue не тестировалась. Следующая проверка — полный Linux/Windows/macOS CI и затем disposable GitHub repository с настроенным merge_group workflow: queued PR closed without merge, queue entry removed, async failed, queued → merged, expiry/lost UUID и PR/base/head drift.

Административные блокеры без изменений: classic tag protection `v*` и required reviewers для Environment `production` не подтверждены. Production tag/deploy запрещены до проверки через GitHub Settings. PR #230 остаётся открытым; его merge не входит в этот план.


## Дополнение — исправление audit allowlist после проверки CI (26 ноября 2026)

Первый full CI для closed-unmerged queue failure slice был запущен на exact head `0c56ffbc234ebf57a984b8a7ce80c53440d93eb1` (run [#45794974665](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45794974665)). Linux завершил job, macOS показал ту же причину в Application; в трёх тестах сервер дошёл до записи audit event, но audit schema guard отклонил неразрешённое поле `pullRequestState`.

Исправление в `feat/release-management`:

- удалено только неallowlisted поле `pullRequestState` из JSON audit details;
- оставлены стандартные allowlisted `policyCode` и `resultCode`, различающие `GitHubAsyncMergeFailed` и `GitHubPullRequestClosedWithoutMerge`;
- валидация пары failure code/PR state до записи audit остаётся строгой, allowlist не ослаблялся.

Source correction: `fb00cf6d37f9b93d0319054361014cd69db8177b`; journal commit: `e2cc19ed38937fe3cc86d38f10f23b9cc6f7fb28`. Полный exact-head CI на следующей вершине обязателен; предыдущий красный run не включает исправление. До зелёного CI этот участок не считается принятым.

Live merge-queue acceptance и административная проверка classic tag protection `v*` / Production Environment reviewers остаются обязательными блокерами. Production tag/deploy не выполнять.


## Дополнение — различать async merge pending и enqueued после истечения UUID (26 ноября 2026)

### Найденная ошибка

Beacon сохранял UUID GitHub async merge request, но не сохранял исходное состояние ответа `pending` или `enqueued`. GitHub хранит async request record ограниченное время (24 часа). Поэтому отсутствие доступного UUID позже само по себе не доказывает, что PR был удалён из merge queue или merge завершился отказом.

### Изменения в `feat/release-management`, PR #230

- `LandingAttempt` теперь хранит отдельные маркеры `GitHubMergePending:{uuid}`, `GitHubMergeEnqueued:{uuid}` и `GitHubMergeEnqueued` для случая без UUID.
- `GitLandCompletion` сохраняет точный `mergeRequestStatus`; pending без UUID отвергается.
- API target discovery передаёт `MergeRequestStatus` pinned workstation.
- Повторный Execute блокируется для любого явно принятого GitHub async request, не только для enqueued.
- Отсутствие queue entry считается доказательством queue removal только если GitHub явно подтвердил `enqueued`. Если persisted state был `pending`, а UUID уже недоступен, AutoSync сохраняет неопределённое состояние и продолжает polling, не создавая ложную ошибку.
- При доступном UUID текущее состояние async endpoint может перевести ранее pending запрос в enqueued; только после этого проверяется merge queue entry.
- Старые маркеры без UUID совместимы и читаются как enqueued. Legacy UUID marker используется для read-only polling, но неизвестный subtype не превращается в доказательство удаления из queue.

Изменения записаны без миграции БД: используется имеющееся поле `LandingAttempt.FailureCode`, DTO расширен необязательным полем.

### Тесты и проверка

Добавлены Domain tests для pending/enqueued/legacy transitions, Application tests для передачи статуса в AutoSync и сохранения completion, CLI tests на expired pending UUID и queue removal classification, а также Application test на отсутствие повторной Execute dispatch.

Последний полностью зелёный baseline — CI [#45796862947](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45796862947), exact head `e2cc19ed38937fe3cc86d38f10f23b9cc6f7fb28`. Он не включает новый срез. Точный новый implementation head после source/test и журнала должен пройти полный Linux/Windows/macOS CI до принятия изменений.

### Остаток

- Live acceptance в disposable GitHub repository: pending дольше 24 часов, pending→enqueued, expired enqueued UUID при живой queue entry, queue removal, закрытый без merge PR, eventual merge и PR/base/head drift.
- Classic tag protection `v*` и required reviewers Production Environment всё ещё не подтверждены администратором. Production tag/deploy запрещены до проверки.
- PR #230 остаётся открытым; не сливать его как часть релизной публикации.


## Дополнение — корректировки после первого CI нового async-merge среза (26 ноября 2026)

CI [#45841796175](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45841796175), run 1658, выявил два дефекта в добавленных тестах:

1. Application test обращался к `GitHubMergeRequestStatus` у `LandingAttemptDto`, хотя вычисляемое свойство принадлежит entity `LandingAttempt`. Удалено только это избыточное DTO assertion; тест продолжает проверять сохранённый marker на entity после reload. DTO без необходимости не расширялся.
2. Четыре Domain fixture передавали 40-символьную строку как `candidateCheckOutputHash`, хотя это SHA-256 длиной 64. Исправлены четыре тестовых значения, production validation не менялась.

В Linux логах подтверждено: один compiler error `CS1061`, после которого Application test executable не был создан; это не отдельный runtime test failure. Четыре Domain test failures имели одну причину — invalid test input length. API, CLI (701 tests), Infrastructure, Web и Worker на Linux прошли. Windows/macOS ещё исполнялись на момент разбора, поэтому общий run остаётся красным/незавершённым и не принимается как validation.

Исправления source/tests записаны в `feat/release-management`:
- `78b8edd8a2cf889ad15b7388e8de7aec495750d7` — убрать несуществующее DTO поле из теста;
- `605e0cfddd7f5af9241f0ffba35de06e27178e6f` — исправить хеш-фикстуры на 64 символа.

Журнал разработки дополнен подробным разбором. Новый полный CI должен быть запущен на точной вершине после следующих записей журнала; только зелёные Linux/Windows/macOS подтверждают этот срез.


### Финальный результат первого CI pending/enqueued correction (26 ноября 2026)

Полный run [#45841796175](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45841796175), run 1658, завершился неуспешно на Linux, Windows и macOS. В логах Windows подтверждены те же причины, что и в Linux:

- Один compiler error CS1061: тест обращался к GitHubMergeRequestStatus у LandingAttemptDto, хотя свойство принадлежит Entity. Исправлено удалением лишнего DTO assertion; тест продолжает проверять persisted marker у Entity после reload.
- Четыре Domain test failures возникли из-за ошибочной длины fixture: 40 символов передавались как candidateCheckOutputHash, хотя требуется 64-символьный SHA-256. Исправлены четыре значения, production validation не ослаблялась.
- Application test failure был следствием отсутствующего executable после compile error, а не самостоятельным runtime failure.
- На Linux API, CLI, Infrastructure, Web и Worker прошли. На Windows/macOS API и CLI прошли; общий run остаётся красным из-за Build/Application/Domain.
- Исторический run не засчитывается для исправлений.

Исправления source/test находятся в feat/release-management: 78b8edd8a2cf889ad15b7388e8de7aec495750d7 и 605e0cfddd7f5af9241f0ffba35de06e27178e6f. Журнал с финальным разбором дополнен в commit 77b9cccc185254ec35ee0f28aaf4dc0e9891f32b.

Текущий implementation HEAD после обновления журнала: 77b9cccc185254ec35ee0f28aaf4dc0e9891f32b. Требуется новая полная Linux/Windows/macOS матрица exact-head; старый CI не подтверждает исправления.


## Дополнение — stale expectation async merge marker (26 ноября 2026)

Полный CI [#45844150177](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45844150177), run 1659, завершился одной и той же ошибкой Application test на Linux, Windows и macOS.

Тест `DeviceRemoteLandingReconciliationHandlerTests.RecordsOnlyTheExactVerifiedGitHubQueueFailureAndKeepsTaskOpen` вызывал совместимый overload `MarkGitHubMergeQueued(requestId)`. Текущая реализация намеренно переводит его в явное состояние `enqueued` и сохраняет `GitHubMergeEnqueued:{requestId}`. Единственная ошибка была в устаревшем ожидании `GitHubMergeQueued:{requestId}`.

Исправлено только тестовое ожидание в implementation branch `feat/release-management`, commit `e7f848ffd604c0ecbe9da6371e07e5299bf7d76c`. Production-код не менялся. Все остальные Application tests, остальные тестовые модули и форматирование прошли на всех ОС; Linux использовал PostgreSQL lane.

**Проверка:** run #45844150177 остаётся красным и не подтверждает test fix. Нужен новый полный Linux/Windows/macOS CI на точной вершине после тестового исправления и записи журнала.

Остаток не меняется: требуется live acceptance GitHub merge queue в disposable repository; требуется административная проверка защиты тегов `v*` и required reviewers для `production`. Production tag/deploy не выполнять до снятия этих блокеров.


## Дополнение — сохранение GitHub merge-queue state между AutoSync-циклами (26 ноября 2026)

### Обнаруженный дефект

Beacon сохранял первоначальный статус GitHub async merge request (`pending`), но переход в `enqueued` видел только внутри текущего AutoSync-цикла. При пропуске цикла статус в БД оставался `pending`. GitHub хранит запись async request ограниченное время; после её истечения UUID endpoint может вернуть not-found, хотя сама запись PR всё ещё находится в merge queue. Поэтому Beacon мог бесконечно оставаться в pending и не мог отличить удаление из очереди от истечения временной async-записи.

### Реализованное исправление

В ветке `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230):

- `LandingAttempt.RecordGitHubMergeEnqueued(requestId)` монотонно переводит только точный сохранённый UUID из pending в enqueued; повторный вызов идемпотентен. Несовпадающий UUID, unknown state и состояние вне ExecutePending отклоняются.
- Новый device-only endpoint `POST /v1/devices/me/remote-landings/status` сохраняет только явное состояние enqueued для открытого PR. Сервер проверяет закреплённую станцию, runtime, latest landing attempt, UUID, Source/Base, последнюю успешную Review, Approve и отсутствие активной pipeline session. Отчёт не создаёт merge proof и не закрывает задачу.
- Pinned workstation повторно читает точный PR snapshot до status report. AutoSync отправляет этот отчёт отдельно от reconcile и не считает его успешным merge.
- Если async UUID уже истёк, но GraphQL всё ещё показывает действительную queue entry точного открытого PR, AutoSync может восстановить pending → enqueued. Отсутствие entry при неизвестном/истёкшем async request не считается доказанным удалением из очереди.
- Failure reconciliation принимает явный отказ GitHub для точного принятого async request в состояниях pending или enqueued. Generic `CommandCompletionUnknown` без точного accepted request по-прежнему не считается отказом.
- TaskPipelinePanel различает pending и enqueued, показывает локализованные информационные сообщения en/ru/de/ja/zh и не раскрывает UUID.

Добавлены Domain, Application, CLI, API authorization inventory и Web/BUnit тесты, включая идемпотентность, несовпадение UUID, AutoSync round-trip, отсутствие закрытия задачи и проверку, что пользователь видит понятные pending/enqueued сообщения.

### Проверка

- Предыдущий CI [#45850686745](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45850686745) запускался на старом HEAD `9505ab11`, до этого среза. Все три платформы завершили тестовые модули, но завершились ошибкой `Verify formatting`. Linux log сообщил xUnit1026: неиспользуемый параметр `requestId` в theory `HasConfirmedMergeQueueEntry_RequiresEnqueuedEvidence`. Параметр и избыточные InlineData удалены в commit `b5082889d4bb1ceb2b5d71b197b8d6bab9c3e1c0`. Старый прогон не проверяет ни это исправление, ни новый slice.
- Implementation head перед обновлением журнала: `4e042dece7fa251c2b273fb55326af58fbe30bf5`; журнальная запись сохранена отдельным commit `a7f9a5a6abe7292dfeb8d2e473bc58baa01548b9`.
- Новый полный CI требуется для точного post-journal HEAD; текущий slice не считается проверенным до зелёных Linux/Windows/macOS jobs.
- Локальный .NET SDK в этой среде недоступен; локальная сборка не заявляется.

### Оставшиеся блокеры

1. Получить зелёный кроссплатформенный CI на точной текущей вершине.
2. Провести live acceptance async merge queue в disposable GitHub repository: pending expiry, восстановление по live GraphQL entry, queue removal, закрытие PR без merge, завершение merge, расхождение PR/head/base.
3. Классические `v*` tag protection и required reviewers для Environment `production` остаются непроверенными через connector. Production tag/deploy запрещён до административной проверки.
4. Продолжить user-project acceptance: Release/landing UI, RLS/authorization, локализация и ошибки/recovery. SaaS Control Plane и биллинг остаются вне объёма работ.


## Дополнение — устранение CI-регрессий GitHub landing (26 ноября 2026)

CI [#45859153428](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45859153428) был запущен на старом exact head `f1692d47e60afa071cd7bc60cf25d834f4ddc0dd`. Linux и macOS завершились с ошибками; Windows на момент последней проверки ещё выполнял CLI tests. Этот run не валидирует исправления ниже.

### Исправленные причины

- **Build / xUnit CS0407:** тест `LandingAttemptTests.cs:167` передавал bool-returning method group в `Assert.Throws(Action)`. Переведено на statement lambda, игнорирующую return value. Commit `a67f3aaef8235357f52b9ebe31fb39f326a6bf8c`.
- **Audit allowlist:** серверное событие async merge использовало безопасные GitHub repository/PR/request identifiers, отсутствовавшие в общем whitelist. Добавлены узкие типизированные regex-проверки для `owner/repository` и canonical HTTPS PR URL; числовой PR number и UUID request проходят стандартные scalar validations. Добавлены положительные и отрицательные Domain tests. Commits `856b67bb4f2843951eab33d51f46c68c2a9e7f61`, `c65007a9a9af21eee9561353eac662eaff41e30f`.
- **Tenant bypass manifest:** фактическое количество `IgnoreQueryFilters()` в `DeviceRemoteLandingReconciliationHandler.cs` равно 27, а reviewed manifest содержал 18. Запись обновлена на 27. Пересчитаны все 40 manifest entries по текущим исходным файлам — значения совпали. Commit `c38eab3709b4857e07574cf07b4a84347747b8fb`.
- **BUnit Alert assertion:** test ожидает текстовую причину pending merge и отсутствие UUID, но дополнительно проверял внутреннее CSS-имя `mud-alert-info`, которого MudBlazor не выдаёт. CSS-зависимая проверка заменена на semantic assertion `MudAlert.Severity == Info`. Commit `b218893f54fea16792ec005af399e795df1a3a6b`.

### Статус

Исходный async merge-queue implementation и эти исправления находятся в `feat/release-management`, PR #230. Журнал текущей ветки обновлён; последние исходные/test commits перед журнальной записью: `b218893f54fea16792ec005af399e795df1a3a6b`; journal commit: `ffcdcdbb2ac7230f489ee9f5a70ebaf9d2ce85fd`.

Новый full CI требуется на точном post-journal HEAD. Существующий зелёный CI [#45582326452](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45582326452) относится к предыдущему срезу и не включает новую async merge-queue реализацию. Локальный .NET SDK недоступен; локальная сборка не заявляется.

### Следующие шаги

1. Запустить точный cross-platform CI (Linux/Windows/macOS) на post-journal HEAD, исправить оставшиеся build/test/format ошибки.
2. Завершить live async merge-queue acceptance на disposable GitHub repository: pending expiry, восстановление по live GraphQL entry, queue removal, close-without-merge, успешный merge и drift PR/head/base.
3. Администратору подтвердить классическую tag protection `v*` и required reviewers Environment `production`; не создавать production tags и не запускать deploy до проверки.
4. Продолжать user-project acceptance UI, RLS/authorization и локализованных failure/recovery states. SaaS Control Plane и billing не затрагивать.


## Дополнение — совместимость BUnit 1.40 (26 ноября 2026)

CI [#45860968625](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45860968625) на старом HEAD `ffcdcdbb` выявил compile error `CS1061`: pinned bUnit `1.40.0` не предоставляет `FindComponent<MudAlert>()` для `IRenderedComponent<TaskPipelinePanel>`. Production UI не был причиной ошибки.

В `ProjectBeacon.Web.Tests/Bunit/TaskPipelinePanelTests.cs` неподдерживаемая component lookup заменена на существующий DOM API `cut.Find(".mud-alert")`. Проверка оставляет текст pending-состояния и UUID redaction, но не зависит от внутреннего severity CSS-класса MudBlazor. Production severity mapping не менялся.

Исправление: `99d90d9798a43c86458b686852731b36d19c49ed`. Текущий implementation head перед журналированием: `99d90d9798a43c86458b686852731b36d19c49ed`; требуется новый полный CI на exact post-journal HEAD. Старый run не валидирует этот тестовый фикс.


## Дополнение — повторная ошибка совместимости bUnit 1.40 (26 ноября 2026)

CI [#45862576833](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45862576833) на head `c25fe43b` показал, что и `cut.Find(".mud-alert")` недоступен на типизированном `IRenderedComponent<TaskPipelinePanel>` с закреплённым bUnit `1.40.0`. Все три ОС упали на компиляции одного Web-теста. Linux Web/TRX guard затем сообщил об отсутствии седьмого TRX-отчёта — это каскадный эффект несобравшегося Web test assembly.

Исправление в `feat/release-management`, commit `f3a16483b88e53d3831e975002641ebf57216a05`: удалена только DOM/CSS-проверка существования alert. Сохранены assertions на отображение pending-сообщения и отсутствие UUID в пользовательском markup. Production UI не менялся.

**Статус проверки:** старый CI не валидирует этот фикс. Требуется новый full Linux/Windows/macOS CI на точном post-journal HEAD. Локальный .NET SDK отсутствует. PR #230 не сливать до зелёного exact-head CI и live merge-queue acceptance в disposable repository.


## Дополнение — финальная проверка Regex/TOML parity (27 ноября 2026)

В реализации S1/S3 закрыты ещё два расхождения между серверным Version Policy и tag-triggered Python release gate. Изменения находятся в `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230):

### Regex

- .NET валидатор заменён на детерминированную проверку той же переносимой грамматики, которую использует Python.
- Разрешены ровно одна группа захвата (именованная `(?<version>...)` или обычная группа 1), non-capturing groups, look-ahead и согласованный набор escapes.
- Именованный backreference `\k<version>` допускается только после объявления группы.
- .NET-only/несовместимые escapes (`\0`, `\a`, `\x`, `\p` и др.), look-behind, inline options, numeric backreferences, word-class/boundary escapes, вложенные/вычитаемые character classes отвергаются уже при сохранении политики.

### TOML

- Оба обработчика принимают только строковые и целочисленные TOML значения.
- Float, bool, arrays, tables и date/time не считаются version projections. Это устраняет различия в текстовом преобразовании дробных значений между Tomlyn и Python `tomllib`.

### Product UI и тесты

- Domain и Python regression tests покрывают положительные/отрицательные Regex selectors и TOML scalar types.
- Подсказка Project Version sources уточнена во всех пяти локалях (en/ru/de/ja/zh), включая ограничение TOML и Regex.

**Проверка:** implementation source/test HEAD до обновления журнала — `a1a3361ae96332345f9bd3c0f0c04722ff1fd428`. Последний зелёный CI [#45864599646](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45864599646) был запущен на более раннем `149548ce` и не покрывает этот срез. Требуется свежий полный Linux/Windows/macOS CI после обновления журнала. Локальный .NET build не заявляется: SDK отсутствует в среде выполнения.

**Остаток:** live merge-queue acceptance в disposable GitHub repository; подтверждение администратором классической tag protection `v*` и required reviewers Environment `production`; полный exact-head CI и user-project acceptance. До проверки production protection не создавать production tag и не запускать deployment. SaaS Control Plane и billing не затрагиваются.


## Дополнение — исправление компиляции portable Regex validator (27 ноября 2026)

Полная матрица CI [#45898337610](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45898337610) на exact head `cf919a332fb4df18f7be80eb18735b4cfb7ede65` обнаружила одинаковую ошибку компиляции C# на Linux, Windows и macOS: `CS1010/CS1012` в строке 386 `ProjectVersionPolicy.cs` из-за некорректно сохранённого character literal. Ошибки остальных методов и отсутствие test binaries были каскадными, а не независимыми test failures.

Исправлено в `feat/release-management`:

- проверка backslash теперь использует числовой код символа вместо проблемного C# character literal;
- portable Regex scanner переписан на последовательные явные ветви для escapes, character classes, групп и единственного version capture;
- удалено неиспользуемое состояние и восстановлено нормальное форматирование метода.

Текущая реализационная вершина перед журналированием исправления: `3f89c04df347cdc87a1aa5b64185fa3fef424285`. Run #45898337610 проверял старую вершину и **не** валидирует исправление. Нужен новый полный Linux/Windows/macOS CI на post-journal HEAD. Локальный .NET build не заявляется.

Остаток без изменений: live merge-queue acceptance в disposable GitHub repository; классическая tag protection `v*` и required reviewers Environment `production` должны быть подтверждены администратором. PR #230 не сливать до зелёного exact-head CI и live acceptance. SaaS Control Plane и billing вне scope.


## Дополнение — удаление дублирующего хвоста Version Policy catalog (27 ноября 2026)

После устранения C# character-literal ошибки следующий exact-head CI [#45898748221](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45898748221) выявил отдельный структурный дефект: в `ProjectVersionPolicy.cs` после первой полной закрывающей скобки `ProjectVersionPolicyCatalog` остался дублирующий хвост от предыдущего редактирования helper и вторая копия private methods. Компиляторы Linux/macOS/Windows указывали на `CS1022/CS0116` с начала orphan-блока в строке 624. Ошибки test binaries/TRX были каскадом сборки, а не независимыми test failures.

Исправление в `feat/release-management`:
- удалён весь лишний блок после первого полного класса;
- сохранена одна реализация portable Regex validator и по одной реализации Calendar/tag/path и XML/JSON/TOML/Regex readers;
- исходник заканчивается сразу после закрытия класса, без дублированных методов вне namespace/type.

Source head до журналирования: `6efd63f9982dec21cf6d99789af316471043c008`. CI #45898748221 проверял старый `f24e8f15` и не валидирует удаление дубликата. Следующий полный Linux/Windows/macOS CI должен использовать точную post-journal вершину. Локальная .NET сборка не заявляется.

Остаток: завершить exact-head CI; live async merge-queue acceptance в disposable GitHub repository; подтвердить классическую tag protection `v*` и reviewers Environment `production` через администратора. PR #230 не сливать до снятия этих условий. SaaS Control Plane и billing остаются вне scope.


## Дополнение — exact-head CI прошёл на Linux, Windows и macOS (27 ноября 2026)

**Проверенный implementation source HEAD:** `e87f4e6fc962b82102957b6d48c4ea7eac45f475`  
**GitHub Actions:** [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564)  
**Итог:** все три платформенных jobs завершились `success`.

- Linux: Python release-manifest tests, Restore, Build, EF model drift, API/Application/CLI/Domain/Infrastructure/Web/Worker, TRX guards и `dotnet format` — success.
- Windows: Restore, Build, EF model drift, все семь .NET test modules и `dotnet format` — success.
- macOS: Restore, Build, EF model drift, все семь .NET test modules и `dotnet format` — success.
- Это подтверждает исправление предыдущих `CS1010/CS1012` и `CS1022/CS0116`, единый Regex/TOML selector contract и новые regression tests на точном проверенном source SHA.
- Windows/macOS намеренно пропускают PostgreSQL persistence tests через `BEACON_TEST_SKIP_POSTGRES=1`; PostgreSQL service-container lane исполняется в Linux job. Эти platform skips не считаются успешным DB integration test.
- После CI добавлен journal-only commit в implementation branch; source SHA, валидированный run, остался неизменным.

**Оставшиеся условия до merge/release:**

1. Live async merge-queue acceptance в disposable GitHub repository: pending→enqueued, истечение 24h UUID и recovery по GraphQL queue entry, queue removal, direct/queued merge completion, closed-unmerged PR, PR/head/base drift и post-merge check failure. Unit tests/CI не заменяют эту проверку.
2. Администратор должен проверить классическую tag protection `v*` и required reviewers для GitHub Environment `production`. Подключённая интеграция не позволяет подтвердить эти параметры.
3. Провести полный user-project acceptance Release Management: проектные настройки, роли/RLS, UI ошибок, локализация, Candidate/Publish/recovery.
4. PR #230 пока остаётся open/unmerged. Не создавать production tag и не запускать deploy до выполнения пунктов 1–2. SaaS Control Plane и billing не затрагиваются.


## Дополнение — parity JSON Pointer по RFC 6901 (27 ноября 2026)

В implementation branch `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230), обнаружено расхождение: .NET policy validator уже ограничивал escape-последовательности JSON Pointer, но Python release gate оставался permissive и использовал `str.isdigit()` для array indexes.

Исправлено:

- Оба слоя поддерживают пустой pointer `""`, обозначающий корень JSON-документа. В ProjectVersionPolicy он разрешён только для формата Json.
- Сегменты pointer строго декодируют только `~0` и `~1`; любой другой/trailing `~` отклоняется.
- Индексы массивов — только ASCII decimal, `0` или число без ведущего нуля; пустые, signed, Unicode и out-of-range indexes отклоняются.
- Добавлены одинаковые Domain/Python regression tests на root pointer, escaped slash/tilde object keys, malformed escapes, leading zero и Unicode indexes.

Source/test head до последнего journal commit: `74486e8517136165e2705b948cb6e2e50895a0e7`; journal commit в implementation branch: `d24a550cebfa4cb3b0fdf200c04205d4bcacd388`. Новый CI на post-journal head обязателен. Последний ранее зелёный CI [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564) проверял `e87f4e6f` и не покрывает JSON Pointer changes.

### Состояние ветки относительно main

- `main` продвинулся до `ed4dace38357f5062478ba3322148ab352d916ce` (понятные UI-ошибки, версия 0.7.5.10).
- `feat/release-management` отстаёт на один commit. PR #230 сейчас reported as not mergeable; main changes overlap with UI/resource files Release Management.
- Нельзя перезаписывать UI/resource files целиком. Требуется контролируемый three-way merge последнего main commit в feature branch после проверки текущего exact-head CI.

Остаток без изменений: live async GitHub merge-queue acceptance в disposable repository; administrator verification classic tag protection `v*` и required reviewers Environment `production`; user-project acceptance (RLS, roles, errors, locales, Candidate/Publish/recovery). Production tag/deployment не запускать. SaaS Control Plane и billing вне scope.


## Дополнение — RFC 6901 и истечение GitHub async merge UUID (27 ноября 2026)

Реализация находится в отдельной ветке `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230).

### Согласование JSON version source

- Пустой JSON Pointer `""` теперь означает корневой JSON-документ; в VersionPolicy он разрешён только для Json source.
- В обоих валидаторах поддерживаются только escape-последовательности `~0` и `~1`; trailing `~` и остальные коды отклоняются.
- Индексы массивов используют только ASCII decimal без знака и ведущих нулей (кроме `0`). Unicode-цифры, пустые и выходящие за диапазон индексы отклоняются.
- Domain/Python regression tests покрывают root scalar, escaped slash/tilde keys, invalid escapes и array index cases. C# root fixture содержит корректную JSON string literal.

### Истечение pending async merge request

Предыдущая реализация восстанавливала `pending → enqueued`, если после истечения UUID GraphQL ещё показывал live `mergeQueueEntry`, но оставляла попытку бессрочно ждать, если UUID уже не существовал и открытый PR не был в очереди.

Добавлен отдельный terminal failure, не смешивающий неизвестное состояние с подтверждённым отказом:

- При принятии GitHub async request Beacon сохраняет момент принятия через существующий `LandingAttempt.UpdatedAt`; он передаётся в AutoSync DTO без миграции БД.
- Только явный HTTP 404 от lookup точного async UUID классифицируется как истёкший/отсутствующий request. 403, 429, timeout, сетевые и GraphQL ошибки остаются неизвестными/retryable и не переводят attempt в Failed.
- После 24 часов workstation выполняет GraphQL lookup. `GitHubAsyncMergeFailed` отправляется только при точном `OPEN` PR и отсутствующей `mergeQueueEntry`. Наличие queue entry восстанавливает `enqueued`; для ранее подтверждённой enqueued-записи продолжает действовать отдельная проверка удаления из очереди.
- Сервер снова проверяет device pin, точный attempt/request UUID, PR/source/base и Review proof. В Failed переходит только landing attempt; Task остаётся InProgress/Approved, success proof не создаётся и Task не закрывается.
- Добавлены CLI tests для границы 24 часа, HTTP 404 vs 403/429/timeout и Application test для передачи pending timestamp в AutoSync. Диагностика AutoSync больше не утверждает, что всякий сбой означает именно удаление из merge queue.

### Проверка

- Source/test head перед последним journal commit: `a6efad9df6a61d0c279e931fe7b93e5cd06db3c0`; Development Journal commit: `c4888617ae82777bc3987004057c123594a10a8c`.
- Последний полный зелёный CI [#45899056564](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45899056564) проверял более ранний SHA `e87f4e6f`, не включает ни RFC 6901, ни expiry change.
- На текущий head новый Actions run не найден, combined status пуст. GitHub connection не предоставляет workflow-dispatch write action, а CI не слушает PR `synchronize`; local .NET SDK недоступен. Нельзя обозначать текущий head как проверенный.
- Python smoke exercise для JSON Pointer root/escape/index поведений прошёл, но не заменяет CI.

### Остаток

1. Получить полный CI Linux/Windows/macOS на текущий post-journal head.
2. Влить актуальный `main` через настоящий three-way merge: `main=ed4dace38357f5062478ba3322148ab352d916ce`, feature branch на один commit позади; PR #230 reported not mergeable из-за пересечения UI/resource файлов. Не force-update refs и не перезаписывать UI ресурсы.
3. Провести live acceptance async merge queue в disposable GitHub repository (pending/enqueued, expired UUID с queue entry и без него, queue removal, direct/queued completion, закрытый unmerged PR, source/base drift, failed post-merge check).
4. Администратору проверить classic tag protection `v*` и GitHub Environment `production` required reviewers. Production tag/deploy не создавать до проверки.
5. Продолжить user-project acceptance: RLS/roles, localized UI/error states, Candidate/Publish/recovery. SaaS Control Plane и billing остаются вне scope.


---

## Дополнение — exact-head CI, release manifest hardening и merge-commit policy (27 ноября 2026)

Implementation: ветка `feat/release-management`, PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230).

### Проверка CI

- CI [#45955852379](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45955852379) успешно проверил implementation SHA `e02e4d7579c3251e7b6ee5b82c8eaf7fc4611ad9` на Linux, Windows и macOS.
- Все три платформы прошли Build, EF model validation, семь test modules и formatting.
- Linux TRX guard прошёл с 2 762 результатами, одним ожидаемым platform-conditional skip и нулём failures. Новые Prepare/Verify/Publish handler tests и ReleasePrepare symlink tests вошли в этот run.
- Следующие commits добавили явный merge-commit contract и regression assertion. Их проверяет новый exact-head run #45957896644; до его завершения нельзя считать текущий head CI-валидированным.

### Безопасность ReleasePrepare manifest

- ReleasePrepare проверяет .beacon и .beacon/release.json на symlink/junction перед записью; dangling symlink также распознаётся через LinkTarget.
- Это предотвращает запись release manifest за пределы выделенного release worktree.
- ReleasePrepare использует один resolved filesystem path для записи и SHA-256 проверки манифеста.
- Регрессионные тесты покрывают безопасный путь, symlink каталога .beacon и dangling symlink файла release.json.

### Merge method — обязательное ограничение

- VerifyLanding требует, чтобы точный подготовленный ReleasePrepare commit SHA оставался предком удалённой target branch. Release tag validator также ищет single-parent preparation commit и Beacon-Release trailer.
- Проверка на временном Git repository подтвердила: merge commit сохраняет исходный ReleasePrepare SHA в ancestry; squash создаёт новый SHA и исходный коммит не является предком. Rebase также переписывает SHA.
- Поэтому Release PR должен сливаться через merge-commit strategy. Для Merge Queue администратор задаёт Merge method=merge. Без очереди repository default strategy или явный выбор Owner/Admin должен создавать merge commit; общий GitLand executor не меняет merge_method обычных task PR.
- Если для ветки настроена GitHub Merge Queue, её отдельный административный параметр **Merge method** также должен быть установлен в **merge**. merge_method в API поддерживается для direct merges; merge queue использует метод, выбранный в настройках очереди.
- VerifyLanding остаётся fail-closed при отсутствии исходного prepared SHA. В этом случае Candidate не создаётся, PublishRelease нельзя вызвать, production tag/deploy не запускается.
- Repository metadata сообщает, что разрешены merge commits, squash и rebase. Сама настройка Merge Queue и защита ветки через текущую GitHub integration не читаются. Это не доказывает, что очередь использует неправильный метод; параметр остаётся непроверенным.

### Текущий статус PR и остаток

- PR #230 открыт, не слит. Текущий implementation head: `8ddd6f535eb5d3bab1886d8829ec9ffa5a4f9820`; base main: `ed4dace38357f5062478ba3322148ab352d916ce`. Feature branch не отстаёт от main.
- Текущий exact-head CI: [#45957896644](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45957896644), ещё выполняется.
- Для завершения live acceptance всё ещё нужен отдельный writable disposable repository с настроенной Merge Queue. Деструктивные queue scenarios нельзя запускать на ProjectBeacon.
- Администратор должен подтвердить Merge Queue method=merge, classic tag protection `v*` и required reviewers Environment `production`.
- После этого остаётся end-to-end user-project acceptance: ReleasePrepare → PR merge → immutable Candidate → Publish/tag → Actions workflow → deployed version/SHA, включая recovery/failure paths.
- Не создавать production tag/deployment и не вливать PR #230 до выполнения acceptance blockers. SaaS Control Plane и billing не затрагивать.


## Дополнение — required checks для GitHub Merge Queue (27 ноября 2026)

- По документации GitHub merge queue требует, чтобы CI слушал отдельное событие `merge_group` с типом `checks_requested`. Обычных `pull_request` и `push` событий недостаточно: без merge-group check GitHub ожидает обязательный статус и не может завершить элемент очереди.
- В implementation branch `feat/release-management` добавлено событие `merge_group: types: [checks_requested]` в `.github/workflows/ci.yml`. Оно запускает ту же полную Linux/Windows/macOS матрицу и публикует те же имена required checks для синтетической merge-group ветки.
- Этот workflow change ещё не покрыт завершённым exact-head CI; run #45957896644 проверяет предыдущий head и не включает событие merge_group. Новый запуск необходим.
- Дополнительное ограничение: GitHub указывает, что Merge Queue доступна публичным репозиториям, принадлежащим организациям, либо частным репозиториям организаций на GitHub Enterprise Cloud. ProjectBeacon сейчас public, owner type User; merge queue в этом репозитории недоступна по текущим условиям продукта. Для live acceptance нужен отдельный org-owned disposable repository.
- Merge Queue administrator settings включают отдельный параметр Merge method. Release Management требует метод merge, потому что VerifyLanding закреплён за точным исходным ReleasePrepare SHA. Squash/rebase переписывают SHA и не проходят этот серверный барьер.
- Следующие external acceptance blockers: доступ к writable org-owned disposable repository с merge queue; подтверждение Merge method=merge; проверка classic tag protection `v*` и required reviewers Environment `production`.


## Дополнение — проверка immutable annotated tag при конкурентном push (27 ноября 2026)

В implementation branch feat/release-management обнаружена гонка в обработке потерянного подтверждения Git push:

- Предыдущая ветка восстановления проверяла только peeled commit SHA удалённого тега. Если другой writer одновременно создавал одноимённый annotated/lightweight tag на том же Candidate SHA, но с неправильной или отсутствующей Release/Candidate metadata, локальный executor мог принять только совпадение commit SHA.
- Теперь в ветке восстановления Beacon fetch-ит удалённый тег во временный локальный ref refs/beacon/release-tag-verification/<release>/<nonce>; обычный локальный tag ref не перезаписывается.
- Проверяются тип annotated tag, tag name, object target, peeled commit SHA и точные однократные строки ProjectBeacon-Release, ProjectBeacon-Candidate, ProjectBeacon-Candidate-SHA и ProjectBeacon-Version.
- SHA самого annotated tag object сверяется с SHA, опубликованным в git ls-remote, как при восстановлении после спорного push, так и при финальной проверке.
- Три regression facts проверяют точное совпадение metadata, отказ от lightweight/другого target/tag name, отказ от пропущенных/изменённых/дублирующихся identity lines.
- CI и release.yml Linux TRX threshold синхронно повышены до 2 765.

Кодовые commits:
- 3f56a0516068de9e61e4dbe282498366bb25cac1 — удалённая tag identity verification.
- a52a1d1a0ecbc99f7fa809695c2fc457bb1de748 — regression facts.
- efc1fb9d34b0c527d61a7adf82ba5ed91e0b2957, 06150e4c04804df643290b4dbf641b23b94de27a — TRX guard 2 765.
- e5ec37cda058b584c638a42cfe241acd6f8806fe — implementation journal.

Последний проверенный green CI #45955852379 относится к более раннему implementation head e02e4d75. Новые изменения требуют отдельного exact-head CI. Production tag/deployment не выполнялся; live Merge Queue test, администраторская проверка tag protection v* и Environment production reviewers остаются блокерами. Для live queue acceptance нужен отдельный writable org-owned disposable repository.


### Дополнение — fail-closed разбор ls-remote при публикации тега

- После fetch удалённого annotated tag во временный refs/beacon/release-tag-verification/<release>/<nonce> Beacon сверяет SHA tag object с SHA ref, объявленным git ls-remote.
- Проверка повторяется при конкурентном push recovery и в конце publication flow. В дополнение malformed/partial output от ls-remote теперь является отдельным fail-closed условием: частично разобранная карта refs больше не считается достаточным доказательством.
- Локальный bare-remote exercise подтвердил, что fetch в такой временный ref возвращает annotated tag object и peeled commit, а удаление временного ref не перемещает обычный refs/tags ref.
- Code commit: eb1033d9bc19f3dd091c74a43474b029d3a4e811; journal commit: 8b351b84c3ed4d2268cd17e9acab144c945a6f68.
- Новый exact-head CI требуется; предыдущий run #45959467058 покрывает head e5ec37cda058b584c638a42cfe241acd6f8806fe, а не этот follow-up.


## Дополнение — retry publication только с исходным immutable Candidate payload (27 ноября 2026)

- Добавлен Domain regression test FailedAfterTag: после того как immutable tag уже опубликован, failure оставляет Release в FailedAfterTag и не разрешает отмену Candidate; повторный Publish разрешён только с тем же payload, Candidate, tag и deployment endpoint.
- Проверка также фиксирует сохранение PublicationTagPushed и ранее полученного workflow run id, при этом новый retry связывается с новым command id и увеличивает PublicationRevision.
- CI и release.yml Linux TRX floor повышен до 2 766; оба TRX guard blocks должны оставаться одинаковыми.
- Commits: e569c12168bac0da273ca1a0eb75d74d6012ed41 (test), 9b0054661751f96d7683b8a963c40eea622d4667 и 05a894353218347dd2e7791acbd559d39c919e5d (guards), 5ec65e8c3846c4a59e981055994fc0c8c0175d38 (journal).
- Последний завершённый green CI всё ещё относится к более раннему head e02e4d75; актуальная группа annotated-tag and retry changes требует fresh exact-head CI.


### Дополнение — точное сравнение tag identity lines

- Проверка metadata annotated tag теперь сравнивает строки полностью, не удаляя trailing whitespace. Строки Release/Candidate/SHA/version должны совпасть целиком и встречаться ровно один раз.
- В существующий tag-evidence regression fact добавлено отрицательное условие для Release identity line с хвостовым пробелом. Общее число новых test facts не изменилось, Linux TRX threshold остаётся 2 766.
- Code commit: 2de0e5f3dba63c434430900bf6598f82d6c2d17f; test commit: aa85877a7baffdab021fc8833b40ec00e453d27e; journal commit: 85b83094c168e59d9e2359faa72ba1f34707eeab.
- Текущий точный кодовый head требует новой матрицы CI Linux/Windows/macOS.


## Дополнение — fail-closed tag parsing, temporary-ref cleanup и серверное publication evidence (27 ноября 2026)

Implementation: `feat/release-management`, PR #230.

### Tag identity exactness

- Annotated tag message parser больше не использует CR как разделитель строк. Git tag object использует LF; CR остаётся содержимым строки и не может быть нормализован перед exact identity comparison.
- Добавлен regression assertion: Release identity line с trailing CR отвергается, как уже отвергается trailing space, лишний суффикс и duplicate identity line.
- Temporary verification ref теперь удаляется из finally, охватывающего remote fetch и чтение tag object. Удаление выполняется с CancellationToken.None; отмена publication не оставляет временный ref из-за прерывания cleanup.
- Implementation commit: `a633b755aa205be53a7fb583ddd169817fb1b965`; test: `123745108c8f9668b1ef35f906be87dee19ca0f0`.

### Server-side FailedBeforeTag / FailedAfterTag evidence

- Completion handler раньше трактовал любой синтаксически корректный JSON object без свойства `tagPushed` как `false`. Это могло перевести публикацию в FailedBeforeTag и разрешить отмену Candidate, хотя immutable tag мог уже существовать.
- Теперь отсутствие/неверный тип `tagPushed`, malformed JSON или несовпадающая identity трактуются консервативно: tag мог быть опубликован, состояние FailedAfterTag.
- Явное `tagPushed=false` принимается только при полном failure result с action=Publish и точным совпадением projectId, releaseId, candidateId, version, tagName и candidateSha с серверным command payload.
- Добавлен один Application regression fact на exact false, true, missing field, missing result, malformed JSON, mismatched candidate/action/SHA.
- Code/refactor commits: `62b9e67c52f4044c520c51b68d3c967d83456987`, `2555277d727c4ba884eb24446651aa79b5eb6b6a`; test commit: `2aaf5d597c9d2b0f13f4cfe6d0bd94495e800380`.

### CI

- CI и release.yml Linux TRX floors синхронизированы на 2 767. Их полные TRX guard blocks проверены на byte-identical parity.
- Предыдущий точный зелёный run [#45960317995](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45960317995) проверил SHA `85b83094c168e59d9e2359faa72ba1f34707eeab`: Linux/Windows/macOS success; Linux 2 766 test results, 7 modules, one expected platform skip.
- Следующие fixes (CR handling, temp-ref cancellation cleanup, strict server failure evidence and the new test) ещё не входили в этот run; текущий post-journal head требует нового exact-head CI.

### Текущий внешний acceptance

- Live Merge Queue по-прежнему требует writable org-owned disposable repository; ProjectBeacon — public personal-account repository, подключённый пользователь не состоит в организации.
- В eligible org-owned test repository Merge Queue method должен быть `merge`. Администратор также должен подтвердить classic tag protection `v*` и Environment `production` required reviewers.
- Не создавать production tags/deployments и не вливать PR #230 до выполнения live acceptance и проверки protections. SaaS Control Plane и billing вне области работ.


## Дополнение — exact-head CI и fail-closed publication evidence (27 ноября 2026)

- CI [#45960317995](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45960317995) завершился success на implementation SHA `85b83094c168e59d9e2359faa72ba1f34707eeab`.
- Linux/Windows/macOS: Build, EF model checks, все семь тестовых модулей и formatting прошли. Linux TRX: 2 766 результатов, 7 модулей, один ожидаемый platform-conditional skip, ноль failures; symlink gates Passed.
- После этого run добавлены дополнительные hardening changes, которые не входят в проверенный SHA:
  - tag object parser теперь делит строки только по LF, не удаляя CR до exact identity comparison; добавлен тест на trailing CR;
  - temporary tag verification ref cleanup охватывает fetch и использует CancellationToken.None, чтобы отмена запроса не прерывала удаление локального ref;
  - серверный Publish completion принимает tagPushed=false только если failure result содержит typed boolean и точный Publish identity: projectId, releaseId, candidateId, version, tagName и candidateSha. Missing/malformed/mismatched evidence консервативно трактуется как возможный опубликованный tag (FailedAfterTag).
- Добавлен Application regression fact для неполного/несовпадающего tag-pushed evidence. CI и release.yml TRX floors повышены до 2 767; полные guards остаются идентичными.
- Source/test/workflow commits: `a633b755aa205be53a7fb583ddd169817fb1b965`, `123745108c8f9668b1ef35f906be87dee19ca0f0`, `62b9e67c52f4044c520c51b68d3c967d83456987`, `2555277d727c4ba884eb24446651aa79b5eb6b6a`, `2aaf5d597c9d2b0f13f4cfe6d0bd94495e800380`, `21149f8b7534b96365a97b27c345284c73faaa24`, `e99a96d31dfd8d1f3306634b511c33e8154e283c`.
- Журнал implementation branch записал изменения в commit `1db2e3d2a42e435b9ac0671d0d2742b431980559`. Новый exact-head CI требуется на post-journal PR head.
- Live Merge Queue по-прежнему нельзя проверить на ProjectBeacon (public personal-account repository). Нужен org-owned disposable repository с queue method=merge; tag protection `v*` и Environment `production` reviewers требуют administrator verification. Production tag/deploy и PR merge не выполнять до этих проверок.


---

## Дополнение — exact-head CI для publication hardening (27 ноября 2026)

**Проверенный implementation SHA:** `841dd90bbd1e5375558acb20d2b0515c828876d4`  
**CI:** [#45962729055](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45962729055)  
**Результат:** Linux, Windows и macOS — success.

- Все три платформы прошли Restore, Build, EF model validation, семь test modules и formatting.
- Linux TRX: **2 767 результатов**, 7 модулей, 1 ожидаемый platform-conditional skip, 0 failures. Symlink security gates прошли.
- Run включает последние изменения по annotated tag object identity, LF-only parsing с сохранением CR bytes, cleanup временного verification ref при cancellation, fail-closed серверную обработку Publish completion и тест отсутствующего/malformed/mismatched `tagPushed`.
- Порог CI и release.yml синхронизирован на 2 767; полные TRX guard blocks byte-identical.
- Предыдущий Build failure на SHA `1db2e3d2` был вызван неверной областью видимости publication evidence helpers. Исправление `aa50996534283f5b099e91be657dd378cb8d9f29` вошло в зелёный run #45962729055.

### Подтверждённые гарантии публикации

- Remote annotated tag принимается только после проверки advertised tag-object SHA, fetched tag object, точного имени тега, target Candidate SHA и точных identity lines Release/Candidate/version. Каждая обязательная строка должна встретиться ровно один раз.
- При remote fetch используется уникальный локальный ref `refs/beacon/release-tag-verification`; cleanup выполняется в `finally` с `CancellationToken.None`, чтобы отмена запроса не оставила частично записанный ref.
- `tagPushed=false` может перевести публикацию в FailedBeforeTag только при полном result JSON с typed boolean и точным совпадением project/Release/Candidate/version/tag/SHA с исходной Publish-командой. Missing, malformed или mismatched evidence консервативно означает, что тег мог быть опубликован, то есть FailedAfterTag.
- Подтверждение Published дополнительно требует точного Candidate, annotated tag identity, завершённого успешного Release workflow и равенства production version/gitSha Candidate.

### Остаток до merge/release

1. Live Merge Queue не проверена: ProjectBeacon — публичный personal-account repository, текущий подключённый аккаунт не состоит в организации. Нужен отдельный writable org-owned disposable repository.
2. Workflow CI содержит `merge_group: checks_requested`; это необходимая публикация required checks на synthetic merge-group refs, но фактическое поведение в live queue ещё не доказано.
3. Merge Queue method в тестовой организации должен быть `merge`, иначе исходный ReleasePrepare SHA не сохранится в ancestry. Без очереди direct merge должен использовать merge-commit strategy через настройки репозитория или явный выбор Owner/Admin; общий GitLand не форсирует её для обычных PR.
4. Администратор должен проверить classic tag protection `v*` и required reviewers Environment `production`.
5. End-to-end acceptance: ReleasePrepare → merge → Candidate → Publish/tag → Release workflow → production version/SHA, включая отказ и retry.

PR #230 остаётся open/unmerged. Production tag и deployment не запускались. SaaS Control Plane и billing не менялись.

Implementation journal: `DEVELOPMENT_LOG.md`, commit `cc89faa0d3d0c078bad27711fb4dfc5d11628f81`.


---

## Дополнение — принцип наименьших привилегий для Release workflow (27 ноября 2026)

В `.github/workflows/release.yml` найдено избыточное разрешение: workflow-level `packages: write` наследовалось jobs тестирования, сканирования и production deploy, хотя запись в GHCR необходима только image publisher.

Исправлено в implementation commit `e1100e783b56100228a435b8b42ff753731dfb47`:

- Workflow-level permissions: только `contents: read`.
- `client`: `contents: write` — создание GitHub Release и загрузка клиентских архивов.
- `image`: `contents: read`, `packages: write` — публикация контейнера в GHCR.
- `scan`: `contents: read`, `packages: read` — pull и Trivy scan immutable image.
- `tests` и `deploy`: только унаследованный `contents: read`, без package write.
- Production secrets, SSH deployment и порядок gate jobs не менялись.

Статическая проверка файла подтвердила: package write присутствует только в image job; package read — только в scan; глобального package write больше нет.

Предыдущий exact-head CI #45962729055 был зелёным на SHA `841dd90bbd1e5375558acb20d2b0515c828876d4` (Linux/Windows/macOS, 2 767 Linux TRX результатов), но не включает изменение permissions. Run #45971210327 проверяет более поздний journal head `cc89faa0d3d0c078bad27711fb4dfc5d11628f81`, также до permission change; он не подтверждает эту правку. Требуется новый exact-head CI после обновления журнала.

PR #230 остаётся open/unmerged. Production tags/deployments не запускались. SaaS Control Plane и billing вне scope.


---

## Дополнение — fail-closed классификация результата tag push (27 ноября 2026)

В `ReleasePublishExecutor` обнаружена опасная ветка: после неуспешного/прерванного `git push` повторный `ls-remote` мог завершиться ошибкой или вернуть некорректный список, но исполнитель возвращал `tagPushed=false`. Сервер принимает точный typed false как доказательство `FailedBeforeTag`, после чего Candidate можно было отменить, хотя тег мог фактически появиться.

Исправление в implementation branch `feat/release-management`:

- Добавлен helper `MayHavePublishedTag`: failed remote inspection или malformed listing всегда означает «тег мог быть опубликован». False допустим только когда успешный корректный список доказывает отсутствие тега Candidate либо другой target SHA.
- Первичный `ls-remote` failure и malformed output теперь также дают `tagPushed=true`; повторная попытка возможна только с тем же immutable Candidate.
- Существующий remote tag, который нельзя fetch/verify, консервативно считается потенциально опубликованным.
- После неуспешного push follow-up lookup, который сам не завершился или дал malformed listing, больше не приводит к FailedBeforeTag. При подтверждённом Candidate SHA продолжается проверка annotated tag object; при доказанном отсутствии/другом target допустим FailedBeforeTag.
- Если remote tag указывает на Candidate, но annotated object отличается от ожидаемого, Candidate также нельзя отменить: тег с этим target/name мог быть создан.
- Добавлен CLI regression fact для failed inspection, malformed listing, Candidate target, доказанного отсутствия и конфликтующего target.
- CI и release.yml TRX floors синхронизированы на 2 768; полные guard blocks идентичны.

Implementation commits: `8f9fece3675fedee0bb9b94e13a2454eef843316`, `d099ccedcc74c0c765f984196597e14b2408ce4c`, `efd835e1eed944b8b6f9c9b102a7b158b022c3df`, `3ebaec14f7af24612a8f10bdf0f16b4bce9446fe`, `03fc6bbd933803a73452d8eab812ac5f6361ea07`.

Предыдущий точный зелёный CI #45962729055 проверял SHA `841dd90bbd1e5375558acb20d2b0515c828876d4`, не содержит permission hardening и новый tag-push test. Run #45971667573 проверяет SHA `59752b2379457389ffe2b722f5764830011915bf`, содержит least-privilege permissions, но не содержит новый ambiguous-push fix и ещё выполняет тесты. Требуется новый exact-head Linux/Windows/macOS CI после актуального journal commit.

PR #230 остаётся open/unmerged. Live Merge Queue требует org-owned disposable repository; защиты `v*` и Environment `production` требуют admin verification. Не запускать production tag/deploy до завершения acceptance. SaaS Control Plane и billing не трогать.


### Follow-up — полная проверка пары annotated-tag refs

- Для annotated Git tag Git возвращает tag-object ref `refs/tags/<name>` и peeled commit ref `refs/tags/<name>^{}`. SHA самого tag object не является SHA Candidate commit.
- Предыдущий helper использовал tag-object SHA как fallback для отсутствующего peeled ref. Это могло ошибочно принять неполный remote listing за доказательство отсутствия Candidate tag.
- `MayHavePublishedTag` теперь получает отдельные признаки наличия обоих refs. Failed/malformed listing, отсутствие только одного ref или peeled target, равный Candidate SHA, означают «тег мог быть опубликован».
- False допустим только для успешного корректного listing, где оба refs отсутствуют, либо присутствуют оба refs и peeled commit однозначно отличается от Candidate.
- Если peeled ref отсутствует, но tag-object ref совпадает с уже локально проверенным annotated tag object SHA, existing-tag path может подтвердить target по локально проверенному object. Если object SHA не совпадает или tag ref отсутствует, состояние остаётся неопределённым/потенциально опубликованным.
- После неоднозначного push неполная пара refs приводит к fetch в уникальный verification ref и проверке fetched annotated object. Финальная проверка использует ту же классификацию; SHA tag object больше никогда не сравнивается напрямую с SHA commit.
- Регрессионная проверка расширена случаями отсутствующего tag ref, отсутствующего peeled ref и отсутствующего peeled target. Новых test facts нет; Linux TRX floor остаётся 2 768.

Implementation commits: `36ee76e2891b54377e6be5c42c808cf60ad52ce1`, `3c06b5700a41f3f3c4bbde843224504ef0ddf0fc`. Test commits: `a0b0ed13f74e29f48427db0bb7355205f07d403c`, `d285d2bf1722a94f0187e806d7723ec53554b4ae`. Journal commit: `65471ad201f8cd387a2fc5794ff6bdad4b43b552`.

Ранее запущенный CI #45972484561 проверяет предыдущий head `f1986015703297ad9c6c6f71aecc5198660d9868`, без follow-up по полной паре refs и последнего journal commit. Нужен новый exact-head CI на post-journal ветке.


### Follow-up — аудит всех отрицательных результатов tag push

- Проверены все места, где `EnsureImmutableTagAsync` возвращает `TagPushed=false`.
- False остаётся допустимым только для локального invalid tag при успешном remote listing, доказывающем отсутствие refs; ошибки создания local annotated tag до remote push; либо неуспешного push, после которого успешный корректный listing доказывает отсутствие обоих refs или другой peeled target.
- Ошибки начального remote lookup, malformed output, невозможность fetch уже существующего remote tag, failed/malformed post-push lookup, неполная пара tag/peeled refs, совпавший Candidate target с непроверенной annotation и ошибки final verification оставляют `TagPushed=true`/сохранённое true.
- Если peeled ref отсутствует, публикация может быть подтверждена только после fetch и проверки самого annotated tag object: его object SHA обязан совпасть с remote-advertised tag-object SHA, а содержимое доказывает точный Candidate commit и Release/Candidate identity. Tag-object SHA никогда не сравнивается напрямую с commit SHA Candidate.
- Это закрывает последний найденный путь, при котором отсутствие remote evidence могло ошибочно разблокировать `FailedBeforeTag`.

Exact-head CI [#45973251379](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45973251379) проверяет полный code/test/workflow head `65471ad201f8cd387a2fc5794ff6bdad4b43b552`, включая 2 768-result guard. На момент записи запуск ещё выполняется. Он предшествует journal-only commit этой записи; нельзя считать его success до завершения всех jobs. Новый exact-head run требуется после актуального журнала.

Journal commit implementation branch: `d9a0eb767e586a89a4577da112b0e2b0b7239a9f`. PR #230 остаётся open/unmerged; production tag/deploy не выполняются. SaaS Control Plane и billing не затрагиваются.


---

## Дополнение — автоматический тест Least Privilege для Release workflow (27 ноября 2026)

Добавлен standard-library Python unittest `scripts/test_release_workflow_permissions.py`. Он закрепляет минимальные GitHub Actions permissions и наличие merge-group trigger:

- Workflow-level permissions должны быть ровно `contents: read`.
- Единственный job с `packages: write` — `image`.
- `client` получает только `contents: write` для GitHub Release assets.
- `image` получает `contents: read, packages: write`; `scan` — `contents: read, packages: read`.
- `tests` и `deploy` не получают `packages: write`.
- `ci.yml` должен содержать `merge_group: types: [checks_requested]`.

Тест запускается в авторитетном Linux job обычного CI и повторно в tag-triggered `release.yml` перед release manifest validation. Это защищает не только PR CI, но и будущую tag publication цепочку от возврата чрезмерных permissions.

Implementation commits:
- `342e5f3b3d6c9737fd3d970dcce945864fb1014a` — добавить контрактный тест.
- `555cb8c4b92367197b7560f8170c4095f62da064` и `efd558a5b3a3123f3f7b7ab8cd0efc8b7e48905e` — исправить regex/newline assertion для merge_group.
- `097d1d6a154d111291603194792478919975c5e3` — включить тест в CI.
- `3b0722d8103bfa6bf66385643c6a7192a16845a7` — включить тест в release workflow.
- Журнал implementation branch: `DEVELOPMENT_LOG.md`, commit `500c2e872239c9807920fc94ae67c66095164ab1`.

TRX threshold остаётся 2 768: Python permission test не добавляет .NET test result. CI/release.yml TRX blocks должны оставаться одинаковыми.

Тест и workflow steps новее последнего green CI #45962729055 и не проверены тем run. Run #45973571011 относится к более раннему SHA `d9a0eb767e586a89a4577da112b0e2b0b7239a9f`, не содержит permission test. Требуется новый exact-head Linux/Windows/macOS CI на post-journal PR head. Tag/deploy не запускать до завершения внешнего acceptance. SaaS Control Plane и billing вне scope.


---

## Дополнение — строгая проверка immutable tag и безопасное восстановление Publish (27 ноября 2026)

Implementation находится в `feat/release-management`, PR #230.

### Проверка remote annotated tag

После анализа гонки при конкурентном push усилена проверка неизменяемого тега:

- Сверяется не только peeled commit SHA, но и SHA самого annotated tag object, полученный из `git ls-remote`.
- Remote tag fetch выполняется во временный ref под `refs/beacon/release-tag-verification/<release>/<nonce>`; обычный `refs/tags/*` локально не перемещается.
- Из fetched tag object проверяются object type `tag`, точное имя тега, commit target, Release ID, Candidate ID, Candidate SHA и version.
- Идентификационные строки сравниваются целиком и должны встречаться ровно по одному разу. Trailing whitespace, CR в строке и дубликаты не нормализуются и отклоняются.
- Сверка remote object SHA с advertised SHA выявляет изменение тега между `ls-remote` и fetch.
- Проверка применяется к уже существующему remote tag, восстановлению после неопределённого push и заключительной верификации.
- Временный ref удаляется в `finally` с `CancellationToken.None`; сбой cleanup не должен изменить удалённый тег или маскировать результат проверки.

Добавлены regression tests для валидного annotated tag, неправильного target/tag name/lightweight tag, missing/altered/duplicated identity lines, trailing whitespace и CR.

### Ambiguous push и lifecycle

- При ошибке или timeout push результат считается неопределённым, пока не выполнен успешный и корректно разобранный `ls-remote`.
- Только полный успешный remote listing, однозначно подтверждающий отсутствие Candidate tag, позволяет вернуть `tagPushed=false`.
- Если отсутствует одна из двух refs для annotated tag, listing malformed, remote недоступен или SHA не совпадают, результат консервативно сообщает, что тег мог быть опубликован.
- Сервер принимает отрицательное `tagPushed=false` только из результата с точным совпадением queued Publish: Project, Release, Candidate, version, tag name и Candidate SHA. Отсутствующий/невалидный/mismatched result классифицируется как потенциально опубликованный.
- После immutable tag разрешён retry только с исходным payload. Нельзя отменить Candidate, изменить tag/version/endpoint или начать новую публикацию с другими параметрами; retry повторно проверяет tag и наблюдает workflow/deployment.
- Добавлены Domain regression tests для `FailedAfterTag` retry и Application tests для консервативного чтения publication completion evidence.
- Linux TRX gate синхронизирован на 2 768 результатов в `ci.yml` и `release.yml`; полные TRX guard blocks должны оставаться идентичными.

### Least-privilege Release workflow

- Добавлен `scripts/test_release_workflow_permissions.py` на стандартной библиотеке Python.
- Проверяется global `contents: read`; `contents: write` только для client asset release; `packages: write` только для image publisher; scan получает только `packages: read`.
- Тест выполняется в обычном Linux CI и до release manifest validation на tag-triggered workflow.
- Отдельная проверка подтверждает наличие `merge_group: checks_requested` в CI.

### Проверка и остаток

- Полный cross-platform CI на implementation SHA `85b83094c168e59d9e2359faa72ba1f34707eeab` завершился success: Linux/Windows/macOS, build, EF, все семь test modules, formatting; он предшествует permission workflow test и последующим fixes.
- Актуальный точный запуск [#45974881947](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45974881947) проверяет SHA `500c2e872239c9807920fc94ae67c66095164ab1`, включает remote tag hardening, 2 768-result gate и Python workflow-permission contract. На момент записи все платформы прошли Build и EF; Linux и macOS прошли API/Application, Windows прошёл API; оставались следующие test modules.
- Live Merge Queue acceptance остаётся невозможной в текущем личном публичном ProjectBeacon repo. Нужен writable org-owned disposable repo; в нём администратор должен включить очередь, выставить Merge method=merge и настроить required checks.
- Владелец репозитория также должен проверить classic tag protection `v*` и required reviewers GitHub Environment `production`.
- До завершения этих условий не создавать production tag, не запускать deploy и не вливать PR #230. SaaS Control Plane и billing не затрагивать.


---

## Дополнение — cross-platform CI подтвердил защиту публикации (27 ноября 2026)

Полный exact-head CI [#45974881947](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45974881947) успешно проверил implementation SHA `500c2e872239c9807920fc94ae67c66095164ab1`.

- Linux, Windows и macOS: Restore, Build, EF model check, все семь test modules и formatting — success.
- Linux TRX: 2 768 результатов, один ожидаемый platform-conditional skip, ноль failures; symlink gates Passed.
- Linux permission contract test прошёл: workflow-level contents read; только image job имеет packages write; scan получает packages read; client имеет contents write для GitHub Release assets; tests/deploy не получают packages write.
- `ci.yml` и `release.yml` используют одинаковые TRX guard blocks с порогом 2 768. CI подписан на `merge_group: checks_requested`.

В проверенный head входят:
- fail-closed remote tag handling при неоднозначном push и неполном `ls-remote`;
- проверка annotated tag object SHA и peeled Candidate SHA отдельно;
- temporary verification refs, byte-exact Release/Candidate/version identity lines и rejection CR/trailing whitespace/duplicates;
- публикационный retry только с неизменным Candidate/tag/version/deployment endpoint после потенциальной публикации тега;
- completion proof принимается только при точном совпадении queued Publish identity.

Implementation journal записывает полный результат в commit `46c02964e859eace48a01f4dbc7070a17a00083e`. Этот journal-only commit изменяет текущий PR head и требует отдельного exact-head CI.

### Acceptance blockers без изменений

1. Live Merge Queue acceptance требует writable org-owned disposable repository. ProjectBeacon является public repo с owner type User, а подключённый аккаунт не имеет organization memberships; у него нет подходящего тестового репозитория.
2. На тестовой очереди администратор должен выставить Merge method=merge и required CI checks, затем проверить pending/enqueued, expiry UUID с/без queue entry, removal, direct/queued completion, closed PR, head/base drift и post-merge check failure.
3. Администратор должен подтвердить classic tag protection `v*` и required reviewers Environment `production`.
4. Нужна реальная end-to-end приёмка ReleasePrepare → merge commit → immutable Candidate → Publish/tag → release workflow → deployed version/SHA, включая failure/retry paths.

PR #230 остаётся open/unmerged. Production tag/deployment не создавались. SaaS Control Plane и billing вне scope.


---

## Уточнение — стратегия merge не должна меняться у обычного GitLand (27 ноября 2026)

При ревью обнаружено, что `GitHubLandingMergeExecutor` является общим executor для обычных task PR, а не только для Release PR. Безусловный параметр `merge_method=merge` менял бы merge strategy всех GitHub task PR и нарушал существующую настройку репозитория.

Исправлено:
- Обычный GitLand снова отправляет exact head SHA и `merge_action=default`, не задавая `merge_method`; стратегия обычного PR остаётся на стороне настроек репозитория/merge queue.
- Regression test закрепляет отсутствие `merge_method` в общем GitLand запросе.
- Release PR сохраняет отдельный merge-commit contract. VerifyLanding проверяет ancestry точного ReleasePrepare commit; tag workflow проверяет original commit/trailer. Squash/rebase не создают Candidate и не допускают Publish.
- Для Release PR Merge Queue должна быть настроена на Merge method=merge. Если очередь не используется, repository direct merge default либо выбор Owner/Admin должен создать merge commit.
- Текущий shared GitLand не форсирует особый метод только для Release PR. Если требуется, чтобы Beacon сам инициировал слияние Release PR без участия Owner/Admin или repository policy, это отдельная задача: dedicated Release merge command с соответствующей авторизацией и async state recovery.

Implementation commits: `00e0bc0fbbcf4d0135ddbcd4e3afa16c24614bca`, `d2f1448ce622ed70f4363f1d0043b55f139e3bd1`, `ce6577819e57df8c7907f32f97d8e7dba38874b5`.  
Implementation journal commit: `3fa2dc36a253ddbccde3dddaa545a3a826201cb1`.

CI #45974881947 passed on previous implementation SHA `500c2e872239c9807920fc94ae67c66095164ab1`, but predates this scope correction. A new exact-head CI is required. No PR merge or production publication.


## Дополнение — ограничение write-разрешений Release workflow (27 ноября 2026)

- В implementation branch `feat/release-management` усилен `scripts/test_release_workflow_permissions.py`.
- Теперь regression guard проверяет полную матрицу write-scope: `client` может иметь только `contents: write` для GitHub Release assets; `image` — только `packages: write` для публикации контейнера; `tests`, `scan` и `deploy` не должны получать никаких write-разрешений.
- Это закрывает прежний пробел, когда тест запрещал `packages: write` для tests/deploy, но не обнаруживал бы случайное `contents: write` или `id-token: write`.
- Commit теста: `4ee8625e999adac5dc0958482725ad9aaa3d8658`; journal implementation branch: `34111f1f2f05ba96e5abd5fe109bc012a3c6b445`.
- Прежний CI [#45977749031](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45977749031) проходил до этого теста и не валидирует его. Нужен новый exact-head CI на post-journal SHA.


## Дополнение — локализованные ошибки Release в Roadmap (27 ноября 2026)

- В UI Roadmap поля `PreparationError` и `PublicationError` ранее выводились как единственный английский диагностический текст.
- Добавлены отдельные локализованные сводки и подпись «Диагностика» для en/ru/de/ja/zh. Полный технический текст сохраняется и показывается с переносами строк и длинных строк.
- Для ошибки публикации локализованная сводка отдельно напоминает: если immutable tag уже отправлен, повторная попытка использует тот же Candidate и тег.
- Три новых ключа включены в существующую проверку `UiNotificationResources_AreAvailableInEverySupportedCulture`; LocalizationParityTests проверяет наличие ключей в satellite assemblies всех поддерживаемых языков. Новых test cases и изменения TRX floor нет.
- Реализация: resource commits `beeb442d`, `4ed9f47e`, `a5aaf49b`, `39093054`, `0efcc485`; Roadmap UI `14f6a09f`; localization test `803e655a`; implementation journal `a3ac90d3`.
- Предыдущий CI #45977749031 был зелёным до новых permission/localization changes. Нужен новый exact-head CI на последней feature-ветке.


## Дополнение — восстановление Candidate при Publish и настройка версии после Published (27 ноября 2026)

### Критический дефект Candidate → Publish

- В EF mapping `ReleaseConfiguration` поле `Release.CandidateSnapshot` явно исключено из модели. Immutable snapshot хранится отдельно в `ReleaseCandidateSnapshots`.
- `PublishReleaseHandler` уже загружал отдельную запись snapshot, но перед вызовом `Release.BeginPublication` не прикреплял её к повторно загруженному Release. Доменный метод проверял `CandidateSnapshot != null`, поэтому нормальный production Publish всегда отклонялся после EF reload.
- Тот же разрыв был в `ReleasePublishCompletion`: completion загружал Candidate row отдельно, но не прикреплял его перед `Release.CompletePublication`. Даже после фактического tag/deploy evidence результат отклонялся как FailedAfterTag.
- Добавлен доменный `AttachCandidateSnapshot`, который восстанавливает только точный immutable snapshot, проверяя ProjectId, ReleaseId, CandidateId, pinned workstation, revision Candidate и target version/branch. Метод не изменяет бизнес-состояние и revision.
- Publish handler и completion handler теперь вызывают его после загрузки отдельного Candidate row и до соответствующего domain transition.
- Добавлен Application integration test, который проходит через независимые DbContexts: сохранение Candidate, повторная загрузка и постановка Publish, ещё одна загрузка и workstation completion, затем проверка Published и точных tag/version/SHA. Негативный snapshot с другим CandidateId должен быть отклонён.
- Commits: domain `f764da26`, Publish handler `26ab2c44`, completion `d55e45ab`, integration tests `58761cde` и `4afe9620`.

### Изменение version policy после опубликованного релиза

- SetProjectVersionPolicyHandler ранее продолжал считать Published Release активным, потому что PreparationStatus остаётся Candidate после публикации. Это навсегда блокировало изменение policy и отключение Release Management после полного завершения релиза.
- Проверка active Prepare/Verify теперь исключает Published и Cancelled releases. Непубликованные InProgress/Candidate и неотменённые failed Prepare по-прежнему блокируют изменение.
- Существующий Application test расширен: после публикации immutable Candidate и подтверждения deploy разрешается изменить tag pattern и отключить Release Management; authoritative OfficialVersion сохраняется.
- Commits: handler `c5d591a3`, regression test `4285f6fc`.

### Validation

- Последний предыдущий зелёный full CI [#45977749031](https://github.com/vitalyKl/ProjectBeacon/actions/runs/45977749031) проверял SHA `3fa2dc36` и не содержит этих исправлений.
- CI #45981186013 проверял pre-fix SHA `a3ac90d3`; он также не валидирует Candidate rehydration и Published-policy regression.
- Нужен новый exact-head Linux/Windows/macOS CI после обновления implementation journal. До этого не считать Publish path проверенным.
- PR #230 остаётся open/unmerged; production tags/deployments не запускались. SaaS Control Plane и billing не затрагиваются.


### Follow-up — synchronized TRX floor for Candidate rehydration integration test

- The fresh-DbContext Publish/Completion integration test adds one .NET test fact.
- Raised the required Linux test-result floor to 2,769 in both `ci.yml` and `release.yml`.
- Verified that both thresholds are 2,769 and their complete TRX guard blocks are byte-for-byte identical.
- Workflow commits: `57bca5d7b4a134b18b681280a203bfceef426066`, `2962247979515cee3d4379d16dc79eb98156e2e8`; implementation journal: `24efb8d55b2f2f29bda46196a27cfcfa9bfb1548`.
- The latest previous CI predates the rehydration fix and this gate update; a new exact-head run is required.


## Дополнение — интеграция locale audit из main (27 ноября 2026)

- Main продвинулся на один commit 7258d4e20a4cc5ad32f5a7b0994ae400365a4c5a с аудитом и завершением переводов ресурсов en/ru/de/ja/zh.
- В implementation branch feat/release-management выполнен настоящий двухродительский merge commit b34f72682913cf597cdfe2fc61c024d15d2dd5fa: первый parent feature head 24efb8d55b2f2f29bda46196a27cfcfa9bfb1548, второй parent main 7258d4e20a4cc5ad32f5a7b0994ae400365a4c5a.
- Merge ограничен пятью .resx ресурсами. Новые переводы main сохранены, три ключа ReleasePreparationFailedSummary / ReleasePublicationFailedSummary / ReleaseDiagnosticDetails восстановлены поверх версий main во всех пяти культурах.
- После merge feature branch не отстаёт от main. В implementation journal результат записан в commit 104bde53dbbef9825c1905b11a54126d1e37abff.
- Предыдущий CI #45982946580 проверял до-merge SHA 24efb8d, поэтому не валидирует текущую вершину. Требуется новый exact-head CI после обновления журнала.



## Дополнение — восстановление ресурсов Release Management и аудит публикации (27 ноября 2026)

В PR [#230](https://github.com/vitalyKl/ProjectBeacon/pull/230) после merge main в feature-ветку обнаружена неполная локализационная интеграция: при разрешении конфликтов было восстановлено только три ключа ошибок релиза, а ещё 105 ключей Release Management отсутствовали во всех пяти ресурсных файлах.

Исправления:
- Сравнение текущих пяти `.resx` с feature-parent до merge `b34f72682913cf597cdfe2fc61c024d15d2dd5fa` обнаружило одинаковый набор из 105 отсутствующих ключей: Version Policy, Release/Roadmap, Prepare/Verify/Publish, GitHub Merge состояния и Task Version Impact.
- Все 105 ключей восстановлены из feature-parent во всех пяти культурах: neutral English, German, Japanese, Russian, Chinese. Значения, которые уже изменил последний main locale audit, сохранены.
- Теперь каждый ресурс содержит 1 345 ключей; множества keys во всех пяти культурах совпадают, missing/orphan keys = 0.
- bUnit проверки сообщений GitHub merge и Task Version Impact теперь сравнивают UI с соответствующим `IStringLocalizer<Web>`, а не с английскими литералами.

Отдельно исправлен провал Application теста Publish:
- PublishReleaseHandler отправлял в AuditEvent метаданные Candidate и рабочей станции, которые не входили в allowlist.
- Разрешены только структурированные candidateId/workstationId GUID, candidateSha Git SHA, tagName по safe Git tag policy и integer publicationRevision.
- Необязательный deployment endpoint hostname убран из audit payload; произвольный текст по-прежнему запрещён.
- Добавлены две Domain regression facts: допустимые данные и отклонение malformed identity/SHA/tag/revision.

CI после этих изменений требует минимум 2 771 Linux TRX результата; ci.yml и release.yml guard blocks синхронизированы. Run #45983775039 фиксировал старый провал до этих исправлений и не является валидацией новых изменений. Нужен новый exact-head CI на implementation ветке.

Оставшиеся условия не изменились:
- Live Merge Queue acceptance возможна только в отдельном writable org-owned disposable repository. ProjectBeacon public и принадлежит персональному аккаунту; подключённый аккаунт не состоит в организациях.
- Queue Merge method=merge, classic tag protection `v*`, production Environment required reviewers требуют проверки администратора.
- PR #230 не вливать и production tag/deploy не создавать до завершения проверок. SaaS Control Plane/billing вне scope.


## Дополнение — регрессия локалей и audit metadata после merge main (27 ноября 2026)

- CI #45983775039 на SHA `104bde53dbbef9825c1905b11a54126d1e37abff` выявил четыре Web-тестовые регрессии и одну Application-интеграционную регрессию.
- Application: Publish fresh-DbContext integration test не проходил из-за отсутствия `candidateId` в allowlist audit details. В реализации добавлен узкий набор ключей immutable publication evidence с проверкой GUID/SHA/tag; произвольный текст по-прежнему запрещён.
- Web: tests использовали неправильный тип resource localizer после merge, а пять .resx потеряли 105 ключей Release Management. Восстановлены Release/Roadmap/GitHub merge/Task Version Impact записи во всех пяти культурах; тесты используют реальный `ProjectBeacon.Web.Resources.Web` localizer.
- Все пять resource-файлов имеют ровно 1 345 ключей, missing/orphan key count — 0. Добавлены обязательные проверки локализованных GitHub merge pending/queued/failed и Task Version Impact strings для en/ru/de/ja/zh.
- TaskDetail bUnit fixture использует валидный XML version source в ProjectVersionPolicy.
- CI и Release workflow Linux TRX thresholds совпадают на 2 771; complete guard blocks идентичны.
- Implementation branch `feat/release-management` head после этих исправлений и журнала: `ce4f2a4c6de79af9458342d50d68db3fb74b1efe`. Exact-head CI после этих исправлений ещё не завершён; run #45983775039 остаётся pre-fix failure.
- Остаток: точный CI на post-fix head; live Merge Queue acceptance в eligible org-owned disposable repo; administrator verification Merge method=merge, classic tag protection `v*` и Environment `production` required reviewers. Production tag/deploy не запускать, PR #230 не вливать.


### Follow-up — corrected Web resource marker type in bUnit tests

- The first exact-head build attempt for the audit/localization fixes reported `CS0118`: short type name `Web` resolved to the namespace instead of the generated marker class.
- The generated resource class is internal `ProjectBeacon.Web.Resources.Web`; the Web project explicitly grants `InternalsVisibleTo` to `ProjectBeacon.Web.Tests`.
- Updated tests to request `IStringLocalizer<global::ProjectBeacon.Web.Resources.Web>`.
- Implementation/test branch now has test commits `142fcc19081caa6135b94556fd649bbe47086726` and `0d72f310cacb92619f4435652cc1f24caad6600a`, plus journal commit `419cc890ed93279ace5548e2deda574ddf7b0e27`.
- The CI run #45987044512 targeted the pre-fix head `ce4f2a4c6de79af9458342d50d68db3fb74b1efe`; a fresh full cross-platform run is required on the latest exact head.


## Дополнение — аудит завершения публикации Release (27 ноября 2026)

- В процессе дополнительного ревью после исправления `candidateId` обнаружилось, что ReleasePublishCompletion создаёт несколько audit events с полями `error` и `workflowRunUrl`, не разрешёнными строгим audit schema. Исправление только allowlist-ключа не закрывало success/failure completion path.
- Completion теперь записывает исключительно структурированные поля: candidateId, tagPushed, resultCode, workflowRunId, targetVersion, tagName, candidateSha и deployedSha. Неизвестные значения не попадают в аудит; свободный текст ошибки остаётся в Release.PublicationError/diagnostics, workflow URL — в Release publication evidence.
- Audit schema принимает `tagPushed` как boolean и `workflowRunId` как положительное целое; deployedSha валидируется так же строго, как Candidate SHA.
- Расширены существующие Domain audit assertions и fresh-DbContext Application integration test: после успешного Publish проверяются точные Candidate/version/tag/deployed SHA/run ID и отсутствие workflowRunUrl/free-form error в audit JSON.
- Новых test facts не добавлено; CI и Release Linux TRX floor остаются 2 771.
- Implementation branch: `feat/release-management`; commits `0c47493b`, `7f0f970f`, `32470c63`, `cb2c1d3f`. Полный exact-head CI ещё требуется.
- Merge Queue live acceptance и настройки GitHub production publication остаются отдельными внешними blockers; production tag/deploy и PR merge не запускать.


### Дополнение — структурированные audit evidence публикации и correction CI (27 ноября 2026)

- В completion handler публикации убраны из audit details произвольные тексты ошибок и workflow URL. Добавлен единый `PublicationAuditDetails`, который пишет Candidate id, `tagPushed`, короткий `resultCode` и, когда имеется, положительный workflowRunId.
- Успешное завершение записывает Candidate id, `tagPushed=true`, targetVersion, tagName, candidateSha, deployedSha и workflowRunId. URL и free-form error в audit events не попадают.
- Audit allowlist валидирует Candidate/workstation GUID, Git SHA длиной 40/64 символа, безопасное имя тега и положительный integer workflow run ID. Новые отрицательные случаи покрывают malformed deployed SHA и нулевой workflow ID.
- Fresh-DbContext Publish/Completion integration test теперь читает созданный `project.release.published` AuditEvent и проверяет все ожидаемые значения, отсутствие workflowRunUrl и свободного поля error.
- Последние commits: code `0c47493b8c7b1f77ab56d19d25fa263c9b1fa481`, `7f0f970f5c04ce42e334f6feabde57c5b3aadb34`; tests `32470c633fe1cc4a6d887e5890ba14311b1aae98`, `cb2c1d3fbb809b456720f933b1a59815937813f0`. Два последних test commits расширяют существующие facts, не добавляя новых TRX результатов; минимум 2 771 остаётся достаточным.
- CI #45987044512 проверял промежуточный SHA `ce4f2a4c6de79af9458342d50d68db3fb74b1efe` и выявил `CS0118` в bUnit тестах: короткое имя Web разрешалось в namespace. Исправлено полным именем `global::ProjectBeacon.Web.Resources.Web` в `IStringLocalizer<T>`; Web assembly предоставляет InternalsVisibleTo тестовой сборке.
- Тестовые commits correction: `142fcc19081caa6135b94556fd649bbe47086726`, `0d72f310cacb92619f4435652cc1f24caad6600a`. Промежуточный run не включает эти изменения.
- Implementation branch после correction нуждается в новом exact-head Linux/Windows/macOS CI. Не считать более ранние успешные runs проверкой текущего head.
- Journal commit implementation branch: `b49aa5ebf4b037940122626a647696a870cdea88`. PR #230 остаётся open/unmerged.


## Дополнение — исправление области видимости publication audit helper (27 ноября 2026)

- Exact-head run #45987491865 на SHA `16abf8e891f315af63b14d3546da040e2e9bbd18` выявил ошибку сборки `CS0103: PublicationAuditDetails does not exist in the current context` на Linux/macOS.
- Причина: helper был вставлен в `ReleasePrepareCompletion` перед первым `AddAudit` в файле, тогда как вызовы находились в `ReleasePublishCompletion`. Он не был доступен из класса публикации.
- Исправлено: helper перенесён внутрь `ReleasePublishCompletion` перед его собственным `AddAudit`. Все четыре вызова и объявление теперь находятся в одном классе; дополнительного helper в ReleasePrepareCompletion нет.
- Сопутствующие ошибки запуска API/Application/CLI/Infrastructure/Web/Worker tests на macOS/Linux были следствием failed solution Build и отсутствующих test assemblies, а не самостоятельными assertion failures. Windows на том run дошёл до успешного Build, но run был отменён из-за concurrency.
- Implementation commit: `8d343b437ea06096ecedae5bb29854223f3e05ee`; implementation journal update: `2ad852f6d083004a194466aa98af7c242204d508`.
- Нужен новый full exact-head CI после journal commit; прежний #45987491865 — известный pre-fix failure/cancelled run, он не валидирует исправление.
- External blockers остаются: live Merge Queue acceptance в writable org-owned disposable repository; administrator verification queue Merge method=merge, classic tag protection `v*` и required reviewers Environment `production`. PR #230 не сливать, production tag/deploy не запускать.


## Дополнение — строго типизированные поля audit publication metadata (27 ноября 2026)

- Полный cross-platform CI #45988134913 успешно проверил implementation head `2ad852f6d083004a194466aa98af7c242204d508`: Linux, Windows и macOS прошли Build, EF model validation, все семь test modules и formatting; Linux TRX — 2 771 результатов, 0 failures, ожидаемый platform skip и пройденные symlink gates.
- После зелёного запуска закрыт оставшийся schema hardening крайний случай: раньше общий scalar validator позволял представить `workflowRunId` строкой и `tagPushed` строкой/числом.
- Теперь `workflowRunId` принимается только как положительный Int64 JSON number; `tagPushed` — только как JSON boolean.
- Добавлены negative assertions в существующий Domain audit metadata test; test fact count не изменился. CI и Release workflow TRX floor остаётся 2 771.
- Implementation commits: `3180d3e248169aad31d630f2f6aa9cd3cf3694d2`; regression test commit: `7d3b1ee17c396bb8f3c22b770d0c44f58cc7be31`; implementation journal: `7dfa09fb751a361bcb5e3c74260d69698be55ee0`.
- Строгая типизация появилась после run #45988134913 и требует нового exact-head CI на текущей ветке.
- Live Merge Queue acceptance по-прежнему требует writable org-owned disposable repository. Администратору нужно подтвердить Merge method=merge, classic tag protection `v*` и required reviewers Environment `production`. PR #230 не сливать, production tag/deploy не создавать.

## Решение по remote landing и production safeguards — 27 ноября 2026

Ветка реализации feat/release-management закрыла открытый вопрос об авторитете remote landing. При наличии GitHub PR remote merge выполняется через GitHub как авторитетную систему, а закреплённая workstation выступает исполнителем только после server-side authorization и повторной проверки Candidate/Review evidence. Запрос отправляется через GitHub async merge API с точным PR head SHA и merge_action=default; Beacon не запрашивает bypass_rules, admin merge или force update. Ответы pending/enqueued не считаются landed. AutoSync ждёт MERGED, проверяет merge commit, base ancestry/tree и post-merge check, после чего сервер фиксирует remote landing proof. Прямая локальная модификация remote target ref не конкурирует с GitHub.

Проверка PR #230 на 27 ноября 2026:
- Exact implementation head 0c1d749610fc42a391e85a85430e27cdc2bf0804, GitHub Actions run #46022091769: Linux, Windows и macOS — success; все семь test modules и formatting прошли, Linux TRX floor 2,773 пройден.
- Документальные исправления после этого run ещё требуют exact-head CI на обновлённом implementation head.
- PR #230 остаётся открытым, Ready for review и не слитым.

Внешнее условие перед первым production publish остаётся обязательным и не должно считаться выполненным автоматически:
- repository owner должен проверить tag protection для release tags (запрет update/delete при разрешённом первичном создании доверенным publisher);
- проверить branch/ruleset protection и правила required checks;
- настроить Environment production: required reviewer, запрет self-review, допустимые deployment refs;
- задать PRODUCTION_SSH_HOST, PRODUCTION_SSH_USER, PRODUCTION_SSH_PRIVATE_KEY, PRODUCTION_SSH_KNOWN_HOSTS именно в Environment production.

Repository rulesets endpoint вернул пустой список; branch protection read отвечает 403 для подключённой GitHub-интеграции, а legacy tag protection и Environment settings недоступны. Это означает «не подтверждено», а не «отсутствует». Production tag/deployment не запускать, пока владелец репозитория не подтвердит эти настройки.
