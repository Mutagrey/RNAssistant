# Conversation Response v8

Status: **active on `main`**. Host-neutral integration is merged; full Harness, real-model and Windows/Office/WebView2 qualification remain open.
Response protocol is 8, reference contract revision is 2, new chat stream format
is 8, and built-in prompt schema is 57. Formats 6 and 7 remain read-only history;
intermediate 9/10 candidates were never deployed and have no compatibility path.
Earlier format-8 histories without the required bindings or explicit finish outcome are refused;
no automatic rewrite/deletion or execution upgrade. See [ADR-0018](../decisions/ADR-0018-typed-model-references.md).

## Model envelope

In `json_object` mode the model returns one raw JSON object with fields in this order: `action`,
`reason`, action-specific fields, `message` last. `reason` is a short decision
basis, not private reasoning. `message` is user-facing text; only `execute` and
`reflection` may leave it empty. `finish.message` must contain a nonblank answer.
No Markdown, duplicate keys, model-owned IDs or extra fields are accepted.

| Action | Required action-specific field | Meaning |
| --- | --- | --- |
| `execute` | Nonempty `tool_calls` | Propose one or more validated calls. Each call has exactly `name` and root object `arguments`; runtime assigns IDs. Only independent local reads may batch. |
| `reflection` | Nonblank `decision` | Record a useful interpretation and request another bounded model step. No tool dispatch, result or new authority. |
| `finish` | `outcome`, `results` array, nonblank `message` | Assert the final answer and assessed task outcome. No questions, approval requests, offers to continue or promises of future work. |

`results[]` entries contain exactly `summary`, `evidence` (exact short execution
references such as `E1` from delivered terminal results) and `assessment`: `satisfied`,
`partially_satisfied`, `unverified` or `not_satisfied`. A direct answer
without accepted calls may use `results=[]`. After accepted calls, finish
needs at least one result and one terminal evidence reference in aggregate;
discovery calls need not all be cited.

### Finish is an assertion

The universal finish form is `action + reason + outcome + results + message`.
`reason` briefly explains why the model ends the run now. `message` delivers the
actual answer/result, a concise explanation and any unfinished work or verification
limits, in the user's language. A simple direct answer may be one sentence; no
fixed Markdown headings are required. Necessary questions use `execute` with
`common.questions_ask`. Continue feasible authorized work before finishing.
The question/answer distinction is a semantic model instruction; punctuation is
not an admission rule (quoted questions and source code can contain `?`).

`outcome` is required only on finish and is the **model's claim**, independent of
runtime lifecycle, effect health and task verification. Results assess requested
task outcomes, not a list of incidental successful discovery operations.

| `outcome` | Local consistency rule for `results` |
| --- | --- |
| `completed` | Every reported result is `satisfied`. With no accepted calls, `results=[]` is allowed for a direct answer. |
| `partial` | Some work is achieved and some remains: at least one `partially_satisfied`, or a mixture of `satisfied` and `unverified`/`not_satisfied`. |
| `unable_to_complete` | No result is `satisfied` or `partially_satisfied`; explain the obstacle. With no accepted calls, an empty results array is allowed. |

```json
{"action":"finish","reason":"The requested data and chart were produced and checked.","outcome":"completed","results":[{"summary":"Data and chart created.","evidence":["E7","E12"],"assessment":"satisfied"}],"message":"The data and chart are ready. The saved values and chart series were checked."}
```

The parser rejects missing/unknown outcome (`invalid_outcome`), blank final
answers (`empty_finish_message`) and contradictory assessments
(`contradictory_outcome`) in both response modes. They receive bounded format
repair on the same request; rejected drafts never become accepted history.
The strict schema requires outcome and a nonblank finish message; cross-field
assessment consistency is checked locally, including on typed kernel proposals.
No legacy outcome is inferred from text or results.

`ModelProtocolWire` owns the canonical action/message/escaping guidance,
writer and format repair. Default Agent, Chat, Plan, Office editable-prompt
composition and Workspace/CLI use it. `json_schema` uses an explicit transport
wrapper: `{"response": <v8 decision>}`. Its root is a closed object with the one
required `response` property; the three-way `anyOf` lives inside that property.
Strict parsing requires the wrapper and unwraps it exactly once. Accepted history
keeps the inner v8 decision; raw traffic diagnostics keep the actual transport.
`json_object` uses the unwrapped decision. A configured, explicit provider fallback
switches both parsing and prompt guidance to that unwrapped transport. Neither mode
bypasses local parser/evidence/kernel validation; duplicate evidence IDs are checked
locally rather than through an unsupported `uniqueItems` keyword.

