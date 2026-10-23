# Conversation v8: phase-1 protocol candidate

2026-10-23. Owner: Core ModelProtocol. Base: `1e5c4789` (main's phase-0 commit).
Implementation is isolated in the managed `conversation-v8` worktree. It is not a
runnable application cutover: v7 kernel/adapters/history callers still require
migration and the full application currently cannot compile against the new API.
Main retains v7. Phases 1–4 form one atomic integration; default cutover additionally
requires the phase-5 real-model comparison from [ADR-0016](../../decisions/ADR-0016-conversation-v8-admission.md).

## Implemented boundary

- `ConversationResponse` version 8, three closed forms: execute, reflection, finish.
  Required reason; required message with empty string allowed; decision only for
  reflection; nonempty calls only for execute; results always present for finish.
- Immutable response snapshots, typed claim assessment, nonblank summary/evidence
  and unique references within each claim. Evidence identity is not resolved here.
- Canonical writer emits action/reason/specialized field/message in that order;
  parser accepts any JSON property order. No v7 aliases, inference writer or
  action-only writer remains in this codec.
- Root `anyOf`, closed branch/result/call objects, dynamic captured tool schemas,
  local independent-read batches up to 32 and singleton other calls. No-callable-tool
  schema omits execute. Existing strict optional-null transport projection remains.
- Typed format failures and stable machine codes, including empty_execute_calls,
  missing_finish_results and wrong_action_fields. ModelProtocolClient forwards the
  exact code to repair and trace; provider retry/fallback remains its separate path.
- Canonical v8 guidance and bounded repair content in ModelProtocolWire; no claim of
  accepted/executed rejected calls. Request-options wiring is a partial file;
  ModelProtocolCallContext was moved intact out of orchestration contracts.
  The parser's diagnostic dependency on SkillLoadToolHandler was removed so the
  codec checks do not need to compile execution handlers.

The existing lexical parser still rejects comments, duplicate keys, trailing commas,
nonfinite values, unquoted properties and excessive depth. Tool arguments still
pass the captured schema validator before the response can leave ModelProtocol.

## Local evidence

The source-linked [protocol runner](../../../tests/RNAssistant.ProtocolTests/README.md)
passes **153 response cases / 2156 assertions**, including invalid forms/IDs/claims,
unknown/unloaded tools, exact local batching, argument validation, optional-null
normalization, snapshot isolation, writer round trips, every streaming split and
single-character chunks (including surrogate pairs), empty message and repair codes.
Independent Python `jsonschema` checks **139 envelopes** plus the no-tools union.
Fourteen lexical/depth cases remain parser-specific. One documented representation
difference is the existing optional-field strict transport projection; there is no
mode-dependent local validator. These are synthetic checks, not model task success.

Local Ollama **0.35.0**, model `rna-gemma4-12b-32k:latest`, digest
`cf94f3793da5f501e0a2948b34825db5baceed926438a78551ffaa83f85a4082`:
**9/9** synthetic probes returned HTTP 200, `finish_reason=stop` and schema-valid
content. Root anyOf/oneOf, const, minItems/enum and $defs probes deliberately requested
values excluded by their schema; returned values conformed. The production dynamic
execute schema, reflection and failure-reporting finish passed the independent
validator and actual local parser; a direct finish also passed in json_object mode.
`anyOf` needs no wrapper for this observed endpoint/model. Pattern/uniqueItems and
other providers are not exhaustively qualified; local validation remains mandatory.

[Exact probe manifest](evidence/phase1-ollama/manifest.json), request/response bodies,
[local check summary](evidence/phase1-ollama/local-checks.json), generated schema and
[SHA-256 inventory](evidence/phase1-ollama/sha256.json) are retained. The compressed
candidate-source patch applies to base `1e5c4789bbd569b8afdfd54de17278e86021d9a8` and
includes the actual source-linked runner/probe scripts; compressed codec cases
retain the local positive/negative fixtures. Outputs contain only synthetic inputs.
Token/latency observations belong to these probes, not a task benchmark.

## Open integration gates

1. Phase 2: kernel reflection continuation/limit, canonical terminal evidence resolver,
   current-run/session/origin checks, reject feedback, no replay, durable counters.
   `unknown_evidence`, `nonterminal_evidence`, missing results/evidence after accepted
   calls and InspectionRequired enforcement belong to this boundary, not the parser.
2. Phases 3–4: migrate all removed v7 API callers, prompts/probes and history writers;
   exact version cutover, explicit incompatible-history handling, results/reflection
   persistence, UI and compaction/restart/confirmation/steer/cancel coverage.
3. Re-run the actual frozen context compiler + ModelProtocolClient integration
   scenarios, including repair budgets, provider failure/fallback and source attempt
   identity. Only repair content is exercised by the narrow runner so far.
4. Phase 5: pinned v7/v8 task comparison and order/reason ablation. Windows x64 +
   Office x64/WebView2 remains separate target-host evidence.

The phase-0 premature finish reproducer is retained as historical red evidence;
this codec alone does not fix runtime completion admission. No product version,
release workflow, tag or user history has changed.
