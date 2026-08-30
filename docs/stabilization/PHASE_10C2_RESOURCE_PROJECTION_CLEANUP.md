# Phase 10C2 — resource projection cleanup

Date: 2026-08-31
Baseline: `00153be86f1b8d11d86771b350a1bcd5680a0b3b`

## Scope

The four controller-facing definitions for `common.resources_list`,
`common.resources_resolve`, `common.resources_search` and
`common.resources_read` now use `ControllerToolDefinition.CreateReadProjection`.
The projection preserves each native handler's exact descriptor id, description,
JSON schema and source-owned `ToolPolicy` instance, including confirmation and risk
fields.

`LegacyToolDefinitionAdapter.ProjectRead` and the resource catalog's dependency on
the runtime legacy adapter were removed. The adapter's active `Adapt`, `PolicyFor`
and `BindingFor` methods remain for the consumers listed in `MIGRATION_MAP.md`.

## Boundaries preserved

- No native handler, execution binding or `ResourceGatewayService` behavior changed.
- No ToolPack authority, admission/revision, mode policy or model wire changed.
- No policy was reconstructed: the controller definition references the same
  immutable handler-owned instance.
- A project-structure assertion prevents resource data-plane/catalog files from
  depending on `LegacyToolDefinitionAdapter` again.

## Verification

- `tool runtime: native resource tools manual and model paths`: 1/1 pass.
- `resources: hard cutover artifact tools`: 1/1 pass.
- `architecture:`: 4/4 pass.
- `harness: production projects include all source files`: 1/1 pass.
- `ValidateVersionFormat`: pass; product remains `16.1.0-dev`.
- `git diff --check` and changed Markdown local-link validation: pass.

Windows/Office/WebView and real-provider qualification were not performed on this
machine. WQ-PACK remains open; this structural cleanup does not qualify runtime
provider, media-lifetime or restart reconstruction behavior.

## Result

Phase 10C is complete host-neutral. The next mandatory host-neutral step is the
separate Phase 10D canonical-docs, migration-status, project-include and architecture
suite audit.
