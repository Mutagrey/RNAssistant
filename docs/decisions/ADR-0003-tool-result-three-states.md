# ADR-0003: Tool contracts and three-state results

Date: 2026-08-28
Status: Accepted; Phase 4A implemented and checked host-neutral. Phase 4B wire gate remains open.

## Context

[Master Phase 4](../stabilization/STABILIZATION_MASTER_PLAN.md#phase-4--tool-contracts-и-toolruntime)
separates description, local policy, execution binding and package metadata, while
moving tools incrementally to a generic runtime. Its
[Tool Result v1](../stabilization/STABILIZATION_MASTER_PLAN.md#74-tool-result-v1)
has only three terminal states. Existing model-result writers and schema-evidence
readers still use the legacy envelope; changing only the writer would break
progressive discovery, materialization and replay.

Phase 4A therefore introduces the typed internal contract and one native handler.
The model-facing result switch is a separate, coordinated Phase 4B. Conversation
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

The current legacy model-result writer remains authoritative in 4A, including its
existing `ok`/status/error/resource representation. `NativeToolRuntimeAdapter`
temporarily projects native results into that path. This is not simultaneous
legacy/v1 model output. Do not serialize the new internal result directly.

Phase 4B must switch the Tool Result v1 writer together with AppSettings prompts
and their review policy, ModelCompatibilityService probes,
ProgressiveToolWorkingSet schema/skill-evidence readers, result materialization
and resource handling, native/user/developer history writers and a full-history
result-compatibility gate. It then removes the replaced writer/reader projection.
Until that atomic switch, the target wire states `ok/error/unknown` in master §7.4
are not the active model-result envelope. Existing VBA and other domain handlers
are not migrated merely by changing a result serializer.

## Consequences and verification

The first native tool establishes the registration/runtime boundary without
redesigning all domain executors. Remaining legacy adapters have explicit consumers
and removal gates in the [migration map](../stabilization/MIGRATION_MAP.md).
Exact lookup, schema, policy, confirmation and dispatch/effect handling require
fake-handler checks; actual invocation and event replay require the native resource
slice. Fake verification evidence exercises aggregation only and does not qualify
Office effects.

Actual checks and cleanup are recorded in
[Phase 4A evidence](../stabilization/PHASE_4A_TOOL_RUNTIME.md#verification).
The 4A checks qualify the host-neutral runtime boundary only. R28, Windows
x64 + Office + VS 2022 and the separate Phase 5–9 host/domain/resource/persistence/UI
gates remain open. R29 and product version are not changed by this decision.
