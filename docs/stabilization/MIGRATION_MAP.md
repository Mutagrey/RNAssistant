# Current-to-target migration map

## Inspector selected-context cutover — 07, 2026-11-13

Owner: Diagnostics navigation/query + ConversationPanel snapshots/drafts. Removed
Inspector global active-chat/context/usage/draft fallback, request-preview composer
fallback, duplicate detailed activity timeline/CSS and selected-chat source switching.
Existing bounded Journal/request readers and Advanced Diagnostics stay reachable.
Inspector uses the existing visible-only Dock renderer; no temporary adapter, new
store, registry or preference migrator. Windows/UI acceptance remains separate.

## Common native find — F2c5, 2026-11-13

Owner: Browser/native integration. Typed find controls reuse the existing semantic
capture and visual overlay. No legacy iframe find consumer, extra index or store.
Common per-view search owns cancellation and request generation; Windows native
focus/shortcut evidence is still required.

## Native annotation cutover — F2c4, 2026-11-13

Owner: common Browser/native integration. Shared VisualInspector now uses typed native
control/events, with no iframe message listener or generated inspector injection.
Removed HTML-editor staging/toolbar/selection facade and plugin unavailable stub;
chat cards/drafts delegate to the exact Browser owner. Existing draft/CAS/source
owners remain; no new store or temporary adapter. Native compilation stays open.

## Browser input evidence — F2c3, 2026-11-13

The existing accepted-response evidence and media provenance owners admit optional
viewport input; there is no coordinate registry/store or model-visible capture ID.
Workspace copies request evidence at ResponseAccepted, matching Office. Native
preparations remain one-shot and Unknown remains blocked until manual reopen.

## HTML export cutover — F2c2, 2026-11-13

Owner: Browser/resource export. Browser and editor now share exact target capture
and HtmlResourceExport.downloadTarget. Removed prepareHtmlWorkspaceExport, its DTO,
mutating checkpoint helper and editor exportSources path. No temporary adapter or
second resource store. Source ZIP remains a distinct supported project transfer.

## Native local Browser cutover — F2c1, 2026-11-13

Owner: Browser/native integration. LocalBrowserRenderer/Page/Environment now consume
BrowserContentSession and the existing SDK/data-plane/assembler owners. Deleted
BrowserIsolationPolicy and the old HTML iframe resource-control listener/client;
there is no second authored iframe path. Source-only remains an explicit host/runtime
unavailable state, not the product behavior. Native coordinate/selection delivery
and common autonomous export are unfinished work, not compatibility adapters.

## F2c interactive delivery — in progress, 2026-11-13

Owner: Browser/native integration. BrowserContentSession binds existing owners to
native byte routes without a second store. The unconditional SourceOnly policy must
be removed at the native consumer cutover. Keep only runtime-specific unavailable
errors. Old iframe resource-control listener must switch to a scoped native adapter;
RN.resources and existing plugin SDK/data/command owners remain the API owners.

## F2b Local agent admission — 2026-11-13

BrowserPageAutomation and its unused Office script/style embeds are removed.
ExternalBrowser-prefixed gateway/tool files retain their existing ownership rather
than a cosmetic namespace migration. No synthetic native-action adapter remains.
Historical JS semantic/privacy fixtures are test-only; native execution/input/media
are unavailable under B02. A future qualified native adapter must use the existing
IBrowserPageRuntime/IBrowserActionSource contracts, not a second renderer.

## F4c Transfer adapters — 2026-11-13

The remaining Workspace Browser copy/export adapter is connected. It delegates to
DocumentArtifactStore CAS members, HtmlProjectImportService and ResourceDataPlane;
there is no extra content store or fake ChatSession. The same download component
accepts provider open/close callbacks. Existing workspace catalog exposes metadata
without loading conversation lists. Saved suggestions use read-only catalog pages.

## F4b Provider cutover — 2026-11-13

HtmlArtifactView/BrowserObjectView and the editor's Browser preview mount are removed.
Materials/editor/plugin rail route to app-browser; editor remains the sole dirty/write
owner. Common BrowserSourceComparison replaces provider-specific UI bridges and DTOs.
The getPluginVersions/getSavedHtmlHistory temporary adapters below are now removed.
Workspace snapshot copy/export is the remaining provider action adapter (owner:
Browser/Application); it must use the existing CAS/data plane/import owners.

## F4a StartSurface / Shell — 2026-11-13

Flat chooser + all-chat enumeration and watermark screen list are replaced by one
StartSurface. Existing registry is extended; no adapter store is added. Panels/top
maximize and direct split DOM are removed; app-window calls Shell owner commands,
with group actions in the existing overflow. Browser Home placeholder is removed.
Document completion no longer loads a ChatSession; current metadata discovery has a
ConversationMetadata overload. Remaining old provider routes/actions and saved-entry
completion belong to F4b (Browser adapters), not this presentation component.

## Version catalog — F3b Plugin cutover, 2026-11-13

Candidate Apply and PublishedRelease writer/listing were removed. Plugin tools,
bootstrap, Browser sources/history/compare use exact saved entries. The old
getPluginVersions adapter was removed in F4b after SourceComparison switched to the
common BrowserVersionRequest. Old plugin/plugin-release UI routes and executable
transport are gone; existing Dock owner prunes those descriptors individually.
Generic Browser checkpoint controls currently admit plugin providers; document and
Workspace HTML adapters are the next F3c slice (owner 04/05). B02 remains source-only.

## Browser F2a — capability cutover, 2026-11-13

Workspace consumers переключены на IBrowserCapabilityContribution; borrowed Office
Browser tool/resource branch удалён. Общие Gateway/action handlers сохранены без
второго runtime/store. ResourceMediaMessages принадлежит Core; Office routing и
Workspace terminal owners используют его, LlmClient проверяет final image delivery.
Остаточные ExternalBrowser имена и BrowserPageAutomation — owner 01/03, удаление/
замена при F2b local action/native admission cutover. Они не открывают production
renderer: B02 остаётся обязательной блокировкой. Source/version adapters — F3/F4.


## Local Browser F1a — source-only boundary, 2026-11-13

External native Page/Dock singleton и Shell plugin frame transport удалены.
Один LocalBrowserPageRuntime поддерживает точные source-only sessions/history.
BrowserPageAutomation и прежние gateway/action consumers сохраняются до F2;
owner 01/03, условие удаления остаточных external names — переключение common
capability contribution. Active native renderer/input admission заблокирована B02,
не заменяется iframe. Legacy provider toolbar/source routes — owner 05, удаление
при F3/F4 version adapter cutover. Source/body stores не дублируются.


## Local Browser — промежуточный F0, 2026-11-13

Contracts переведены в Application/Contracts; старый Office Contracts file удалён,
consumers переключены. `ResolveLocalLocator`/`QueryNavigationSuggestions` имеют
настоящие catalog consumers. Version DTO и independent capability seam ожидают
F3/F2, не выдают доступность до functional wiring. В F1 удаляются старые native
singleton/probe и provider iframe consumers; owner прежний Browser package.

Owner: Browser package 01/02/03 в текущем последовательном чате. Существующий
незавершённый extraction доведён до общего `BrowserPageAutomation`: native Page
передаёт exact target/protocol, прежние gateway/tool consumers перечисляют owned
pages и отклоняют неоднозначный semantic target. Renderer пока прежний singleton;
это ещё не multi-tab/local-only delivery. Неиспользованный iframe-binding draft
не становится альтернативным renderer. Остаточные ExternalBrowser names/contracts
удаляются/переключаются в F0/F1 вместе с consumers. Guard/read/effect evidence
сохраняет прежнего владельца; нового store/runtime нет.

## Storage/context optimization — 2026-11-10

