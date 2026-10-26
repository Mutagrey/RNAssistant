# Tools architecture implementation plan

Approved scope: workspace + Office text exploration, existing registry/runtime/context owners,
unchanged v8 admission. Baseline and inventory: [audit](../audits/tools-architecture-current-state.md).
Owners: Core search contracts/matcher; Application workspace orchestration; Office resource Gateway;
Office presentation/Web activity; existing storage/VBA/HTML owners retain mutations.

## Stages and dependencies

| Stage | Deliverable | Gate |
|---|---|---|
| P0 | Audit, complete static built-in catalog, decisions and this plan | Documentation links and inventory |
| P1 | Current VBA display constants and semantic target arguments | Presentation checks |
| P2 | Shared closed target/scope search schema, literal/regex matcher, typed results | Schema, Unicode, timeout and coverage checks |
| P3 | Workspace content search and recursive/glob metadata discovery | Exact continuation, conflict, access and cancellation checks |
| P4 | Office VBA/HTML sources and bound workspace Office search | Native ToolRuntime parity; Windows qualification separate |
| P5 | Closed model result, typed preview and activity summary | Coordinates preserved; authority stripped; existing Inspector/trace checks |
| P6 | Existing guarded patch regression checks; delete only replaced paths | Stale/ambiguous/unknown non-replay; no parallel writer |
| P7 | Reproducible mixed-language exploration fixture and evaluation protocol | Measured tool fixture; model A/B remains separate evidence |

P0 → P2 → P3/P4 → P5 → P7. P1 independent. P6 preserves the existing owners throughout.
No new public names: common.resources_search gains target or scope=workspace/vba/html.
Bound workspace Office target search uses type=office; scope=vba is document-bound. HTML scope
belongs to the Office document's existing HTML owner, not the workspace filesystem.

## Contracts and deliberate limits

Semantic query, scope, paths, glob filters and UTF-16 line/column coordinates are model-facing.
Exact references remain runtime-only. Regex is line-oriented .NET, culture-invariant, with a 100 ms
per-match timeout; no promise of full semantic references. Basic search needs no parser or executable.
Workspace traversal scans at most 5000 entries; a page scans up to 100 sources and 8 Mi UTF-16
characters. Regex lines above 65536 characters are skipped with explicit incomplete coverage.
Literal lines use the character scan budget. Workspace source capture has a 64 MiB per-source
operation budget. These are work/safety bounds, not invented performance acceptance thresholds.
Existing Office literal target/indexed search keeps its source/part behavior.

Continuation contains afterTarget/afterLine/afterColumn (0/0 means the whole target was scanned).
It must match the last emitted page and identical query/options. The ephemeral handler retains
exact source pins and ordered membership only, never another body store. Prior sources are
revalidated before continuation. Source changes, changed membership or lost handler state refuse
continuation; restart requires a fresh search. All prior incomplete reasons survive later pages.
A page has bounded matches, cumulative totalMatches and totalMatchesExact; never infer a total from
a truncated result. Search observations cannot authorize files.patch or whole-file replacement.

No atomic directory snapshot or constant-memory COM source capture is claimed. Revalidating prior
sources can be expensive; benchmark before adding a source index. Immutable CAS is existing storage,
not a new search cache. Model request snapshots remain the existing authoritative diagnostic source.

## Validation

Focused Harness: text search; relevant resource discovery/indexed navigation, presentation, guarded
workspace/VBA/HTML patch, v8 and saved-request suites. Existing coverage is reused; no Office/VSTO
validation on this machine. New files enter the old-style Core csproj / Application projitems.
No product version bump, commit, tag or release workflow is implied.

P7 model comparison: same fixed model/version/settings, task corpus, prompts, permissions and source
snapshot. A = current text tools; B = same tools plus optional symbols only after a justified prototype.
Tasks: named function, behavioral defect, signature/call sites, JS→C#→VBA string chain, dynamic event,
and sparse relevant code among unrelated files; unknown-language case mandatory. Record task success,
false links, calls, delivered bytes/tokens, latency, repeated reads, patch conflicts, check outcomes and
premature finish. Do not label a scripted tool fixture as a model experiment. No LSP/index ships here.

## Migration and rollback

Keep exact IDs; changed schema/policy/binding fingerprints invalidate incompatible frozen surfaces.
Never silently rebind pending calls. No chat/CAS rewriting, format downgrade, mutation replay or dual
writer. Existing document-owned HTML/file seams stay in MIGRATION_MAP under their existing owners.
Rollback stops new changed-contract calls and explicitly resets incompatible runs; history is retained.
A future retained continuation implementation must use canonical events rather than another store.

## Recorded tool experiment — 2026-10-25

