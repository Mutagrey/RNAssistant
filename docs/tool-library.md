# Tool Library and capability truth

Trusted `ToolPolicy.EvidenceEligibility` is `None` or `Eligible`; it participates in
snapshot hashes, copies and policy drift checks. Capability search/schema reads,
skill loading and resource find/search/outline publish no E reference. Other reads
and mutations default to Eligible. Only a terminal runtime commit may issue E;
a tool/model never supplies it. See [reference revision 3](protocols/CONVERSATION_RESPONSE_V8.md#typed-references-revision-3).

Текущий контракт Core, patch и имён: [Text mutations](text-mutations.md).
Текущий [подсчёт встроенного каталога](audits/capability-inventory-2026-10-29.md):
78 уникальных публичных ID вместо 91; это объединение host/settings-каталогов,
включая условные и manual-only записи. Начальный профиль модели — 10 доступных
Core tools. Private owner commands и динамические пользовательские tools в эти
78 не входят. Общая грамматика patch принадлежит его schema; skills дополняют её
предметными правилами без копирования всего контракта.

Conversation permission composition is owned by
[ConversationRunPolicy](conversation-protocol.md#unified-agent-audit-and-cutover-2026-10-13).
The source-owned `ToolPolicy.ExecutesCode` flag marks read-producing executors
(`js.run`, JS packages, CLI `web.verify`); a read result does not exempt code
execution from approval. An accepted Agent/Ask run may grant automatic approval
through the user's captured `AutoConfirmToolActions` setting; the tool's effect,
verification, code classification and confirmation requirement stay unchanged.
ToolRuntime applies Ask/Deny and Plan independently of
catalog visibility, before preparation and dispatch. Only exact built-in handlers
for Plan-only `core.write/patch` and Task List receive the planning exception.
Mixed Plan/file/Office packages are rejected; Plan delete/restore retain Agent policy.

## Request exposure through skills

Core `ToolDescriptor` and published `ToolCatalogEntry` carry `Exposure` (`Direct`
or `Skill`) and `ContractVersion`. Both participate in the exact registration
fingerprint with schema, policy and binding. Core/direct discovery remains usable
without a skill. Specialized HTML workspace and skill/tool package authoring tools,
and CLI browser verification, are exposed through declared skill dependencies.

`core.skills.load` is a direct bootstrap call in Agent and Plan. The loader
captures exact compatible dependencies; they become callable on the next request.
`core.tools.read` inspects direct schemas. For `Skill` exposure it returns
successful bounded selection guidance with related skill ids, not a cached
refusal. The selected request supplies the exact schema; discovery neither
duplicates it into history nor admits the tool. An already selected tool can be
called directly without another schema read.
`ConversationResponseParser` directs a guessed specialized call
to the loader. `ToolRuntime` checks the originating receipt before preparation and
dispatch; schema presence alone grants no mutation permission. Manual Library and
Tools UI actions keep their existing explicit authoring/confirmation guards.
See [manifest, selection and Arsenal](skills.md#manifest-and-task-selection).

Model-facing capability search/read results omit the root `loaded` field: a
complete source read is not skill selection or callable admission. `complete`,
`truncated`, source text and selection guidance retain their meaning. Only an
exact offered tool can receive `schemaInRequest=true`. Stale and oversized
model-context results likewise report their error and incomplete content without
a selection flag. Original durable read metadata, including `loaded`, stays
available to its existing runtime consumers; no admission rule changes.

### Общий Core и расширение каталога — 2026-10-28

Office, проект и плагин используют `CallableToolProfile.Core()` (`resource-core-v1`):
`core.find`, `core.read`, `core.search`, `core.write`, `core.patch`, `core.replace`,
`core.ask`, `core.tools.search`, `core.tools.read`, `core.skills.load`.
Mode, read-only и доступность owner фильтруют этот список: отсутствующая возможность
не появляется только из-за префикса `core.`. Встроенные host-specific tools не
подмешиваются в начальную схему. Остальные descriptors подключаются через существующий
exact admission `core.tools.read` или зависимости выбранного skill.

Контракт текста, patch, upsert, recovery и имён задаёт
[Text mutations](text-mutations.md). Профиль сохраняет exact policy/binding и порядок
каталога. Единственная schema `core.patch` — `targets[].edits[]` с `old/new`
и необязательными `anchors_before/anchors_after`; contract version 3, binding
`.resource.v5`. Private owner commands не публикуются второй модельной schema.
Результат использует прежний `rnassistant.patch` и `preparation/execution`,
per-target statuses и bounded diagnostic `edit/code/nextAction/candidates`;
Unknown требует `verify_effect`, без автоматического повтора.
Смена профиля явно сбрасывает несовместимые admissions; старые вызовы
не переименовываются и не исполняются через aliases. Explicit AllDirect остаётся
только композиционным выбором для диагностики, не production default.

### Shared discovery and admission — 2026-10-26

Core `CapabilityCatalogService`/`CapabilityToolCatalog` now own descriptor construction,
metadata search and exact schema reads for both consumers. `CallableToolSet` owns
staging, candidate membership and replay checks; `ToolPackAdmissionJournal` persists
accepted/rejected extensions in the existing chat event stream. Office retains its
profile factory and ResourceGateway skill source reader. No second registry/journal,
alias path or migration adapter remains.

Workspace Agent/Plan registers `core.tools.search/read` and uses the shared Core profile.
Plugin and bound-Office contributions remain discoverable and use exact admission.
The composition-owned profile is not a model argument or a permission override.
Chat and unavailable/read-only filesystem tools retain their mode/catalog filters.

A completed schema result stages membership only after terminal persistence. At the
next request, the existing skill compiler and ModelRequestPreparation test the full
candidate (instructions, history, schema, repair/continuation reserves). The journal
append precedes publication. Successful reads of already callable tools create no
extension; Skill-only tools still require a selected compatible package. A refused
candidate does not partially publish or gain authority from retained schema history.
Reopening validates exact descriptor/policy/binding pins; drift resets optional
membership explicitly. Prior-turn optional schemas can yield to the current budget
with an explicit notice; no effect is replayed. Current-run effects remain kernel-owned.

Schema receipts and runtime metadata removal now share Core helpers: exact callable
schemas are not duplicated in model history, and `loaded`/revision fields do not
claim selection authority. Workspace capability reads of skill IDs return routing
metadata to `core.read` (`type=skill`, semantic target) and
`core.skills.load`; they do not read source, select the skill or execute code.
Office retains its existing exact skill/reference source reads through ResourceGateway.

The [paired live pilot](audits/tool-profile-comparison.md) records the earlier broad/compact
comparison. This cutover deliberately selects the compact Core requested by the user;
the historical model metrics do not qualify the new contract. Scripted production-loop
checks cover discovery, admission, patch and copy; live-model evaluation remains separate.

The Office `core.read` selector schema has one Core owner. The direct
Office descriptor uses it unchanged; the workspace/CLI Office binding adds only
its required `type=office` discriminator to each closed alternative. A section,
PDF page/range, tree, table, procedure or paired text window therefore has the
same admission shape in both routes. Provider availability and exact target
binding remain separate runtime checks.

Model-facing argument schemas in `json_object` use `ToolSchemaSupport.ForPrompt`
in both the capability catalog and workspace prompt. Complete `anyOf` alternatives
must not retain the internal closed parent that forbids their branch properties.
The original descriptor still owns runtime validation; strict output keeps its
separate `ForStructuredOutput` projection. Prompt normalization grants no authority.

Once a direct tool is in the frozen callable surface, its successful schema-read
result is projected as a compact receipt with `schemaInRequest=true`. The compiler
passes that surface separately from the discoverable catalog; the projection must
match both the complete descriptor and its revision before omitting the duplicate
descriptor. The exact schema remains in the request instructions or strict response
schema, including fallback to `json_object`. Full read evidence stays in the durable
transcript/CAS. Admission still consumes that complete result; replay still requires
durable tool-pack extension decisions. The compact receipt cannot grant authority.
Missing admission/surface, incomplete reads, mismatches and
stale/error diagnostics do not become successful compact receipts.

The `skills.excel.analysis.reporting` package supplies inspection, sheet creation, range
writes, tables, formatting and both workbook/chat charts. Standalone operations do
not require selecting that skill. The shared profile is `resource-core-v1`;
incompatible older direct admissions explicitly reset to the current core without
deleting history or replaying calls. Unchanged admissions under the current profile
retain the existing exact validation and budget checks across turns and restart.

## Status and boundary

This is the current contract for tool visibility and authoring. It does not
change the current `ToolRuntime`, `ToolPackSnapshot`, confirmation or execution
policy, and it does not add dynamic authoring to the `16.1.0` release scope.

Migration of built-in Excel/Word/PowerPoint/Outlook execution is the separate 11T
contour. Its parity-first semantic-family switches and 11T10 cleanup are complete
host-neutral: generic host dispatch/catalog and definition/result projections are
deleted. Windows `DocumentSession` qualification remains mandatory; the Library
projects the exact current endpoint catalog and cannot hide or satisfy that gate.

A tool is an executable capability, not a chat artifact. Built-in tools belong to
the application build, custom tools are global or host-scoped packages, and
document-local VBA tools belong to the exact live document. An uploaded manifest,
source file or package remains an untrusted immutable artifact until an explicit
validated and confirmed import creates a tool package revision.

The UI may place Artifacts, Tools and Skills under one Library shell, but they keep
separate owners, stores, version rules and model transports:

- artifacts use revision-pinned `ResourceRef` values and `core.find/read/search`;
- tools use the selected endpoint's exact capability catalog and
  `core.tools.search/read` for model discovery/schema admission; source inspection
  uses `core.find/read` without admission;
- skills use the same capability reader for instruction bodies, but never become
  executable tools;
- authoring remains separate from discovery and cannot grant authority to an
  already accepted run.

## Current implementation

Library строит один уровень групп по первому сегменту:
`html.data.write` → HTML → `html.data.write`. Tools и skills имеют отдельные корни;
у skills префикс `skills.` служит типом, а не лишней группой. Core идёт первым.
Статусы Core / Подключён / По запросу / Недоступен берутся из текущего каталога
и уже существующего Inspector Arsenal; название группы не выдаёт authority.

Plan, prompt templates and custom skill reference text use the shared source
tools and existing domain owners; see [Text mutations](text-mutations.md).
Retired Plan/prompt/reference-write and HTML refresh IDs fail accepted-history
validation explicitly. Existing history is preserved, without aliases or fallback.

Office publishes `core.find/read/search + resources.outline` as direct, read-only
semantic resource tools. Search accepts one target or a scoped VBA/HTML collection with literal/regex matching;
outline pages indexed document text parts. Workspace supplies the same search ID for
relative files and recursive/glob scopes; a bound Office document adds VBA/Office targets. `resources_read.partNumber` is a bounded
source/text selector, not a raw URI, cursor or mutation guard. The exact
provider/index limits and incomplete-source semantics are owned by
[Resource Fabric](resource-fabric.md#large-document-text-navigation--2026-10-14).

Search is a direct read capability with exact binding revision `resources.search.v3`
in Office. It is not marked independent-batch read because continuation state belongs
to its handler. Workspace registers the exact same Core schema owner; descriptor,
policy and binding changes participate in existing snapshot compatibility checks.
Search belongs to Core; outline remains an optional schema.
Search details use existing typed result blocks and activity summaries; Inspector still
shows the saved request/result, not a second reconstructed log. Five VBA captions now
use the active catalog constants and moduleName rather than retired tool IDs.

### Excel row and column deletion

Owner: `ExcelDeleteRowsColumnsService` in Office; the bound Excel backend owns COM.
`excel.rows.columns.delete` removes consecutive `rows` or `columns` using explicit
`sheet`, `scope`, one-based `start` and positive `count`. `scope=table` also requires
the exact `table` name; indexes are relative to that table, with row indexes
excluding headers and totals. `scope=worksheet` deletes whole worksheet rows or
columns and forbids a table argument. A table must retain at least one column;
all data rows may be removed. There is no implicit worksheet fallback.

For the extra seventh column in `filesTB`, use table scope, `axis=columns`,
`start=7`, `count=1`. The adapter uses Excel's
[ListColumn.Delete](https://learn.microsoft.com/en-us/office/vba/api/excel.listcolumn.delete),
which does not remove the worksheet column. `excel.range.clear` only clears content
or formatting; clearing table header values is refused before dispatch with
`excel_clear_table_header` and guidance to the structural deletion tool.

Preparation captures the exact target before confirmation; execution rechecks that
snapshot under the document gate. Worksheet guards also retain the used-range
extent to reject newly added data outside the prepared observation window.
The operation observes at most 100,000 cells and verifies the remaining table
dimensions/names or worksheet cell shifts, constant values and formula positions.
Excel owns formula-reference rewriting and recalculation; this check does not
certify formula semantics, formatting, charts or dependent objects. Unknown effects
remain unknown and cannot be automatically retried. Real Windows/Excel qualification
is open, including empty tables, totals, formulas, protection and partial COM failure.

### Workspace CLI file tools (development)

The independent CLI advertises `core.find/read/search`, `core.write/patch/replace`
and optional `resources.copy/move/delete/restore` for writable workspaces.
CLI `core.find` accepts an optional `directory` and filename `query`.
By default it returns at most 200 semantic targets from a single directory, with
explicit completeness/unavailable-scope information. Scanned-entry counts remain
diagnostic metadata. An incomplete result requires narrowing and does not prove
absence. The scan stops after 5000 entries; there is no opaque model cursor. Optional recursive/includeGlobs/excludeGlobs discover nested files;
core.search supplies bounded content search and semantic continuation.
`resources.move` requires a current source observation and confirmation under the accepted policy;
it preserves identity, verifies the target bytes and source absence, then publishes
a new revision. The target parent must exist and no existing file or reserved
identity may be overwritten. An interrupted move uses its durable two-path intent
for explicit reconciliation; no move is automatically replayed.
When an isolated Chromium executable is available, the CLI also advertises
`web.verify` as a read-only development tool. It loads a bounded immutable
snapshot assembled through `WorkspaceFileService`, reports dependency/load/console/
runtime errors and attaches exact file evidence to its durable tool result. It
never writes the source. Without a browser the tool is absent from the catalog;
the explicit CLI `verify` command returns `not-run`.
After document load it observes browser events for 1.5 seconds; later timers and
long-running interactions require a functional check. CLI `--web-checks` supplies
a frozen typed contract (up to 32 steps) before the run.
`web.verify` executes these checks on the exact snapshot and reports each observed
result; its model arguments remain only `entryPath`. The model cannot change the
checks, select another entry or substitute a smoke result for functional acceptance.
Steps require a unique visible HTML/SVG target (an empty table section may have
zero height); clicks use browser pointer events and reject disabled/obscured targets.
`TextEquals`, `TextContains` and `NumberEquals` assert bounded text. `SelectValue`
and `InputValue` set an enabled control and dispatch its change/input events.
`UploadCsv` uses a private fixture file, `TableEquals` checks ordered cell values,
`BarChartEquals` checks positive proportional SVG bar lengths in table order, and
`DownloadCsvEquals` checks a real completed export. Expected CSV strings are data,
never paths or executable code. The first failure leaves later steps `NotRun`.
Checks and results are retained with the snapshot in the verification resource.
Both passed and failed browser evaluations carry their captured source evidence.
After an edit, the compiler marks the old failure as historical while retaining
its diagnostic and assertion details; a new verification is needed for the current
files. This evidence is not a successful file read or write authorization.
When a JavaScript exception occurs, the result may include an advisory hint for
literal `getElementById` calls whose IDs are absent from captured HTML. Dynamic
DOM construction can make such a hint inapplicable; the browser error remains the
verification fact.
For a failed published browser snapshot, typed `ChangeRequired` recovery retains
up to 32 exact checked dependency revisions in runtime metadata. An identical
`web.verify` becomes eligible after a later verified whole-resource change on a
checked dependency, with matching before/after identity and a new exact revision.
Reading source, changing an unrelated file or writing identical bytes cannot
satisfy that condition. Unknown effects never unlock a retry. The model receives
the policy and semantic entry target, without dependency URIs/revisions. The next
verification is still an explicit model call and captures a new immutable snapshot.
This recovery applies to failed snapshots with complete source evidence; `NotRun`
and failures before snapshot publication do not receive it.
`resources.delete` requires a current accepted observation of the UTF-8 file and
moves it to workspace-local managed trash after explicit confirmation and journal preparation. It publishes
success only after the original path is absent and trash bytes match the retained
preimage. `resources.restore` explicitly restores the latest managed deletion to an
absent original path and publishes a new revision. A possible effect after dispatch
returns `unknown` and requires `recover` before another mutation; no automatic
retry or permanent-delete fallback exists. Workspace and Office mutation routing share the public text contract and retain their domain owners.

### Read-only JavaScript tools

Agent JavaScript is experimental and disabled by default. The Settings → Agent →
Execution checkbox `JavaScript инструменты агента` enables it for separate testing.
While disabled, `js.run` and saved JS packages are absent from the visible
tool catalog, JS creation fields and guidance are omitted from `tools.upsert`,
and direct execution or authoring fails closed. Its VBA-only model schema omits
`host` and `executor`: the manifest owns the package host and VBA is the only
available executor. When enabled, the schema exposes `executor`, `host`,
`parameters` and `code` for JS package authoring. Saved JS packages remain on disk.
This switch does not affect JavaScript inside an HTML workspace/WebView page.
There is no dedicated built-in JS skill; the existing Tool Authoring skill describes
VBA packages only, so disabling the switch leaves no JS skill instructions active.

Source text has no separate character or request-byte ceiling; the worker's execution
memory, deadline, statement and result budgets still apply.
`js.run` runs one async JavaScript function body in a separate local .NET Framework
worker. It accepts named `{name,target,view,path?}` resource bindings and exposes
`RN.resources.names()`, `await RN.resources.open(name)`, bounded `read()` and
`for await (const batch of handle.stream())`. The Office resource Gateway resolves
semantic targets and pins exact view revisions before the worker starts; each
subsequent batch uses the existing Resource Data Plane cursor and gate. Scripts
receive no Office, CLR, filesystem, network or WebView bridge object. The return
value must be JSON-serializable and at most 1 MiB. Execution is read-only, limited
to 10 seconds in Jint, with a 12-second worker deadline and process termination on
cancellation. A closed or incomplete resource read fails explicitly.

The worker does not run the page's `app.js` or inherit its named HTML bindings.
For an HTML data member, read its binding metadata to obtain `sourceTarget` and
pass that source explicitly. Worker success alone is not WebView/render evidence.

Custom `executor=js` tools use the same worker and ToolPack snapshot as a one-off
call. Their strict argument schema must require a `resources` array of named
semantic bindings; callers select targets on every invocation. Tool Library stores
the JS source in the package metadata and applies runtime-owned read-only policy.
VBA component storage and execution remain separate.

Source-owned host and controller catalogs are captured in one immutable execution
snapshot. Mutable `ToolCatalogEntry` values are catalog/package projections only;
their exact policy/binding is required at capture and missing authority fails closed.
Catalog owners supply bindings directly, without a shared tool-ID resolver. The
native runtime checks the pinned handler ID and entry point against the selected
handler before registration; scope and host remain pinned by the snapshot.
Agent receives a finite core plus a compact exact-id catalog; optional
schemas are admitted atomically at model-step boundaries. Chat cannot execute tools,
Plan is restricted to its own read-only/Plan-local set, and Agent execution remains
subject to source-owned policy and confirmation.

The prompt discovery index keeps exact IDs, bounded summaries and selection routes.
Tool display names, policy flags and skill size/reference counters stay out of that
index. Explicit capability search retains inspection metadata; the full admitted
descriptor and runtime remain the authority for execution policy.

Existing custom VBA packages are stored under `%AppData%/RNAssistant/tools` and the
current Library editor can validate, save, clone, test and delete them. The current
flat package store is not immutable revision history. Model authoring uses separate
`tools.upsert` and `tools.delete` operations; source inspection uses
the published resources described below. These Agent-only writes use native typed
handlers; R61/11O4 removes separate model validation and limits upsert to semantic
package source/documentation. The manifest
owns callable metadata; runtime validates the complete effective definition and
assigns conservative authority before write. Confirmed upsert/delete bind
accepted arguments to the current effective definition, reject stale state, mark the
storage boundary and require read-back verification. They cannot alter the immutable
ToolPack of the accepted run. Since 11J2, execution and Library install/remove/status
capture one complete `ToolPackageSource` v1. Its deterministic content revision is
separate from the manifest package version and is pinned with the native handler in
the accepted run. Library actions return typed result v1 with status, source revision,
dispatch and effect evidence; PascalCase/legacy result fallbacks are unsupported.
A failed authoring-directory removal cannot be reported as a verified delete, even
if catalog read-back no longer sees the package.
Cleanup of a replaced package or obsolete VBA source directory also fails the save
instead of silently leaving stale files.
Tool entries can also carry optional typed `display` metadata, separately from the
VBA manifest and execution descriptor: `action`, optional `runningAction`, an
`operation` icon category and optional ordered `targetArguments` (up to eight exact
scalar parameter names). For example, `display: {"action":"Пересчёт отчёта",
"runningAction":"Пересчитываю отчёт","operation":"Command",
"targetArguments":["report"]}` works for any custom id whose schema declares the
scalar `report` argument. Labels are single-line text up to 200 characters, never
Markdown/HTML or runtime status. An explicit empty selector list disables target
argument selection. Omitting `display` on update preserves it; supplying the object
replaces its hints, with missing `runningAction` defaulting to `action`.
The model authoring `tools.upsert` schema and typed Library mutations both
support it. Library edits preserve it even without a dedicated form control.

`Core.Tools.ToolDisplayCatalog` owns the shipped localized defaults and bounded
semantic target selection for Office and workspace; unknown entries use their
catalog name and typed policy for a neutral default. `ConversationKernelAdapter`
captures immutable display metadata alongside the accepted local invocation;
`AgentTranscript` and workspace activity projection use that shared catalog and
`ToolResultSummaryProjection`; the former Office-only target/summary paths are removed.
Events, confirmation replacement and chat forks retain that captured
value. The web renderer consumes labels and icon category, without tool-id/English
verb dictionaries or per-custom-tool renderers. Display-only updates participate in
Library revision/read-back checks, but do not alter execution fingerprints, model
`ToolDescriptor` or the serialized invocation. Existing source-owned Office target
formatters now live in the shared Core catalog for built-ins without explicit target selectors.
Collapsed rows show action + semantic target + typed outcome; running attempts already
identify the target, and capability results distinguish skills/references/tool schemas.
Result/effect
presentation remains governed by [Conversation protocol](conversation-protocol.md#effect-mapping-and-ui-projection).
The model receives the shared [semantic result projection](conversation-protocol.md#компактные-модельные-результаты--2026-10-07),
not the raw diagnostic object. New metadata in a migrated family must be explicitly
admitted there. Keep arbitrary business payloads intact. Typed recovery belongs to
the runtime record and is rendered once at request preparation; do not insert a
second recovery object into domain result JSON.


Model and Library upsert paths both carry `components` as one native `JArray` of
component `JObject` values into `ToolAuthoringService`; they never serialize and
reparse that argument. A quoted/stringified array is an `invalid_arguments` failure
before preparation or storage. For model authoring, this outer ordered package is
the sole component-list input: runtime writes the manifest's component-name array
from that order before validation. The model does not duplicate the same list in a
second, differently shaped manifest field.
The supplied `components` array replaces the complete package; it is not a merge
of selected components. VBA `name`, `description` and `parameters` belong only in
the entry manifest. The model schema omits duplicate root metadata fields, and
the Library's host/description/schema controls are read-only for VBA with a pointer
to the manifest. JS-specific authoring fields require `executor=js` and cannot be
mixed with VBA components. Shared documentation-only updates preserve the stored
executor and source.
Every supplied VBA component passes the same source-safety/duplicate-procedure
validation as live module writes before confirmation or storage. This static
check does not certify compilation or macro execution.
Model-facing upsert has no create/update existence mode: it resolves current
existence atomically and creates or updates accordingly. Internal revision-guarded
Library mutations retain explicit create/update intent. Manifest schema errors name
the authored `parameters` field rather than the stored `argumentSchemaJson` field.

Tool, Skill and Prompt mutation guards use `ToolArgumentReader` as the single owner
for recursive ordinal JSON-object canonicalization and SHA256. Each domain still
builds and validates its own accepted argument/target payload, stale-state contract,
write boundary and read-back; this is representation reuse, not a shared authoring
service or validation policy.
Since 11T10 the existing Tools editor also uses lowercase
`rnassistant.toolLibrary` v1 and explicit revision-guarded create/update/rename/
delete mutation DTOs through the same `ToolAuthoringService` as model authoring.
Controller-owned catalog reconciliation, `StoragePath` identity, generic execution
and unversioned response fallback are absent. This does not make the flat store
immutable history.

### Model source reads

`core.find` with `scope=catalogs` and a tool id discovers a semantic
`tool source: <id>` target for the current host's custom and source-owned builtin
tools, including Common definitions. Discovery carries metadata only. Passing the
returned target to `core.read` reads the existing exact catalog
`/source` child, also used by the Library editor. Its JSON text contains
`argumentSchemaJson`, `code`, `readme` and `components`; generic whole-read bounds
and exact internal continuation rules apply. Generated human docs are not listed
or injected into these source bodies. Document-local VBA remains in its existing
VBA resource provider, not a second catalog authority.

The read records CAS-backed resource evidence with an exact catalog-root dependency.
A later publication supersedes that evidence through the common reducer; the
historical snapshot stays readable. Unpublished authoring-file drift cannot replace
the committed body, and missing/corrupt snapshots fail closed. Reading a schema or
implementation never admits a callable tool or grants mutation authority.

`common.tools_definition_read`, its direct file reader/native binding and catalog
entry are removed, without alias or fallback. Stored calls to that old contract
are explicitly rejected by model-history validation; user history is not deleted.
Upsert/delete preparation and confirmed read-back still belong to the existing
authoring owner. Prompt inspection also uses
[published field resources](conversation-protocol.md#published-prompt-inspection);
other domain reads and the Prompts UI transport remain open.

### Library source reads

Init, chat updates, catalog refresh and mutation/install/remove responses carry
only source SHA-256/byte extent in `ToolLibraryItemDto.source`, never schema, code,
README or component bodies. The selected editor calls typed `readToolSource` with
explicit chat, tool id and expected Library revision. `ToolEditorResourceService`
reserves a shared download slot before catalog hydration/live reads; its response
contains exact source refs and a bounded download capability. One inert UTF-8
`ToolSourceBodyDto` (schema, code, README, components) is delivered through the
existing data plane in bounded chunks, with complete-byte integrity verification.
Source size is limited by the runtime's byte-array/string representation and
available memory; there is no separate 16 MiB source ceiling.

Custom source comes from the committed `rna://catalog/tools` publication; built-in
source registrations are published once through the same catalog owner under
`builtin-tools-<host>`. Both expose exact `/source` children with root-publication
dependencies, not separate child heads or another activation store. Document-local
source instead proves each cached manifest/component against an exact live VBA
Gateway snapshot under the existing document access owner. Missing, partial or
changed components fail closed and invalidate document discovery for refresh.
Its JSON download is a disposable editor projection of those refs, not a new
document catalog authority. Reads create no model observations or execution rights.

No input becomes editable until the complete source is verified. Failed reads
cannot become empty drafts; unchanged unloaded items are not serialized as writes.
Built-in tools remain read-only in the Library; their editor offers creation of a
separate custom tool with its own id and source.
Clean source cache is selection-bounded; hydration does not mark the package dirty.
Local source edits survive refresh and late save responses. A changed published
source with a dirty draft blocks saving rather than silently rebasing it. Chat,
selection, Library section and bridge/page closure cancel reads and close late
leases. Existing revision-guarded mutation uploads remain the only write path.

The separate generated built-in human-documentation view uses the same Gateway/
download owner as described [below](#human-documentation-without-model-context-cost).
Model/domain definition reads are switched through the same resource owner.
VBA line-ending-only drift now creates a new exact source revision while preserving
normalized mutation guards and historical bytes; see
[Resource Fabric](resource-fabric.md).
Real Office/WebView2 qualification remains open.

### Library VBA drafts

The manual Add action creates a complete commented manifest and an argument-free
`Run` entry function for Excel, Word or PowerPoint. The starter function raises an
explicit unfinished-implementation error; saving a draft does not implement or run
a task. Draft ids are unique within the current Library. Clone rewrites the copied
manifest id and preserves the original package and implementation. Missing or
malformed entry manifests stop cloning before adding a draft. Generated component
names respect the 31-character VBE limit. These drafts use the existing validated,
revision-guarded mutation upload and read-back path.

### Library mutation uploads

The existing Resource Data Plane owns complete-byte hash/MIME/size checks and
strict UTF-8 decoding for editor uploads. Prompt, Tool and Skill JSON batches
also share strict typed deserialization with their existing depth limits.
Editor owners retain domain validation, exact revision guards and mutation
publication; consumption remains single-use on success, failure and cancellation.

Both Library Save and the save-before-VBA-install step use one bounded upload
consumer, `ToolEditorResourceService`, over the existing resource data plane.
`beginToolMutationUpload` reserves a chat-bound `tool-editor` capability; the
`saveTools` control contains only `chatId`, `uploadLeaseId` and the complete
upload's SHA-256. Code, README, schema and native typed component values travel in
one UTF-8 `ToolLibraryMutationBatch` body, not an inline bridge request. The batch
retains the 256-mutation contract and shares upload concurrency/expiry; the
former 16 MiB body ceiling is removed and input stages to a temporary file.
Complete bytes, hash, typed shape and batch identities/guards are checked before
dispatch; the capability is consumed even on validation failure. Upload alone is
transient input, not a package, catalog publication or model observation.

The same `ToolAuthoringService` still owns package format, validation,
revision recheck, read-back and the existing catalog commit barrier. Mutations
remain sequential and stop at the first non-OK member; a verified committed prefix
is retained, not rolled back or replayed as an atomic multi-package transaction.
Document VBA catalog refresh still uses its existing document access owner.

The UI has one pending save/install/remove operation and retains that slot until
cancelled work exits. Chat/library replacement, bridge/page close and late
responses cannot apply stale state or continue into installation. An unconfirmed
save never retries or triggers install. Successful members acknowledge only their
unchanged submitted drafts; failed members and later text edits remain visible.
If the selected definition changes while saving, installation requires another
explicit save. Installation-status refresh also preserves unsaved authoring text.
Direct package install/remove authorization and execution remain unchanged.

The inline save request and separate pre-install save transport are removed;
catalog and outgoing library projections now use the source metadata above.
Tool package README sidecars are written with a separate UTF-8 BOM so a literal
leading `U+FEFF` remains part of the text on read-back. Reads still accept existing
BOM-prefixed files and do not rewrite them. An older BOM-less file beginning with
literal `U+FEFF` has the same bytes as a BOM-prefixed file, so its original intent
cannot be recovered automatically.

## Mandatory all-tool contract audit (R61)

R61 below records the completed family cutovers, not proof of agent continuity.
The [2026-10-01 runtime/ergonomics audit](#tool-ergonomics-audit--2026-10-01)
rechecks the current surface and its consumers; its open context defects are not
closed by the historical descriptor inventory.

The post-cutover catalog is complete host-neutral but is not yet Windows-qualified
as the final candidate. R61/11O1
corrected Resources + Capabilities host-neutral, 11O2 corrected planning, and 11O3
corrected HTML authoring/data binding. HTML inspection and active selection are now
internal; file/data writes are separate, patch/delete are semantic, and bind reuses
accepted read evidence without model-owned source arguments. Model context/results/
history omit opaque state while durable records retain it. The later whole-read
correction removes public resource continuation; the bound VBA project target is
available directly in runtime context and unfiltered find keeps it first. 11O4
corrected Prompt/Tool/Skill authoring: one-key prompt save, three exact tool-package
operations, separate skill-core/reference mutations, internal validation and
runtime-owned conservative authority. 11O5 corrected VBA/macro: write and rename
are separate, patch operation and raw backup identity are internal, and restore
selects a readable target or latest-for-module. 11O6 minimized the mode/host core,
and 11O7 completed UI-only built-in documentation, schema-aware Library Test and
responsive Implementation/Test containment. Final live-provider, WQ-PACK and real
Windows WebView2/Office evidence remains open.

The concrete failure attribution, state-ownership decisions, merge/split rules
and required model evals are recorded in
[R61 tool contract audit](stabilization/R61_TOOL_CONTRACT_AUDIT.md). That audit is
the per-family acceptance record; later-family IDs are not pre-approved by the
completed Resources + Capabilities slice.

The 11O0 source baseline in that audit enumerated all 35 conditional built-in
`common.*` tool IDs and all nine then-existing built-in Common skill IDs; after
11O5 that checkpoint contained 31 Common tool IDs and eight Common skills.
These counts describe that migration checkpoint, not today's complete inventory.
Progressive capability loading is
acknowledged, but does not
exempt optional tools from merge/split/internalization review or justify
plumbing-heavy schemas after admission. Skill bodies are contract consumers and
must switch atomically with the IDs/arguments they teach. R61 must also compare
current core membership with a smaller relevant pack; registry count alone is not
an optimization target.

11O0 established the source-built-in property baseline in the machine-checked
[R61 inventory](stabilization/R61_TOOL_PROPERTY_INVENTORY.tsv): after 11O5 it held
69 unique built-in IDs and 72 effective host variants. The current reviewed
baseline has 80 rows/IDs. Each row records exact descriptor revision, allowed modes,
direct binding and recursive property paths. Plumbing-shaped additions require an
explicit semantic or runtime-owned decision. Dynamic installed custom-package
schemas remain package-revision-owned. 11O4 rejects plumbing-shaped custom
arguments unless their description contains an explicit `Domain identity rationale:`;
field names alone never trigger unsafe automatic stripping.

Every published tool must be reviewed individually, including Office, VBA/macro,
resources, capabilities, questions, Plan, Task List, HTML, prompts, Tool/Skill
authoring and custom-package execution. The review classifies every input as either
semantic intent or runtime-owned state:

- Model-visible arguments contain only choices or content the model must actually
  decide. Sheet names, A1 ranges, slide numbers, component names and requested text
  may remain when they identify a real domain target.
- Call/run/chat/document/endpoint IDs, UUIDs, internal artifact IDs, catalog or
  package revisions, optimistic-concurrency hashes, prepared guards, cursors,
  internal continuation offsets and page tokens are runtime-owned. Explicit row
  selection such as `resources_read.offset/limit` is semantic intent, not a provider
  cursor. After a family cutover runtime-owned fields are absent
  from model-facing arguments, ordinary Tool Result data, `RUNTIME_CONTEXT` and
  replayed model history; they are not typed, copied or even selected by the model
  or by a person testing a tool.
- A canonical revision-pinned `ResourceRef` remains the durable identity for
  replay, provenance and result evidence, but is present only in the accepted typed
  event/execution projection. The runtime maps a bounded semantic selection or a
  previously accepted semantic candidate to that exact reference. The model-facing
  result contains a readable target description and domain data, not `ResourceRef`,
  `rna://` URI, revision, hash or cursor. Ambiguity fails closed and asks for a
  meaningful target choice; it never falls back silently to the latest or active
  resource and never substitutes another opaque `candidateId`.
- Continuation belongs to the read implementation. It may expose a user-facing
  `Next` action or perform a bounded safe continuation, but a cursor/revision pair
  is copied and validated by code. Revision drift restarts only a read under its
  declared policy and never causes an automatic mutation retry.
- Defaults derivable from the bound session, selected endpoint, current immutable
  run snapshot or prior accepted result are injected after model validation. They
  do not appear as nullable ceremony in the model schema.
- Exact public tool/skill ids may remain only when they are stable semantic
  identities the model must choose from the current catalog. The runtime-generated
  `tool_call_id` may remain in the Tool Result wire solely to correlate an accepted
  call with its result; it is never an argument or model-generated state. Descriptor
  revisions, admission guards and snapshot identities remain internal.

The target therefore has two explicit views of one tool contract: a minimal strict
intent schema for the model and Library test form, plus an internal execution
context containing resolved identity, continuation and guard state. The internal
view is never serialized into a model request or model-visible result. Both views
reach the same `ToolRuntime` registration, policy, confirmation and handler; this is
not a second executor, store or permissive adapter. A system-owned field may remain
public only with a per-tool written rationale proving that the caller must choose it
and that no bounded semantic selector can preserve the same safety.

The cutover is atomic per tool family. Resources + Capabilities are switched; until
another slice switches, its existing contract remains authoritative. No alias,
dual schema, guessed value or compatibility fallback is added.

### Structured-output schema projection

Closed object schemas may share `properties`, types, defaults and common
`required` fields while `anyOf` contains only `{ "required": [...] }` fragments.
Every fragment name must be declared by that parent. `ToolSchemaSupport` combines
each fragment with the parent for validation and optional-null normalization;
complete object variants retain their existing branch-owned contracts.
`excel.range.format` uses this form for its seven optional formatting operations.
At least one real option is required; `bold=false` is an option, all-null fields
are not. Its field names, defaults, effects and accepted arguments are unchanged.

`ForPrompt` retains the compact form. `ForStructuredOutput` expands these
fragments before nullable/strict conversion so the provider receives the same
complete variants as before. The measured saving therefore applies to the
`json_object` prompt/descriptor, including strict-response fallback; the strict
`json_schema` response contract itself is unchanged. Descriptor revisions still
change with their exact serialized contents; old admission evidence cannot stand
in for a current descriptor.

`ToolSchemaSupport.ForStructuredOutput` derives the provider's strict response
schema from the same runtime argument contract. Its transformations traverse only
schema nodes under `properties` values, `items` and `anyOf`. Property names are
data: an argument named `description`, `default` or `type` must survive. Likewise,
literal values inside `const`, `enum` and defaults are not nested schemas.
Optional-null conversion, strict object shaping and alternative normalization
must preserve those values and leave the source contract unchanged.
Only schema-node annotations `description` and `default` are removed from the
structured output; prompt descriptions and runtime defaults keep their owners.
Required argument names must remain declared in the closed output object.
The regression uses the actual `core.ask` wire schema: its required
option `description` is both generatable and checked by the runtime. Missing
descriptions still fail; parser, format-repair bounds and execution policy remain
strict. This conversion is shared by Office and CLI, independent of model/result role.

## Human documentation without model-context cost

Every built-in/system tool must have non-empty human documentation in Library.
Documentation is separate from the compact model selection summary and executable
argument schema. It covers purpose, target selection, semantic arguments, defaults,
types/enums/bounds, confirmation/effect semantics, result and common error examples,
limitations and a safe Library test recipe.

Built-in README is absent from the compact Library projection and remains empty
in the source-editor body. Contract tests
prove that generated documentation is absent from
`ConversationPromptComposer.BuildDescription`, model tool descriptors,
`RUNTIME_CONTEXT`, capability search/read, Tool Result and request-token accounting;
it cannot change executable ToolPack registration or model capability-catalog
revision. Full Markdown is generated from source-owned registrations/policies at
the existing builtin catalog publication boundary. Typed publication entries retain
the definition and a documentation CAS `PayloadRef`; the root's committed view
retains these parts. Neither compact/public catalog projections nor execution
registrations acquire generated Markdown or reconstructed runtime policy.

Startup and successful settings save publish the currently configured built-ins.
In particular, changing Agent JavaScript updates both `tools.upsert` source
and documentation in one committed built-in catalog snapshot. Library metadata
and runtime use the same configured descriptor, retaining its skill-only exposure.
Unchanged publication bytes keep the existing head; older exact snapshots remain
readable. Reads never rebuild missing published source from current definitions.

Only opening the selected builtin's Docs tab requests `getToolDocumentation` with
explicit chat, id and expected Library revision. The controller delegates to
`ToolEditorResourceService`; it reserves shared download capacity before reading
`rna://catalog/builtin-tools-<host>/<id>/documentation` through the Gateway.
The child has an exact root-publication dependency, not its own synthetic head.
The service verifies the published definition/body against this runtime's original
registration/policy; re-generation is comparison only, never a missing-CAS fallback.
This path does not discover live VBA or call Office. Historical reads use retained
snapshots and do not activate a catalog or grant model observations.

The typed `rnassistant.toolLibraryDocumentation*` v1 response carries chat/id/revision,
an exact `ResourceRef` and the shared download lease, not inline `markdown`.
Complete UTF-8/SHA-256 verification precedes rendering; the former 2 MiB
documentation source ceiling is removed. One
selected-tool/chat/revision cache replaces the state-wide body/request dictionaries;
selection/catalog/chat/Library-section and bridge/page closure cancel requests,
discard stale cached bodies and close late leases. Cancelled openings keep their
bounded pending slots until settled. Errors remain explicit, without automatic
retry or an inline reader. Custom README/provenance remains package-owned.
Windows WebView2/Office qualification remains open.

Model-facing descriptions stay short and operational. `UseWhen`, `DoNotUseWhen`
and limitations are audited for selection value rather than copied from the human
manual. Custom package README is not automatically injected into model context.

## Library test form and layout

Library testing uses the effective minimal intent schema and the normal production
runtime. The form must provide:

- checkbox/switch for boolean, numeric controls with integer/number step and
  min/max, select/radio for enum, multiline input for long strings, and bounded
  structured editors for arrays/objects;
- explicit required/optional state, omit/null controls where the contract permits
  them, visible defaults and inline validation before run;
- a wrapping description below each argument instead of a truncated placeholder,
  with consistent spacing between the argument name, description and control;
- runtime-owned values resolved from the selected host/document, current test
  fixture and prior read result. UUIDs, URIs, revisions, hashes and cursors are not
  editable fields. An advanced diagnostic may show the resolved execution context
  read-only to the human after preparation, but it is never copied into test
  arguments, model context or model-visible result;
- `Next`/continuation UX for paged reads and typed result/effect evidence, without
  teaching the user to copy an opaque cursor;
- the same confirmation, document binding, safety and disposable-document rules as
  a normal invocation. Test mode cannot expand authority.

The reported layout defects are explicit acceptance cases: opening
`Implementation` must keep the editor, tabs and actions inside the right pane at
all supported widths; no horizontal displacement or hidden controls are allowed.
The `Test` page must keep labels and controls aligned, wrap full descriptions and
remain usable in the narrow Office task pane. CodeMirror refresh after tab changes,
`min-width:0`, overflow ownership and responsive grids are verified in real
WebView2, not inferred from a desktop browser screenshot.

The package editor stacks the wrapping component toolbar, metadata and full-width
code editor vertically. Test arguments use neutral separated rows, themed controls
at least 34 px high and a 760 px maximum form width; empty output is hidden.
`tests/web/tool-library-layout.test.js` measures shipped markup/CSS and CodeMirror
in Chromium at four pane/window combinations, in both themes and across repeated
tab switches. This replaces CSS-string layout assertions; Windows WebView2/DPI
qualification remains open.

## R61 delivery and gates

1. **Done host-neutral (11O0, refreshed through 11O6):** freeze every effective built-in tool ID/schema by mode and host. For each
   property record semantic owner, source/default, validation, internal resolver,
   result dependency, test fixture and keep/remove decision.
2. **Done for Resources + Capabilities, planning, HTML, authoring and VBA/macro:** contract checks fail on unreviewed or unexplained plumbing-shaped
   arguments (`*Id`, UUID, URI, revision/hash/etag, cursor/offset/page token). Names
   are a review trigger, not an unsafe automatic stripping rule.
3. **Semantic family switches and final core-pack selection through 11O6 are done
   host-neutral:** deleted argument paths have no aliases or dual schemas. Excel
   The later skill-exposure and [minimal bootstrap](#minimal-office-bootstrap)
   contracts supersede the original eager Excel/VBA core. Exact schemas remain
   available through direct admission or selected skill dependencies.
4. Run deterministic model scenarios proving that calls complete without invented
   opaque values and that cursor/revision confusion is structurally impossible,
   not merely discouraged by descriptions.
5. Inspect the fully materialized request, Tool Results and replayed model history:
   after each family cutover they contain no `ResourceRef`, `rna://`, revision/hash,
   cursor or internal id outside the two explicit public-id/correlation exceptions.
   Harness fixtures create and retain exact state behind runtime; scenarios cannot
   inject it through scripted model arguments.
6. **Done host-neutral (11O7):** populate and verify UI-only documentation for every
   built-in tool, switch the typed test form and fix both reported layouts with
   focused browser tests. Exact Markdown is fetched only for the selected id/revision;
   semantic `Next` retains its opaque state in a bounded isolated runtime session.
7. Qualify the final exact catalog with live providers and Windows WebView2/Office.
   Earlier evidence for a changed schema/catalog cannot close WQ-PACK or release.

R61 was completed host-neutral during the migration. Formal release qualification
must use the exact current catalog; evidence for a changed schema/catalog does not
carry forward. This is no longer a Phase 12 work-order prerequisite for ordinary
bug fixes.

## Read-only Tool Inspector first

Before authoring is expanded, Library → Tools must expose capability truth for the
current execution endpoint. Before Host Fabric this is the local owner endpoint;
after Host Fabric the same DTO follows the explicitly selected target. Each row
shows:

- exact id, name, host and kind;
- origin: built-in, custom package or document-local;
- selected `HostInstanceId`/document scope and catalog snapshot revision;
- `callable`, `discoverable`, `disabled`, `blocked` or `unavailable`, with an exact
  reason rather than an inferred status;
- effect class, risk, confirmation requirement and batch policy;
- descriptor/schema revision and package fingerprint where applicable;
- qualification coverage/readiness, without turning N/A into pass;
- links to the exact recent run/tool call, result and causal evidence.

Changing the UI-selected target refreshes the inspector from the new owner endpoint.
It never retargets an accepted run. A disconnected or stale endpoint keeps its last
descriptor only as visibly stale diagnostic information and grants no callable
authority. The inspector is a projection, not another catalog or settings store.

Built-ins display the application/catalog revision and are immutable. A custom
package displays its human package version separately from its immutable content
revision. A document-local tool displays `Live` plus its exact fingerprint; it does
not receive fabricated RNAssistant history.

## Custom package history and authoring

Dynamic authoring follows Tool Inspector and Host Fabric target pinning. Its target
contract is:

- one append-only custom-package journal and immutable CAS-backed bodies;
- one logical head per exact tool id and host scope;
- optimistic concurrency against the exact current package revision;
- every save creates a new complete package revision; restore creates a new head
  with `restoredFrom`; delete appends a tombstone;
- built-in tool ids and every tool/skill id in the shared namespace cannot be
  shadowed;
- import/export carries exact provenance and never treats an uploaded file as
  trusted before explicit validation and confirmation;
- VBA/custom-tool authoring defines only user/domain parameters consumed by the
  implementation. Bound host/document, `ResourceRef`, revisions, guards, package
  fingerprints and execution IDs are supplied by runtime outside the authored JSON
  Schema and never become hidden macro parameters. Plumbing-shaped authored fields
  fail validation unless the package records an explicit domain-identity rationale;
- authoring preview and Library test use generated runtime fixtures behind the same
  preparation boundary. The author/model fills only the effective semantic form;
  exact prepared state may appear only in a human read-only diagnostic;
- a committed catalog change becomes available at the next model-request boundary
  or next run; it cannot mutate an already accepted request;
- editor test executes through normal `ToolRuntime`, policy, confirmation,
  `DocumentSession` and result/effect evidence, using a disposable document or an
  explicitly approved copy for mutations;
- Outlook does not gain VBA-package execution merely from a host label; every
  host/executor combination must have an admitted runtime and qualification pack.

The flat `ToolStore` is removed at cutover rather than retained as a dual-write
history. Tool result payloads may still be materialized as ordinary artifacts by
the existing bounded result boundary; that does not make the tool definition an
artifact.

## R56 Tool Library slices and gates

1. Read-only selected-endpoint Tool Inspector and capability/availability DTO.
2. Exact run/result/evidence links through the existing chat history and trajectory
   detail; selected-endpoint host capability matrix in the Tool Inspector. No
   separate Issue Center is planned.
3. Append-only custom package revisions, restore/tombstone and import/export.
4. Guarded Library editor switch, conflicts and disposable-document test flow.
5. Model authoring switch and later-run catalog refresh.
6. Existing VBA package definition/result adapter removal (completed host-neutral in
   11J2); Windows VBE/Library qualification remains required.
7. Final existing-editor typed boundary and generic catalog/result removal
   (completed host-neutral in 11T10); Windows WebView qualification remains required.

Host-neutral tests cover catalog projection, stale endpoint behavior, scope and
revision conflicts, no-shadow rules and run-boundary refresh. Windows x64 + Office
x64 tests cover actual custom-package discovery/install/run/cleanup in Excel, Word
and PowerPoint, target changes during an editor session and endpoint loss. UI status
alone never proves an Office effect; install/remove require package journal/read-back,
and arbitrary VBA macro execution remains unknown after dispatch.

## Tool contract review — 2026-10-21

Status: static source review now covers the Common resource/authoring, HTML/VBA,
Excel, Word, PowerPoint and Outlook tool families. It establishes their current
intent and result boundaries, not model-choice reliability or real Office behavior.
Owner: Tool Library / model-context, with each affected domain owner.

This continues stage 5 of the user-supplied unified stabilization plan dated
2026-10-20: capability choice, preferred editor, instructions and results. Stage 4
also covers request duplication and budgeting. A smaller schema does not settle
whether the operations, argument choices and returned facts are understandable.
The older audit below remains historical evidence, not a current defect list.

### Current findings and decisions

| Area | Current code / evidence | Decision |
|---|---|---|
| Capability read versus selection | Native source-read metadata uses `loaded` for completeness, while only `core.skills.load` selects a skill. | Fixed in the model view: omit the root flag, retain completeness/content/guidance and exact `schemaInRequest` checks. Durable admission evidence is unchanged. Local checks establish the distinction; improved live model behavior has not been measured. |
| Excel write target | `excel.range.write` requires nonblank `sheet` and `address` in all three branches. Schema, adapter, domain and backend no longer substitute a destination. | Omitted/null/blank targets fail before dispatch. Explicit `A1`, scalar null, formulas and bounded tables retain their meaning. This intentionally changes accepted arguments; it is separate from schema compression and does not establish a prior wrong-target incident. |
| Worksheet names and references | A `sheet` argument is the literal worksheet name. Composed references use the shared formatter: `Sales Report` → `'Sales Report'!A1`, `O'Brien` → `'O''Brien'!A1`. | Quote only the composed reference; retain literal names in lookup, result coordinates and resource identity. Resource reads decode Excel quoting once; host-enumerated names are not unquoted or trimmed. Double quotes are literal name characters, not alternate reference delimiters. |
| Large Excel ranges | Discovery is metadata-only. Above 100000 cells, `structure` returns dimensions and sample targets; `excel.columns.summarize` scans explicitly selected columns locally and returns compact counts/numeric aggregates. Current `table`/`records` row selectors still apply after whole-source capture. | Keep overview, raw slice reads and selected-column aggregates distinct. The analysis is a narrow read-only tool rather than a broad `excel.inspect` action or an unbounded resource representation. Exact selector pushdown/filtering remains open; evaluate the extra descriptor's live token/tool-choice cost. See [large ranges](resource-fabric.md#large-excel-range-overview) and [column analysis](resource-fabric.md#large-excel-column-analysis). |
| Other Excel destinations | `add_table` requires an explicit nonblank `sourceRange`; generated name, headers and optional active-sheet selection retain their existing behavior. `upsert_chart` also requires an explicit nonblank `sourceRange` when creating a chart; omitted source on an existing chart preserves its series. Chart name, type, title and geometry keep their creation defaults. Explicit chart types are limited to `line`, `column`/`col`, `bar` and `pie`. Formatting and other range mutations retain their documented active-sheet/default-range behavior. | Missing table source fails before backend read; missing chart source fails after existence discovery but before dispatch. `upsert_chart` retains omission-as-keep on updates, including `updateOnly`; an explicit blank source or unsupported type is invalid before dispatch. Do not apply these contracts indiscriminately to other mutations. |
| Structural views | `table` and `records` use the same internal gateway branch, CAS index, row selectors, `ResourceTableBatch`, coverage and HTML/JS batch shape. New built-in model reads, discovery and HTML/JS input schemas expose only `table`; derived mappings already select it. Saved HTML bindings, v1 artifact transfers and previously authored custom JS inputs may still carry `records`. | Internal `records` reads remain for those consumers. Their explicit upgrade, provider-capability cleanup and removal gate are in the [migration map](stabilization/MIGRATION_MAP.md#historical-structural-records-view--2026-10-21). No distinct structural semantics were found; live model-choice improvement is unmeasured. |
| Discovery, content and search | `excel.inspect` supplies metadata, `excel.cells.find` finds cell matches, `resources_read` supplies values/source, and `resources_search` searches a text/source target. | Keep these purposes distinct; review descriptions and supported representations together. A known range needs no discovery call. Do not merge them into an unconstrained action switch. |
| Workbook and chat charts | `excel.chart.upsert` mutates the workbook; `excel.chat.chart.create` reads source data and creates a chat artifact. | Keep separate destinations and effect policies. Check that results clearly identify the destination and do not imply Excel rendering was verified. |
| Format schema repetition | `excel.range.format` now shares field definitions with seven required-only fragments. The runtime combines each fragment with its parent; strict output expands to the previous full variants. | Implemented with argument-equivalence checks: prompt schema 7021 → 1191 characters. The first paired live run passes the report but spends more cumulative input after extra protocol repairs; no overall token-saving rate is established. See the current progress entry. |
| Model batching | The current report runs attempt write batches; the parser rejects them and executes no writes. | Treat this as observed protocol-choice friction. It does not establish that tool duplication caused it, or justify combining five independently verified writes into a report macro. |

### Other families and destinations

| Family | Reviewed choice and result boundary | Decision |
|---|---|---|
| Authored files / Markdown / HTML | `file_create/copy/patch` publish downloadable document-owned file revisions; `markdown_save` publishes a standalone Markdown document; HTML write/patch changes a runnable workspace member. A `.html` or `.md` filename alone does not choose the destination. | Keep separate stores/effects. File tool descriptions now name their destination and point to the page/document tools; no generic file action union. |
| Resource discovery / indexed text | `resources_find` resolves targets, `resources_search` scans one text/source target, `resources_outline` navigates indexed parts, `resources_read` returns exact content. Word/PowerPoint/Outlook search tools add host-specific scopes and coordinates; they are not substitutes for a complete source read. | Keep distinct. A search-only negative requires explicit complete coverage; outline symbols are advisory. `partNumber` is a semantic indexed-source selector, not a provider cursor. |
| HTML / VBA | HTML whole-file write, exact patch, data write, bind, refresh and freeze have different targets or persistence effects. VBA whole write, patch, rename, delete, restore and macro execution have different guards and effect policies. | Keep separate. Refresh resolves bindings without populating a page; successful static preflight does not prove rendering. A successful macro call does not prove its external effects. |
| Word / PowerPoint | Inspect/list and domain search return summaries or coordinates; resource reads return exact source views. Word insert/replace/format/table/comment and PowerPoint slide/object/text operations have distinct mutations. | Keep domain operations and strict discriminators. No duplicate mutation with the same target, effect and result was found in this static pass. |
| Outlook | Folder search, mailbox archive index, draft creation and selected-mail update have different scope and effect. `index_archive` checkpoints local scan state but does not change mail; draft creation does not send. | Keep separate. Catalog descriptions now state these boundaries; incomplete scans cannot establish absence. |
| Plans / questions / authoring | Task List progress, Plan document, Markdown document, user question, prompt/skill/tool authoring operate on different authorities. Skill selection is separate from skill source inspection or authoring. | Keep separate and optional. Runtime owns list IDs, guards, revisions and permissions; an agent need not create tracking artifacts for ordinary edits. |

### R61 inventory reconciliation

The current built-in inventory has 80 unique IDs/rows. Six added intents were
reviewed individually: file create/copy/patch, indexed resource search/outline,
and bounded Excel column summary. Five existing Common descriptors changed:
HTML whole-file write, resource read (`partNumber` added), skill load, Task List
notes and VBA patch. The four other changes are guidance wording; host, mode,
binding and property paths stayed fixed. The chart-artifact, archive-index and
file-destination description clarifications in this pass have updated exact
descriptor revisions. The focused R61 inventory check passes. This is static
contract evidence; exact live-model choice and Office qualification remain open.

### Review sequence and acceptance

1. Done locally: clarify capability read results while retaining discovery,
   selection, admission and permission as distinct runtime facts. Model projection
   and original durable evidence are checked independently; live model-choice
   improvement remains unmeasured.
2. `write_range` now has an explicit destination and checked name/reference
   round-trips; `add_table` requires an explicit source range at schema, adapter,
   domain and backend boundaries. `upsert_chart` requires a source only for creation,
   preserves omitted fields on update and rejects unsupported chart types instead
   of silently creating a line chart. Static `table`/`records` equivalence and
   retained consumers are recorded above. Continue with an isolated persisted-binding
   migration before removing `records`.
   For each proposed rename, merge, split or removal, identify the user intent,
   competing operation, actual consumer, effect/confirmation boundary and owner.
   Remove the replaced path and update schemas, skills, prompts and results together;
   do not add aliases or a second execution path.
3. Review result producers with their tools: confirmed effect, exact affected
   target, readable output, completeness and actionable errors. Preserve business
   payload keys; exclude runtime revision/guard/admission plumbing from model text.
   Do not equate a saved binding, static check or successful invocation with a
   populated dashboard, rendered chart or verified document effect.
4. Static keep/merge/split decisions for HTML/VBA, Word/PowerPoint/Outlook and
   authoring are above. Revisit any family when a concrete wrong choice, rejected
   argument or false effect claim is reproduced; static review is not an eval.
5. Compare changed choices on the existing unprompted Excel and combined HTML/VBA
   fixtures with the same model/profile. Measure task result, wrong choices,
   rejected arguments, repairs, calls and actual API usage separately. A single
   passing trajectory does not establish reliability; fake Office does not qualify
   calculation, rendering or COM delivery. Run only checks relevant to each change.

## Historical tool ergonomics audit — 2026-10-01

The following inventory predates the shared Core and universal mutation cutover.
Current names and contracts are defined above and in [Text mutations](text-mutations.md).

Статус: каталог исследован; общий selector/whole-read/context cutover реализован.
Ниже сохранены исходные наблюдения; массовое объединение/переименование tools
и миграция стабильных domain payloads не выбраны. Owner: Tool Library / Resource Gateway, совместно с model-context owner.
Причины зацикливания, воспроизведения и общий план находятся в
[Agent continuity audit](conversation-protocol.md#agent-continuity-audit--2026-10-01).

### Действующий общий контракт

`ResourceSelectorContract` задаёт одинаковые name/target/view/path/pageIndex для
HTML bindings и JS inputs; target и record path переиспользуются resource reader.
`view` явный, `path` только для JSON table rows, `pageIndex` — целое zero-based число
для page views. Office table rows не требуют path. Колонки выбираются по keys из
`columns`, не по переведённому заголовку. `policy=head` — default HTML binding;
для фиксированного снимка указать `exact`. Source target из JSON write/binding
metadata проходит round-trip отдельно от workspace и самой binding.
Текущую книгу Excel связывают по range/table/name с `table`; исходный
загруженный XLSX/XLSB/XLS/ODS файл можно связать как `raw` для локального разбора
в HTML через SheetJS. Binding указывает на оригинал и не делает его live-книгой.
HTML vendor globals исполняются только в sandbox HTML workspace; они не входят в
экспериментальный `executor=js` Jint worker. Пользовательский JS tool остаётся
отдельным подтверждённым Tool Library package с собственным read-only contract.

Переиспользуемые JS/CSS/HTML файлы находятся в локальном каталоге HTML assets
([контракт](artifact-library.md#reusable-local-html-assets--2026-10-02)):
`html.assets.list` читает компактные метаданные по запросу, import копирует
выбранный пакет в workspace, publish сохраняет новую версию. Это исходники
страницы, не новые исполняемые инструменты агента; список пакетов не добавляется
в обычный контекст модели.

Повторные операции контролируются typed `ToolExecutionProgress`/recovery в kernel,
исторический результат — `CompletedToolOperation`, отсутствие/устаревание тела —
`ResourceObservationNotice`. Внешний Tool Result v1 не меняется. Complete core skill
и resource read используют один Gateway whole-reader; partial references сохраняют
свою точную coverage. Prompt schema 33 согласована с этим контрактом; custom prompts
требуют review, без автоматической перезаписи. R61 inventory отражает новые schemas.

### Реальная поверхность и качество схем

Инвентаризация через `OfficeToolCatalog.ForHost` и
`OfficeToolExecutor.GetControllerTools` поверх host-neutral fake adapters, с
включённым Agent JavaScript: **70 уникальных built-in ID** — Common 36, Excel 14,
Word 8, PowerPoint 8, Outlook 4. Доступный объединённый каталог для Excel содержит
50 ID, Word/PowerPoint — по 44, Outlook — 34. При default-off JS уникальных ID 69;
схема custom tool authoring также зависит от этой настройки. Пользовательские
пакеты в эти числа не входят.

Это историческая инвентаризация R61, не размер текущего model request.
Текущий [bootstrap](#minimal-office-bootstrap) содержит 6 schemas в Agent/Excel
и 5 в остальных Office profiles. Direct optional schemas подключаются по admission;
специализированные — через выбранный skill. Само число ID в полном каталоге
не определяет размер запроса.

Во всех рассмотренных схемах описаны top-level arguments; closed object branches,
enums и диапазоны уже используются. Критичная проблема не в отсутствии JSON Schema,
а в расхождениях semantics, зафиксированных до cutover:

- `resources_read.representation`, `html_data_bind.view` и `js_run.resources[].view`
  имеют перекрывающиеся, но неодинаковые словари. Чтение использует `media`, runtime
  bindings различают image/raw/page views. Это допустимые разные операции, однако
  связь между ними и фактическая форма batch должны описываться одним контрактом.
- `path` означает JSON record-array path, а для page views — индекс страницы в
  строке. Для Office record path уже знает provider, для JSON его выбирает caller.
  `$.records` из примера легко перенести на Excel, где правильный путь `$`.
  В read schema уже сказано omit Office path; в bind примеры всё ещё общие.
- `table` и `records` проходят через один `ResourceStructuredViewService` и дают
  один `ResourceTableBatch` с columns/rows. Название `records` не означает, что
  tool result — голый массив объектов, а Excel header не всегда structural field
  key. `fields` должно отсылать к опубликованным ключам конкретного view.
- У `js_run.resources[]` name/target/view/path не имеют собственных пояснений.
  Поэтому особенно легко перенести HTML binding name в независимый worker или
  выбрать text для target, поддерживающего только metadata/structure.
- `html_data_bind` по умолчанию выбирает `view=text`, `policy=exact`. Для живого
  Excel dashboard нужны подходящий records view и намеренно выбранная head policy;
  иначе корректный snapshot API может не соответствовать ожиданию пользователя.
- Полный source/text read собирается до 2 000 000 символов, а skills reader отдаёт
  лишь 24 000. Слово «прочитано» не должно скрывать разные completeness/coverage.
- Семантические row `offset/limit` доступны в resource reads, хотя общий текст
  правил относит любые offsets/limits к runtime. Нужно отделить выбор строк от
  внутреннего provider cursor; запрет одного не должен запрещать другое.

Это аудит описаний и общей цепочки; все 70 domain operations не исполнялись в
реальном Office. Наличие корректной схемы не доказывает корректность COM delivery.

### Объединение и разделение

| Семейство | Решение для следующего изменения |
|---|---|
| Resource find/read — 2 | Сохранить отдельно discovery и content read. Exact known target читать напрямую; поиск не должен быть обязательным ритуалом. Не добавлять отдельные Excel/VBA/HTML readers поверх общего gateway |
| Resource schema/mapping/derive — 4 | Оставить отдельным optional advanced workflow. Не требовать draft → publish → mapping → derive для обычного чтения диапазона или dashboard binding |
| Capabilities — 2 | Search только фильтрует каталог; read показывает исходник skill либо допускает прямую schema. Выбор skill выполняет только `core.skills.load`. Показывать actual per-request inclusion/admission; не заставлять искать известный id или заново читать уже callable schema |
| HTML — 7 | Сохранить разделение file write/patch, JSON data write и live source binding. JSON write уже создаёт binding: эта атомарность полезна и должна быть видна в результате. Freeze/refresh оставить optional; не объединять их в универсальный action tool |
| JS — 1 | Сохранить отдельный read-only worker с explicit resource inputs. Его `ok` не доказывает исполнение app.js в WebView или отрисовку страницы. Переиспользовать общий selector/batch contract с HTML runtime |
| VBA/macro — 6 | Whole write, targeted patch, rename, delete, restore и arbitrary execution различаются effects/guards; объединять их нельзя. Согласовать reuse after-source для write/patch. Длинное описание patch сократить до выбора/правил/исправления ошибки; подробную грамматику оставить в нужном skill/reference |
| Excel — 14 | Сохранить тематические inspect/search/write/format/table/chart operations. `create_chat_chart` и `upsert_chart` имеют разные destinations. Discriminated `write_range.kind` лучше трёх почти одинаковых tools; для записи стоит требовать явный range intent вместо неожиданного default A1. Это оценка контракта, не установленный случай записи не туда |
| Word — 8; PowerPoint — 8 | Сохранить domain operations и branch schemas. Ясно различить preview/поиск и полную evidence coverage; не сливать inspect и source read из-за частичного пересечения данных. `add_object.kind` не требует трёх новых tools без evidence путаницы |
| Outlook — 4 | Сохранить search, bounded archive index, draft и update: разная цена, scope и effect. Ограничения scan coverage не должны превращать неполный поиск в утверждение «писем нет» |
| Task List — 1; Plan — 3; Markdown — 2 | У сущностей разные цели: ход выполнения, план решений и пользовательский документ. Не объединять storage semantics; убрать обязательное создание всех сущностей для обычной правки. Checklist — для длительной работы, Markdown Plan — когда нужен план как deliverable или сложные решения |
| Questions — 1 | Сохранить отдельный typed user interaction. Не делать его обязательной стадией там, где ответ уже есть в контексте |
| Prompt — 1; skill authoring — 4; tool authoring — 2 | Оставить optional authoring; обычная работа не должна менять собственные инструкции для обхода ошибок. Для VBA/JS upsert нужны ясные discriminated branches; JSON Schema в `parameters` лучше принимать объектом, а не JSON-строкой внутри JSON. Не разбивать authoring на новые tools без конкретного сценария |

Первый шаг — общий **семантический selector contract** для read/bind/JS и общие
генераторы его schema/usage. Это отдельный model-facing DTO, не сериализация
внутреннего `ResourceReadRequest` с URI/revision/cursor. Provider объявляет допустимые
representations, структурные keys и допустимый выбор path/page; caller не угадывает.
Page index и JSON path следует развести по typed branch. Для Office canonical path
подставляет runtime. `table/records` — кандидат на одно каноническое representation
после проверки всех consumers; оставить два имени имеет смысл лишь при явно разной
семантике, которой текущий materializer не показывает.

Не нужен новый registry aliases или большой tool `resources.action`. Существующий
semantic target должен выдерживать round-trip: target из find/read/write/binding
разрешается обратно в тот же ресурс в той же authority. Exact resolution не должно
зависеть от того, попал ли ресурс в первые результаты общего поиска. Для HTML
описания явно различают workspace, binding metadata и source values; один title
или видимая карточка не заменяют тип и supported operations.

### Что именно унифицировать в ответах

Единый внешний Tool Result v1 уже существует: `tool_call_id`, `name`, `status`,
`message`, `data`; статусы `ok/error/unknown`. Это сохраняется. Добавлять ещё один
`success/ok` рядом или заставлять таблицу и VBA source иметь одинаковое содержимое
не нужно.

В HTML-контуре `HtmlWorkspaceToolService` передаёт в outcome структурированный
`JObject` и отдельный список `ResourceRef`, полученный из каталога workspace.
Preflight дополняет тот же outcome, сохраняя effect, recovery и read-back;
`HtmlWorkspaceToolHandler` сериализует данные один раз при создании Tool Result.
Handler не восстанавливает resource-ссылки из JSON. Durable `artifactRef`/`members`
и внешний Tool Result v1 сохраняются; preview получает typed DTO напрямую из дерева.

Слабая граница — `data` как произвольный JSON плюс независимые metadata paths.
`ModelToolResultProjection` знает семейства по именам и рекурсивно удаляет ключи,
похожие на runtime state. Для business table rows уже требуется специальное
исключение. Такой blacklist хрупок при добавлении новых форм результата, даже если
конкретная известная форма сейчас защищена. Исправлять надо источник DTO и общую
проекцию, а не добавлять очередное исключение имени поля.

Общий контур использует typed metadata-композицию при сохранении domain payload.
Для ещё не переключённых family producers это критерий последующего локального cutover:

| Аспект | Единое правило |
|---|---|
| Outcome/effect | Terminal invocation status отдельно от проверенного эффекта: changed/no-change/unknown. «Binding сохранён» не значит «dashboard получил строки»; static preflight не значит render verified |
| Error/recovery | Стабильный code, исходное сообщение, existing typed failure/retry policy, при необходимости исправляемое поле и допустимые значения. `retryable` не должен независимо противоречить policy. Ошибка не становится другой при replay/compaction |
| Observation | Semantic target, representation, coverage, completeness, body delivery/currentness. Runtime identity/revision/guards остаются typed internal evidence |
| Content | Отдельный domain payload: точный source/text, structured rows/columns или computation value. Его произвольные business keys не фильтруются как runtime metadata |
| Produced resources | Readable target, resource purpose/type, supported representations; для binding — source target и выбранный view/policy |

Переиспользовать `ToolRecoveryContract`, `ResourceEvidence`, `ResourceCoverage`,
`ResourceEffect` и `ResourceTableBatch`; не строить параллельную иерархию статусов.
Model projection формируется из typed metadata и неизменённого domain payload;
compiler/compactor потребляют те же факты до сериализации. Поля и casing меняются
атомарно со schema/skill/prompt consumers; несовместимые старые streams явно
reset/skip по действующему правилу, без alias/dual-write путей.

Приёмка — сравнение exact next request с исходным result, три result roles,
target round-trip, согласованность read/bind/JS и сохранение произвольных business
keys. Inventory/hash test полезен для обнаружения дрейфа схемы, но не заменяет эти
сценарии. Сначала исправляются потеря ошибок/фактов и повтор без прогресса из
общего плана; публичные переименования и уменьшение core — после измерения пользы.
