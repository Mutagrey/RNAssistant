# Phase 11O3 — HTML semantic intents

Date: 2026-09-02
Scope: `common.html_workspace_*`, `common.html_data_*`

## Result

- The former eight-tool model family is replaced by seven exact Agent-only
  verified writes: separate file/data writes, exact patch, semantic delete,
  bind, refresh and freeze. Public inspect, active-selection and general upsert
  IDs are removed without aliases.
- File kind, preview selection, static preflight, revisions, resource references,
  hashes and refresh policy are runtime/UI-owned. Static preflight runs after
  mutations and when the preview state is projected.
- Bind accepts only a semantic name and optional transform/header choices. It
  consumes the latest successful eligible accepted Office read from the same
  Agent run, validates the exact call/result pair and keeps source identity and
  arguments only in durable runtime state. Refresh revalidates that stored schema
  and invokes only the typed Office read owner.
- Model Tool Results remove URI/revision/hash/source/internal identity. Accepted
  history rejects old HTML schemas, synthetic `rna_*` names and runtime-owned
  arguments before another model request; incompatible history requires an
  explicit new chat/reset. Prompt schema is 19.
- The reviewed inventory now contains 67 unique built-in IDs and 70 effective
  host variants.

## Checks

- Targeted harness: 46/46. This includes HTML 23/23, direct host ownership 8/8,
  R61 inventory/discovery, prompt/settings/catalog/source-inclusion and exact
  resource replay/projection checks 15/15.
- Affected web tests: 25/25; changed JavaScript syntax passes.
- Version-format and final diff checks pass before commit. Existing CA1416
  platform warnings remain unchanged.

## Deferred evidence

Windows WebView2 and real Excel/Word/PowerPoint/Outlook bind/refresh execution
remain mandatory. This host-neutral result is not Windows qualification and does
not make the build stable/beta/RC.

## Next

11O4 switches Prompt/Tool/Skill authoring to minimal semantic schemas with
runtime-owned validation and authority. Phase 12 remains blocked until the full
R61 route and accumulated Windows qualification are complete.
