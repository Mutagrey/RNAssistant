# Conversation Response v8

Status: **active on `main`**. Host-neutral integration is merged; full Harness, real-model and Windows/Office/WebView2 qualification remain open.
Response protocol is 8, reference contract revision is 3, new chat stream format
is 8, and built-in prompt schema is 59. Formats 6 and 7 remain read-only history;
intermediate 9/10 candidates were never deployed and have no compatibility path.
Earlier format-8 histories with missing/older ReferenceContractVersion are read-only;
new chats persist revision 3. Incompatible continuations are not hydrated or resumed;
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

`results[]` entries contain exactly `criterion`, `evidence` (issued eligible
terminal references such as `E1`) and `assessment`: `satisfied`,
`partially_satisfied`, `unverified` or `not_satisfied`. Criteria describe original
user requirements; no runtime criterion registry exists. `summary` is rejected.
Reference lookup is case-insensitive; duplicate `E1`/`e1` within one claim is invalid.
With no eligible terminal results, including service-only calls, evidence/results
may be empty. Otherwise at least one delivered eligible reference is required.

### Finish is an assertion

The universal finish form is `action + reason + outcome + results + message`.
`reason` briefly explains why the model ends the run now. `message` delivers the
actual answer/result, a concise explanation and any unfinished work or verification
limits, in the user's language. A simple direct answer may be one sentence; no
fixed Markdown headings are required. Necessary questions use `execute` with
`common.questions_ask`. Continue feasible authorized work before finishing.
The question/answer distinction is a semantic model instruction; punctuation is
not an admission rule (quoted questions and source code can contain `?`).

After accepted calls, a first admitted `finish/partial` receives one bounded
recheck when model iterations and tool steps remain. The draft is not accepted into
history; runtime asks the model to execute feasible authorized work or explain the
concrete obstacle and repeat the honest partial finish. A repeated partial finish
is accepted. Exhausted limits do not replace an available partial answer with a
limit failure. This check does not infer task success or authorize tool effects.

`outcome` is required only on finish and is the **model's claim**, independent of
runtime lifecycle, effect health and task verification. Results assess requested
task outcomes, not a list of incidental successful discovery operations.

| `outcome` | Local consistency rule for `results` |
| --- | --- |
| `completed` | Every reported result is `satisfied`. With no accepted calls, `results=[]` is allowed for a direct answer. |
| `partial` | Some work is achieved and some remains: at least one `partially_satisfied`, or a mixture of `satisfied` and `unverified`/`not_satisfied`. |
| `unable_to_complete` | No result is `satisfied` or `partially_satisfied`; explain the obstacle. With no accepted calls, an empty results array is allowed. |

```json
{"action":"finish","reason":"The requested data and chart were produced and checked.","outcome":"completed","results":[{"criterion":"Data and chart created.","evidence":["E7","E12"],"assessment":"satisfied"}],"message":"The data and chart are ready. The saved values and chart series were checked."}
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
required `response` property; the union of currently available actions lives inside it.
Strict parsing requires the wrapper and unwraps it exactly once. Accepted history
keeps the inner v8 decision; raw traffic diagnostics keep the actual transport.
`json_object` uses the unwrapped decision. A configured, explicit provider fallback
switches both parsing and prompt guidance to that unwrapped transport. Neither mode
bypasses local parser/evidence/kernel validation; duplicate evidence IDs are checked
locally rather than through an unsupported `uniqueItems` keyword.

JSON-object prompts include short shape examples for available actions and exact argument schemas.
Strict prompts retain action/evidence/permission semantics without repeating the
format examples or tool argument schemas. Argument descriptions stay in the supplied
schema; schema types alone do not explain their meaning. Both full and projected
prompts belong to the same frozen request, including repair and fallback.
Strict admission counts the projected wire prompt and schema at compilation and
dispatch. A fallback separately checks the retained full prompt before sending
`json_object`; an invalid strict schema is reported as a provider error rather
than treated as lack of format support. No-call accepted history rejects stray
tool transport metadata and a second reason outside its v8 envelope.

## Typed references (revision 3)

`T1`, `T2`, … identify accepted calls. ChatStore allocates `ToolCallReference`
atomically before dispatch, alongside canonical IDs/origins. `E1`, `E2`, … identify
eligible terminal results: ChatStore allocates `EvidenceReference` only on
`ToolExecutionFinished`, from the captured trusted `ToolPolicy.EvidenceEligibility`.
Pending confirmation never issues E; failed/unknown results may issue E for honest
limitations. `E` means citable, not successful. Capability search/schema read, skill
loading and resource discovery/search/outline use `None`; ordinary reads/mutations
use `Eligible`. This policy participates in snapshot hashing and drift checks.

T/E counters are independent, chat-wide replay projections; restart, confirmation,
message removal and compaction never reuse them. Fork copies both high waters and
bindings; canonical IDs/events/CAS are unchanged. Runtime emits uppercase prefixes
and `@v`; only reference lookups normalize case, never target/path/source strings.

Raw execute remains `{name, arguments}`. Fallback history adds `id:"T1"` to calls
and `tool_call_id:"T1"` to result/receipt JSON. Native history rewrites only the wire
pair to `assistant.tool_calls[].id == tool.tool_call_id == "T1"`. It has no
`EXECUTION_REFERENCE` narration or duplicate `content.tool_call_id`. Eligible
result content includes `evidence:"E1"`; service-only results omit it. Original
accepted response bodies and runtime call IDs remain unchanged.

Output response format and accepted history are independent. Both `json_object`
and `json_schema` generate the V8 decision in response `content`; either can use
native history after runtime acceptance. Agent settings expose the history format
separately, with user/developer result roles available for JSON history only.
The native-history instruction describes `assistant.tool_calls` and matching tool
messages; fallback-only JSON ID/envelope explanations are omitted. Repair and
schema fallback retain this history choice and the frozen accepted messages.

P5 admission resolves selected E against retained same-chat canonical terminal events,
trusted eligibility and the successful request's delivered set. T/R, unknown and
undelivered references fail closed. A `satisfied` claim cannot cite a failed,
unknown or undispatched result, even alongside a successful one. Uncited earlier
errors do not block finish. Unknown-effect inspection/no-replay guards are unchanged.
Earlier runs may be cited, including a bounded compact receipt after a checkpoint.
Summaries and assistant prose never issue evidence. Reference revision remains 3;
older histories are not reinterpreted or rewritten.

`R1` identifies a scoped logical resource; `R1@v2` identifies a registered exact
revision in that resource's observation history. A model-only `ref` is attached
directly to the delivered `core.read` data, or to the structured header
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
groups by exact canonical identity; it never allocates replacement `T`/`E`/`R` names.

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
{"tool_call_id":"T7","evidence":"E3","name":"core.read","status":"ok","message":"Excerpt read.","data":{"ref":"R1@v2","kind":"resource-read","target":"src/example.txt","representation":"text","coverage":{"kind":"line-range","start":10,"end":12,"fields":[]},"text":"first line\nsecond line","complete":false,"selectionComplete":true}}
```

