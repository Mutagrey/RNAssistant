# ADR-0011: Working baseline and trunk development

Date: 2026-09-28
Status: Accepted for ordinary development; formal release decision remains separate.

## Context

The mandatory host-neutral architecture migration is complete. RNAssistant is used
for document work and agent tasks, while reproducible reliability defects and
Windows/Office/WebView2 qualification gaps remain. Keeping every change inside a
permanent stabilization phase and creating a branch per task delayed bug fixes and
made historical migration gates look like requirements for all future work.

## Decision

- Accept the migrated system as a working baseline for continued development. This
  records the maintainer's operational assessment, not exact-build release evidence.
- Use `main` for ordinary sequential fixes and scoped product work. Create a branch
  only for parallel work, a risky experiment, or an explicitly requested PR.
- Retire the general feature freeze and Phase 11 → WQ → Phase 12 dependency queue as
  daily work control. Prioritize data safety, truthful effects, reproducible defects,
  responsiveness and diagnostics. Require a concrete user goal and owner for features.
- Keep the canonical runtime and data-safety contracts. A possible effect without
  read-back remains `unknown`; neither model prose nor a passing host-neutral test
  qualifies Office behavior.
- Keep open Windows, Office, WebView2 and provider evidence visible. Run targeted
  validation for changed behavior; use the complete exact-build matrix when making
  a formal release or claiming a platform is qualified.
- Preserve in-flight work. The branch-policy change never authorizes automatic
  commits, rebases, stashes, branch deletion or movement of another working tree.

## Consequences

`AGENTS.md` and [development rules](../development-rules.md) own the current workflow;
[PROGRESS](../stabilization/PROGRESS.md) holds the short operational status and open
priorities. The [master plan](../stabilization/STABILIZATION_MASTER_PLAN.md) and
cutover reports remain historical evidence. The working baseline alone creates no
tag, version bump or signed build manifest. [Versioning](../operations/VERSIONING.md)
and [release process](../operations/RELEASE_PROCESS.md) remain the canonical release
contracts. No runtime protocol, storage format or Office binding changes here.
