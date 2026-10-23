# Conversation Response v8

Status: **active on `main`**. Host-neutral integration is merged; full Harness, real-model and Windows/Office/WebView2 qualification remain open.
Response protocol is 8, new chat stream format is 8, and built-in prompt
schema is 53. Formats 6 and 7 remain readable history; neither gains v8
execution authority or is rewritten/deleted automatically.

## Model envelope

In `json_object` mode the model returns one raw JSON object with fields in this order: `action`,
`reason`, action-specific fields, `message` last. `reason` is a short decision
basis, not private reasoning. `message` is user-facing text and may be empty.
No Markdown, duplicate keys, model-owned IDs or extra fields are accepted.

| Action | Required action-specific field | Meaning |
| --- | --- | --- |
| `execute` | Nonempty `tool_calls` | Propose one or more validated calls. Each call has exactly `name` and root object `arguments`; runtime assigns IDs. Only independent local reads may batch. |
| `reflection` | Nonblank `decision` | Record a useful interpretation and request another bounded model step. No tool dispatch, result or new authority. |
| `finish` | `results` array | End the run with the answer or honest outcome. No further work is scheduled by a promise in `message`. |

`results[]` entries contain exactly `summary`, `evidence` (runtime
`tool_call_id` strings) and `assessment`: `satisfied`,
`partially_satisfied`, `unverified` or `not_satisfied`. A direct answer
without accepted calls may use `results=[]`. After accepted calls, finish
needs at least one result and one terminal evidence reference in aggregate;
discovery calls need not all be cited.

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
complete validated append-only event stream for this session, run and turn.
Each citation must match an accepted call and its canonical
`tool.execution.finished` fact; pending, naked, foreign and invented IDs are
rejected. Failed/unknown outcomes can support honest unsatisfied or
unverified claims. An `ok` result or a macro dispatch alone does not prove a
user-requested effect. Existing effect read-back, unknown-effect guards,
workspace acceptance and no-replay rules still apply.

`finish accepted`, `RunLifecycle.Completed` and task success are distinct.
`RunViewState.GoalVerification` remains `not_assessed`; model
`results/assessment` are shown as claims with exact evidence IDs, never as
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

Workspace project/plugin chat also presents accepted finish claims and exact
evidence IDs as model assessments. It shows reflection decisions as saved steps;
an empty `message` does not create an answer bubble. A direct finish may include
evidence-free claims when this run accepted no calls; any cited foreign ID is
still rejected. Target WebView2 and full integration qualification remain open.

History readers require `ResponseProtocolVersion=8` and exact accepted
metadata. User/developer history cannot contain native call metadata; native
history has exactly one paired call. Workspace replay checks adjacent batch
siblings against the same run, step and model attempt with consecutive indexes;
an index reset cannot start a second batch before the preceding results.
Format-7 chats are read-only after this cutover; no v7 inference,
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
