# RNAssistant architecture

This document is the current detailed architecture and ownership map. The compact
documentation index is [docs/README.md](README.md). Permanent
cross-cutting engineering rules live in
[development-rules.md](development-rules.md); temporary execution order and gates
live in the [stabilization progress](stabilization/PROGRESS.md). Historical phase
reports and ADRs are evidence/rationale, not a second current architecture.

Canonical domain documents:

- [Resource Fabric](resource-fabric.md): artifact/document identity and read data plane.
- [Artifact Library and Viewers](artifact-library.md): user-visible artifact lifecycle.
- [Tool Library](tool-library.md): capability truth, model-facing contracts and authoring.
- [Skill Library](skills.md): trusted instruction packages and references.
- [Conversation protocol](conversation-protocol.md): model loop and result/effect contracts.
- [Session events](session-events.md): durable stream, replay and recovery.
- [Qualification](qualification.md): typed qualification and issue evidence.
- [Host Fabric](host-fabric.md) and [Local Automation](local-automation-agent.md): deferred contours that do not expand the current stable-core route.
- [Desktop runtime](desktop-runtime.md): standalone shell, activation and Office target selection.

## Product

RNAssistant is a local Office assistant for Word, Excel, PowerPoint, and Outlook. It stores per-document chats and context, talks to an OpenAI-compatible endpoint, executes Office tools locally, and requires no backend.

## Dependency direction

```text
static WebView UI
    -> typed bridge
        -> RNAssistant.Office orchestration and local tools
            -> RNAssistant.Core models, storage, ModelProtocol, LLM transport, parsers
            -> IOfficeApplicationAdapter
                -> host-specific COM adapters
```

- `RNAssistant.Core` cannot reference Office, VSTO, WinForms, or WebView2.
- `RNAssistant.Office` owns host-neutral orchestration, session services, prompt assembly, transcripts, and tool execution. It cannot contain host-specific COM interop.
- `RNAssistant.OfficeHosts` and `RNAssistant.*AddIn` own host adapters and Office wiring only. Host document identity is owned by `RNAssistant.OfficeHosts/Identity/DocumentIdentity.cs`, while the dynamic COM/VBE backend is owned by `RNAssistant.OfficeHosts/Vba/VbaProjectSupport*.cs`; host-neutral Office code cannot consume either helper.
- `web` is static HTML/CSS/JS with no build pipeline.

## Deferred host and local automation boundaries

One RNAssistant window may eventually select targets owned by other Office
processes, but COM and execution remain in the owning host endpoint. The registry
contains only ephemeral descriptors/leases; an accepted run is pinned to one exact
host/document session and cannot follow focus mid-run. A signed broker is the
preferred enterprise profile; a same-user Office-only rendezvous may remove the
standalone RNAssistant EXE but cannot bypass application-control policy. See
[Host Fabric](host-fabric.md).

Browser, arbitrary filesystem mutation and process/shell execution are separate
optional capabilities, not an automatic expansion of Agent mode. Non-document
automation first needs an ADR for workspace-owned sessions and a signed isolated
worker. Office processes never become general shell workers. See
[Local Automation Agent](local-automation-agent.md).

## Qualification boundary (WQ-A1–A5)

Qualification Center is an application orchestrator over declarative,
versioned host packs. Agent tasks use the normal conversation/kernel/tool/domain
path; allowlisted host probes and deterministic verifiers supply evidence. Model
text and UI presentation cannot declare pass. Runs append closed typed operations to
the existing document chat stream/CAS and are projected through `ITrajectoryQuery`;
there is no second result store or test executor. The empty-chat card opens the
runner instead of inserting a prompt. Host-neutral harness evidence remains a
build artifact and is never executed by VSTO. See [qualification.md](qualification.md)
and [ADR-0010](decisions/ADR-0010-qualification-evidence-authority.md).

WQ-A1 implements the host-neutral boundary in `RNAssistant.Office/Qualification`:
strict data-only manifest and coverage parsers, an immutable catalog, a finite runner,
closed mandatory qualification events over `IEventStore`, CAS-backed large evidence
and bounded typed bridge DTOs. Automatic pass requires a required assertion with
typed expected/actual evidence. A durable start barrier precedes every automatic
step; an open possible effect after replay is blocked and never redispatched. The
WQ-A2 application service, controller routes and WebView shell expose one embedded
read-only `common.ui-shell` pack from both empty chat and Diagnostics. Each run owns a
dedicated document chat, replays from the same validated event stream after restart,
rejects ordinary conversation turns and navigates to the existing exact run journal
and shared JSON viewer. UI status cannot override the typed runner result. The shell
itself does not exercise Office, COM, the model loop or document tools. WQ-A3 adds
the single Excel identity owner/host port and bounded same-build helper.
WQ-A4 embeds the closed versioned suite catalog; absent exact production capabilities
remain N/A. WQ-A5 verifies a detached RS256 envelope against the signer pinned in
assembly metadata, exact build/catalog/file hashes and the complete release run
matrix. Only compatible complete evidence enables the read-only
`release.candidate` pack. Real Office/provider adapters and scenario evidence remain
Milestone WQ; local admission tests do not close them.

## Chat, Plan, and Agent

There are three persisted modes and one structured execution service.

- `Chat` uses `ConversationRunService` with `ChatSystemPrompt`, the shared conversation-response v4 `message + tool_calls[]` JSON contract, and only the two read-only `common.resources_find/read` tools. Runtime policy removes skills, Office tools, local mutations, and confirmation regardless of prompt wording.
- `Plan` uses the same loop with `PlanSystemPrompt`, read-only discovery, skills, typed user questions, a single revisioned Markdown plan document, and an optional temporary Task List. Runtime policy removes Office/shared mutations and confirmation. A ready plan is exact-revision validated internally, then handed to Agent as a semantic find/read instruction.
- `Agent` uses the same service and transcript loop with progressive tool discovery, enabled skill metadata, confirmation, and policy-approved mutations. The full mode/session-filtered catalog stays local as execution authority; it is not injected into every prompt.

`ConversationModelSession` owns each invocation's model context outside the loop: prompt composition and compaction, callable ToolPack admission/reconstruction, request options/cache, bounded tool-result materialization and temporary media. It uses the existing composer, compactor and resource services; it neither executes tools nor decides run outcomes. `AgentTranscript` owns visible tool-activity construction, resource/chart provenance and HTML checkpoints. `ConversationRunService` orchestrates `Core/Agent/AgentKernel` for all modes and confirmation. Invocation-scoped `ConversationKernelAdapter` partials implement model, executor and typed persistence ports; the kernel alone owns ordering, accepted-call IDs, counts and lifecycle.

