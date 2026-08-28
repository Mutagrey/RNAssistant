# Excel identity qualification probe — Phase 5B2

**Diagnostic candidate only; not connected to production factories or locks.**
Run only on Windows x64 + Office x64 + VS 2022. Do not run Office validation on the
development Mac. The harness tests parsing and the non-Windows refusal, not Excel.

## Candidate and limits

Compare `(Excel process ID, process start UTC, OXID, OID)` on the same machine.
The process fields scope the observation; they do not identify a workbook.
`STDOBJREF` carries exporter/object/interface identifiers; OXID/OID is the object
candidate, while IPID is recorded separately. This is a hypothesis for Excel,
not proof that all Excel proxies expose one stable identity.
[Microsoft: STDOBJREF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dcom/5ee74828-43a8-400b-9629-2bb4e707d7ec).

`ComIdentityLease.Create(workbook)` marshals IUnknown with `MSHCTX_LOCAL` and
`MSHLFLAGS_NORMAL` and retains that packet until disposal. Keeping the reference
is part of the candidate ownership mechanism: repeated snapshots alone do not
prove identity stability when clients attach/detach. `ReadAgain()` creates and
releases another packet while the original remains held. No packet is unmarshaled,
saved, or accepted from a file/network. Only bounded identity fields are emitted.
[Microsoft: marshaling flags](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ne-wtypesbase-mshlflags).

The decoder accepts only a bounded `OBJREF_STANDARD` for IUnknown and checks the
resolver array frame. Custom/handler/extended formats fail explicitly; do not
add a pointer/path/HWND/GUID fallback. Any unsupported format blocks this candidate.
[Microsoft: OBJREF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dcom/fe6c5e46-adf8-4e34-a8de-3f756c875f31),
[resolver array](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dcom/50889dd8-1960-49ca-a444-6212a73dc397).

Dispose on the creating STA, including failure paths. The lease releases its
original trusted marshal packet and its own stream; it does not `FinalRelease`
caller-owned Office RCWs. There is no finalizer because marshal-data release
requires the originating apartment. A retained reference is **not** a liveness
test. Observe collection membership and close/reopen separately.
[Microsoft: CoReleaseMarshalData](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coreleasemarshaldata).

## External clients

Use disposable test workbooks. The script does not write cells, properties or VBA,
save, close, quit, or activate Office. Manual Save As/close/reopen actions below
are performed by the tester. Output includes workbook paths; review before sharing.

Build the diagnostic DLL on Windows with the .NET 4.8 targeting pack installed:

```powershell
dotnet build tests/RNAssistant.ExcelIdentityProbe/RNAssistant.ExcelIdentityProbe.csproj -c Debug
```

Obtain the desired Excel HWND (for example, inspect `MainWindowHandle` for the
chosen EXCEL process). First list workbook indices using that explicit window:

```powershell
Get-Process EXCEL | Select-Object Id, MainWindowHandle
powershell.exe -NoProfile -STA -File tests/RNAssistant.ExcelIdentityProbe/Invoke-ExcelIdentityProbe.ps1 -Hwnd <HWND>
```

Replace `<HWND>` and `<INDEX>` with the observed numeric values. Then run two
**separate Windows PowerShell x64 processes**, using the same HWND and index:

```powershell
powershell.exe -NoProfile -STA -File tests/RNAssistant.ExcelIdentityProbe/Invoke-ExcelIdentityProbe.ps1 -Hwnd <HWND> -WorkbookIndex <INDEX> -ClientLabel client-A
powershell.exe -NoProfile -STA -File tests/RNAssistant.ExcelIdentityProbe/Invoke-ExcelIdentityProbe.ps1 -Hwnd <HWND> -WorkbookIndex <INDEX> -ClientLabel client-B
```

Both resolve through native OM, then bind the explicitly selected workbook once.
Keep the sessions open; enter a scenario label after each manual action. `q`
releases the lease on its STA. JSON records are observations, never a qualification
pass. Require a final `released` record; record any cleanup error as a failure.
`released` means that lease disposal returned without an exception; it does not
prove that every managed/native reference was released. Repeated attach/detach,
process-exit and lifetime observations below remain separate Windows evidence.
The script does not rebind by path/index after the first selection. Local IUnknown
equality is used only for membership in that apartment, not as a shared ID.

## Real desktop / VSTO / native call sites

Two external scripts alone do **not** qualify the in-process VSTO/native paths.
In a disposable Windows debug build, use the actual workbook selected at each
factory/owner STA. Load the diagnostic DLL in that client and retain a
`ComIdentityLease.Create(workbook)` in the debug session. Record `Initial`,
`ReadAgain()`, process/start identity, Excel build and caller/thread information,
then dispose on the same STA. Do not commit diagnostic hooks into production.

- VSTO: the `workbook` argument in `ThisAddIn.EnsurePane`, before adapter creation.
- Desktop/native: the exact workbook resolved for the selected descriptor on the
  dispatcher used by `OfficeComAdapterProvider`; never substitute ActiveWorkbook.
- Repeat from a second independently resolved proxy/STA, not a raw pointer passed
  across apartments. Compare with the external probe while both leases remain held.

The current bound-session tests use supplied fake IDs and cannot substitute for
these observations. If an in-process format differs, retain the failure and revise
the candidate decision before any factory switch.

## Required observations

| Scenario | Required result |
|---|---|
| Same workbook, two clients/STA/proxies | Same server scope + OXID/OID; independently recorded call sites |
| Different open workbooks; same visible name in different Excel instances | Different scoped identities; labels/path alone are not evidence |
| Switch active workbook/window, including during repeated reads | The original bound workbook and identity remain unchanged |
| Save As / first save | Name/path may change; identity and original bound reference do not |
| Close then reopen the same path while an old lease remains | Old target stays closed/unavailable; new binding has a different identity |
| Cancel a close prompt | Still-open workbook retains identity; BeforeClose alone is not liveness evidence |
| Release client A, keep B; attach C | B/C still agree; then repeat with the in-process lease as the only survivor |
| Idle with a retained lease for a representative model/user wait | Same identity after the wait and after a new client attaches |
| Second window of the same workbook | Both observations identify the same workbook |
| Repeated create/dispose; rejected packets / COM errors | No cleanup failure or retained diagnostic references after exit |
| Snapshot without manual edits | Saved flag unchanged before/after bind/read; no document mutation |

Record Windows/Office build and bitness, .NET version, repository revision + dirty
diff, client process/thread, scenario labels, observed values and cleanup outcome.
Any mismatch, unsupported format, ambiguous membership or missing call-site
coverage leaves the identity gate open. After identity qualification, production
`ExcelDocumentSession`/factory wiring and the full gate/write/read-back/confirmation
matrix still require their own Windows tests; this probe cannot close R04.

Owner: HostRuntime/OfficeHosts, removal gate: 5B2 candidate decision. If rejected,
remove the candidate implementation. If accepted, move the qualified reader into
OfficeHosts and point diagnostic consumers at that implementation, deleting the
duplicate probe reader/resolver. This is not a compatibility adapter.
