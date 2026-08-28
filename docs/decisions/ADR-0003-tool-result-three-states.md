# ADR-0003: Tool contracts and three-state results

Date: 2026-08-28
Status: Accepted; Phase 4A contracts and Phase 4B atomic wire cutover. Qualification is recorded in stabilization progress.

## Context

[Master Phase 4](../stabilization/STABILIZATION_MASTER_PLAN.md#phase-4--tool-contracts-и-toolruntime)
separates description, local policy, execution binding and package metadata, while
moving tools incrementally to a generic runtime. Its
[Tool Result v1](../stabilization/STABILIZATION_MASTER_PLAN.md#74-tool-result-v1)
has only three terminal states. The former result writer and schema-evidence readers used a legacy envelope;
changing only the writer would break progressive discovery, materialization and replay.

Phase 4A introduced the typed internal contract and one native handler.
Phase 4B switches the model-facing result together with its consumers. Conversation
Response v4, runtime call-ID ownership and the R29 history contract are unchanged.

## Decision

- Core owns immutable `ToolDescriptor`, `ToolPolicy`, `ToolBinding`,
  `ToolPackageMetadata`, `ToolRegistration` and handler/result/evidence contracts.
  No Office, COM, UI or persistence dependencies enter these contracts.
- `ToolHandlerRegistry` uses exact ordinal IDs. Captured registrations are
  immutable, parsed schemas are copied, duplicate exact IDs and conflicting
  handler bindings are rejected. Registry registration remains additive; this
  does not claim a globally sealed registry or introduce the Phase 8 ToolPack.
- `ToolRuntime` handles one accepted call: exact lookup, captured policy/revision
  match, mode and argument validation/defaults, confirmation before handler entry,
  one dispatch and typed terminal evidence. ModelProtocol/kernel retain the
  whole-response singleton/batch guard before the first dispatch. The runtime
  receives no model envelope and performs no generic retry or verification pass.
- Trusted source definitions provide `RuntimePolicy`. The legacy definition
  adapter projects that authority with existing mutation/confirmation restrictions;
  false legacy flags alone do not establish local read safety. The old central
  `LocalReadIds` list is replaced by this policy projection, not another name list
  or suffix heuristic. Custom tool JSON cannot set the source-owned policy.
- `common.resources_list` is the first native handler, with its descriptor,
  read policy and binding beside `ResourceListToolHandler`. It reuses the existing
  resource gateway/providers. Other tools remain on the explicit legacy port;
  VBA prepare/preview/live-guard-before-confirmation and document binding do not
  move into the generic runtime in 4A.

## Internal result contract

`RNAssistant.Core.Tools.Contracts.ToolResult` contains `Status`, `Message`,
`DataJson` and defensive `ResourceRef` snapshots. `ToolResultStatus` has exactly
`Ok`, `Error`, `Unknown`; there is no second generic `Success` boolean, duplicated
error object or extra terminal state for confirmation. Call identity remains in
the execution context. Default DTO serialization is not a model wire contract.

Awaiting confirmation, awaiting user input and proven non-dispatch remain typed
runtime controls/evidence. A confirmation pause has no fabricated successful
terminal result. Domain journals may retain richer states without exporting them
as new generic statuses. Resource references retain their URI/revision identity;
internal artifact IDs, hashes and file paths do not become model transport.

## Dispatch and effect evidence

`ToolExecutionEvidence` records two independent facts:

| Axis | Values |
|---|---|
| Dispatch | `NotDispatched`, `MayHaveDispatched` |
| Effect | `Unreported`, `None`, `VerifiedNoChange`, `VerifiedChange`, `Unknown` |

`ToolPolicy.Verification` describes a requirement, not observed evidence.
`status=Ok`, a successful legacy result, or optimistic message text cannot create
`VerifiedChange`. A verified no-op stays distinct from an actual verified change.
A definite error may retain evidence of a known partial effect. Counts describe
calls, not changed objects; `WriteOk` alone does not certify applied changes.

Handlers call `MarkDispatchPossible` before entering an operation whose failure
could hide dispatch. A failure before that boundary remains non-dispatched; an
unreliable result after a possible write/external dispatch remains unknown. A read
without a reliable result is an error, not an unknown write. Cancellation does not
erase a terminal result/effect already established by a handler. Contradictory
evidence cannot certify non-dispatch. No automatic retry is introduced; see
[ADR-0008](ADR-0008-unknown-effects-are-not-retried.md).

The kernel aggregates each record once and owns lifecycle/execution health.
The existing `IRunStore` path persists compact dispatch/effect evidence in
`ChatActivity.ExecutionEvidence` together with the completion projection, before
optional model-result materialization. Later projection saves retain that evidence.
The internal result object is not serialized beside the existing payload, and no
new store, index, domain journal or payload copy is introduced. Replay and cloning
preserve facts; missing historical evidence stays missing rather than becoming a
verified effect.

## Phase 4B wire gate

The single active writer is Core `ModelProtocol.ToolResultWire`. Its strict v1
reader accepts only `tool_call_id`, `name`, `status`, `message`, `data`, and optional
`resources`. Status is exactly `ok/error/unknown`; errors use `data.code` rather
than a second error object. Resource URI/revision identity is preserved, with at
most one `relation=result`; `kind`, internal IDs and CAS paths are not wire fields.
Default DTO serialization, legacy status aliases and a second result writer are
not supported. Bounded materialization is Office-owned and uses that same writer.

`AgentJsonProtocol`, all three result roles, compatibility probes, schema evidence,
prompts and full-history gate switch together. Prompt schema 14 preserves existing
custom text/marker until explicit review/reset and corrects R31's model-owned-ID
instruction. Runtime IDs and Conversation Response v4 do not change.

Both accepted call and result records carry local `ToolResultProtocolVersion=1`.
`ToolResultHistoryReader` validates current role shape, identity and body; the full
history gate pairs results within the accepted user run, even outside compacted
context. Old results and old pending calls require explicit reset/new chat, without
rewriting or deleting streams. In-flight/typed pause records are not fabricated
terminal failures; cancelling old pending work remains available. Historical prompt
projection preserves complete envelopes and markers, rather than appending reference
prose after JSON. History edit selection uses runtime metadata IDs, not an alternate
permissive ID parser in a result body.

`NativeToolRuntimeAdapter.ProjectLegacy` and the old writer/readers are removed.
Native results flow directly through `ToolResultMaterialization`. The one-way
`LegacyToolResultAdapter` converts only active domain results using their runtime
outcome. `ToolResultUiProjection` serves manual commands and Activity consumers,
never the model writer. Those consumers retain their domain preparation and richer
internal states until their own migration gates; the wire switch does not migrate
VBA/Excel document binding or declare their verification complete.

Projection/budget/media failure cannot rewrite a saved outcome or effect evidence.
Incomplete capability evidence is explicitly not loaded; its request projection is
an error while the execution record remains immutable. No generic retry is added.

## Consequences and verification

The first native tool establishes the registration/runtime boundary without
redesigning all domain executors. Remaining legacy adapters have explicit consumers
and removal gates in the [migration map](../stabilization/MIGRATION_MAP.md).
Exact lookup, schema, policy, confirmation and dispatch/effect handling require
fake-handler checks; actual invocation and event replay require the native resource
slice. Fake verification evidence exercises aggregation only and does not qualify
Office effects.

Actual checks and cleanup are recorded in
[Phase 4A evidence](../stabilization/PHASE_4A_TOOL_RUNTIME.md#verification) and
[current progress](../stabilization/PROGRESS.md).
These checks qualify the host-neutral runtime/wire boundaries only. R28, Windows
x64 + Office + VS 2022 and the separate Phase 5–9 host/domain/resource/persistence/UI
gates remain open. R29 and product version are not changed by this decision.
