> Historical prompt. Sprint 10 applied this cleanup. Do not run it again as an active plan.

# ProjectBeacon — Documentation Cleanup & Baseline

## Цель

Перед началом дальнейшей разработки привести документацию репозитория в единое актуальное состояние.

Сейчас в репозитории одновременно находятся документы разных этапов проекта. Часть из них описывает старую архитектуру, уже выполненные работы или решения, которые больше не соответствуют текущему коду. Это особенно вредно для coding agents: при `grep`, полнотекстовом поиске и чтении `docs/` агент получает противоречивые сведения и может начать реализовывать уже устаревшую архитектуру.

Твоя задача — провести **documentation baseline cleanup**, не меняя бизнес-логику проекта.

---

# 1. Обязательные документы для чтения

Перед изменением документации прочитай:

1. `ProjectBeacon-master-roadmap-v1.md`
2. `ProjectBeacon-code-review-roadmap-v3.md`
3. `ProjectBeacon-ui-ux-review-roadmap-v1.1.md`

Эти документы являются текущим источником плана работ.

Также изучи:

- `README.md`
- `AGENTS.md`
- текущую структуру `ProjectBeacon.Web`
- текущую структуру solution/projects

Не считай старые документы источником истины без проверки по текущему коду.

---

# 2. Главный принцип

После завершения этой задачи должно выполняться следующее правило:

> Один актуальный документ должен описывать текущую архитектуру и направление развития, а исторические документы не должны участвовать в обычном поиске рабочего контекста.

Нельзя оставлять несколько документов, которые одновременно претендуют на роль authoritative architecture / roadmap и содержат разные модели системы.

---

# 3. Документы, которые нужно архивировать или удалить

## 3.1 `dotnet project docs/ProjectBeacon-analysis.md`

Проверить содержимое.

Этот документ является историческим audit/review и содержит сведения, которые уже не соответствуют текущему коду.

Не использовать его как рабочую документацию.

Действие:

- перенести в `archive/` как historical document;
- либо удалить, если историческая ценность не нужна.

После переноса он не должен находиться в обычном `docs/`-поиске.

---

## 3.2 `dotnet project docs/ProjectBeacon-fix-plan.md`

Это старый execution backlog.

Многие пункты уже реализованы, изменены или заменены новой архитектурой.

Действие:

- удалить или перенести в `archive/`.

Не пытаться поддерживать этот документ актуальным.

---

## 3.3 `dotnet project docs/ProjectBeacon-forward-plan.md`

Документ основан на более старом состоянии репозитория.

Его полезные идеи уже должны быть представлены в актуальном backend roadmap.

Действие:

- извлечь из него только те пункты, которые всё ещё имеют значение;
- проверить их наличие в `ProjectBeacon-code-review-roadmap-v3.md`;
- сам документ после этого архивировать или удалить.

---

## 3.4 `dotnet project docs/ProjectBeacon-own-swapper-plan.md`

Основная реализация собственного swapper уже существует.

Действие:

- удалить или архивировать;
- не оставлять как active implementation plan.

Если есть важные окончательные архитектурные решения — перенести их в актуальную архитектурную документацию.

---

## 3.5 `dotnet project docs/ProjectBeacon-dotnet-roadmap-v2.md`

Это старый roadmap.

Он должен быть полностью заменён новым roadmap.

Действие:

- не редактировать для дальнейшего использования;
- архивировать или удалить.

Главный рабочий roadmap:

`ProjectBeacon-master-roadmap-v1.md`

Детали:

`ProjectBeacon-code-review-roadmap-v3.md`

`ProjectBeacon-ui-ux-review-roadmap-v1.1.md`

---

# 4. Документы, которые нужно обновить

## 4.1 `README.md`

README должен описывать **текущее состояние проекта**, а не историю.

Проверить и обновить:

- архитектуру;
- dependency model;
- workstation/daemon model;
- user-level Agents configuration;
- project-level configuration;
- MCP;
- Worker;
- authentication/security;
- frontend structure;
- актуальные authoritative documents.

Не оставлять ссылки на старые `v2` roadmap/design documents как authoritative.

README должен ссылаться на:

- Master Roadmap;
- Backend Review Roadmap;
- UI/UX Review Roadmap;

только как на текущую рабочую документацию.

---

## 4.2 `AGENTS.md`

Это особенно важный файл, поскольку его читают coding agents.

