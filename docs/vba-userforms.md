# Code-only VBA UserForms

## Source of truth

RNAssistant supports a deliberately narrow `CodeOnly UserForm` profile. The VBA project contains a blank generated `MSForm`; its code-behind is the only durable semantic source for controls, layout, runtime-settable properties and event bindings. The live form instance is a disposable projection rebuilt from source.

Designer-time controls/properties and `.frx` assets are not part of this profile. They cannot be hashed, compared or restored from `CodeModule` source alone, so RNAssistant must not claim complete backup or replay for them.

## Authoring rules

- Create controls in one idempotent builder called from `UserForm_Initialize`, normally with `Me.Controls.Add` and stable explicit names.
- Set the form caption, size and every relevant runtime-settable control/layout property in code. Do not rely on manually edited Designer state.
- Use typed `Private WithEvents` fields for a fixed number of interactive controls.
- For repeated dynamic controls, keep one typed event-sink class per control in a form-level `Collection`; otherwise the sink can be garbage-collected and events stop firing.
- Put the public `Show` entry point in a standard module. A reusable package may therefore contain a form, launcher and optional event-sink classes.
- Make initialization safe for one form instance. Do not append duplicate controls from `Activate` or another repeatedly fired event.
- After RNAssistant edits or restores form source, unload any existing instance and instantiate it again. Restoring source does not rebuild an already loaded form and does not undo document mutations already performed by its handlers.

Example shape:

```vb
Option Explicit

Private WithEvents btnOK As MSForms.CommandButton

Private Sub UserForm_Initialize()
    Me.Caption = "Parameters"
    Set btnOK = Me.Controls.Add("Forms.CommandButton.1", "btnOK", True)
    btnOK.Caption = "OK"
End Sub

Private Sub btnOK_Click()
    Unload Me
End Sub
```

## Storage and recovery

Direct `common.vba_write_module` and `common.vba_apply_patch` mutations journal the UserForm code-behind like other VBA source. This makes source edit/restore deterministic, but it does not turn VBA into a chat artifact: chat fork, HTML undo/redo and replay never mutate the Office VBA project.

Package storage uses `.form.vba` for code-only form source and never persists an exported `.frm` or `.frx`. Install/remove journals the form, launcher and event-sink classes as one multi-component transaction before COM mutation. Its terminal record retains each component's observed state and rollback outcome. Existing forms can be updated or removed only when runtime verifies an owned blank code-only `MSForm`; a type collision, Designer control, unverified Designer state or exported form payload fails closed.

## Required Windows verification

Validate on Windows x64 + Office x64 + VS 2022 in Excel, Word and PowerPoint: blank `MSForm` creation, `Controls.Add`, fixed and collection-backed events, unload/recreate after source restore, Trust Access disabled, macro-free documents, and package install/remove/crash/rollback behavior.
