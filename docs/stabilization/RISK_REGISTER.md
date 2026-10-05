# Stabilization risk register

Current triage is in [PROGRESS](PROGRESS.md#current-operating-status--2026-09-30).
The dated incident notes distinguish observed symptoms, reproduced host-neutral
causes and remaining Windows/target-model evidence. The R-number table retains
the severity assigned when each risk was opened; a row marked fixed host-neutral,
retired or deferred is not an active P0/P1 implementation defect merely because
that original severity remains in the table. Release qualification is a separate
exact-build gate.

## Workspace CLI false completion with local Qwen — 2026-10-04

Owner: shared agent completion contract and CLI acceptance evidence (M4–M5).
A real `rna-qwen35-9b-32k` creation run returned `model_done` and described
`index.html`, `styles.css` and `app.js`, but had zero tool calls and left the
workspace empty. In a controlled repair run, the model read the broken files
then returned `model_done` with zero writes while the defect remained. Its final
message ended mid-sentence. The kernel's `ToolCounts` and the file authority
correctly showed no effect; no file artifact was committed from the model's words.

Explicit feedback produced three verified creates and three reads, and a later
narrow repair produced a verified patch plus read-back. External Chromium checks
passed on the repaired app. The CLI now offers opt-in `--expect-files`,
`--min-reads` and `--min-writes` postconditions. The contract is persisted before
dispatch; assessment counts current complete file-read evidence and verified
changed file effects, not generic tool success. Scripted CLI failure and pass
runs confirmed the exit code and persisted inspection state; earlier live Qwen
runs exercised the shallower postcheck. A scripted confirmed delete after an
external edit preserved the file and failed the minimum-change contract despite
the model's `done`. Tasks without those conditions still
expose unverified `model_done`, and the conditions do not grade file content or
browser behavior. Autonomous completion reliability remains open.

A fresh local-Qwen run after the persisted contract change failed the complete
three-file task twice: first by repeatedly reading missing files, then by batching
mutations despite ten protocol repairs. Narrow single-file turns did create all
three files, but source inspection found a missing form/list and incorrect JS
delete/localStorage behavior; attempted agent repair made no write. The shallow
file/count contract passed those narrow turns but did not certify the app. This
keeps M5 browser verification and agent repair acceptance open.

The development CLI now has isolated `web.verify` and an opt-in
`--require-web-verify` acceptance condition. A successful browser smoke retains
exact file evidence; a later file mutation or observed external edit makes that
verification stale at completion. Scripted verify/repair and stale-check cases
passed, and local Qwen completed one real read/patch/read/verify repair after the
user supplied the precise DOM-ID diagnosis. The initial Qwen request still asked
for input despite accessible files, and its next turn stalled after reads and
failed verification. This does not close autonomous diagnosis, functional browser
assertions or the original multi-file task risk.
An advisory DOM-ID hint was added to a failed browser result and exposed in the
top-level tool message. Two fresh Qwen attempts still repeated either the failed
verification or a patch without accepted source evidence; the no-progress guard
stopped both without writes. The hint improves diagnosis for a human or another
model, but it is not evidence that this local model repairs autonomously.

The independent CSV dashboard grader now checks functional browser behavior
outside the writable workspace. A fresh local-Qwen task again returned `done`
without tool calls; CLI acceptance rejected it. One feedback turn produced three
files and a passing browser smoke, but the grader found broken export and invalid/
empty CSV handling (8/11 assertions passed). A second feedback turn made no
change and falsely claimed all three issues fixed. This reproduces the risk with
an independent functional oracle; model completion still cannot certify the app.

A same-task comparison on the current CLI source found Gemma 4 12B produced three
files and 10/11 browser behaviors, while Qwen 3.5 9B again returned `done` with
no tool calls. Both failed the explicit read-back/verification contract. A Gemma
feature follow-up exposed a separate CLI projection defect: the persisted failed
`web.verify` result was correct, but its current-turn model-request body was null.
The CLI projection now materializes the exact execution result, with a scripted
read/error regression. This removes one cause of blind completion; a fresh Gemma
run still skipped read-back and failed independent invalid-CSV behavior. Agent
completion and two-phase functional acceptance remain open.

## Cloud Gemma v6 protocol mismatch — 2026-10-04

Owner: Ollama OpenAI-compatible endpoint/model profile and ModelProtocol
qualification. `gemma4:cloud` through Ollama 0.35.0 returned Markdown-fenced,
wrong-shaped JSON to a strict `json_schema` request and to the full CLI task in
explicit `json_object` mode. Both fresh CLI runs exhausted ten format attempts
before any tool call or file effect. A simple `json_object` greeting did return
valid v6, so the failure is prompt/task dependent. CLI response mode is now an
explicit saved setting for controlled comparison; it does not relax v6 parsing.
Do not use this tag as a strong reference until the exact full-task protocol
probe succeeds. The local Gemma 12B evidence remains separate.

## Offline managed dependency closure — 2026-10-02

Owner: local packages and host delivery targets. The Windows photos show
`Microsoft.Data.Sqlite` and `SQLitePCLRaw.core` missing at runtime. Host-neutral
inspection found mismatched SQLite strong-name versions and a NativeHost portable
manifest without the four SQLite DLLs. The pinned packages and manifest are now
aligned. A full local assembly-reference audit found no missing non-framework DLL
in the Office or JS worker outputs, but found older worker `System.Memory`,
`System.Buffers` and `Unsafe` versions than the host copied beside it. Worker
references and generated redirects now match Core. Desktop/VSTO targets explicitly
copy the full managed and architecture-matched native closure. Local copy targets
and package integrity passed; exact Windows Desktop, VSTO and NativeHost loading
remain open evidence. The portable `lib/` relocation and x64/x86 VBA DLL search
also require an exact Windows/Office load check with rebuilt macro add-ins;
host-neutral publisher checks establish file placement only.

## Local-data reset and active WebView profile — 2026-10-02

Owner: Core storage paths and Office controller lifecycle. The Windows log shows
`ClearRuntimeData` deleting durable roots before failing to remove the open
`webview/EXCEL` profile; the surviving controller then reported a truncated
resource journal from its stale in-memory projection. The reset now validates
durable roots first, leaves the active WebView profile alone, and invalidates chat,
document-authority and resource caches after a successful explicit clear. Focused
host-neutral checks cover a late invalid root and fresh resource reads after reset.
Exact Windows/WebView2 behavior remains unverified. A filesystem failure during
deletion of a durable root can still leave a partial reset and must be reported as
an error; no automatic replay or silent recovery is permitted.

## HTML patch `replace` mismatch — 2026-10-02

Owner: Core text patch engine and HTML workspace tools. The photo shows a
two-hunk patch for `app.js`; the second `find` is CSS-shaped and returns
`text_patch_not_found`. No write occurred. The user's exact workspace source is
unavailable, so the photo alone does not establish whether that anchor belongs
to another file or differs from current source.

Host-neutral code review found a separate reproducible failure: when `find`
contains normalized newlines and source mixes LF/CRLF, fallback matching could
miss a present span. It now maps newline-equivalent matches back to original
source offsets. A missing exact anchor found in another workspace file reports
that path, while retaining the atomic no-write failure. The focused HTML patch
harness passes; the photographed Windows/model trajectory remains unverified.

## HTML editing memory growth — 2026-10-02

Owner: HTML navigation/source projection and WebView editor.
The user reports progressive slowdown after many HTML revisions in a long chat.
Code inspection and focused checks reproduce eager ancestor-body hydration and
full source replacements accumulating in CodeMirror undo history, including its
hidden editor. Empty source-cache entries also had no count limit.

Navigation now carries at most 20 metadata summaries; restart hydrates only the
active aggregate and restore reads its exact source on demand. CodeMirror clears
history on programmatic source/document changes, retains local typing undo, and
releases its hidden buffer in Preview. Source caching is capped at 200 files and
3 million characters. Preview comparisons avoid another serialized source copy;
an already cleared iframe is not navigated again on every edit render.

Targeted storage/shared HTML checks and a real Chromium/CodeMirror stress case
cover 160 large source replacements, local undo, source cleanup and narrow-panel
layout. This is not a memory profile or qualification of the reported Windows
session. Exact Windows/WebView2 long-chat evidence remains open. Full library
metadata enumeration and authority-journal startup still scale with history;
metadata pagination remains a separate Resource/Library owner task.

## Chat navigation and startup freezes — 2026-10-02

Owner: WebView navigation/bridge, controller startup and ChatStore headers.
The user reports the whole UI freezing during chat creation/selection and Office
application changes. The supplied October 1 log photo shows roughly 300–500 ms
warm header/catalog scans with zero full/incremental header replays, about 4,600
CAS references across 18 chats, and roughly two seconds for init versus 64 ms for
its WebView render. Model `headersWait` also reaches tens of seconds; those network
waits do not establish a blocked UI thread or justify changing model/context limits.

Code inspection confirmed synchronous chat creation/document navigation in bridge
UI callbacks, a full interrupted-run scan in the controller constructor, and a
filesystem metadata probe for every CAS reference on every catalog read. Rapid
selections could race exclusive host rebinding: the newer request failed busy
while the older successful response was discarded. Focus events were truthy
`force` arguments and bypassed the active-send polling guard.

The implementation now serializes navigation and init, drains catalog sync,
coalesces not-yet-dispatched selections and applies each committed binding before
the next request. Composer submission is paused during the transition. Navigation
and Office coordinator dispatch leave the UI callback; startup recovery is lazy,
single-execution and precedes chat reads/writes. Catalog size reporting shares a
fresh prefix-directory metadata snapshot per listing, rechecks misses for concurrent
publication and never reuses that snapshot as read/mutation/GC authority. Slow
navigation now reaches the runtime log as `kind=chatNavigation`; recovery has its
own timing. No event/CAS data or request limits were removed or relaxed.

The October 2 log also shows `EnsureHostSwitchReady` rejecting every Office panel
chat selection while a run is active. The coordinator now permits selection and
creation within the currently bound document while preserving the run's original
chat; it still requires the run to finish before rebinding to another document.
This host path needs an exact Windows/Office check.

Focused Node checks cover switching/creation/init races, failed-next-switch recovery,
focus polling, history paging and run revision ordering. Host-neutral harness checks
cover caller-context isolation, coordinator rebinding, cancellation responsiveness,
typed document dispatch, header cache integrity, CAS deletion/restoration and late
publication, plus interrupted-run recovery. Controller construction/lazy wiring is
source-reviewed: the bridge harness uses a controller stub, so it does not qualify
the production controller or Office adapter.

Remaining evidence: repeat cold start, rapid chat creation/selection and Excel ↔
Word/Outlook activation on the exact Windows/Office/WebView2 build with the same
history. Record recovery, header/catalog, bridge/render and COM/modal delays. Cold
full replay and persistence lock waits remain possible. Desktop `MainForm` also
marshals callbacks back to its UI thread and `AttachTarget` selects the requested
chat synchronously before replacing the pane; a separate Desktop attach slice is
still needed. No measured Windows speedup or complete freeze resolution is claimed.

Panel/model follow-up: model/mode/reasoning persistence and projection also ran in
the bridge UI callback; the closed model picker rebuilt every menu item on composer
updates, and foreground activity events rebuilt the transcript individually.
These paths now use worker dispatch, lazy/cached picker DOM and one render per
animation frame. The duplicate hidden model select and its canvas width measurement
are removed. Hidden settings model tables are not rendered from the chat tab.

Model discovery could hold exclusive host rebinding until its 30-second HTTP timeout,
close its own picker while loading, and use an unsaved settings-form API key from the
chat picker. Discovery is now single-flight and cancellable through the HTTP token;
navigation drains both discovery and its cancellation acknowledgement, plus accepted
chat preference writes. Late results cannot replace a new binding's catalog; init and
settings publication invalidate it. Different-URL/explicit-key catalog previews no
longer persist capabilities to the current server's settings. Leaving settings also
discards their preview catalog; the chat picker can reload after cancellation without
reopening. Regression review covers a failed cancellation acknowledgement as well:
the original request must still drain before navigation. This does not identify
the user's unspecified model error as a context overflow or justify reducing limits.

Node regression checks cover a 500-model catalog without hidden DOM work, cancellation
races and stale binding replies, preference/navigation ordering, and 100 foreground
activities coalesced without lost events. Four targeted host-neutral checks pass:
catalog cancellation releases binding, navigation/model controls leave the caller
context, bound session identity, and per-chat model isolation. These were run against
an isolated HEAD archive plus this slice because unrelated in-flight kernel changes
prevented the shared harness from compiling. Production controller/HTTP wiring is
source-reviewed; the harness controller remains a stub. Desktop also synchronously
discovers Outlook mailboxes on its minute timer, before its foreground-focus guard;
that COM wait and attach preparation still require a separate Desktop owner change
and Windows timing evidence.

Large-chat follow-up (2026-10-02): a 30–50 MiB JSONL file does not cross the
bridge whole; the initial message page is 80 items. A rebuildable per-chat SQLite
projection now holds header state and normalized message/artifact rows. The first
index build still validates the entire event stream on a bridge worker, with a
chat-list loading indicator; subsequent reads check an exact tail cursor, read
only validated suffixes and query history pages by ordinal. JSONL remains the
authority and the explicit CAS health audit detects/repairs a divergent index.
Scalar preference writes avoid a full message diff; generic aggregate writes
still compare all messages. The earlier 16-million-character in-memory cache,
document-scoped write locks, bound selection shell, deferred chat detail and
bounded UI projection remain in place. Temporary synthetic 30/50 MiB fixtures
with 6,000 messages and nine HTML revisions show index build 1.23/1.27 s,
indexed cold load 0.34/0.26 s, warm load 16/24 ms and generic save 0.23/0.29 s
on this host. These are host-neutral synthetic measurements; the reported chat
and Windows/WebView2 delivery have not been measured. First conversion, packaging
of `winsqlite3.dll` from Windows, and real cross-tab responsiveness remain open
evidence. See [the storage contract](../session-events.md) and
[ADR-0012](../decisions/ADR-0012-chat-sqlite-projection.md).

## Agent continuity and deterministic read loops — 2026-10-01

P1 incident evidence remains open; shared host-neutral fixes are implemented.
Owner: Conversation/model context, Core kernel and Resource Gateway.
The additional two Windows photos show the same `resources_read` path rejection
repeated for minutes, followed by an explanation naming a different error. Five
host-neutral probes established pre-fix defects independent of model quality:
the compiler replaces an original failed read with `resource_evidence_unavailable`
in all three result roles; unrelated tool admission drops a resource claim;
folded mutation provenance becomes assistant interpretation at compaction;
compaction notices disappear; six identical failed calls dispatch until the test's
iteration limit. The shared compiler/progress/working-set corrections now cover
these mechanisms, with exact terminal facts retained independently of source bodies.

Follow-up (2026-10-02): the first progress guard ended the whole run on a duplicate
failure or a second source conflict, preventing the model from acting on recovery.
Rejections now return explicit non-dispatch feedback and allow another tool or
corrected input; only three consecutive responses consisting entirely of rejected
calls stop the loop. A compact archived mutation receipt also lost its semantic
target when dropping data. Targets now survive separately from historical source
bodies, with unchanged success/no-op/error/unknown effect distinctions. These
paths are covered host-neutral; they do not close the original live incident.

Cross-turn follow-up (2026-10-02): a new user message previously reset optional
callable schemas to core even when the same chat retained exact accepted admissions.
The next turn now reuses unchanged, retained admissions within its request budget;
edited/cleared history, descriptor drift and budget overflow return to core.
Complete current skill/resource bodies already follow history/CAS and working-set
selection, so their actual inclusion still needs the exact next-request trace.
Focused host-neutral tool-pack checks cover carryover, drift, history edit and
budget fallback. Live Windows/model evidence remains open.

The [canonical analysis and ordered implementation plan](../conversation-protocol.md#agent-continuity-audit--2026-10-01)
separates operation facts from observation freshness, keeps typed provenance through
compaction and uses existing typed recovery for bounded progress. The
[tool audit](../tool-library.md#tool-ergonomics-audit--2026-10-01) covers current
schemas, contradictory guidance, result contracts and merge/split decisions.
Do not add another per-tool reread prompt as the primary remedy. Original accepted
calls/next-request bytes and exact Windows/Office/WebView2/model evidence remain
needed to attribute and close each photographed incident. Host-neutral passes do
not establish live provider or target-model behavior.

## HTML read presentation and premature completion — 2026-10-02

P1 — task continuity/completion; P2 — read presentation. Owners: Conversation/model
context, Task List/completion policy, transcript/UI presentation. Ниже сохранён
исходный аудит до исправления; реализованный follow-up и открытые границы — в конце
раздела. Исходный Windows-инцидент не закрыт.
На фото счётчики HTML/CSS показывают **символы**, не строки. В видимой части run
есть изменение `styles.css`, после которого модель утверждает также исправление
`app.js` и работу взаимодействий. Exact build, полный event stream и отправленные
model requests отсутствуют; нельзя доказать причину этого конкретного финала или
что отсутствующий в показанном участке вызов никогда не выполнялся.

Подтверждённые до исправления механизмы и границы:

1. **Подпись зависит от хранения тела.**
   [RuntimePayloadService.ExternalizeActivity](../../src/RNAssistant.Core/Storage/RuntimePayloadService.cs)
   при `DataJson.Length > 8192` сохраняет результат в CAS и обнуляет `DataJson`.
   Это происходит в `ChatStore.SaveInternal` до отчёта UI.
   [ChatCloneService](../../src/RNAssistant.Office/Services/ChatCloneService.cs)
   не передаёт runtime payload reference в bridge; отдельной краткой сводки нет.
   [activityResultCaption](../../web/js/app-agent-activity.js) читает только
   `DataJson`, дополнительно отказывается разбирать JSON длиннее 32768 символов.
   В обоих случаях успешный read получает общую подпись «Прочитано».
   [ToolResultPresentationService.Read](../../src/RNAssistant.Office/Services/ToolResultPresentationService.cs)
   также использует только `activity.DataJson`: раскрытие архивированного read
   не восстанавливает тело. Наличие файла/байтов в CAS не означает наличие preview.
   Это presentation defect; model result хранится отдельным protocol message,
   который `ModelContextCompiler` гидратирует независимо. Подпись не доказывает
   ни потерю, ни доставку тела модели.
2. **Завершение ответа не проверяет исполнение всей задачи.**
   [EvaluateFinalResponse](../../src/RNAssistant.Office/Services/ConversationKernelAdapter.Store.cs)
   возвращает `Complete`, если активного Task List нет. Список необязателен.
   При его наличии проверяется факт закрытия, а
   [TaskListService.Close](../../src/RNAssistant.Office/Services/TaskListService.cs)
   принимает model-authored `status=completed` без связи шага с mutation/read-back
   evidence. Поэтому один успешный CSS patch и финал с утверждением о двух файлах
   допустимы для runtime. Это gap семантической проверки, а не ошибка wire parser:
   `Completed` корректно означает конец run, не доказанную полноту deliverables.
3. **Точный остаток работы не закреплён после compaction.**
   `ConversationPromptComposer` включает для Task List только discovery entry
   через `ChatResourcePromptIndex`, с общим бюджетом индекса 192–600 tokens.
   Цель/шаги/статусы отдельной обязательной проекцией не передаются.
   [ContextWorkingSet](../../src/RNAssistant.Office/Services/ContextWorkingSet.cs)
   восстанавливает source/resource/capability bodies, но не гарантирует восстановление
   последнего `task_list_set` с полным списком. После ухода результата в compacted
   prefix остаются summary claims и возможность повторного чтения ресурса.
   Это риск пропуска этапов, не доказательство потери списка в исходном run.
4. **Остались противоречия инструкций.** В
   [AppSettings](../../src/RNAssistant.Core/Models/AppSettings.cs) основной tool
   prompt сохраняет admission через compaction, а `ContextCompactionPrompt`
   требует повторный `capabilities_read` и новое admission. Runtime notice самого
   compactor тоже говорит, что повторное admission не требуется. Основной workflow
   разрешает при блокере оставить список открытым и сообщить о блокере через final,
   но completion gate и `RUNTIME_CONTINUE` запрещают любой final с открытым списком;
   два последовательных final дают `task_list_open`. У Task List есть blocked step,
   но закрыть его можно только как completed/cancelled/superseded. Дополнительно
   `RUNTIME_CONTEXT.document.artifacts` советует Plan для complex task, тогда как
   основной prompt ограничивает отдельный Plan запросом пользователя/нуждой в design
   artifact. Исправление только одной строки системного промпта эти слои не согласует.
5. **Большое source read может оборвать продолжение.**
   `ResourceReadToolHandler` читает source целиком; `offset/limit` допустимы только
   для table/records, `section` — для Markdown. Gateway whole-read bound —
   2,000,000 символов, а доставка ограничена меньшим фактическим request budget.
   `ModelContextCompiler` может выбросить `PromptBudgetExceededException` с
   `CanCompact=false` для одного большого complete source. Это честный отказ,
   но модель не получает следующего шага для выбора меньшего фрагмента.
   Поиск даёт snippet targets, однако штатного запроса диапазона строк HTML/JS нет.
   Связанный large-resource gap уже есть в [BACKLOG](BACKLOG.md#large-resource-working-set-and-compacted-action-memory--2026-09-29).
6. **Read-back файла не доказывает исправление взаимодействия.** HTML tools
   выполняют static preflight (`scope=static_preflight`), не браузерный сценарий.
   Встроенный HTML skill это правильно оговаривает. «Запись подтверждена»,
   «статическая проверка прошла» и «Y-zoom/drag проверен» должны оставаться разными
   claims. Фото не подтверждает последний. Живую проверку нельзя заменить
   успешным scripted-model harness.

Исходный порядок предложенных изменений:

- **Presentation owner:** перед externalization сохранять typed bounded read summary
  (representation, returned count/unit, coverage/complete), независимо от тела;
  ленивый preview читать из CAS через существующего владельца storage с явным
  unavailable. Удалить зависимость подписи от полного JSON и согласовать producer,
  clone/bridge и UI. Не увеличивать inline limit ради счётчика.
- **Prompt/context owners:** согласовать основной/tool/helper/runtime prompts;
  сохранить authored prompts и существующий explicit review. Исправить устаревшие
  указания в документации: wire-v5 doc ещё называет prompt schema 32 при текущей 33,
  а backlog «Resource read prompt wording» описывает уже исправленные defaults.
- **Task List + Conversation owner:** из существующего состояния включать в каждый
  запрос компактную точную проекцию цели, оставшихся шагов, blockers и имеющихся
  receipts с резервом бюджета перед большими телами. Не создавать второй durable
  task store; для коротких ответов не навязывать отдельный Plan/ритуальные стадии.
  Task List сейчас требует минимум три шага — пересмотреть это для простых
  задач с двумя независимыми deliverables.
- **Completion policy owner:** связать проверяемые deliverables с semantic targets
  и требуемым видом evidence; runtime связывает их с фактическими terminal events.
  Финал при незакрытых проверяемых обязательствах возвращает модели конкретный
  остаток для продолжения. Отдельно разрешить честный blocked/input-needed исход,
  сохранив незавершённые шаги. Не определять выполнение по словам «исправил» или
  числу успешных calls. Семантическое качество полностью детерминированным gate
  не доказывается и требует model evaluation.
- **Resource/context owners:** добавить ограниченный source excerpt с понятным
  selector/coverage и восстановимым ответом о недоставленном большом body. Runtime
  сохраняет revision/cursor authority; excerpt не разрешает whole-file overwrite.
  Сохранить текущие unknown-effect non-replay и bounded no-progress recovery.

Минимальные проверки реализации: read выше 8192/32768 после save/reload и отсутствующий
CAS; два обещанных файла с записью только первого; compaction между двумя правками
с точным остатком/исходником второго; blocked final с открытым списком; oversized
source с успешным переходом к excerpt. Для целевой модели повторить эти сценарии
и оценивать фактические mutations/проверки/повторы/пропуски, а не текст финала.

Evidence исходного аудита: вызовы shipped UI functions из Node дали счётчики для
3563/6965 символов, общую подпись после simulated activity externalization для
10000 и общую подпись уже inline для 40000; `node tests/web/completion-guard.test.js`
прошёл 19/19. C# storage/preview/completion/context paths проверены чтением кода,
новый end-to-end C# repro не запускался. Build, Office/VSTO, WebView2 и live-model
проверки не выполнялись. Production code этим аудитом не изменён.

Реализованный follow-up (2026-10-02):

- Transcript/UI: typed `ReadSummary` переживает externalization и replay; caption
  не разбирает большой JSON. Preview читает точный CAS payload и явно показывает
  отсутствие тела. Прежние activities без summary остаются с общей подписью.
- Conversation/Task List: точные goal/steps/statuses/notes/reason/blocker закреплены
  в обязательном `RUNTIME_CONTEXT.active_task_list`. Допустимы 1–32 этапа; список
  необязателен. Сохраняется история уточнения цели, замены и перестановки шагов.
- Completion, пересмотр после review: удалены первоначальные `read`/`changed`
  bindings и проверка изменения baseline hash. Они мешали обоснованному no-op,
  пересмотру гипотезы и работе без полного source в контексте. Удалён и прежний
  completion gate с принудительным `RUNTIME_CONTINUE` из-за открытого списка.
  Статусы плана — `agent_assessment`; агент решает о завершении из контекста и
  фактов, может пояснить отсутствие необходимой правки. `close` не требует
  механически завершить все шаги и не меняет неуказанные статусы. `blocked` с reason
  сохраняет список без привязки к одному run. Final не закрывает список и не теряет
  незавершённые шаги; следующий запрос снова получает точное состояние.
  Tool receipts, guards записи и unknown-effect non-replay не заменяются оценкой
  агента. Снятие gate не доказывает правдивость финала модели: это остаётся предметом
  target-model evaluation.
- Resource/compiler: source/text `startLine` + `lineCount` возвращают точный excerpt
  с частичным покрытием. Не помещающееся полное тело заменяется явным notice и
  operation receipt с прежним outcome/effect; это не доказательство чтения тела и
  не разрешение whole-file overwrite. Сохраняются bounds и unknown non-replay.
- Defaults schema 36 согласует основной/tool/Task Tracking prompts с новым
  контрактом. Schema-35 migration выбирает текущие defaults и сохраняет прежние
  тексты перед записью settings. Canonical docs и reviewed inventory обновлены
  вместе со схемой tool.

Дополнение (2026-10-02): response v6 и prompt schema 38 разделили вызов
инструмента, продолжение без вызова, завершение, блокировку и ожидание ответа.
Шаг `continue` теперь сохраняется и виден следующему запросу; повторение без
действий ограничено. Итоговая карточка показывает эффект и известный остаток
Task List отдельно от решения модели. Это устраняет неоднозначность wire-формата,
но не доказывает, что target model выберет `done` только после фактического
выполнения всех требований. Исходный Windows/HTML-инцидент остаётся открытым.

Focused evidence после пересмотра: `agent continuity:` 5/5, `task lists:` 4/4;
пересмотр плана и rationale после save/reload/compaction, open/blocked final в двух
запусках, CSS-only правка без лишней записи JS, read summary/CAS и excerpts.
Defaults/guidance, schema migration, R61 inventory и unknown-effect non-replay
также прошли. UI: task continuity 1/1, completion 20/20, artifact JSON 8/8,
Plan 8/8, artifact text 10/10, run state 6/6. Это scripted-model и host-neutral
проверки, не оценка качества решений реальной модели.
Office/VSTO validation и исходный Windows/WebView2 incident остаются открытыми.

## Repeated resource reads and HTML binding failures — 2026-10-01

Six Windows photos show repeated capability loads, reads/rewrites, missing HTML or
Excel targets, and `records` requests failing with an unavailable `text` view.
Owners: model context compiler, semantic Resource Gateway and HTML workspace.
Code/host-neutral reproductions establish separate causes:

- Evidence deduplication changed an earlier successful read to
  `resource_evidence_stale` merely because a later call used the same snapshot.
  It could also erase different JS outputs. Different results now retain status/data;
  equal complete read results retain one body plus successful linked causal frames,
  avoiding repeated skill bodies consuming the request budget. Completed mutation frames
  retain their runtime `tool_call_id` correlation.
- The prompt index could advertise superseded document HTML snapshots that current
  discovery no longer resolved. Those known old roots are omitted, with history
  retained. HTML scope now includes the document-owned workspace root.
- Static JSON writes already created a text binding, but results did not supply a
  readable source target. Binding/manifest reads exposed runtime metadata instead
  of usable source/member targets. Explicit semantic projections now separate the
  source values from workspace/binding metadata; invalid self-binding is rejected
  before mutation, and an identical rebind is a verified no-op.
- The HTML skill hard-coded `stream({view:'table'})`, conflicting with records/text
  bindings. Guidance now consumes the bound view and describes actual batch fields.
  JS worker reads are separate from page execution and cannot verify its render.

The exact original model requests and accepted arguments are absent from the
photos. Repeated loading after a new user turn or skill-body compaction can be
intentional; same-turn repetition after complete current evidence still needs the
affected trajectory to distinguish model behavior from another delivery defect.
Windows Office/WebView2 and the target model remain open evidence.

## Office panel switch and shutdown race — 2026-09-30

Windows photos show repeated bridge switch-busy errors during chat navigation and
`ObjectDisposedException` from `OfficeStaDispatcher` after an in-process panel
session was disposed. The exact pending request at each switch is not recorded, so
the photos alone do not prove every refusal was false. Code inspection confirms two
risks: background catalog sync could overlap an exclusive chat selection, and panel
shutdown disposed its controller/adapter before accepted bridge work had finished.
Owner: WebView bridge/pane and `AssistantRuntime`; in-process and Desktop binding teardown.

The UI now drains an existing catalog sync before selection and pauses new polls;
timing telemetry does not reserve the binding. Bridge and resource handlers expose
a shutdown drain; runtime and in-process Office binding cleanup wait for that drain
without blocking the Office UI thread. VSTO add-in shutdown also defers its shared
dispatcher cleanup until pane requests leave. Busy switching returns a typed error without
a stack trace. Focused WebView sync and host-neutral bridge checks pass; live
Windows Office/WebView2 and long-running cancellation still need exact-build verification.

Desktop review found the same early-release path in `MainForm`: it disposed its
original Office adapter immediately after `AssistantRuntime.Dispose`, before the
runtime's accepted bridge/resource work had drained. Desktop now defers that adapter
release until `ShutdownCompletion` without blocking its UI thread. The host-neutral
checks above do not exercise Desktop/COM teardown; verify attach replacement and
window close during a long request on Windows before closing this risk.

## Repeated confirmed VBA writes — 2026-09-30

The new screenshot starts the second user request at “Вы правы”. Within that
response, Agent reports three confirmed `common.vba_write_module` actions on
`P5_Consolidator` and proposes another correction after the user had observed the
original compile error disappear. The preceding screen section shows two unverified
VBA changes from earlier activity; those are separate outcomes and do not negate
the three later read-backs.
The screenshot does not show the exact second model request, VBA source or Windows
build, so it cannot establish whether the model missed the first after-state or
ignored it. A confirmed write proves matching source read-back, not compilation.

Follow-up photo shows successful patches interleaved with stale/ambiguous patch
errors and repeated renaming of already changed identifiers. Owner: model context
compiler / VBA patch engine and guidance. Two host-neutral defects are reproduced:

- A call's input evidence was treated as its result's currentness evidence. The
  write superseded that input, so the next request could contain `outcome: Error`
  and “Prior observation is not current evidence” alongside `VerifiedChanged` and
  complete correct after-source. The compiler now filters result observations
  independently; input evidence remains available for audit/guards.
- Completed mutations folded before CAS hydration and discarded `data`, losing
  patch hunk diagnostics and structured recovery. Folding now follows full result
  hydration/projection and retains semantic data within the complete request
  budget. Missing mutation payloads fail explicitly; no terminal status is invented.

The earlier regression checked source presence only; its final-step assertion was
also on an unreachable request number. The corrected Agent test asserts actual
completion and the next request's successful write outcome outside the model stub.
Compiler tests cover fresh reads after stale input, all three result roles,
archived mutation data above 8192 characters, patch coordinates and budget refusal.
The outcome assertion failed before correction and passes afterward.

Patch now accepts exact line/column coordinates with old-text/context verification,
or unique context. Ambiguity returns explicit candidate locations. A stale selected
location cannot redirect to another matching line. Tests cover repeated text in
different procedures, a repeated old patch, newline styles and bounded diagnostics.
Guidance requires a concrete remaining defect before editing a verified after-state.
These host-neutral checks do not establish the exact photo incident's full cause
or real-model behavior; Windows/Office and the affected model trajectory remain open.

Broader result-delivery audit (2026-09-30), owner: conversation model session and
context compiler. Further defects were identified and corrected:

- Confirmation created a model session and compiled its initial snapshot before
  appending the terminal result. That cache could serve the next request without
  the already completed action. Compilation now occurs at the request boundary;
  accepted history appends invalidate cached snapshots.
- After compaction, complete current input evidence on a later assistant message
  suppressed source carry-forward, although that message contained no source bytes.
  Only actual source-bearing observations suppress the archived body. A focused
  regression reproduced zero restored bodies before correction and one afterward.
- Media preparation reconstructed materialization without ResourceEffect,
  ResourceEvidence or AuthorityCommitId. The explicit reproduction failed with a
  missing verified effect. Both media and general delivery failures now preserve
  terminal semantics/evidence; a warning does not imply the mutation did not happen.
- Large mutation archival dropped module/title labels needed before result
  hydration and after compaction. Markers now retain semantic source labels.
- Current after-source and unresolved accepted-call hydration used an uncalibrated
  byte bound. They now use the same calibrated request-capacity admission as other
  payloads, followed by the complete request budget check.

The successful confirmation regression uses 100 ordered hunks, forces result CAS
archival, verifies one dispatch and complete labeled after-source in the next
request, and checks the provider message builder preserves it. The stale-confirmation
fixture now keeps the original run identity so it tests source drift independently
of execution-identity mismatch. These checks do not exercise real Office or claim
that model decisions are deterministic when the correct result is supplied.

## Excel inspection loop — 2026-09-30

Photos show repeated successful `excel.inspect` steps, a separate 155.8 KB result
and a failed `excel.inspect charts` resource search, without visible progress to
the requested dashboard. Exact arguments, model requests and Windows build are
not supplied; the photo incident's complete causal sequence remains unverified.

Confirmed host-neutral defect before correction: `ConversationModelSession.MaterializeToolResultMessage`
archived result envelopes above 8192 **characters** and replaced their data with
`payload_externalized`, `complete` and a size. `ModelContextCompiler.RequiresExactPayload`
hydrated only resource/capability results; `excel.inspect` kept the marker even
with ample request budget. A readable tool-result artifact was created separately,
only above 8192 estimated **tokens**. Results between these thresholds could therefore
reach the model as `ok` / `complete=true` without business data or a discoverable
result resource. The full bytes remained in CAS; this was lost model input, not lost
durable data. Larger results required an additional semantic resource read.

A temporary probe invoked the production materializer and compiler from the
existing Debug harness assembly; portable-PDB SHA-256 checks matched all five
relevant then-current source files. With a 900000-token compiler budget, a 1070-character
result retained its observed chart name. A 16070-character result became a
199-character model message with no chart data and zero readable artifacts;
a 160070-character result became a 360-character message with one artifact but
no chart data. Neither archived payload was hydrated. No Office or live model was
used.

Correction (2026-09-30), owner: model context compiler / result projection (P1).
All selected non-folded result payloads now hydrate from CAS before model projection;
the resource/capability-only hydration filter is removed. The full JSON reaches the
model when the complete request fits, without a separate per-result token cap.
Storage thresholds remain storage decisions. Complete results no longer receive
an instruction to rediscover their already supplied data. Payload admission follows
calibrated request capacity; a real overflow uses existing compaction or an explicit
budget failure, without replacing generic data with a success marker. Focused tests
pass for 1/16/160 KB JSON in all three result roles, calibrated capacity and actual
overflow, plus existing result projection/current-source checks. Exact Windows,
Office and target-model reproduction remain open.

At the initial 2026-09-30 probe, AgentKernel blocked identical unknown-effect calls,
not successful repeated reads; the default 256 model iterations only bounded the eventual run length. Any
separate no-progress policy must preserve legitimate refresh/recovery. Resource
search is literal, so `excel.inspect charts` need not match the generic
`Tool result · excel.inspect` title. Correlate the Windows trajectory before
claiming this delivery defect caused every repeat in the photos.

## Agent recovery photos — 2026-09-30

The reported VBA patch loop, Excel snapshot refusal and empty-tools stall are
corrected at their host-neutral contract boundaries. The exact accepted model
arguments, source bytes, workbook UsedRange and Windows build in the photos are
unavailable, so their full causal sequence and target-model behavior remain open.
Owner: VBA mutation diagnostics / AgentKernel / Excel search / ModelProtocol.
Reproduce on Windows x64 + Office x64 + WebView2 with a redacted trajectory before
claiming the user scenario qualified; do not infer that a rejected patch wrote code.
The older R72 and R78 entries below describe their historical implementations;
the current empty-call and definite no-effect recovery rules are in
`docs/conversation-protocol.md`.

## Outlook VSTO pane report — 2026-09-29

Windows photos show two `CS0019` errors in Outlook `OfficeHosts` code; the other
missing-assembly errors follow the failed build. A separate debug photo shows the
Outlook VSTO DLL loading and two first-chance `ArgumentException` entries without
messages or stacks. Those entries do not establish why the pane was absent.

The `CS0019` expressions are corrected. Outlook Inspector/Explorer handle lookup
now uses `IOleWindow`; the prior `HWND` property lookup could yield zero and make
the ribbon action return without showing the pane. The ribbon now reports failure
to identify the active window. Owner: `RNAssistant.OfficeHosts` Outlook window
binding and `RNAssistant.OutlookAddIn` pane activation. Evidence still needed on
Windows x64 + Office x64 + VS 2022: rebuild `Debug | x64`, check the Outlook
COM Add-in load state, click Open Assistant in Explorer and Inspector, and capture
the full exception text/stack if either path fails. No Office qualification is
claimed from the source correction.

## VBA patch incident — 2026-09-08

Original incident open; overwrite defect reproduced and corrected host-neutral.
User reports: assistant claims changes succeeded while requested code was
reported absent. Photos 1–2 show two DemoModule diff rows labelled unverified;
photo 2 shows a subsequent source read and patch attempt. Photo 4 contains
`Debug.Print` in SumRange, FormatHeader and CleanData after the later attempt.
The photos do not expose the initial accepted patch arguments, tool results,
exact read coverage, journal terminal or Windows build revision. Model prose
about a failed/overwritten patch is not execution evidence.

Initial code inspection and 19 focused host-neutral checks established:

- `VbaPatchEngine` / `VbaMutationService.ApplyPatch` reject stale or ambiguous
  hunks before dispatch; ordered replacements are applied to candidate text only.
- `VbaReadBackRejectsWriteDrift` injects backend success without changing source:
  result is `error`, code `vba_patch_verify_mismatch`, terminal `not_applied`.
  Unreadable/divergent final state is `unknown`; terminal persistence failure is
  also `unknown`. Current native/result projection retains these statuses.
- `RunChangesService.AddVba` requires matching live-text hashes for the retained
  diff. A write accepted by VBE-comparable verification can still get the same
  `unverified` label when VBE changes formatting. This is a diagnostic ambiguity,
  not proof that read-back accepted missing executable statements.

Run-diff follow-up (2026-09-08): retained original → intended code is now visible
for unverified results, including comparable-only formatting matches. A separate
planned label and subtotal distinguish it from exact confirmed source; the header
includes all available comparisons. This removes
the hidden-preview limitation; it does not establish the actual source for the
original Windows incident or close its evidence gate.

Owners: VBA mutation/verifier, model result projection, run changes projection.
Follow-up reproduction: one accepted model response contains an exact patch adding
`Debug.Print`, a source read, then a whole write containing the old source plus a
new header. Before correction, the whole write returned `ok`: `MutationCorrelation`
collected evidence from all chat messages, including the sibling read whose result
the model had not yet received. Both writes could match their own read-back; this
is an observation/authorization ordering defect, not concurrent COM dispatch or a
missing full-module patch buffer. Accepted calls now retain their input-snapshot
evidence and the VBA native owner uses that exact call's evidence. The regression
rejects the stale overwrite before dispatch and permits a corrected write after
the next model response receives the current source. Manual/editor guards and
multiple managed writes remain supported.

Next evidence: export the affected chat's accepted calls/results and exact source
read responses, plus correlated `mutation.prepared` / `mutation.terminal` and
before/intended source from `%AppData%/RNAssistant/vba-journals` / shared CAS; record
the actual Windows build revision. Determine whether the missing change was in
the accepted patch, rejected before dispatch, rolled back, left unknown, or replaced
by a later write, and what result reached the model. Do not infer the exact
cause for the original incident from photos alone. Windows/Office reproduction and
the diff-label ambiguity remain open; the reproduced sibling-observation defect is
fixed host-neutral.

Исходная база: `v16.0.4`. Приоритеты ниже — стартовая оценка из master plan,
не утверждение о воспроизведённых дефектах. Phase 0 не проверяла runtime/Office.
Отдельно отмеченные результаты Phase 1A получены с fake LLM/Office, не на реальном COM.
Phase 1B проверяет host-neutral correlation; production controller/Office/WebView
не исполнялись. Known baseline failure указан отдельно от новых trace tests.
Phase 1C проверяет runtime guard, replay/DTO и JS-проекцию без Windows execution.
Phase 2A проверяет ModelProtocol с fake endpoint; live tLLM не проверен.
Phase 2B закрывает R20 на host-neutral tests; provider retries проверены с fake
transport и injected delay, без реального network/backoff qualification.
Phase 2C1 проверяет только новый Core v3 contract; live parser/history остаются v2.
Phase 2C2 проверяет context wiring на host-neutral loop и current-v3 history reader;
active v2 client context не enforce. V3 cutover/old-chat guard и Windows ещё не проверены.
Phase 2C3A проверяет shared active wire и probes; v2 остаётся. Existing prompt-reset
characterization подтверждает R27 как gate будущего cutover, не как новый regression.
Phase 2C3B заменяет этот reset preservation/review flow; Core settings/loop и JS
проверены, production controllers/WebView/DPAPI — нет.
Phase 2C3C переключает actual wire/history на v3 и проверяет preflight, run IDs, singleton
safety, refusal и review/reset на prompt schema 12. Windows/live-provider gates остаются.

Screenshot corrections (2026-09-07, host-neutral): final warning now labels
`WriteError`/`WriteUnknown` as attempt history. Write errors do not by themselves
assert unresolved final failure; unknown effects retain a confirmation warning.
Historical counters remain authoritative and unchanged. Per-effect reconciliation
still requires explicit correction evidence and is not implemented by wording.
Owner: kernel summary / run projection.

Both accepted/rejected parser diagnostics now use the same best-effort helper.
Failure is reported without stopping repair; cancellation/attempt limits remain.
Mandatory runtime acceptance, execution and request/response storage still fail
closed. Owner: ModelProtocol + ModelTracePersistenceService. This fixes optional
parser-trace availability, not persistent storage corruption or queue contention.

Trace queue concurrent-waiter fix (2026-09-28): each scheduled write now carries
its own failure outcome. Previously the first terminal waiter could clear the
shared queue failure after all writes completed, letting a second terminal waiter
report success although its write was skipped. Both waiters now receive the failed
write outcome; a later request can retry only after the failed queue drains. This
is a host-neutral fix; live model/Windows behavior remains unqualified.

Chat navigation / persistence contention (2026-09-07, partially contained): user reports freezes
while streaming, loading artifacts and switching chats. `ChatStore.PersistenceSync`
is static across chats; `SaveInternalLocked` performs artifact externalization,
projection/diff and durable append under it. `SelectChat`/`GetChatState` currently
load and build the full projection, including Office document state. Bridge
dispatch now moves chat catalog/state/selection and complete agent/tool calls to
cancellable worker boundaries; bound Office work still uses its existing STA
dispatcher. This removes Office UI starvation and keeps cancel/navigation delivery
reachable during a long run. A cold validated replay or waiting for an append may
still delay completion of an individual navigation request. LLM raw stream traces
are already batched at 64 Ki characters or one
second of buffer age; this is not evidence of a durable save per token. Owners:
ChatStore + ChatSessionService/controller projection + Web bridge dispatch. Next
storage slice must separate host-neutral loading/projection from bound Office
capture and make lock wait observable/cancellable without weakening flush/commit
barriers. Measure
lock wait, replay/CAS, projection and bridge/UI durations on the actual Windows
candidate before assigning the dominant cause. The host-neutral stream UI fix
removes repeated history serialization/DOM detachment and batches background
sidebar paints; bridge worker dispatch contains UI starvation but does not close
this storage/navigation gate.

Navigation follow-up (2026-09-28): `SelectChat` now records load, active-selection and
full-projection durations above 250 ms in the runtime log; the WebView console
records bridge versus render duration for slow chat switches and exact HTML source
load duration above 250 ms. Full chat-state updates retain an already loaded HTML
source for the same exact revision and file metadata; a bounded in-memory exact
source cache avoids another download when revisiting a chat.
Follow-up inspection found that the 60-second focused WebView poll scans all chat
headers and reloads the active projection. For a chat exceeding the per-entry
projection cache limit (about 4 million characters), each load can validate and
replay the complete JSONL. Skill source selection also loads the addressed chat
before opening the small source; the editor evicts clean text from other skills,
so switching back reopens it. A model request trace synchronously waits for CAS
storage and a durable event append before HTTP dispatch, and these writes share
the static persistence lock with session saves. The background poll now skips an
active send; explicit refresh remains available. Runtime timings above 250 ms now
separate catalog active load, header scan, full chat detail, skill chat/open and
model trace persistence. These are code-path findings, not a measured Windows root
cause. The existing model diagnostics UI already separates preparation, response
headers and first chunk; compare those stages with trace persistence and WebView
bridge/render timing on the affected Windows host before changing storage cache
limits or replay rules.

Windows timing photo (2026-09-29): 25–26 chats account for about 70 MiB of JSONL;
header reads take roughly 0.6–0.9 s. One startup reports session 1.7 s, chats
0.6 s, libraries 2.0 s and projection 0.06 s; its bridge handler takes 4.4 s,
serialization 0.09 s for about 1.1 million characters, and browser render 0.11 s.
One completion reports `finalSave=26ms` and `responseProjection=2525ms`: the visible
“saving history” interval chiefly covers response preparation, not the final
JSONL append. The completion response rebuilds tools and skills and scans chat
headers; Agent mode invalidates the document VBA catalog before that response.
The response now logs those sub-times separately and uses a broader progress label.
The canonical save also reused its already serialized post-change projection for
the in-memory cache, removing a second full session serialization per save; event
bytes, hash validation and durable append ordering are unchanged.
Model setup separately reports compaction around 3 s and catalogs around 1.4 s.
Several failed model requests last about 42 s without an HTTP status, while later
successful requests show roughly 2–5 s to response headers and 70–144 KiB request
bodies. These are stage observations, not proof that JSON format causes the provider
delay or that a 70 MiB stream is safe to trust without replay validation.
The WebView message projection now omits hidden protocol bodies, extracted
attachment text and activity guard material; completion logs no longer repeat
tool `dataJson`, and bridge output avoids the intermediate token tree. Next Windows
run should compare response characters, handler/serialization time, header cache
behavior and the newly separated prompt/tool/skill times on the same chat; inspect
failed-request endpoint/cancellation evidence before changing model request limits.

Resource cutover / catalog freeze (2026-09-07, fixed host-neutral): the generation
captured by `UseInput` is carried into the model session and compared against its
final frozen `CaptureMany` tuple. Intervening publication refuses with
`RESOURCE_CATALOG_CHANGED` before compilation/dispatch; fresh binding is required,
without automatic model/tool replay. Existing request/repair retains its original
publication and settings/budget. The extended frozen-prompt regression covers
publication between capture and freeze, unchanged receipt on refusal and successful
fresh rebind. Owner: existing catalog capture / model-session boundary. Real
Windows execution remains unqualified.
See the finite closure order in [Resource Fabric](../resource-fabric.md#master-acceptance-reconciliation--2026-09-07).

Resource cutover / generic search capture (2026-09-06, fixed host-neutral): Gateway
now publishes typed live document/VBA scan captures independently of matches, then
binds bounded snippet evidence to the published logical revisions. Zero-match drift,
historical no-I/O reads, missing-CAS refusal, partial VBA coverage and body-free
backup metadata pass focused checks. The matched-snippet publication path is removed;
no global polling/background rescan. Owner: Gateway + live document/VBA providers.
Real Windows/COM qualification and other host-specific search consumers remain open.

Resource cutover / Outlook (2026-09-06, open): `MailItem.Body` materializes a whole
COM string before the character ceiling can reject it; exact-capture completion
does not prove bounded source allocation. Owner: Outlook backend/resource cutover.
The former session-wide `ResolveMail(entryId)` lookup is contained host-neutral:
Inspector lookup uses only its retained mail; folder lookup supplies StoreID and
checks parent StoreID/EntryID before capture. Discovery reads bound headers only.
Real COM membership/unsaved-mail and large-mail execution remain unqualified;
no scope/large-mail Windows gate is closed.

Resource cutover / allocation deferral (2026-09-07, explicit user decision): Outlook
whole-string allocation above remains unresolved. Inspector's initially deferred
full-JSON-then-truncate path is now replaced by bounded early-stop serialization,
including bounded string escape buffers; its compiler/input snapshot is unchanged.
Owners/triggers are recorded in [BACKLOG](BACKLOG.md#user-deferred-source-allocation--2026-09-07).
This host-neutral correction does not qualify Outlook source allocation,
real WebView2 responsiveness, Windows/Office or a release candidate.

| ID | Priority | Риск | Владелец | Защита / фаза | Статус |
|---|---|---|---|---|---|
| R01 | P0 | Model completed скрывает write error/unknown или отсутствие write | AgentKernel / Application / UI | Phase 1C warning + Phase 3B2 shared kernel summary, actual event replay; production delivery R21 | contained host-neutral 1C; Windows qualification open |
| R02 | P1 | tLLM protection вместо JSON | ModelProtocol | 2A/2B: typed boundary, clean repair, общий лимит и fake protection/HTML tests; former v4 validation/repair проверены 2C3C + R29; current v6 contract описан в canonical protocol, live endpoint qualification отдельно | contained for fake content; live-provider gate open |
| R03 | P0 | Write применён, ответ потерян | Domain/Host | 6D–6J contain typed VBA writes; 7C and 11T1–11T8 move every current Office mutation family to exact typed owners with precondition/read-back verification and a dispatch marker before the first possible effect. 11T9–11T10 complete VBA/controller/custom switches and delete the result fallback. Verified no-op/change are distinct; apply/read-back failure after possible dispatch is non-retryable unknown | contained host-neutral for all current mutation families through 11T10; real Windows partial-effect/rollback/read-back qualification remains open |
| R04 | P0 | Patch направлен не в ту книгу | HostRuntime | 5B1: общий operation gate до guard/preparation, manual/resource/editor reads, повторная проверка после ожидания/confirmation и нейтральный session port. 5B2: direct selection/context/catalog reads switched host-neutral. 11T0/7D–11T8 bind exact Excel/Word/PowerPoint/Outlook objects or windows for their retained lifetime and remove execution-time active-document/window fallback; WQ0/WQ-SESSION квалифицируют принятое identity assumption | open evidence; all current host target fallbacks are removed host-neutral, but real COM proxy identity, close/reopen, Save As, multi-window and desktop/VSTO/native composition remain unverified |
| R05 | P1 | Schema исчезла из контекста либо snapshot изменился под тем же tool ID | ToolPack | 8A pins descriptor/schema + typed policy + handler/entry point/scope/host + package fingerprint in one immutable execution snapshot. 8B replaces callable LRU with atomic full-budget extensions/new revisions and no eviction/partial publication. 8C persists accepted/rejected decisions before publication and rematerializes only the exact accepted `TurnId` extension chain; drift/broken chain visibly falls to core until an accepted rebase, while raw/rejected evidence has no authority. [8A](PHASE_8A_TOOL_PACK_SNAPSHOT.md), [8B](PHASE_8B_CALLABLE_TOOL_PACK.md), [8C](PHASE_8C_TOOL_PACK_EVENTS.md) | contained host-neutral through confirmation/compaction/crash replay; Windows/live-provider WQ-PACK open |
| R06 | P1 | Модель не знает о tool | ToolPack/Discovery | 8B finite exact mode/host core plus 11O6 reassessment publish the complete routine profile (Excel + VBA write/patch); complete compact registry plus exact read admits rename/restore/delete/macro and other optional schemas atomically and reports rejection visibly. [Evidence](PHASE_11O6_MINIMAL_CORE_PACK.md) | fixed host-neutral for core/discovery/admission; live-provider and Windows WQ-PACK open |
| R07 | P1 | VBE нормализует source | VBA | 6A: один comparable canonicalizer и raw CAS hash отдельно; 6B: typed host read validation без повторного source unescape. Реальный VBE/read-back требует Windows | contained host-neutral 6A/6B; Windows/VBE qualification open |
| R08 | P0 | Journal расходится с live state | VBA | Read-only recovery; 6C–6G centralize module preparation/read-back. 6I package correlates session lifecycle and marker state; 6J rename classifies exact complete-before/complete-intended/mixed identities without replay. Prepared CAS reaches each backend | contained host-neutral through admitted module/package/rename boundaries; Windows reconciliation gate open |
| R09 | P0 | Cancellation после COM dispatch | Host/Domain | 6D module, 6I package and 6J rename: cancellation before dispatch records inspected terminal and rethrows; after dispatch is inspected and maps to non-retryable unknown unless intended/before state is proven. Session package cleanup uses a non-cancelled explicit mutation in `finally`. Real HostRuntime/COM cancellation remains WQ-SESSION/WQ-VBA | contained host-neutral for admitted typed VBA pipelines; Windows gate open |
| R10 | P1 | UI показывает устаревший статус | UI/Persistence | 9D5: every full projection carries session revision; per-chat UI monotonic guard rejects late detail and keeps a newer catalog summary, while canonical stream CAS rejects stale writers. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral 9D5; Windows multi-window/WebView acceptance open |
| R11 | P0 | Replay меняет outcome | Runtime / Persistence | Phase 3B2 stored `RunSummary`; 9D1–9D5 extend actual normal/error/unknown/pending/cancel/append-fault/recovery replay through equal immutable `RunViewState`, without policy recomputation. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral through Phase 9; Windows restart/WebView matrix open |
| R12 | P2 | Локальное исправление затрагивает десятки файлов | Architecture | Freeze, boundaries, change budget; все фазы | monitored |
| R13 | P2 | Версия/tag на каждый commit | Release process | Правила заменены; repeat-build/commit и release gates tests, Phase 0 | mitigated |
| R14 | P1 | Legacy/new paths сосуществуют бессрочно | Migration | 11T0/7D–11T9, 11J/11K and 11T10 switch every active consumer, delete generic host catalog/dispatch plus definition/result/UI projections and add forbidden-symbol/source-inclusion checks. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10; Windows WQ validates the typed direct behavior separately, not legacy coexistence |
| R15 | P1 | Feature flags становятся второй архитектурой | Application | Временный явный release scope, Phases 10–12 | open |
| R16 | P1 | Новые build metadata / ClickOnce не проверены на Windows | Release process | Сохранить исходную AssemblyVersion 16.0.4.0; qualification до release | open |
| R17 | P2 | Чужие незакоммиченные изменения попадут в Phase 0 | Governance | Проверить исходные файлы и stage только явный список Phase 0 | monitored |
| R18 | P2 | Source archive без Git потеряет build identity | Build | Явные SHA/branch/tree-state properties; отказ вместо скрытого fallback | documented |
| R19 | P1 | Two-stage release/signing scripts ещё не выполнены на release workstation | Release process | На Windows проверить prepare-without-tag → exact candidate/WQ → immutable signing → finalize/tag; обычные commits workflow не запускают | open; host-neutral gates cover MSBuild/runtime evidence contracts only |
| R20 | P1 | Лимит 20 retries допускал 21 invalid response вместо 20 attempts | ModelProtocol | Phase 2B: initial включён в total 1–20, valid на 20 принимается; provider retries/fallback считаются отдельно; red→green boundary tests | resolved host-neutral 2B |
| R21 | P2 | Optional trace может быть неполным; controller wiring/реальная UI delivery не проверены | Diagnostics / Application | Fixed-stage error log без payload; no effect decisions from trace; `ui.projected` — только DTO, CAS failure допускает пропуск marker после release lease; Windows validation в Phases 1C/5–9/12 | documented 1B; open |
| R22 | P1 | Full harness: compact catalog ожидал устаревшие host tool counts | ToolPack / Tests | 8A audited current public catalogs (Excel 15, Word 9, PowerPoint 9, Outlook 5), updated only stale expectations and retained removed-alias rejection | resolved host-neutral 8A |
| R23 | P2 | Legacy ToolResult не всегда различает частичный/неизвестный effect; успешный mutating call может быть no-op или иметь слабую domain verification | ToolRuntime / Domains | 3B2 preserved historical uncertainty; Phase 4 introduced typed runtime/result evidence, mandatory 11T/11J/11K switched every current family, and 11T10 deleted the final result/UI conversion. Historical missing evidence stays exact `unknown` and is never reconstructed from prose | resolved for active runtime host-neutral in 11T10; real Windows effect/read-back behavior remains WQ evidence |
| R24 | P2 | Media сохраняются и могут отправляться повторно на protocol repair: больше traffic и дольше lifetime | ModelProtocol / Resources | Один materialized accepted prompt, bounded retry/budget и release в finally; fake image integration pass; проверить реальные media/endpoint budgets до qualification Phase 12 | documented 2A; open |
| R25 | P2 | Provider retry после timeout/потери ответа может повторить оплачиваемую генерацию и увеличить latency | ModelProtocol / Release | Не более двух transient retries на весь step, delays 1s/2s с cancellation; raw ceiling N+3; no Office tool replay, no auth/429 retry; проверить реальные timeout/media/endpoint budgets до Phase 12 | documented 2B; open |
| R26 | P1 | Неполный accepted-run restore или неверная batch projection допустят неверную связь calls / unsafe batch | ModelProtocol / Runtime | R29: runtime IDs/origins и полный preflight. Current policy допускает в одном ответе только независимые локальные reads; каждая mutation — singleton. Batch authority source-owned и не восстанавливается из serialized catalog flags; unknown effect никогда не replay | contained host-neutral; production controller/Office qualification open |
| R27 | P1 | Schema mismatch / UI save могли молча заменить custom prompts или подтвердить старую схему | ModelProtocol / Settings | 4B introduced v4 + Tool Result v1/schema14; 8B advances to schema15 for callable-pack admission; 8C advances to schema16 for durable turn replay. Saved schema15 and older/future markers preserve text during ordinary/failed save; only explicit review/reset approves current instructions. Shared guard remains before preparation/confirmation | fixed host-neutral through schema16; production controller/WebView/DPAPI qualification pending |
| R28 | P2 | Пользователь сообщает об отсутствии live streaming на последнем HEAD; transport / projector / WebView причина не установлена | ModelProtocol / Application / UI | Сохранить progress forwarding при switch 3B2; диагностика существующего SSE → message/reasoning → bridge path, UI qualification Phase 9 и live-provider gate Phase 12 | reported 2026-08-28; not reproduced |
| R29 | P1 | Model-owned tool-call ID вызывал отказ всего ответа и новую генерацию полезного payload при служебной коллизии | ModelProtocol / Runtime | Атомарный v4 switch: kernel выдаёт ID до accepted persistence, сохраняет exact raw attempt/position origin; confirmation/result/replay используют тот же ID. [Evidence](R29_RUNTIME_CALL_IDS.md) | fixed host-neutral R29; original incident payload и Windows/live-provider qualification открыты |
| R30 | P1 | Target Resource/Tool Result contract возвращает CAS `content_ref` как второй model transport либо теряет `ResourceRef` при упрощении результата | Resources / ToolRuntime | Phase 8D переводит list/resolve/search/read на exact native read-only handlers поверх одного gateway, сохраняет bounded data + exact `ResourceRef`; media bytes живут только в request-local materialization, без нового durable transport. [Evidence](PHASE_8D_RESOURCE_DATA_PLANE.md), [ADR-0004](../decisions/ADR-0004-resource-data-plane.md) | fixed host-neutral; Windows/live Office/VBA/media WQ-PACK open |
| R31 | P2 | Встроенный common.prompt_authoring требовал unique model call id вопреки v4 ownership | Prompts / ModelProtocol | 4B: guidance исправлен, schema14/custom review сохранены; targeted skill-content/default prompt/probe checks pass. Runtime IDs/parser R29 не менялись | fixed host-neutral 4B; live skill incident not reproduced |
| R32 | P2 | Диагностика требовала сложной навигации между views/payload; JSON отображался разными read-only renderers, не было общего раскрываемого причинного журнала | Diagnostics / UI | [Phase 9 spec](R32_DIAGNOSTICS_JSON_VIEWER.md): `run-causal` source evidence → общий bounded/lossless viewer → direct latest/exact run journal; no inferred effects, no second durable store. Incident correction: filtered rows no longer turn absence of a loaded terminal row into global `Нет terminal`; metric explicitly stays selection-scoped. [Evidence 9C](PHASE_9C_RUN_JOURNAL_UI.md) | mitigated host-neutral through 9C UI plus selection-scoped terminal correction; Windows/WebView/reload/confirmation/live-append acceptance open |
| R33 | P1 | Exact patch принимал неоднозначный hunk с перекрывающимися совпадениями | VBA | После отдельного 6A: counter проверяет все стартовые смещения (`aaaa` / `aaa` → 2); ambiguity отклоняет весь patch до confirmation/write/нового backup/journal. [Evidence](PROGRESS.md#r33--overlapping-exact-matches) | fixed host-neutral 2026-08-29; 2 regression tests red→green, 8 targeted pass; real Windows/VBE regression pending |
| R34 | P2 | Успешный, но malformed VBA project/module payload мог считаться пустым/недоступным catalog и попасть в минутный cache; raw parsing дублировался | VBA / Tool discovery | 6B: единый typed `VbaReader` валидирует fields/identity/hash/truncation, malformed success прерывает load без partial cache, valid `modules: []` остаётся cacheable; regression red→green. [Evidence](PROGRESS.md#phase-6b--typed-vbareader) | fixed host-neutral 2026-08-29; real VBE/Trust Access/catalog refresh pending |
| R35 | P0 | LLM Markdown очищался устаревшим `DOMPurify 3.1.6`, входящим в уязвимый диапазон GHSA-v2wj-7wpq-c8vv | UI / Security | Отдельный user-requested hotfix: exact upstream `3.4.14`, npm integrity + vendored SHA-256, обе лицензии; `marked → DOMPurify` boundary и CSP сохранены | fixed host-neutral 2026-08-29; headless Chromium malicious-markup checks pass, real Windows WebView qualification open |
| R36 | P1 | Existing UI vendor inventory был неполон: не было единого file manifest/hashes/license texts/transitive asset audit; KaTeX ссылался на отсутствующие fallback fonts, часть icon provenance не отражалась | UI / Supply chain | Exact allowlist 36 runtime files, versions/commits/integrities/hashes/licenses; KaTeX WOFF2-only, Feather source attribution, fail-closed network/worker/WASM policy и admission test. [Evidence](R36_WEB_VENDOR_GATE.md) | fixed host-neutral 2026-08-29; первый новый vendor обязан расширить manifest, real Windows WebView2 qualification open |
| R37 | P1 | Accepted call с Markdown/developer tool-result role мог записаться как `tool.result.recorded`: writer ошибочно связывал semantic call/result с наличием native `ToolCalls`; повторный message upsert мог также дублировать accepted/start/finish causal rows | Persistence / Diagnostics | Current writer классифицирует новый accepted call только по runtime-owned `AcceptedCallOrigin` + `ToolCallId`; result role остаётся независим. Execution boundaries теперь принадлежат переходу состояния exact tool-call/run, а дальнейшие annotations/hydration сохраняются как `message.updated`. Explicit removal decision удалил historical inference: wrong-type retained operation показывается incompatible/reset-only и не входит в tool-execution | fixed host-neutral; semantic boundaries emit once, writer/reader have no compatibility inference, Windows diagnostics UI qualification remains open |
| R38 | P2 | Предпочтённый Web Awesome Tree использует ESM graph, который не регистрируется из текущего `file://` WebView host; скрытый custom bundle или host switch расширил бы build/security scope | UI / Hosting / Supply chain | 9B3: не собирать fork и не менять host; pinned zero-dependency Wunderbaum UMD за bounded local-array `TreeAdapter`, exact manifest/license и zero-network gate. [Evidence](R38_TREE_VENDOR_SWITCH.md) | mitigated host-neutral 9B3 for one consumer; Web Awesome/virtual host, other trees and Windows WebView2 qualification open |
| R39 | P2 | Diff renderer мог скрыто стать вторым diff algorithm или показать UI-generated projection как authoritative mutation evidence | UI / VBA diagnostics | 9B4 gate: проверить все consumers/DTO; Diff2Html admitted только для source-owned bounded unified diff. Текущие before/after consumers сохраняют один legacy bounded formatter без нового vendor. [Evidence](R39_DIFF_VENDOR_GATE.md) | contained host-neutral; vendor deferred, future unified-diff contract and Windows VBA/WebView qualification open |
| R40 | P1 | Restore confirmation guard фиксировал current module, но не exact backup; подмена `backupId` после preparation могла применить неподтверждённый source | VBA | 6G: dedicated guard связывает exact backup id/module/type/canonical live-source hash и current target existence/source hash; mismatch блокируется до journal/dispatch. Raw CAS hash остаётся storage evidence. [Evidence](PHASE_6G_VBA_RESTORE.md) | fixed host-neutral 2026-08-30; real Windows confirmation/VBE qualification open |
| R41 | P1 | Временный VBA package может остаться после применённого session install при потерянном terminal/cleanup; marker-insensitive probe затем считает совпадающий source обычным `installed` и может выполнить его без очистки | VBA packages / Recovery | 6I: один typed package owner; durable lifecycle id записан в install/cleanup и exact session marker. Probe объединяет live marker/source/type с append-only journal, блокирует run/persistent overwrite при незакрытом lifecycle даже после marker strip; recovery read-only, cleanup только fresh explicit journalled Uninstall. Prepared CAS additionally refuses post-prepare source/type/marker drift before install mutation. Terminal/cancellation/restart/cleanup/marker fault cases pass. [Evidence](PHASE_6I_VBA_PACKAGE_LIFECYCLE.md) | fixed host-neutral 2026-08-30; production Windows/VBE/Trust Access qualification open |
| R42 | P1 | Legacy rename guard связывал source только по hash, не по component type, а executor path не передавал cancellation в prepared→dispatch boundary; одинаковый source после type race мог быть переименован вне подтверждённого состояния | VBA rename / Recovery | 6J: rename-specific typed guard связывает оба имени, source hash/type/code-only state и correlation; backend повторно проверяет hash/type до COM. Cancellation проверяется после durable prepare и effect классифицируется before/intended/mixed; terminal loss recovery read-only. [Evidence](PHASE_6J_VBA_RENAME.md) | fixed host-neutral 2026-08-30; production Windows/VBE/confirmation/cancellation qualification open |
| R43 | P1 | `excel.inspect` перечислял workbook collections без общего bound и для defined name читал `RefersToRange.Value2` без cell ceiling; большой named range мог materialize незапрошенный массив и задержать COM/память | Excel read / Bounds | 7B: один typed inspect owner, 200-item/100-series bounds и truncation; defined names возвращают metadata без `Value2`; `read_range` ceiling передаётся backend и проверяется до values/formulas materialization, затем dimensions проверяются повторно domain owner. [Evidence](PHASE_7B_EXCEL_READ.md) | fixed host-neutral; real Excel COM/protected-sheet/large-workbook qualification open |
| R44 | P1 | HTML data bind/refresh вызывал `_adapter.ExecuteTool` напрямую; partial native switch `excel.inspect`/`excel.read_range` оставил бы отдельный legacy result/limits/error path | Excel read / HTML | 7B injects тот же typed read adapter под уже взятым live-document access; switched public IDs не достигают host adapter, второй operation root/fallback отсутствует. [Evidence](PHASE_7B_EXCEL_READ.md) | fixed host-neutral; Windows WebView2/Excel refresh qualification open |
| R45 | P1 | После mandatory append failure controller правильно не сохранял mutated `UnpersistedSummary`, но reconciliation вызывался только при startup; в том же процессе durable open dispatch мог остаться в UI как running до restart | Persistence / Recovery / UI | 9D2: оба controller path release ownership, discard memory и через один `ChatSessionService` reload exact stream/reconcile once. Pre-dispatch pending сохраняется; possible dispatch становится unknown; fabricated terminal, append retry и tool replay отсутствуют. [Evidence](PHASE_9D2_RUNSTORE_RECOVERY.md) | fixed host-neutral 9D2; Windows controller/WebView qualification open |
| R46 | P1 | Broad Office `AppendTrace(type, ...)` не различал authority/diagnostic и mandatory/best-effort; новый consumer мог записать произвольный event type или принять marker за replay authority | Persistence / Diagnostics | 9D3: closed `SessionEventKind` descriptors + один `IEventStore`; storage lifecycle закрыт для port writes, accepted `session.commit`/ToolPack authority отделена от rejected/causal markers, direct Office string append/read удалён, raw `ChatStore` event API internalized. [Evidence](PHASE_9D3_TYPED_EVENT_STORE.md) | fixed host-neutral 9D3; Windows controller/WebView persistence qualification open |
| R47 | P1 | Session/controller/kernel consumers зависели от broad concrete `ChatStore`, поэтому application code мог обойти intended projection boundary, смешать aggregate operations с artifact/CAS/event internals или создать второй write path при следующем изменении | Persistence / Application | 9D4: один минимальный `IConversationStore` + `ChatConversationStoreAdapter` над тем же backend; active aggregate consumers switched atomically, replaced public conversation API internalized. Recovery выражен intent operation, raw lifecycle/CAS не опубликованы; concrete controller access ограничен artifact/HTML revision owners. [Evidence](PHASE_9D4_CONVERSATION_STORE.md) | fixed host-neutral 9D4; Windows controller/restart/multi-window qualification open |
| R48 | P1 | UI собирал outcome из model `ResponseStatus`, flat `RunExecutionSummary` и Activity; verified change/no-change терялись, legacy `ok` мог выглядеть доказанным, а поздний bridge response — заменить более новую историю | Application / UI / Persistence | 9D5: один immutable `RunViewState` из `KernelState` + source-owned effect evidence; unverified success stays unknown, bridge/catalog/message consumers switched atomically, session-revision ordering and stale integration covered. Flat type/getter/fields/readers removed. [Evidence](PHASE_9D5_RUN_VIEW_STATE.md) | fixed host-neutral 9D5; Windows WebView/reload/confirmation/live-append acceptance open |
| R49 | P1 | Три host-specific physical files (`DocumentIdentity`, `VbaProjectSupport*`) оставались в host-neutral Office assembly; это противоречит owner boundary и позволяет новым COM dependencies обойти OfficeHosts | Architecture / Host | 10B1/10B2 перенесли exact files в OfficeHosts без aliases/duplicate backends. Скрытая internal marker dependency заменена узким public read-only Office.Vba contract, broad friendship не добавлена; boundary-test запрещает обратных Office consumers. [Audit](PHASE_10A_BOUNDARY_AUDIT.md), [10B1](PHASE_10B1_DOCUMENT_IDENTITY_MOVE.md), [10B2](PHASE_10B2_VBA_HOST_BACKEND_MOVE.md) | fixed host-neutral 10B2; production OfficeHosts/VSTO compile, WQ0 и real Windows/VBE qualification open |
| R50 | retired (was P0) | Встроенный runner мог дать ложный pass или затронуть пользовательский документ | Qualification / Application / Host | Qualification Center, packs, helper и in-app admission удалены 2026-09-30; в текущем runtime нет отдельного test executor. Историческое решение: [ADR-0010](../decisions/ADR-0010-qualification-evidence-authority.md). | retired with removed feature; Windows/Office behavior remains open evidence |
| R51 | P1 | Artifact UI смешивает draft и durable semantics: committed resource может не появиться в library до terminal response, generic upload показывает только metadata, Plan помечен JSON, а `v1`/edit/delete по extension создают ложную модель версий и authority | Resources / Application / UI | 11A1 queues the full committed state before model transport and its live correction queues the same revisioned state after every durable tool result, including confirmation continuation. 11A2 adds one replay-derived server projection for immutable/versioned/derived classes and exact heads/history. 11B1–11B3 establish complete exact Plan lineage/UX. 11C1–11C3 establish one HTML lineage/checkpoint owner, inert exact upload import, binding completeness/integrity and guarded export of one pinned CAS revision without JSON normalization. 11D1 adds exact bounded text/source and complete-only sanitized Markdown viewers over the shared gateway without a second transport. [11A1](PHASE_11A1_ARTIFACT_COMMIT_PROJECTION.md), [11A2](PHASE_11A2_ARTIFACT_LIBRARY_PROJECTION.md), [11B1](PHASE_11B1_PLAN_REVISION_GUARD.md), [11B2](PHASE_11B2_PLAN_RESTORE_TOMBSTONE.md), [11B3](PHASE_11B3_PLAN_HISTORY_HANDOFF.md), [11C1](PHASE_11C1_HTML_LINEAGE.md), [11C2](PHASE_11C2_HTML_IMPORT_PREVIEW.md), [11C3](PHASE_11C3_HTML_BINDING_EXPORT.md), [11D1](PHASE_11D1_TEXT_MARKDOWN_VIEWERS.md) | contained through 11D1 host-neutral; live tool-result timing fixed host-neutral, while image/PDF/audio viewers, other committed-resource removal and Windows WebView qualification remain open; WQ/Phase 12 unchanged |
| R52 | P0 | Текущий cross-host выбор чата/документа использует COM/ROT attach: при нескольких Office instances или устаревшем HWND target может быть неоднозначным либо неверным. После bind принятый run закреплён за exact document session, но это не доказывает правильность выбора исходного процесса | Host / Application | Текущий путь: `OfficeHostChatCoordinator` → `OfficeComAdapterProvider` → exact adapter/controller rebind. [Host Fabric](../host-fabric.md) отложен как owner-endpoint registry/lease и typed dispatch на STA владельца без cross-process COM fallback | UI cross-host chats реализованы host-neutral; multi-instance Windows/Office verification и endpoint architecture остаются открытыми |
| R53 | P0 | Browser/files/shell превращают Office assistant в ambient local execution authority; prompt/file/page может расширить доступ, выйти из root, уничтожить данные или использовать Office как application-control bypass | Local Automation / Security | Staged [Local Automation contract](../local-automation-agent.md): prior workspace-session ADR, immutable explicit grants, Resource Fabric reads, guarded/Recycling mutations, browser isolation, typed non-shell process runner and signed isolated worker; raw shell/desktop are separate deny-by-default capabilities | open; design accepted docs-only, no local automation capability is admitted or implemented |
| R54 | P0 | Skill-shaped upload или stale custom package может получить trusted instruction authority без explicit install; flat overwrite/delete теряет provenance/history, а показ skills как chat artifacts смешивает global deletion, context transport и host scope | Skills / Capabilities / Storage / UI | Phase 11 [Skill Library contract](../skills.md): installed package != ChatArtifact, explicit confirmed import, immutable package revisions over core+references, append-only tombstone/restore, exact capability read only, later-run catalog refresh and selected endpoint host scope | open; progressive reads implemented, target history/import/UX accepted docs-only and deferred without expanding WQ-A/Phase 12 |
| R55 | P1 | Release evidence другого build или неподтверждённые сценарии могли быть объявлены квалификацией | Release | In-app admission удалён. Формальный релиз сохраняет signed detached manifest; скрипт проверяет подпись и exact commit/version, а release owner сверяет сценарии и hashes по [release process](../operations/RELEASE_PROCESS.md). | open manual review gate; no in-app pass claim |
| R56 | P0 | Library показывает tool другого host/revision как доступный либо authoring меняет catalog уже принятого run; flat overwrite/delete теряет package provenance и может shadow built-in/skill id | Tools / Host Fabric / Capabilities / UI | Phase 11 [Tool Library](../tool-library.md): сначала read-only selected-endpoint inspector над immutable snapshot, затем exact package history/conflicts, no-shadow, confirmed import and next-run-only refresh after Host Fabric pinning | open; target accepted docs-only, current flat authoring/UI and Windows multi-host behavior not qualified |
| R57 | P1 | Отдельный список проблем мог стать вторым outcome store или скрыть unknown | Diagnostics / UI / Privacy | Proposed Issue Center снят со scope. Текущая [история чата](../trajectory-query.md) читает существующие события, детали и payload открывает по запросу; VBA journal остаётся recovery evidence. | simplified UI delivered; Windows/WebView2 delivery remains open |
| R58 | P1 | Массовая «typed migration» поверх generic `ExecuteTool(ToolCommand)` или unbound/nullable session скрывает старый target/dispatch path, размножает compatibility backends и создаёт ложное ощущение надёжных tools | Office domains / ToolRuntime / Host | 11T0/7D–11T8 switch all current Office families to exact bound sessions/direct typed backends; 11T9A–11T9C and 11J/11K switch VBA/controller/authoring owners. 11T10 deletes `GetBuiltInTools`/`ExecuteTool`, generic catalog, compatibility definitions/results/UI projections and retired fake queues; exact policy/binding is mandatory at snapshot capture. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10; WQ0/WQ-SESSION/host Windows evidence remains open and any failure fixes only the typed direct path |
| R59 | P0 | OpenAI-compatible endpoint может прислать terminal `finish_reason`, но не `[DONE]`/EOF, оставив фактически завершённый model step активным до общего 30-минутного timeout | ModelProtocol / Transport | Streaming reader принимает non-empty `choices[0].finish_reason` как terminal evidence, bounded одну секунду дочитывает optional usage/`[DONE]` и завершает response без inference из model text. Cancellation и общий request timeout сохраняются | fixed host-neutral; open-stream и final-usage regressions pass, real Qwen/live endpoint Windows qualification open |
| R60 | P1 | После direct Outlook switch четыре legacy harness fixture всё ещё ставили queued `outlook.create_draft` result и ожидали generic adapter dispatch; native handler их не потреблял, поэтому full harness был ошибочно красным | Tests / ToolRuntime | 11T10 moved the affected fixtures to direct typed fake state/fault hooks, deleted the retired command/result queue and repeated the complete matrix without restoring Outlook legacy dispatch. [Evidence](PHASE_11T10_ACTIVE_LEGACY_REMOVAL.md) | resolved host-neutral 11T10: full harness 589/589; the original 572/576 result on parent `32a6f7a` remains historical characterization |
| R61 | P0 | Model-facing tool schemas смешивают semantic intent с runtime transport/guard state, поэтому модель или человек в Library вынуждены подставлять URI, UUID, artifact IDs, revisions и cursors; загруженные skill bodies повторяют эту choreography, а flat text-only test form скрывает реальные типы и ограничения | Tools / Skills / Resource Fabric / ModelProtocol / Tool Library UI | Mandatory Phase 11 [all-tool contract audit](../tool-library.md#mandatory-all-tool-contract-audit-r61) и [failure/ownership audit](R61_TOOL_CONTRACT_AUDIT.md): отдельные minimal intent schema и runtime-owned execution context над тем же ToolRuntime; no fabricated opaque values, per-family atomic tool/skill cutover, UI-only built-in documentation, typed Library test controls and reported layout fixes | partially contained host-neutral: 11O1–11O5 switch Resources/Capabilities, planning, HTML, Prompt/Tool/Skill authoring and VBA/macro to semantic intents; installed packages fail closed on unexplained plumbing-shaped arguments. 11O6 finalizes the 21-schema Excel core and moves rename/restore/delete/macro to exact on-demand admission without changing authority. Inventory is 69 unique ids / 72 effective host variants. Library documentation/Test/layout and live-provider/Windows WebView2 WQ-PACK remain open before Phase 12 |
| R62 | P1 | Ошибка после начала основного `SendChat` закрывала activity и flat `LastRun.Status`, но оставляла authoritative `KernelState` в `running`; после bridge refresh UI бесконечно показывал, что модель думает или выполняет действие | Application / Persistence / UI | Main new-run error path теперь, как уже существующий confirmation path, вызывает `AgentRunState.Interrupt` до diagnostic/save/rethrow. Записанные tool counts/effect evidence сохраняются, возможный незавершённый effect остаётся conservative unknown, retry/replay не добавлен. Source-wiring regression фиксирует обязательный terminal переход | fixed host-neutral 2026-09-02; 3 targeted run-view/tool-error/model-failure regressions pass, MockDemo actual-controller compile 0 errors / 3 existing CA1416 warnings; Windows WebView model/tool-error retest open |
| R63 | P1 | После native cutover HTML workspace `common.html_data_bind` / `common.html_data_refresh` брали document gate, но выполняли вложенный Office data-source read вне owner STA; реальный Excel fail-closed возвращал `document_session_thread_mismatch` | HTML Workspace / HostRuntime | Session-aware source callback направляет только вложенный Office read через exact bound `HostRuntime.ReadDocument`; HTML/CAS работа остаётся вне document gate, retry/fallback не добавлен | fixed host-neutral 2026-09-02; bind/refresh bound-dispatch regression red→green, real Windows Excel/WebView qualification open |
| R64 | P1 | Matching x86 PDFium/Skia are now vendored and packaged, but real Office x86 loading/rendering/model-send behavior has not been executed; a loader/export mismatch could still fail only on the target machine | Resources / Attachment routing / Packaging | Never cross-load the x64 pair. Windows x86 qualification must cover text-readable import/extraction, PDF preview, scanned-page rendering, explicit loader errors and image-capable model sending before x86 support is claimed | updated 2026-09-02: PdfPig closure is IL-only `32/64`; exact-package PE32 x86 PDFium/Skia hashes and host-neutral package wiring are recorded; real Windows x86 execution remains open |
| R65 | P1 | В сложной HTML/VBA задаче модель начала mutation до загрузки подходящих skills и исследования source, создала Skill раньше основного решения, вложила лишний `arguments` в вызов write, упростила dashboard после preflight error и завершила run как успешный без binding/verification evidence | Prompts / ModelProtocol / Skills / HTML | Schema 23 ввела readiness, Task List, root arguments, source → primary deliverable → bind/test → requested reusable authoring и evidence reconciliation; schema 24 закрепила это как явный six-stage workflow с completion gate; schema 25 усиливает open Task List как незавершённый финал. Format repair удаляет лишний `arguments`; HTML/Skill guidance запрещает placeholder degradation и преждевременное skill authoring | contained host-neutral in defaults/repair/skill guidance; saved prompts require explicit review/reset, live-provider and Windows Excel/WebView regression remain open |
| R66 | P1 | Full-document HTML preview вставлял workspace JS после внедрения ECharts и мог найти literal `</body>` внутри minified vendor string; ECharts повреждался, browser выдавал SyntaxError, а vendor text отображался вместо dashboard | HTML Workspace / WebView UI | Scripts вставляются относительно closing body/html исходного документа до vendor head injection. Full-document regression исполняет ECharts и workspace JS; зависимость `Dependencies/echarts.min.js` проецируется отдельно как loaded/read-only, а не как mutable workspace file | fixed host-neutral; local Chromium verifies ECharts 5.6.0 + canvas, exact Windows WebView2 preview/export remains open |
| R67 | P1 | Built-in skills и editable prompt layers могли расходиться с текущими tool schemas: host skills не называли точные операции/DoD, HTML guidance не задавал устойчивую multi-file структуру и ECharts/layout/accessibility lifecycle, а drift exact ids обнаруживался только в live run | Prompts / Skills / Tool catalog / HTML | Schema 24 разделяет authority system/skill/tool, задаёт six-stage lifecycle и запрещает terminal scaffolding; schema 25 усиливает finish gate для Task List и live HTML render evidence. Все common/host built-ins получили prerequisites, exact tools, recovery и DoD; HTML требует split HTML/CSS/JS, bundled read-only ECharts, responsive accessible UI and evidence boundary. Harness разрешает каждую capability-ссылку против catalog и pin-ит изменённый descriptor revision | contained host-neutral; saved prompts require review/reset and target-model complex Excel/VBA → HTML plus Windows WebView2 qualification remain open |
| R68 | P1 | `common.html_data_refresh` менял JSON только в transient workspace projection и не создавал новый active artifact; следующий `getHtmlWorkspace` гидратировал старую ревизию, поэтому кнопка `Данные ↻` выглядела успешной, но preview возвращался к прежним значениям. Экспорт также мог молча скачать chart page без runtime, если bundled ECharts не загрузился | HTML binding / Artifact lineage / Export | Refresh после changed reread фиксирует replacement authoritative HTML head без нового Undo step и возвращает exact resource evidence; verified no-change не создаёт artifact. Regression меняет Excel cell и проверяет JSON плюс artifact snapshot/history. Standalone assembly исполняет pinned ECharts и bound `rnassistant.table.v1` до workspace JS, не содержит Office bridge и fail-closed при отсутствующей dependency. HTML skill/tool descriptions задают точную table shape, source→bind→authoring order и refresh/export DoD | fixed host-neutral 2026-09-03; real Excel cell edit → toolbar refresh → WebView2 rerender and downloaded-file ECharts interaction remain open on Windows |
| R69 | P1 | Artifact Library отправляла immutable `rnassistant.chart` artifact в generic JSON preview, потому что ECharts renderer принимал только inline tool activity | Artifact Library / WebView UI | Chart renderer принимает exact artifact JSON; Artifact Detail выбирает domain preview до безопасного JSON fallback, а Details сохраняет точный payload. Cache keys и browser regression покрывают новый route | fixed host-neutral 2026-09-03; real Windows WebView2 Artifact Library preview remains open |
| R70 | P1 | Один Agent run мог создать chart artifact и HTML workspace revision для одной визуализации; карточка ответа показывала `Ресурсы · 2` (`Диаграмма · HTML workspace`), хотя в чате была одна диаграмма | Chat resource cards / Skills / HTML | Run resource cards скрывают supporting HTML workspace, когда в том же run есть chart artifact, но оставляют HTML-only workspace видимым. HTML skill guidance требует выбрать один visual surface и не вызывать `excel.create_chat_chart` для тех же данных при ECharts/HTML dashboard | fixed host-neutral 2026-09-03; real Windows WebView2 resource-card count and target-model behavior remain open |
| R71 | P1 | После успешного refresh JSON bound data обновлялся, но generated HTML dashboard мог оставаться пустым/старым: модель угадывала имя data source или обращалась к Excel label (`Продажи`) вместо canonical key (`продажи`); также run мог завершиться с открытым Task List | HTML binding / Preview / Prompts / Task List | Table transform сохраняет canonical `columns[].key` и добавляет first-row label aliases в rows; assembled HTML добавляет `RNAssistant.data.first/defaultName` и warning fallback при единственном data source; preflight повышает missing data-source reference до error, когда workspace data уже существует. Schema 25 и task-tracking skill запрещают successful final с open Task List | fixed host-neutral 2026-09-03; real Windows WebView2 live refresh/export and target-model behavior remain open |
| R72 | P1 | Empty `tool_calls` мог завершить model loop после промежуточного ответа модели | ModelProtocol / AgentKernel / Persistence | Conversation-response v5 завершает loop только при `final=true` и пустых calls. `final=false` с пустыми calls отвергается до acceptance и получает bounded format repair; повторный invalid ответ не dispatch-ит tool. `final` не является evidence effect/status | fixed host-neutral; live-provider and Windows target-model qualification remain open |
| R74 | P1 | HTML skill guidance accidentally generalized one user example into a preferred dashboard layout shape, and the general completion gate did not explicitly require final read-back plus bug/regression review before handoff | Prompts / Skills / HTML quality | HTML skill is neutralized around page purpose, information architecture, semantic HTML/CSS/focused JS, bundled ECharts only when needed, and content-driven layout with no fixed widget template. Prompt schema 27 requires latest user-visible read-back, likely bug/regression check, in-scope repair loop and explicit fit-to-handoff decision before successful `final=true` | fixed host-neutral 2026-09-03; saved prompts require review/reset and target-model behavior remains open |
| R78 | P1 | User reports an Agent run that said the task was done but continued repeating actions. Screenshot shows different successive writes to `index.html`/`styles.css` and Task List at 1/3. Exact causal sequence is unknown without the trajectory | ModelProtocol / AgentKernel / ToolRuntime / HTML Workspace / Task List | v5 ends only on `final=true` with empty calls; non-final prose may claim completion. ModelProtocol rejects empty non-final calls before acceptance; successful different writes may still continue. A singleton-mutation boundary now rejects batches containing writes. HTML `write_file` was confirmed to replace existing source without model-visible current-source evidence, while context reduction drops the authored source from later write-result frames. Existing-file replacement now accepts a complete source read actually shown to the model in any accepted request of the same run while that file member and its exact content hash stay unchanged. Focused host-neutral tests cover unseen/current/stale replacement, omitted evidence, unrelated file edits and cross-run isolation. Correlate accepted calls/results, source reads, effect records, Task List revisions and compaction boundaries in the affected run | overwrite and source-carry paths contained host-neutral; original loop cause and target-model/Windows/WebView2 behavior still open pending trajectory |
| R79 | P1 | Freshly created HTML files were found, but the model read their exact file targets with workspace-only `structure`; repeated view errors stopped the run | Resource reader / HTML workspace | Model-facing read negotiates this exact mismatch to complete `source`, names the actual representation, and retains exact read evidence. Find/skill usage states which view belongs to root and file; all other unsupported views still fail | fixed host-neutral 2026-09-28; target-model/Windows/WebView2 regression open |
| R80 | P1 | Active Task List could silently lose unfinished stages or permit `final=true` while still open; a Windows screenshot shows `task_list_not_terminal` after an attempted completed close with the list at 1/6, followed by run failure | Task List / Agent completion | Saves preserve goal and prior step text/order; different scope requires `superseded`. Completed close still requires every step completed. Rejected close now names unchanged unfinished indexes; a tool call between premature finals permits correction, while consecutive premature finals fail | mitigated host-neutral; focused harness passes, exact Windows/target-model recovery remains open |

R78 observation follow-up (2026-09-28): a complete read shown to the model in an
earlier accepted request of the same run now remains valid for whole-file HTML
replacement while that file's member identity and exact content hash match.
Unrelated workspace file changes and later context reduction no longer force
repeated reads. A changed file, foreign run or read never shown to the model is
still rejected. The affected Windows run trajectory is still needed to confirm
the exact historical sequence.

R78 context follow-up (2026-09-29): completed mutations were folded without
their exact after-state; compaction could remove the last complete source/text
read. Current published VBA, Markdown, Plan and HTML file source is now carried
as an authority-checked model observation, including across compaction. Older
versions remain folded or stale. Oversized current source fails the request
budget explicitly. Host-neutral tests cover the next HTML overwrite with this
observation; the reported run's historical cause and live model/Windows result
are still open.

Historical R78 follow-up described a `repeated_refresh_required_failure` guard
after three `ConflictNoEffect/RefreshRequired` calls without a satisfying read.
The 2026-10-01 source audit finds no such guard/code in the current runtime;
`AgentKernel` has an identical-unknown-call guard and overall iteration limits.
Do not count the historical statement as current protection. The shared recovery
and no-progress work is open in the current continuity incident above. Model
projection now omits source hashes; exact evidence remains runtime-owned.

## Архитектурный аудит 2026-08-28

Baseline `15dea46` (Phase 3B2). Проверены target contracts и Phases 4–12 master plan против canonical conversation/resource/session/VBA docs и точечных runtime contracts. Это аудит документов и границ, не повторная проверка всего runtime или Windows qualification. R29 — подтверждённый design bug; остальные строки уточняют противоречия/пробелы плана и существующие риски, не объявляют новые runtime failures воспроизведёнными.

| Контур / риск | Найденное противоречие или пробел | Исправленное требование и gate |
|---|---|---|
| ID / R29 | Master §7.1 продолжал представлять model-generated ID как окончательный контракт | Действующий v3 отделён от обязательного runtime-ID correction. Raw response неизменяем, mapping хранится в accepted event; replay не генерирует ID, collision не вызывает model repair после switch |
| Effect evidence / R01, R23 | `WriteOk` invocation count можно было принять за verified writes; policy `verification` не доказывает actual read-back | §§7.3–7.6: typed evidence отдельно от policy/status, no-op отдельно от фактической записи; Phase 4 fake-handler и Phases 6/7 domain cases |
| Batch/control ownership / R26 | Phase 4 поручала whole-response batching ToolRuntime, хотя порт исполняет один call; target v1 не объяснял pending/awaiting-user | Полный batch проверяют ModelProtocol/kernel до первого dispatch, на runtime policy; ToolRuntime сохраняет typed control signals и возвращает record, kernel учитывает/сохраняет один раз. Не считать любой read independent local |
| Resources / R30 | §7.8 предлагал CAS `content_ref`, а §7.4 не сохранял transport для больших результатов | Удалён новый envelope/transport; сохраняются ResourceRef и существующие bounded readers/serializer references. Gates Phases 4/8 |
| ToolPack / R05, R06 | Snapshot по IDs и запрет LRU не определяли изменение handler/policy, compaction и admission limits | §7.7/Phase 8 pin-ят schema+policy+binding, проверяют budget до publication, восстанавливают exact schemas после compaction. Намеренный переход к конечному immutable core pack сохранён, текущий LRU не меняется заранее |
| Host / R04, R09 | Сериализация только writes оставляла guard-read/read-back вне явно заданного критического окна | §7.9/Phase 5: один bound target/reentrant gate для live read/prepare/write/read-back и manual/resource paths, без ожидания LLM/пользователя под document lock; tests гонок/identity/lock order |
| VBA / R07 | Нормализация transport/comparable source не была явно отделена от raw CAS identity | §10/Phase 6: JSON decode один раз, raw bytes и comparable hash различаются; literal backslashes/строки/комментарии не меняют смысл |
| Persistence / R03, R11 | «Разделить events» и «каждый write возвращает terminal result» допускали второй store или выдуманный terminal при сбое persistence | Phases 6/9 сохраняют один chat stream и domain journals, ordered durable barriers и unresolved evidence после сбоя; no replay effects. Actual minimal replay из 3B2 не объявляется полной Phase 9 матрицей |

Также исправлены stale v2/model-owned status и media-repair формулировки в canonical session/conversation docs: они противоречили active v3 и существующей materialized-step lifetime. Последующая реализация должна читать актуальный контракт, а не исторический пример. Diff/локальные ссылки проверяются без build/tests; R28/R29 и Windows gates этим аудитом не закрываются.

## Live report 2026-08-28 — duplicate call ID и streaming

Пользователь указывает последний HEAD (локально `c1628ce`); build SHA на фото не виден. На фото событие `#32 agent.response.rejected`, stage `model.attempt.rejected`, `FailureKind=invalid_model_response`, `Attempt=0`: `Tool call id already exists in the accepted run: call_html_upsert_dashboard. Use a new id.`

- R26 / ModelProtocol Phase 2C3C, ID ownership switch Phase 3B2: источник текста — `ConversationResponseParser`, проверка ID по accepted-run context до dispatch. Этот attempt не выполняет calls и не доказывает ни отказ HTML executor, ни конечный провал run: ModelProtocol допускает bounded repair. Нужны предыдущий accepted call/result с тем же ID и последующие rejected/accepted/terminal events, чтобы отличить повтор ID моделью от ошибки scope/confirmation bookkeeping; защиту не ослаблять и ID скрыто не переписывать. Повтор ID не доказывает повтор той же операции или arguments.
- R28: streaming не отключён фазой. `LlmClient` использует `StreamResponses`; `ConversationStreamProgressProjector` извлекает только root `message` и отдельный provider reasoning, `AssistantWebBridge` передаёт deltas в `app-core.js`. HTML внутри tool arguments не показывается как поток ответа, пустой/поздний `message` может выглядеть как пауза. Это возможное объяснение, не установленная причина жалобы. Для локализации нужны настройка streaming, provider/model, request/response и stream chunk events с таймингами из того же run; отдельно проверить доставку в реальном WebView на Windows x64 + Office + VS 2022.

Уточнение пользователя: после repair получен неполный HTML и показана страница без части кода. В текущем `ModelProtocolClient` repair заново генерирует ответ из accepted history + ошибки, без отклонённого payload; `HtmlArtifactToolExecutor.UpsertFile` заменяет всё содержимое, а `ValidateFile` проверяет путь/размер/тип, не полноту HTML/JS. Это объясняет возможную цепочку, но оба payload и результат записи не предоставлены. Проверка Git: до cutover `dbb8ce1` прежний parser проверял уникальность только внутри ответа; с 2C3C действует accepted-run scope. ID задаёт модель, prompt требует уникальности, schema задаёт непустую строку с description, а локальный accepted-ID snapshot не является ограничением генерации. Host-neutral duplicate tests доказывают отказ, не отсутствие повторов у live provider. Повторное использование ID моделью могло существовать раньше и стать видимым после усиления проверки. Первичная диагностика — ModelProtocol/Phase 2 follow-up; при неверном scope — runtime owner/3B2. Автоматического устранения этого сценария будущим kernel switch план не гарантирует; перед runtime fix нужен отдельный согласованный scope.

R29 зафиксирован отдельным багом, а не допустимым поведением незавершённой фазы. Полный `*.events.jsonl`/trajectory export не предоставлен: точная пара исходного/repaired HTML и корректность accepted-ID scope пока не проверены. Runtime fix, harness, live-provider и Office validation здесь не выполнялись.

## R29 — Runtime должен владеть идентификаторами вызовов

**Текущий статус:** исправлен host-neutral отдельным v4 correction после 3B2 и architecture audit; [точная проверка и ограничения](R29_RUNTIME_CALL_IDS.md). R28 не исправлен. Windows/controller/live-provider gates остаются открытыми; original/repaired HTML пользователя не предоставлены.

- **Исходный дефект:** v3 prompt/schema/parser требовали model-generated ID и run-wide уникальности. ID — служебная связь call → pending confirmation → execution/result → history/trajectory, а не содержимое пользовательской задачи. Обязательность ID в runtime не обосновывает обязанность модели его придумывать. Ранее введённая проверка защищает целостность учёта, но оставляет ненужную причину отказа и полного repair; это ошибка распределения ответственности в контракте.
- **Происхождение и границы:** model-generated IDs существовали до v3; Phase 2C3C (`dbb8ce1`) расширила проверку с одного ответа до accepted run. Новый kernel 3B1 также принимает calls с ID и проверяет повторы, поэтому один switch 3B2 баг не устраняет. Повтор ID не доказывает повтор операции; выдача разных IDs сама по себе также не предотвращает повторение одного действия. Automatic tool retry/deduplication сюда не добавлять.
- **Реализованное исправление:** модель возвращает `message` и calls с `name`/`arguments`; единственный runtime owner назначает уникальный ID каждому принятому вызову после валидации, до записи accepted call, confirmation и dispatch. ID сохраняется один раз и используется для результатов, history/native tool-role transport и continuation; replay не генерирует его заново. Назначать ID только готовому результату слишком поздно. Оригинальные `message`/`name`/`arguments` не требуют новой генерации из-за ID.
- **Scope исправления:** отдельный protocol correction Phase 2 со start/confirmation и kernel consumers Phase 3. Model-facing version, §7.1 master plan, canonical protocol/ADR, schema/prompts/parser и accepted history writers/readers переключены атомарно на v4. Model-ID path удалён без скрытого переименования или dual-contract fallback; несовместимая история — explicit skip/reset без удаления данных. Новая domain/UI feature и реализация Phase 4 сюда не входят.
- **Критерии закрытия:** модель не генерирует ID; последовательные calls и batch получают разные runtime IDs; один call сохраняет ID через confirmation, compaction/replay и смену runtime RunId; results во всех поддерживаемых roles однозначно связаны с calls. Длинный HTML проходит из валидного ответа в tool посимвольно без ID-triggered repair, rejected attempts ничего не исполняют, replay не повторяет effects. Проверки безопасности/schema/arguments сохраняются. Production controller/WebView — отдельная Windows x64 + Office + VS 2022 validation; качество HTML как программы не гарантируется одной этой правкой.

Новые дефекты вне текущей фазы фиксировать здесь или в [BACKLOG.md](BACKLOG.md),
не исправлять попутно. Исключение P0 требует отдельного явно ограниченного изменения.

## Baseline checks isolated during continuity work — 2026-10-01

Clean source archive of HEAD `dfa154e28e0d90c4195f01fdab6dd4243e70b13c`
reproduces three failures also seen with the continuity changes:
`PromptResourcesReadPublishedTemplates` (50 mutable settings loads, expected 0),
`PromptResourcesFailClosed` (8 loads, expected 0), and
`ResourceSchemaMappingDerivedPublication` (old copied workspace restore fails).
Owner: catalog/settings read boundary and resource fork/restore. Investigate as
separate slices; do not waive these tests or label the full harness green. No user
history or copied artifact was deleted to make the checks pass.
