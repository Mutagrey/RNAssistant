# Phase 11A1 — artifact commit projection boundary

Date: 2026-08-31
Baseline: `1290181e2c7ac38e9cc0a3ca822cd6a6e7ac5621`

## Scope

After attachment CAS commit, message/artifact linking and the mandatory chat save,
`AssistantController` now synchronously queues one full `ChatStateResponse` before
attachment analysis/helper transport or the primary model transport. The projection
contains the durable `sessionRevision`, committed user message, exact pinned
`ResourceRef` values and artifact revisions.

`ChatStateMessage.scope` separates this active-chat `full` projection from later
best-effort catalog-only title updates. The WebView applies `full` only to the still
selected chat through the existing monotonic per-chat revision guard; stale state is
rejected and a background chat can update only the catalog. No WebView acknowledgement
is awaited.

Attachment chips now state their actual lifecycle: `Не отправлено` for a composer or
retry draft, `Подготовка` for the optimistic pending turn and `Оригинал` only after a
durable projection replaces it.

## Boundaries preserved

- The append-only chat stream, CAS, artifact event schema and exact `ResourceRef`
  transport are unchanged; no second store or index was added.
- AgentKernel, model/result wire, Resource Gateway and callable ToolPack authority are
  unchanged.
- UI queue failure remains best-effort after commit and cannot roll durability back.
  Reload/select reconstructs the projection from the canonical stream.
- The provider/model failing after the boundary leaves the committed turn and resource
  visible and reusable.
- Exact Library heads/history, kind/class labels, Plan/HTML mutation semantics and MIME
  viewers are intentionally left to later Phase 11 slices.

## Verification

- `node tests/web/artifact-commit-projection.test.js`: 3/3 pass; full push precedes a
  fake model call, stale/background pushes are rejected and the production ordering plus
  lifecycle labels are wired atomically.
- `node tests/web/run-view-state.test.js`: 5/5 pass after the affected cache-key update.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj --
  "bridge: typed sendChat"`: 1/1 pass; typed full scope is serialized.
- `dotnet run --project demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c
  Release -- --artifact-commit-test`: pass. The real controller commits one text draft,
  the bridge observer sees the exact revision before the fake transport, and a scripted
  provider failure preserves the committed resource. Only the existing three PDF
  platform-analysis warnings were emitted.
- `ValidateVersionFormat`, `git diff --check` and 270 local Markdown link targets in
  eight changed documents: pass; product remains `16.1.0-dev`.

Windows x64 + Office x64 + real WebView2 reload/multi-window/delivery were not run on
this machine and remain required qualification. No WQ result is claimed.

## Result

Phase 11A1 is done host-neutral. The next independent commit is 11A2: one server-owned
Artifact Library heads/history projection plus immutable-original/versioned kind and
label cleanup. It must not add a resource transport, generic editor or Plan mutation.
