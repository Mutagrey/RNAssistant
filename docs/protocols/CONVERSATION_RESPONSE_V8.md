# Conversation Response v8

Status: **current production contract**. Host-neutral integration is implemented; full Harness, real-model and Windows/Office/WebView2 qualification remain open.
Response protocol is 8, reference contract revision is 4, new chat stream format
is 8, and built-in prompt schema is 69. Formats 6 and 7 remain read-only history;
intermediate 9/10 candidates were never deployed and have no compatibility path.
Earlier format-8 histories with missing/older ReferenceContractVersion are read-only;
new chats persist revision 4. Incompatible continuations are not hydrated or resumed;
no automatic rewrite/deletion or execution upgrade. See [ADR-0018](../decisions/ADR-0018-typed-model-references.md).

## Model envelope

In `json_object` mode the model returns one raw JSON object with fields in this order: `action`,
`reason`, action-specific fields, `message` last if present. `reason` is a short decision
basis, not private reasoning. `message` is optional for `execute` and `reflection`; omission is preferred for
routine steps. If supplied it is a string (including `""`), never null.
`finish.message` must contain a nonblank answer.
No Markdown, duplicate keys, model-owned IDs or extra fields are accepted.

Prompt schema 69 shares the fresh-call rule between object output, strict-schema
instructions/field descriptions and format repair: only `name` and object
`arguments` belong to a new call. Historical `id`, `tool_call_id`, `type` and
`function` are runtime/transport fields, not response examples to copy. This
restriction is at the call level: tool arguments such as `core.tools.read`'s
`arguments.id` remain required by their own schema. The JSON profile retains
the original call shape in accepted history; runtime IDs remain internal.

Prose guidance is shared by object-mode instructions and strict-schema field
descriptions (`ModelProtocolWire` owns it):

| Field | Writing rule | Runtime constraint |
| --- | --- | --- |
| `reason` | One short sentence explaining this action; no extended analysis, plan, logs or copied arguments. | Nonblank string. |
| `decision` | Free-form reasoning summary, usually a few short sentences or bullets: relevant facts/uncertainty, alternatives when useful, chosen direction and check. No mandatory outline or hard length limit; may revise overall strategy. | Nonblank string, reflection only. |
| `results[].criterion` | Short statement of one original user requirement. | Nonblank string, finish only. |
| `execute/reflection.message` | Omit by default, including the first action. Use one or two short sentences for important findings, changed approach, blockers or a meaningful result; routine actions already appear in UI. | Optional string; may be empty, never null. |
| `finish.message` | Useful final answer, explanation and limitations, proportional to the user's request. | Nonblank string. |

These are writing instructions, not hard character limits: schema and parser do
not impose `maxLength` or truncate accepted strings. Overall response output is
bounded by the configured token budget. Required source/code in tool arguments and
requested full deliverables are not shortened to satisfy prose guidance. Runtime
still validates action-specific fields, evidence, batching and outcomes independently.

| Action | Required action-specific field | Meaning |
| --- | --- | --- |
| `execute` | Nonempty `tool_calls` | Propose one or more validated calls. Each call has exactly `name` and root object `arguments`; runtime assigns IDs. Only independent local reads may batch. |
| `reflection` | Nonblank `decision` | Record a useful interpretation and request another bounded model step. No tool dispatch, result or new authority. |
| `finish` | `outcome`, `results` array, nonblank `message` | Assert the final answer and assessed task outcome. No questions, approval requests, offers to continue or promises of future work. |

The API message's `content` is a JSON string containing this envelope. After one
decode, `tool_calls` must be an array of objects and each `arguments` an object;
JSON-encoded calls or arguments are not accepted. Production history keeps this
same call shape; native generation/history variants exist only in the laboratory. The parser validates new content before
any accepted-history projection. Rejection traces preserve that content exactly.
`invalid_tool_call` diagnostics identify the call index (including `response.` in
schema mode), unexpected field names or the actual JSON type. Names are bounded
and escaped; argument values are omitted. Repair receives the same diagnosis.
The shared run-journal viewer initially expands the first rejected call, leaving
argument bodies collapsed so the shape is visible in a screen photo.

The JSON service message `type:format_repair` carries the exact error and only the latest rejected response's
content as quoted diagnostic data, its original response format and the native-call
presence flag. The quoted content is bounded to 4096 UTF-8 bytes including JSON
escaping; `content_truncated` explicitly marks a prefix. A cut-off field is not
proof of its absence. Error text is bounded to 2048 quoted bytes with an explicit
`error_truncated` flag. The repair identifies this unaccepted attempt, not the
preceding accepted assistant/tool exchange, and asks for a full corrected response.
Missing nonfinal `message` is valid and does not trigger repair. Decision fields
such as `reason`/`decision` remain in the bounded quoted rejected content. Provider-private reasoning is not copied.
Rejected drafts never become accepted assistant history or executable native calls;
older repair diagnostics are replaced, not accumulated. Schema guidance
projects only the owned instruction, leaving quoted rejected content unchanged.
The initial request reserves the bounded diagnostic cost before dispatch for both
response transports. A short shared repair contract refers to the complete frozen
instructions and schemas; it does not duplicate the entire main protocol prompt.

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
`json_object` uses the unwrapped decision and is the default. Unsupported
`json_schema` stops with a provider error; the user selects another format explicitly. Neither mode
bypasses local parser/evidence/kernel validation; duplicate evidence IDs are checked
locally rather than through an unsupported `uniqueItems` keyword.

