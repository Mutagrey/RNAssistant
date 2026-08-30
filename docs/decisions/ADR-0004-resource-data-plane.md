# ADR-0004: Resource Fabric is a data plane

Date: 2026-08-30
Status: Accepted in stabilization Phase 8D; Windows live-provider qualification remains open.

## Context

RNAssistant exposes chat artifacts and live Office/VBA content through the common
`resources_list/resolve/search/read` vocabulary. The provider gateway already owned
canonical `rna://` routing, revision checks, bounded pages/chunks, stale-cursor
rejection, and media selection, but three public operations still executed through
the broad legacy tool executor. That split obscured whether a Resource could carry
execution authority and risked either losing `ResourceRef` on typed conversion or
inventing a second CAS `content_ref` model transport.

## Decision

- A `Resource` is addressable data. It never grants callable authority, contains a
  handler, changes `CallableToolPack`, or substitutes for a domain mutation tool.
- `ResourceGatewayService` and its provider registry remain the single data-plane
  owner for list, resolve, literal search, bounded read, canonical URI/revision, and
  opaque cursor semantics. Adding a provider does not change `AgentKernel`.
- All four exact public resource IDs use source-owned immutable descriptors,
  read-only `ToolPolicy`, bindings, and native `ToolRuntime` handlers. Each handler
  creates one `DocumentAccessGate` operation root; live provider access through
  `HostRuntime` may reenter only that same synchronous document operation.
- The typed Core result carries bounded JSON and exact `ResourceRef` values. CAS is
  provider storage, not a second model-facing result envelope; paths, blob ids, and
  internal artifact ids do not cross the wire.
- Media bytes are a request-local Office materialization attached to the accepted
  read call. They are consumed by the immediate next model step, including its
  bounded protocol repair attempts, and then released. The durable event/history
  keeps the `ResourceRef`, not the bytes.
- Capability discovery/read remains separate from tool/skill definition authoring.
  A capability descriptor or Resource cannot authorize execution by itself.

## Consequences

- `ResourceToolExecutor` and its legacy dispatch branch are removed. A small
  `ResourceToolCatalog` projects the four source-owned registrations only for
  current mixed catalog consumers; it is not an executor or authority owner.
- Manual UI and model calls execute the same captured registration and gateway.
  Native error data preserves provider codes and optional retryability for UI
  projection without changing the Tool Result v1 wire.
- `rna://`, revisions, CAS reachability, cursor bounds, providers, and `ResourceRef`
  identity remain unchanged. No migration, alias, dual execution, new reader, or
  second durable store is introduced.
- Windows x64 + Office x64 + VS 2022 must still qualify live document/VBA gate
  behavior, UI/manual parity, media lifetime, and retryable stale-cursor display.