Here `T7` identifies the call, `E3` the eligible result, `R1@v2` the source revision, and `coverage` the
delivered excerpt. The label itself includes neither the range nor the text.

## Admission, persistence and presentation

Kernel accepts `execute` calls before dispatch and pins their runtime ID,
step and model-attempt origin. Reflection is persisted as a no-call accepted
decision. Settings → Agent → **Глубина рефлексии** (`MaxConsecutiveReflections`,
CLI `--max-reflections`) accepts 0–20, default 2. With 0, `json_schema` excludes the
`reflection` branch and `decision` field: only `execute` and `finish` remain (only
`finish` if no tools are callable). Both transports remove reflection instructions
and examples and explicitly state that it is disabled. This projection also applies
to format repair, explicit `json_object` fallback and context budget/Inspector.
`json_object` constrains JSON syntax, not action selection. If a provider nevertheless
returns a reflection, the runtime fails with `ReflectionDisabled` immediately,
without a correction request. Existing accepted reflection history is unchanged.
Fresh input or tool results cannot enable reflection at 0; save a different setting
before starting a new run to enable it again.
The limit is frozen for the run, including restart/confirmation. With a positive
limit, only accepted reflection decisions increment the consecutive counter;
terminal tool results and fresh user input reset that counter. `REFLECTION_BUDGET`
tells the model the remaining allowance before every step; at zero remaining it
requests `execute` or an honest `finish`. An additional valid reflection
is rejected with one semantic correction opportunity; another reflection then ends
with `reflection_limit`. Format repair, provider retries and schema fallback do not
spend this allowance or that semantic opportunity. The separate overall iteration
limit still bounds model steps; no extra step is granted beyond it. Confirmation, restart and continuation keep
the same run ID, limits and accepted-call identities; a confirmation cannot
claim a continuation under a new run ID.

Before accepting `finish`, the run store resolves selected E aliases from this
session against references actually delivered in the successful model request.
The existing disposable SQLite projection supplies canonical event locators;
admission rereads the selected accepted/start/terminal JSONL records and checks
their identities, ordering, trusted eligibility and retained history membership.
The exact accepted body is hydrated only for cited calls. Current-run accepted
IDs must still agree with kernel state and have terminal facts; old errors and
uncited payloads are not revalidated on every finish. A fork's canonical seed owns
copied bindings. Pending, removed, foreign and invented evidence is
rejected. Failed/unknown outcomes can support honest unsatisfied or
unverified claims. An `ok` result or a macro dispatch alone does not prove a
user-requested effect. Existing effect read-back, unknown-effect guards,
workspace acceptance and no-replay rules still apply.

The index follows validated append/suffix replay and can be rebuilt from JSONL;
it is not outcome authority or another durable store. A stale cursor, incomplete
tail or invalid selected canonical body fails closed. Correction hints are bounded
to 16 delivered aliases. Pure service work can finish with no evidence; a run with
eligible terminal work still requires a result and at least one delivered E.
The read unit is a canonical JSONL record: a fork seed may contain many historical
headers, but only selected positions are restored as admission facts.

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
For a finish with result claims, the shared Office/Workspace final renderer also
shows the runtime count of actions with a confirmed change (including zero after
read-only work), verified no-ops, failed calls and unknown effects. A direct information answer
with no result claims gets no effect row. These counts describe tool effects, not
independent verification of the user's whole task.
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