JSON-object prompts include short shape examples for available actions and exact argument schemas.
Strict prompts retain action/evidence/permission semantics without repeating the
format examples or tool argument schemas. Argument descriptions stay in the supplied
schema; schema types alone do not explain their meaning. Both full and projected
prompts belong to the same frozen request, including repair.
Strict admission counts the projected wire prompt and schema at compilation and
dispatch. An invalid or unsupported strict schema is a provider error; no fallback
changes the format during a run. No-call accepted history rejects stray
tool transport metadata and a second reason outside its v8 envelope.

## Typed references (revision 4)

`T1`, `T2`, … identify accepted calls internally (journal/Inspector only). ChatStore allocates `ToolCallReference`
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

Production uses one JSON history profile. Both response formats produce the same
accepted inner decision. The final projection restores the original ordered batch
from `AcceptedToolCallOrigin` (step, attempt, call index, exact call count), then
emits one `assistant.content` decision and one `user.content` object:

```json
{"type":"tool_results","results":[{"call_index":0,"name":"core.read","status":"ok","message":"Excerpt read.","data":{"text":"observed source"},"evidence":"E1"}]}
```

`call_index` is zero-based within the preceding accepted batch. Runtime joins by
canonical IDs before assigning the index; arrival order cannot change pairing.
Model calls have only `name` and object `arguments`, exactly as in the fresh
response. No synthetic native fields, call IDs or `TOOL_RESULT` prefixes are added.
`reason`, `decision`, optional `message`, finish claims and literal arguments retain
their semantics. E references remain citable; internal T/UUID values stay out of
owned model-facing correlation fields. Business strings are never globally rewritten.

Durable events remain per-call and append-only. The final wire projection is not a
second store. Missing/duplicate results fail closed. If older batch siblings were
compacted, retained operations become explicit `type:completed_operation` receipts
with `batch_compacted:true`, original index, call and result; runtime does not invent
a smaller assistant decision. Full latest batches are retained together. Inspector
keeps source-message links for grouped exchanges and the exact serialized request.

Service data is typed JSON (`tool_results`, `format_repair`, `tool_pack_state`,
`decision_rejected`, `resource_observation`, `completed_operation`, `context_summary`, `context_claims`). Tool data and
quoted rejected content are not instructions. `format_repair` and `decision_rejected` request a specific correction;
quoted rejected content and tool data remain data. `format_repair.dispatched:false`
identifies an unaccepted attempt and never refers to the previous valid result.
The default is three response attempts including the first; two consecutive exact
repetitions of the same invalid response stop early. Network recovery is separate.
An intervening truncated response breaks that sequence and retains its own bounded
output-limit correction. `completionFeedback` inside runtime context is a JSON
object in both Office and Workspace, never a JSON-encoded string. The request cache
compares its value and freezes a detached copy.

### Runtime presentation and message roles — 2026-11-02

At the user's request, the runtime context uses the earlier Markdown/YAML presentation:
`# RUNTIME_CONTEXT`, Environment metadata, Available now tools, a filtered Load on
demand catalog and Callable tool definitions. JSON-object mode supplies exact JSON
argument schemas there; strict mode replaces only that section with the location of
the supplied response schema. Accepted JSON history and typed repair/results do not change.
Nested feedback stays a JSON object value on its YAML line, without double encoding.

| Message | API role / ownership |
| --- | --- |
| Base instructions, runtime context, selected skill | Configured instruction role; developer by default |
| Actual user input | user; preserved as supplied |
| Accepted execute/reflection/finish | assistant; model-authored JSON decision |
| tool_results, completed_operation, resource_observation | user; runtime-supplied data, not new human requests |
| format_repair, tool_pack_state | user; explicit runtime correction/admission |
| context_summary, context_claims | user; historical reference data; individual claims retain their original sourceRoles |

Verified after-source must never be presented as a model-authored assistant message.
Compaction preserves its required summary and original claim provenance. UI-only
activities remain excluded from the request. Native tools remain a separate diagnostic
profile, not a reinterpretation of these production roles.

Agent/Workspace share task-planning and task-approach guidance: bounded discovery,
an outcome-based Task List for dependent stages, updates from actual results, and
proportionate verification. Simple tasks skip tracking. Reflection is optional and
can synthesize observations, compare approaches or revise the overall plan; it has
no mandatory internal template and does not replace execution or evidence.

