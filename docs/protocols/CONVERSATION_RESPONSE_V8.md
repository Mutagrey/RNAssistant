# Conversation Response v8

Status: **candidate** in the isolated `conversation-v8` branch. Main remains v7.
Response protocol is 8, new chat stream format is 8, and built-in prompt
schema is 52. Formats 6 and 7 remain readable history; neither gains v8
execution authority or is rewritten/deleted automatically.

## Model envelope

The model returns one raw JSON object with fields in this order: `action`,
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
composition and Workspace/CLI use it. The root `anyOf` schema is a closed
union of the three forms. `json_schema` and `json_object` both receive local
parser and kernel validation; a provider schema is never the authority for
execution. Strict schema rejection may fall back to `json_object` under the
configured policy without accepting a different logical response shape.

## Admission, persistence and presentation

Kernel accepts `execute` calls before dispatch and pins their runtime ID,
step and model-attempt origin. Reflection is persisted as a no-call accepted
decision; at most two consecutive reflections are accepted, then bounded
feedback and a terminal limit apply. Terminal tool results reset the
consecutive reflection counter. Confirmation, restart and continuation keep
the same run limits and accepted-call identities.

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
as model history. Ordinary completed answers need no service card; partial,
unverified and unsatisfied claims receive a separate notice. The run journal
retains the exact accepted wire and tool-result correlation.

These claim/notice presentation rules currently apply to the Office run view.
Workspace project/plugin chat still omits claims and renders empty reflection
messages; the candidate's [open readiness gaps](../stabilization/RISK_REGISTER.md#conversation-v8-readiness-review--2026-10-23)
also record its stricter-than-specified direct-finish admission.

History readers require `ResponseProtocolVersion=8` and exact accepted
metadata. Format-7 chats are read-only in this candidate; no v7 inference,
dual-write or automatic migration is allowed. Windows x64 + Office x64 +
WebView2 and real-model qualification remain open.
