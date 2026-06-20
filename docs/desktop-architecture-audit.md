# Desktop Office Assistant Architecture Audit

## Current State

- `RNAssistant.Desktop` already exists as standalone WinForms/WebView2 shell.
- `RNAssistant.OfficeHosts` contains shared Excel/Word/PowerPoint/Outlook COM adapters.
- `RNAssistant.*AddIn` are now compatibility VSTO shells, not the only launch path.
- `wrappers/native` contains VBA launcher modules for `.xlam`, `.dotm`, `.ppam`/`.potm`, and Outlook VBA.
- Web UI is static local files under `web`; no npm/bundler is required.
- Tool safety is metadata-driven through `MutatesDocument`, `AgentCanRun`, and `RequiresConfirmation`.
- VBA mutation tools are not agent-runnable by default.

## Implemented Desktop Path

```text
Office launcher or manual attach
    -> RNAssistant.Desktop.exe
    -> WinForms shell + WebView2
    -> RNAssistant.Office controller
    -> RNAssistant.Office STA dispatcher
    -> RNAssistant.OfficeHosts COM adapter
    -> Office object model
```

Desktop activation supports:

- `--host Excel|Word|PowerPoint|Outlook`
- `--hwnd <window-handle>`
- `--pid` / `--process-id`
- `--document-path`
- `--document-title`
- `--selection`
- `--target` / `--target-base64`
- `--action`

The native wrappers pass `--hwnd` and target JSON with `Hwnd`. The desktop app is single-instance; later launches send a JSON activation message to the user-scoped named pipe.

## Target Selection Model

Desktop owns a lightweight target registry:

- mode: `Manual` or `Auto follow`;
- known targets from launcher activation, `Use active`, or explicit `Refresh`;
- selected working target independent from the currently focused Office document;
- target records store only descriptors: host, hwnd, process id, document path/title, folder/mail id, and selection reference.

No long-lived `Workbook`, `Range`, `Document`, `Presentation`, `MailItem`, or other COM object is stored in the registry. Adapters resolve live COM objects from the selected descriptor only when a tool runs.

Default behavior is `Manual`:

- the first detected target is selected;
- later launcher events add/update targets but do not switch the working document;
- the user switches documents through the Desktop target dropdown or `Use active`.

`Auto follow` is opt-in:

- incoming launcher activation switches the working target immediately;
- this is useful for users who want the assistant to follow Office focus.

## Safety Findings

- `Marshal.GetActiveObject` is still used for ROT attach, but explicit `hwnd` now validates the resolved COM object before adapter creation.
- If the resolved COM object does not match the requested window/process, attach fails instead of silently operating on a different Office instance.
- Excel attach first tries to resolve `Excel.Application` from the launcher `hwnd` through the native Office window object, then falls back to ROT attach.
- Full Excel multi-instance enumeration is still best-effort; exact attach depends on a launcher/foreground `hwnd`.
- `Refresh` target enumeration is best-effort and based on currently available ROT objects; launchers remain the more precise source for hwnd/document metadata.
- Desktop COM calls now flow through `DispatchedOfficeApplicationAdapter` and a dedicated STA thread before they reach host COM adapters.
- Mutating tools still flow through existing confirmation policy.
- Outlook now follows the Inspector-first, Explorer-selection-second rule.

## Runtime/Install Findings

- Desktop install path does not require ClickOnce.
- `install-desktop-local.cmd` writes `RNASSISTANT_DESKTOP_EXE` to the CurrentUser environment.
- Desktop logging writes to `%LOCALAPPDATA%\OfficeAssistant\logs`.
- WebView2 fixed runtime fallback already checks `vendor/webview2-runtime`.
- VSTO install remains optional compatibility/debug path.

## Remaining Work

- Validate Excel `hwnd` native-object attach across multiple Excel instances on Windows.
- Add Named Pipe direct messages from wrappers, not only single-instance command-line forwarding.
- Add DockingService floating/pinned modes.
- Broaden typed C# tools to the full target list.
- Add controlled temp macro injection fallback with explicit user confirmation and Trust Access detection.
- Validate the Desktop STA dispatcher against real Office modal/busy states on Windows.
- Validate on Windows + Office x64 + VS 2022.
