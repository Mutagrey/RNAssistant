# Resource Fabric

Status: implemented through R61/11O1 host-neutral. Providers retain exact typed resource state; the public model surface is the semantic `common.resources_find/read` pair. Replaced public list/resolve/search handlers and ids are removed without aliases.

## Goals

- A pasted, dropped, or attached file immediately becomes a chat-scoped resource draft; Send promotes it to a durable chat-owned resource before model dispatch. No separate “В запрос” action is required.
- Model context keeps compact semantic targets and a bounded working set, never exact resource identity or every artifact body.
- Chat can use read-only resources; Agent uses the same reads plus policy-approved mutations.
- A multimodal primary model reads supported media directly. A helper model is used only when the primary model lacks that modality or when durable derived text is explicitly useful.
- HTML, plans, uploads, generated images, live Office content, VBA projects, tool results, and future objects share discovery/read contracts without losing domain-specific mutation semantics.

## Domain model

`Resource` is addressable data, never execution authority, callable-tool state, or a hidden command. A resource may be live and mutable, such as the active workbook, or backed by immutable revisions, such as an uploaded image or plan. Reading it does not grant a tool, change a `ToolPack`, or bypass domain mutation policy.

`ChatArtifact` is an internal replay projection of immutable content/provenance stored through CAS. It is not a second model transport. Active HTML/plan/checkpoint ids and exact revision URIs remain runtime/domain pointers; model context receives readable semantic targets. There is deliberately no mutable model-facing head URI whose meaning could change during replay.

`ResourceRef` is the compact durable identity carried by messages, accepted tool
results and events, but not by the R61 resource/capability model projection:

```json
{"uri":"rna://chat/s1/artifact/a1/revision/2","revision":"2"}
```

Canonical URIs use `rna://<provider>/<escaped-segments>`. They never expose local paths, credentials, or provider implementation details. Query strings, fragments, dot segments, encoded separators, and non-canonical spellings are rejected. Immutable chat URIs encode the revision and are checked against `ResourceRef.Revision`; a live resource keeps a stable URI and reports a content-hash revision after materialization.

`ResourceRef` intentionally contains identity only. Contextual relations belong to the owning message/tool envelope or to descriptor lineage (`Parent`/`Related`); for example, a tool envelope marks the full externalized value with `relation:"result"`. This avoids creating different identities for the same revision. A separate `ETag` field is unnecessary: for live reads, `Revision` is the observed content hash used by cursors and guards.

`Tool` is an executable capability, not a resource. An installed `Skill` is trusted
global/host-scoped instruction content, not a `ChatArtifact`; it may name resources
and tools but is loaded only through exact capability read. An uploaded `SKILL.md`
remains an untrusted immutable chat resource until explicit installation creates a
separate skill package revision. See [Skill Library](skills.md). Domain mutations
remain typed tools because safety, confirmation, and compare-and-swap rules differ
by domain.

## Providers

Office owns a registry of resource providers. The common layer knows only provider contracts.

Registered providers:

- `chat`: uploaded files, images, audio, generated artifacts, plans, HTML revisions, chart payloads, and tool-result artifacts;
- `document`: bounded structure and content from the active Office document;
- `vba`: project/component metadata, bounded source reads, and journal-backed backup reads.

Within one replayed chat, an immutable artifact id must be unique
case-insensitively. If projection corruption produces duplicates, every ambiguous
revision is excluded from list/search and exact resolve/read fails closed; the
provider, shared reference helpers and bounded model prompt never choose an
arbitrary first artifact.

Every provider still implements bounded internal `list`, `resolve`, `search`, and
`read(ResourceReadRequest)`. Those typed calls carry the exact `ResourceRef`,
representation, opaque cursor and size so revision evidence cannot be lost. R61/11O1
does not weaken their SHA-256 scope bindings, immutable revision checks or live
content/collection drift failures.

