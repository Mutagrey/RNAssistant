# WQ-A4 — versioned qualification suites

Date: 2026-08-31
Scope: host-neutral suite catalog and admission gates; live environment evidence remains open.

## Result

- The built-in catalog now contains the canonical families `common.quick`,
  `provider.live`, `storage.recovery`, `ui.webview`, `excel.read-write`,
  `excel.complex-task`, `vba.lifecycle` and `cross.full-run` in addition to the
  WQ-A2 shell and WQ-A3 Excel WQ0 packs.
- Every manifest pins schema/revision/content SHA-256, an exact all-or-nothing
  readiness capability, finite allowlisted steps and a required typed final-state
  assertion. Mutating and complex packs declare runner-owned fixtures and end in
  cleanup.
- Coverage entries name the production owner for every quick/full/release family.
  Catalog construction rejects unknown coverage IDs; all mandatory Excel suite IDs
  have a scenario owner.
- The current application does not fabricate readiness. Packs without their exact
  production adapter/environment capability are visible as unavailable and cannot
  start. Model text, manual acknowledgement and UI state cannot turn them green.
- Manifests contain no scripts, command lines, URLs, CLR/JS types or raw tool IDs.
  They do not create a second model loop, executor, confirmation path or store.

## Checks

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "qualification:"`
  — 12/12 pass.
- Production source/resource inclusion and pre-commit version validation are
  recorded with the commit.

## Open runtime gates

No live provider, production AgentTask adapter, storage fault run, WebView2 capture,
Excel/VBA fixture or cross-host task was executed on this macOS machine. Their exact
capabilities remain absent, so the UI reports N/A rather than pass. Windows x64 +
Office x64 + WebView2, real provider, restart and host-specific verifier evidence are
required in Milestone WQ. WQ-A4 catalog readiness does not close WQ0, 5B2, 7D,
WQ-EXCEL, WQ-PACK, WQ-UI, R04 or any live-provider gate.

Product version remains `16.1.0-dev`; no tag or release is created.
