# Outlook 2013 setup

Outlook VBA is stored per user in:

```text
%APPDATA%\Microsoft\Outlook\VbaProject.OTM
```

1. Open the Outlook VBA editor and import `RNAssistantOutlook.bas` into a normal
   module.
2. Set `RNASSISTANT_ROOT=C:\Temp\RNAssistant`, or use the built-in
   `C:\Temp\RNAssistant` fallback.
3. Restart Outlook and allow the signed/approved macro project.
4. Add `Project1.RNAssistantOutlook.ShowAiPanel` through:
   `File → Options → Quick Access Toolbar → Choose commands from: Macros`.
5. Add `CloseAiPanel` the same way if required.

The macros are public parameterless procedures, so they also appear under
`Customize Ribbon → Choose commands from: Macros`.

Outlook 2013 cannot receive portable Ribbon XML from `VbaProject.OTM`.
Programmatic Ribbon integration requires an `IRibbonExtensibility` COM add-in,
which is outside this no-registration MVP.

If the macro does not run, check Trust Center macro policy, Trusted Locations,
bitness matching and `C:\Temp\RNAssistant\logs`.