`ConversationPromptComposer` selects the editable mode instruction, then appends one dynamic `RUNTIME_CONTEXT` containing mode, readable document state, current callable schemas, complete compact `capabilities.items`, user context and bounded semantic resource targets. Document keys, `ResourceRef` values and catalog/package/descriptor revisions are omitted. Callable capability membership is public id/kind plus `schemaLoaded`; unloaded tools and skills retain bounded selection metadata. Chat receives an empty capability catalog. Agent/Plan bootstrap is the four semantic resource/capability tools. `common.capabilities_search/read` have exact native handlers; runtime validates hidden descriptor/package revisions and projects stale evidence as error. Complete tool reads may stage one atomic optional extension, whose exact before/after state stays only in durable events while `TOOL_PACK_STATE` exposes public ids and admission outcome. One shared admission calculation covers messages, request options, repair overhead and continuation reserve. No execution touch or LRU eviction changes membership. Strict response JSON Schema comes from the same callable set. Confirmation, compaction and restart reconstruct authority from the exact durable chain; raw read results never grant it.

R61/11O is the mandatory post-migration audit of that model-facing surface. 11O1
has atomically switched Resources + Capabilities host-neutral; every remaining family
must similarly reduce its public schema to semantic intent while the same
ToolRuntime receives runtime-owned exact target, reference, guard and continuation
state. Durable `ResourceRef` provenance is retained, but the model and Library test
form do not fabricate URI/UUID/revision/cursor values. Human built-in documentation
is a UI-only selected-tool detail and is excluded from descriptors, capability
context/results and token accounting. See [Tool Library R61](tool-library.md#mandatory-all-tool-contract-audit-r61).

Editable general/tool/skill Agent, Plan, Chat, title, compaction, and attachment-analysis prompts are stored as Markdown. Their instruction role (`developer`/`system`/`user`) is independent from the shared response format (`json_object`/strict `json_schema`) and tool-result role (`user`/`developer`/matched `tool`). Protocol repair and compatibility-probe instructions remain fixed.

`IMaterializedModelProtocol` in `Core/ModelProtocol` owns conversation endpoint attempts, local validation/repair, native refusals, prompt-budget checks and format fallback. The current loop receives one accepted response/metadata or typed failure per logical step; it neither counts raw attempts nor executes tools before acceptance. See [ADR-0002](decisions/ADR-0002-model-protocol-boundary.md) for the v3 boundary and remaining controller adapters.

Phase 3B2 connects the pure kernel to production start/confirmation. Its generic
`IModelProtocol.SendAsync`, `IToolRuntime` and `IRunStore` ports keep materialization,
resource lifecycle, provider metadata and visible projections in Office. Immutable
`RunSummary` separates lifecycle from effect health. `ChatRunRecord.KernelState`
is carried by existing typed `run.updated` operations; immutable `RunViewState`
is the only active bridge/UI projection and is not another outcome accumulator or store. The old Office loop,
`RunSummaryBuilder`, mutable accepted-ID bookkeeping and `Failure.Cause` are removed.
Actual event replay and neutral adapters are tested; production controller is
compiled in MockDemo, with Windows/Office delivery qualification still open.
See [ADR-0001](decisions/ADR-0001-model-does-not-own-completion.md),
[ADR-0008](decisions/ADR-0008-unknown-effects-are-not-retried.md) and
[cutover evidence](stabilization/PHASE_3B2_KERNEL_CUTOVER.md).

R29 activates the ID-less v4 `ConversationResponse` through the single
`Core/ModelProtocol/ModelProtocolWire` owner: schema, local validation and canonical
JSON writing are shared by the client, loop, transcript and compatibility probes.
The old model-ID wire/context path is removed. The kernel converts validated
drafts to accepted calls with unique runtime IDs before persistence or dispatch;
allocation failures never trigger model regeneration. Native provider refusal is
a separate result; it cannot schedule tool calls.

Accepted runtime IDs and raw `StepId/ModelAttemptId/CallIndex` origins are durable
in the same accepted-message commit. Raw payloads are unchanged; results and
confirmation/replay retain IDs. ModelProtocol receives only the conservative
batch-safety context, not a model-ID registry. Full-history and confirmation preflight precede
controller preparation, manual compaction and pending consumption; incomplete
CallContext cannot trigger a raw request or format repair. Saved prompts retain
their text, while schema marker 13 requires explicit review of prior instructions.
No old chat is converted/truncated automatically. See the [v4 contract and
qualification gates](protocols/CONVERSATION_RESPONSE_V4.md#remaining-cutover-gates).

These values are internal correlation keys, not domain objects or another state
machine:

| Key | Only reason it exists |
|---|---|
| `SessionId` | Identifies the chat event stream; it is the chat id, not a second session concept. |
| `EventId` | Addresses one immutable event and its optional CAS payload. |
| `RunId` | Identifies one physical invocation; confirmation resume may use another run. |
| `TurnId` | Keeps one logical user turn continuous across that resume. |
| `StepId` / `ModelAttemptId` | Separates a model step from bounded format/provider attempts inside it. |
| `ToolCallId` | Pairs one accepted call with confirmation, dispatch and its result. |
| `DocumentRuntimeId` | Prevents an Office action from silently changing its live target. |
| `MutationId` / journal run | Correlates only domain recovery/read-back records such as VBA. |

There is no generic correlation/operation/batch id. These keys stay collapsed in
ordinary UI; Diagnostics leads with model payload, tool name, arguments, result and
effect, and exposes IDs only in a technical section.

Both `json_schema` and `json_object` enforce the same v4 contract against the current callable tools. Model-owned lifecycle fields are absent; empty calls only end the model loop. Immutable `RunViewState` projects lifecycle from `KernelState` and effect health/counts from source-owned execution evidence. `Core.Tools` descriptor/policy/binding contracts and `Office.Runtime.ToolRuntime` own exact typed registrations. R61/11O1 replaces the four public resource ids with exact native `common.resources_find/read` handlers over the unchanged internal gateway/provider operations; no alias remains. `ModelToolResultProjection` separates full durable resource/capability evidence from the model wire and validates stale capabilities against the current catalog. All 11T0–11T10 direct host/domain ownership remains unchanged. `ToolCatalogEntry` is only a mutable catalog/package projection; missing source authority fails closed. Core `ToolResultWire` remains `{tool_call_id, name, status, message, data, resources?}` with `ok/error/unknown`; switched-family model projections omit opaque state, while durable/manual projections retain exact evidence. Generic oversized data and unswitched families keep their current behavior until their own atomic R61 cutover.

Before a turn mutates a chat, the controller acquires a per-chat lease backed by an in-process registry and a cross-process lock file, reloads a newer persisted revision, and appends the user request, committed attachment references, and run state before calling the model. Attachment drafts stay in staging until their content-addressed references and the message are durable. After that mandatory save, the controller queues one full revision-guarded active-chat projection before attachment-helper or primary model transport; catalog-only background updates cannot replace the active transcript, and no WebView acknowledgement gates execution. Tool-start and tool-result boundaries are appended as typed operations. First-class turn events keep one logical `TurnId` across confirmation continuations, while every model request has `step.started`/`step.ended` boundaries correlated by request id. The event tail sequence is the monotonic compare-and-swap revision, so a stale window fails instead of overwriting newer history. A confirmation pause persists its pending id and cumulative iteration/tool counters; a new request is rejected until the action is confirmed or cancelled. Confirmation acquires a new lease and resumes the same logical budget. On startup, recovery checks the canonical event stream for a tool start without its matching finish; only that case becomes `interrupted_unknown`, while already persisted results remain replayable. Open model steps receive a synthetic interrupted terminal event.

11T9C1–11T9C6 move questions, Plan Documents, Task Lists, HTML workspace/data,
capability discovery/read and prompt read/save to exact native handlers. Prompt save
is Agent-only, prepares an exact accepted-arguments/current-field guard before
confirmation, preserves unrelated global settings, marks dispatch before storage
mutation and requires exact supplied-field read-back; stale preparation fails before
dispatch and no-change is explicit. Their former controller executors and legacy
result paths are deleted.

11J1 moves `common.tools_definition_read`, `common.tools_validate`,
`common.tools_upsert` and `common.tools_delete` to exact Agent-only native handlers.
Authoring mutations prepare a bounded exact-arguments/current-definition hash guard,
reject stale confirmation before dispatch and require stored-definition read-back;
exact no-change avoids storage dispatch. `ToolAuthoringExecutor` and its controller
branch are deleted. 11J2 captures every existing global/document-local VBA package
as immutable `ToolPackageSource` contract v1 with a distinct content revision, binds
its exact id to `vba.custom.package.execute.v1`, and executes it through the native
runtime and bound VBA backend. Library install/remove/status consume the same typed
source plus versioned result/effect DTO. `VbaPackageToolAdapter`,
`VbaLegacyResultProjection` and their legacy custom execution branch are deleted.

11K1 moves exact `common.skills_upsert/delete` to Agent-only native handlers over
`SkillAuthoringService`. Each confirmed core/reference mutation binds the accepted
arguments and complete current package revision, rejects stale state before storage
dispatch and verifies the resulting package revision or absence. No-change avoids
dispatch. `SkillToolExecutor`, the final controller-executor branch and Skill use of
legacy result conversion are deleted. 11K2 gives the existing Skills UI one strict
versioned package/result bridge, explicit per-package revision guards and the same
service owner; controller-to-`SkillStore` mutation, raw catalog reconcile and
unversioned/PascalCase response fallback are removed.

11T10 completes the mandatory existing-tool route. The Tools UI uses lowercase
`rnassistant.toolLibrary`/mutation v1 DTOs with exact revision guards and the same
`ToolAuthoringService` owner as model authoring. Generic host catalog/dispatch,
legacy definition/result/UI projections and retired fake command queues are absent;
`OfficeToolExecutor` remains only the typed composition/manual façade over a captured
runtime.

See [conversation-protocol.md](conversation-protocol.md).

## Important boundaries

- Controller files coordinate bridge requests; reusable behavior stays in services and executor logic stays in `RNAssistant.Office/Tools`.
- Tools are executable capabilities. Their selected-endpoint inspection, immutable/custom/document-local scope and deferred authoring history are defined in [tool-library.md](tool-library.md); a Tool Library projection never grants execution authority. Skills are concise trusted core Markdown plus optional direct Markdown references, discovered through the compact catalog and read through one normal tool; there is no activation/dependency runtime. An installed skill is a global/host-scoped Library entity, not a chat artifact. Uploaded skill-shaped files remain untrusted artifacts until explicit confirmed import. Current reads and deferred package revision/history UX are defined in [skills.md](skills.md).
- Model-facing tools are grouped by user intent, not backend COM primitives. Collection/item reads share selectors (`excel.inspect`, `word.inspect`, `powerpoint.list_objects`); range values/formulas/profile share `excel.read_range`; scalar/formula/table writes share `excel.write_range`; chart create/update share `excel.upsert_chart`; formatting/autofit share `excel.format_range`; PowerPoint text/notes and added objects use `set_text`/`add_object`; Outlook message/attachment reads and simple updates use `read_mail`/`update_mail`; HTML uses manifest/range read, source search, whole-resource upsert, structured file patch, and exact delete. Tool/skill create+update use idempotent upsert with optional strict modes. Operations with materially different payload, destructive, or confirmation semantics remain separate instead of using a large union schema. Removed public ids and their old argument shapes are unsupported; supported tools use current exact ids and schemas.
- Native tool authority lives in immutable `ToolPolicy`. Phase 8A captures one immutable execution `ToolPackSnapshot` after run filtering: descriptor/schema, policy, handler/entry point/scope/host and package fingerprint are one registration revision, and native handlers register that captured authority directly. Since 11T10, mutable `ToolCatalogEntry` values must carry their source-owned exact policy/binding into capture; `ToolPackSnapshotFactory` fails closed when either is absent, and runtime rechecks the pinned binding/revision without id-derived fallback. Native resource catalog projections are owned by `ControllerToolCatalogEntry` and preserve the handlers' exact descriptor, schema, policy and binding. The separate `CallableToolPack` selects finite mode/host core profiles and evaluates optional exact schemas atomically at model-step boundaries only when the complete next request plus actual options, repair and continuation reserves fits. `ToolPackAdmissionJournal` writes accepted/rejected typed events to the chat stream before publication; the ordered accepted chain for the same `TurnId` pins each exact extension and its before/after snapshot revisions for confirmation/compaction/restart. Invalidated refs or a broken chain leave only core plus a visible restore state until an accepted core rebase; rejected events and raw result history grant no authority. Membership has no LRU/touch path. [ADR-0006](decisions/ADR-0006-tool-pack-snapshot.md). Tool-call status is separate from observed dispatch/effect evidence. Compact evidence survives Activity materialization/replay without duplicating the result payload. [ADR-0003](decisions/ADR-0003-tool-result-three-states.md) and the [active result contract](conversation-protocol.md#tool-result) define current typed materialization/UI behavior; Phase 4B removed the old model-result wire and 11T10 removed the final compatibility projections. Prompts cannot bypass local authority.
- Resource Fabric is a data plane. `ResourceGatewayService` owns internal provider list/resolve/search/read semantics; exact native `find/read` handlers own the public ToolRuntime entry and source policy. Resource catalog/data-plane files cannot depend on generic host execution or result conversion. `ResourceRef` is the only durable exact identity and never enters the switched model projection; CAS remains storage, not a second transport. Media bytes cross only a request-local adapter for the immediate next model step. Resource data cannot grant execution authority. [ADR-0004](decisions/ADR-0004-resource-data-plane.md).
- Custom VBA tools require a strict object JSON Schema with documented arguments. `Office.Vba.VbaPackageService` owns existing global/document-local execution, temporary/persistent package lifecycle, marker+journal-aware state and R41 recovery. Since 11T9A, package install/remove/run carries typed prepared state directly through `IVbaHostBackend` to `OfficeHosts.Vba.VbaInteropBackend`, which is bound to the exact retained document session; no host command or serialized command payload is involved. Since 11J2, `Core.Tools.ToolPackageSource` v1 is the complete immutable execution/UI source boundary, its content revision is distinct from the human package version, and `VbaPackageResult` v1 carries status, dispatch and effect evidence. Arbitrary macro dispatch remains `unknown`; persistent install/remove can claim change/no-change only from the journalled read-back. The pure ownership-marker parser is an explicit read-only `Office.Vba` contract shared with that backend; no friend-assembly access or duplicate parser is used. `Office.Vba.VbaMutationService` separately owns public rename guard/dispatch/read-back/recovery through a rename-specific domain API. `VbaPackageToolAdapter` and `VbaLegacyResultProjection` are deleted. New immutable package history and Host Fabric remain optional/unqualified contours, not claims of this cutover. Phase 8A pins immutable descriptor/policy/binding/package revisions and Phase 8B supplies finite core plus monotonic optional admission. Pipelines are disabled: no catalog, execution, authoring or editor path. The pipeline executor/parser and nested dependency traversal are removed; old definitions are skipped without migration. Reintroduction requires a separate Phase 11 decision after stable core, not a compatibility adapter.
- Model-facing VBA discovery, source reads/search, and backup access go through provider `vba` and shared `common.resources_find/read`; provider/kind/URI/revision/cursor stay internal. The compact host-neutral `common.vba_*` facade contains only write/rename, exact patch, delete, and restore mutations. Host-prefixed VBA/macro backend ids were removed in 11T9A. Mutation snapshots are acquired internally; journal preparation/terminal evidence and backup resources remain exact durable state. See [vba-mutation-journal.md](vba-mutation-journal.md) and [vba-tool-packages.md](vba-tool-packages.md).
- Generic host `get_context/get_selection` tools are removed: provider `document` exposes the bound `office-document` and `office-selection` as metadata, structure, and bounded text. Structured domain reads such as Excel ranges/formulas, Word character ranges, PowerPoint notes/objects, and Outlook messages/attachments remain typed tools because their addressing and result contracts are not interchangeable with a document snapshot.
- Accepted agent calls/results are hidden protocol messages. Visible tool activity is presentation only and is excluded from model context. Activities from one accepted model turn share a `StepId` and its user-facing `message`, so the UI can group batch calls and results without inferring boundaries from tool names or ordering heuristics.
- `Core.Services.RunViewStateProjector` is the only runtime-to-UI outcome projector. Application results, full bridge responses, chat summaries and visible run messages carry the same immutable state; bridge responses additionally carry the canonical session revision. Static UI validates the typed shape and orders projections per chat, but does not persist them or infer lifecycle/effects from model prose, `ResponseStatus` or Activity wording. The old `RunExecutionSummary` type and bridge/UI paths are removed; retained unknown JSON fields give no authority and require explicit new-chat/reset when current `KernelState` is absent.
- `Office.Services.ArtifactLibraryProjectionService` is the only owner of Artifact Library classes, groups, heads and exact history. It derives a revision-stamped read-only DTO from replayed `ChatArtifact` records, chooses active Plan/HTML pointers before revision order and never stores another index. Raw artifact DTOs remain the exact-revision viewer/message source; UI lineage inference is removed. Immutable originals display `Original`, snapshots have no version badge, and `plan_document` is normalized to the Plan/Markdown presentation without changing its resource kind or URI.
- `Office.Services.ArtifactViewerService` owns the typed already-authorized text/Markdown projection. It revalidates the active chat and canonical exact artifact URI, then pages the shared gateway representation at 32,000 characters under a 512,000-character document bound. Stable representation hash/offset/total/kind evidence is required before the UI screen owner can assemble full source; attachment text uses its extracted-text hash. The allowlisted text/Markdown adapters only render provided data, Markdown sanitization runs only after complete read, and their ephemeral per-chat page cache is neither persistence nor execution authority. HTML/JSON and future media remain separate viewer owners.
- `Office.Services.PlanDocumentService` owns Plan create/update/restore/delete validation and linear revision append. It preserves the complete Markdown payload, requires the supplied artifact id to equal the active unique head and refuses broken/skipped/branched lineage. Restore appends a new head with exact-source provenance; delete appends a tombstone and clears the active pointer without deleting revisions or rewriting pinned message refs. Library/resource/context projections omit removed Plans and exact reads fail with `resource_removed`. `PlanDocumentToolCatalog` owns the exact schemas and Plan-only verified-write policies; `PlanDocumentToolHandler` marks the direct session mutation boundary and verifies the exact artifact/active head. The former controller executor has no alias or fallback.
- `Office.Services.TaskListService` owns typed Task List create/update/close validation and immutable revision append. `TaskListToolCatalog` supplies the exact Agent/Plan verified-write contracts; `TaskListToolHandler` marks the session mutation boundary and verifies the appended artifact plus active/closed pointer. The former controller executor has no alias or fallback.
- Plan UI history actions currently pass exact server-projected revision guards to the Plan owner pending 11O2. Ready handoff revalidates active raw revision/status/URI internally, switches mode and places only a semantic find/read instruction in the Agent request.
- Provider reasoning is stored separately from agent JSON.
- Context belongs to the active chat. Compaction stores a model-produced checkpoint, a deterministic bounded union of exact resource references, and an exact raw tail without deleting the source transcript or splitting a tool exchange.
- One append-only `*.events.jsonl` stream is the durable source of truth for a chat. `ChatSession`, headers, model history, HTML navigation, chart cards, and compaction checkpoints are replayed projections; mutable chat snapshots and summary/body sidecars do not exist. Each record has a contiguous sequence and a SHA-256 chain or optional HMAC-SHA256 chain, and large immutable bodies use SHA-256 references into the shared `chat-blobs` CAS. Optional authenticated encryption protects event data and committed CAS bytes while leaving the event envelope queryable; it is disabled by default and uses either the DPAPI-protected API key or a separate DPAPI-protected custom secret. Model requests are appended after final prompt/tool/schema materialization and before network dispatch; logical turn/model-step boundaries, bounded raw streaming-frame batches, responses, failures, rejected Agent envelopes, tool boundaries, and artifact revisions use the same stream. Old snapshot formats are intentionally unsupported. See [session-events.md](session-events.md).
- Current-turn media uses an adaptive path. A chat model that declares the required Vision/Audio capability receives that modality directly in its normal request, with no duplicate helper pass. Only missing capabilities are interpreted by isolated bounded helpers selected from the attachment priority. A helper sees only its fixed instruction, the current user request, and modality-specific files—never chat history, Office runtime context, tools, or skills. Vision and Audio route independently; persisted helper evidence replaces only the routed raw media, and confirmation continuation reuses matching evidence by source fingerprint. `AttachmentHelperMaxTokens` and `AttachmentEvidenceMaxTokens` configure the helper output and primary evidence caps; `0` keeps automatic limits of 1024 helper tokens and at most 20% of the primary input budget, capped at 2048. Explicit evidence remains capped by the primary input budget. There is no endpoint/network failover.
- Resource model context exposes only compact semantic targets: the exact bound VBA project target may be read directly, while other targets are discovered through semantic find. Local paths, bodies, exact refs and internal ids are omitted. Paste/drop/paperclip still durably link CAS bodies and revisions before model dispatch. Chat/Plan/Agent use `common.resources_find/read`; runtime maps targets to exact canonical refs, applies live hash/collection guards and assembles internal pages into one complete model-facing representation or an explicit error. Model Tool Results, historical replay, compaction input and media provenance omit URI/revision/cursor state. Media is request-local to the immediate logical step and then released. Live provider calls remain document-identity guarded and serialized with Office mutations.
- A chat's non-empty `Model` overrides the global default through one cloned effective-settings resolver; requests, title generation, context budgets, and compaction use the same effective model without mutating stored global settings.
- Different chats may run concurrently; one chat has one active operation across RNAssistant windows. Runtime reset, document deletion, chat creation, document-identity migration, and background title writes use a cross-window coordination gate, so maintenance cannot race a newly started chat. Shutdown signals cancellation but retains each chat lease until that run actually exits, including a slow/non-cooperative COM call. Chat-local plan/HTML mutations use the chat lease. HostRuntime holds one operation/target gate from guard and preparation through dispatch, read-back and the existing journal terminal; live resource, manual and editor reads share it. The order is chat lease → document gate → short shared-local-state/storage locks. A keyed semaphore registry always applies; a storage root adds the bounded cross-process file lock. Only explicit synchronous STA handoff carries reentry permission; new roots and child tasks cannot borrow it. An occupied gate returns busy immediately on its owner STA. Cancellation is rechecked before the owner action; access failure or cancellation after mutation starts cannot become retryable pre-dispatch refusal. No document gate spans a model or user wait. Agent runs for archived or closed documents retain document-independent local tools; Office/VBA tools remain unavailable until the bound document is open. Excel, Word, PowerPoint and Outlook factories create one `IOfficeDocumentSession` for an exact selected workbook, document, presentation/window or Inspector/mail/Explorer/folder; current runtime identity, retained COM target and gate remain fixed for that session, and close/target drift cannot rebind to another active object. Word, PowerPoint and Outlook VSTO own a separate document/window-bound runtime per pane. These host-neutral switches do not prove real COM proxy identity, so R04, WQ0 and WQ-SESSION remain open evidence. Direct selection/context capture and VBA catalog reads enter `HostRuntime.ReadDocument` as independent operation roots. `OfficeContextCaptureService` holds preparation and selection capture in one access, returning before controller persistence; UI context is omitted on failed access. Catalog identity, cache access, module list and component reads share one gate; busy/closed access, failed/null backend results, malformed typed snapshots and read exceptions cannot publish an empty/partial cache or trigger an internal retry. A successful empty catalog remains cacheable. Saved documents use a persisted legacy document id when present and otherwise their full path; unsaved documents use runtime identity, so identity reads do not dirty Office files. Exact full-path matches migrate older chat keys to the live identity.
- `Office/Domains/Excel` owns the public `excel.inspect`, `excel.read_range`, `excel.write_range`, `excel.find_cells`, `excel.replace_cells`, `excel.add_sheet`, `excel.rename_sheet`, `excel.clear_range`, `excel.sort_range`, `excel.filter_range`, `excel.format_range`, `excel.add_table`, `excel.create_chat_chart`, `excel.upsert_chart` and `excel.delete_chart` outcomes. Exact native handlers own Agent/manual `HostRuntime` entry; HTML bind/refresh invokes the same read adapter inside its existing synchronous document access. Reads keep bounded collections, fail-closed snapshots and the 100000-cell pre-materialization ceiling. The write owner normalizes one exact scalar/formula/table target, null-pads ragged rows, reads values/formulas/formula flags before and after, and emits only verified no-change/change or explicit error/unknown. Find/replace preserves bounded literal/regex and values/formulas scope semantics; replacement verifies each exact value/formula pre-state and read-back. Sheet lifecycle preserves current name/default/active rules, rechecks the ordered collection before dispatch and verifies exact add/rename read-back. Range clear/sort/filter/format keeps the exact public schemas, resolves one contiguous target with a 100000-cell ceiling, rechecks an opaque operation-specific pre-state token immediately before COM, and requires content/order/filter/format read-back before verified evidence; autofit additionally pins the exact observed dimensions and is limited to 10000 rows or columns per requested axis. Table creation bounds one contiguous source to 100000 cells and the exact workbook collection to 1000 tables, rechecks an opaque token over source values/formulas plus that collection immediately before `ListObjects.Add`, and requires exactly one matching new table in read-back. Chat-chart source reads are limited to 10000 cells. Workbook chart mutations bound the full collection to 200 charts and each chart to 100 series, recheck an opaque token over the collection and requested source/label ranges, and verify exact created/updated/deleted state plus untouched charts. Ambiguous workbook-wide names fail closed. `ToolVerification.Tool` is authoritative for chart and other mutations; each mutation marks the boundary immediately before its first host assignment. `OfficeHosts.ExcelInteropBackend`, `ExcelFindReplaceInteropBackend`, `ExcelSheetInteropBackend`, `ExcelRangeMutationInteropBackend`, `ExcelTableInteropBackend` and `ExcelChartInteropBackend` operate only on the workbook retained by `ExcelDocumentSession`; ranges, selection, active cell and sheets must belong to that session. Their compatibility commands/backends and production host branches/helpers are removed.
- `Office/Domains/Word` owns the public `word.read_text`, `word.find_text`, `word.inspect`, `word.write_text`, `word.replace_text`, `word.format_text`, `word.add_table`, `word.insert_page_break` and `word.add_comment` outcomes. Exact native handlers own Agent/manual `HostRuntime` entry, and HTML bind/refresh uses the same typed read adapter. `WordService` preserves the exact public schemas, normalizes selection/document/range and main/selection/all story scopes, plans bounded replacements, caps table creation at 10000 cells and separates verified no-change/change from pre-dispatch error and post-dispatch unknown. `WordInteropBackend` rechecks operation-specific target state before the first COM assignment and requires exact read-back for writes, replacements, formatting, tables, page breaks and comments. It operates only on the document retained by `WordDocumentSession`; selection ranges must belong to that document. Public Word generic host branches/helpers and execution-time `ActiveDocument`/descriptor resolution are removed.
- `Office/Domains/PowerPoint` owns the public `powerpoint.read_slides`, `powerpoint.search_text`, `powerpoint.replace_text`, `powerpoint.add_slide`, `powerpoint.set_text`, `powerpoint.add_object`, `powerpoint.duplicate_slide`, `powerpoint.move_slide` and `powerpoint.list_objects` outcomes. Exact native handlers own Agent/manual `HostRuntime` entry, and HTML bind/refresh uses the same typed read adapter. `PowerPointService` preserves the exact public schemas, bounds slide/shape/text/table/image inputs and separates verified no-change/change from pre-dispatch error and post-dispatch unknown. `PowerPointInteropBackend` rechecks operation-specific presentation state before the first COM assignment and requires exact read-back for replacement, slide creation/duplication/move and shape text/object mutations. It operates only on the presentation retained by `PowerPointDocumentSession`; selection reads are tied to its retained window. Public PowerPoint generic host branches/helpers and execution-time `ActivePresentation`/descriptor resolution are removed.
- HTML workspace state belongs to the chat, is revisioned as immutable artifacts, and remains sandboxed with explicit network-origin permission. `Office.Services.HtmlWorkspaceArtifactService` owns the whole-workspace lineage: each save takes `max(all branch revisions)+1`, records the exact active artifact as parent, and rejects duplicate/invalid revision graphs before mutation or pointer restoration. `ActiveHtmlArtifactId`, not the numerically greatest revision, remains the authoritative head after undo. `Office.Services.UploadedHtmlResourceService` separately owns uploaded-HTML validation, bounded source projection and explicit import. It accepts only the exact canonical immutable attachment revision for the active chat with matching attachment identity/hash/length and HTML MIME/name. Source preview reads at most 32,000 characters through `ResourceGatewayService`; the UI places it only in `textContent`. Import requires the exact active HTML head, a new `.html`/`.htm` path and complete decoded source within 300,000 characters, then creates a normal workspace revision carrying source URI/hash/relation provenance without changing or executing the original. Only that imported workspace content may enter sandbox preview. `HtmlWorkspaceToolCatalog` and `HtmlWorkspaceToolHandler` own all eight exact Agent-only runtime registrations; `HtmlWorkspaceToolService` owns typed validation, mutation and read-back outcomes without a controller executor or legacy command/result roundtrip. Static inspection is an independent read; each mutation has source-owned `Write + ToolVerification` policy and marks dispatch before changing chat state. A data source may carry a binding to a `CanSourceHtmlData` read-only adapter tool; bind validates its exact schema and executes only the matching typed Excel/Word/PowerPoint/Outlook read adapter under the shared document gate, refresh revalidates and replays it under the same gate while retaining prior JSON on failure, and freeze removes the binding. There is no generic `IOfficeApplicationAdapter.ExecuteTool` fallback for HTML binding. The typed tool plus arguments are intentional because a parameterized Office range is not faithfully represented by a generic resource URI. Automatic refresh does not add artifact revisions; the next chat turn checkpoints current live data normally. Undo follows parent artifacts; its projection remains bounded to 20 items and 2,000,000 content characters. Redo is valid only to a direct child; one child can be selected implicitly, while multiple children require an explicit artifact id exposed as metadata-only `redoBranches`. There is no mutable redo stack, and child CAS bodies load only after selection. Recovery status and candidate metadata are rebuilt from the validated artifact graph: an unreadable active CAS body blocks mutation and requires explicit verified revision selection, while a missing ancestor only truncates undo history and does not invent lineage. Unrelated chat saves cannot manufacture an empty child over a damaged active revision. The `activeHtml` revision exposes a bounded `structure` manifest; current files and data are advertised as readable semantic targets. `common.resources_find` performs bounded literal discovery and `common.resources_read` resolves exact revision-pinned member state plus continuation only inside runtime. Static preflight inspection remains a domain read-only tool; it checks the selected entry plus injected CSS/scripts/data for assembly, CSP, duplicate-id, and likely missing-reference problems without executing JavaScript. Structured edits apply an ordered patch atomically to current file text and create one artifact revision. WebView bridge messages are accepted only from the canonical local `web/index.html`; top-level/frame navigation, permissions, popups, and direct preview networking are restricted by host policy and CSP.
- The generic session store never compares mutable HTML workspace state or creates a workspace artifact. The HTML domain owner alone checkpoints refreshed binding JSON at the next chat turn or an explicit exact-head export. Each binding carries exact JSON SHA-256 plus explicit completeness; export returns the checkpoint's pinned URI and CAS hash before local assembly, which embeds raw JSON strings without a numeric/object normalization round trip.
- The Artifacts UI shows chat-owned artifacts and HTML workspace state only. The active document's VBA project is a live document resource in a separate VBA tab, not a `ChatArtifact`: chat fork/prune/rewind does not copy, remove, or restore it. The VBA tab can create a blank UserForm and edit its code-behind. A `CodeOnly UserForm` may build all controls, layout, runtime-settable properties and event bindings from that source; the blank Designer is only a generated host shell. Designer-time controls/properties and `.frx` assets remain outside the protocol. UI deletion is limited to standard/class modules and goes through current-hash validation plus rollback backup. See [vba-userforms.md](vba-userforms.md).
- Model request diagnostics are passive request-scoped milestones from the normal LLM pipeline (`preparing`, `sending`, headers, first data, terminal state). The bridge forwards typed `modelDiagnostics` events, and the manual connection probe uses the same completion path with bounded output and timeout; there is no background polling. Active Office event writers/readers use one closed `IEventStore` port over the existing `ChatStore`: each current top-level event has source-owned lane, authority, durability and write-scope classification; storage lifecycle events cannot be appended through the port, and arbitrary Office event strings are rejected. Session/controller/kernel aggregate consumers separately use one minimal `IConversationStore` adapter over that same backend for load/save, header/active projection, move/delete and interruption recovery intent. Artifact bodies, HTML revision activation, raw events, reducers and CAS maintenance stay with their existing owners; neither port adds a stream, snapshot or dual-write. Diagnostics queries canonical session events through disposable `ITrajectoryQuery`: raw cursor pages and snapshot-paged derived model/tool/artifact/confirmation/failure/turn views are rebuilt from the validated stream and never become durable state. Phase 9A adds chronological `run-causal`, preserving exact attempt/call/origin/mutation/journal and source-event evidence; explicit terminal gaps never infer an outcome. Accepted calls are classified by runtime `AcceptedCallOrigin`, independently of model-result role/native transport shape. Every derived row retains complete source event sequences and ids; CAS payload bodies remain lazy. Diagnostics also exposes repository-wide CAS health/GC; collection rebuilds reachability from every validated chat/VBA source under the cross-window maintenance gate and deletes only canonical proven orphans. Any invalid or incomplete source blocks all deletion. See [session-events.md](session-events.md), [trajectory-query.md](trajectory-query.md) and [cas-maintenance.md](cas-maintenance.md).
- Phase 9 diagnostics uses the UI-only allowlisted `ViewerRegistry` after the correlated `ITrajectoryQuery` projection. Its 9B1 JSON adapter owns immutable raw/token spans, bounded parse/lazy DOM and exact copy without converting authoritative payloads to a JS object. A screen owner resolves and bounds content, then passes completeness/redaction metadata and already loaded payload to the adapter. Registry/vendors never fetch from bridge/CAS/network, own storage, or add a generic artifact envelope to the model protocol: durable transport retains Tool Result v1 plus revision-pinned `ResourceRef`, while `ModelToolResultProjection` removes the exact reference from model history. Phase 9C renders already loaded `run-causal` rows through `RNAssistantRunJournal`: the component owns bounded chronological presentation, filters and expansion only; raw events/CAS payloads stay with the existing Diagnostics owner and typed evidence remains authoritative. The journal marks persisted API request/response rows visibly, provides a dedicated body filter and automatically asks the existing lazy Diagnostics owner for the bounded persisted CAS body preview when the user expands one row; accepted arguments and tool results stay inline, while correlation IDs remain under a collapsed technical section. A local Worker is permitted only as an exact vendored same-origin asset created through a host-owned allowlist/factory with bounded lifetime; it is not a network dependency. Structured logs use this chronological event viewer; a terminal emulator is reserved for a separately typed real process/PTY artifact, not diagnostic text. See [R32](stabilization/R32_DIAGNOSTICS_JSON_VIEWER.md), [9C evidence](stabilization/PHASE_9C_RUN_JOURNAL_UI.md) and the [vendor evaluation](stabilization/R32_VENDOR_UI_EVALUATION.md).
- `AssistantRuntime.Dispose` owns pane/bridge/controller shutdown and cancels active bridge requests, chat runs, and background title generation before a host adapter or dispatcher is released.
- Each Excel window pane is bound directly to its workbook COM object and only the active pane is refreshed on window changes; inactive WebViews do not rescan tools or take focus.
- Each Word window pane owns a runtime bound directly to that window's document COM object; activation and close events refresh or dispose only the affected retained runtime, never rebind it through `ActiveDocument` during execution.
- Desktop COM calls enter adapters through the dedicated STA dispatcher. In-process VSTO and NativeHostCli adapters marshal every Office call back to the host UI thread through `OfficeUiDispatcher`; VSTO/COM changes still require Windows validation.

## Main code zones

- `src/RNAssistant.Core/Llm`: HTTP transport, message construction, response/reasoning parsing, budgets.
- `src/RNAssistant.Core/ModelProtocol/ConversationResponseParser.cs`: strict conversation-response v4 parser; it validates model drafts but neither assigns runtime call IDs nor executes tools.
- `src/RNAssistant.Core/Tools/VbaPatchEngine.cs` and `VbaTextCanonicalizer.cs`: pure VBA text operations/representations, shared by parser/storage and Office consumers. JSON/tool mapping, COM, guards and journal orchestration stay outside; Phase 6A preserves algorithms and does not qualify production binding.
- `src/RNAssistant.Office/AssistantRuntime.cs`: public application/UI lifetime façade for controller and pane construction/disposal; document/tool coordination remains in `Runtime`.
- `src/RNAssistant.Office/Vba/VbaReader.cs`: единственный host-neutral owner internal VBA list/module command construction, deterministic name fallback and typed snapshot validation. Callers already hold the `HostRuntime` document gate; reader does not own target binding, mutation dispatch, journal persistence or Tool Result v1. Dynamic host COM/VBE now lives only in `src/RNAssistant.OfficeHosts/Vba/VbaProjectSupport*.cs`; Office consumes no host helper or duplicate backend.
- `src/RNAssistant.Office/Domains/Excel`: typed Excel read/write/find/replace/sheet/range-mutation/table contracts, canonical bounded snapshots, verified mutation outcomes and narrow bound-backend provider ports.
- `src/RNAssistant.Office/Domains/Word`: typed Word read/search/inspection and mutation contracts, replacement planning, verified outcomes and one narrow bound-backend provider port.
- `src/RNAssistant.Office/Domains/PowerPoint`: typed slide/shape reads and mutations, bounded contracts, verified outcomes and one narrow bound-backend provider port.
- `src/RNAssistant.OfficeHosts/Excel`: production `ExcelDocumentSession` and direct read/write, find/replace, sheet, range-mutation, table and chart backends for one exact workbook; compatibility backends/internal command ids and replaced host branches/helpers were removed in 11T0/7D–11T5.
- `src/RNAssistant.OfficeHosts/Word`: production `WordDocumentSession` and direct read/mutation backend for one exact document; public generic host branches/helpers and execution-time target fallback were removed in 11T6.
- `src/RNAssistant.OfficeHosts/PowerPoint`: production `PowerPointDocumentSession` and direct read/mutation backend for one exact presentation/window; public generic host branches/helpers and execution-time target fallback were removed in 11T7.
- `src/RNAssistant.OfficeHosts/Outlook`: production `OutlookDocumentSession` and direct read/mutation backend for one exact Inspector/mail or Explorer/folder; public generic host branches/helpers and execution-time active-window fallback were removed in 11T8.
- `src/RNAssistant.Office/Vba/VbaMutationService*.cs`, typed mutation contracts and `VbaVerifier.cs`: owners of complete `common.vba_apply_patch`, whole-module `upsert/createOnly/updateOnly` including rename, `common.vba_delete_module`, and `common.vba_restore_backup` workflows. They own target/observation guards, exact backup binding for restore, prepared journal, typed create/replace/rename/delete/restore actions, source/type or absence read-back and terminal assessment. Rename binds both names plus source type/hash, uses a narrow rename journal port over the existing two-component wire, and never replays an interrupted effect. The service receives only narrow document-context, read, backend and journal ports; restore sees an immutable backup snapshot without storage DTO/CAS-reference access. It returns `Ok/Error/Unknown` and cannot construct tool commands, consume/map legacy `ToolResult`, choose a document or open the `HostRuntime` gate. Internal journal classification remains durable diagnostics evidence and is not a common tool-result field. Compile validation is separate and is not inferred from source read-back.
- `src/RNAssistant.Core/Storage`: settings, chats, tools, skills, attachments.
- `src/RNAssistant.Core/Models/SessionEventModels.cs`: canonical event envelope and typed state-operation vocabulary.
- `src/RNAssistant.Core/Storage/ChatBlobStore.cs`: shared content-addressed immutable payload store for chat payloads, artifacts, committed attachments, and VBA source snapshots.
- `src/RNAssistant.Core/Storage/CasMaintenanceService.cs`: validated cross-stream reachability, CAS health classification, and fail-closed orphan collection.
- `src/RNAssistant.Core/Storage/VbaJournalStore.cs`: document-scoped append-only VBA mutation/backup source of truth and its replayable projections.
- `src/RNAssistant.Core/Services/ChatSessionNormalizer.cs`: format-preserving chat normalization shared by storage operations.
- `src/RNAssistant.Office/Services/ConversationRunService.cs`: shared structured Chat/Agent loop.
- `src/RNAssistant.Office/Runtime/HostRuntime.cs`: document expectations, neutral bound-session access and full guard/preparation/dispatch/read-back scopes. `DocumentAccessGate.cs` owns synchronous operation/target reentry, ordering and bounded acquisition; `Contracts/IOfficeDocumentSession.cs` defines the host port. Production Excel binding is delivered host-neutral by 11T0/7D, Word by 11T6, PowerPoint by 11T7 and Outlook by 11T8 under the deferred-qualification identity assumption; see [ADR-0005](decisions/ADR-0005-bound-document-session.md).
- `src/RNAssistant.Office/Services/ConversationModelSession.cs`: Office-owned model-context lifecycle and result/media materialization, outside the loop.
- `src/RNAssistant.Office/Agent/AgentTranscript.cs`: visible messages, tool activity and resource/chart provenance.
- `src/RNAssistant.Core/ModelProtocol`: typed conversation response/failure boundary and raw attempts; no Office dependency or tool execution.
- `src/RNAssistant.Office/Services/ConversationRunPolicy.cs`: hard mode boundary for tools, skills, and confirmation.
- `src/RNAssistant.Office/Services/ConversationPromptComposer.cs`: mode instruction and runtime context.
- `src/RNAssistant.Office/Services/CallableToolPack.cs`: deterministic core profiles, live current-run evidence staging, atomic full-request-budget admission and exact durable-snapshot rematerialization; raw history never restores admission.
- `src/RNAssistant.Office/Services/ToolPackAdmissionJournal.cs`: typed accepted/rejected extension events in the canonical chat stream and latest-accepted lookup scoped by logical `TurnId`.
- `src/RNAssistant.Office/Services/ResourceProviderRegistry.cs`: exact provider registration and dispatch boundary.
- `src/RNAssistant.Office/Services/ResourceGatewayService.cs`: common resource orchestration and URI-based dispatch.
- `src/RNAssistant.Office/Services/ResourceReadCursor.cs`: immutable offsets, live revision-bound continuations, and collection-drift validation.
- `src/RNAssistant.Office/Services/ChatArtifactResourceProvider.cs`: bounded chat-owned metadata, text, and one-step media hydration.
- `src/RNAssistant.Office/Services/ToolResultResourceService.cs`: bounded generic result externalization into exact CAS-backed resources.
- `src/RNAssistant.Office/Services/ContextCompactionService.cs`: optional checkpointing.
- `src/RNAssistant.Office/Tools`: dispatch, schemas, tool/skill/prompt CRUD, VBA lifecycle.
- `src/RNAssistant.Office/Tools/CapabilityToolCatalog.cs`, `CapabilityToolHandler.cs`, and `CapabilityCatalogService*.cs`: source-owned Agent/Plan read policies, exact native bindings, compact tool/skill metadata, bounded search, and unified exact tool-schema/skill/reference reads without a controller command/result or `SkillToolExecutor` path.
- `src/RNAssistant.Office/Tools/VbaToolHandler.cs`: exact native public VBA/macro handler. It prepares under the document read gate, persists an opaque bounded guard through ToolRuntime confirmation and executes under the bound mutation gate with explicit dispatch/effect evidence.
- `src/RNAssistant.Core/Tools/ToolPackageSource.cs` and `src/RNAssistant.Office/Tools/VbaPackageToolHandler.cs`: versioned immutable custom-package source plus exact native execution binding. `VbaToolExecutor*.cs` adapts only typed VBA domain calls and narrow remaining resource/editor seams. Patch, whole-module write/rename, delete, restore and package lifecycle/journal/read-back owners live in `Office/Vba`; deleted controller/compound/package projection helpers have no compatibility alias. Package and module outcomes never infer rollback from prose.
- `src/RNAssistant.Office/Tools/UserQuestionToolHandler.cs`: exact Plan-only native local-interaction handler. It validates the typed question set and returns ToolRuntime `AwaitingUser`; AgentKernel, not result prose, owns the pause.
- `src/RNAssistant.Office/Tools/PlanDocumentToolHandler.cs`: exact native Plan mutation handler over `PlanDocumentService`; pre-dispatch validation remains a known error and successful revision/tombstone appends require exact post-state evidence.
- `src/RNAssistant.Office/Tools/TaskListToolHandler.cs`: exact native Agent/Plan checklist handler over `TaskListService`; create/update/close return verified revision evidence rather than legacy success inference.
- `src/RNAssistant.Office/Controller`: typed bridge-facing orchestration.
  Chat/session bridge methods принадлежат `AssistantController.Chats.cs`, context
  capture — `AssistantController.Context.cs`; reusable behavior остаётся в
  тематических `Services`, а не переносится в controller partials.
- `src/RNAssistant.OfficeHosts`: Excel/Word/PowerPoint/Outlook COM adapters.
- `web/js/app-html-workspace.js`: HTML workspace view orchestration; normalized data/selection rules, editor state/rendering, sandbox assembly, artifact/plan presentation, resource-tree rendering, and mutation bridge calls live in the adjacent `app-html-workspace-model.js`, `app-html-workspace-editor.js`, `app-html-workspace-preview.js`, `app-html-workspace-artifacts.js`, `app-html-workspace-tree.js`, and `app-html-workspace-actions.js` modules.
- `web/js/app-chat-session.js`: chat/document CRUD, bridge initialization, and navigation synchronization; composer rendering/input state lives in `app-chat-composer.js`, send/retry/cancel, run tracking, and Agent tool decisions in `app-chat-run.js`, while `app-chat.js` keeps chat-level actions and bindings.
- `web/js/app-model-render.js`: model info/status coordination; catalog selects and the composer picker live in `app-model-picker.js`, while editable capability overrides live in `app-model-capabilities.js`.
- `web/js/app-tools.js`: tool catalog and editor state; schema/run-argument editors live in `app-tools-structured.js`, while save/run and VBA package bridge calls live in `app-tools-actions.js`.
- `web/js/app-vba.js`: VBA editor modes and UI bindings; the separate project tree and lazy module loading live in `app-vba-project.js`, diff calculation/rendering in `app-vba-diff.js`, and save/delete/restore/run bridge calls in `app-vba-actions.js`.
- `web/js/app-agent.js`: Agent run grouping and article composition; individual activity rendering lives in `app-agent-activity.js`, while pending-confirmation traversal and the approval dock live in `app-agent-approval.js`.
- Other `web/js` files remain static feature modules; no agent routing or business rules.
- `web/js/app-trajectory.js`: read-only diagnostics projection over session events and external payloads.

## Harness

Run the host-neutral fast suite with:

```bash
dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
```

For test discovery, category/name filters and `--no-build` usage, see
[`tests/RNAssistant.Harness/README.md`](../tests/RNAssistant.Harness/README.md).

The suite covers the Agent parser/prompt/tool-result loop, durable confirmation counters, chat revision conflicts and run leases, Chat isolation, storage, context, pipeline rejection, tool safety, VBA package/backup behavior, attachments, HTML workspace, and typed bridge payloads. It has no Office COM dependency.

The `architecture:` slice additionally checks forbidden source dependencies across
Core.Agent, ModelProtocol, VBA, resources, OfficeHosts and the UI/bridge. These are
dependency-direction checks, not a requirement that every public façade namespace
mirror its organizing folder. Old-style production source inclusion remains a
separate `harness:` check.

Windows-only validation remains: build `Debug | x64` in VS 2022 and smoke-test each Office host, desktop attach, VSTO task panes, and VBA native-host loading.
