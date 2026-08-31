# ADR-0005: Bound document sessions and host access

Status: accepted. Phase 5A extracted the access boundary; 5B1 introduced its neutral
session contract and operation gate. The 2026-08-31 accepted-risk decision removes
WQ0 as a blocking prerequisite and re-owns the actual Excel binding plus legacy
removal in one 11T0/7D switch. Windows qualification remains unperformed.

## Context

`OfficeToolExecutor` mixed tool routing/safety with document expectations, lock
acquisition and nested live-read scopes. Changing workbook binding there required
understanding unrelated domain tools. Current identity checks accept either a
stable key or a runtime key; current file locks use the stable key. Neither proves
that a live object survived close/reopen or an active-window change.

## Decision

- `Office.Runtime.HostRuntime` owns synchronous document access. Tool routing,
  catalog policy and domain preparation stay with their current owners. The runtime
  receives only the document expectation, access flags and the synchronous operation;
  it does not receive a chat session, controller, model client or tool catalog.
- Phase 5B1 introduces `IOfficeDocumentSession`; 11T0/7D adds `ExcelDocumentSession` under the
  [Document Session v1 contract](../stabilization/STABILIZATION_MASTER_PLAN.md#79-document-session-v1).
  A session holds one live document object, its stable/runtime identities, STA
  dispatcher, liveness check and gate. Descriptor/active-document resolution occurs
  at explicit binding, not during each execution access.
- The bound object supplies both mutation and read-back. Closing it cannot redirect
  work to another active or reopened workbook. Save As may change the durable chat
  key but must not change the current runtime target or its gate. Different proxies
  of one live document share identity; a per-adapter GUID is not sufficient.
- One reentrant gate per runtime document covers guard/live read, validation,
  preparation, dispatch, read-back and terminal evidence. Manual mutations and live
  resource reads use the same gate. Identity/liveness are checked inside STA before
  access, and guard state is checked again after confirmation or queued waiting.
- Do not hold the gate across model or user waits. The target order is chat lease,
  document gate, then short storage locks, without reverse acquisition or waiting
  for user confirmation under a lock. Cancellation before dispatch prevents the
  operation; cancellation after possible dispatch does not prove absence of effect.

## Delivered in 5A

The current executor, manual VBA and live resource/editor paths call `HostRuntime`.
The former executor-owned guard, file-lock, monitor, nesting and lease helpers are
removed; resource/tool error mapping stays at the caller boundary.

That extraction preserved the existing ten-second lock timeout, `local_state` then
stable-document file-lock order, global monitor fallback without storage, and
per-runtime nested-read depth. It did **not** make that depth target-aware, extend
the gate over pre-confirmation preparation, or replace stable-key/OR identity checks.
The copied expectation is execution input, not a bound document or lifetime proof.

## Delivered in 5B1

`HostRuntime` takes document access before guard reads and domain preparation and
retains it through dispatch, read-back and the existing journal terminal. Resource,
HTML data, manual tool and VBA editor reads share that access. Native resource-list
dispatch establishes its own operation root. Confirmation returns after releasing
access; a confirmed call rechecks its guard under a fresh gate.

`DocumentAccessGate` replaces per-runtime async depth and the global monitor with
one keyed semaphore registry plus the existing bounded file lock. Reentry requires
the same synchronous operation and target; only explicit STA handoff carries that
permission. New roots/child tasks cannot borrow it. Order is document then shared
local state, after the caller's chat lease. The owner STA returns busy instead of
waiting on an occupied gate. Cancellation is checked again on the STA before the
action; cancellation or access failure after mutation starts remains nonretryable
uncertainty, without replacing domain-returned evidence.

The session port distinguishes cached immutable host/runtime/gate/dispatcher
metadata from STA-only stable identity, bound object and liveness. Wrappers cache
one session at initialization; a new lifetime requires a new adapter. For a bound
session, runtime identity takes precedence over a matching saved path, and the
whole synchronous action runs on its owner STA. Fake sessions exercise this port;
no production Excel factory supplies it yet. Legacy production adapters still use
stable-key locks and stable-key OR runtime-key validation.

## Delivered in 5B2: direct context/catalog reads

`HostRuntime.ReadDocument` creates a new operation root and uses the same target,
gate and guarded STA execution as tool access. `OfficeContextCaptureService` owns
preparation plus selection capture; controller persistence follows after release.
Preparation guard/access failures propagate; best-effort UI context returns null
on failed access. `ToolCatalogService` holds one gate across cache identity/lookup,
module discovery and every component read. A failed/null backend result or read
exception aborts the entire load without an empty/partial cache or internal retry;
a successful empty list remains cacheable. Closed bound sessions cannot reuse
cached document tools. The former direct
controller capture and catalog guard-only scope are removed.

This independently switches the remaining read roots. The former prerequisite
Windows identity gate was later retired by the accepted-risk decision below;
neutral fixtures still cover only root isolation, owner dispatch, failure cleanup,
target/close rejection and cache behavior, not real controller/WebView/COM
execution.

## 5B2 identity candidate — diagnostic only

The diagnostic candidate is a retained standard COM marshal reference and its
OXID/OID, scoped by the local Excel process and creation time. It is not production
runtime identity. The isolated [Excel identity probe](../../tests/RNAssistant.ExcelIdentityProbe/README.md)
records independent observations without modifying workbooks or switching factories;
it is optional regression evidence, not a cutover prerequisite.
Only OBJREF_STANDARD/IUnknown is supported; any other format or cross-client mismatch
blocks the candidate. No fallback to local pointer, path, HWND or a generated ID.

The probe owns its marshal packet until explicit disposal on its creating STA.
Identity lifetime, client attach/detach, close/reopen and in-process VSTO/native
equivalence must be demonstrated, not inferred from parser tests. The probe README
defines observations, actual call sites and cleanup evidence. Production liveness
must remain separate from a retained COM reference. The diagnostic reader/resolver
has no production consumer and is removed or replaced by the qualified implementation
at the 5B2 candidate decision; it is not an additional runtime path.

## Production switch and deferred qualification

The next 11T0/7D change replaces the remaining legacy identity/binding together
with all Excel factories and access consumers. The active-workbook fallback and
repeated descriptor lookup still have live consumers; their removal is atomic with
the bound switch recorded in the [migration map](../stabilization/MIGRATION_MAP.md).

One live identity should be shared by desktop, VSTO and native clients/proxies. The
current local `IUnknown` address is not proof of that identity across apartments or
processes; a path, HWND or per-adapter GUID is not a substitute. This uncertainty is
an accepted deferred risk rather than a factory-switch blocker.
The direct context/catalog access switch delivered above does not qualify this
production identity mechanism or its UI/STA behavior.

Fake host checks can cover ordering, cancellation and access release. They cannot
close the Windows x64 + Office + VS 2022 gates for STA/COM identity, workbook switches,
close/reopen, Save As, multiple windows/chats, manual/resource access or UI waits.
R04 remains open; Excel effect verification itself remains Phase 7.

## 2026-08-31 accepted-risk cutover decision

By explicit user decision, WQ0 no longer blocks production binding. One atomic
11T0/7D switch binds the workbook selected at pane/target creation, creates one
`ExcelDocumentSession`, passes only its `BoundDocumentObject` to the direct interop
backend, and deletes the compatibility backend plus execution-time
`ActiveWorkbook`/descriptor lookup. No nullable/unbound session or typed wrapper
over `ExecuteTool(ToolCommand)` is an allowed intermediate state.

The first implementation captures the existing `DocumentIdentity.RuntimeKey` once
for that bound object and lifetime. This is explicit risk acceptance, not proof that
independently resolved proxies/processes share an identity. WQ0 remains optional
diagnostic/regression tooling and is still required as part of release qualification;
it is not runtime authority or a pre-cutover gate. Windows close/reopen, Save As,
multi-window/client and cross-proxy scenarios remain unperformed qualification.
Failures found later are fixed against the bound-session contract; legacy
active-document fallback must not return.
