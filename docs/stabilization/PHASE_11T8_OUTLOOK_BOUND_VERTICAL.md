# Phase 11T8 — bound typed Outlook vertical

Date: 2026-09-01
Scope: all existing public Outlook reads and mutations

## Result

- The exact public ids `outlook.read_mail`, `outlook.search_mail`,
  `outlook.create_draft`, `outlook.update_mail` and `outlook.collect_mail` keep
  their schemas and now use direct `ToolRuntime` registrations, typed
  requests/outcomes and `OutlookService`.
- Desktop composition resolves one exact open Inspector/mail or Explorer/folder.
  Outlook VSTO owns one runtime per window; each runtime retains that exact window
  and target in `OutlookDocumentSession`. A closed window or changed Explorer
  folder fails instead of rebinding during execution through `ActiveInspector` or
  `ActiveExplorer`.
- `OutlookInteropBackend` receives only the retained session, runs under
  `HostRuntime` on the owner STA and never receives `ToolCommand` or calls generic
  `ExecuteTool`. An explicit `entryId` remains an exact read selector; implicit
  selected-mail access is limited to the retained Inspector or Explorer.
- Read, search, collect and attachment projections are bounded. HTML binding uses
  the same typed Outlook read adapter and source-owned read policy.
- Draft and mail-update mutations recheck the selected target immediately before
  dispatch and require operation-specific read-back. No-change/change/error/unknown
  evidence is explicit; any failure after possible effect is non-retryable
  `unknown`.
- The five public branches and all replaced target/folder/tool helpers were
  physically removed from `OutlookAdapter`. Desktop target discovery enumerates
  open Outlook windows, and the fake generic route is fail-closed. There is no
  public Outlook alias or dual execution path.

## Checks

- `outlook tools:` 4/4.
- Isolated full host-neutral harness candidate: 572/576; four unchanged baseline
  failures remain in tool-result materialization/replay outside the 11T8 sources.
- Architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors, existing platform warnings only.
- Production C# sources parse with C# 7.3: 0 syntax errors.
- Version format and `git diff --check` pass.

## Deferred evidence

Windows x64 + Office x64 + VS 2022 must run WQ0/WQ-SESSION/WQ-OUTLOOK against
real Inspector and Explorer windows. Required cases include saved and unsaved mail,
multiple stores/windows, Explorer folder changes and selection ownership, exact
EntryID resolution, attachment metadata, bounded search/collect, new/reply/reply-all/
forward drafts, recipient normalization, categories/unread updates, close during
access, target drift, COM failure before/after dispatch, partial effects, divergent
read-back and per-window pane cleanup. Failure fixes the typed backend or bound
session contract; the removed generic Outlook path must not return.