JSON-object prompts include three short shape examples and exact argument schemas.
Strict prompts retain action/evidence/permission semantics without repeating the
format examples or tool argument schemas. Argument descriptions stay in the supplied
schema; schema types alone do not explain their meaning. Both full and projected
prompts belong to the same frozen request, including repair and fallback.
Strict admission counts the projected wire prompt and schema at compilation and
dispatch. A fallback separately checks the retained full prompt before sending
`json_object`; an invalid strict schema is reported as a provider error rather
than treated as lack of format support. No-call accepted history rejects stray
tool transport metadata and a second reason outside its v8 envelope.

## Typed references (revision 2)

`E1`, `E2`, … identify accepted operations and their outcomes within a chat. The
existing accepted commit allocates them atomically before dispatch, alongside
canonical call IDs and origins. They survive restart, confirmation, history removal
and compaction without reuse. Prefix and case are fixed; only exact lookup is valid.
Raw execute remains `{name, arguments}`. Wire-only historical calls add `id: "E1"`;
matching results and folded receipts expose `tool_call_id: "E1"`. Canonical history
keeps its original envelope and ID. Native transport pairing is preserved; its
call narration exposes the short reference separately. No global text replacement.

`R1` identifies a scoped logical resource; `R1@v2` identifies a registered exact
revision in that resource's observation history. A model-only `ref` is attached
directly to the delivered `common.resources_read` data, or to the structured header
of a hydrated `CURRENT_RESOURCE_VIEW`. It sits beside that source's semantic target,
representation/view and coverage; it is never a loose list on the result envelope.
Two excerpts of one revision share a reference and retain their different coverage.
The binding requires one typed source observation, matching view/coverage and an exact
scope/URI/revision mapping. Missing or ambiguous bindings produce no label; target
text and array order never establish identity. Runtime URI/hash/revision
remain private. Resource labels are not tool arguments, finish evidence, permission,
coverage or currentness. Model reads still use semantic targets. `C`/`S` are deferred.
ResourceEvidence IDs remain distinct; several observations may belong to one `E`.
`O` labels observations and `Q` requests in diagnostics. All type prefixes are uppercase;
`@v` is a version marker. A single ChatStore allocator persists resource bindings on
message observations and saved request links. Inspector reads these bindings and
groups by exact canonical identity; it never allocates replacement `E`/`R` names.

An omitted body does not erase the operation's reference. Compaction retains the
latest current-run terminal receipt; older receipts are bounded by relevance/budget.
A reference absent from the successful request cannot be guessed from the journal.
Repair/fallback retain the same aliases and report the actual delivered set.

Discovery/search/outline remain navigation results with semantic targets and bounded
snippets/coordinates. Their current durable contracts do not map each visible item
to exact scoped evidence, so they receive no inferred `R` labels. Mutation/omitted-body
receipts expose `E` and outcome; they do not advertise the observed source as delivered.
Saved result bodies, CAS, event bindings, diagnostic resource rows and chat presentation
retain their existing contracts. The saved final model request contains the projected
`ref` exactly as sent. This projection change stays within format 8.

Example model-facing result (line-range end is exclusive):

```json
{"tool_call_id":"E7","name":"common.resources_read","status":"ok","message":"Excerpt read.","data":{"ref":"R1@v2","kind":"resource-read","target":"src/example.txt","representation":"text","coverage":{"kind":"line-range","start":10,"end":12,"fields":[]},"text":"first line\nsecond line","complete":false,"selectionComplete":true}}
```

Here `E7` identifies the execution, `R1@v2` the source revision, and `coverage` the
delivered excerpt. The label itself includes neither the range nor the text.

## Admission, persistence and presentation

