# ADR-0005: Bound document sessions and host access

Status: accepted target design. Phase 5A extracts the existing access boundary;
the bound-session and Excel switch is still Phase 5B, including Windows qualification.

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
- Phase 5B introduces `IOfficeDocumentSession` and `ExcelDocumentSession` under the
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

This extraction preserves the existing ten-second lock timeout, `local_state` then
stable-document file-lock order, global monitor fallback without storage, and
per-runtime nested-read depth. It does **not** make that depth target-aware, extend
the gate over pre-confirmation preparation, or replace stable-key/OR identity checks.
The copied expectation is execution input, not a bound document or lifetime proof.

## Remaining switch and qualification

Phase 5B replaces those semantics together with all Excel factories and access
consumers. The active-workbook fallback and repeated descriptor lookup still have
live consumers; their removal requires the bound switch and Windows tests recorded
in the [migration map](../stabilization/MIGRATION_MAP.md).

Fake host checks can cover ordering, cancellation and access release. They cannot
close the Windows x64 + Office + VS 2022 gates for STA/COM identity, workbook switches,
close/reopen, Save As, multiple windows/chats, manual/resource access or UI waits.
R04 remains open; Excel effect verification itself remains Phase 7.
