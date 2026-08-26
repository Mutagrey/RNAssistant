# Resource Fabric

Status: implemented. Core contracts, providers, the unified Chat/Agent loop, automatic ingestion, progressive tool discovery, and the event/projection cutover all use one canonical resource path; replaced paths are removed, not retained as aliases.

## Goals

- A pasted, dropped, or attached file immediately becomes a chat-owned resource. No separate “В запрос” action is required.
- Model context keeps compact references and a bounded working set, never every artifact body.
- Chat can use read-only resources; Agent uses the same reads plus policy-approved mutations.
- A multimodal primary model reads supported media directly. A helper model is used only when the primary model lacks that modality or when durable derived text is explicitly useful.
- HTML, plans, uploads, generated images, live Office content, VBA projects, tool results, and future objects share discovery/read contracts without losing domain-specific mutation semantics.

## Domain model

`Resource` is an addressable object. A resource may be live and mutable, such as the active workbook, or backed by immutable revisions, such as an uploaded image or plan.

`ArtifactRevision` is immutable content plus provenance stored through CAS. `ResourceHead` points to the current revision. A head and a revision have different URIs so replay can always pin exact historical content.

`ResourceRef` is the compact value carried by messages, tool results, events, and the model working set:

```json
{"uri":"rna://chat/s1/artifact/a1/revision/2","revision":"2"}
```

Canonical URIs use `rna://<provider>/<escaped-segments>`. They never expose local paths, credentials, or provider implementation details. Query strings, fragments, dot segments, encoded separators, and non-canonical spellings are rejected.

`Tool` is an executable capability, not a resource. `Skill` is versioned instruction content and may reference resources and tools. Domain mutations remain typed tools because safety, confirmation, and compare-and-swap rules differ by domain.

## Providers

Office owns a registry of resource providers. The common layer knows only provider contracts.

Initial providers:

- `chat`: uploaded files, images, audio, generated artifacts, plans, HTML revisions, chart payloads, and tool-result artifacts;
- `document`: bounded structure and content from the active Office document;
- `vba`: project/component metadata and bounded source reads;
- `skill`: enabled skill packages and reference files where model access is allowed.

Every provider implements bounded `list`, `resolve`, `search`, and `read`. Search v1 is structural plus lexical. The interface permits semantic search later, but embeddings and a durable vector index are not required. Reads select a representation such as `metadata`, `text`, `structure`, `source`, or `media`, use cursors, and report truncation explicitly.

## Conversation loop

Chat and Agent use one buffered structured loop. The policy differs, the transport and transcript do not:

- Chat receives exactly the four resource discovery/read tools and no mutation tools, confirmation, or skills.
- Agent keeps the complete mode/session-filtered catalog only as local execution authority. The initial model prompt contains bootstrap resource/skill/discovery schemas plus compact namespaces.
- `common.tools_list/search` return bounded schema-free metadata. `common.tools_read` returns one exact revisioned descriptor; only complete, untruncated evidence matching the current descriptor enters the callable working set.
- The dynamic working set is an evidence-derived LRU of at most eight schemas with an 8k–20k token budget. Exact tool calls update recency, so replay reconstructs the same eviction. Compaction, truncation, revision drift, or explicit eviction requires another read.
- A schema or skill body remains loaded only while its exact revision is present in active model context.

The prompt contains compact resource references relevant to the conversation. On a later question such as “что на той картинке?” the model resolves or reads the referenced URI again. Raw media is hydrated only for the next model step and then released; the durable reference remains.

## Ingestion and derived data

Paste, drag-and-drop, and the paperclip all call the same chat-scoped ingestion pipeline. Bytes are staged while the composer remains editable; sending the turn promotes them into the durable resource graph in a fixed recoverable order:

1. Validate type/size and stage bytes under the target chat id.
2. On send, copy bytes into CAS, append attachment/artifact revision events, and bind the canonical revision to the user turn before model dispatch.
3. Extract cheap deterministic representations once (metadata, safe text, page structure).
4. Route supported media directly to a multimodal primary model for the current turn.
5. If the primary model cannot consume the modality, call a bounded helper with only the current request and selected media.

Helper output is query-specific evidence for that model step. It is not silently treated as a complete durable description. Reusable OCR/transcription may be stored as a derived artifact revision with explicit provenance: source URI, extractor/model, parameters, timestamp, and content hash.

## Context and storage

The append-only session event stream remains the durable source of truth. Events store resource references and CAS references, not copied bodies. Model requests persist the exact materialized working set before dispatch.

Compaction preserves user intent, decisions, tool protocol pairs, and resource references. It may remove hydrated bodies and old read results. A later read reconstructs evidence from the provider. CAS garbage collection derives reachability from verified event streams and journals as before.

Live Office/VBA resources are bound to the chat's document identity and carry content-hash revision evidence on materialized reads. Their provider calls share the document mutation gate, so journal reconciliation and source reads cannot observe an in-flight VBA mutation. Mutations keep domain-specific guards, confirmations, journals, and read-back verification; Resource Fabric does not bypass them.

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
6. **Done:** Agent uses compact tool namespaces plus `common.tools_list/search/read`; exact revisioned schemas enter a bounded replayable LRU working set, and full-catalog prompt injection is removed. Custom-definition inspection moved to `common.tools_definition_read` without an alias.
7. **Done:** durable messages, media handoff, compaction, fork reachability, replay, and trajectory diagnostics carry revision-pinned `ResourceRef` values. Internal `ArtifactIds` message transport and `ChatArtifactService` are removed; event schema 3/session format 6 reject pre-cutover streams without migration.

Each slice must leave one authoritative path for the capability it migrates and add harness coverage for URI safety, provider bounds, replay, context compaction, media hydration lifetime, and Chat mutation denial.
