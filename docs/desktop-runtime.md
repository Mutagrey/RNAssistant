# Desktop runtime

Статус: канонический контракт standalone WinForms/WebView2 shell. Отложенные
продуктовые улучшения находятся в
[BACKLOG](stabilization/BACKLOG.md#deferred-product-decisions), а Windows gates —
в начале [PROGRESS](stabilization/PROGRESS.md).

## Runtime path

```text
Office launcher or manual attach
    -> RNAssistant.Desktop.exe
    -> WinForms shell + WebView2
    -> RNAssistant.Office controller and STA dispatcher
    -> RNAssistant.OfficeHosts bound COM adapter
    -> Office object model
```

`RNAssistant.*AddIn` остаются compatibility/debug VSTO shells. VBA launchers для
Excel, Word, PowerPoint и Outlook находятся в `wrappers/native`.

## Activation and target selection

Desktop принимает `--host`, `--hwnd`, `--pid`/`--process-id`,
`--document-path`, `--document-title`, `--selection`, `--target`,
`--target-base64` и `--action`. Native wrappers передают window handle и target
JSON. Приложение single-instance; последующие launches пересылают activation через
user-scoped named pipe.

Target registry хранит только descriptors: host, hwnd, process id, document
path/title, folder/mail id и selection reference. Долгоживущие COM-объекты в нём
не сохраняются; bound adapter разрешает live object во время операции.

- `Manual` — первый target выбирается автоматически, последующие activation только
  обновляют список. Пользователь явно меняет рабочий документ.
- `Auto follow` — launcher activation сразу меняет выбранный target.

Уже принятый run закреплён за exact document session и не следует за фокусом.

## Safety and runtime limits

- Explicit `hwnd` проверяется против разрешённого COM application/window;
  несовпадение завершает attach ошибкой.
- Excel сначала разрешает application через native Office window object, затем
  использует ROT fallback. Multi-instance enumeration остаётся best-effort;
  launcher/foreground `hwnd` является наиболее точным источником.
- COM calls проходят через `DispatchedOfficeApplicationAdapter` и выделенный STA.
- Mutations используют общий confirmation и ToolRuntime policy; успешный COM return
  сам по себе не доказывает effect.
- Outlook выбирает Inspector раньше Explorer selection.

Desktop не требует ClickOnce. `install-desktop-local.cmd` сохраняет
`RNASSISTANT_DESKTOP_EXE` в CurrentUser environment. Logs находятся в
`%LOCALAPPDATA%\OfficeAssistant\logs`; fixed WebView2 fallback — в
`vendor/webview2-runtime`.

Реальные multi-instance attach, Office modal/busy states и production STA/COM
cleanup требуют Windows x64 + Office x64 + VS 2022 qualification.
