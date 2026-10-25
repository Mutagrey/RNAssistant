# Skill guidance comparison — 2026-10-26

Owner: Core skill contracts / Office skill catalogs. Scope: evaluate the guidance
and dependency change, without changing protocol, runtime or mutation owners.

## Previous evidence and recovered profiles

[Conversation v8 pilot](../stabilization/conversation-v8/PHASE5_PILOT.md) retains
local-model comparisons, exact requests and archives. The existing CLI comparison
runner changes protocol/transport variants; it is not used for this skills-only A/B.
Ollama at `http://127.0.0.1:11434` is available. `/api/tags` confirms:

| Profile | Exact tag | Digest | Mode / requested context |
|---|---|---|---|
| Local pilot | rna-gemma4-12b-32k:latest | cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082 | json_schema / 32768 |
| Cloud reference | gemma4:cloud | ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba | json_object / 32768 |

Cloud digest pins the local alias metadata, not immutable remote model weights.
Historical cloud results include both successful workflows and tool-format failures;
prior success is not proof that the current v8 request will work. Local aliases for
Qwen and GPT-OSS are installed too; this bounded pilot starts with the two Gemma profiles.
No model download, profile update or user-session reuse is needed.

## Isolated variants

- A: `6ae5cfb471dccfc9553bd846a0f86b7dbef9f76d`.
- B: `8bdeb8a48b2a2ccf574c7c19a0168d4e3f72d4a9`.
- Production diff: only `WorkspaceSkillProvider.cs` and `BuiltInSkillProvider.cs`.
  Tool schemas, v8, prompts, model settings and source fixtures are unchanged.
- A built from a Git archive in an isolated temporary directory; B uses the current
  host-neutral Harness build. Evidence manifest records source refs and binary SHA-256.
- Same model within each pair, developer instructions, reasoning_effort request mode,
  temperature 0, 4096 output tokens and fixture limits. Requested reasoning behavior
  must be checked from actual HTTP payload/response rather than inferred from its name.
- Fresh fake-document/session/store per run. Automatic approval applies only to these
  synthetic fixtures. No Office, user documents or native VBA execution.

## What can already be measured without a model

| Selected skill | A required host tools | B required host tools | Common resource tools in both |
|---|---:|---:|---:|
| word.document_editing | 2 | 8 | 4 |
| powerpoint.deck_building | 2 | 8 | 4 |
| outlook.email_assistant | 1 | 4 | 4 |

The next request now exposes the operations already named in those skill workflows.
This resolves a dependency omission; it also increases potential schema/context cost.
It does not establish better model selection or fewer calls. Plugin optional check/apply
are admitted only when published; absence returns explicit fallback guidance.
The previous commit's built-in package fixture covers this next-request exposure.

The existing tool-only mixed-project experiment was rerun: 505 sources, 512236
characters, six cases. Each uses 6 calls and 2005 source opens, including revalidation.
Delivery bytes: 2024 / 2020 / 2164 / 2439 / 2211 / 2021. Hits: 1 / 1 / 2 / 3 / 2 / 1.
Observed elapsed milliseconds: 41 / 4 / 3 / 4 / 3 / 3; first-run warmup prevents
interpreting this as a regression against earlier 3–5 ms. This is a tool experiment,
not model task success or semantic reference resolution.

## Live pilot and acceptance

Use existing `--vba-workflow-live` first. Task explicitly asks to load
`common.vba_code_editing`, read ReportModule, replace Old report with Sales report
in one patch, preserve other source and verify the result. Thus this is a controlled
execution/regression case, **not spontaneous skill discovery** or project-scope search.
The existing fixture independently checks exact final source and write count and
retains requests, responses, context receipts, session/store and acceptance.json.
Order: local B → A, cloud A → B. One pair per profile is exploratory, not statistical.

