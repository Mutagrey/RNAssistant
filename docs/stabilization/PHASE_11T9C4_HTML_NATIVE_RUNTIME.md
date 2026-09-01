# Phase 11T9C4 — native HTML workspace runtime

Date: 2026-09-01
Scope: `common.html_workspace_*`, `common.html_data_*`

## Result

- `HtmlWorkspaceToolCatalog` owns all eight exact Agent descriptors and policies.
  Inspect is an independent local `Read`; the seven mutations use
  `Write + ToolVerification`.
- `HtmlWorkspaceToolHandler` and `HtmlWorkspaceToolService` are the only
  execution path. They preserve typed dispatch/effect evidence and publish exact
  artifact/member resource references.
- Bind and refresh validate the exact source schema and invoke only the existing
  typed Excel, Word, PowerPoint or Outlook backend under the document access gate.
  The generic host-command fallback is removed.
- `HtmlArtifactToolExecutor`, `ControllerExecutorKind.HtmlArtifact` and the four
  adapter `ExecuteDataSource` compatibility shims are deleted without aliases or
  dual dispatch.

## Checks

- HTML-focused harness: 23/23, including native ownership, policy, verified
  mutation/no-change evidence, resource references and direct bound Excel source.
- Bound Word/PowerPoint/Outlook source regressions: 3/3; Plan mode: 3/3;
  ToolPack: 6/6; architecture boundary: 4/4; production source inclusion: 1/1.
- MockDemo build: 0 errors; three existing platform warnings.
- Version format, changed-document links and `git diff --check` pass before commit.

## Deferred evidence

Windows WebView/live Office must verify workspace projection, persistence, export
and all four bound source reads. Qualification fixes the typed path and cannot
restore the removed executor or fallback.

## Next

11T9C5 moves `common.capabilities_search/read` to exact native handlers and removes
`ControllerExecutorKind.CapabilityDiscovery`.
