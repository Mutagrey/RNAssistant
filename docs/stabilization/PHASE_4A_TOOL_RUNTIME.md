# Phase 4A — Tool contracts and first native runtime slice

Status: done host-neutral on 2026-08-28; Phase 4B wire gate and Windows qualification remain open. Baseline: `6a256f0` (R29 v4).

Requirements: [master Phase 4](STABILIZATION_MASTER_PLAN.md#phase-4--tool-contracts-и-toolruntime),
[Tool Result v1](STABILIZATION_MASTER_PLAN.md#74-tool-result-v1),
[Run Summary](STABILIZATION_MASTER_PLAN.md#76-run-summary),
[ADR-0003](../decisions/ADR-0003-tool-result-three-states.md).

## Phase 4A scope

| Owner | Change |
|---|---|
| Core Tools | Immutable descriptor, policy, binding, package metadata and registration; `IToolHandler`; internal three-state result; explicit dispatch/effect evidence |
| Office Runtime | Exact ordinal `ToolHandlerRegistry`, generic single-call `ToolRuntime`, native composition and explicit legacy definition/result adapters |
| Office Tools | First native `ResourceListToolHandler` for `common.resources_list`; source-owned read policies replace the central local-read ID list |
| Existing run store/projections | Compact `ChatActivity.ExecutionEvidence` survives completion, materialization, event replay and clone without copying result payloads |

Registrations capture descriptor/schema, policy, binding and revision. Existing
entries cannot be replaced or rebound by another registration; callers receive
schema copies and immutable contract data. The registry can accept new exact IDs;
it is not described as globally sealed. This is not the future immutable callable
ToolPack or a change to current resource/LRU lifecycle.

`ToolDefinition.RuntimePolicy` is trusted source metadata excluded from custom
tool JSON. `LegacyToolDefinitionAdapter.PolicyFor` preserves existing mutation and
confirmation restrictions and grants independent-local-read authority only from
a valid source declaration. Missing declarations remain conservative/unclassified.
`ConversationProtocolContext.BatchSafeReadIds` projects this authority instead of
maintaining `LocalReadIds`. Exact IDs still determine lookup; names do not imply
safety. Native and legacy policies participate in captured fingerprints.

## Invocation flow

1. `ConversationRunService` composes the mode/session catalog and kernel adapters.
   `NativeToolRuntimeAdapter` registers the exact `common.resources_list` handler
   with the captured descriptor, source policy, binding and revision.
2. ModelProtocol validates the v4 envelope and current callable schemas. AgentKernel
   validates the complete response policy, applies the singleton/read-batch guard,
   assigns runtime IDs and persists accepted calls before dispatch. These remain
   response-level responsibilities; `ToolRuntime` accepts only one call context.
3. `ConversationKernelAdapter` routes the owned exact ID to the native runtime.
   That runtime rechecks the policy/revision and allowed mode, validates/defaults
   arguments, honors cancellation and gates confirmation before handler entry.
   It calls the selected handler once; it has no generic retry loop.
4. `ResourceListToolHandler` invokes the existing `ResourceGatewayService.List`
   for bounded provider/resource metadata. Its schema, cursor/filter semantics and
   resource transport remain in the resource contour. It marks the invocation
   boundary and supplies a typed read result with explicit no-effect evidence.
5. Kernel completion accounting crosses the existing `IRunStore` before optional
   result/media/context work. The Office adapter projects the native result to the
   current legacy model-result writer, preserving resource references. This
   projection does not change execution evidence or create a second dispatch.

Manual `common.resources_list` commands also enter the native schema/policy/handler
boundary through `NativeToolRuntimeAdapter.ExecuteCommand`. Their transient
command identities do not create accepted model responses or change R29 ownership.

Every other handler stays on the explicit legacy execution route, with
`LegacyToolDefinitionAdapter` supplying captured typed policy where appropriate.
Existing `OfficeToolExecutor` and domain executors retain VBA preparation,
preview, live runtime guards and guard-before-confirmation ordering. The first
native read does not authorize moving those steps into a generic confirmation
gate or changing document binding/COM behavior.

## Internal result and evidence

`Core.Tools.Contracts.ToolResult` is internal typed output in 4A: `Ok`, `Error` or
`Unknown`, message, data and resource references. It does not add a `Success`
boolean or duplicate generic error object. Runtime pending confirmation,
awaiting-user and non-dispatch controls remain outside those three terminal states.
The active model-result wire is still legacy; see the [4B boundary](#phase-4b-boundary).

Dispatch and effect are separate from status:

| Situation | Required record semantics |
|---|---|
| Policy/schema rejection or cancellation before handler entry | No dispatch; no fabricated effect |
| Pending confirmation | Unexecuted call and pending identity, not a successful terminal result |
| Read without reliable data | Read error, not unknown write |
| Verified no-op | `VerifiedNoChange`, distinct from `VerifiedChange` |
| Definite error with known partial effect | Preserve both error outcome and actual effect evidence |
| Missing/uncertain result after possible write/external dispatch | Unknown effect; never ordinary success or an automatic retry |
| Cancellation after a known terminal result | Preserve established result/effect, then let kernel end the lifecycle |

`ToolPolicy.Verification.Tool` does not manufacture verification. A handler reports
observed evidence and marks dispatch possibility before the operation that may
hide it. `status=Ok`, policy settings and result prose are insufficient to certify
a change. `ToolCounts` counts calls; neither `WriteOk`, clean execution health nor
completed lifecycle means all requested changes were applied. No-op/actual-change
facts remain available separately in the execution evidence.

## Evidence and replay

`ToolExecutionRecord.Evidence` contains only `Dispatch` and `Effect`.
`ConversationKernelAdapter.Store` copies it to `ChatActivity.ExecutionEvidence`
before the first completion save and retains it after result materialization.
The existing typed session operations persist that activity in the same append-only
chat stream. `ToolExecutionRecord.Result` is excluded from serialization; evidence
does not duplicate data, media, domain hashes or journal payloads.

Event replay and `ChatCloneService` preserve the immutable evidence exactly,
including absence on older records. They do not infer `VerifiedChange` from a
legacy successful result or rerun a handler. Kernel summary accounting stays once
per execution record through the existing `IRunStore`/CAS path. Required save
failure remains a stop/reload boundary, not an append/tool retry. Full crash/UI
normalization and host recovery qualification remain in their later phases.

## Phase 4B boundary

Do not activate a Tool Result v1 model serializer in 4A. The temporary
`NativeToolRuntimeAdapter.ProjectLegacy` output feeds the existing
`AgentJsonProtocol` writer and current schema-evidence readers. Legacy tool-result
statuses/`ok` handling remain intentional for these active consumers, not a second
new model protocol. Conversation Response remains v4 and prompt schema remains 13.

The next atomic result-format switch must cover all of:

- Tool Result v1 model writer and all native/user/developer result-history forms;
- AppSettings prompt defaults/review handling, ModelCompatibilityService probes and
  built-in prompt-authoring guidance (R31);
- ProgressiveToolWorkingSet schema/skill-evidence readers and their exact load rules;
- ConversationModelSession materialization, bounded data and `ResourceRef` handling;
- a full-history result compatibility gate before model preparation/confirmation;
- removal of the replaced wire/reader paths and native-to-legacy result projection.

Changing only the terminal DTO, writer or prompts is insufficient. The broader
Phase 4 serializer gate remains open until 4B; it is not closed by internal typed
results or this first native handler. Domain migrations and their preparation
guards remain separate work.

## Local cleanup and adapters

The replaced `common.resources_list` body/schema path is removed from the legacy
executor and now belongs to the native descriptor/handler; there is no alternate
list dispatch path. The replaced
central `LocalReadIds` registry is removed rather than renamed. No mass moves,
unrelated domain refactor or new durable store belongs to this slice.

| Temporary path | Owner and consumers | Removal boundary |
|---|---|---|
| `LegacyToolDefinitionAdapter` | Office Runtime; current ToolDefinition catalog/discovery/authoring and unmigrated execution consumers | Per consumer/domain contract switch; not blanket removal at 4A |
| Explicit legacy execution/outcome port | Office kernel adapter and existing Office/domain executors | Migration of the corresponding handlers with equivalent preparation/effect evidence |
| `NativeToolRuntimeAdapter.ProjectLegacy` | Office Runtime; native results entering current model materialization/history writer | Coordinated 4B result writer/readers/history gate |

The [migration map](MIGRATION_MAP.md) owns the complete consumer inventory and
nearest removal gates. These paths are for active consumers, not compatibility
with abandoned chat formats or tool definitions.

## Verification

macOS, .NET 8 harness with C# 7.3 source compatibility and fake Office/LLM.
**135 distinct targeted tests pass**, no full harness or VSTO/Office validation.
The existing fake adapter initially dropped the new policy while cloning tools;
its explicit clone now preserves the immutable policy, and affected checks below
were rerun. A native-list fixture was corrected to select provider `chat` rather
than expecting bodies/URIs from the provider-discovery response. Neither failure
was suppressed or changed into a weaker expected result.

Run form: `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "FILTER"`.
After the relevant source build, all other filters used `--no-build`.

| Filter | Pass |
|---|---:|
| `tool runtime:` | 14/14 |
| `kernel:` | 44/44 |
| `kernel replay:` | 10/10 |
| `protocol context:` | 6/6 |
| `agent:` | 36/36 |
| `resources:` | 8/8 |
| `storage: event log is canonical` | 1/1 |
| `completion guard:` | 5/5 |
| `causal trace:` | 6/6 |
| `tools: strict schema validates metadata and constraints` | 1/1 |
| `tools: controller catalog uses strict schemas` | 1/1 |
| `tools: safety metadata gates mutations` | 1/1 |
| `conversation v4: batches only explicit read-only calls` | 1/1 |
| `harness: production projects include all source files` | 1/1 |

The 13 generic runtime cases and 44 pure kernel cases were reused after the
fixture-only clone correction: their source/tests/runtime dependencies did not
change and do not call FakeOfficeAdapter. The native runtime case was rerun with
`tool runtime: native resource`; it is counted once above. Protocol context,
kernel replay, agent and resources were rerun against the corrected fixture.
No earlier R29 count or previous phase result is included in 135.

Review regressions include `verification=Tool` on reads, missing required policy
or evidence JSON fields, and cancellation while registering confirmation. Missing
legacy evidence remains absent/Unreported; malformed present typed objects fail
closed. Tests distinguish no-op from actual change and retain known partial
change with error; they do not claim to verify Office effects.

`KernelNativeResourceReadEvidenceReplays` runs the real `resources_list` handler
through the kernel and ChatStore, checks the first completion save before optional
materialization and subsequent saves, then reloads/clones without dispatch.
`SessionEventLogIsCanonical` covers a compact dispatch/effect matrix and absence;
its large result body is stored once. Existing native read-batch tests retain
runtime IDs, raw origin positions, model result pairing and reload behavior.

`dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj --no-restore -nologo -v:minimal`
passes with **0 errors / 3 existing CA1416 warnings** in ModelAttachmentService.
It compiles the actual controller and corrected fake adapter, not VSTO or COM.
Old-style project includes and linked harness/MockDemo sources include every new
contract/runtime/handler. The removed list branch/schema and central LocalReadIds
have no remaining executable path. Pre-commit `dotnet msbuild tests/RNAssistant.Harness/RNAssistant.Harness.csproj
-t:ValidateVersionFormat -nologo -v:minimal` passes. `git diff --check` and 97 local
links/anchors in 11 changed Markdown files pass. After executable verification,
only one source comment was corrected to match the per-handler removal gate;
no executable input changed. Product props and the complete Git tag ref snapshot
match baseline; product remains `16.1.0-dev`, no tag or push is part of 4A.

## Open gates

R31 stale unique-call-id guidance in `common.prompt_authoring` remains an explicit
4B prompt-consumer task; it was confirmed by source review, not a new reproduced
live incident. R28 streaming, Windows x64 + Office + VS 2022 controller/WebView/DPAPI qualification,
and real-provider validation remain open. Phase 5 document binding/host gate,
Phases 6–7 domain effect qualification, Phase 8 resources/ToolPack and Phase 9
persistence/UI work remain separate. Fake verification evidence qualifies only
the runtime's handling of supplied facts. R29 is unchanged; this stage does not
claim new payload correctness or live-provider evidence for it.