The public surface is now `common.resources_find` and `common.resources_read`.
`find` accepts only optional literal `query` and semantic `scope`, returns at most
20 readable targets, distinguishes true empty from unavailable/partial scopes and
keeps provider routing, kind vocabulary and paging internal. `read` accepts one
exact returned `target`, optional representation and semantic `action=read|next`;
runtime resolves the exact reference and returns fixed 8,000-character chunks.
`next` reconstructs the accepted internal read chain from durable results. URI,
revision/hash, cursor, offset and page size are absent from schema, model result,
`RUNTIME_CONTEXT`, media provenance and replayed model history. Ambiguity or drift
fails closed; no opaque replacement candidate id is exposed.
[Tool Library R61](tool-library.md#mandatory-all-tool-contract-audit-r61) owns the
inventory and acceptance gates; the observed empty-list/kind/revision/cursor cluster
and target ownership boundary are classified in the
[R61 tool contract audit](stabilization/R61_TOOL_CONTRACT_AUDIT.md).

HTML workspace mutations still retain exact artifact/member `ResourceRef` values in
their durable results. The central internal resolver translates a semantic target
to the exact artifact/member key. Exact chat resolution distinguishes invalid URI, active-chat mismatch,
missing artifact/revision/member, noncanonical member key and corrupt persisted
payload. Tool errors include the stable code and a recovery hint; the enclosing
Tool Result `tool_call_id` remains the correlation identity, so Resource Fabric does
not introduce a second correlation protocol.

All eight public `common.html_workspace_*` / `common.html_data_*` operations use
exact Agent-only native registrations. Static inspection is a source-owned
independent `Read`; the seven mutations are `Write + ToolVerification` and mark
dispatch immediately before the first possible chat-workspace change. Their typed
handler returns exact revision/member `ResourceRef` values at Tool Result root.
There is no HTML controller executor or `ToolCommand`/legacy `ToolResult`
roundtrip. Bind/refresh call only the already-bound typed Excel, Word, PowerPoint,
or Outlook read adapter under the shared document gate; an unknown source cannot
fall through to generic host dispatch.

Text availability is based on exact body/extraction evidence, not on whether the
text contains non-whitespace characters. A valid empty or whitespace-only immutable
representation is returned as complete exact content; a missing body still fails
closed.

Semantic find is bounded case-insensitive literal search plus provider structure. Regex, embeddings, and a durable vector index are intentionally absent until they have a concrete use and bounded semantics. Skills are trusted instructions, not untrusted document resources: their runtime revision-matched bodies are read through the unified `common.capabilities_read` id path shared with tool schemas. HTML files/data and plans remain subresources of the chat provider so ownership, revision lineage, and CAS checks are not duplicated. The existing host-neutral `IOfficeApplicationAdapter` supplies document/VBA reads; a second `IOfficeResourceAdapter` would only repeat that boundary.

## Conversation loop

Chat and Agent use one buffered structured loop. The policy differs, the transport and transcript do not:

- Chat receives exactly the two resource discovery/read tools and no mutation tools, confirmation, or skills.
- Agent keeps the complete mode/session-filtered catalog only as local execution authority. The initial model prompt contains resource and unified capability bootstrap schemas plus a compact exact-id tool/skill catalog.
- `RUNTIME_CONTEXT.capabilities.items` contains the complete compact exact-public-id index without catalog, package or descriptor revisions. `common.capabilities_search` accepts only query/kind; `common.capabilities_read` accepts exact id and, for a listed skill reference, path plus `action=read|next`.
- Tool schemas start from a finite mode/host core. Runtime validates hidden exact revisions and may stage one atomic optional extension for the next model-step boundary only after the full request fits and its accepted event is durable. Membership is monotonic for the logical turn; model-visible state reports public ids and admission outcome only.
- Skill bodies remain runtime revision-matched context evidence rather than callable-pack membership. Stale evidence is projected as an explicit error; compaction, truncation, or revision change requires another exact read.

The prompt contains bounded semantic resource targets. On a later question such as
“что на той картинке?” the model finds and reads that target. Raw media is hydrated
only for the next model step and then released; its durable exact reference remains
outside model context.

Both public resource operations execute as exact native read-only `ToolRuntime` handlers over the same `ResourceGatewayService` and provider registry. Their descriptor, policy, and binding are source-owned beside the handler; no legacy adapter or second authority exists. Every call enters a fresh `DocumentAccessGate` operation root. The accepted durable result retains provider-bounded data plus exact `ResourceRef` values for continuation/provenance, while `ModelToolResultProjection` emits the same strict Tool Result v1 correlation/status with semantic data and no opaque state. A successful chunk is passed intact or replaced by explicit `resource_evidence_context_too_large`. Media bytes are request-local, appear only on the immediate model step, and are released afterward.

Ordinary large tool results keep the full value exact. The result envelope carries optional `resources:[{uri,revision,relation?}]`; `relation:"result"` distinguishes the CAS-backed `tool_result` containing the complete payload from other produced/cited resources. Materialization selects the largest inline projection that keeps the shared repair and continuation reserves; a zero-preview case uses a compact externalized marker rather than pretending an oversized truncation wrapper fit. Resource/schema/skill reads are not rewrapped as untrusted artifacts. Chart payloads become their specialized immutable artifact at the same result boundary, so the next model step receives kind plus exact URI rather than a duplicate chart body. Durable activity also keeps only that pointer; the storage/UI projection rehydrates the chart from CAS instead of storing or creating a duplicate.

For the switched resource/capability families the durable accepted result owns the
exact `resources` relation and hidden revisions, while the model-facing Tool Result
omits them. Media is hydrated by runtime from that evidence; this is one accepted
event with separate runtime/model projections, not a second store or authority.

## Ingestion and derived data

Paste, drag-and-drop, and the paperclip all call the same chat-scoped ingestion pipeline. Bytes are staged while the composer remains editable; sending the turn promotes them into the durable resource graph in a fixed recoverable order:

1. Validate type/size and stage bytes under the target chat id.
2. On send, copy bytes into CAS, append attachment/artifact revision events, and bind the canonical revision to the user turn before model dispatch.
3. Extract cheap deterministic representations once (metadata, safe text, page structure).
4. Route supported media directly to a multimodal primary model for the current turn.
5. If the primary model cannot consume the modality, call a bounded helper with only the current request and selected media.

An attachment-backed representation is admitted only when its artifact resolves to
exactly one source message and one metadata-named attachment, and the attachment CAS
SHA-256 and byte length match the immutable artifact revision. Missing, ambiguous,
failed, or mismatched provenance remains metadata-only; the provider never falls
back to another message with the same attachment id or to an artifact inline body.

Drafts are not artifacts, are not listed in model context, and may appear outside
the composer only in a separately labelled non-durable Drafts group. After CAS,
message/artifact linking and the mandatory chat save succeed, application queues a
full monotonic `sessionRevision` projection containing the committed turn and exact
artifact revisions before attachment-helper or primary model transport. Model
execution does not wait for WebView acknowledgement; missed delivery recovers by
chat reload. Model failure after this boundary never rolls the committed resource
back to a draft. Phase 11A2 derives exact Library heads/history from the replayed
session, keeps message cards pinned to raw exact revisions and selects the active HTML
branch pointer instead of a client-guessed maximum revision. The full user-visible
lifecycle is defined in [Artifact Library and Viewers](artifact-library.md).

Uploaded HTML follows the same immutable ingestion path and never executes when it
is selected. Its UI source view is one exact revision-pinned gateway read capped at
32,000 characters with explicit completeness/truncation metadata. An explicit import
revalidates that same attachment identity and complete decoded text, then creates a
separate HTML workspace revision with the source URI, content hash and relation in
artifact provenance. The original message reference and CAS object are unchanged;
there is no second resource identity, automatic conversion or viewer execution path.

Helper output is query-specific evidence for that model step. It is not silently treated as a complete durable description. Reusable OCR/transcription may be stored as a derived artifact revision with explicit provenance: source URI, extractor/model, parameters, timestamp, and content hash.

## Context and storage

The append-only session event stream remains the durable source of truth. Events store resource references and CAS references, not copied bodies. Model requests persist the exact materialized working set before dispatch.

Compaction preserves user intent, decisions and complete tool protocol pairs. The durable checkpoint retains the bounded exact references needed for reachability and replay, including resources attached to presentation-only activity messages; its model projection exposes only semantic targets. It may remove hydrated bodies and old read results. A later read reconstructs exact evidence from the provider. CAS garbage collection derives reachability from verified event streams and journals as before.

Live Office/VBA resources are bound to the chat's document identity and carry content-hash revision evidence on materialized reads. Public `common.resources_find` maps semantic `vba`/`backups` scopes to internal provider list/search and returns readable targets; `common.resources_read` resolves a target and reconstructs `next` from durable exact evidence. Provider vocabulary, opaque document token, URI, revision and cursor never enter model arguments or results. Document-key migrations retain prior keys in the append-only projection, so an internal exact URI survives first save and Save As while runtime identity proves that the live target is still the same document. Provider calls share the document mutation gate, so journal reconciliation and source reads cannot observe an in-flight VBA mutation. Mutations keep domain-specific guards, confirmations, journals, and read-back verification; Resource Fabric does not bypass them.

## Domain projections and UI

Resource access is unified at the model/runtime boundary, not forced into one generic editor. The Artifacts view renders chat-owned attachments, plans, immutable chart snapshots, and HTML workspace revisions; VBA stays a live document view with its own editor and journaled mutations. Installed skills stay in Library and are not duplicated into Artifacts; a skill mutation may expose only a UI link to that Library entity. Message resource cards resolve exact revisions. Paste, drop, and paperclip are the normal attachment path. A future explicit `@artifact` composer affordance may insert a readable semantic target for disambiguation; runtime resolves it to the exact durable reference, and no second transport is introduced.

The library distinguishes immutable originals/snapshots, versioned domain
documents/aggregates and derived resources. Uploaded TXT/Markdown/HTML remains an
immutable original; extension alone never enables editing or execution. Immutable
items display `Original`, while Plan/HTML/authored documents expose exact revision
history. Specialized viewer, edit, restore and delete behavior is canonical in
[Artifact Library and Viewers](artifact-library.md); UI viewers consume bounded
gateway representations and never read CAS or grant execution authority.
11D1 exposes text/source and Markdown only through a typed controller projection
over the same `ResourceGatewayService`: the active chat and canonical revision URI
are revalidated on every fixed 32,000-character page. A stable representation hash,
offset and total bind continuations; attachment pages use extracted-text hash rather
than binary-media hash. The UI may assemble a full source only inside the explicit
512,000-character viewer bound, and it stores pages only in an ephemeral per-chat
cache. This creates no resource, event, index or model-facing transport.
Plan create/update/restore/delete is owned by `Office.Services.PlanDocumentService`:
a Save keeps the complete Markdown string unchanged and appends only after the
supplied artifact id is still the unique linear head; restore copies an exact prior
revision into a new head; delete appends a tombstone instead of removing revisions
or rewriting message refs. Removed Plan revisions are absent from list/search and
new working-set/compaction admission. Exact resolve/read reports non-retryable
`resource_removed`, so discovery remains read-only and never becomes a second Plan
mutation path.

HTML data bindings intentionally persist an approved typed read-only `toolId + arguments` contract, document identity, transform, last-good exact JSON, its SHA-256 and explicit `complete|bounded|truncated` evidence. A generic resource URI cannot represent parameterized reads such as an Excel range without recreating a tool contract inside the URI. Bind and refresh therefore revalidate the current tool schema and execute the exact typed read backend inside the shared document gate; failure retains the last-good value, and no generic adapter command is available as fallback. Refresh is not independently durable: the sole HTML lineage owner checkpoints it at the next chat turn or guarded export, and ordinary storage saves cannot create revisions. Export returns that exact revision-pinned resource URI and CAS hash before local standalone assembly; the raw JSON string is not normalized through a JavaScript number/object round trip. Charts are immutable data snapshots with a human-readable source locator as provenance, not live bindings; current data requires regeneration or an explicit HTML binding.

## Audit decisions

- Keep three providers instead of separate plan, HTML, skill, and media provider layers.
- Keep exact revision URIs and active pointers inside durable/runtime state instead of mutable or opaque model-facing heads.
- Keep structured range/slide/mail reads and every mutation as typed tools; generic resources cover discovery and bounded content, not all domain commands.
- Keep literal bounded search and disposable in-memory projections; do not add regex, semantic search, or a durable vector index speculatively.
- Keep specialized UI projections over the shared backend instead of a single weakly typed artifact editor.

## Removed architecture

The completed cutover removes:

- required manual “В запрос” attachment selection;
- `PlainChatService` and its no-tools request path;
- model-facing `common.artifacts_*` and duplicated plan/HTML/VBA/Office read tools;
- full tool-catalog injection on every Agent step;
- attachment bodies or generic helper analyses kept in ordinary conversation context;
- compatibility aliases and migration of pre-cutover chat/context formats.

Users may clear Chats/Data during the cutover. Unsupported prior streams are skipped; no dual write, shadow index, or mutable compatibility snapshot is introduced.

## Delivery order

1. **Done:** Core resource contracts and canonical URI validation.
2. **Done:** Provider registry plus chat-artifact provider; `common.artifacts_*` removed and replaced by `common.resources_*` without aliases.
3. **Done:** Unified `ConversationRunService`; read-only resource loop in Chat; removed `PlainChatService` and `ChatContextWindowBuilder`.
4. **Done:** Automatic chat-scoped UI ingestion and durable pre-dispatch message references; explicit `artifactIds`/“В запрос” selection removed.
5. **Done:** plan/HTML reads use canonical `chat` resources; live Office document/selection and VBA project/component/backup providers are registered. Duplicated plan/HTML/VBA reads plus host `get_context/get_selection` tools are removed without aliases; domain-specific range/slide/mail reads remain typed tools.
6. **Done:** Agent receives one compact exact-id tool/skill catalog plus `common.capabilities_search/read`; exact revisioned tool schemas enter a finite-core plus atomic optional `CallableToolPack`, and full-schema catalog injection is removed. Optional membership has no LRU or execution touches and is reconstructed only from the exact accepted extension chain for the logical turn. Split model-facing tool/skill readers were removed without aliases. Custom-definition inspection remains `common.tools_definition_read`.
7. **Done:** durable messages, media handoff, compaction, fork reachability, replay, and trajectory diagnostics carry revision-pinned `ResourceRef` values. Internal `ArtifactIds` message transport and `ChatArtifactService` are removed; event schema 3/session format 6 reject pre-cutover streams without migration.
8. **Done host-neutral (R61/11O1):** public resource discovery is the semantic `find/read` pair; provider routing, exact references, revisions and continuations stay in durable runtime state and are removed from model arguments/results/context/history. Capability catalog/read follows the same model-visibility boundary while durable admission events retain exact revisions.

Each slice must leave one authoritative path for the capability it migrates and add harness coverage for URI safety, provider bounds, replay, context compaction, media hydration lifetime, and Chat mutation denial.
