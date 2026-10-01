# Windows qualification runbook

Этот runbook проверяет один exact build для формального release qualification и
отдельных заявлений о Windows/Office/WebView2 доставке. Он сохраняет накопленные
сценарии миграции; рабочий baseline и обычные bugfix не требуют прохождения всей
матрицы перед каждым изменением. Непроверенный build нельзя называть
квалифицированным beta/RC/stable release.

## 1. Что подготовить

- Windows x64, Office x64 и VS 2022; записать версии Windows, Office, WebView2 Runtime
  и тип установки add-in.
- Известный Git commit, product version и один неизменяемый Release/x64 build на весь
  прогон; candidate metadata заранее pin-ит SHA-256 evidence signer certificate.
- Отдельные тестовые `.xlsx`/`.xlsm`, включая две книги с одинаковым видимым именем в
  разных каталогах. Не использовать пользовательские документы без резервной копии.
- Включённые diagnostics и возможность экспортировать causal journal/trajectory.
- Для каждого сценария: ID, шаги, expected/actual, PASS/FAIL/BLOCKED, build/host,
  document/chat IDs, causal export и при необходимости screenshot.

`BLOCKED` не считается pass. После изменения production inputs создаётся новый build;
старое evidence применяется только к неизменившимся контурам.

Проверка выполняется в обычном приложении на Windows/Office. Встроенного
Qualification Center, отдельных packs, probe/helper и автоматического pass нет.
Для формального релиза результаты привязываются к exact build и сохраняются по
[release process](../operations/RELEASE_PROCESS.md); обычная разработка не требует
этого прогона после каждого изменения.

## 2. WQ0 — exact document identity

По принятому 2026-08-31 риску WQ0 не блокирует production identity/factory switch:
11T0/7D фиксирует текущий `RuntimeKey` на lifetime exact bound workbook и удаляет
active-document fallback. Для формального релиза поведение identity проверяется
через реальные действия с книгами; отсутствие evidence остаётся открытым gap.

Проверить:

- одну книгу через desktop/VSTO/native call sites и разные COM proxies;
- две разные книги и две книги с одинаковым видимым именем;
- switch active workbook, close/reopen и Save As;
- отсутствие ложного равенства, чтения или записи в другой книге.

Результат WQ0 — diagnostic evidence, а не общий pass Phase 5. Failure исправляется
в bound-session identity/lifetime contract без восстановления `ActiveWorkbook` или
descriptor fallback; затем повторяются WQ-SESSION scenarios ниже.

## 3. Финальный прогон candidate

