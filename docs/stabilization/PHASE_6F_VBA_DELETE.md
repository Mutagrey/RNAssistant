# Phase 6F — VBA delete ownership

Date: 2026-08-29

Baseline: `57c157b49cab12e3256e8416dda63ef65dc6c5ce`

Status: done host-neutral; restore/package ownership and Windows/COM/VBE
qualification remain open.

## Scope and ownership

`common.vba_delete_module` now has one typed domain workflow:

```text
VbaToolExecutor argument/guard/result adapter
→ VbaDeleteModuleRequest + typed guard
→ VbaMutationService.DeleteModule
→ typed current-state read + component policy
→ prepared journal
→ typed compare-and-swap delete backend
→ absence read-back + terminal
→ VbaMutationOutcome (Ok | Error | Unknown)
→ current Tools result adapter
```

`VbaMutationService` owns existing-target resolution, optional observed-state
staleness detection, confirmation guard binding/recheck, allowed component types,
dry-run, preparation, backend action selection and verified terminal outcome. The
removed executor path has no alias or dual execution fallback. Missing or malformed
guard state cannot dispatch a delete; every public executor path first runs the
typed preparation.

`IVbaMutationBackend` gained one typed delete action. Its current Tools adapter is
the only place that constructs `vba_delete_module_internal`; the model and domain
service do not know host-prefixed tool ids or legacy `ToolResult`.

## Safety properties

- only `StdModule` and `ClassModule` pass the domain policy;
- DocumentModule and UserForm refusal occurs before journal preparation and
  backend dispatch;
- journal preparation is durable before the delete action;
- the backend receives the live source SHA-256 as compare-and-swap evidence;
- backend success is not sufficient: `ok` requires verified module absence;
- backend error/throw, read-back drift and terminal append use the shared typed
  `Ok/Error/Unknown` and read-only reconciliation rules;
- accepted call/run/turn/step correlation comes from the prepared guard and is
  retained in the mutation record.

The COM implementation, HostRuntime gate, document binding, journal/CAS format,
tool schema, confirmation policy and public result wire did not change.

## Cleanup and remaining boundaries

Removed from Tools:

- executor-owned `DeleteModule` orchestration;
- `PrepareExistingModuleGuard` and `ValidateExistingModuleGuard`;
- the delete-only `IsExistingModuleMutation` router helper;
- direct construction and legacy result mapping of the internal delete command.

Still intentionally executor-owned:

- restore workflow;
- rename and package operations;
- recovery/reconciliation outer loop;
- legacy argument/result mapping.

The next separate slice is Phase 6G restore ownership. Production document
identity and typed host binding still depend on Phase 5B2 and Windows
qualification.

## Verification

- `vba: delete service owns workflow` — 1/1 pass.
- Full `vba:` filter — 69/69 pass.
- `agent: characterization` — 7/7 pass.
- `causal trace:` — 6/6 pass.
- `harness: production projects include all source files` — 1/1 pass.
- Total — 83 distinct targeted harness cases.
- Harness compile — 0 errors, 4 existing platform warnings.
- MockDemo actual-controller compile — 0 errors, 3 existing platform warnings.
- `ValidateVersionFormat`, changed Markdown links and diff checks — pass.

No Windows x64 + Office x64 + VS 2022, VSTO, real COM/VBE, controller delivery or
live-provider validation was run. Product version and tags remain unchanged; this
commit is not a release.