Он должен описывать только актуальные правила.

Проверить:

- архитектурную dependency graph;
- security model;
- Actor/Capability model;
- workstation boundaries;
- MCP trust model;
- Worker state;
- user/project/runtime scopes;
- documentation hierarchy.

Особенно важно удалить устаревшие утверждения о том, что Worker ещё не существует.

Также убрать старую dependency graph, если она не соответствует фактической структуре solution.

---

## 4.3 `dotnet project docs/agent-prompt-template.md`

Этот документ нельзя оставлять привязанным к старому:

`ProjectBeacon-fix-plan.md`

Обновить его так, чтобы agent workflow начинался через:

1. `ProjectBeacon-master-roadmap-v1.md`
2. соответствующую секцию backend roadmap;
3. соответствующую секцию UI/UX roadmap;
4. текущий phase state.

Не ссылаться на архивные планы.

---

## 4.4 `dotnet project docs/features.md`

Проверить на наличие старых архитектурных предположений.

Особенно проверить:

- модель запуска llama-swap;
- ownership workstation/model runtime;
- MCP terminology;
- model backends;
- project/user scope.

Этот документ либо:

- привести к чистой актуальной feature specification;

либо объединить его полезное содержимое с актуальными architecture/product documents и удалить дубликат.

Не оставлять две разные версии одного и того же описания функциональности.

---

## 4.5 `dotnet project docs/task-pipeline-local-agents.md`

Документ содержит полезный material, но часть implementation assumptions уже устарела.

Проверить и обновить:

- workstation architecture;
- agent runtime;
- authorization/capabilities;
- task/pipeline model;
- subtask scoping;
- actual implementation status;
- OpenCode assumptions.

Если часть текста описывает только историческое решение — вынести её в archive.

---

## 4.6 `dotnet project docs/mcp-host.md`

Оставить как рабочий документ.

Обновить:

- Actor model;
- API token capabilities;
- local MCP trust model;
- project/task scope;
- workstation/chat boundary;
- agent runtime abstraction;
- актуальный список инструментов.

Особенно чётко разделить:

```text
Local MCP
    = trusted local workstation process

Remote/API access
    = authenticated control-plane interaction
```

---

# 5. Документы UI/UX

## 5.1 `dotnet project docs/UI Design Migration Specification.md`

Оставить как UI design reference, но привести его в соответствие с текущим кодом и новым UI/UX roadmap.

Особенно проверить:

- sidebar geometry;
- design tokens;
- surface hierarchy;
- spacing;
- typography;
- component geometry;
- responsive rules;
- current page inventory.

Важно:

если фактический код осознанно расходится со старой спецификацией, не надо сохранять противоречие.

Либо:

- изменить код согласно актуальному design system;

либо:

- изменить specification согласно новому принятому решению.

После этого должна существовать одна однозначная версия design rules.

---

## 5.2 `dotnet project docs/ProjectBeacon-ui-spec-corrected-geometry.md`

Использовать как источник исправленной geometry information.

Если его данные уже полностью перенесены в основную UI specification:

- объединить содержимое;
- затем удалить этот отдельный документ.

Не оставлять два документа с design token tables, если они могут расходиться.

---

## 5.3 UI/UX roadmap

Основной текущий UI/UX planning document:

`ProjectBeacon-ui-ux-review-roadmap-v1.1.md`

Не создавать ещё несколько конкурирующих UI migration plans.

---

# 6. Master roadmap

Текущий master orchestration document:

`ProjectBeacon-master-roadmap-v1.md`

Он должен стать верхнеуровневой точкой входа для coding agents.

Его задача:

- определить порядок фаз;
- определить зависимости между UI/UX и backend;
- указывать конкретные секции двух специализированных roadmap;
- определять phase gates.

Не дублировать в нём подробные технические решения из backend/UI документов.

---

# 7. Документационная иерархия после cleanup

После завершения работы структура должна стремиться к следующей модели:

```text
README.md
AGENTS.md

dotnet project docs/
    ProjectBeacon-master-roadmap-v1.md
    ProjectBeacon-code-review-roadmap-v3.md
    ProjectBeacon-ui-ux-review-roadmap-v1.1.md

    ProjectBeacon-design-doc-v3.md
    architecture.md
    security.md
    workstation.md
    agent-runtime.md
    context.md
    pipeline.md
    evaluation.md
    deployment.md
    mcp.md

    UI Design Migration Specification.md
    cold-diff-review.md
    agent-prompt-template.md

archive/
    historical documents...
```

