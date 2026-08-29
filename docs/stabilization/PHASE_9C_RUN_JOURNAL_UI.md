# Phase 9C — causal run journal UI

Baseline: `7f1a3008a7d133238e1b4f1d14c69102faf950f8`.

Status: **done host-neutral; Windows/WebView2 qualification remains open**.

## Scope

- Diagnostics opens `run-causal` as the primary run view and selects the latest
  known run when an exact run was not supplied.
- A completed Agent run and a failed activity have a direct **Открыть журнал
  запуска** action. Exact chat/run/step/tool-call filters remain owner-controlled.
- `RNAssistantRunJournal` renders one bounded chronological list with typed status,
  layer, time/duration and correlation metadata. Problems, model, tools and effects
  are UI-only filters over already loaded rows.
- Native `details` rows lazily mount the shared lossless JSON viewer for row data and
  projection links. Collapse and rerender unmount viewer controllers.
- The summary counts unique `ToolCallId` values rather than lifecycle rows. Known
  typed failure/unknown/interruption statuses are problems; prose never changes the
  classification.
- Exact `SourceEventSeqs`/`SourceEventIds` stay visible. A range action opens the
  existing raw JSONL view; raw CAS payload remains lazy under that owner.
- Manual refresh and paging preserve expanded-row ownership and reading position.

## Boundaries

- The component receives `TrajectoryViewRowDto[]`; it does not call bridge, CAS,
  fetch, XHR, WebSocket or EventSource and does not parse source `DataJson` itself.
- `ITrajectoryQuery` and the validated chat stream remain the only query/source
  authority. No durable UI log, index, cache, replay path or model-facing artifact
  envelope was added.
- `ui.projected` is labelled as projection, not delivery. Missing evidence says only
  that a required boundary was not recorded. Verified effect wording comes only from
  stored domain/read-back evidence.
- Each bridge query remains capped at 200 rows; the renderer has a hard 1,000-row
  loaded-DOM cap and bulk expansion mounts at most 50 problem rows. Malformed order,
  required kind/status, IDs or correlated source-evidence shapes fail closed.
- Existing raw and specialized trajectory views remain drill-down details. Their
  query/export/storage ownership was not copied into the journal renderer.
- No vendor or runtime asset was added; the R36 manifest remains unchanged.

## Verification

- `node --test tests/web/*.test.js`: 12 files / 65 internal cases pass.
- `tests/web/run-journal.test.js`: 6/6; chronology, typed summary, filters, lazy JSON,
  explicit evidence gaps, navigation, bounds and malformed projection.
- `tests/web/trajectory-json-viewer.test.js`: 6/6; latest-run request, page size 200,
  stale owner and exact JSON integration.
- `node --check` for changed JS/tests and `git diff --check`: pass.
- Reused unchanged Phase 9A evidence: 17 targeted Core/bridge `run-causal` cases.
- Local Google Chrome `file://` DOM probe produced 12 rows / 1 problem, mounted two
  lazy JSON viewers, created no data-driven `img/script/iframe/object`, recorded zero
  component network calls and no page-level horizontal overflow. This is not a dark
  theme, clipboard, keyboard/DPI or WebView2 qualification.

## Open gates

- Windows x64 + Office x64 + VS 2022 / real WebView2: both themes, keyboard/focus,
  clipboard, DPI/responsive layout, reload/confirmation, paging/live append and stale
  response behavior.
- R28 live streaming remains a separate SSE → projection → bridge → WebView gate.
- R37 read-only historical classifier remains until retained current-v4 data is
  checked on Windows and the explicit reset/removal decision is made.
- The remaining Phase 9 persistence/fault matrix is not closed by this UI slice.
