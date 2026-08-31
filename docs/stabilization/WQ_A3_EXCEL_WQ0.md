# WQ-A3 — Excel WQ0 implementation

Date: 2026-08-31
Scope: host-neutral implementation; Windows qualification remains open.

## Result

- `ComIdentitySample`, `ComIdentityLease` and `ExcelProbeTarget` now have one
  production owner: `RNAssistant.OfficeHosts.Qualification`. The duplicate
  diagnostic source project was removed; the PowerShell fallback consumes this
  owner and cannot decide pass.
- `IQualificationHostPort` is the only application-to-host qualification boundary.
  UI-thread and dedicated-STA wrappers preserve host ownership. Host assertions
  receive completed action evidence reconstructed from the canonical run events.
- Embedded release pack `excel.wq0.identity` covers a runner-owned disposable
  workbook, current in-process owner observation, two independent helper clients, active switch,
  Save As, second window, detach/attach, close/reopen, a same-name workbook in a
  different Excel process, deterministic identity verification and cleanup.
- `RNAssistant.ExcelIdentityHelper.exe` is x64 and same-build only. Its one-time
  named-pipe protocol accepts four exact operations (`bind`, `list`, `observe`,
  `release`), explicit HWND/index, bounded JSON, a random nonce and the exact
  `OfficeHosts` module MVID. It has no network, shell, URL or custom command field.
- The Qualification Center can select quick/full/release suites. WQ0 is visible but
  unavailable unless Windows x64, Office x64 and the packaged helper are present.

## Checks

- `dotnet run --project tests/RNAssistant.Harness/RNAssistant.Harness.csproj -- "qualification:"`
  — 11/11 pass.
- Same compiled harness with `--no-build -- "excel identity probe:"` — 5/5 pass.
- `node tests/web/qualification-center.test.js` — 5/5 pass.
- Pre-commit version validation and diff/link checks are recorded in the commit.

## Open Windows/Office gates

Not run on this macOS machine: OfficeHosts/helper/VSTO build, helper process and
pipe lifecycle, OBJREF marshal/release on real Excel proxies, workbook fixture
actions, second Excel process discovery, WebView wizard, cleanup after Office
errors, or the full WQ0 matrix. Until the embedded pack passes on one exact Windows
x64 + Office x64 build from both VSTO and Desktop/native owners, WQ0, WQ-SESSION,
5B2, 7D and R04 remain open.

No production `IOfficeDocumentSession` identity/factory switch is included here.
Product version remains `16.1.0-dev`; this work does not create a tag or release.
