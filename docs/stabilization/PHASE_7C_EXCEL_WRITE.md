# Phase 7C — verified Excel `write_range`

Date: 2026-08-30
Baseline: `0531823fa8024d383aa6e6a32f65a9bd6b7e9c8b`

## Scope

This host-neutral slice moves only `excel.write_range` to a typed domain owner and
an exact native `ToolRuntime` handler. Scalar, formula and 2D table writes share one
before/apply/read-back workflow. Other Excel mutations, production factories,
workbook identity and the 7D interop backend are unchanged.

## Runtime boundary

- `ExcelWriteService` validates the request, bounds and table shape, null-pads
  ragged rows deterministically, resolves one exact target through
  `IExcelWriteBackend`, and compares typed before/read-back state.
- `ExcelWriteToolHandler` owns one `HostRuntime.ExecuteDocumentMutation` scope for
  Agent and manual execution. The public ID is registered only with its exact
  handler and no longer reaches `ExcelAdapter`.
- The catalog policy now requires `ToolVerification.Tool`. A matching before state
  returns `VerifiedNoChange` without write dispatch; only matching read-back after
  dispatch returns `VerifiedChange`.
- A host refusal before `IExcelWriteDispatchBoundary.Mark` is a definite `error`.
  Apply failure, cancellation, unreadable state or divergent state after that
  boundary is non-retryable `unknown`. COM return text and legacy `Success` are not
  effect evidence.
- Native manual `dryRun` validates the public schema but never enters the handler.

## Target and bounds

The temporary `ExcelWriteCompatibilityBackend` uses two reserved internal commands:
one reads the exact target state and one applies the write. The host validates the
sheet, contiguous rectangle, worksheet bounds and 100,000-cell ceiling before
`Value2`/`Formula` materialization or COM matrix allocation. Table input is bounded
before null-padding; the host allocates the matrix only after rechecking exact
dimensions. Values, formulas and per-cell formula flags distinguish constants from
equal-looking formula results.

The internal callback is an explicit compatibility dispatch seam, not model data.
It is invoked immediately before `Value2`/`Formula` assignment and is removed with
both internal commands in 7D, when the backend receives only the bound
`ExcelDocumentSession.BoundDocumentObject`.

## Cleanup and exclusions

The public `excel.write_range` switch and legacy `WriteRangeByKind`, scalar, table
and formula methods were removed from `ExcelAdapter`; the admitted temporary host
behavior lives in thematic `ExcelAdapter.WriteRange.cs`, with no alias, fallback or
dual execution. HTML reads continue through the 7B adapter. `find_cells`,
`create_chat_chart`, `replace_cells`, table/chart mutations, formatting, sheets,
clear/sort/filter, ToolPack, persistence and UI were not moved.

## Verification

Focused host-neutral checks pass:

- `excel write:` — 4/4: exact ownership/policy, scalar/formula/table and ragged
  normalization, verified no-op/change, dry-run, pre-dispatch error,
  mutate-then-throw, unavailable/divergent read-back and bound-session scope;
- `excel read:` — 4/4 regression;
- HTML refresh, mutation safety metadata, prompt schema, batch policy, built-in
  catalog, search-fixture and production source includes — 1/1 each.

MockDemo compiles with 0 errors / 3 existing CA1416 warnings. Version format and
`git diff --check` pass; 176 local links in 8 changed Markdown files have no missing
targets.

Windows x64 + Office x64 + VS 2022 remains required for real Excel formula/value
normalization, mixed formula ranges, protected sheets, large ranges, target switch/
close, COM throw timing, STA/reentrancy and desktop/VSTO/native composition. WQ0 and
5B2 still block 7D; this host-neutral switch does not qualify production identity.
