# Phase 7B — typed Excel reads

Date: 2026-08-30
Baseline: `7383854c560e9b1573382f0cf52ea79bacb87dd0`

## Scope

`excel.inspect` and `excel.read_range` now have one host-neutral owner and exact
native `ToolRuntime` handlers. This slice does not change `excel.write_range`, other
Excel tools, ToolPack/catalog schemas, production factories or document identity.

## Runtime boundary

- `Office/Domains/Excel/ExcelReadService` owns selector/content validation,
  canonical JSON, range profile calculation and returned-snapshot validation. It
  does not reference COM, `ToolCommand`, legacy `ToolResult`, chat or UI types.
- `NativeToolRuntimeAdapter` registers each public read id only when its exact
  definition and handler are present. Model and manual execution enter
  `HostRuntime.ReadDocument` with the chat document expectation; the public executor
  no longer opens a second operation root around native handlers.
- `ExcelAdapter` removed both public cases. The temporary
  `ExcelReadCompatibilityBackend` maps typed requests to two internal host commands;
  it preserves runtime call/step correlation but owns no public dispatch semantics.
  The internal ids are absent from the catalog and reserved from authored-tool
  collisions. This is a one-way 7B/7C adapter with removal gate 7D after WQ0/5B2.
- HTML bind/refresh calls the same `ExcelReadToolAdapter` under its already-held
  document access. Unswitched Office data sources may still use their current host
  path, but the two switched Excel ids cannot fall back to it.

## Bounds and results

- Range requests carry the fixed 100,000-cell ceiling to the host. The host checks
  area/dimensions before `Value2` or `Formula`; the service rejects null, malformed,
  inconsistent or oversized snapshots again before projection.
- Workbook collections return at most 200 items and charts at most 100 series per
  item, with explicit `returnedCount`/`truncated` evidence.
- Defined names return name, formula and optional sheet/address metadata. No
  `RefersToRange.Value2` read remains in the inspect path.
- Values and formulas return coordinates, dimensions, cell count and an explicit
  matrix. Profile is calculated in the domain owner; an explicit sheet wins over an
  active selection on another sheet. An explicit matrix containing empty cells
  succeeds; a missing collection/matrix is an error.

## Verification

Host-neutral focused coverage includes:

- exact registration without phantom static ownership and no public host dispatch;
- Agent/manual route plus bound-session owner-STA, closed and wrong-target refusals;
- all inspect selectors, chart detail, values, formulas, profile and empty cells;
- collection truncation, oversized range before fake materialization and malformed
  backend/series fail-closed behavior;
- HTML bind and refresh through the same typed adapter;
- existing protocol call-id pairing, prompt-budget materialization, native resource,
  desktop dispatcher and HostRuntime regressions.

`26` distinct targeted harness cases pass after the final read changes: `4`
Excel-read, `8` directly related Agent/manual/HTML/dispatcher, `10` HostRuntime and
`4` protocol/native/source-include cases. Harness compilation has `0` errors and
`4` existing CA1416 warnings. MockDemo compiles the actual controller composition
with `0` errors and `3` existing CA1416 warnings. `ValidateVersionFormat`, all `173`
local links in the `7` changed Markdown files and `git diff --check` pass.

The harness source-links the changed Core/Office-neutral code. MockDemo compiles the
actual controller composition. Real `ExcelAdapter` COM execution, protected sheets,
large live workbooks, desktop/VSTO/native delivery and production identity are not
qualified on this machine and remain Windows x64 + Office + VS 2022 gates. The
temporary internal backend cannot be removed before 7D/WQ0/5B2.