P1/P2 native single-chat deletion (2026-11-13): ConversationJournal directly owns
`IConversationStore.Delete` / `chat.deleted`; the existing card locator becomes the
verified tombstone receipt, with no new table/schema/store. Live alias, metadata
and selection are removed atomically; retained identity prevents resurrection.
Scope lease is shared with groups, and active runs reject deletion. Office
ChatSessionService / WorkspaceStore, fault recovery, group races, rebuild and clean
Workspace restore are checked host-neutral. History/evidence/CAS are retained;
this does not reclaim storage. Default writers and the existing adapter remain
until whole-port P7. DeleteDocument, Move/MoveDocument and storage usage are still
separate Store work, followed by other resource consumers and metadata/header.
[Contract](../session-events.md#journal-single-chat-deletion--2026-11-13).

P5 native chat groups (2026-11-13): existing ConversationGroupService owns scoped
metadata, ResourceJournal/CAS publication and typed UI changes. AgentCommunication
uses the existing Inbox alias lookup; exact accepted calls bind resource preparation
and kernel outcomes. No new store/table/registry/schema or hidden fallback. Native
reads page only group heads, but hydrate all group bodies; authority Publish snapshot
and metadata/header costs remain open. Owner: Application ConversationGroupService /
existing conversation and resource ports. Whole-port P7 removes ChatStore.Groups,
the explicit ChatStore constructor and retiring catalog branch; no data import.
Native single-chat Delete now shares this lease and its add/delete race is checked
host-neutral. Move/document deletion remain open. Default writers unchanged; Windows evidence open.
[Contract](../session-events.md#journal-chat-groups--2026-11-13).

P5 native assignment pages (2026-11-13): the existing event port projects canonical
Send/Resolve/spawn intake into the same assignment DTO, now owned by Core. Owner:
ConversationJournal / Application AgentCommunicationService and Inbox. Schema 21
extends only existing `agent_delivery` with a flag and directional partial indices.
Cold listing skips Inbox replay/catalog enumeration; it reads only page request
bodies, never report bodies. Exact scope/revision and canonical locators guard each
page. No new store, durable read model, import, dual-write or fallback. Whole-port
P7 removes explicit AssignmentsEvents and the capability branch/retiring adapters.
Group operations are next; Store/resource consumers and metadata/header stay open.
[Contract](../session-events.md#journal-assignment-pages--2026-11-13).

P5 native cancel (2026-11-13): the same control port/DTO now accepts pause/resume/
cancel. One `queue.control` pins source call, both heads and exact live lifetime
or saved wait. Owner: existing Application service/Inbox/RunOwner, journal and
ConversationWaitCancellation; no new store/table/schema. Read-back precedes the
live signal; cold termination reuses the same kernel/CommitRun helper as retiring
cancel and user recovery. Source Unknown survives a lost target terminal receipt.
Whole-port P7 removes explicit CancelEvents with PauseEvents/SendEvents/SpawnEvents
and the capability/adapters. No replay, import, fallback or dual-write. Next:
group operations, other Store/resource operations and metadata/header;
default writers and Windows/Office qualification remain unchanged.
[Contract](../session-events.md#journal-peer-cancellation--2026-11-13).

P5 native pause/resume (2026-11-13): typed event-port control commits one existing
`queue.control` with source-call proof and both heads. Owner: Application
AgentCommunicationService/Inbox, Core journal and existing runtime consumers.
Schema 20 extends `control_fact`; no new store/registry. Local user controls/ack use
the same direct frame, removing chat/run snapshot serialization. Office/Workspace
checkpoint commits share the existing Inbox lock with native controls. The live
RunOwner lifetime survives checkpoint invocation changes, never restart. Read-back,
safe acknowledgement, protected user pause, stale release and Unknown keep their
existing authorities. `PauseEvents` remains only behind the explicit retiring-backend
capability; whole-port P7 removes it with SendEvents/SpawnEvents/adapters. Native
cancel is connected above; next are group operations, other Store/resource
operations and metadata/header costs. Default writers are unchanged.
[Contract](../session-events.md#journal-peer-pause-and-resume--2026-11-13).

P5 application spawn (2026-11-13): existing `agent.spawned` now embeds the child's
Store metadata/context and optional native input, advancing both heads atomically.
Owner: Application AgentCommunicationService/Inbox and Core ConversationJournal.
Schema 19 indexes spawn sender/sequence on the existing table; no second store or
registry. Common metadata writing/runtime-entry checks are shared with chat/Send.
`SpawnEvents` is explicitly retained only for the retiring backend; whole-port P7
removes it and the capability branch with SendEvents/adapters. Native creation has
no separate root reservation, Store commit, initial Send, import or fallback.
Peer projections, remaining Store/resource operations, metadata/header
costs and default composition remain open.
[Contract](../session-events.md#journal-application-spawn--2026-11-13).

P5 outbound Send/Resolve (2026-11-13): existing event port exposes atomic delivery
explicitly; native indexed alias lookup supplies the existing scope endpoint. Owner:
Application AgentCommunicationService/Inbox and Core ConversationJournal. No catalog
scan/global service lock during native persistence, no new store/adapter/schema.
Exact runtime entry, receipt-linked terminal, both live cursors and Inbox signal are
connected. Whole-port P7 removes the temporary capability branch and SendEvents
together with the old event adapters; atomic delivery has no fallback/dual-write.
Spawn and peer controls are connected above; group operations, other
Store/resource consumers and default composition remain open. [Contract](../session-events.md#journal-outbound-send-and-resolve--2026-11-13).

P5 peer Inbox/correlation (2026-11-13): native acceptance shares delivery, input,
membership and both chat heads in one frame; existing Inbox consumes it. The event
port supplies addressed request references to common saved reply/completion waits.
Owner: Core journal / existing Inbox, AgentCommunicationService and runtime consumers.
Schema 18 extends existing chat/delivery indices only. Legacy Prepared parsing moves
to the existing ChatEventStoreAdapter, removed with whole-port P7; no new adapter,
fallback or dual-write. Send/Resolve and live notification are connected above;
spawn and peer controls are connected above; default composition remains open. Input-history bounds,
metadata/header and remaining Store/resource ports stay open.
[Contract](../session-events.md#journal-peer-inbox-and-correlated-waits--2026-11-13).

P5 event feed (2026-11-13): ConversationJournal implements the existing reader port
with typed notices, exact anchors and bounded chat-write index queries. Common feed
no longer parses legacy envelopes; that parsing remains inside the retiring reader.
Workspace reconnect now uses existing WorkspaceStore metadata ownership and its
configured event port, removing full-load and unconditional adapter construction.
Owner: journal / WorkspaceStore / ConversationEventFeed. No new durable format,
index, store or adapter. Existing ChatStore reader/adapters are removed at whole-port
P7 cutover; production writers and Office UI subscription adoption are not switched.
[Contract](../session-events.md#journal-event-feed-and-metadata-reconnect--2026-11-13).

P1/P2 user Inbox delivery (2026-11-12): text acceptance/edit/removal and delivery
share the existing chat/control/input indices, with CAS text and an exact original
message receipt for Applied. Owner: Core journal / existing Inbox and runtime
consumers. Schema 17 adds request identity to input_state. Common input code delegates
format-specific proof to IEventStore; legacy parsing stays only in the existing
retiring ChatEventStoreAdapter until full-port P7 removal. No new adapter/store,
dual-write, imported history or production switch. Native peer input/feed and waits
are connected above; attachments, peer projections, other
Store/resource operations, full Workspace steer transport
and metadata/header costs remain open. [Contract](../session-events.md#journal-user-inbox-delivery--2026-11-12).

P1/P2 saved waits and queue control (2026-11-12): Office/Workspace `agents.wait` now
retains and explicitly claims its existing checkpoint through ConversationJournal.
Timeout/cancellation close the original call once; lost acknowledgement leaves a
recoverable wait. User pause/resume is atomic with the chat cursor and uses existing
control rows (schema 16 adds only a query index). Owner: Core journal / existing
kernel and Inbox. User input and native correlated waits/feed are connected above;
application peer delivery/controls and other
mutation/Store ports retain their P7 gate. No adapter, alternate scheduler/store,
production writer switch or version bump. [Contract](../session-events.md#journal-saved-waits-and-queue-control--2026-11-12).

P1/P2 interrupted-run recovery (2026-11-12): existing Office/Workspace recovery uses
one typed kernel commit for the checkpoint, diagnostic and any synthetic terminal.
Reads become Error; possible writes remain Unknown. Known terminals, pending approval
and consumed claims retain distinct outcomes. Repeated recovery/ack loss/rebuild do
not replay. Workspace recovery no longer writes directly to ChatStore. Owner: existing
Application recovery / Core persistence. Application peer delivery, other mutation/Store/control
ports and P7 composition remain open; same old-backend removal condition, no new
adapter. [Contract](../session-events.md#journal-interrupted-run-recovery--2026-11-12).

P1/P2 mutation confirmation (2026-11-11): existing Office/Workspace consumers now
resume the saved file/VBA `core.write` through ConversationJournal. Pending has no
terminal/E; preparation remains in checkpoint CAS, with one disposable call locator.
Resume claims a new invocation before dispatch and consumes that locator on exact
runtime entry. Repeated approval, preparation mismatch and lost claim acknowledgement
cannot repeat the effect. Workspace denial retains NotDispatched. Owner: Core
journal / existing kernel consumers. Correlated wait delivery, other mutation/Store ports and
P7 composition remain open; no new adapter. [Contract](../session-events.md#journal-mutation-confirmation--2026-11-11).

P1/P2 bound mutation calls (2026-11-11): `core.write` now uses existing Workspace
file and Office VBA owners with ResourceJournal. Prepared facts bind exact accepted
call/runtime-entry positions before dispatch; schema 14 indexes one attempt per
origin. Chat terminals and later inspection link exact resource receipts without
promoting Unknown or replaying work. Office/Workspace catalogs accept the same
injected ports. Scope guards in the existing mutation owners reject unconnected
targets; remove those restrictions as their owners join the journal composition.
Owner: Core journal / Office resource authority / Workspace file owner. Remaining
mutations, correlated wait delivery and full production composition retain the
same P7 gate; no additional store/adapter or legacy optimization.
[Contract](../session-events.md#bound-mutation-calls-on-the-journal--2026-11-11).

P1/P2 direct text Store slice (2026-11-11): `ConversationJournal` now implements
`IConversationStore` itself; no wrapper over `ChatStore` is introduced. Scoped text
create/append/preferences/selection, metadata/catalog and paging are exercised through
Office `ChatSessionService`/bridge presentation and `WorkspaceStore`. WorkspaceStore's
concrete `ChatStore` signatures are removed. Production Workspace composition supplies
the already existing `ChatConversationStoreAdapter`; its owner is Core Persistence /
Application Workspace, removal condition is the complete Store/runtime/resource port
switch at P7, together with the old backend. This intermediate integration does not
enable a second production writer. Resource, rewrite/move/delete
and usage operations explicitly reject unsupported requests. The next storage
work extends this existing journal owner instead of optimizing the old aggregate path.
[Contract and limits](../session-events.md#direct-journal-text-store-integration--2026-11-11).

P1/P2 no-tool runtime: `CommitRun` now reuses the existing checkpoint reducer inside
the chat commit's single frame/transaction. Model event bodies and context receipts
use existing CAS; metadata headers exclude the kernel body. Office run service and
Workspace runtime ports exercise real model preparation/HTTP, reflection, finish,
restart and another turn. WorkspacePorts/Plan/task-list owners no longer depend on
concrete ChatStore; composition supplies existing Store/events/artifact ports.
No new adapter is added. Existing ChatConversationStoreAdapter/ChatEventStoreAdapter
remain production composition only, with the same complete-port P7 removal gate.
ToolPackAdmissionJournal no longer reads all events to restore callable membership.
Resource owners still produce legacy journals; mixed-root backup therefore remains
explicitly blocked. Next: mutation/resource integration, remaining Store and Inbox/
control operations, then production switch and old backend removal.

Owner: Core persistence/context; Application consumes common contracts.

P1/P2 read-tool runtime (2026-11-11): real Office/Workspace turns now persist
accepted calls, entry, terminal and checkpoints through that same `CommitRun`.
Existing call/result/context reducers share its frame/transaction; Office's later
model presentation does not create a second terminal. Indexed T/E/R and ToolPack
admissions support exact finish evidence and cross-turn reuse. Lost terminal ack,
unknown read result, restart/rebuild/restore and citation correction are covered.
Default production composition remains unchanged. The bound-mutation slice above
connects the first write and confirmation; native correlated waits are connected
above. Other mutations and application outbound peer delivery remain unsupported.
The same P7 removal gate applies; no additional adapter was introduced.
[Contract](../session-events.md#atomic-read-tool-runtime-on-the-journal--2026-11-11).

P1/P2 direct resource ports (2026-11-11): `ResourceJournal` now implements authority,
revision and mutation ports itself. `WorkspaceFileService` accepts those owners;
Office mutation observer and Plan/task publication accept the extracted mutation
contract. Concrete backend dependencies were removed from these semantic consumers.
The pre-existing notification list moved to ResourceJournal; the retiring backend
uses that same list until deletion. No new store, adapter, registry or dual writer.
Snapshots retain lazily loaded history pinned to exact canonical generation/hash;
mutation receipts are addressed by attempt instead of scanning the whole history.
Real file lifecycle and Office observer checks pass, including unknown and recovery.

Production constructors still select the old backend. Owner: Core ResourceJournal /
Office resource authority / Workspace file owner. Removal gate: connect accepted
tool call/result correlation and remaining resource consumers, then replace root
composition and remove `ResourceAuthorityStore`/`ResourceMutationJournal` together.
Full backup/restore must preserve workspace identity locators and pending move intent
as well as journals/CAS/keys; clean resource-root restore alone does not close that
gate. [Contract](../resource-fabric.md#direct-journal-resource-ports--2026-11-11).

[Plan](../proposals/RNAssistant_Architecture_and_Implementation_Plan.md),
[ADR-0019](../decisions/ADR-0019-bounded-jsonl-storage.md),
[P0 evidence](../audits/storage-context-optimization.md).
`StorageJournal` / `ConversationJournal` / `ResourceJournal` / `JournalIndex` /
`JournalIndexAdmission` are internal
new-generation owners used only in isolated Harness roots. They have no production
storage consumer, feature flag or dual-write. Accepted calls/results, bounded
context/windows, idempotent receipts and two-process index repair are exercised;
single-resource prepared/dispatch/outcome and exact conversation linkage also
rebuild from canonical frames. `ConversationJournal.Delivery` adds one-frame local
send/membership, scoped alias lookup, pending pages and historical reply correlation.
Existing Application communication/inbox consumers still use their previous path;
their handshake/replay is removed only with the P5b/P7 integration switch.
Atomic spawn, bounded run checkpoints and guarded queue controls also exist in the
isolated owner. `ConversationLeaseService.ForScope` is not yet a production lease
namespace; switch requires stopping old writers. Live Inbox paging/eviction and
notify integration remain open. Production `ConversationRunHandle` already checks
an exact invocation for Cancel/Dispose/IsActive; no storage adapter is involved.
Production resource owners, multi-target publication
and remaining consumers are not migrated. Existing storage
remains active until P2/P6/P7 durability, index repair, CAS closure and integration
gates. Removal condition: replace current journal transport with this single
primitive and delete old transport/replay paths at cutover; do not retain a second
production backend. No old chat data or keys are moved/deleted.

Production result consumers already use common `ToolResultPersistence` and typed
retained bodies. Removed Office envelope externalization, Workspace `WireResult`,
the per-run Workspace observation cache and historical-file refresh loop. There is
no adapter between the old aggregate store and the isolated journal. Full
`ChatSession` save/context paths still exist and prevent claiming overall completion.

Production diagnostics use mandatory compact manifests and optional bounded RAM
captures. `SessionTraceWriteQueue`, normal durable SSE and raw debug-file writes
are removed. Both hosts and Inspector consume the new semantics; there is no raw
persistence adapter. Historical raw CAS roots are preserved. P6b now inventories and validates all new canonical streams independently of the
production index; GC honors their typed/nested refs. Internal new-generation backup
restores JSONL/CAS and rebuilds SQLite. Production root switch, backup command and
actual Windows/DPAPI evidence remain P7/release gates.

P4a replaces the per-instance CAS encoding fingerprint with one shared bounded
verified entry plus lazy text coordinates. Common readers are switched; large/
explicit-seek streams retain their necessary bounded paths. No model authority or
live file freshness is inferred from cache. Remaining large-source/index/search
work and P3 aggregate session paths are open.

P3a removes per-source excluded receipt materialization and nested atom searches
from the shared compiler. Office/Workspace and dispatch manifests use the bounded
sample/reason counts directly, without an adapter. Full-session compiler inputs and
working-set restoration remain for subsequent P3 slices.

P3b replaces skill-catalog JSON storage/getters with typed defensive copies;
`SkillCatalogSnapshot` consumers in both hosts use that owner directly. No adapter
or additional cache.

P3c extracts `ModelContextSnapshot` into its own model file and replaces all three
JSON-backed getters with typed copies/read-only immutable evidence. Compiler clones,
repair and wire provenance use the same copy owners; nested mutable metadata stays
detached. Removed snapshot JSON storage and the redundant repair copy; no adapter.

P3d removes `ConversationRunOwner.Session`, its snapshot delegate/update/get copies
and full-session catalog overlay. Office progress uses the existing scalar identity
update; active document ownership uses metadata. Catalog/detail consumers read
canonical headers/sessions; Office and Workspace Stop address the existing Inbox
without hydration. No adapter or second store. Aggregate commits, explicit detail
loads, kernel history/working-set restore and remaining context scans stay open.

P3e extracts request-local read-body metadata into `ContextReadBody`. The common
compiler uses it for exact keys, body presence, receipts and delivered observations;
the repeated parsing/hash helpers are removed. The existing history reader owns
header validation. Omission/replacement discards derived metadata; no parsed tree,
body cache, store or adapter is added. Archive scans and indexed restart remain open.

P3f adds `ConversationJournal.Context` to that existing isolated index: bounded
working-set candidates, persistent unknown/recovery membership, compact presence
notice, keyset details and exact-call replay-guard lookup. The recent-only
`OpenContext` entry point is removed and its isolated consumers use indexed recovery
or explicit historical queries. No count-based chat stop, production adapter or new
registry. Production archive restore remains until P7 connects shared semantic detail
reads and action guards together with context selection; the summary alone cannot
replace those guards. Externalized signature reconstruction stays outside append/SQL
locks. Unknown runtime outcomes behind wire `ok` are retained by production restore.

P1 runtime write boundary: Office/Workspace and saved-wait cancellation now submit
`ConversationRunCommit` alone, with exact identity/revision/count, non-history state
and explicit delta. Removed the aggregate `CommitRun(session, command)` overload and
Office whole-history ID capture. The existing Core persistence owner temporarily
uses `ChatSession.CopyForRunCommit` / `CreateStateProjection` internally to feed the
legacy backend; this carrier has no messages and cannot enter generic `Commit` as a
runtime command. Runtime storage now reads only addressed/related rows using the
existing message index and finish locators. Full-history read/merge/reorder and
complete-root cache insertion are removed from this path. Checkpoint source membership
uses an ID lookup. The internal carrier and metadata/header/artifact processing remain
until these consumers move to the new journal reducer at P2/P7. No second store,
fallback or dual-write. Ordinary aggregate commits and full loads remain open.

P1 common fact projection: the existing `ConversationRunProjection` moved from
Office to Core Services; Office/Workspace and saved-wait cancellation consume it.
Removed Workspace's separate accepted-response/run-state constructors, Office's
call factory and `AppendPairedResult` insertion. Both hosts use chronological
append; existing request compilation owns call/result pairing. Office request
evidence and tool/media presentation stay with their owners, and result persistence
continues through `ToolResultPersistence`. No adapter or extra facade was added.

P1 read metadata: `ReadDiagnosticsSession` and its partial `ChatSession` result are
removed. Existing conversation/event ports use immutable `ConversationMetadata`
for identity-only reads, event readers and payload access. Office/Workspace
diagnostics, download/upload leases and draft staging consume that DTO; live callers
capture six scalars without history. Diagnostic lease checks no longer load the full
chat. No adapter or new owner/store; exact scopes and existing publication guards
remain. Full load/ordinary commit and new-journal consumer integration
are still open; this DTO alone does not make the backend bounded.

P1 message windows: removed `ConversationMessageWindow.Session`; Office/Workspace
now consume metadata, page messages and resolved artifact presentation data. Shared
URI/card/library helpers take explicit read inputs, preserving the same implementation
for full live sessions. No new adapter, store or resource owner. The temporary
`ChatStore.ReadMessageWindowAtPath` → `ProjectSqliteSession` path is now removed:
Core Storage reads typed page inputs directly and shares scoped resource/context/chart
projection with full loads. This internal adapter removal gate is closed. Root copying,
synthetic source messages and unused HTML workspace recovery are removed from pages.
Page reads exclude `LastRun`, removing their execution-CAS hydration dependency.
Full load/recovery keeps its exact-body check;
unavailable evidence is not reset, substituted or retried by a history read.

P1 resource read scopes: `DocumentArtifactStore.Read/List/InspectMetadata/InspectMetadataList/
SnapshotHistory/IsRemoved` no longer accept `ChatSession`. Office/Workspace and storage
projection consumers pass existing `ConversationMetadata` directly; internal Plan/record
readers use that scope too. Pure identity helpers share scalar validation with live
callers. No read compatibility overload, new owner/store or session reconstruction.
Specialized Plan/text operations, artifact/history enumeration, ordinary Store
operations and journal cutover remain open; no page aggregate adapter remains.

## Общие вложения, очередь и активные запросы — 2026-11-03

Owners: Application Conversations/Workspace, Core attachments, Web ConversationPanel.
`PluginApplicationService.ProjectFiles`, project upload DTO/bridge commands и
`uploadProjectFile` с file-status/вставкой `chat-files/` удалены. Office/Workspace/
plugin используют общий upload lease и attachment cards; draft scope остаётся exact.
Workspace очередь переключена на существующие Inbox/Application owners и общий
pending renderer, прежние отдельные Workspace pending bubbles удалены. Статусное
меню читает текущих RunOwner без durable registry. Нового adapter/dual-write нет.
Остаток S2: контекст/сжатие и pending-approval Steer parity, PDF/media explicit read,
адресные secondary consumers; этот срез их закрытие не заявляет.

## Независимые Chat View — 2026-11-03

Owner: Web ConversationPanel / source adapters; placement — DockWorkspace.
Singleton conversation root/reparent, shared composer binding, прежние Workspace
chat orchestration и Office inbox/history observer заменены per-object factory.
`chatComposer()`/`messageView()`/`$()` и selected Office accessors остаются только
адаптером вторичных consumers к выбранному **существующему** view/source. Второго
state/store или per-tab копии глобального state нет. Полное удаление facade перед
включением вкладок оказалось слишком широкой зависимостью; actual Chat View уже
не адресует через него команды или async delivery. Это явное уточнение прежнего
gate S2b.9–12 ниже, а не заявление об удалении всех legacy consumers.

Адресная дочистка после `c768fa38`: удалены пустой `clearSnapshot` и его вызовы,
неиспользуемые `ownsView`/`composerAction`/`changedOwner`, старые DOM-аргументы mount
и недостижимый repaint после его постоянного `false`. Навигационный mount остаётся
действующим adapter Shell, без контракта переноса singleton DOM. Source search не
находит прежних project-file upload handlers/DTO/bridge/include. Это удаление
мёртвых остатков, а не закрытие живых consumers ниже.

Plugin source/candidate/watch и live HTML/source/annotations уже instance-owned
(S3b.3c, 2026-11-03). `RNAssistantPluginAnnotations` и выбранный HTML annotation
entrypoint удалены: карточка сообщения передаёт exact conversation/source.
Оставшийся gate selected accessors — вторичные material/model-context consumers;
новые chat features через них не добавляются. Runtime/drafts/receipts остаются
у прежних owners. Визуальная контрольная точка — In Review.

## JSON-профиль: старые настройки — 2026-11-02

Owner: Core AppSettings / ModelProtocol. `ToolResultRole` и `FallbackToJsonObject`
остаются только read adapters старого settings JSON: effective user/false, видимое
`ProtocolMigrationNotice`; прежние поля не сериализуются. UI save очищает notice.
Удаление adapters — после прекращения импорта настроек до prompt schema 67 либо
явного сброса таких настроек. Production native projection/fallback уже удалены;
лабораторный native transport не является adapter или вторым runtime.
Старые reference-contract <4 чаты остаются read-only, без конвертации/удаления.


## Navigation target routes S3b.1 — 2026-11-02

Owner: Web Library / ArtifactCatalog / DockWorkspace. В S3b.1 `library-object` и
`material` были section routes в одном views catalog. S3b.2 удалил material adapter:
per-object descriptor/factory владеет exact scope и lifecycle, non-HTML singleton
catalog preview/refresh удалён. Library adapter остаётся до S4 dirty/save scope.

S3b.3a удалил ChatBrowser/plugin splitters и смешанный Preview/Inspector switcher.
S3b.3b удалил HTML Catalog preview и browsingRelease из plugin workbench: exact saved
HTML/release factories используют общий Browser lifetime/renderer и существующий
transport. Catalog/download/source viewer не имеют второй реализации.
S3b.3c (2026-11-03) удалил singleton Plugins: PluginView владеет mutable
source/candidate/watch/annotations по pluginId, template имеет локальные роли.
Live HTML editor/source/annotations также перенесены в HtmlWorkspaceView по chatId:
локальный DOM/рабочая копия/generations, exact source guard, close/dispose и общий Browser.
Сохранённый HTML не расширяет annotation права активной версии исходного чата.

S4a разместил Library/VBA в том же Dockview и подключил VBA к NavigationPane.
Временный `library-object` route выбирает объект в одном retained Library editor;
VBA тоже сохраняет одного действующего editor owner. Owner: Web Library/VBA.
Removal gate: предметные per-object factories с независимыми dirty/save scopes и
эквивалентными typed reads/writes; не размножать globals и не клонировать store.
Cold restart mode/search/scroll и несохранённый код остаются явным gap.


## Conversation composer S2b.2 — 2026-11-02

Owner: Web conversation presentation, `app-chat-composer.js`. Общий controller
получает DOM nodes и адресные adapters; `chatComposer()` временно возвращает
единственную production form и связывает dispose с ConversationView. Удалены
Workspace action registry/listeners и Office global editing fields, их fallback
не оставлен. ChatDrafts/ConversationInput остаются прежними владельцами данных.
Removal gate adapter: DOM/renderers, preferences/drafts и snapshots/subscriptions
адресованы конкретному view; проверены независимые чаты и не осталось consumers
глобального active selection. Не клонировать adapter для нескольких вкладок.

S2b.3: shared message/run builders и footer уже получают explicit presentation
context; global full-render flag удалён. Office snapshot factory остаётся adapter
selected state, Workspace transport/subscriptions и preference/draft adapter —
следующий шаг. Вспомогательные inbox/approval/media/context readers также должны
получить view scope до нескольких Dockview conversation instances.

S2b.4: polling/history-read lifecycle передан `ConversationView.observe`; прежние
global inbox observer/history-refresh map удалены. Source adapters используют
общий capture/delivery guard. Selected Office state / Workspace active snapshot,
full push delivery и preference/draft owner ещё требуют per-target factory;
temporary singleton adapter не размножать для Dockview tabs.

S2b.5: команды model/mode/reasoning и pending lifecycle принадлежат ChatComposer;
source preferenceService захватывает exact target. Global saving flags и прежний
Workspace preference dispatch/render удалены. Facts всё ещё читаются через selected
Office/Workspace adapter; полный snapshot и draft/file binding остаются removal gate.

S2b.6: draft capture/restore/accept/append принадлежат тому же ChatComposer;
Office ACK и Workspace больше не назначают обычный input отдельно. Workspace
`current/loaded/mode/reasoning` удалены, существующий ChatDrafts не менялся.
Остаток adapter: `sharedComposer` file/service binding, selected source snapshots,
full push routing и вспомогательные consumers. Owner/removal gate выше сохраняются.

S2b.7: общий snapshot source проверяет адрес read/command/full push до применения;
Office/Workspace предоставляют только DTO address adapter. Данные не копируются.
Остаток: selected Office state / Workspace active snapshot и доставка full push
должны перейти к per-view owner; затем остальные consumers и удаление singleton.

S2b.8: Workspace `active` и retained snapshot у run observer удалены. Единственный
bounded snapshot теперь у общего ConversationView / SnapshotOwner; source policy
сохраняет revisions, paging и live ordering. `ProjectChats` временно заимствует
один `conversationView`; owner — Web conversation presentation. Removal gate:
Office использует тот же owner, navigation/commands/push получают explicit view,
проверены независимые targets; после этого удалить singleton accessor, не копировать
его в новые вкладки. Domain receipts/drafts/runtime не принадлежат projection.

S2b.9: Office transcript/window/model/mode/reasoning использует тот же SnapshotOwner.
Шесть прежних полей `state` не хранят значения, а делегируют `selectedOfficeProjection`.
Owner — Web conversation presentation. Это temporary selected source adapter для
нынешнего общего DOM; скрытый Office не должен вытеснять Workspace projection.
Removal gate: explicit source/view factories принимают context/artifacts/live data
и delivery, все consumers читают адресный owner; затем удалить selected adapter и
accessors перед независимыми chat tabs. Не создавать из facade новую per-tab копию.

S2b.10: context/usage, material metadata и active artifact/checkpoint ID перенесены
в тот же Office owner; добавлено восемь accessors вместо отдельного хранения этих
полей. Все context commands используют захваченный owner, а не только chat ID.
HTML working copy/source cache/editor не перенесены. Owner/removal gate прежние:
explicit source/view factory, live delivery и consumers должны заменить selected
adapter; 14 accessors удалить, не размножать в независимых chat tabs.

S2b.11: activeRunViewState перенесён в тот же source, liveRun заимствуется из
существующего chatRuns. Пять live accessors теперь read-only; прежние writers и
resetLiveReasoning удалены. Office progress reducer принадлежит ChatRun, bridge
только маршрутизирует; release не отменяет run и delivery не создаёт source.
Owner прежний. Остаток: explicit source/view factory, адресные consumers и удаление
всех selected accessors перед независимыми chat tabs; per-tab копия запрещена.

S2b.12: Office source создаётся explicit factory; full-state reducer и render-input
builder работают без global state. Selected adapter вызывает эту factory, а не
имеет свой decode path. Known revision передаётся явно из прежней delivery map;
catalog/editor/runtime не перенесены. Owner прежний. Removal gate остаётся:
explicit Chat View nodes/commands/lifecycle, переключение остальных consumers,
затем удалить selected facade перед независимыми Dock chat tabs.

## Conversation view S2b.1 — 2026-11-02

Owner: Web conversation presentation. `app-conversation-view.js` — общий
target-scoped cache/viewport/paint lifecycle для Office и Workspace/plugin.
Временный adapter `messageView()` в `app-messages.js` удерживает текущий singleton
даже при transient DOM detach в Dockview; он не хранит второй chat snapshot.
Removal gate: builders, input/edit/actions и доставка snapshots/subscriptions
принимают explicit view scope, все consumers переключены и проверены на двух
независимых targets. Тогда удалить currentConversationView/global DOM route,
а не клонировать его для каждой вкладки. Storage/run owners не меняются.
Прежние global renderer/scroll caches, Workspace entries и Dock viewport/dirty
state удалены в этом срезе; duplicate fallback для них не оставлен.

## Request view S3a — 2026-11-02

Owner: Web diagnostics presentation / DockWorkspace. `request` — singleton в
существующем каталоге; consumers чата/Inspector/журнала используют один reader.
Старое размещение в Diagnostics и семь tabs удалены. `switchTab("request")` —
маршрут к существующему DockWorkspace, не второй placement engine. Journal/Lab/
Приложение остаются root sections: owner Web Shell, удаление границы — их будущий
проверенный Dockview-срез. Source scope живёт в памяти view, не в layout storage.
Новых adapters, stores, bridge contracts и параллельных readers нет.

## Production UI S1b/S1c — 2026-11-02

Owner: Web Shell / DockWorkspace. Conversation, Inspector и Settings используют один Dockview;
три singleton DOM mounts живут с текущим global active owner. Temporary boundary: остальные
root sections (кроме перенесённого Settings) ещё размещает switchTab; HTML/plugin preview имеют прежние splitters.
Removal gate: соответствующий функциональный срез перенёс всех consumers, сохранил
lazy/deactivation, native frame identity и проверил old route aliases. После этого
его прежние placement/CSS удаляются; постоянный dual path не вводится.
Global IDs/active owner снимаются только S2 с полноценным view-scoping и проверкой
exact session/receipt ownership; до этого simultaneous chat clones запрещены.
Старые Inspector grid/overlay/width/open и перемещаемые header toggles удалены уже
в S1b. Уточнение навигации удалило fullscreen/compact CSS и эвристики выбора
из app.js: один ShellLayout получает событие общего NavigationTree, preview
потребляет общие navigation columns. Отдельного временного nav adapter нет.
Повторная сверка: Shell DOM/CSS владеют ShellLayout/app-product-navigation;
DockWorkspace передаёт snapshots/callbacks, ActionMenu обслуживает все Dock menus.
Прежний flex Shell, дубли rail/topbar/footer, `#tab-workspace` CSS и Dock menu
renderer удалены. Root sections/native previews остаются временной границей выше;
наращивать их второй registry или controller нельзя.
S1c удаляет `#tab-settings` и глобальную settings navigation/splitter.
`switchTab("settings")` остаётся только command alias к существующему Dock catalog;
removal gate — перевод оставшихся section consumers на единую карту в их срезах.
Owner alias: Shell navigation, owner формы/dirty/save: app-settings.js.

[Scope/review/rollback](../ui/UI_MIGRATION_PLAN.md).

## Demo-only chat embedding — 2026-11-02

Owner: Demo presentation (`prototypes/ui/`). Consumers: Dockview chat views.
`chat-views.js` and `docked-chat.css` temporarily embed the existing product preview
with its mock bridge; the reference frame reuses production sources. No second chat renderer
or runtime is introduced. Removal gate: replace or retire this visual prototype
after explicit approval of the production phase; do not carry the iframe adapter
into production. Scope and limits: [UI Layout / UX](../ui/UI_LAYOUT_UX.md).
P3 is frozen at 4ea3d6e4 after the strategy transition; current migration gates
are in [UI Migration Plan](../ui/UI_MIGRATION_PLAN.md). Production has no dependency
on this adapter; the production migration boundary is recorded above. The frozen reference shares
the vendored asset and skips inner DockWorkspace binding in its mock-only frame.

## Compact multi-target patch — 2026-10-29

Owner: Core Tools / Application Workspace / Office Tools; UI: Web.
`core.patch` now has only `patch`, with `*** Update Target:` sections and line hunks.
The old JSON-hunk schema, `{target, patch}` variant and bottom-up physical VBA writer
are removed, with no callable compatibility path. One wrapper prepares all owner
commands and preserves per-target existing journals/effects; it adds no store.
Prepared state/confirmation/replay, model projection and per-target UI consume the
new contract. `DomainOperation` below remains the existing temporary owner bridge.
VBA patch and replace now invoke the guarded whole-module writer directly. The
private VBA JSON-operation reader, substring engine and patch service/guards are
removed; shared fault/journal fixtures use the surviving whole-write owner. Workspace streams large
UTF-8 sources. Authored-file patches now stream UTF-8/16/32 through the existing
artifact owner/CAS; its old private find/replace-hunk parser is removed. Metadata
resolution and full file read-back no longer hydrate complete bodies. Markdown/HTML
aggregates and other authored-file mutations still materialize their source.

Workspace's retired substring writer (`PatchExact`/`PatchLargeExact`) and its
private unique-anchor scan/result-range helper are removed. The file owner uses
`ApplyTextEdits`; shared streaming byte-copy and source/read-back verification
remain in the current text-edit/stream-write paths. Owner and plugin fixtures now
use full-line hunks, including large-source ambiguity, stale reads and recovery.
HTML consumes typed resolved edits from the same planner. Its old JSON-operation
reader, `StructuredTextPatchEngine` and unused private tool schemas/descriptors
are removed. Private operation identifiers still route existing owner commands;
they are not model-visible registrations. Public diagnostics retain bounded hunk
errors/candidates instead of returning the complete HTML source after a mismatch.
HTML batch guards now pin existing member heads plus their authority generations.
Sibling publication no longer invalidates an unchanged next member; source/hash,
member identity/version and selected current aggregate remain guarded. A changed
then restored member fails before its write. Office prepared state is version 2;
older pending state fails closed and needs fresh preparation, not migration/replay.

## Universal text tools — 2026-10-28

Owner: Core Tools / Office Tools / Application Workspace. Public per-environment
source registrations are removed. `core.write/patch/replace` and optional resource
lifecycle tools route to the existing owners; no retired name can dispatch.
The private `DomainOperation` bridge in OfficeResourceMutationHandler is temporary:
VBA/HTML/Markdown/authored-file/prompt/skill-reference handlers still consume their owner command arguments.
Plan source already uses typed `PlanResourceCommand` through its existing publication owner.
Removal gate: migrate the remaining owner entrypoints to typed prepared resource commands,
then remove private string routing without changing accepted ToolCall, policy, journal
or read-back. This bridge is not a compatibility layer for old chats or model calls.
Current semantics and explicit custom-dependency migration: [Text mutations](../text-mutations.md).


Историческая карта завершённого host-neutral переноса и оставшихся постоянных
границ. Открытое Windows evidence не превращает удалённые пути в активные.
Обычной разработкой теперь управляют [development-rules](../development-rules.md)
и краткий [PROGRESS](PROGRESS.md); новые временные adapters добавляются сюда только
с owner, consumers и условием удаления.

ModelProtocol работает на v8; streams прежних форматов доступны только для чтения,
а новый run требует format-8 chat. Pipelines отключены. Phase 3B2 подключает
`AgentKernel` к production start/confirmation через Office model/tool/store ports.
Новый summary replay проверен на существующих events; старый loop, builder,
mutable ID bookkeeping и Failure.Cause удалены без fallback. Phases 4/8/9 и
обязательный existing-tool route через 11T10 переключили typed ToolRuntime/effect
wire, resource data plane, persistence/UI и все active tool paths host-neutral.
Ниже остаются current owners, удалённые seams, permanent narrow ports и их evidence
gates; active tool compatibility adapter отсутствует. Windows/Office qualification
не закрыта. Resource direct cutover завершён host-neutral вместе с отдельной
проверкой resource-consumer cleanup (2026-09-07). Порядок задаёт
[Resource MASTER](resource-cutover/MASTER.md), владельцев — [Resource Fabric](../resource-fabric.md).

## Общий владелец разговора — 2026-10-27

Активная карта для [S1c](BACKLOG.md#s1c-общий-владелец-разговора--2026-10-27),
не заявление о завершённом переносе. Общий owner — Application conversation
orchestration; оба окружения переключаются в одном поведенческом срезе.
Уже общие `ConversationLeaseService`, `ConversationWorkService` и
`ConversationRunInputChannel` в Application используются обоими окружениями;
старые per-environment lock acquisition, worker tracking и input-consume loops
удалены. Office `ChatRunRegistry` удалён: live handle, exact cancellation и
резервация запуска принадлежат `ConversationRunOwner` в Application у обоих
потребителей. Owner хранит только live metadata; session snapshot и Office clone
adapter удалены в P3d. Общая UI-проекция остаётся открытой. Workspace named pipe
доставляет Stop этому owner, а прежний локальный run token из pipe удалён.
Короткая глобальная Office coordination тоже
принадлежит Application; Workspace использует только точную резервацию.
`ConversationApplicationService` теперь управляет tracked work обоих consumers:
Office FIFO выбирается и дренируется в Application через узкий document port;
Workspace submit/approval используют тот же owner для admission receipt,
completion, отмены и drain. Старые Office `StartInboxWorker` и Workspace
`StartRun`/`RunOperation` удалены. Workspace пока запускает обычный ввод напрямую
после exact lease, а не через общий FIFO; общего полного scheduler ещё нет.
Office lease всё ещё лежит в
`locks/run_*.lck`, Workspace — в `workspaces/run-leases/*.lck`.
Обычный Workspace submit также записывает intake/Applied через общий inbox; прямой
append этого пути удалён. Live inbox адресуется по host/workspace/document/chat,
поэтому совпавшие chat ID разных документов не разделяют состояние.
Workspace activation/resume тоже переводит доказанную доставку через этот inbox:
он обновляет журнал и уже загруженную проекцию; отдельный Workspace writer receipt
удалён. Office explicit resume использует ту же сверку сохранённого user message.
Workspace pending-steer snapshot теперь использует общий inbox selector, как Office
pending snapshot; отдельный Workspace сбор списка из journal удалён.
Поиск принятого operation ID тоже переключён на один Application projector:
Office читает live inbox, Workspace — canonical journal при reconnect; проверка
первого event/fingerprint/status общая, отдельный Workspace lookup удалён.
Остальное восстановление run/lease и FIFO ещё раздельно.
Решение о recovery и сверка exact run/cursor после повторного чтения теперь общие
в Application `ConversationRecoveryService`: Office и Workspace переключены,
старые status-only/Workspace `NeedsRunInterruption` проверки удалены. Доменный
read-back и формирование diagnostic остаются в своих адаптерах. Захват lease,
применение recovery и общий scheduler ещё не объединены в одного владельца.
Оба live steer теперь проходят `ConversationInboxService.SubmitToRun` с exact
kernel run ID. Office pending-confirmation steer проверяет ещё и pending ID;
поздний bound steer остаётся в журнале, но не запускает новый ход. Office
подтверждение продолжает исходный run ID. Общий owner tracked work не объединил
Workspace FIFO admission и domain preparation; эта часть S1c остаётся открытой.
Повтор exact steer теперь разрешается в общем inbox до проверки live target;
Workspace убрал внешний lock вокруг общего admission и чтения квитанции.
Закрытие kernel input использует уже захваченную проекцию inbox. Это не
переключение Workspace на общий FIFO scheduler.

| Временный путь / текущие consumers | Что остаётся доменным | Условие удаления / owner |
|---|---|---|
| Office `AssistantController.StartControllerRun`/`CancelChatRun` façade, send/confirmation и controller disposal | Exact document binding, ресурсная подготовка, host runtime use lease, domain model/tool/store ports | Office FIFO уже выбирает Application; удалить оставшийся controller run workflow после переключения полной общей admission и UI projection |
| Workspace orchestration в `ExecuteInputAsync`/`ExecutePendingAsync` и `.Control.cs` (`ContinueOwnedAsync`); bridge + CLI | Workspace association/root, settings/tool/resource profile, file authority; CLI ждёт completion общей задачи | Tracked operation уже принадлежит Application; переключить direct submit на общий FIFO/admission и оставить только domain port composition |
| Office `ConversationKernelAdapter.Input.cs` и незавершённый Workspace `.Input.cs`; input channel обоих kernels | Подготовка Office-вложений/ресурсов, применение подготовленного ввода к domain model session | Общие admission/consume/append/applied/interrupt через существующий journal и `IRunInputChannel`. Удалить отдельные channel algorithms; перенос одного inbox helper не закрывает строку |
| `ChatSessionService.ReconcileInterruptedRuns` и Workspace `RefreshSession`/`PrepareResume`/`InterruptAbandonedRun`; activation/reopen | Сведения domain mutation journals и read-back, exact target availability | Общий owner ещё должен забрать захват lease и сохранение recovery transition. Kernel-lifecycle decision и exact run/cursor guard уже общие; повторного tool dispatch нет. Receipt reconciliation уже переключена на общий inbox |
| Workspace `WorkspaceRunControl` preview/input/cross-process client | Локальный current-user transport и его ACL, техническая доставка/таймаут | Перенаправить preview/input на typed команды/snapshot общего owner; отдельный run token и Stop owner удалены, transport остаётся |
| `app-chat-inbox.js` и chat-части `app-project-chats.js`: inbox/receipt snapshot projection, approval/Stop и action dispatch в `app-chat.js`/`app-workspace-chat.js` | Навигация, дерево проектов, Office target/resource selection | Общие input gate, адресный submit barrier и observer уже переключены на обоих consumers; старые Office/Workspace submit-pending карты удалены. Дальше один Web conversation controller/composer использует общий snapshot/commands; удалить оставшиеся раздельные action/projection ветки, сохранить scope+generation и draft revision guards |

Общий `app-conversation-state.js` задаёт индикатор подтверждённого владельца
для деревьев Office и Workspace. Общий input gate покрывает flush/dispatch/
receipt текста и typed answers; планировщик чтений и адресный submit barrier тоже
общие. Approval/Stop, составление снимков и остальной Web controller из строки
выше ещё подлежат замене.

Новый контракт typed, source module подключается через существующий `.projitems`.
Run lifecycle/outcomes остаются у kernel; live handle Application не второй статусный
store. Один canonical event stream/CAS на разговор, без dual-write и переписывания
истории. При переключении не оставлять старого активного writer рядом с новым:
остановить и дождаться старых задач до смены composition; lease namespace не меняется
при активном владельце. Не удалять пользовательские чаты или mutation evidence.

## Historical structural `records` view — 2026-10-21

| Temporary seam | Owner / current consumers | Removal gate |
|---|---|---|
| Internal `records` structural reader beside canonical `table` | Resource Fabric and HTML binding/transfer owners. New built-in model reads, discovery and HTML/JS input schemas offer `table` only. Saved `HtmlWorkspaceDataBinding.View=records`, v1 artifact transfer packages and previously authored custom JS inputs can still reach the internal gateway/data plane. This is an explicit legacy read path, not a new binding choice. | Define and verify an explicit upgrade for saved bindings and v1 package import/export without losing source refs, head/exact policy, view path or data. Switch HTML/JS/transfer consumers and provider capabilities together; then remove `records` from the structural reader. Check restart, exact/head, derived mapping, Excel/Outlook/JSON and transfer host-neutral; qualify Windows/Office separately. No silent alias or deletion. |

## Product UI shell transition — 2026-10-18

| Temporary seam | Owner / current consumers | Removal gate |
|---|---|---|
| New section rail around existing panel DOM and `switchTab` | `web` presentation; all current `tab-*` panels and navigation callers. The rail owns only section selection; feature controllers retain data and actions. | Move each section's tree/content into the shared layout, switch every navigation entry, then remove replaced panel shells and CSS. Keep one navigation owner throughout. |
| Product-styled Materials catalog with old format grouping and HTML editor shell | `web/js/app-artifact-catalog.js` and HTML workspace controller; real catalog projections, viewer registry and guarded actions. | Connect the agreed ownership tree and common material/editor header to these controllers, then delete old catalog/editor shell markup and selectors. |

## Data Templates application adapters — 2026-10-16

Data Templates is an independent application source workspace in
`web/data-templates`, distinct from user data projects. The shared UI/core and
semantic tests have moved there; old copies/consumers are removed. Browser folder
export excludes trusted native transport. As of 2026-10-19 the root-DOM mount and its
controllers are removed; [Plugins](../plugins.md) owns the isolated viewer/commands.
These remaining seams are explicit, not fallback paths. [Contract](../data-templates.md#application-project-and-data-projects).

| Temporary seam | Owner / current consumers | Removal gate |
|---|---|---|
| Compiled `TabularRuntimePin` and fixed application-module list in `TabularRuntimeBundle` | Native execution adapter; `WorkspaceTabularResourceSession`, `TabularWebViewExecutor`, Harness and native browser integration tests. Trusted `web/tabular/native-*` stays outside the portable source project. | The new plugin path uses immutable releases and separate grants; it does not call this executor. Qualify the new Windows admission/isolation/revocation/drain, switch remaining native/CLI consumers and delete the compiled app pin/list. Never simply permit arbitrary workspace scripts or widen the root UI CSP. |
| Native preparation queue precedes integrated processing | `WorkspaceSourceQueue`, CLI/Office typed preparation commands and existing native integration tests. The replaced root source chooser is removed. It creates exact source snapshots and does not parse templates. | The application's own standalone processing workflow consumes browser or prepared-native source capabilities through the same commands. Retain the preparation service for native acquisition, but remove any UI-specific orchestration once those consumers switch. Do not add a second C# business-processing queue. |

## Runtime commit boundary — 2026-10-15

All production conversation writers now use explicit `ConversationCommit` /
`ConversationRunCommit`; the general `Save` API and message-shape classifier are
removed. State-only commits retain metadata/artifact delta calculation and the
existing JSONL/CAS barrier. History rewrites are explicit commands; ordinary saves
never rediscover message semantics or diff every message body.

| Temporary path | Owner / remaining consumers | Removal gate |
|---|---|---|
| Existing JS casing adapters for embedded Core projections | Office Contracts / Web feature owners; chat summaries, settings, message/activity and document/resource projections. Transport version, operation policies and typed init/navigation/progress envelopes are cut over | Replace each embedded shape with its exact typed contract and switch its consumers together; delete both-case readers and verify serialization/consumer parity. Do not apply global casing conversion to opaque tool data or stored history |

## Model result families — 2026-10-07

Core `ModelResultProjection` owns closed resources/workspace-file envelopes;
Office `ModelToolResultProjection` owns Excel and native VBA result metadata;
Tool/Skill/Prompt owners now provide closed authoring envelopes, preserving opaque
schemas and source text without recursive field removal.
There is no second result store or fallback for these families. Typed recovery is
rendered at this shared boundary, not copied into domain `DataJson` by ToolRuntime.

The existing capability, HTML, Word/PowerPoint/Outlook and custom-package
projections remain explicitly outside this slice. Owner: Office domain result
producers / model projection. Removal gate for each remaining generic/field-removal
branch: inventory its real payloads, switch its consumers and actual-wire tests to
an explicit envelope, then delete that branch in the same change. Arbitrary custom
output remains business content, not recursively filtered metadata.

## Workspace-first adapters — 2026-10-04

| Temporary seam | Owner / current consumers | Removal gate |
|---|---|---|
| `Host="Workspace"`, `DocumentKey=WorkspaceId` as ChatStore path partition | `WorkspaceStore` creates workspace chats in the canonical event store; CLI loads them through the same association. `DocumentAuthorityId` is null. These fields are internal routing, not a fake Office document. | M7: native workspace partition in session/catalog paths, then remove the routing adapter without rewriting historical events. |
| Net8 Core project over the same production source tree | `RNAssistant.Core.Net8.csproj` builds Core for CLI; .NET Framework project remains for Office. DPAPI `SettingsService` is excluded from the net8 text path; CLI reads key from process environment. | M11: settle the cross-target secret/storage ports and packaging; no copied source files or fake Office dependency. |
| Shared application source module (2026-10-16) | Application owner: `RNAssistant.Application/WorkspaceApplicationService` and typed contracts, now including `Workspace/WorkspaceConversationService` and plugin services, imported once per target through `.projitems`. Office bridge and CLI project commands use the same implementation; Harness/MockDemo compile that source too. No Office types, browser dependencies, separate filesystem state or copied conversation code. Chromium verification sits behind `IWorkspaceWebVerifierFactory`; the Framework host does not depend on .NET 8. | M7/M11: replace source imports with a common application assembly when compatible Framework 4.8/CLI packaging is established, preserving these contracts, binding guards and file owners. Do not require an Office framework migration for this slice. |
| CLI accepted-fact and authority preparation | `WorkspaceConversationService` normalizes durable accepted facts with stable message IDs, refreshes current-run file authority through `WorkspaceFileService` and pins committed skill metadata, conversation-owned Task Lists and workspace-owned Plans through shared Core publication owners. Core `ModelContextCompiler` owns frozen context assembly/filtering/repair; `ModelRequestPreparation` owns common wire-schema/projection coordination, request budget/reserves and `ModelProtocolRequest` assembly for Office and workspace (2026-10-26). `ContextCompactionRuntime` owns checkpoint selection/claims for both hosts. Core `ContextWorkingSet` also restores receipts and current bodies for both hosts. Office retains authority capture, domain projection and prompt preparation, with no separate compaction/retention algorithm. | M5–M7: consolidate application preparation when host profiles share the same capability/context inputs; preserve one compiler, compactor and file authority owner. |
| Filesystem text-only provider surface | Office and CLI use shared `ResourceFindPage`/`ResourceReadProjection` and exact `ResourceReadObservation`; bounded CLI discovery carries metadata descriptors and explicit incompleteness. The filesystem provider now exposes exact retained text to the browser verifier (2026-10-05). `ResourceProviderRouter<TProvider>` still only routes providers. | §8.1 remains open for raw/binary views, remaining preview/viewer consumers and shared Office/filesystem operation dispatch. Close each with a concrete consumer, bounded coverage and exact evidence, preserving Office host guards; the text slice does not replace the target architecture. |
| CLI abandoned-run reconciliation | Plain `PrepareResume` still closes an abandoned run without replay. Explicit continuation now uses the shared coordinator/kernel, exact checkpoint, complete history and original remaining limits. Cross-process cancellation reaches only the owning kernel through an ephemeral current-user pipe. | M5–M7: consolidate startup and CLI reconciliation in the common application lifecycle; adopt control contracts in UI and qualify Windows transport. Keep incomplete/unknown tool exchanges blocked from same-run continuation. |
| Workspace application request/reconnect consumer | CLI `run`/`answer` and Office inbox share Core input event/CAS serialization, fingerprints/conflict checks and typed question admission. `ReadState`/`ReadEvents`/`SubscribeEventsAsync` use canonical sequence/hash cursors and Core event metadata projection. ChatStore's bounded reader reuses unchanged-file boundaries; changed files receive streaming full validation. No second store. | §11/M7: adopt event reconnect contracts in Office UI and consolidate host composition/lifetime. Initial scope loading and changed-file validation remain O(history); optimize only through the canonical owner without weakening prefix integrity. |
| Existing document-owned HTML authoring | `HtmlWorkspacePublication` and Office UI still own old document results; CLI files have no dual writer. | M6: explicit materialization/linkage and guarded old-writer removal for cut-over authored types. |

The Office-only mode policy and question catalog/handler seam was removed on
2026-10-05. Office and CLI now use Core `ConversationRunPolicy` and
`UserQuestionToolCatalog/Handler` and `UserQuestionAnswers`; typed Office inbox and
CLI answer admission run under their application leases. Question state is derived
from the accepted result and kernel summary, with no second pending store.
The shared compiler projects semantic questions and preserves transport pairing.
The Office-only Task List service/catalog/handler path was removed on 2026-10-05.
Core `TaskListService`, `TaskListToolCatalog/Handler` and `TaskListPublication`
serve both hosts. The permanent Office wrapper owns DocumentAccessGate only;
workspace composition owns its existing session lease, journal, ChatStore save and
durable read-back. Both use the same conversation artifact/CAS ownership.

The Office-only Plan service/catalog/handler path was also removed on 2026-10-05.
Core `PlanDocumentService`, `PlanDocumentToolCatalog/Handler` and
`PlanDocumentPublication` serve both hosts. The existing artifact store supports
workspace authority for Plan records only, with distinct `WorkspaceId` ownership;
other document artifact guards remain. `OfficePlanDocumentToolHandler` owns the
bound gate. CLI Plan composition publishes through the same journal/CAS owners,
uses semantic exact reads and pins metadata against the frozen compiler snapshot.
It does not write a second Plan file or invent a document identity.

Explicit uncertain Task List/Plan reconciliation and file-backed Plan authoring
remain open. Typed Office answer admission is implemented: only the shared runtime
validator creates readable semantic question/answer text, including explicit skips.
Owner: conversation/planning application services.
Close each by reusing the existing publication/mutation owners, switching consumers
and preserving read-back and append-only history.

The ephemeral-only web manifest seam was removed on 2026-10-05.
`WorkspaceWebSnapshotStore` now publishes manifests and verification records through
the existing authority/CAS owners; CLI can reopen exact snapshots and list pending
records after restart. In-memory bytes are only bounded browser delivery buffers.
General preview UI, binary assets and functional verification remain open; see
[Resource Fabric](../resource-fabric.md#retained-web-project-snapshots--2026-10-05).

## Document artifact ownership — active slices

Typed compaction provenance (2026-09-08): `StructuredContextClaim`, existing
`ContextCompactionService` and `ModelContextCompiler` now retain kind/source roles.
`context-claims-v4` replaces untyped v3 replay admission; older checkpoints remain
preserved but skipped. Both first compaction and recompaction validate source
eligibility. No alternate summary parser, shared store or implicit cross-chat replay
is introduced. Document-owned publication is now implemented by the existing
`DocumentArtifactStore.Claims` facet; its consumers are compaction, document
provider/discovery, working-set links and the frozen model compiler. The raw archive
has no model bypass: direct projections mask it and compiler reads filter claims.
No temporary adapter or replacement physical store was added.

Resource decision context (2026-09-08): existing `ChatResourcePromptIndex` owns
bounded purpose/scope/next-read hints; optional description no longer participates
in mandatory target admission. Existing `ModelContextCompiler.Mark` retains a
semantic recovery target when excluding read evidence. Neither path reads bodies
or becomes an authority/currentness store; no adapter is introduced.

Markdown section selection (2026-09-08): the existing `common.resources_read` and
typed `ResourceReadRequest.Section` carry a semantic heading selector. The chat
provider and `DocumentArtifactStore.ReadMarkdownSection` resolve exact CAS text;
normal authority/evidence retains only selected character coverage. The duplicated
inline discovery heading parser is replaced by the shared Core scanner without
changing v1 index bytes. Other providers reject this selector; no adapter, separate
section store or mutation/evidence bypass is introduced. Whole reads remain a
distinct request. See the canonical [section contract](../artifact-library.md#implemented-markdown-section-reads--2026-09-08).

Exact text discovery (2026-09-08): `DocumentArtifactStore.SearchText` owns the
deterministic Markdown/Plan text view over existing revision/CAS ports. The chat
provider uses it instead of the old 128k prefix scan for these document resources;
Gateway projects heading context and preserves search-only authored descriptions.
Original extracted text now uses the same owner with an extraction-bound revision
view and explicit partial coverage; its 128k provider search prefix path is removed.
HTML member discovery now uses `ChatHtmlResourceCatalog` for exact member
resolution/serialization and `DocumentArtifactStore.SearchMemberText` for a derived
view under the published parent. Its document-member 128k search path is removed;
local artifacts/members keep their existing source paths. No temporary adapter or new
durable store; read guards still require the normal evidence path. Last-page
generation validation closes the indexing/read race. See the canonical
[text view contract](../artifact-library.md#implemented-exact-text-discovery-views--2026-09-08).

| Seam | Owner and consumers | Removal gate |
|---|---|---|
| Transient `ChatArtifact` projection over document-owned originals/Plans/HTML/Markdown | `DocumentArtifactStore` owns new originals, Plan, HTML/JSON and authored Markdown snapshots in existing authority/CAS; native Plan binding v3 and mutation observer publish per-Plan document heads. Ingestion, Gateway/provider, viewers, chat projection and fork consume document refs. Plan conversation-head publication and direct local-action mutation bypass are removed. No second store or document-ref fallback; old chat Plans are explicitly rejected for mutation | HTML identity/head/tools and working-set consumers are switched through the same record path and mutation observer; independent Markdown now uses the same authored-record path with prepared exact intent and no active singleton; slices 3–4 finish indexed discovery/recovery and remove remaining chat-only ownership. Individual metadata loss now uses transient unavailable projections; partial model discovery now isolates per-resource failures; bounded indexing and authority-wide recovery remain open. Originals/Plan/HTML/Markdown explicit selection/refresh/unlink now use `ArtifactWorkingSetService` and chat-event-backed `ArtifactLinks`; prompt/library/compaction, fork and clear consume these decisions. Incompatible stream handling stays explicit, without deleting user data. Windows delivery remains open |

Artifact metadata seam consolidated (2026-09-08): `DocumentArtifactStore` now uses
one private record retention/read path over its existing authority/revision/CAS
ports. The Plan-only record implementation is removed; Plan retains its persisted
view and provenance shape. The unregistered HTML-specific draft was discarded.
Shared HTML/JSON now uses the common authored-record path. HTML conversation-head
publication, member-URI chat rebasing, implicit export capture, dialogue rollback
and shared fork copying are removed from active consumers. Restore/redo advances
the document lineage. Unused direct restore/redo and ChatStore activation entry
points are removed; old navigation coverage uses test-only projection fixtures.
No temporary storage adapter or parallel store is admitted. Markdown adds a native
consumer of the same record/publication contract. Snapshot naming moved from the
HTML helper to `ChatResourceUri.CreateSnapshotId`; the replaced helper is removed.
ToolRuntime now passes its existing prepared state to the handler and observer
prepare/read-back calls while preserving the kernel execution identity and persisted
event format. No session-level MD store is used.

Model collection discovery now consumes bounded `InspectCurrentMetadata` source
pages over `ResourceAuthorityStore.ReadHeads` and the same ordered Heads projection.
Full snapshot copies, all-library metadata hydration and per-entry live-head selection
are removed from model list/search. Provider identity resolution now reads one exact
head/record, removing its all-history load. HTML member discovery uses only its
selected exact current workspace; the old standalone inline test fixture uses the
production Gateway/owner. Source cursors bind generation and preserve unavailable
slots; fresh scans recover earlier omissions. Gateway/search page ceilings avoid
unbounded filtered scans. No temporary adapter or separate durable index;
picker/history, semantic section reads and cold replay/write allocation
remain open. Markdown/Plan retained text views are described above.

HTML UI action seam removed (2026-09-08): the typed bridge passes complete
`HtmlWorkspaceActionPayload` guards to addressed/reserved controller actions.
`HtmlWorkspaceActionGuard` validates snapshot/session revision after reload and
under the existing mutation gate; scalar unversioned delete/select/restore/redo/
import/export signatures are removed. The UI captures before dialogs and guards
late responses against navigation/new edits. This adds no storage adapter and
does not move HTML ownership; that remains the active removal gate above.
Host-neutral guard/bridge/UI checks pass; bound-document delivery awaits Windows.

## Unified resource cutover — completed host-neutral removal records

Current closure scope is the [MASTER acceptance reconciliation](../resource-fabric.md#master-acceptance-reconciliation--2026-09-07).
The catalog-capture/final-freeze race, integrated mutation acceptance and final
consumer cleanup are complete host-neutral. Source-allocation limits are explicitly
user-deferred with owners/triggers in BACKLOG; Windows qualification remains open.
Finer Excel coverage and new checkpoints are optional, not unfinished replacement
paths or prerequisites merely because they could be improved.

| Replaced/pending contour | Canonical owner | Consumers / removal gate | State |
|---|---|---|---|
| Intent discovery drops terminal provider truncation and treats zero observed matches as absence | Existing Gateway intent enumeration/search → common.resources_find/read | Model/manual discovery and generic semantic-target resolution | Fixed host-neutral: full pagination remains complete; terminal source truncation survives filtering and later providers. Incomplete scans cannot report true empty, and incomplete enumeration cannot prove a unique target. Existing completeness fields and typed refusal are reused; no fallback or additional discovery store. Explicit domain resolvers/exact reads are unchanged; Windows qualification remains open |
| `document:` resolution scans every document catalog; bounded Excel names/tables fail discovery and metadata search loses truncation | Live document provider's exact kind → Gateway target validation; existing ExcelResourceProvider bounded list/search | Model/manual document reads and resource discovery | Replaced host-neutral: unrelated catalog availability no longer gates document identity; table/name discovery preserves terminal source coverage and coverage-bound continuation. Search preserves source/page/result clipping. Name/table point admission remains strict. No fallback, store or limit increase; reported workbook Windows retest remains open |
| Excel tables discoverable only as inspect metadata, with consumers binding fixed A1 ranges | Existing ExcelResourceProvider → typed table metadata/range capture → Gateway/CAS | Generic model/manual discovery/read and HTML named-table bindings | Switched host-neutral for ListObjects: bound workbook/name identity survives relocation/resize; extent is part of exact snapshot bytes. Shared text/formulas/profile and records at $.values, historical no-I/O, bounded unambiguous lookup and no former-address fallback. Ordinary explicit A1 resources remain distinct supported targets. Defined Names use the same owner below; finer coverage and real Windows qualification remain open |
| Defined Names available only as inspect metadata with unverified target sheet/address | Existing ExcelResourceProvider → typed name classification/range capture → Gateway/CAS | Generic model/manual discovery/read and HTML name bindings | Switched host-neutral: exact qualified name identity; only positive bound-workbook/single-area proof admits cell reads. Other targets remain metadata-only, without formula parsing or local-address fallback. Definition and extent participate in exact snapshots; records use $.range.values; retained reads survive live removal without Office I/O. No extra reader/store; real COM classifier, dynamic/external/multi-area behavior and finer impact/coverage qualification remain open |
| `excel.find_cells` direct adapter read and hash/full-cell output | ExcelSearchResourceService → existing Excel provider/Gateway/CAS → pure domain matching | Native/manual workbook/sheet/range/selection searches and exact source reads | Replaced host-neutral: complete bounded cell captures, zero-match drift, historical no-I/O, invalid/oversized capture refusal and bound-STA/closed-workbook checks. Shared paging retained; direct search method removed. Replacement owner unchanged; real Windows qualification remains open |
| `outlook.search_mail` direct folder read, EntryID/body output and `maxBodyChars` | OutlookSearchResourceService → Gateway/document provider/CAS → pure Outlook field matching | Native/manual search, exact source reads and evidence | Replaced host-neutral: bounded header/body-prefix projections, explicit folder/body truncation, zero-match drift, retained historical pages, header-only no-body capture and bound-STA/Inspector/closed-window checks. Duplicate search body/unused path removed; mutation owner unchanged. Real Windows/OOM/WebView qualification remains open |
| `powerpoint.search_text` direct adapter read and hash-only output | PowerPointSearchResourceService → Gateway/document provider/CAS → pure PowerPoint matching | Native/manual search, exact scope-source reads and evidence | Switched host-neutral: deck/slide and optional notes snapshots, zero-match drift, retained historical pages, bounded capture and bound-STA/closed-presentation refusal. Direct search branch removed; mutation owner unchanged. Real Windows/COM/WebView qualification remains open |
| Generic live search publication driven by matching snippets | Existing document/VBA providers → Gateway → shared authority/CAS | `common.resources_find`, exact match evidence and historical resource reads | Replaced host-neutral: typed captures include zero matches; complete views retained, partial scan coverage explicit, metadata body-free. Snippet-only publication removed without fallback. Real Windows qualification remains open |
| `word.find_text` direct adapter/backend read and hash-only search output | WordSearchResourceService → existing Gateway/document provider/CAS → pure Word matching | Native/manual Word literal/regex search, scope-source discovery/read and model evidence | Switched host-neutral; same semantic tool, exact main/selection/all story snapshots, zero-match drift publication, bounds before text capture, retained historical pages and bound-STA/closed-document refusal. No direct search fallback; other hosts have their own completed rows; real Windows qualification remains open |
| Public `outlook.collect_mail`, direct folder JSON/preview output and monthly wrapper | Existing document provider + OutlookService.CaptureCollection → shared authority/CAS → text/records/data plane | Model/manual collection, HTML folder bindings and accepted-call replay | Removed host-neutral; bounded exact folder projection, explicit body/folder truncation, semantic month field, no locators in body, retained records after membership drift and Inspector refusal. No new store/alias. Real Windows/OOM/WebView qualification remains open; field search is covered by its own row above |
| Whole-binary resource responses and image elements using capability URLs | Existing Gateway/CAS → resource data-plane chunks/shared transfer budget → verified browser consumer | Image/PDF/thumbnail viewers, raw HTML bindings and standalone export | Replaced host-neutral: 256 KiB sequential byte chunks, separate whole-payload limits, single verified capture, busy-close quota retention and completion release. Viewers use revocable verified blob URLs; RN.resources and export v2 consume chunks. Raw admits exact attachment bytes, stored file/JSON/Markdown/plan/task-list/chart originals and workspace file sources through one provider capture port; the viewer raw-reader is removed. Retained metadata must have a matching CAS part; missing/corrupt metadata fails canonically without recapture or authority repair. No inline-body reconstruction, broken-attachment fallback, whole-response fallback or second store. Binding metadata is not raw bound data; no blanket raw expansion to every provider is required. Real Windows qualification remains open |
| Inspector inline request JSON bridge body | PromptContextInspectorDownloadService → shared resource data plane/reader | Opt-in Inspector raw viewer | Removed host-neutral; reserved 2 MiB UTF-8 preview, metadata-only setup, verified chunks and single cancellable UI cache. No new publication/store; raw serialization now stops at the 512,000-character boundary with bounded escape buffers, without a full intermediate JSON string. Real Windows/WebView2 qualification remains open |
| Unused runtime log inline bridge tail/clear | RuntimeLog keeps file writes; WebView-session log keeps its existing UI owner | No runtime-file UI consumers; old bridge commands rejected | Removed host-neutral: commands, controller methods, DTO and exclusive read/clear helpers deleted. No new download/diagnostic feature; existing log files and UI log untouched |
| Removed Outlook direct reader/output wrapper and generic document/selection source fallback | Existing document provider → `OutlookService.CaptureMail` + header discovery → bound backend/Gateway/CAS | Model/manual resources and HTML mail bindings; public id/binding/output wrapper removed | Consumer switch complete host-neutral 2026-09-06. Inspector/folder-scoped mail targets, retained source evidence and body-free metadata; OOM pre-materialization and real Windows gates remain open |
| Per-session VBA observed hashes and refresh callbacks | Shared ResourceAuthority + immutable conversation evidence | VBA guard/read-back, next model request | Removed; no parallel hash authority |
| VBA restore without exact origin metadata | VBA journal preimage reference → prepared restore → shared authority commit | Restore confirmation/read-back | Exact live origin or exact independent backup pinned before confirmation; no history search by equal hash |
| Mutable/untyped DocumentContext text injected into the model | ContextNoteRole → ModelContextCompiler + CAS/evidence reducer | Context bridge, normal request, Inspector, clone/upsert | Removed; explicit user instructions separate from supplied data/Office observations; untyped notes require reattachment, no preview fallback |
| Context exact refs unavailable to generic reads; partial state view or equal hash used as a whole revision | ContextResourceProvider + shared ResourceSnapshotReadService over authority/CAS | resources_find/read, data-plane leases, copied context | Replaced host-neutral; typed scope/role discovery, whole payloads, logical revision cursors and historical reads without publication. Instructions remain outside resource discovery |
| Semantic read reuses a stale discovery head or floats across internal pages | ResourceReadToolHandler → same Gateway | Model/manual resource reads | First mutable read captures the current head, subsequent pages use its exact revision; old discovery observation is not a new current-read guard |
| Physical content-hash continuations at the live/retained Office boundary | ResourceGateway + ResourceAuthorityService + shared snapshot reader | Live Office/VBA views, PrepareRead/ReadRetained | Replaced host-neutral; exact logical tokens translate to private physical guards only at dispatch. Separate retained reader removed; exact CAS access never queries Office or falls back when payloads are missing |
| Catalog text continuation bound to payload hash; prepared metadata treated as readable publication | CatalogResourceProvider + CatalogPublicationService + shared cursor/reducer rules | Catalog/skill/reference reads, capability reference next and data-plane leases | Replaced host-neutral; exact publication tokens, commit-proven visibility and typed missing/corrupt CAS errors. Child currentness follows root dependencies without synthetic heads; historical reads cannot activate a generation |
| Collection-style continuation validation and lowercased projection bindings in structured/derived reads | ResourceStructuredViewService + ResourceDerivedViewService + shared exact cursor rules | Record/table projections and virtual derived reads | Replaced host-neutral; explicit exact refs and case-sensitive path/field bindings checked before capture/hydration. Gateway cannot silently pin a floating artifact continuation; unsupported virtual paths fail explicitly |
| Identity-level head or cached index admits a prepared exact revision; separate catalog/fork publication checks and raw CAS failures | ResourceAuthorityService publication proof + ResourceSnapshotReadService payload reader | Retained text/index/virtual views, catalog reads and fork preparation | Replaced host-neutral; exact commit proof, with existing fork provenance admitted only after its verified commit. Historical non-head copies remain readable; missing/corrupt CAS fails explicitly without republishing or fallback |
| HTML accepted tool-result binding/current dataset JSON | ResourceRef bindings → Gateway/data plane | HTML bind/refresh, RN.resources | Removed; no workspace bulk state |
| Detached HTML export with host-required resource reads | HtmlWorkspaceExportService → same Gateway/data plane → inert exact snapshot transport | Export action, standalone RN.resources | Replaced host-neutral; bounded/integrity-checked table, text and binary parts, no head store or bridge bodies. Preview/export head currentness uses the common reducer including dependencies. Windows/downloaded-file qualification remains open |
| Viewer text/base64 bridge bodies | ResourceDataPlaneService → Gateway/CAS views | Text, images, PDF pages/thumbnails | Removed; metadata-only bridge, bounded leases and close/cancel lifecycle |
| Uploaded-HTML source bridge/body/cache and direct attachment import read | ArtifactViewerService/shared data plane; UploadedHtmlResourceService → exact Gateway pages | Inert original source viewer and explicit HTML workspace import | Removed host-neutral; one viewer cache/lease lifecycle, separate original/text hashes, bounded complete integrity-checked import before mutation. Original/provenance/active-workspace guards remain; real Windows source/import qualification open |
| Attachment base64 staging control message and decoder | ResourceDataPlaneService upload leases → existing ChatResourceIngestionService/AttachmentStore | File picker/clipboard, draft ingestion and pre-send barrier | Removed host-neutral; metadata-only begin/complete/cancel and sequential binary POST chunks, bounded shared reservations, exact chat ownership and no retry of uncertain completion. CAS/linking stays at send; real Windows request-stream/CORS/PDF-close qualification remains open |
| Trajectory ZIP base64 control body and decoder | Existing TrajectoryExportService → TrajectoryExportDownloadService → shared resource data plane | Diagnostic export and download UI | Removed host-neutral; reserved bounded capture, exact sequential binary chunks, shared upload/download quota and pre-download SHA-256 verification. ZIP remains disposable, with no published head or second store; redaction is unchanged. Windows/WebView2 download/cancel qualification remains open |
| Event payload inline control body and full-read-then-clip preview | TrajectoryPayloadService → existing CAS streaming verifier → shared resource data plane/reader | Raw event preview and Run Journal body callback | Removed host-neutral; exact addressed event/complete stream, bounded verified UTF-8 prefix, separate source/preview hashes and cancellation including late responses. No publication or second store. Windows/WebView2 qualification remains open; cold-replay optimization is optional |
| Direct VBA editor module read and source-in-DataJson bridge | VbaEditorResourceService → Gateway/VbaResourceProvider → existing CAS and shared data plane | VBA source editor; model/HTML exact source continuations share the same retained view | Removed host-neutral; one bounded live capture retains complete source, exact reads survive drift without COM, partial source cannot become editable. Normalized write guard remains separate from raw byte hash. Existing mutation owner/save semantics unchanged; actual Windows editor/STA/WebView2 qualification open |
| Inline VBA save/create source DTOs and attachment-only browser chunk loop | VbaEditorResourceService + shared upload router/browser writer → existing manual mutation owner | VBA save/create and attachment ingestion | Removed host-neutral; single-use chat/consumer-scoped upload, complete UTF-8/raw SHA-256 verification, mandatory editor save guard, bounded capacity and no automatic replay. Uploads neither stage attachments nor publish VBA. Windows save/create/cancel qualification remains open |
| Mutable-disk skill reference activation | CatalogPublicationService → exact CAS resources | Skill catalog, capability reads, compiler, Inspector | Removed; publication dependencies control freshness |
| Skill Library direct authoring-file reference reader, inline core catalog bodies and separate reference-only bridge | SkillEditorResourceService → published CatalogResourceProvider/Gateway/CAS → shared download | Library core/reference editor, built-in preview/clone/context-copy | Removed host-neutral; one exact bounded reader, metadata-only catalog, fail-closed editing and selected-source clean cache. Dirty conflicts/late edits are retained. Metadata-only mutations explicitly preserve the body under the existing complete package guard/commit barrier; no read alias or new store. Windows Library/WebView2 qualification open |
| Inline Skill Library core mutation batches/reference upserts and reference response source echo | SkillEditorResourceService → shared single-use upload → existing SkillAuthoringService/commit barrier | Library Save and reference Save | Removed host-neutral; chat/lease/hash-only control, bounded typed UTF-8 JSON and whole-batch structural/body validation before sequential guarded writes. Shared capacity, one UI writer, frozen drafts, late-lease cleanup and no automatic replay; successful sources reread published snapshots. No parallel inline path or new store. Windows Library/WebView2 qualification open |
| Independent history/repair/free-summary authority | ModelContextCompiler + EvidenceStateReducer | Normal request, repair, compaction, Inspector | Removed in compiler path. Catalog generation from the active publication is now checked against the final frozen authority tuple; mismatch refuses before dispatch, fresh binding restores progress. Repair retains original catalog/settings/budget. No new builder, current-head fallback or automatic replay |
| `common.tools_definition_read` direct authoring-file read, catalog entry and native binding | Existing CatalogResourceProvider source children → common.resources_find/read → shared evidence/reducer | Model inspection before Tool authoring | Removed host-neutral; metadata-only host-visible discovery, bounded exact source/CAS and root-publication dependency. File drift cannot replace the published body; later publications supersede old evidence without deleting historical snapshots. No schema admission, alias, second store or automatic write. Old accepted calls explicitly fail model-history validation |
| `common.prompts_read` direct mutable-settings reader, include-defaults response and native binding | Existing CatalogPublicationService/Provider → exact prompt/default fields → common.resources_find/read | Model prompt inspection/comparison before confirmed save | Removed host-neutral; key-only discovery, complete bounded values and root-dependent evidence from existing CAS. Builtin defaults publish at startup; read never reloads settings, regenerates defaults, activates or resets them. Current saves supersede old evidence without changing defaults/history; old accepted calls are rejected. Prompts UI transport is switched in the row below |
| Inline generated builtin documentation and state-wide Markdown/request caches | CatalogPublicationService → exact catalog documentation child → ToolEditorResourceService/shared download | Selected Tool Library Docs tab | Removed host-neutral; source-owned policy generates committed CAS parts, metadata-only chat/id/revision controls, 2 MiB complete verified download, no Office discovery or generator fallback. One selected cache, bounded pending opens and late-lease/cancel cleanup. Execution registration/model catalog are unchanged; Windows WebView2/Office qualification open |
| Inline Tool Library mutation batch in Save and pre-install save | ToolEditorResourceService → shared single-use upload → existing ToolAuthoringService/commit barrier | Library Save and save-before-VBA-install | Removed host-neutral; chat/lease/hash-only control, bounded typed UTF-8 batch, existing sequential guarded writes. One UI writer, exact submitted-draft acknowledgement, cancellation/late-lease cleanup; failed or unconfirmed save cannot continue into install. No replay, second store or inline fallback. Windows Library/WebView2/Office qualification open |
| Inline Tool Library schema/code/README/components in catalog and outgoing library projections; whole-catalog UI serializer | ToolEditorResourceService → existing catalog/VBA Gateway/CAS → shared download | Selected custom/builtin/document-local source editor; Init/chat/catalog/mutation/package responses | Removed host-neutral; hash/extent-only catalog, exact revision/addressed read, capacity before hydration, complete verified body, selected clean cache and conflict-preserving drafts. Custom/builtin children depend on committed catalog roots; document-local reads prove exact VBA components without another authority. Canonical/raw VBA collision is fixed: changed exact bytes create a new logical revision without changing normalized mutation guards or historical CAS. Windows Library/WebView2/Office qualification remains open |
| Controller clear/edit/message-delete/fork bypass of authority | Chat history/clone owners + typed intent → existing journal/authority commit | Chat/workspace lifecycle | Bypass removed; atomic active-state publication and retained history checked host-neutral. Fork requires bounded exact copy preparation, then one authority publication |
| Blanket rejection or implicit alias for bound definitions in forks | ResourceForkService + exact copy provenance in conversation events | Schema/mapping/derived bindings, supplied context, nested forks and workspace restore | Replaced by bounded exact graph copying; original snapshots remain immutable. Missing/unpublished dependencies fail before publication; no cross-chat access fallback |
| Init/settings inline prompt bodies and whole-form prompt serializer | PromptEditorResourceService → existing catalog/Gateway/CAS and shared transfers → guarded settings writer/commit barrier | Selected Prompts UI source, Save/Review/explicit reset | Removed host-neutral; typed controls plus exact metadata, bounded source download, changed-only single-use upload, pre-dispatch publication/live-template guard and preservation of unloaded bodies/dirty edits. No inline fallback, new store or replay; Windows settings/DPAPI/WebView2/multi-window qualification open |
| HTML workspace file/data Save/create inline bridge bodies | HtmlWorkspaceEditorResourceService → shared uploads → existing HtmlWorkspaceToolService / mutation journal and authority | All four HTML/CSS/JS and JSON UI writes | Removed host-neutral; single-use UTF-8/hash-checked source, exact displayed-workspace and Known logical-publication guard before dispatch, original domain limits and commit owner. Dirty/late drafts survive Save acknowledgements; no inline fallback or replay. Source readers are switched in the row below; Windows/WebView2 qualification remains open |
| Outgoing workspace file bodies in init/chat/mutation/export responses | HtmlWorkspaceEditorResourceService metadata/download → Gateway/ChatHtmlResourceCatalog → existing CAS/data plane | Selected HTML editor, complete preview and export source assembly | Removed host-neutral; exact member metadata only, one bounded source producer, strict complete UTF-8/hash/length checks, original-owner cancellation/late-close and explicit reload. Unloaded bodies cannot become empty drafts; dirty source stays pinned across newer metadata. No second source store/reader or inline fallback; real Windows/WebView2/export qualification remains open |
| Definition/domain reads and bulk upload/export closure | Existing Gateway/providers + shared data plane | Model/manual reads, authoring, viewers, import/export UI | Complete host-neutral in the named rows above: consumers switched and their replaced readers/inline transport removed. Final source scan finds retired tool ids only in explicit incompatible-history refusal. No blanket remaining domain-read work package; real Windows/UI qualification and explicitly deferred source allocation remain |
| Full history/cold replay and conservative CAS retention | Existing event store/CAS + rebuildable projections | Long histories, compaction, GC | Journal-tail catch-up and conservative retention roots implemented; exact missing/corrupt historical payloads fail closed. New checkpoints/retention optimization are optional deferred work, not unfinished cutover |
| Cross-process notifications and real provider/WebView behavior | Shared authority + HostRuntime/WebView | Office/desktop windows | Fresh captures preserve correctness; Windows evidence open |

Правило удаления: [master plan §15.1](STABILIZATION_MASTER_PLAN.md#151-обязательная-локальная-чистка-после-каждого-подэтапа). Заменённый путь удаляется в подэтапе переключения последних consumers после проверки; Phase 10 — только финальная структурная сверка. Совместимость со старыми чатами не является причиной удерживать adapter. Статусы ниже описывают зафиксированную реализацию, а новые removal gates — план, не выполненное удаление.

Подготовительные выделения выполняются только в своей фазе по [§15.2](STABILIZATION_MASTER_PLAN.md#152-рефакторинг-который-облегчает-миграцию). Указанные ниже точки проверены чтением кода; это ориентиры для проверки актуальных consumers перед работой, не разрешение начать следующие фазы заранее.

| Current path / policy | Target | Owner | Consumers сейчас | Switch / removal gate | Статус |
|---|---|---|---|---|---|
| Public tool arguments carry provider/kind/URI/revision/cursor/guard plumbing; built-in skill bodies teach the same choreography | Minimal semantic intent schema + typed runtime preparation context over the same ToolRuntime; exact tool/skill consumer switch | Tool family owner + Resource Fabric/ToolRuntime for resolved state | Model callable packs, capability-loaded skills, Library Test, current Common tools and their host/custom consumers | R61: property inventory, then atomic per-family tool/schema/skill switch; delete replaced public arguments/ids and keep exact guard/evidence internally. [Audit](R61_TOOL_CONTRACT_AUDIT.md) | 11O1 Resources + Capabilities, 11O2 planning, 11O3 HTML, 11O4 Prompt/Tool/Skill authoring and 11O5 VBA/macro switched host-neutral; final reviewed inventory is 66 unique ids / 66 host variants. Dynamic package rationale is enforced on authoring and load. Core-pack/UI implementation through 11O7 and the Resource consumer rows above is complete host-neutral; real Windows/model qualification stays open. No adapter or dual schema admitted |
| AGENTS/README: bump + tag после commit | Release-only versioning | Release process | Нет consumers старого правила | Phase 0: правила заменены | removed |
| ValidateRNAssistantVersion | ValidateVersionFormat + отдельные release gates | Build | Нет callers старого target | Phase 0: callers обновлены, старый target удалён без alias | removed |
| AssistantController orchestration, включая `.Agent` confirmation | Application Facade → общий kernel | Application | Bridge, chat lease, document/preflight guards | 3B2: direct confirmed execution/accounting удалены; actual controller компилируется в MockDemo, live Windows delivery остаётся gate | switched; no alternate execution path |
| `Services/ConversationRunService.cs`: прежний loop | Core/Agent/AgentKernel + Office ports | Runtime / Application | Agent/Chat/Plan, confirmation | 3B2: старый RunLoop/ContinueAfterToolAsync удалён; service только orchestration | switched; old loop removed |
| `Core/Agent/AgentKernel`, RunSummary, ToolExecutionRecord | Постоянный kernel и runtime truth | Runtime | Production start/confirmation, pure/integration harness | 3B2: единый учёт, локальная lifecycle classification, current accepted IDs и captured limits | active; shared owner |
| `Core/ModelProtocol/IModelProtocol.SendAsync` | ConversationKernelAdapter.Model → ConversationModelSession/client | Application / ModelProtocol | AgentKernel | 3B2: metadata, prompts, media и working set остаются снаружи; v3/retry implementation не меняется | active; no second parser |
| `IMaterializedModelProtocol.GetResponseAsync` (имя до 3B1: IModelProtocol) | Текущий materialized endpoint boundary | ModelProtocol | ModelProtocolClient, ConversationRunService factory, harness | 3B1: все typed callers переименованы, старой GetResponseAsync-сигнатуры под IModelProtocol/alias нет; 3B2 меняет caller, не retry implementation | permanent boundary; rename complete |
| `Core/Persistence/IRunStore`, AgentRunContinuation | ConversationKernelAdapter.Store → existing typed events | Runtime / Persistence | Kernel, real ChatStore replay | 3B2: KernelState в run.updated; full-history continuation; mandatory appends + private cursor/global CAS; Phase 9 расширяет matrix | active; minimal R11 gate covered, Windows/full matrix open |
| `Services/ConversationModelSession.cs`: prompt/compaction, working set, request cache/options, bounded results и media | Office application-owned model context вне kernel | Application / Model context | ConversationKernelAdapter.Model/Store; PromptContextInspectorService использует общий options builder | Phase 3A: callers переключены, старые helpers удалены; Phase 3B2: model port переключён на этот owner; Phase 8 меняет внешние ToolPack/resource implementations | permanent owner; не compatibility adapter |
| Tool-activity construction, chart provenance и HTML checkpoints внутри loop | `Agent/AgentTranscript.cs` | Application / Transcript | ConversationKernelAdapter.Store, controller/manual tools; model session использует общий ResourceRef clone helper | Phase 3A: прежние inline helpers удалены, activity/result ordering сохранён; Phase 9 меняет projection по своему gate | moved to existing owner; no duplicate path |
| Loop CompleteAsync/parser/format retries/fallback/trace helpers; `AgentJsonProtocol.CreateFormatRepairMessage` | `Core/ModelProtocol/ModelProtocolClient.cs` | ModelProtocol | Старых callers нет; loop использует IMaterializedModelProtocol | Phase 2A: переключено и физически удалено без aliases; имя materialized port уточнено в 3B1 | removed |
| Initial + MaxAgentFormatRetries; fallback только до первого repair | Total 1–20 protocol responses + отдельные provider retries/fallback | ModelProtocol | ModelProtocolClient, loop только как typed caller | Phase 2B: старый control flow заменён, лишняя попытка удалена | removed |
| Duplicated Office response options, JSON call/probe envelopes и direct probe parser/status checks | Core/ModelProtocol/ModelProtocolWire | ModelProtocol | ModelProtocolClient, ConversationModelSession, AgentJsonProtocol, ModelCompatibilityService | Phase 2C3A: все callers переключены, AgentOptions/manual call-envelope paths удалены; shared owner остаётся при v3 switch | removed duplicates; permanent shared contract owner |
| NormalizeAgentPrompts mismatch reset, duplicate Chat/Plan defaulting, UI marker fallback/omitted Plan prompt | Preserve authored text + explicit schema review | Settings / ModelProtocol | SettingsService, typed saveSettings bridge, settings/prompt UI; controller start/confirmation и neutral loop guard | 2C3B удаляет destructive path; 2C3C проверяет старую schema 11 и explicit review/reset на actual v3 defaults/schema 12; Windows controller/DPAPI qualification остаётся | removed destructive path; permanent review precondition, не adapter |
| Settings/bridge key `MaxAgentFormatRetries` | Тот же ключ, теперь total protocol responses | ModelProtocol / Settings | SettingsService, bridge, static settings form, retry budget | Phase 2B: значение не переписывается, caption уточнён; стабильный ключ остаётся | retained contract, не adapter/alias |
| `ModelProtocolFailure.Cause` → exception rethrow | Typed kernel failure/lifecycle | Runtime / Application | Старых consumers нет | 3B2: Cause, rethrow и obsolete assertions удалены | removed |
| `Services/RunSummaryBuilder.cs`: effect aggregation | Core RunSummary/ToolCounts | Runtime | Старых consumers нет; normal/confirmed execution учитывает kernel | 3B2: builder и ручной seed/observe/publish удалены | removed |
| Legacy result/outcome fallback and captured mutable safety | Typed ToolRuntime evidence + strict `ToolRunResult` v1 UI projection | ToolRuntime | Нет active consumers | 4B switched model wire; mandatory 11T/11J/11K switched every family, and 11T10 deleted the final fallback/converters | removed host-neutral; historical missing evidence remains exact/unknown and is not reinterpreted |
| `RunExecutionSummary` на messages/run/bridge и его getter projection | Immutable `RunViewState` из `KernelState` + source-owned effect evidence | Application / Persistence / UI | Старых active consumers нет | 9D5: application/bridge/catalog/UI switched atomically; тип, поля, clone/getter и JS readers удалены. Retained raw JSON fields игнорируются и не дают authority; incompatible run требует explicit new chat/reset | removed; no fallback/backfill/dual projection |
| Model-owned call ID / v3 wire DTO + accepted-ID parser context | ID-less v4 draft → kernel allocation → accepted ToolCall + origin | AgentKernel / ModelProtocol | Normal/Chat/Plan, confirmation, accepted history/native transport, probes; старых runtime consumers нет | R29: удалены wire ID fields/schema/prompt uniqueness, parser ID set и мёртвый AgentJsonProtocol.ToCommand; origin сохраняется в existing commit. V3 history — explicit reset/skip | removed; historical v3 spec только evidence, no alias/ID rewrite/dual-write |
| conversation-response v2 / AgentResponse DTO, Core/Tools/AgentResponseParser и AgentResponseSchemaBuilder | Core/ModelProtocol/ConversationResponse + parser/schema; runtime summary отдельно | ModelProtocol | Старых consumers нет; ModelProtocolWire/client/kernel adapter/probes/prompts/writes используют v4 (R29) | Phase 2C3C: переключены все consumers и marker, удалены DTO/parser/schema/includes и obsolete tests; старые чаты — explicit skip/reset | removed; no v2 fallback/dual-write |
| ConversationResponseV2Adapter / legacyV2 branch в ConversationResponseJson | Current-v4 history reader; несовместимые старые чаты — explicit skip/reset | ModelProtocol | Runtime consumers не было; obsolete harness consumers удалены | Phase 2C2: adapter/branch/include/obsolete tests физически удалены; 2C3C добавляет full-history preflight до подготовки/dispatch | removed; no automatic fallback |
| Response-local IDs / Office transient accepted-ID bookkeeping | Kernel accepted-turn IDs + full-history restore | Runtime / ModelProtocol | Kernel → materialized ModelProtocolCallContext | 3B2 removed mutable Begin/ObserveAccepted; 4A replaced central read-id list with source-owned ToolPolicy | old bookkeeping and name list removed |
| ConversationProtocolContext.ReadCurrentV2CallIds | ConversationResponseHistoryReader только для current-v3 history | Runtime / ModelProtocol | Старых consumers нет; RestoreContinuation читает полную actual v3 history | Phase 2C3C: typed-ID helper удалён вместе с v3 writer/version switch; old chats не мигрируются | removed |
| Controller EnsureCurrentResponseProtocol: LastRun-only guard | ConversationProtocolContext.EnsureCurrentHistory / EnsureCanContinue | Runtime / Application | Send/edit/retry preparation, manual compaction, pending confirmation и neutral service entries | Phase 2C3C: helper удалён, все эти entries используют full-history reader (current v5; R29 was the prior v4 correction); Windows wiring qualification остаётся | removed old helper; permanent preflight, не adapter |
| AgentResponseStatuses / LastRun lifecycle projection | Runtime-owned `KernelState` → immutable `RunViewState` | Runtime / Application / Persistence / UI | Retained response status is accepted-history/diagnostic metadata only; bridge/UI consumers switched | 3B2 removed model/status runtime authority; 9D5 removed bridge/UI status inference and flat catalog fields. Native refusal remains explicit provider metadata mapped by kernel | switched host-neutral; no model-status effect/lifecycle authority |
| ConversationProtocolContext: transient accepted-ID bookkeeping | AgentKernel run context | Runtime | ConversationRunService start/confirmation → ModelProtocolRequest.CallContext | Phase 3B2: один kernel owner IDs; model adapter материализует snapshot; full-turn/compaction/confirmation tests сохранить | adapted 2C2; текущий owner до switch, не durable index |
| ConversationProtocolContext.LocalReadIds + mutable safety projection | Source-owned `ToolPolicy` captured in `ToolPackSnapshot` | Runtime / ToolRuntime | Snapshot and batch admission only | 4A removed the central name list; 11T10 removed the last generic projection and now rejects catalog entries without exact policy/binding | removed; batch permission comes only from captured policy, never id or mutation flags |
| Accepted `LlmCompletionResult` / context-usage object / separate ProviderRefusal в ModelProtocolResult | Office model-context/transcript metadata вне kernel | ModelProtocol / Application | ConversationRunService → AgentTranscript и current turn result | Phase 3B2: adapter сохраняет accepted metadata вне kernel; generic port получает только response/failure/native refusal | current metadata bridge; no rejected completion |
| `Services/RunCausalTrace.cs`, ModelTracePersistenceService и trace hooks текущих loop/executor/journal/controller | Наблюдение границ выделенных ModelProtocol / AgentKernel / ToolRuntime / domains / Application | Diagnostics / Application | Текущий loop, top-level executor, VBA journal wrappers, send/confirmation projection | Перенос hooks вместе с consumers в Phases 2–6/9; заменённые hooks удалить в том же подэтапе, historical events сохраняются | introduced 1B; только logging scope, не compatibility runtime |
| Former broad `OfficeToolExecutor` validation/safety/generic domain dispatch | Typed composition/manual façade over `ToolRuntime` + domain handlers | Tools / HostRuntime | Kernel runtime construction and manual typed invocations | 4A/5 moved runtime/access ownership; mandatory 11T/11J/11K switched every handler, and 11T10 deleted the final generic host/result fallback | switched host-neutral; façade retains composition only, no second execution authority |
| Executor-owned expected-document scope, mutation locks, live-read depth and leases | `src/RNAssistant.Office/Runtime/HostRuntime.cs` | HostRuntime | OfficeToolExecutor, resource gateway, HTML data access, manual VBA/editor | 5A: callers switched; former helpers/fields and System.IO import removed. HostRuntime is the continuing owner, not a parallel executor | removed old ownership; existing behavior preserved |
| HostRuntime global fallback monitor / per-instance AsyncLocal read depth / preparation before document gate | `Runtime/DocumentAccessGate`: synchronous operation + target ownership, document → shared order | HostRuntime | Normal/manual execution, resource gateway, editor/HTML reads; native list has a separate root | 5B1: callers switched; old monitor/depth/lease implementation removed. Reentry, cancellation, failed-acquire cleanup and guard/read serialization verified host-neutral | replaced; no parallel depth/fallback path |
| HostRuntime stable-key lock identity; wrapper stable-key OR runtime-key checks | Bound IOfficeDocumentSession + common runtime lifetime identity | HostRuntime | No remaining production Office host consumer | The former WQ-A3 decoder/lease was removed with Qualification Center; the current Windows runbook owns WQ0 evidence. 11T0/7D binds Excel, 11T6 Word, 11T7 PowerPoint and 11T8 Outlook, capturing each current `RuntimeKey` for one exact object/window lifetime and removing execution fallback | removed host-neutral for all four hosts; WQ0/WQ-SESSION/R04 evidence remains open |
| Direct selection/context capture and document VBA catalog reads | HostRuntime.ReadDocument + OfficeContextCaptureService | HostRuntime / Application | AssistantController.AddSelectionContext/Initialize; ToolCatalogService | 5B2 read switch: separate operation roots; catalog cache/list/components share gate, failed backend reads cannot publish empty/partial cache; capture returns before persistence. Production bound identity/UI qualification remains 5B2 Windows | old direct controller capture and catalog guard-only scope removed; no parallel read path |
| Removed `LegacyToolResultAdapter` | Native typed terminal results from domain handlers | ToolRuntime | Нет active consumers | 11T10 deleted the final generic conversion after all domain/authoring switches; old history is never read through an adapter | removed host-neutral; model writer remains Tool Result v1 only |
| Removed `ToolResultUiProjection` / legacy Activity DTO | `ToolRunResult` v1 + `RunViewState` / typed Activity evidence | Application / UI | Manual commands, bridge and Activity projection | 11T10 switched the remaining presentation consumers to typed runtime records and deleted the legacy UI projection | removed host-neutral; no native→legacy roundtrip or response-shape fallback |
| Removed `LegacyToolDefinitionAdapter.Adapt` / `PolicyFor` / `BindingFor` | Source-owned direct catalog plus immutable execution authority | ToolRuntime | `ToolPackSnapshotFactory`, runtime registry and callable catalog | 11T10 made exact `ToolPolicy`/`ToolBinding` mandatory on `ToolCatalogEntry`, consumes them directly when capturing the snapshot and deleted the generic adapter | removed host-neutral; no projection, id-derived fallback, alias or second authority |
| Removed `src/RNAssistant.Core/Models/ToolModels.cs`: definition/command/result DTO family | `Core/Tools/ToolCatalogEntry`, `ToolInvocation`, descriptor/policy/binding/package plus Office `ToolRunResult` v1 | Tool contracts | Catalog/storage/schema, accepted local invocation, runtime/model materialization and typed bridge | Mandatory 11T/11J/11K switched every consumer; 11T10 split mutable catalog/input/UI shapes from immutable runtime authority and deleted the old file/path | removed host-neutral; current contracts only, no legacy alias or dual-read |
| Host adapters / текущий выбор target | Bound DocumentSession + HostRuntime | HostRuntime | No remaining unbound Office host consumer | Excel production binding switched in 11T0/7D, Word in 11T6, PowerPoint in 11T7 and Outlook in 11T8; real Windows fault/lifetime tests remain deferred evidence | removed host-neutral for all four hosts; WQ0/WQ-SESSION and host packs remain open |
| `Office/Runtime/DocumentIdentity.cs` | `OfficeHosts/Identity/DocumentIdentity.cs` | Host identity | Excel/Word/PowerPoint adapters; harness identity cases | 10B1 `git mv` + namespace/project/harness update only; do not alter WQ0 identity semantics or production factories | moved 10B1 host-neutral; old source/project include removed, no alias or Office consumer; Windows/WQ0 qualification open |
| `Office/Vba/VbaProjectSupport*.cs` | `OfficeHosts/Vba/VbaProjectSupport*.cs` | Host VBA backend | Excel/Word/PowerPoint adapters; harness fake-VBE cases | 10B2 `git mv` + namespace/project/harness update only; typed VBA domain/journal/guards and COM algorithms unchanged | moved 10B2 host-neutral; old sources/includes removed, no alias/Office consumer; package marker parser is an explicit public Office.Vba contract, Windows/VBE qualification open |
| `Office/Runtime/AssistantRuntime.cs` | `Office/AssistantRuntime.cs` root application façade | Application / UI lifetime | VSTO add-ins, desktop, in-process panel | 10C1 physical move + production old-style project include only; no composition-root refactor or lifecycle change | moved 10C1 host-neutral; byte-identical, old path removed, Windows Office/WebView lifetime qualification open |
| `VbaToolExecutor.Patching` text replacement / `VbaToolManifestParser` normalization and text hashes | Core/Tools/VbaPatchEngine + VbaTextCanonicalizer | VBA / Core | Manifest parser, ToolStore/VbaJournalStore, Office guard/read-back/package/catalog, VbaProjectSupport, fake tests | 6A: all callers switched; previous normalization/count/newline implementation removed, no compatibility aliases; exact algorithm extraction verified | text switch done host-neutral; separate R33 replaces non-overlapping count in the same owner, no fallback; Windows/VBE regression pending |
| `VbaToolExecutor`/`ToolCatalogService` duplicate internal read builders, raw project/module parsers and name helpers | `Office/Vba/VbaReader` typed project/module snapshots | VBA / Office | Mutation guard/verification/reconciliation/package probes, live resource adapter, document-tool discovery/cache | 6B: all active backend read consumers switched; malformed success fails closed, valid empty project remains cacheable. Old builders/parsers/helpers removed; resource ToolResult projection remains executor-owned until its domain handler switch | read switch done host-neutral; no dual-read/alias. HostRuntime gate and host COM unchanged; Windows/VBE qualification pending |
| `VbaToolExecutor` apply-patch/whole-write/rename/delete/restore workflows and duplicate module/compound journal/read-back helpers | `Office/Vba/VbaMutationService` + `VbaVerifier` | VBA / Office | `common.vba_apply_patch`, `common.vba_write_module`, `common.vba_rename_module`, `common.vba_delete_module`, `common.vba_restore_backup`; reconciliation assessment | 6C moved patch/common ownership; 6D typed the boundary; 6E moved whole-write; 6F moved delete; 6G moved restore; 6J moved rename guard/two-name preparation/backend/read-back/recovery and removed the final compound executor path; 11O5 separated its public intent | module mutation switches done host-neutral; no second store/dual-write. Package moved separately in 6I; Windows/VBE qualification pending |
| Removed `VbaMutationDocumentContextAdapter`, `VbaMutationReaderAdapter`, `VbaMutationBackendAdapter` and host VBA command/result branches | `IVbaHostBackend` + bound `VbaInteropBackend` | VBA / OfficeHosts / Tools | `VbaReader`, typed mutation/package services and native public handler | 11T9A binds one exact retained Excel/Word/PowerPoint session, passes typed requests/snapshots/actions directly and deletes the old host ids/backends. 11T9B routes public execution directly to this boundary | host/backend and public native switches done host-neutral; no command/result/JSON roundtrip or active-document fallback; Windows/VBE qualification open |
| Public VBA `ToolDefinition` controller executor, mutable accepted arguments + `RuntimeGuardJson`, legacy `ToolResult` projection | `VbaToolCatalog` + `VbaToolHandler` + bounded `IPreparableToolHandler` state | ToolRuntime / VBA | Agent confirmation/replay and explicit manual VBA mutations/macro | 11T9B captures exact policy/binding, stores opaque guard state with pending kernel evidence, preserves accepted argument JSON and dispatches typed outcomes through the bound backend. `ControllerExecutorKind.Vba` and public projection methods are deleted; 11J2 deletes the remaining package/manual result projection | removed host-neutral; no public/package alias, dual dispatch or re-prepare; Windows qualification open |
| `UserQuestionToolExecutor` + caller-owned question/option ids | `UserQuestionToolCatalog` + exact `UserQuestionToolHandler` + ToolRuntime `AwaitingUser` | Conversation / ToolRuntime | Plan loop, direct/manual Plan tool and question UI projection | 11T9C1 removed the legacy executor; 11O2 keeps the exact public id but switches prompt/options, runtime-generated UI ids, semantic answers and identity-free model projection atomically | switched host-neutral; no alias/dual schema or prose lifecycle inference; Windows question UI/continuation evidence open |
| `PlanDocumentToolExecutor` plus public create/update and caller-owned plan/artifact/revision guards | `common.plan_doc_save/restore/delete` + `PlanDocumentService` runtime preparation | Conversation / ToolRuntime | Plan loop, direct/manual Plan mutations and artifact/UI persistence projections | 11T9C2 removed the legacy executor; 11O2 merges create/update, resolves readable restore version and binds exact active/source guards internally while preserving linear revisions/tombstones | switched host-neutral; old create/update ids deleted without aliases; Windows Plan UI/persistence evidence open |
| `TaskListToolExecutor` plus public create/update/close and caller-owned list/step ids | `common.task_list_set` + typed `TaskListService` runtime identity binding | Conversation / ToolRuntime | Agent/Plan loop, task-tracking skill, direct/manual checklist actions and artifact/UI persistence projections | 11T9C3 removed the legacy executor; 11O2 merges lifecycle into typed save/close, generates list/step ids and preserves step identity across complete semantic saves | switched host-neutral; three old ids deleted without aliases; Windows Task List UI/persistence evidence open |
| `HtmlArtifactToolExecutor` + `ControllerExecutorKind.HtmlArtifact` + generic source command/result fallback; later model-facing inspect/set-active/general upsert and nested bind source arguments | `HtmlWorkspaceToolCatalog` + exact `HtmlWorkspaceToolHandler` + typed `HtmlWorkspaceToolService` / accepted-read resolver | HTML / ToolRuntime | Agent semantic file/data write, exact patch/delete/bind/refresh/freeze; internal preview selection/preflight and artifact/UI projections | 11T9C4 removed legacy execution; 11O3 replaces eight plumbing-heavy schemas with seven semantic verified writes, resolves bind source from the exact accepted call/result in the same run, keeps refresh source internally and projects runtime evidence out of model history | switched host-neutral; old inspect/set-active/upsert ids, regex/line patch arguments and nested bind source path removed without aliases/dual schema. Internal UI selection/preflight and durable binding/resource evidence remain; Windows WebView/live Office binding evidence open |
| `CapabilityDiscoveryExecutor` + `ControllerExecutorKind.CapabilityDiscovery` legacy command/result path and skill-read methods in `SkillToolExecutor` | `CapabilityToolCatalog` + exact `CapabilityToolHandler` + typed `CapabilityCatalogService*` | ToolPack / ToolRuntime | Agent/Plan discovery, exact tool-schema and skill/reference reads, manual diagnostics and prompt context | 11T9C5 preserves both ids, schemas, compact catalog/revision and admission evidence, assigns independent Agent/Plan read policies and passes the immutable run tool/skill catalogs directly to the handler | removed host-neutral; old class/path/controller branch, old skill-reader dependency and capability use of legacy outcome/result adapters deleted with no alias/dual dispatch; Windows/live-provider WQ-PACK evidence open |
| `PromptToolExecutor` + `ControllerExecutorKind.Prompt` legacy command/result path | `PromptToolCatalog` + exact native read/save handlers + typed `PromptSettingsService` | Settings / ToolRuntime | Agent prompt inspection/save, direct Prompts UI/manual save and settings persistence | 11T9C6 preserves both ids and payload fields, assigns exact Agent-only policies and guards confirmed save with accepted-argument/current-field hashes plus supplied-field read-back while preserving unrelated settings | removed host-neutral; old executor/class/controller branch and prompt use of legacy outcome/result adapters deleted with no alias/dual dispatch; Windows settings/DPAPI/Prompts UI evidence open |
| `ToolAuthoringExecutor` + `ControllerExecutorKind.ToolAuthoring` legacy command/result path | `ToolAuthoringCatalog` + exact native handlers + typed `ToolAuthoringService` | Tool Library / ToolRuntime | Agent definition read/validate/upsert/delete and direct/manual authoring calls | 11J1 preserves all four ids/schemas and omitted-field update semantics, assigns Agent-only read/verified-write policies and carries bounded accepted-argument/current-definition guards through confirmation with exact read-back | removed host-neutral; old executor/files/controller branch and authoring use of legacy command/result adapters deleted with no alias/dual dispatch; package execution/UI followed in 11J2, Windows Library/VBE qualification open |
| `VbaPackageToolAdapter` + `VbaLegacyResultProjection` + legacy custom-package dispatch | `ToolPackageSource` v1 + `VbaPackageToolHandler` + `VbaPackageResult` v1 | Tool Library / ToolRuntime / VBA | Existing global/document-local package execution and Tools UI install/remove/status | 11J2 captures exact source/content revision in the registration, dispatches exact ids through native ToolRuntime, marks backend dispatch at install/run/remove and returns explicit effect evidence. UI accepts only result contract v1 | removed host-neutral; old classes/files/branch and PascalCase UI fallback deleted; immutable history/Host Fabric and Windows VBE qualification remain open |
| `SkillToolExecutor` + final controller executor/legacy result branch | `SkillPackageSource` v1 + `SkillAuthoringCatalog`/native handler + typed `SkillAuthoringService` | Skill Library / ToolRuntime | Agent core/reference upsert/delete and confirmation/replay | 11K1 preserves the two exact ids/schemas and omitted-field semantics, binds exact accepted arguments plus complete current package revision, rejects stale confirmation before dispatch and verifies package read-back/absence; 11K2 reuses the same owner for the editor | removed host-neutral; old class/controller branch and Skill legacy result conversion deleted without alias/dual dispatch; Windows Library qualification open |
| Direct Skills controller → `SkillStore` reconcile/reference mutation, raw `SkillDefinition[]` bridge and unversioned reference responses | Versioned `SkillLibrary`/mutation/reference DTOs + explicit revision-guarded `SkillAuthoringService` manual operations | Skill Library / UI | Init/SendChat catalog projection and existing Skills editor/reference actions | 11K2 sends explicit core/delete mutations, guards every existing package/reference by its exact revision, verifies service read-back and accepts only lowercase contract v1 in the WebView | removed host-neutral; controller store field/direct mutations, implicit catalog deletes, storage-path identity and response-shape fallbacks deleted; Windows Library/WebView qualification open |
| Removed Harness `FakeOfficeAdapter.Vba` scripted command/result queue | Direct typed fake backend state/fault scripting | Tests | VBA fault matrix fixtures only; not compiled into production | 11T10 moved package/mutation/macro fixtures to direct typed fake requests, state and fault hooks, then deleted retired ids/DTO queues | removed host-neutral; 93-case VBA matrix runs without a compatibility seam |
| `VbaMutationJournalStoreAdapter` | Narrow `IVbaMutationJournal` port over the existing `VbaJournalStore` | VBA / Storage | `VbaMutationService`; deterministic fault tests wrap the same port | Permanent layer boundary while storage stays independent; remove only if the canonical store directly implements the port, never by adding another store | permanent architectural adapter; same append-only journal/CAS authority, no dual-write |
| `VbaPackageJournalStoreAdapter` | Narrow `IVbaPackageJournal` port over the existing `VbaJournalStore` | VBA / Storage | `VbaPackageService`; deterministic lifecycle/fault tests wrap the same port | Permanent layer boundary while storage stays independent; remove only if the canonical store directly implements the port, never by adding another store | permanent architectural adapter; same package events/CAS authority, no dual-write |
| `VbaRenameJournalStoreAdapter` | Narrow `IVbaRenameJournal` port over the existing `VbaJournalStore` two-component wire | VBA / Storage | `VbaMutationService` rename workflow and deterministic recovery/fault tests | Permanent layer boundary while rename retains `package.mutation.*`; remove only if the canonical store directly implements the narrow port, never by adding another store | permanent architectural adapter; same append-only journal/CAS authority, no dual-write |
| Remaining `VbaToolExecutor*.cs` rename orchestration | `Office.Vba.VbaMutationService` typed rename workflow | VBA / Office | `common.vba_rename_module` plus rename recovery/diagnostics | 6J switched guard/preparation/backend/verification/recovery and deleted executor compound helpers; 11O5 removed the combined write branch and retained the same `package.mutation.*` journal/CAS authority | switch done host-neutral; no alias/dual execution. Production COM/identity/Windows VBE qualification open |
| Removed `VbaPackageToolAdapter` | `ToolPackageSource` v1 + typed package handler/result | VBA / Tools | Custom Agent/manual package execution and Tools UI persistent install/remove/status | 11T9A deleted the host compatibility backend; 11J2 moved source/result/UI consumers and deleted the remaining adapter. Package domain never reads legacy result/history | removed host-neutral; Windows qualification open |
| Public `excel.read_range` / native range binding / direct range-output adapter | Existing Gateway + `ExcelResourceProvider` + `ExcelReadService` | Resource Fabric / Excel | Model/manual `common.resources_read`, HTML bindings | Values/formulas/profile use exact resource views; catalog, core-pack id, direct branch and output wrapper removed; old accepted calls rejected | removed host-neutral 2026-09-06; Windows Excel and catalog qualification open |
| Public `excel.inspect` legacy dispatch in `OfficeToolExecutor` → `ExcelAdapter` | `Office/Domains/Excel` typed inspect service + native ToolRuntime handler | Excel / Office | Agent, confirmed/manual execution | 7B switched all inspect selectors; range reads now use the resource row above | switch done host-neutral; Windows Excel qualification pending |
| Public `excel.write_range` legacy dispatch and success-on-COM-return | `Office/Domains/Excel/ExcelWriteService` + exact native handler/effect evidence | Excel / Office | Agent and manual scalar/formula/table writes | 7C switched exact before/apply/read-back with VerifiedNoChange/VerifiedChange/error/unknown; public host case and old write helpers deleted. Other Excel mutations remain legacy | switch done host-neutral; real Excel/protected-sheet/COM normalization qualification pending |
| Public `excel.find_cells` / `excel.replace_cells` host dispatch, range/pattern helpers and legacy result mapping | `Office/Domains/Excel/ExcelFindReplaceService` + direct native handlers + `OfficeHosts/Excel/ExcelFindReplaceInteropBackend` | Excel / Office | Agent/manual find and replace over workbook/sheet/range/selection | 11T1 preserves exact ids/schemas and pattern/scope behavior, binds all reads/writes to `ExcelDocumentSession`, verifies exact pre-state/read-back and removes the production host branches/helpers | switch done host-neutral; no alias/dual execution; real protected/large-range and COM partial-write WQ-EXCEL evidence open |
| Public `excel.add_sheet` / `excel.rename_sheet` host dispatch, methods and worksheet-name helper | `Office/Domains/Excel/ExcelSheetService` + direct native handlers + `OfficeHosts/Excel/ExcelSheetInteropBackend` | Excel / Office | Agent/manual worksheet creation and rename | 11T2 preserves exact ids/schemas/default/active-sheet behavior, validates and rechecks exact collection pre-state, verifies read-back and removes the production host branches/methods/replaced helper | switch done host-neutral; no alias/dual execution; protected workbook, rollback/partial COM and case-only rename WQ-EXCEL evidence open |
| Public `excel.clear_range` / `excel.sort_range` / `excel.filter_range` / `excel.format_range` host dispatch, methods and color/alignment helpers | `Office/Domains/Excel/ExcelRangeMutationService` + direct native handlers + `OfficeHosts/Excel/ExcelRangeMutationInteropBackend` | Excel / Office | Agent/manual range clearing, sorting, filtering, formatting and autofit | 11T3 preserves exact ids/schemas/defaults, bounds one contiguous target, rechecks an operation-specific pre-state token immediately before dispatch, verifies operation-specific read-back and removes the production host branches/methods/replaced helpers | switch done host-neutral; no alias/dual execution; sorting locale, filter normalization, Normal-style/conditional formatting, autofit and partial COM WQ-EXCEL evidence open |
| Public `excel.add_table` host dispatch and method | `Office/Domains/Excel/ExcelTableService` + direct native handler + `OfficeHosts/Excel/ExcelTableInteropBackend` | Excel / Office | Agent/manual structured-table creation | 11T4 preserves the exact id/schema/defaults, bounds one contiguous source and the full workbook collection, rechecks source values/formulas plus collection state before dispatch, verifies one exact new table and removes the production host branch/method | switch done host-neutral; no alias/dual execution; `xlNo` header/range semantics, styles/localization, overlap/protection, rollback and partial COM WQ-EXCEL evidence open |
| Public `excel.create_chat_chart` / `excel.upsert_chart` / `excel.delete_chart` host dispatch, methods and chart helpers | `Office/Domains/Excel/ExcelChartService` + direct native handlers + `OfficeHosts/Excel/ExcelChartInteropBackend` | Excel / Office | Agent/manual chat-chart reads and worksheet-chart creation/update/delete | 11T5 preserves exact ids/schemas/defaults, bounds chat/source ranges plus full chart/series collections, rechecks requested ranges and collection state before mutation, verifies exact chart and untouched peers, and removes the production host branches/methods/replaced helpers | switch done host-neutral; no alias/dual execution; real series-formula normalization, chart types/axes, generated names, protection, rollback and partial COM WQ-EXCEL evidence open |
| `ExcelReadCompatibilityBackend` / `ExcelWriteCompatibilityBackend` internal host commands plus `ExcelAdapter` target/sheet resolution and admitted read/write COM methods | `OfficeHosts/Excel/ExcelInteropBackend` over bound `ExcelDocumentSession` | Excel / Host | 7B typed reads, 7C typed writes; desktop/VSTO/native composition | 11T0/7D introduced the production bound session/factories using the captured current `RuntimeKey`, installed the direct backend and removed all internal commands plus `ActiveWorkbook`/descriptor execution fallback. WQ0/WQ-SESSION/WQ-EXCEL remain deferred evidence | removed host-neutral; no files, ids, alias or dual execution path; Windows evidence open |
| Public Word read/write/find/inspect/replace/format/table/page-break/comment host dispatch, helpers and descriptor/active-document target resolution | `Office/Domains/Word/WordService` + direct native handlers + `OfficeHosts/Word/WordInteropBackend` over `WordDocumentSession` | Word / OfficeHosts | Agent/manual Word actions; source reads moved to the resource row below | 11T6 switched the host family and bound desktop/VSTO runtimes to one exact document/window, rechecks mutation pre-state and read-back, and removed every public Word host branch/helper plus execution-time target fallback; the later source-read cutover removes `word.read_text` | switch done host-neutral; no alias/dual execution; VBA/macro branches remain only for 11T9; WQ-SESSION/WQ-WORD evidence open |
| Public `word.read_text`, source output wrapper and generic adapter text fallback inside Word resource reads | Existing document provider + `WordService.CaptureText` + bound Word backend | Resource Fabric / Word | Model/manual resources, HTML document/selection/range bindings | Complete text retained in existing CAS; old id/native binding/adapter removed; bounds before COM text capture; explicit range target | removed host-neutral 2026-09-06; real Word/STA/selection and catalog qualification open |
| Public PowerPoint read/search/replace/add/set/add-object/duplicate/move/list host dispatch, helpers and descriptor/active-presentation target resolution | `Office/Domains/PowerPoint/PowerPointService` + direct native handlers + `OfficeHosts/PowerPoint/PowerPointInteropBackend` over `PowerPointDocumentSession` | PowerPoint / OfficeHosts | Agent/manual PowerPoint actions; source reads moved to the resource row below | 11T7 switched all nine original ids/schemas, binds desktop/VSTO runtimes to one exact presentation/window, rechecks mutation pre-state and read-back, and removes every public PowerPoint host branch/helper plus execution-time target fallback | switch done host-neutral; no alias/dual execution; VBA/macro branches remain only for 11T9; WQ-SESSION/WQ-POWERPOINT evidence open |
| Public `powerpoint.read_slides`, direct output wrapper and clipped document snapshot inside resource reads | Existing document provider + `PowerPointService.CaptureSlides` + bound PowerPoint backend | Resource Fabric / PowerPoint | Model/manual resources, HTML document/slide bindings | Exact text/notes views in existing CAS; old id/native binding/adapter removed; explicit slide target and bounds before COM text capture; selection observation unchanged | removed host-neutral 2026-09-06; real PowerPoint shapes/notes/STA and catalog qualification open |
| Removed Outlook generic host branches, active-window target resolution and helpers | `Office/Domains/Outlook/OutlookService` + direct native handlers + `OfficeHosts/Outlook/OutlookInteropBackend` over `OutlookDocumentSession` | Outlook / OfficeHosts | Agent/manual Outlook built-ins and admitted HTML reads | 11T8 preserves all five exact ids/schemas, binds desktop/VSTO runtimes to one exact Inspector/mail or Explorer/folder and removes every public host branch/fallback; 11T10 deletes the empty generic interface/catalog seam | switch done host-neutral; no alias/dual execution; WQ-SESSION/WQ-OUTLOOK evidence open |
| Removed `Services/ProgressiveToolWorkingSet.cs`: read evidence, Touch/LRU/eviction | `Core/Services/CallableToolSet.cs` + `Core/Services/ToolPackAdmissionJournal.cs`; Office profile factory `Services/CallableToolPack.cs`; immutable execution `Core/Tools/ToolPackSnapshot` | ToolPack | ConversationModelSession, prompt inspector and capability discovery | 8B: finite exact core profiles, live current-run staging, atomic full-request-budget extension/new revision and no eviction; old class/touch/state path deleted. 8C: same-stream accepted/rejected event is the publication barrier and the exact accepted `TurnId` delta chain is reconstruction authority; raw history cannot infer admission | switched host-neutral; no LRU/alias/dual membership or side store; Windows/live-provider WQ-PACK open |
| Removed public `ResourceList/Resolve/SearchToolHandler`; retained internal provider list/resolve/search/read in `ResourceGatewayService` | `ResourceToolCatalog` plus exact `ResourceFindToolHandler` / `ResourceReadToolHandler` intent registrations and `ModelToolResultProjection` | Resources | `common.resources_find/read`, domain providers, request-local media projection and durable replay | 11O1 switches Chat/Plan/Agent and prompt/skill consumers atomically, reconstructs exact refs/cursors from durable evidence, strips runtime state from model projections and deletes old public ids/handlers without aliases | switched host-neutral; internal provider authority and exact durable evidence retained, Windows/live-provider WQ-PACK open |
| `src/RNAssistant.Core/Storage/ChatStore.cs`, ChatStore.EventLog.cs, ChatStore.SessionProjection.cs, ChatHeaderReducer.cs | active `IRunStore` + `IEventStore` + minimal `IConversationStore`, all over the same stream/projections | Persistence | Controller/session service/kernel persistence, model/causal/ToolPack writers, diagnostics/trajectory, history | 9D3 switches event consumers to one closed `ChatEventStoreAdapter`. 9D4 switches all active conversation aggregate/projection consumers to one `ChatConversationStoreAdapter`; artifact hydration, HTML revision activation, CAS maintenance and storage reducers retain existing owners. Replaced public conversation/event methods are Core-internal | switched host-neutral; no writable snapshot, fallback or dual-write. Both adapters share one `ChatStore`; permanent boundary unless that backend directly implements the same narrow ports. Windows controller qualification open |
| Removed controller-only `RunStoreException` escape; startup-only reconciliation | Shared `ChatSessionService` targeted/startup reconciliation after run ownership release | Application / Persistence | Agent start and confirmation controllers; active chat projection | 9D2: both controllers discard mutated memory and reload canonical stream; durable pending is retained, open dispatch reconciles once. No `UnpersistedSummary` persistence, append retry or tool replay | switched host-neutral; old escape-only path removed, Windows controller qualification open |
| Pre-R37 current-v4 `tool.result.recorded` с `AcceptedCallOrigin` | Exact current operation type only; incompatible retained evidence is reset/new-chat only | Persistence / Diagnostics | Старых adapter consumers нет; `run-causal` показывает exact operation как incompatible, `tool-execution` не принимает её | 11T cleanup: inference удалена; retained events не переписываются и не переинтерпретируются | removed host-neutral; no execution/result authority or fallback |
| `web/js/app-chat-state.js`, app-utils.js, app-agent-model.js, app-agent.js; flat `RunExecutionSummary`/model-status projection | Immutable typed `RunViewState` derived from `KernelState`/effect evidence | UI/Application | Static web UI, ChatState/SendChatResponse, chat catalog and run journal navigation | 9D5 switches bridge/JS together, stamps replayable message/header state and guards full responses by per-chat session revision; existing stream CAS remains cross-window writer authority | switched host-neutral; old flat/model-status readers removed, Windows WQ-UI open |
| Client `artifactResourceHeads` lineage/root/max-revision inference and extension/version labels | `Office.Services.ArtifactLibraryProjectionService` + typed exact head/history DTO | Resources / Application / UI | Init/ChatState/SendChat/direct HTML responses, resource dock and Artifacts tree/detail | 11A2 derives classes/groups/logical heads/history from replayed artifacts; HTML uses active pointer, messages keep raw exact revisions. Client lineage calculation and `plan_document`/JSON/`Revision>1` label heuristics are removed | switched host-neutral; no adapter, writable index or second transport. Plan/HTML mutation/viewer slices and Windows WebView qualification remain |
| Per-screen read-only `prettyJson` / `JSON.parse` / plain JSON `<pre>` | UI-only `ViewerRegistry` → bounded/lossless `JsonAdapter` | UI | Diagnostics, Agent, Context, manual Tools, VBA metadata, artifact details и completed message JSON switched | 9B2A diagnostics; 9B2B1 Agent; 9B2B2 Context/Tools/VBA; 9B2B3 artifacts; 9B2B4 closed fenced Markdown. Live/unclosed fences remain code | read-only inventory switched through 9B2B4; editable/transport/domain chart JSON intentionally excluded |
| Former qualification event v1/v2 (removed in-app contour) | Historical `qualification.*` records retained in the existing chat stream | Persistence / diagnostics | No active qualification runner or admission consumer | Old runs remain historical evidence only; no compatibility reader, rewrite, replay authority or new qualification chat is created | retired with Qualification Center; release evidence is recorded externally against the exact build |
| Pipelines: executor/parser, nested safety/fingerprint/document traversal, authoring/editor/storage fields | Отложены за пределы stable core | Optional tools | Нет действующих consumers; catalog/dispatch/authoring закрыты, старые файлы skipped | Отдельное решение Phase 11 после stable core; новый контракт через ToolRuntime, без legacy migration/adapter | removed/disabled по согласованному scope 2026-08-28; не blocker Phases 3–10/12 |
| Existing custom authoring and Library seams | Typed versioned authoring contracts | Domain owners | Tool/Skill model authoring and Library UI | 11J1–11K2 switched model and Skills UI owners; 11T10 switched the Tools editor to lowercase `rnassistant.toolLibrary`/mutation v1 with exact revision guards and deleted final generic catalog/dispatch/result consumers | switched host-neutral; no active generic legacy, Windows Library/WebView/VBE qualification remains |
| New optional Browser/Automation/Host Fabric/Library capabilities | Отдельно квалифицированные product contours | Domain owners | Нет обязательных stable-core consumers | Только отдельное явное решение после mandatory 11T; не prerequisite Phase 12 | optional, not admitted by legacy-removal decision |

В Phases 0/1A/1B новые compatibility adapters не вводились. Adapter paths 1C/2A
указаны выше и в [PROGRESS.md](PROGRESS.md) с owner/consumers/removal phase.
V2 read adapter 2C1 удалён в 2C2 после проверки consumers. Current-v3 history reader
не поддерживает старые форматы и не мигрирует streams; current-v2 typed-ID helper
и live v2 parser/schema/DTO удалены в 2C3C. Dual-write не вводится.
Массовые переносы файлов, aliases, новые runtime loops и dual-write запрещены.

Путь model status через ChatTurnResult, LastRun, controller/bridge, persistence и UI:
[PHASE_1A_CHARACTERIZATION.md](PHASE_1A_CHARACTERIZATION.md). В Phase 1C assertions
заменены red→green safety assertions: [evidence](PHASE_1C_COMPLETION_GUARD.md).
После 2C3C model-owned status отсутствует; оставшиеся runtime lifecycle labels не являются proof of effect.
Phase 9D5 удалил этот путь из bridge/UI: retained status остаётся только accepted-history/
diagnostic metadata, а lifecycle/effects отображаются через `RunViewState`.

Correlation ids, значения stages и ограничения trace:
[PHASE_1B_CAUSAL_TRACE.md](PHASE_1B_CAUSAL_TRACE.md). Старые transport `StepId` /
`llm.*` events сохранены для recovery; logical step находится в model `Data.StepId`.
Это диагностическая связь, не alias нового model protocol и не второй storage path.

Граница Core/Office, lifetime одного protocol step и сохранённые legacy ограничения:
[ADR-0002](../decisions/ADR-0002-model-protocol-boundary.md),
[Phase 2A evidence](PHASE_2A_MODEL_PROTOCOL.md). Office stream projector адаптирует
прежний provisional preview к callbacks Core; это presentation, не второй protocol.

## Plugin icon SVG storage decoder — 2026-10-20

Owner: Core `PluginIconLibrary` / `PluginStore`. The active format is a normalized
static SVG string plus an optional appearance color; old geometry conversion and
path-array bridge/render contracts are removed. Library v1 files are decoded on read,
verified against their original path digest and retain the same ID without a
write during reads. Immutable registration bodies with `Appearance.IconPaths` decode into
`IconSvg` when read; their next normal update writes only SVG. There is no dual-write
or alternate rendering path. Removal gate: an explicit storage-format reset or an
offline migration proves that no library v1 or reachable registration body needs
the decoder. Do not drop existing user icons merely to remove this decoder.

### Workspace HTML checkpoints — 2026-11-13

Workspace live/saved sources now use WorkspaceHtmlVersionService + existing artifact
owner. The former entry-only source branch in PluginApplicationService was removed.
The existing Browser checkpoint UI temporarily admits plugin/Workspace kinds only;
owner: Browser/artifact integration, removal condition: document HTML checkpoint
adapter and cutoff migration connected in the next slice. No second version store.

### Document checkpoint cutover — 2026-11-13

The ArtifactVersions provider guard now includes document HTML; its old savedHtml
history-picker path is removed. HtmlArtifactView and getSavedHtmlHistory were removed in F4b; common Browser owns
checkpoint history/comparison. Automatic revision durability is retained.
Raw HTML import is implemented at the existing artifact/import owners; remaining
common UI action/provider wiring is F4, not another import pipeline.
