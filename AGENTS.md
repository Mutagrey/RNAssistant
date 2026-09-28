# RNAssistant Agent Rules

Отвечай коротко и по делу. Экономь токены и контекст: сначала используй `rg`, читай
только нужные диапазоны и запускай минимальные релевантные проверки. Не запускай
VSTO/Office validation на этой машине.

RNAssistant — локальный Office/WebView2 assistant без server-side runtime. Чаты и
контекст принадлежат документам; Office tools выполняются локально.

## Текущий режим

- Точка входа в документацию — `docs/README.md`; постоянные правила —
  `docs/development-rules.md`.
- Перенос основных контуров на новую архитектуру завершён host-neutral. Проект в
  режиме сопровождения рабочего baseline: приоритет — воспроизводимые ошибки,
  ложный успех, потеря данных, зависания и наблюдаемость. Новые возможности допустимы
  с явным scope и owner; прежний общий feature freeze и маршрут Phase 11 → WQ →
  Phase 12 больше не управляют обычной разработкой.
- `main` — ветка обычной последовательной работы. Не создавай ветку на каждую задачу;
  отдельная ветка нужна лишь для параллельной работы, рискованного эксперимента или
  явно запрошенного PR. Существующие незавершённые изменения не перемещай и не
  коммить автоматически. Один commit — один инвариант или понятное изменение.
- Начало `docs/stabilization/PROGRESS.md` — текущий статус и открытые риски;
  `STABILIZATION_MASTER_PLAN.md` и phase reports — история завершённой миграции.
  Непроверенные Windows/Office/WebView2 сценарии остаются открытым evidence для
  соответствующего поведения и формального релиза, но не блокируют независимые
  исправления. Не выдавай host-neutral проверку за реальную Office qualification.
- После замены пути переключи consumers и удали старый path и мёртвые зависимости.
  Временный adapter фиксируй в `MIGRATION_MAP.md` с owner и условием удаления.
- Совместимость со старыми чатами/форматами не является целью: несовместимый stream
  явно reset/skip, без скрытого fallback, dual-write и удаления пользовательских
  данных. Pipelines остаются отключены.

## Перед изменением

По `docs/README.md` выбери canonical document области и прочитай его нужный раздел.
Начало `PROGRESS.md` читай для приоритета и открытых рисков; исторический master plan,
evidence и ADR — только когда нужны причина, точная проверка или прежнее решение.
Не загружай все docs/tests «на всякий случай».

Соседний дефект не смешивай с текущим исправлением: зафиксируй в `RISK_REGISTER.md`
или `BACKLOG.md`, а проблему потери данных/ложного успеха исправляй приоритетно.
Рефакторинг оправдан, только если он упрощает ближайшее конкретное изменение,
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
  считает lifecycle/outcomes. Model wire — conversation-response v5
  `message + final + tool_calls`; IDs, guards, URI/revision/cursor и authority принадлежат
  runtime, а не модели/UI.
- Model-facing reads используют только `common.resources_*` и semantic target;
  revision-pinned `rna://`/durable `ResourceRef` остаются runtime-only evidence. Chat events — append-only source of truth;
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
- Docs-only: diff и затронутые links/anchors, без build/harness. Изменённое
  COM/VSTO/controller delivery требует отдельной Windows x64 + Office x64 + VS 2022
  проверки до заявления о его квалификации.
- Перед commit выполни
  `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj -t:ValidateVersionFormat -nologo -v:minimal`.
  Обычный commit не меняет product version, не запускает release workflow и не
  создаёт tag.

## Definition of Done

Scope и owner однозначны; responsibilities не смешаны; новый contract typed и без
hidden fallback. Изменённое поведение имеет минимальную релевантную проверку или
явный открытый gap. Заменённый path удалён, canonical doc актуален;
`PROGRESS.md` обновляется при изменении общего статуса или приоритета.
Непроверенные Windows/release gates не объявляются закрытыми.