Existing broader scenarios: `--resource-workflow-live` (HTML/CSS/JS + VBA) and
`--excel-report-workflow-live` (Excel report, unprompted skill selection unless
RNA_REQUIRE_REPORT_SKILL=true). They remain distinct cases, not substitutes for
Word/PowerPoint/Outlook model tests. A scoped-search task and host-specific autonomous
selection cases still need independent acceptance fixtures before claiming coverage.

Do not infer correctness from finish alone. Report protocol failure, source acceptance,
LLM/tool calls and repeats separately; keep failed runs and exact HTTP evidence.
Only compare cost/latency for paired equivalent tasks and label cold-start effects.
No universal-tool migration or LSP prototype follows from one successful pair.


## Harness defect found during the pilot

Initial local A and B each returned two schema-compliant wrapped responses, then
failed with zero tools. This was `HARNESS_ERROR`: the diagnostic used
`CreateConversationRunService` → `FixtureCompletion`, a scripted helper that adds
another response envelope. Production parser correctly rejected the resulting
nested envelope. Those runs cannot measure model quality or the skill change.
The live diagnostic now constructs the production service directly; scripted mode
retains its helper. The identical harness-only fix is applied to both archived A
and current B and recorded separately from the skills diff. Failed evidence is kept.

Existing strict-schema transport/repair check passes. Scripted VBA at 32K requests
compaction and violates that fixture's no-compaction assertion; at 131072 it passes
with four completions and exactly one write. No production context policy is changed.

## Results after the shared harness fix

| Profile / variant | Acceptance | Model calls | Tool calls / writes | HTTP prompt / completion tokens | Request bytes | Seconds |
|---|---|---:|---|---:|---:|---:|
| Local 32K A | CONTEXT_ERROR | 2 (one compaction) | 1 / 0 | 7993 / 281 | 63325 | 43.77 |
| Local 32K B | CONTEXT_ERROR | 2 (one compaction) | 1 / 0 | 7993 / 281 | 63325 | 46.44 |
| Cloud 32K A | PASS | 4 | 3 / 1 | 58573 / 371 | 284636 | 6.16 |
| Cloud 32K B | PASS | 4 | 3 / 1 | 58685 / 383 | 285103 | 9.26 |

Both cloud trajectories: skills_load → resources_read → vba_apply_patch → finish.
Both produce exactly the required final module and one write. No mutation is retried.
Verified byte-identical paired task, initial source, profile and scenario files.
The cloud request totals differ by 467 bytes and 112 prompt tokens; one pair does not
establish a latency or quality effect. Local timing includes model warmup/cache effects.

Local A needs approximately 29847 input tokens including schema and reserves; B needs
29899, against a 27648 input limit. Both stop after selecting the skill and compaction,
before source read/patch. This is an existing strict-schema request-budget limitation,
not evidence that the local model cannot edit VBA or a newly introduced 32K regression.
Cloud uses json_object, so its smaller schema overhead is not a controlled comparison
of model intelligence against the json_schema local run. Do not compare their scores
as if only the model changed.

The matching digests were recovered from existing tests; no new profile was invented.
Endpoint request traces show reasoning_effort=none. The local schema wrapper is
required by transport and correctly handled by production after bypassing the helper.

[Evidence archive](evidence/skills-guidance-pilot-20261026.tar.gz) includes the manifest,
per-file hashes, paired exact HTTP requests/responses, context/acceptance/session/CAS,
invalid initial runs and the common harness fix. SHA-256:
`9f3a9ef2d1880c219e4e9fee6c2f4f3b4d7110d9db322f81f6c0e5a47ee05386`.
Local working evidence: `/tmp/rna-skills-ab-5uXf01`; the archive is the retained copy.
The script records DLL paths for provenance; rebuilding on another machine requires
both pinned sources, the same harness-only patch and newly recorded binary hashes.

Next: address the observed strict-schema skill-budget pressure in its own scope,
then add unprompted host-authoring and scoped-search tasks with independent graders.
Do not merge all tools on this evidence: loading larger combined packages could
increase the measured budget problem. File unification remains a separate owner/API
migration under ADR-0013, not a remedy for this harness defect.
