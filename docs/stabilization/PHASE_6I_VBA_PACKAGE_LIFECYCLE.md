# Phase 6I — typed VBA package lifecycle

Date: 2026-08-30
Baseline: `0c6e5db4285fd8a6d78811635ae522614e066161`

## Scope

This host-neutral slice moves the complete existing VBA package lifecycle into one
`Office.Vba.VbaPackageService` owner:

- manifest/component validation and argument preparation;
- live installation/ownership probe and catalog status;
- document-local and persistent execution;
- temporary session install, macro run and cleanup;
- persistent Install/Uninstall;
- package prepare/read-back/terminal outcome and read-only reconciliation;
- diagnostics projection for session lifecycle/ownership evidence.

Dynamic tool authoring, new package features, pipelines, rename, host identity/
factories, Office adapter routing, Tool Result v1 and WebView layout are unchanged.
The existing shared package backend now enforces the prepared per-component CAS
guard before its first mutation. Rename stays in the existing executor contour until
the separate 6J switch.

## Ownership and cleanup

`VbaToolExecutor.Packages` is now a thin ToolDefinition/command/result adapter.
`VbaPackageToolAdapter` and `VbaPackageBackendAdapter` are explicit one-way
compatibility seams; the domain service never consumes `ToolCommand` or `ToolResult`.
`VbaPackageJournalStoreAdapter` writes to the existing append-only
`VbaJournalStore`; no second store, snapshot, alias stream or dual execution path was
added. The old executor package validation/probe/install/remove/journal methods were
deleted. Its remaining compound helpers now serve rename only and have a 6J removal
gate.

## R41 correction

Every new temporary execution allocates one runtime `LifecycleId`. Session install
and cleanup remain independently prepared/terminal package mutations, but both carry
that same lifecycle id; the exact `RNAssistantSession` marker also carries it beside
package id/version/hash.

Probe classifies live state as not installed, document-local, persistent,
session-owned, partial, modified, recovery-required or unavailable. It combines:

1. exact component source/type/code-only-form evidence;
2. exact parsed ownership marker and package fingerprint;
3. all durable session lifecycle records for the package/document.

Therefore an install with missing terminal or missing/failed cleanup cannot be
adopted as ordinary installed/document-local code. Macro execution and persistent
overwrite are blocked even if the live marker was altered or stripped. Temporary
install rechecks absence during preparation, and every run re-probes source/type/
ownership immediately before macro dispatch. Catalog classification also rejects
undeclared package components instead of silently treating the package as installed.
Install passes exact prepared existence/type/source/marker evidence to the backend,
which rejects a missing/incomplete guard and post-prepare drift before its first
component mutation. Read-only
reconciliation may append a terminal for an existing open preparation, but never
replays/removes/overwrites/runs anything. Recovery cleanup requires a fresh explicit
Uninstall mutation and removes only an exact unchanged session-owned package. A
legacy session marker without lifecycle remains eligible for exact explicit cleanup,
without inventing historical correlation.

## Outcome and diagnostics

Package mutations use the typed `ok/error/unknown` rule already used by module
mutations: verified intended state plus durable terminal wins a backend error;
verified before is a definite error; mixed/unreadable/marker-divergent state or lost
terminal is non-retryable unknown. A macro never runs after an unknown install. A
failed cleanup makes the whole temporary lifecycle unknown and blocks later runs.

`LifecycleId`, `SessionOnly` and exact ownership marker are retained in the existing
journal projection/typed bridge. Diagnostics can search both install and cleanup by
one lifecycle id. No source body is duplicated outside existing CAS references.

## Host-neutral verification

- Harness build: 0 errors; 4 existing non-Windows CA1416 warnings from the Excel
  identity probe.
- `vba: package`: 22/22 pass. Added fault cases cover prepare failure, backend throw
  before effect, mutate-then-throw, unreadable read-back, terminal loss + restart,
  cancellation before/after dispatch, macro/cleanup failure, marker drift/strip,
  probe/install, post-prepare CAS and pre-run races, undeclared catalog components,
  explicit orphan cleanup and reserved source markers. Existing atomic/mixed/VBE
  cases remain green; the shared COM helper guard is exercised with a fake VBProject.
- Full `vba:` regression: 87/87 pass.
- Existing session, persistent install, code-only UserForm, document discovery and
  macro-failure cleanup cases are included in the full regression.
- Production source-project inclusion check: 1/1 pass. MockDemo actual-controller
  compile: 0 errors; 3 existing CA1416 warnings.
- `ValidateVersionFormat`, `git diff --check`, static deleted-path/layer checks and
  158 local link targets in 10 changed Markdown files: pass.

## Open gates

- 6J typed rename owner and removal of the final executor compound journal path;
- Windows x64 + Office x64 + VS 2022: real VBIDE marker preservation, Trust Access,
  session install/run/cleanup, crash/restart, persistent Install/Uninstall, UserForm,
  controller and COM lifetime qualification;
- 5B2 production host identity and the remaining release gates.

This commit remains `16.1.0-dev`; it is not a release or Windows-qualified candidate.
