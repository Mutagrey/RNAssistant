# Resource Fabric

Status: implemented and re-audited. Core contracts, providers, the unified Chat/Agent loop, automatic ingestion, progressive tool discovery, and the event/projection cutover use one canonical resource path; replaced paths are removed, not retained as aliases.

## Goals

- A pasted, dropped, or attached file immediately becomes a chat-scoped resource draft; Send promotes it to a durable chat-owned resource before model dispatch. No separate “В запрос” action is required.
- Model context keeps compact references and a bounded working set, never every artifact body.
- Chat can use read-only resources; Agent uses the same reads plus policy-approved mutations.
- A multimodal primary model reads supported media directly. A helper model is used only when the primary model lacks that modality or when durable derived text is explicitly useful.
- HTML, plans, uploads, generated images, live Office content, VBA projects, tool results, and future objects share discovery/read contracts without losing domain-specific mutation semantics.

## Domain model

`Resource` is addressable data, never execution authority, callable-tool state, or a hidden command. A resource may be live and mutable, such as the active workbook, or backed by immutable revisions, such as an uploaded image or plan. Reading it does not grant a tool, change a `ToolPack`, or bypass domain mutation policy.

`ChatArtifact` is an internal replay projection of immutable content/provenance stored through CAS. It is not a second model transport. Model-facing chat resources always use an exact revision URI; active HTML/plan/checkpoint ids remain internal domain pointers and are projected to that URI before entering a message or prompt. There is deliberately no mutable model-facing head URI whose meaning could change during replay.

`ResourceRef` is the compact value carried by messages, tool results, events, and the model working set:

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
provider never chooses an arbitrary first artifact.

Every provider implements bounded `list`, `resolve`, `search`, and `read(ResourceReadRequest)`. The read request carries one `ResourceRef`, representation, opaque cursor, and character limit, so revision evidence cannot be lost between routing and the provider. Immutable text uses an offset internally because its URI is already pinned. Live Office/VBA chunks bind the internal position to the content hash; collection pages bind it to a deterministic collection fingerprint. Model-facing list/read results expose only the usable `nextCursor`, never the current-page cursor or raw offset. Continuation copies it unchanged into `cursor` only for the same operation and exact list query or resource representation. Reusing a cursor after drift fails with retryable `resource_revision_changed`; a cursor from another operation/query/resource fails non-retryably and must be omitted to restart.

Search v1 is bounded case-insensitive literal search plus provider structure. Regex, embeddings, and a durable vector index are intentionally absent until they have a concrete use and bounded semantics. Skills are trusted instructions, not untrusted document resources: their complete revision-matched bodies are read through the unified `common.capabilities_read` id path shared with tool schemas. HTML files/data and plans remain subresources of the chat provider so ownership, revision lineage, and CAS checks are not duplicated. The existing host-neutral `IOfficeApplicationAdapter` supplies document/VBA reads; a second `IOfficeResourceAdapter` would only repeat that boundary.

## Conversation loop

Chat and Agent use one buffered structured loop. The policy differs, the transport and transcript do not:

- Chat receives exactly the four resource discovery/read tools and no mutation tools, confirmation, or skills.
- Agent keeps the complete mode/session-filtered catalog only as local execution authority. The initial model prompt contains resource and unified capability bootstrap schemas plus a compact exact-id tool/skill catalog.
- `RUNTIME_CONTEXT.capabilities.items` always contains the complete compact schema-free capability index. `common.capabilities_search` is an optional bounded metadata filter over it. `common.capabilities_read` loads one exact revisioned tool descriptor or complete skill body according to the catalog kind; only complete, untruncated tool-schema evidence matching the current descriptor enters the callable working set.
- Tool schemas start from a finite mode/host core. Complete exact-revision reads may stage one atomic optional extension for the next model-step boundary only after the full request fits and its accepted event is durable. Membership is monotonic for the logical turn: execution does not touch it, there is no LRU/partial publication, and replay reconstructs only the ordered accepted turn chain.
- Skill bodies remain revision-matched context evidence rather than callable-pack membership. Compaction, truncation, or revision change requires another exact read.

