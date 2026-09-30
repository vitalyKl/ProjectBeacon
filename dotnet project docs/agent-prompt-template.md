# Промпт для агента (шаблон)

## Инструкция

Агент получает инструкцию от master roadmap и specialized roadmaps. Не использовать этот файл как standalone prompt.

## Routing

Перед началом задачи:
1. Прочитай `AGENTS.md` — цели, non-goals, конвенции, security-правила, pitfalls.
2. Определи текущую фазу в `dotnet project docs/ProjectBeacon-master-roadmap-v1.md`.
3. Прочитай указанные секции:
   - Backend roadmap: `dotnet project docs/ProjectBeacon-code-review-roadmap-v3.md`
   - UI/UX roadmap: `dotnet project docs/ProjectBeacon-ui-ux-review-roadmap-v1.1.md`
4. Прочитай все файлы из секции «Контекст» задачи.
5. Выполни подшаги задачи. Меняй только то, что требуется задачей; не рефактори соседний код.
6. Правила:
   - английский UI-текст — только через `IStringLocalizer<Web>` (resx);
   - не трогай `AGENTS.md` и `archive/docs/` если задача прямо об этом;
   - не коммить, если не просят.
7. Валидация: `dotnet build` и `dotnet test` — без ошибок и без сломанных существующих тестов.
   Если задача требует новых тестов — они обязательны.
8. В ответе кратко: какие файлы изменены, какие тесты добавлены, результат build/test.
