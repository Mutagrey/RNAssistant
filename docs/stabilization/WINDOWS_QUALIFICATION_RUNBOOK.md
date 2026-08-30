# Windows qualification runbook

Этот runbook проверяет один собранный `16.1.0-dev` qualification candidate после
host-neutral миграции. Он не заменяет targeted tests подэтапов и не превращает
непроверенный build в beta/RC/stable.

## 1. Что подготовить

- Windows x64, Office x64 и VS 2022; записать версии Windows, Office, WebView2 Runtime
  и тип установки add-in.
- Известный Git commit, product version и один неизменяемый build на весь прогон.
- Отдельные тестовые `.xlsx`/`.xlsm`, включая две книги с одинаковым видимым именем в
  разных каталогах. Не использовать пользовательские документы без резервной копии.
- Включённые diagnostics и возможность экспортировать causal journal/trajectory.
- Для каждого сценария: ID, шаги, expected/actual, PASS/FAIL/BLOCKED, build/host,
  document/chat IDs, causal export и при необходимости screenshot.

`BLOCKED` не считается pass. После изменения production inputs создаётся новый build;
старое evidence применяется только к неизменившимся контурам.

## 2. WQ0 — blocking identity probe для 5B2

Выполняется при первом доступном коротком Windows окне, до production identity/factory
switch. Команды и формат наблюдений заданы в
[Excel identity probe README](../../tests/RNAssistant.ExcelIdentityProbe/README.md).

Проверить:

- одну книгу через desktop/VSTO/native call sites и разные COM proxies;
- две разные книги и две книги с одинаковым видимым именем;
- switch active workbook, close/reopen и Save As;
- retained marshal reference, release/cleanup и отсутствие ложного равенства.

Результат WQ0 — design evidence, а не общий pass Phase 5. После выбора identity нужен
отдельный 5B2 switch и его targeted проверки; затем сценарии WQ-SESSION ниже.

## 3. Финальный прогон candidate

| ID | Контур/owner | Обязательные сценарии |
|---|---|---|
| WQ-BASE | Controller / release | VS/Release build, load/unload add-in, product version + commit, DPAPI settings/API key, live provider request, WebView startup без network vendor fetch |
| WQ-SESSION | Phase 5B2 | bind одной книги; switch до write и во время confirmation; close bound workbook; Save As; same-name books; two chats same workbook; queued read/write; cancel до/после dispatch; отсутствие `ActiveWorkbook` mutation fallback |
| WQ-VBA | Phase 6 | list/read; unique exact patch; overlapping/duplicate refusal без write/journal; whole-module write; delete; restore exact backup, backup/current change after confirmation and type mismatch; rename before/intended/mixed states, source type race, destination collision после prepare и cancel до/после dispatch; package session install/run/cleanup, persistent install/remove and marker/hash/type drift; install с потерянным terminal/cleanup не исполняется и не принимается как persistent; CRLF/VBE normalization; Trust Access denied; COM/read-back/journal failures; restart после prepared без replay/write/remove/run |
| WQ-EXCEL | Phase 7 | все inspect selectors, bounded collections и large defined name без range materialization; Agent/manual/HTML read parity; values/formulas/profile, empty/oversized до materialization; verified scalar/formula/table write и no-op; protected sheet; switched/closed workbook; error до dispatch; possible dispatch без read-back → `unknown`, без auto retry |
| WQ-PACK | Phase 8 | revision-pinned resources; stale cursor; bounded large result + exact `ResourceRef`; полный Excel/VBA core в Agent; optional batch даёт одну новую revision без eviction; accepted/rejected typed event виден в защищённом `*.events.jsonl`, append failure не публикует pack и не отправляет следующий model request; rejected overflow показывает `TOOL_PACK_STATE` и не меняет прежний pack; accepted pack с тем же `TurnId` сохраняется через confirmation, compaction и restart при новом `RunId`, но не переходит в новый turn; handler/policy drift под тем же ID оставляет core + `TOOL_PACK_RESTORE_STATE`, fresh exact read создаёт accepted core rebase; controller доставляет terminal confirmation result вместе с restore warning |
| WQ-UI | Phase 9 / R28/R32 | restart/replay даёт тот же outcome; pending confirmation; causal navigation request→attempt→call→dispatch→effect; JSON/diff raw copy; incomplete/error states; stale projection; WebView keyboard/focus/DPI/clipboard; streaming + repair reset |
| WQ-CROSS | Phases 3–9 | one write ok + one error; unknown dominates health; terminal append failure после possible effect; concurrent chats; close/reopen во время run; model success text не перекрывает runtime error/unknown; no automatic retry |

Для VBA/Excel fault cases использовать предусмотренные test hooks там, где реальную
ошибку нельзя воспроизвести безопасно. Hook должен проходить через production
controller/domain/persistence/UI wiring; чистый fake-host harness уже относится к
host-neutral evidence, а не к этому runbook.

## 4. Как локализовать failure

| Последнее достоверное событие | Первичный владелец |
|---|---|
| target/session identity, STA, close/Save As | Phase 5B2 / HostRuntime |
| VBA prepare, journal, COM mutation, read-back | Phase 6 |
| Excel range materialization, write, verification | Phase 7 |
| resource revision, schema/policy/binding snapshot | Phase 8 |
| event append/replay, projection, WebView rendering | Phase 9 |
| accepted call/result correlation или lifecycle | Phases 2–4; регистрировать cross-cutting risk |

Текст модели не используется для определения owner или фактического effect. Если
causal journal не позволяет установить последнюю достоверную границу, это отдельный
дефект diagnostics Phase 9, а исходный failure остаётся открытым.

## 5. Закрытие

- Каждый обязательный ID имеет PASS evidence на одном candidate build.
- FAIL исправлен отдельным commit, покрыт targeted regression и повторно проверен в
  затронутом Windows scenario.
- После последних исправлений повторены WQ-BASE и общий smoke WQ-CROSS.
- Нет неразобранных P0/P1, false-positive success, wrong-target или unclassified
  `unknown` effect.
- Результаты и оставшиеся ограничения записаны в `PROGRESS.md`; только затем начинается
  Phase 12.