Kernel accepts `execute` calls before dispatch and pins their runtime ID,
step and model-attempt origin. Reflection is persisted as a no-call accepted
decision. Settings → Agent → **Глубина рефлексии** (`MaxConsecutiveReflections`,
CLI `--max-reflections`) accepts 0–20, default 2; 0 disables reflection acceptance.
The value is frozen in the run limits, including restart/confirmation. Only accepted
reflection decisions increment it. Terminal tool results and fresh user input reset
it. `REFLECTION_BUDGET` tells the model the remaining allowance before every step;
at zero it requests `execute` or an honest `finish`. An additional valid reflection
is rejected with one semantic correction opportunity; another reflection then ends
with `reflection_limit`. Format repair, provider retries and schema fallback do not
spend this allowance or that semantic opportunity. The separate overall iteration
limit still bounds model steps; no extra step is granted beyond it. Confirmation, restart and continuation keep
the same run ID, limits and accepted-call identities; a confirmation cannot
claim a continuation under a new run ID.

Before accepting `finish`, the run store resolves cited IDs against the
complete validated append-only event stream for this session, run and turn, and
the references actually delivered in the successful model request. Each citation
must resolve through its saved `ExecutionReference` to an accepted call and its canonical
`tool.execution.finished` fact; pending, naked, foreign and invented IDs are
rejected. Failed/unknown outcomes can support honest unsatisfied or
unverified claims. An `ok` result or a macro dispatch alone does not prove a
user-requested effect. Existing effect read-back, unknown-effect guards,
workspace acceptance and no-replay rules still apply.

`finish accepted`, `RunLifecycle.Completed` and task success are distinct.
`RunViewState.GoalVerification` remains `not_assessed`; model
`outcome` and `results/assessment` are shown as claims with exact evidence IDs, never as
runtime proof. The separate visible reflection activity is excluded from model
context; its accepted protocol record retains the decision and attempt origin
as model history. The visible step retains accepted provider reasoning; an execute
response stores that metadata on its first visible tool activity, once per batch. A typed
reflection progress handoff retires the provisional attempt before the next one;
rejected drafts are never promoted into accepted history. Ordinary completed answers need no service card; partial,
unverified and unsatisfied claims receive a separate notice. The run journal
retains the exact accepted wire and tool-result correlation.
Model-context replay sanitizes runtime identifiers from accepted `reason` and
`message` without changing literal text in `tool_calls.arguments`.

`RunViewState.ModelOutcome` is reconstructed from the accepted finish envelope;
it cannot alter runtime health, write counts or `GoalVerification`. CLI emits
`modelOutcome` separately and returns exit 5 for `partial`/`unable_to_complete`,
even when the run ended `Completed/Clean` without requested acceptance checks.
Explicit workspace acceptance still rejects `completed` when its checks fail;
an honest incomplete finish can end the run without passing those checks.
These rules do not establish that every aspect of a free-form user task was
fulfilled: an `ok` read cited as `satisfied` is still not proof of a requested write.

Workspace project/plugin chat also presents accepted finish claims and exact
evidence IDs as model assessments. It shows reflection decisions as saved steps;
an empty execute/reflection `message` does not create an answer bubble. A blank
finish is rejected before acceptance. A direct finish may include
evidence-free claims when this run accepted no calls; any cited foreign ID is
still rejected. Target WebView2 and full integration qualification remain open.

History readers require `ResponseProtocolVersion=8` and exact accepted
metadata. User/developer history cannot contain native call metadata; native
history has exactly one paired call. Workspace replay checks adjacent batch
siblings against the same run, step and model attempt with consecutive indexes;
an index reset cannot start a second batch before the preceding results.
Format-6/7 chats and incompatible format-8 histories cannot continue; no legacy inference,
dual-write or automatic migration is allowed. Windows x64 + Office x64 +
WebView2 and real-model qualification remain open.

## Output-token termination and workspace delivery

The LLM parser preserves typed termination for both JSON and SSE. `length` never
accepts even apparently complete tool JSON. ModelProtocol gives one short-response
correction inside the configured format-attempt budget, without increasing token
settings. Repeated truncation ends as `OutputTruncated` with an actionable diagnostic.
No proposed call from either truncated response is dispatched.

Workspace messages retain accepted reasoning/usage and stable message/run identities.
Project/plugin snapshots expose typed run state, explicit read-only compatibility,
and safe activity fields. Unsupported historical protocol bodies are skipped visibly
without changing the source record. The renderer retains unchanged message nodes.
While a run belongs to the current process, snapshots also expose a bounded transient
message/reasoning preview scoped to its run, step and attempt. Accepted/terminal facts
retire that preview; it is never a durable store or execution authority. CLI receives
these progress events through its callback. A desktop attached to a run in another
process still sees persisted progress; cross-process token streaming and history
paging beyond the existing 100-message window remain separate open delivery work.
