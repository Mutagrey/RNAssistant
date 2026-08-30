# Phase 8D — Resource data plane

Date: 2026-08-30
Baseline: `7176abb7f1c7892abf4889555e430208b07b0437`

## Scope

This host-neutral slice completes the Phase 8 resource boundary. It moves the
remaining `common.resources_resolve/search/read` operations beside the already
native list operation, keeps the existing gateway/providers and exact public
schemas, and records [ADR-0004](../decisions/ADR-0004-resource-data-plane.md).
It does not change `AgentKernel`, `CallableToolPack`, CAS/storage, URI/cursor
contracts, providers, document identity/factories, COM, or WebView wiring.

## Typed resource execution

`ResourceListToolHandler`, `ResourceResolveToolHandler`,
`ResourceSearchToolHandler`, and `ResourceReadToolHandler` now own exact immutable
descriptors, read-only policies, and bindings. `NativeToolRuntimeAdapter` registers
all four with `ToolRuntime`; Agent, Chat, and manual calls therefore use the same
schema/policy gate and terminal result. Each call establishes a fresh
`DocumentAccessGate` operation root before entering `ResourceGatewayService`;
live Office/VBA provider access through `HostRuntime` preserves same-operation
reentry and serialization with mutations.

The former `ResourceToolExecutor` and `ControllerExecutorKind.Resource` dispatch
branch are removed. `ResourceToolCatalog` is a catalog projection only and cannot
execute a call. Existing capability discovery stays separate from definition
authoring, and provider registration does not touch the kernel or callable-pack
lifecycle.

## Result and media boundary

Core `ToolResult` keeps the gateway's bounded JSON plus exact `ResourceRef` values.
No CAS `content_ref`, path, blob id, reader, or alternate wire is added. A media
read also creates a request-local Office attachment projection keyed to the accepted
runtime call; the model materializer consumes it for the immediate next step and
the adapter removes it. Durable history continues to carry only the exact resource
identity. Native UI error projection reads optional provider `retryable` metadata,
so stale-resource guidance survives without inventing another result shape.

## Cleanup and retained boundaries

- Removed the legacy resource executor, switch case, and all source/test references.
- Kept `ResourceGatewayService` and the provider registry as the sole data owner.
- Kept a one-way catalog projection through `LegacyToolDefinitionAdapter.ProjectRead`
  for current mixed catalog consumers; Phase 10 owns that catalog cleanup.
- Kept domain mutations, non-resource legacy handlers, ToolPack admission/events,
  and all Office identity/factory work outside this slice.

## Verification

The final host-neutral snapshot passes 74 distinct targeted cases: resources 8,
Agent 34, Chat 13, ToolRuntime 14, native resource replay 1, bounded VBA resource 1,
VBA/document gate serialization 2, and production source inclusion 1. This covers
exact native policies/bindings, model/manual parity, invalid pre-dispatch input,
URI/revision/cursor bounds, live document gating, request-local media hydration and
release, replay evidence, Chat read-only routing, and old-style project inclusion.

Harness compilation has 0 errors and 4 existing CA1416 warnings from the Windows-only
Excel identity probe. The actual-controller MockDemo compiles with 0 errors and 3
existing CA1416 PDF warnings. `ValidateVersionFormat`, `git diff --check`, and all
222 local links in the 14 changed Markdown files pass. Product remains
`16.1.0-dev`; the full harness, release script, tag, and push were not run.

Office/VSTO execution was not run. WQ-PACK must validate all four operations with
real Office/VBA providers, manual/model/UI parity, stale-cursor retryability, and
media release through success, repair, cancellation, and failure on Windows x64 +
Office x64 + VS 2022.

## Remaining work

Phase 8 is complete host-neutral. WQ-PACK remains mandatory before Phase 12.
Production document identity/factory work still waits for WQ0/5B2 and blocks 7D;
remaining compatibility/catalog cleanup belongs to a separate Phase 10 slice.