Every **new** execute proposal must still include nonempty `tool_calls` inside
its decision JSON in content (inside `response` in schema mode). An `action/reason/message` object without calls is invalid; runtime never removes
calls from accepted decision content.
Object/schema instructions, schema description and format repair share this rule.
`empty_execute_calls` never borrows a call from history, accepts a narration as
execution or silently converts provider-native calls into a V8 proposal.
JSON/SSE captures retain `NativeToolCallsObserved` even outside diagnostic native
assembly; when true, the missing-call repair explains that calls arrived in the
wrong field. This presence flag neither validates nor authorizes those calls.

P5 admission resolves selected E against retained same-chat canonical terminal events,
trusted eligibility and the successful request's delivered set. T/R, unknown and
undelivered references fail closed. A `satisfied` claim cannot cite a failed,
unknown or undispatched result, even alongside a successful one. Uncited earlier
errors do not block finish. Unknown-effect inspection/no-replay guards are unchanged.
Earlier runs may be cited, including a bounded compact receipt after a checkpoint.
Summaries and assistant prose never issue evidence. Reference revision is 4;
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
Repair retains the same aliases and reports the actual delivered set.

Discovery/search/outline remain navigation results with semantic targets and bounded
snippets/coordinates. Their current durable contracts do not map each visible item
to exact scoped evidence, so they receive no inferred `R` labels. Mutation/omitted-body
receipts expose `E` and outcome; they do not advertise the observed source as delivered.
Saved result bodies, CAS, event bindings, diagnostic resource rows and chat presentation
retain their existing contracts. The saved final model request contains the projected
`ref` exactly as sent. This projection change stays within format 8.

Example model-facing result (line-range end is exclusive):

```json
{"call_index":0,"evidence":"E3","name":"core.read","status":"ok","message":"Excerpt read.","data":{"ref":"R1@v2","kind":"resource-read","target":"src/example.txt","representation":"text","coverage":{"kind":"line-range","start":10,"end":12,"fields":[]},"text":"first line\nsecond line","complete":false,"selectionComplete":true}}
```

Here `call_index:0` identifies the call within its batch, `E3` the eligible result, `R1@v2` the source revision, and `coverage` the
delivered excerpt. The label itself includes neither the range nor the text.

## Admission, persistence and presentation

Kernel accepts `execute` calls before dispatch and pins their runtime ID,
step and model-attempt origin. Reflection is persisted as a no-call accepted
decision. Settings → Agent → **Глубина рефлексии** (`MaxConsecutiveReflections`,
CLI `--max-reflections`) accepts 0–20, default 2. With 0, `json_schema` excludes the
`reflection` branch and `decision` field: only `execute` and `finish` remain (only
`finish` if no tools are callable). Both transports remove reflection instructions
and examples and explicitly state that it is disabled. This projection also applies
to format repair and context budget/Inspector.
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
with `reflection_limit`. Format repair and provider retries do not
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
metadata. Production JSON history rejects native call metadata. Internal results use typed
Tool Result v2 records with user transport; the final request batches them as above. Workspace replay checks adjacent batch
siblings against the same run, step and model attempt with consecutive indexes;
an index reset cannot start a second batch before the preceding results.
Reference-contract revisions before 4 and incompatible format-8 histories cannot continue;
old chats remain readable, without automatic conversion or deletion. An absent
legacy `AcceptedToolCallOrigin.CallCount` remains unknown during deserialization;
it is never guessed as one. Current execution and batch projection require the
recorded count, including externally stored calls. No legacy inference,
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

## JSON-profile implementation checklist — 2026-11-02

This checks the latest approved JSON-profile plan; it does not close the older
diagnostics proposal's full roadmap or real-endpoint qualification.

| Plan item | Implementation / verification boundary |
| --- | --- |
| One production history profile | Accepted JSON decisions and user-role result batches; synthetic native history removed. |
| Explicit output transport | `json_object` default; `json_schema` selected explicitly; unsupported schema stops without fallback. |
| Optional nonfinal message | Parser, schema, accepted history and preview agree; finish still requires an answer. |
| Runtime identities | Canonical IDs/T stay internal; fresh and historical calls contain only name/arguments. |
| Batch pairing and compaction | Original order/count restored; results use call_index; partial old batches become explicit receipts. |
| Structured correction | Typed format repair quotes bounded rejected content; private provider reasoning stays out; runtime feedback is an object. |
| Bounded retries | Default three responses, consecutive identical rejection stop, separate provider and truncation budgets. |
| Diagnostic verdicts | Content, ID visibility, pairing, schema and two-exchange native cycle are separate; scripted transport only. |
| Qwen rendering | Pinned offline template and declared local adapter; no inference or deployed-template claim. |
| Settings and preview | Shared form/viewer and acknowledged next-run save; obsolete controls removed. |
| Migration | Reference revision 4/result v2; old history remains readable, cannot execute, and is never rewritten. |
| Qualification | Targeted host-neutral regression checks; live models, Windows/Office/WebView2 and the full Harness remain open. |
