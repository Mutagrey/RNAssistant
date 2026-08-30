# WQ-A2 — Qualification Center UI shell

Date: 2026-08-31
Scope: host-neutral WQ-A2 only; parent WQ-A1 core `13e48d9`.

## Result

- Added one embedded, strict, read-only quick pack `common.ui-shell` and coverage
  owner `WQ-A2.shell`. Its typed verifier passes only after reading persisted
  preflight and manual-checkpoint evidence from the same qualification event stream.
- Added `QualificationApplicationService` plus typed controller/WebView routes for
  catalog, start, restore and advance. A run owns a dedicated document chat, the
  latest run is discovered from validated `qualification.run.started` events, and
  ordinary conversation turns in that chat fail closed.
- Added one Qualification Center UI reachable from the empty-chat action and
  Diagnostics. It renders server-owned status, stepper, explicit manual action,
  shared JSON evidence viewers, exact run-journal navigation and a bounded report.
- Corrected the unreleased qualification run-status wire to canonical
  `awaiting_user`; the stale `awaitinguser` spelling is rejected.

The shell does not execute a model, production tools, COM, Office mutations,
scripts, commands or URLs. It cannot claim Office/model/full-system qualification.

## Main files

- `src/RNAssistant.Office/Qualification/Packs/*` and
  `QualificationBuiltInCatalog.cs`: exact embedded catalog and coverage.
- `src/RNAssistant.Office/Services/QualificationApplicationService.cs` and
  `Controller/AssistantController.Qualification.cs`: application composition,
  durable restore and qualification-chat boundary.
- `src/RNAssistant.Office/WebView/AssistantWebBridge.cs` and
  `Contracts/BridgeDtos.Qualification.cs`: typed routes and bounded projection.
- `web/js/app-qualification.js`, `web/css/app-qualification.css`, `web/index.html`
  and empty-chat integration: one user-facing center over the existing bridge,
  run journal and JSON viewer.
- `tests/RNAssistant.Harness/Program.QualificationTests.cs` and
  `tests/web/qualification-center.test.js`: runner/bridge/replay and UI coverage.

All new C# sources and embedded JSON resources are explicitly included in the
old-style Office, Harness and MockDemo projects. No compatibility adapter, second
store/index, second runner or alternate result authority was added.

## Verification

- `dotnet build tests/RNAssistant.Harness/RNAssistant.Harness.csproj -c Release
  -nologo -v:minimal`: pass, 0 errors; 4 known Windows-only CA1416 warnings.
- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj
  -c Release --no-build -- "qualification:"`: 10/10 pass.
- `dotnet build demo/RNAssistant.MockDemo/RNAssistant.MockDemo.csproj -c Release
  -nologo -v:minimal`: pass, 0 errors; 3 known PDF platform warnings.
- `node tests/web/qualification-center.test.js`: 5/5 pass; `node --check` passed
  for all six changed/added application JS files.
- Real MockDemo bridge run: catalog/start returned canonical `awaiting_user`; after
  process restart the same run restored at the manual boundary, advanced to durable
  `passed`, and ordinary `sendChat` in its qualification chat was rejected.
- Browser preview: empty-chat entry, pack list, manual checkpoint, terminal status
  and five shared JSON viewer mounts were visible; qualification chat made composer,
  attachments and mode/model selectors inert; browser console had no errors.
- `git diff --check`, 271 local Markdown links/anchors and pre-commit
  `ValidateVersionFormat`: pass.

## Open gates

- Windows x64 + Office x64 + VS 2022/VSTO/WebView2 were not run. The shell UI and
  controller wiring therefore remain unqualified on the production host.
- WQ-A3 must add the single Excel identity owner, native observations and narrow
  same-build x64 helper, then collect real WQ0 evidence before production 5B2/7D.
- Production AgentTask/host-probe adapters, full suites and live provider coverage
  remain WQ-A4. Immutable `buildCommit` provenance remains explicitly
  `unavailable` until WQ-A5 BuildEvidenceManifest.
- Product version remains `16.1.0-dev`; no release check, Git tag or push is part of
  WQ-A2.
