# JS execution: standalone Jint process → WebView2 Web Worker

Patch base: `Mutagrey/RNAssistant`, verified against `main` commit `8e197bcfbf954c8c679cff0a824a3bbb4cd39b47` on **2026-11-18**. This is a **code patch awaiting Windows validation**, not a qualification or production-release report.

Owner: Office tool runtime / WebView executor. Retire this report after the Windows
acceptance matrix is closed; fold its durable contract into Tool Library and its
evidence into `PROGRESS.md`.

## Why this cutover

Before this cutover, `JsToolHandler.RunWorkerAsync()` started `RNAssistant.JsWorker.exe` and spoke line-delimited JSON on stdin/stdout; that process owned Jint/Acornima. WebView2 is already in the Office stack, but `TabularWebViewExecutor` is reserved for tabular shipped code and **must not** be repurposed to run arbitrary agent JavaScript. There is no reason to keep two JS runtimes after a verified cutover.

## Resulting boundaries

- `js.run` and authored `executor=js` both go through the same `JsToolHandler` and `JsWebViewExecutor`.
- Per invocation: dedicated STA message loop and hidden `CoreWebView2Controller`, in-private profile under a unique temporary directory, and a dedicated Web Worker. It tries the app's bundled Fixed Version Runtime and then the installed Runtime, requiring Chromium 152+. No Shell control, DOM access, COM, CLR, filesystem access, user Edge cookies or privileged host object is injected.
- The two trusted executor assets are **embedded in `RNAssistant.Office.dll`** and served from a single in-memory HTTPS origin. Every other HTTP/HTTPS WebResourceRequested route is rejected. Network policy is reinforced by CSP, Connection-Allowlist and existing Chromium flags; these controls require runtime verification and are **not a proof** that every UDP/socket channel is blocked.
- The existing Resource Data Plane owns explicit revision-pinned inputs. `RN.resources.names()`, `open/read/stream/close`, sequential offsets, binary chunks, read-only policy and 1 MiB JSON response remain.
- A process-wide semaphore restricts concurrency to **one** invocation. A 15-second total deadline (including cold startup), cancellation, late controller-close handling and a 5-second shutdown + pending-request drain threshold are applied. If cleanup fails, the executor refuses further work until host restart; **no Jint fallback**.
- Native `ResourceDataPlaneService.Dispose()` closes residual leases even for JS code that omits `close()`.

## Removed after cutover

- `src/RNAssistant.JsWorker/` project, source, and packages.config.
- `packages/Jint.4.9.2/`, `packages/Acornima.1.6.2/` (only these dedicated, previously tracked package assets; shared `System.Memory`, etc. are retained).
- Solution registration, worker MSBuild build/copy targets, portable-package requirements and obsolete wrapper README entries.

## Important limits and differences

The Jint worker used heap 64 MiB, 1,000,000 statements, recursion depth 100 and a 10-second engine timeout (12-second process deadline). **None of those heap/instruction/recursion limits is reproduced by V8.** A Web Worker is an execution realm, not necessarily a separate renderer process from other WebView2 controls. Cancelling a runaway script by closing a controller is not the same as a guaranteed hard OS memory cap. OOM or process-host loss can affect other embedded browser controls, despite the isolated WebView2 user-data folder. These are release-blocking qualification concerns; do not claim parity with Jint limits.

The new code also caps JavaScript *source* at 1 MiB UTF-8 to bound host messaging. Legacy bodies above this size must be restructured rather than silently truncated.

## Acceptance matrix — run on actual Windows WebView2 Runtime

| Case | Expected outcome |
| --- | --- |
| `return { sum: args.a + args.b }` | JSON result and evidence, no child `.exe` |
| `RN.resources.names()`, named `open`, `read`, `stream`, `close` | exact revision; bounded chunks; no mixed views; all leases drained |
| custom tool `executor=js` in plugin/Library | identical execution path as built-in `js.run` |
| `return undefined`, cyclic object, result > 1 MiB | explicit failure, no retained worker |
| cancellation before creation, during environment/controller creation, during `read` | result cancelled, late controller closed, no stale response |
| `for (;;) {}` and busy Promise loop | 15-second deadline, worker/controller shutdown, Shell/UI remain responsive |
| repeated jobs (≥ 100), cold and warm | profiles/controllers do not accumulate; 1-at-a-time admission |
| renderer crash / allocation stress in **disposable test host** | fail-closed behavior, no Shell state corruption; capture process memory |
| `fetch`, image/url import, XHR, WebSocket, Worker importScripts, local file, loopback, WebRTC/STUN | no unauthorized network or file access (verify actual Windows network traces) |
| x86/x64 installed Runtime, Office 2013/2016+, desktop | WebView2 startup / resource reads, no hidden form/taskbar leakage |
| legacy deployment builds and portable publisher | no `RNAssistant.JsWorker.exe`, `Jint.dll`, `Acornima.dll` outputs or missing input errors |
| App exit / Office shutdown while executing | no pending STA thread / WebView2 renderer / resource lease after close |

**Minimum checks before merge:**

```cmd
python apply_patch.py C:\path\to\RNAssistant --check
python apply_patch.py C:\path\to\RNAssistant
cd /d C:\path\to\RNAssistant
node tests\web\js-executor-worker.test.js
msbuild RNAssistant.sln /p:Configuration=Release /p:Platform=x64
msbuild build\RNAssistant.NativePortable.proj /t:Publish /p:Configuration=Release /p:Architecture=x64
```

Also run Win32/`x86` and at least one live Office-host invocation. Without Windows builds and the matrix, the change is **In Review**, not complete. The test script covers the worker protocol only, not WebView2 native lifecycle or OS network isolation.

## Review precautions

This bundle changes only the local checkout and requires no network. It refuses to overwrite modified targeted files, missing anchors or pre-existing executor files; run `--check` first. Since `main` can change concurrently, revalidate before applying. Review `git diff` and `git status` before committing. The patch removes tracked old Jint/Acornima assets, not shared NuGet runtime dependencies. If a new consumer of either package has appeared, **do not apply** until that dependency is reconciled.

### Legacy output directories

MSBuild no longer copies `RNAssistant.JsWorker.exe`, `Jint.dll` or `Acornima.dll`. Already-populated local `bin/` and `artifacts/` directories can still contain binaries from an older build; they are not source dependencies. **Close Office**, then clean the relevant build output/portable directories before packaging. The portable publisher prunes its own previously recorded files through `.rnassistant-portable-files`, but unmanaged copies outside that manifest require explicit review. Do not indiscriminately delete shared `System.Memory` or related .NET dependencies.
