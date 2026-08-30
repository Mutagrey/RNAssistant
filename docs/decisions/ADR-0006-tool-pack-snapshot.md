# ADR-0006: immutable ToolPack snapshot

Status: accepted for stabilization Phase 8

Date: 2026-08-30

## Context

The runnable catalog is rebuilt at run and confirmation boundaries, while accepted
calls may execute later or resume after confirmation. An id and argument schema alone
cannot prove that the same policy, handler, execution scope, or package implementation
will execute. The previous fingerprint was rebuilt from mutable `ToolDefinition`
objects and did not pin descriptor text or an explicit native binding.

## Decision

Each run captures one immutable `ToolPackSnapshot` after mode, document availability,
safety, schema, and capability filtering. Every registration has an exact id and a
SHA-256 revision over:

- the complete descriptor and canonical argument schema;
- the typed runtime policy;
- handler id, entry point, execution scope, and host;
- package version, path, source/components hashes, and installation status.

The pack revision includes mode, host, and every registration revision. Native
handlers register the captured `ToolRegistration` directly. Remaining legacy
execution uses the same snapshot for `Describe` and rechecks its compatibility
definition immediately before dispatch. A mismatch fails before effects; an id never
authorizes a replacement implementation by itself. Confirmation rebuilds current
authority and compares it with the persisted accepted policy revision.

The snapshot is outside `AgentKernel`. It is execution authority, not a resource,
model payload, durable registry, or capability activation store.

## Consequences

- Dynamic registries may change for a later run without rewriting an active run.
- Descriptor-only capability evidence and executable registration revisions remain
  distinct: loading a schema does not grant or replace local execution authority.
- Phase 8B now keeps callable membership in a separate `CallableToolPack`: finite
  mode/host core profiles plus explicit atomic optional extensions, each with a new
  revision and no LRU eviction inside the live model session. Full-request admission
  includes the bounded format-repair reserve and cannot partially publish a read batch.
- Durable extension events and rematerialization across confirmation, compaction,
  and crash/replay remain a later Phase 8 slice. Raw schema-read evidence is only
  live staging input and never replay authority for an admission decision.
- Resource data, `ResourceRef`, CAS, and cursors are unchanged. Their data-plane ADR
  remains a later Phase 8 decision.
- Legacy `ToolDefinition` remains one conversion/execution adapter until its listed
  consumers move to direct typed registrations.