| ID | Контур/owner | Обязательные сценарии |
|---|---|---|
| WQ-BASE | Controller / release | VS/Release build, load/unload add-in, product version + commit, DPAPI settings/API key, live provider request, WebView startup без network vendor fetch |
| WQ-SESSION | HostRuntime / bound hosts | exact workbook/document/presentation/window/mail binding; active-target switch before write and during confirmation; close/reopen, Save As and same-name files; two chats for one document; queued read/write; cancel before/after dispatch; no active-document/window execution fallback |
| WQ-VBA | Phase 6 | list/read; unique exact patch; overlapping/duplicate refusal без write/journal; whole-module write; delete; restore exact backup, backup/current change after confirmation and type mismatch; rename before/intended/mixed states, source type race, destination collision после prepare и cancel до/после dispatch; package session install/run/cleanup, persistent install/remove and marker/hash/type drift; install с потерянным terminal/cleanup не исполняется и не принимается как persistent; CRLF/VBE normalization; Trust Access denied; COM/read-back/journal failures; restart после prepared без replay/write/remove/run |
| WQ-EXCEL | Phase 7 / 11T | все inspect selectors, bounded collections и large defined name без range materialization; Agent/manual/HTML read parity; values/formulas/profile, empty/oversized до materialization; verified scalar/formula/table write и no-op; literal Find/FindNext over sparse scopes beyond the old snapshot bound; regex/long-query bounded snapshot recovery; find/replace literal/regex over values/formulas and every scope; add/rename sheets with active fallback, invalid/colliding/case-only names and protected workbook structure; clear values/formats/all, ascending/descending sort с/без headers под локалью Office, AutoFilter empty/text/operator/wildcard criteria, mixed/direct/conditional formatting and number formats/colors/alignment, row/column/both autofit; contiguous/oversized/protected ranges; selection/collection/pre-state drift; switched/closed workbook; error до dispatch; possible/partial dispatch, add rollback or divergent read-back → `unknown`, без auto retry |
| WQ-WORD | Word domain / bound backend | saved/unsaved and Save As identity; multiple windows, selection/range/story reads; literal/regex replacement and length changes; formatting, tables, comments and page breaks; protected/read-only files, target drift, close during access, COM/read-back/partial-effect faults and pane cleanup |
| WQ-POWERPOINT | PowerPoint domain / bound backend | saved/unsaved and Save As identity; multiple presentations/windows, selection, slide/shape/notes reads; replacement, add/set/duplicate/move and table/picture limits; protected files, target drift, close during access, COM/read-back/partial-effect faults and pane cleanup |
| WQ-OUTLOOK | Outlook domain / bound backend | Explorer and Inspector pane activation; saved/unsaved mail, multiple stores/windows, folder and selection changes, exact EntryID resolution; bounded search/collect and attachments; draft/reply/forward and mail update; close/target drift, COM/read-back/partial-effect faults and pane cleanup |
| WQ-PACK | Phase 8 | native semantic find/read parity in Agent/Chat/manual UI; exact read-only policy and document-gate serialization for live Office/VBA; revision-pinned resources remain runtime-only; bounded whole-resource result; media bytes hydrate only the immediate next model step and release after success/repair/failure; final Excel Agent core is exactly four bootstrap + 15 Excel + VBA write/patch, Word/PowerPoint add only VBA write/patch, Plan has four bootstrap and Chat has two resource schemas; rename/restore/delete/macro are initially non-callable but exact-discoverable, then one accepted schema read makes each callable only on the next model step without changing confirmation/effect/binding; optional batch даёт одну новую revision без eviction; accepted/rejected typed event виден в защищённом `*.events.jsonl`, append failure не публикует pack и не отправляет следующий model request; rejected overflow показывает `TOOL_PACK_STATE` и не меняет прежний pack; accepted pack с тем же `TurnId` сохраняется через confirmation, compaction и restart при новом `RunId`, но не переходит в новый turn; handler/policy drift под тем же ID оставляет core + `TOOL_PACK_RESTORE_STATE`, fresh exact read создаёт accepted core rebase; controller доставляет terminal confirmation result вместе с restore warning |
| WQ-UI | Phase 9 / R28/R32/R46/R47 | restart/replay даёт тот же outcome; pending confirmation; causal navigation request→attempt→call→dispatch→effect; event port сохраняет exact type/correlation/CAS payload, chat-bound hydration отклоняет чужой event; conversation port сохраняет create/load/save/list/active/move/delete/recovery semantics через actual controller и один backend; mandatory request/rejected/ToolPack append failure останавливает свой boundary, optional causal/accepted-marker failure outcome не меняет; JSON/diff raw copy; incomplete/error states; stale/multi-window projection; WebView keyboard/focus/DPI/clipboard; chat switch during catalog sync and long bridge work, shutdown drain without disposed dispatcher; streaming + repair reset |
| WQ-CROSS | Phases 3–9 | one write ok + one error; unknown dominates health; terminal append failure после possible effect; concurrent chats; close/reopen во время run; model success text не перекрывает runtime error/unknown; no automatic retry |

Для VBA/Office fault cases использовать предусмотренные test hooks там, где реальную
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
- Detached release manifest имеет status `complete`; release owner сверил hashes
  неизменённых distributable files и результаты Windows сценариев.
- Нет неразобранных release-blocking P0/P1 в выбранном scope, false-positive
  success, wrong-target или unclassified `unknown` effect; отложенные контуры
  и неподтверждённые платформы не объявляются квалифицированными.
- Результаты и оставшиеся ограничения записаны в `PROGRESS.md`.