Не нужно автоматически создавать все перечисленные документы.

Создавай отдельный документ только тогда, когда соответствующая область действительно требует независимо поддерживаемой документации.

---

# 8. Правило для исторических документов

Исторические документы можно сохранить.

Но они должны находиться в:

```text
archive/
```

или другом явно обозначенном historical location.

Обычный поиск по рабочей документации не должен включать архив.

Не оставляй в основном `docs/` документы с названиями:

- analysis;
- fix-plan;
- forward-plan;
- old roadmap;
- migration plan;

если они больше не являются active specification.

---

# 9. Проверка на дубликаты и противоречия

После restructuring выполни поиск по всей документации.

Ищи как минимум:

- `roadmap`
- `architecture`
- `llama-swap`
- `worker`
- `agents`
- `mcp`
- `workstation`
- `authorization`
- `security`
- `sidebar`
- `spacing`
- `radius`
- `design tokens`

Для каждого результата проверь:

1. Это актуальная информация?
2. Это историческая информация?
3. Это дубликат другого документа?
4. Это implementation note?
5. Это active specification?

Если два документа описывают одну систему по-разному, нельзя просто оставить оба.

Нужно определить один authoritative source.

---

# 10. Не изменять код

Эта задача посвящена исключительно documentation baseline.

Не изменяй:

- Domain;
- Application;
- Infrastructure;
- API;
- Web;
- CLI;
- Worker;

за исключением кода, необходимого только для документационной инфраструктуры, если такой код действительно требуется.

Не исправляй backend или UI проблемы в рамках этой задачи.

Создай для них backlog через существующие roadmap.

---

# 11. Git / verification

После завершения:

1. Проверить `git status`.
2. Убедиться, что изменялись только документационные файлы.
3. Выполнить поиск по `docs/` и убедиться, что архивные документы больше не попадают в обычный рабочий поиск.
4. Найти все оставшиеся ссылки на:
   - `ProjectBeacon-design-doc-v2.md`
   - `ProjectBeacon-dotnet-roadmap-v2.md`
   - `ProjectBeacon-fix-plan.md`
   - `ProjectBeacon-forward-plan.md`
   - `ProjectBeacon-own-swapper-plan.md`
5. Каждая оставшаяся ссылка должна быть либо:
   - намеренно исторической;
   - либо заменена на новый authoritative document.

---

# 12. Acceptance Criteria

Задача считается завершённой только когда:

- [ ] Нет двух active roadmap, описывающих один и тот же план.
- [ ] Нет старого roadmap в основном рабочем documentation tree.
- [ ] README указывает актуальную документацию.
- [ ] AGENTS.md указывает актуальную документацию.
- [ ] Agent prompt template больше не ссылается на старый fix-plan.
- [ ] Устаревшие architecture claims удалены из active docs.
- [ ] Historical documents вынесены в archive либо удалены.
- [ ] UI/UX specification не противоречит принятому UI/UX roadmap.
- [ ] Agents scope явно определён как user-level configuration.
- [ ] Workstation/runtime scope отделён от user-level agent configuration.
- [ ] MCP documentation отражает текущую модель доверия.
- [ ] Worker documentation отражает фактическое состояние проекта.
- [ ] Поиск по active documentation не возвращает obsolete implementation plans.
- [ ] Код проекта не изменён без необходимости.
- [ ] Изменённые документы проверены на внутренние ссылки.

---

# 13. Что делать после cleanup

Не переходить сразу к произвольным feature tasks.

После завершения documentation cleanup:

1. Зафиксировать изменения отдельным commit.
2. Использовать `ProjectBeacon-master-roadmap-v1.md` как единственную точку входа для следующих agent sessions.
3. Для каждой следующей задачи читать только связанные секции backend/UI roadmap, указанные master roadmap.
4. Архивные документы не использовать как источник требований, если master roadmap явно не требует обратиться к историческому решению.

Главная цель этой задачи:

> **Сделать documentation tree надёжным источником контекста для локального coding agent.**
>
> После cleanup поиск по репозиторию должен преимущественно возвращать текущую архитектуру, текущий UI/UX и текущий план работ, а не историю того, как проект когда-то был устроен.