The prompt contains compact resource references relevant to the conversation. On a later question such as “что на той картинке?” the model resolves or reads the referenced URI again. Raw media is hydrated only for the next model step and then released; the durable reference remains.

All four public resource operations execute as exact native read-only `ToolRuntime` handlers over the same `ResourceGatewayService` and provider registry. Their descriptor, policy, and binding are source-owned beside the handler. The controller catalog projects those same descriptors, schemas and exact policy instances through `ControllerToolDefinition`; resource files do not use `LegacyToolDefinitionAdapter`, and the projection adds no execution authority. Every call enters a fresh `DocumentAccessGate` operation root; nested live Office/VBA access through `HostRuntime` reenters only that same synchronous document operation. The Core result contains bounded JSON plus exact `ResourceRef` values. For a media read, an Office adapter holds bytes only as request-local materialization until the immediate next model step, then releases them; bytes, CAS paths, and internal artifact ids do not become a second result transport.

Ordinary large tool results follow the same rule. The result envelope keeps a bounded preview and optional `resources:[{uri,revision,relation?,kind?}]`; `relation:"result"` distinguishes the resource containing the full result from other produced/cited resources. When eligible generic result data exceeds its inline budget, up to the shared 2,000,000-character artifact safety bound becomes a CAS-backed `tool_result` resource before the next model dispatch. Resource/schema/skill reads are not rewrapped as untrusted artifacts. Chart payloads become their specialized immutable artifact at the same result boundary, so the next model step receives kind plus exact URI rather than a duplicate chart body. Durable activity also keeps only that pointer; the storage/UI projection rehydrates the chart from CAS instead of storing or creating a duplicate.

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

Compaction preserves user intent, decisions, complete tool protocol pairs, and a deterministic bounded union of exact references from the compacted prefix, including resources attached to presentation-only activity messages. It may remove hydrated bodies and old read results. A later read reconstructs evidence from the provider. CAS garbage collection derives reachability from verified event streams and journals as before.

Live Office/VBA resources are bound to the chat's document identity and carry content-hash revision evidence on materialized reads. Their opaque document token is derived from the chat projection rather than a potentially newer adapter key; document-key migrations retain prior keys in the append-only projection, so an exact URI survives first save and Save As while a runtime identity proves that the live target is still the same document. Their provider calls share the document mutation gate, so journal reconciliation and source reads cannot observe an in-flight VBA mutation. Mutations keep domain-specific guards, confirmations, journals, and read-back verification; Resource Fabric does not bypass them.

## Domain projections and UI

Resource access is unified at the model/runtime boundary, not forced into one generic editor. The Artifacts view renders chat-owned attachments, plans, immutable chart snapshots, and HTML workspace revisions; VBA stays a live document view with its own editor and journaled mutations. Installed skills stay in Library and are not duplicated into Artifacts; a skill mutation may expose only a UI link to that Library entity. Message resource cards resolve exact revisions. Paste, drop, and paperclip are the normal attachment path. A future explicit `@artifact` composer affordance may insert an exact URI for disambiguation, but it is not a separate transport and is not required for later access.

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

HTML data bindings intentionally persist an approved typed read-only `toolId + arguments` contract, document identity, transform, last-good exact JSON, its SHA-256 and explicit `complete|bounded|truncated` evidence. A generic resource URI cannot represent parameterized reads such as an Excel range without recreating a tool contract inside the URI. Bind and refresh therefore revalidate the current tool schema and execute inside the shared document gate; failure retains the last-good value. Refresh is not independently durable: the sole HTML lineage owner checkpoints it at the next chat turn or guarded export, and ordinary storage saves cannot create revisions. Export returns that exact revision-pinned resource URI and CAS hash before local standalone assembly; the raw JSON string is not normalized through a JavaScript number/object round trip. Charts are immutable data snapshots with a human-readable source locator as provenance, not live bindings; current data requires regeneration or an explicit HTML binding.

## Audit decisions

- Keep three providers instead of separate plan, HTML, skill, and media provider layers.
- Keep exact revision URIs and internal active pointers instead of mutable model-facing heads.
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

Each slice must leave one authoritative path for the capability it migrates and add harness coverage for URI safety, provider bounds, replay, context compaction, media hydration lifetime, and Chat mutation denial.