Harness `text search: mixed project experiment`, 505 in-memory sources / 512236 source
characters, no model/network/COM. Each of the six cases completed in six bounded calls;
2005 source opens include exact continuation revalidation. Named definition / behavioral
defect / signature candidates / cross-language candidates / dynamic event candidates /
sparse marker returned 1 / 1 / 2 / 3 / 2 / 1 expected matches. Serialized delivery was
2024 / 2020 / 2164 / 2439 / 2211 / 2021 bytes. Local runs took 3–5 ms per case, which
is a matcher-fixture measurement, not filesystem, model or Office latency. Source-open
amplification is visible and remains a performance risk; no speedup ratio is claimed.
Model tokens, autonomous task success, false semantic links and real-model A/B have not
been measured. The fixture's string candidates do not assert semantic references.

Implementation status: P0–P5 implemented host-neutral; P6 reuses existing mutation owners
and focused regressions, with no unjustified projection cleanup. P7 has the reproducible
tool fixture and evaluation protocol above; real-model A/B and Windows qualification stay
open. No symbols/LSP prototype, new writer or new evidence protocol was introduced.

Focused verification completed: five `text search:` checks; four `resources: original text`
checks; guarded workspace patch (one); VBA patch refusal cases (four); HTML source/read/search/patch
(one); tool presentation (three); saved request-before-dispatch (one); bound Office target (one);
v8 schema/parser (two); context body deduplication (one). Total: 23 selected Harness checks.
Chromium `agent-activity-layout.test.js` and `tool-result-presentation.test.js` passed using
bundled Playwright and installed Chrome. Harness build succeeded with two existing PDF platform
analyzer warnings. No Office/VSTO validation, full Harness, commit or release was run.

## Remaining work after typed references — 2026-10-26

Chat format and model wire stay at 8. [ADR-0018](../decisions/ADR-0018-typed-model-references.md)
now owns uppercase E/R/O/Q references; no new resource/tool runtime or parallel store
was introduced. The interim 9/10 candidates have no compatibility path.

Current result coverage must not be confused with a tool-by-tool qualification:

| Layer / family | Current coverage | Remaining evidence |
|---|---|---|
| Accepted tools, including dynamic registrations | Shared `ToolResult` / wire envelope and `E` correlation; `data` remains domain-specific | Each producer's actual success/error/unknown and omitted/large-result shapes |
| `common.resources_read` and hydrated current-source views | `ref` beside an unambiguously bound body and matching view/coverage; no loose reference list | Additional table/tree/media and host-specific delivery cases; current focused evidence is text/source |
| `common.resources_find/search/outline` | Navigation fields retained; no inferred per-item `R` | Decide whether citations for snippets are needed; adding them requires typed per-item provenance, not positional matching |
| Mutations, capabilities/skills, plans/questions, Office domain tools, plugins/custom tools | Existing shared/domain projections and `E`; no new automatic `R` on arbitrary data | Audit each owned payload and all representations before claiming complete coverage |
| Storage, diagnostics, chat UI | Canonical results/CAS unchanged; saved exact request shows the projected block | Full result → CAS/events → model → diagnostics/UI matrix; inline chat references remain separate below |

1. Result coverage audit: inventory the actual registered tools (static catalogs plus
   dynamic bindings), record representative result shapes and trace each through
   `ToolResultWire`, Core `ModelResultProjection`, Office `ModelToolResultProjection`,
   `ModelContextCompiler`, `ModelReferenceProjection` and the existing presentation owners.
   Owner: Core model context with the current domain owners. Reuse existing tests;
   add only missing checks for misattribution, lost content or false delivery across
   success/error/unknown, multiple sources, compaction and bounded/omitted bodies.
   Acceptance: every family has explicit model/storage/UI behavior and a verified
   check or named gap; no guessed references, blanket data rewriting or new store.
2. Model evidence: rerun the paired profiles with the final uppercase contract and
   extend evaluation to P7's mixed-language exploration tasks. The archived lowercase
   edit/copy pilot is not final-candidate qualification. Reproduce tool-choice errors
   (create instead of copy, failed source discovery) before changing defaults/descriptions.
3. Presentation: add typed E/R links in chat activity/result summaries if this UI scope
   is selected. Diagnostics already show/search the saved labels and explain their kinds;
   chat currently shows narration/action/result text and navigation to the request.
   Reuse canonical bindings; no parsing of arbitrary prose to infer authority.
4. File cutover: agree the public create/copy/patch/replace/delete/restore contract,
   destination ownership and consumers per the [skills/tools audit](../audits/skills-tools-description-review.md#что-далее).
   Only then migrate consumers and remove old paths. No universal mutation facade is shipped yet.
5. Qualification: benchmark source revalidation on a large real multilingual workspace;
   obtain Windows x64 / Office x64 / WebView2 evidence on the exact release candidate.
   Existing host-neutral and Chrome checks do not close those gates.

C/S references, symbols/LSP and derived resource graphs remain separately scoped options,
not prerequisites silently added to this implementation.
