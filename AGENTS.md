# RNAssistant Agent Rules

Отвечай коротко и по делу. Экономь токены и контекст: сначала используй `rg`, читай
только нужные диапазоны и запускай минимальные релевантные проверки. Не запускай
VSTO/Office validation на этой машине.

RNAssistant — локальный Office/WebView2 assistant без server-side runtime. Чаты и
контекст принадлежат документам; Office tools выполняются локально.

## Текущий scope

- Точка входа в документацию — `docs/README.md`; постоянные правила —
  `docs/development-rules.md`.
- Обязательный маршрут задаёт `docs/stabilization/STABILIZATION_MASTER_PLAN.md`,
  текущую задачу и gates — начало `docs/stabilization/PROGRESS.md`.
- Новые product features заморожены. Работай только в текущей фазе/подэтапе и не
  начинай следующую фазу тем же изменением. Не вводи целевой контракт или migration
  будущей фазы заранее.
- 11T0–11T10 и WQ-A1–A5 завершены host-neutral. R61/11O all-tool
  contract/Library UX cutover уже начат host-neutral; накопленный Windows
  rebuild/R62 retest и post-cutover qualification обязательны до Phase 12.
- При недоступной Windows выполняй только dependency-safe host-neutral slices.
  Windows gates накапливаются и остаются открыты. WQ0 проверяет принятое lifetime
  assumption exact bound `RuntimeKey`; без реального evidence candidate нельзя
  называть stable/beta/RC.
- Главная ветка — `stabilization/16.1`, рабочие — `stab/<phase>-<task>`. Не коммить
  стабилизацию в `main`. Один commit — один инвариант или чёткий этап.
- После slice обнови краткий текущий контекст `PROGRESS.md` и выполни локальную
  чистку: переключи consumers, удали заменённый path и мёртвые зависимости.
  Временный adapter фиксируется в `MIGRATION_MAP.md` с owner, consumers и removal
  gate.
- Совместимость со старыми чатами/форматами не является целью: несовместимый stream
  явно reset/skip, без скрытого fallback, dual-write и удаления пользовательских
  данных. Pipelines остаются отключены.

## Перед изменением

По `docs/README.md` выбери один canonical document области, прочитай только нужный
раздел master plan и начало `PROGRESS.md`. Evidence/ADR открывай только для причины
или точной проверки. Не загружай все docs/tests «на всякий случай».

Дефект вне текущего scope занеси в `RISK_REGISTER.md` или `BACKLOG.md`; не исправляй
попутно. Рефакторинг разрешён только если он упрощает ближайшее approved изменение,
удаляет названную зависимость/старый путь и имеет локальную проверку. Размер файла,
`partial` или общий призыв «почистить legacy» сами по себе не основание.

## Обязательные границы

- `RNAssistant.Core`: models, settings, storage, LLM/model protocol и pure parsing;
  без Office/VSTO/WinForms/WebView2.
- `RNAssistant.Office`: application orchestration, typed bridge, shared runtime,
  services и tools; без host-specific COM.
- `RNAssistant.OfficeHosts`/`RNAssistant.*AddIn`: bound host adapters, COM, ribbon и
  VSTO. Host-neutral behavior сюда не добавляется.
- `web`: static UI без npm/bundler; feature logic — в тематических `app-*.js`,
  `app.js` — boot/shared rendering.
- Все modes идут через `ConversationRunService` → `AgentKernel`; только kernel
  считает lifecycle/outcomes. Model wire — conversation-response v4
  `message + tool_calls`; IDs, guards, URI/revision/cursor и authority принадлежат
  runtime, а не модели/UI.
- Model-facing reads используют только `common.resources_*`, revision-pinned
  `rna://` и durable `ResourceRef`. Chat events — append-only source of truth;
  immutable bodies — CAS; projections не становятся вторым durable store.
- ToolRuntime исполняет exact descriptor/policy/binding. `ok` не доказывает effect;
  possible effect без read-back — `unknown` и автоматически не повторяется.
- Run закреплён за exact document session. Guard/preparation/dispatch/read-back
  сериализует HostRuntime/DocumentAccessGate; gate не держится во время model/user
  wait.
- Office document — authority live VBA; mutation пишет `prepared` до COM и terminal
  после read-back. Незавершённое не replay/restore автоматически. UserForms — только
  CodeOnly; Designer/FRX не входят в текущий protocol.
- Новые bridge contracts — typed DTO в `Contracts`, без anonymous response shapes,
  ad-hoc `JObject` parsing и string status inference.

## Код и проверки

- `AssistantController` — orchestration façade; reusable behavior принадлежит
  тематическому service/domain owner. Не вводи service locator, второй store/read
  model, generic Office abstraction или массовый namespace rename.
- Сохраняй C# 7.3/.NET Framework 4.8. Новый `.cs` обязательно добавляй в old-style
  `.csproj`. Не меняй generated `*.Designer.cs`/VSTO metadata без необходимости.
- Не храни secrets в репозитории; API key остаётся под DPAPI CurrentUser.
- Выбирай проверки по риску через `tests/RNAssistant.Harness/README.md`. Existing
  подходящее coverage не требует новых тестов; процент покрытия не является целью.
- Docs-only: diff и затронутые links/anchors, без build/harness. COM/VSTO/controller
  delivery требует отдельной Windows x64 + Office x64 + VS 2022 validation.
- Перед commit выполни
  `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal`.
  Обычный commit не меняет `16.1.0-dev`, не запускает release workflow и не создаёт
  tag.

## Definition of Done

Scope и owner однозначны; responsibilities не смешаны; новый contract typed и без
hidden fallback. Изменённое поведение имеет минимальную релевантную проверку или
явный открытый gap. Заменённый path удалён, canonical doc и краткий progress
актуальны, а непроверенные Windows/release gates не объявлены закрытыми.
