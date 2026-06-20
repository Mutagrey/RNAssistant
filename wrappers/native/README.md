# RNAssistant Native Office Wrappers

These VBA modules are the source for lightweight Office-native wrappers that launch
`RNAssistant.Desktop.exe` without VSTO/ClickOnce manifests.

Set the executable path before using the wrappers:

```cmd
setx RNASSISTANT_DESKTOP_EXE "C:\path\to\RNAssistant.Desktop.exe"
```

Suggested wrapper containers:

- Excel: import `excel/RNAssistantExcel.bas` into an `.xlam`.
- Word: import `word/RNAssistantWord.bas` into a `.dotm`.
- PowerPoint: import `powerpoint/RNAssistantPowerPoint.bas` into a `.ppam` or `.potm`.
- Outlook: import `outlook/RNAssistantOutlook.bas` into Outlook VBA or a local macro project.

The modules pass target metadata through `--target-base64`, so paths and
non-ASCII document names do not need command-line quote escaping. They also pass
`--hwnd` so `RNAssistant.Desktop.exe` can validate that COM attach did not land
on a different Office window.

Ribbon XML can call the public macros such as `RNAssistant_Open`,
`RNAssistant_Summarize`, `RNAssistant_ExplainSelection`, `RNAssistant_DraftRewrite`,
`RNAssistant_RunSkill`, `RNAssistant_Settings`, and `RNAssistant_Context`.
