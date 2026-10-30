# Model endpoint compatibility

RNAssistant uses an OpenAI-compatible Chat Completions endpoint.

## Response format and accepted history — 2026-10-30

Agent settings expose two independent choices: **Формат нового ответа**
(`json_object` or `json_schema`) and **Формат истории** (JSON in content or
Native tools). The model still generates the V8 decision as JSON in `content`.
After acceptance, native history serializes execute calls as `assistant.tool_calls`
and matching `role: tool` results, for either response format. Assistant `content`
retains the accepted decision as JSON: `action:execute`, `reason`, `message`.
Only the calls move to the native field; they are not duplicated inside content.
Finish keeps `action`, `reason`, `outcome`, `results`, `message`; reflection keeps
`action`, `reason`, `decision`, `message`, including across user turns.
The final wire projection builds this from canonical accepted facts; it does not
rewrite stored events or parse the projected partial execute as a new response.
Native function generation by the provider is a separate capability and is not required here.

JSON history uses accepted execute JSON in assistant content and `TOOL_RESULT`
messages with a user/developer role. Native history shows the fixed `tool` role;
JSON history lets the user choose user/developer. The form maps these combinations to the existing typed
`ToolResultRole`: tool for native, user/developer for JSON. There is no duplicate
persisted switch that can disagree with the transport, and existing settings keep
their behavior on reopening. Response-format fallback does not change history.

| New content response | Accepted call history | Result message |
|---|---|---|
| `json_object` or `json_schema` | JSON in `assistant.content` | `user` + `TOOL_RESULT` |
| `json_object` or `json_schema` | JSON in `assistant.content` | `developer` + `TOOL_RESULT` |
| `json_object` or `json_schema` | Decision in content + native `assistant.tool_calls` | `tool` paired by `tool_call_id` |

An unpaired `role:tool` after a call existing only inside a content string is not
an additional portable mode. OpenAI's [Chat Completions tool message contract](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)
requires the corresponding call ID; the [function-calling flow](https://developers.openai.com/api/docs/guides/function-calling)
appends the assistant call before its tool result. [Ollama's tool-calling examples](https://docs.ollama.com/capabilities/tool-calling)
also retain assistant `tool_calls` before tool messages. Endpoint-specific acceptance
and model behavior require separate qualification; merely selecting a role cannot
establish either. No extra persisted flag or duplicate native/content calls is added.

Prompt schema 64 projects only the selected history guidance into the owned Agent
Contract. Native history omits the fallback `TOOL_RESULT`, `tool_call_id` and
runtime-added JSON `id` explanation; output V8 fields remain required. The frozen
source, user content, accepted calls and loaded skills are unchanged. Strict
output removes duplicate shape examples; object fallback restores the object
contract while keeping the same history guidance. Harness covers 36 combinations
of response format, history role, Office instruction role and reflection on/off,
plus repair/explicit fallback across 18 settings combinations. This wire-level
matrix does not remove the existing Workspace restriction to system/developer
instructions. The shipped form checks selection, matching examples and save/reopen
without calling an endpoint.
New execute decisions must include nonempty `tool_calls` in their content JSON;
the historical projection is explicitly not a template for a fresh response.
The photo-shaped `action/reason/message` reply is checked with JSON/SSE, both
response modes and with/without separate provider-native calls. Native presence
is captured as `NativeToolCallsObserved`; it does not enable native dispatch.
Reason/decision/criterion brevity rules are identical in object instructions and
strict field descriptions; they do not introduce truncation or a character limit.

The expandable example shows a fresh model response and the next request's
accepted history through the shared JSON viewer. It follows unsaved controls and
labels them as a draft; it is not a captured request or endpoint probe.
Successful saves apply to the next run, including reprojecting earlier accepted
history. Sent requests and stored Inspector snapshots remain unchanged. The
synthetic save → run check reuses one SettingsService, executor, conversation
service and chat across four changes. In-flight control edits survive a save
response and tab navigation; failed saves remain visible and do not update the
acknowledged settings. The user's Windows restart symptom is not yet reproduced.

A retained native call has `content` equivalent to
`{"action":"execute","reason":"Inspect the requested module.","message":"Reading Module1."}`
and a separate `tool_calls:[{"id":"T1","type":"function","function":{"name":"core.read","arguments":"..."}}]`.
Finish and reflection stay complete JSON decisions without native calls.
Budget folding may replace an older call/result with an explicitly labelled
assistant update and operation receipt; it never presents that receipt as a full
original decision. Selected full decisions use the shapes above.

## Current P2 contract — 2026-10-30

Reference revision 3 separates T calls from eligible E results, accepts case-insensitive
reference lookup and uses `finish.results[].criterion`. Native pairing uses T in the
outer protocol fields; content has no duplicate call ID or EXECUTION_REFERENCE.
New chats persist the revision; earlier chats remain read-only. Default result role
and endpoint settings are unchanged. A4 now checks criterion; scenario revision 3 and
CLI `TargetCriterion` reflect its actual assertion verdict. All current validation is
synthetic. Real endpoint and Windows/Office/WebView2 qualification remain deferred.
Earlier dated baselines below describe their own revisions, not the current wire.

## Current P3 composition — 2026-10-30

Prompt schema 60 uses separate Agent Contract and Runtime Context messages, shared
Office/Workspace Markdown/YAML rendering, explicit callable/on-demand capability
lists and exact JSON schema fences. Strict transport removes prompt schemas only
on exact equality; repair and explicit object fallback preserve the frozen source.
Selected skill reads become causal receipts only when the full exact publication
is already included. Current policy, confirmation and unknown-effect gates remain.

Synthetic CLI capture `/tmp/rna-v8-p3-skills-20261030-3/manifest.json` contains
20 conversation requests, nine selected-skill appearances and **zero** duplicate
skill source bodies. It covers guarded changes, approval, follow-up, policy suspension,
removal, whole-request refusal and cold restart. The manifest records revision 3,
`criterion`, a dirty working tree and the exact CLI hash; it is not release evidence.
The CLI transport matrix also passed object/schema fallback, all result roles and
approval inheritance. Targeted Harness checks cover schema equality/drift, repair,
selected-body coverage/provenance, budgets and Inspector. No real endpoint was called.

See [P3 contract](conversation-protocol.md#p3-request-composition--2026-10-30).
Historical finish citations are implemented in P5 below; full Inspector and
real-machine qualification (P6) remain open.

## Current P4 materialization — 2026-10-30

Read receipts now stay inside their original call/result pairs. Deduplication checks
exact scope/resource/version/view/coverage/completeness and body bytes; final-budget
omission removes dangling `retainedAt` pointers from wire and receipt. Active bodies
suppress redundant current views; retained CAS restores an absent current source
after compaction. [Canonical P4 contract](conversation-protocol.md#p4-chronological-bodies--2026-10-30).

Host-neutral coverage includes all three result roles, full/partial/table/empty reads,
different bodies/scopes/resources/views/ranges, original metadata, archived body
hash comparison and budget eviction. The continuity checks, reference projection,
selected skills, current-source restoration and oversized-source guards passed.
Scripted CLI captures cover read/repeat/patch/read across user turns (7 requests),
two patches (5), failed-patch correction (5) and compaction followed by a guarded
write without rereading (7): **24 conversation HTTP requests** in total.
The omitted-read guard also rejects an overwrite and preserves original bytes.
Captures are local `/tmp/rna-v8-p4-*-20261030` artifacts with exact bytes/manifests;
they are synthetic, reproducible diagnostics, not durable release evidence.
No real models or Windows/Office validation ran; default result role is unchanged.

## Current P5 admission — 2026-10-30

Prompt schema 61 allows delivered historical E from retained same-chat canonical
terminal facts. Current reference revision stays 3. The existing SQLite projection
indexes canonical JSONL locations; selected accepted bodies are checked on citation.
The compiler retains bounded historical receipts after compaction and normalizes
selected hydrated history to the current tool/user/developer transport. The eager
Workspace archive-normalization path is removed.

Host-neutral Harness covers multi-run delivery, failed/unknown/service-only results,
restart/fork, removed or foreign history, case normalization, pending confirmation,
missing selected versus unrelated CAS and index rebuild. Context checks include
the 3×3 stored/request role matrix, exact source retention, compaction and aggregate
budget behavior. Existing SQLite paging/lazy-start/suffix checks and unknown-effect
no-replay check pass. [Context audit](audits/office-workspace-preparation-audit.md#сборка-контекста-и-стоимость-шага--2026-10-30)
documents remaining history scans and live-file hashing costs.

`v8_context_baseline.py --case historical-finish` captures two user turns: the
second finishes with a prior-run E and **zero** new tool steps. Both native tool and
fallback user transport pass (5 conversation requests each), plus guarded read
restoration after compaction (7). The raw second response must cite `E3`; an empty
`results` array cannot pass this fixture. Historical captures are
`/tmp/rna-v8-p5-e3-{tool,user}-20261030`; the final compaction capture is
`/tmp/rna-v8-p5-final-compaction-20261030`. Their manifests retain the
exact CLI hash, dirty-tree flag and request/response bytes; these are synthetic
captures, not real-model or Office qualification. Defaults are unchanged.

## Required transport

- `POST /v1/chat/completions`, or a `BaseUrl` already ending in `/chat/completions`;
- request fields `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream`;
- non-stream `choices[0].message` or SSE `choices[0].delta`;
- `user` and `assistant` roles;
- the configured Agent instruction role: `developer` by default, optionally `system` or `user`;
- the selected Agent response format: `json_object` by default or strict `json_schema`;
- the selected tool-result role: `user` by default, optionally `developer` or a matched `assistant.tool_calls` → `tool` pair.

Current runs use [Conversation Response v8](protocols/CONVERSATION_RESPONSE_V8.md):
`action`, `reason`, action-specific `tool_calls`, `decision` or `results`, and
`message`. `execute` proposes calls without model-owned IDs; `reflection` makes no
tool call; `finish` reports assessments rather than verified effects. In
`json_object` mode this is the root object. Strict `json_schema` mode requires a
closed root object with one `response` property containing the decision. The two
modes share local parsing, evidence admission and runtime-owned call IDs. Plan
receives only its scoped planning tools; Agent receives its admitted catalog.
Response format and tool-result role are explicit settings. Configured
`json_schema` fallback applies only to an explicit format-support rejection; an
invalid provider schema is a typed failure, not evidence that fallback is safe.

Settings → Agent → «Запустить тест» checks three exact sentinels using the currently selected instruction role, response format, and result role: `ROLE_OK`, a v8 `execute` proposal for `compat.echo` with exact name/arguments but no model-owned ID, and a v8 `finish` with `RESULT_OK` after the chosen result transport. Strict-mode probes require the `response` wrapper. The probes do not execute Office actions.

These sentinels repeat the expected answer in the instructions. They check the
selected response contract, not native history visibility, multi-call pairing or
native tool generation. HTTP acceptance alone does not establish those capabilities.

## V8 diagnostics first slice — 2026-10-30

Owner: Core LLM (`ModelDiagnosticScenarios`, `ModelDiagnosticRunner`), consumed by
the CLI. This starts [P0/P1](proposals/RNAssistant_V8_Context_Protocol_Target.md)
and [D0/D1](proposals/RNAssistant_V8_Diagnostics_Qualification_Plan.md).
There is no new agent loop, tool dispatcher or event store. Only synthetic
messages and markers are sent; no workspace or Office document is opened.

```sh
dotnet run --project src/RNAssistant.Cli -- diagnostics \
  --base-url http://127.0.0.1:11434 --model gemma4:31b-cloud \
  --output /tmp/rna-diagnostics-new --seed my-probe --repeat 2
```

`RNA_BASE_URL`, `RNA_MODEL` and `RNA_API_KEY` are supported; authentication is not
written to the report. Use `--cases B5,B6` for a subset and `--timeout 30` for the
per-case deadline. `--synthetic` explicitly labels a scripted endpoint; otherwise
the execution kind is `RealEndpoint`. Neither label implies a successful check.
An existing output directory is rejected before dispatch. Seeds reproduce marker
generation; each repetition derives new markers and call/result order. Calls and
results are oppositely ordered in B5. History visibility/pairing probes put the
expected marker only in result data. Text/format probes intentionally request an
exact sentinel and do not establish history visibility. B6 omits ModuleB's result; rejection of this intentionally incomplete
pair is `Inconclusive`, not proof that the model can detect missing data.

Scenario/report revision 2 implements 23 A/B/C cases through one runner:

| Cases | Assertion |
| --- | --- |
| A1 | Exact text response to a user message, without response_format |
| A2, A2-system | Marker supplied only in developer/system, not repeated in user |
| A3 | Exact V8 execute; existing callable schema and production parser |
| A4 | Current V8 finish with `summary`, `outcome`, assessment and no invented evidence |
| B1–B7 | Null/empty/omitted native content, visibility, shuffled pairing, missing-result control and user decoy |
| B8 | Actual native function call with exact name/arguments and a provider ID; no dispatch |
| B9 | Latest result across synthetic multi-user/run history with distinct paired IDs |
| C1, C2 | Direct V8 json_object and exact production json_schema with the response wrapper |
| C3-valid, C3, C3-text | Minimal valid schema, deliberately invalid schema, explicit text JSON without response_format |
| C4-control, C4 | Same V8 response over JSON and real SSE; compare parsed results |
| C5, C6 | User/developer TOOL_RESULT fallback visibility |

Selecting A2 also includes A2-system; C3 includes C3-valid; C4 includes its non-stream
control. Every repetition uses new markers. The common settings are 512 output
tokens, 8192 context tokens, temperature 0 and reasoning disabled; format and stream
are fixed by each scenario and recorded per result. Production defaults are unchanged.
Native call content variants are request-local and reject nonempty narration.
Diagnostic T/provider IDs carry no runtime authority; no fake function is executed.
B9 tests submitted history, not durable replay/restart (the CLI baseline below covers
that separately). A4 deliberately uses the current reference revision 2: the target
`criterion` contract stays `NotRun` until the coordinated P2 migration.

`Pass` requires the scenario's exact assertion and a captured HTTP exchange.
The existing strict JSON value reader and V8 parser reject malformed JSON, extra
fields, model-owned call IDs, wrong wrappers and contradictory outcome/assessment.
HTTP acceptance, JSON shape, marker, ID, target and V8 parser verdicts are recorded
separately. Native generation is not inferred from an execute JSON in content;
inspection of native calls is opt-in and does not change production dispatch.
Fenced output is not repaired. HTTP 500, auth/network failures, timeouts and malformed
provider envelopes are `Inconclusive`; a normal probe rejected with HTTP 400 fails.
Only the existing typed format-support rejection can produce `Unsupported`.
C3 passes only on a structured schema rejection with HTTP 400/422, never on an
arbitrary error or 500; its Pass means expected rejection, not HTTP acceptance or
general schema support. No retries, protocol repair or automatic format/role fallback
occur. Unrun capabilities remain `NotRun`.

Each case saves the actual post-serialization UTF-8 request, its SHA-256, provider
response text, expected/actual answer, status, assertion verdicts, duration and
token usage. These come from the existing `LlmClient.TraceSink`; invalid HTTP 200
envelopes retain their original body/status as well. For C4, the opt-in stream-body
capture keeps the exact bytes consumed by the existing parser, including malformed
frames, with a 32 MiB cap. `ResponseBodyComplete` is true only after EOF or the entire
declared Content-Length; otherwise the export is explicitly a received prefix.
`ResponseCaptureTruncated` reports capture-budget loss. The capture never drains or
replays the stream, and the reconstructed completion JSON is not exported as raw SSE.
JSON returned for a streaming request fails C4. SSE parity needs both the actual SSE
result and its successful matching non-stream control. No headers are exported.
Known API-key values are redacted, including escaped occurrences; changed exports
are explicitly marked and must not be represented as exact wire. `RequestSha256`
always describes the original bytes. This explicit diagnostic export only contains
synthetic scenario bodies; ordinary sensitive-body logging stays disabled by default.
Native tool schemas are counted in request token estimates. The existing response
parser can inspect bounded indexed native deltas for diagnostics; this is not a new
agent execution path. Reports expose native-generation, V8-parser and SSE-parity
verdicts independently, together with every case's detailed assertions.

Per the user's current instruction, further real-model runs are deferred. Automated
validation uses only synthetic HTTP endpoints; live compatibility will be tested
on the user's real machine. No live model was called for scenario revision 2.

### Diagnostics UI — 2026-10-30

Settings → **Помощь и обслуживание → Протокол модели** exposes the shared A/B/C
runner: a shared navigation tree selects a group, a single scenario or all checks
(20 scenarios and three automatic controls), 1–3 repetitions,
reproducible seed and a 10–60 second per-request timeout. Opening or refreshing
only reads the catalog; model requests require an explicit Run. Stop cancels the
active bridge request and prevents subsequent scenarios. Results stay in panel
memory until another run or panel reload; there is no additional durable store.
The groups explicitly separate content response, instruction role, native history,
JSON history, native generation and SSE. Native generation is not a prerequisite
for native history. Scenario revision 4 makes B4 carry the actual execute decision
JSON in content while calls stay native. C5/C6 use accepted call aliases and the
production `TOOL_RESULT` envelope, without repeating arguments in the result.
The Agent-page compatibility probe now uses the same final history projection too.

`ModelDiagnosticsService` owns the UI use case with typed catalog/run/exchange DTOs.
The endpoint and credentials come from the saved connection; the model can be
overridden for a diagnostic run. The saved endpoint is read-only context, not a
duplicate input. Catalog snapshots refresh when reopening the panel or saving
settings, so a previously loaded endpoint cannot keep breaking new runs after a
connection change. Existing results retain their own run/model/endpoint label. Repeat,
timeout and seed live in the collapsed **Параметры прогона** disclosure. Endpoint
drift is rejected before dispatch. Each
scenario/control group freezes settings and credentials, with 512 output tokens,
8192 context tokens, temperature 0 and reasoning off. Chat settings are not saved
or changed. Both CLI and UI use Core selection/control rules, assertions, SSE parity
and redaction. No synthetic function is dispatched to a real tool or document.

The screen reuses the product editor tabs (wrapping labels at narrow widths),
navigation tree, buttons, form fields, table,
compatibility statuses and disclosures; its CSS owns layout only. The table shows
scenario names, explicit verdicts, HTTP status and duration. The scenario selector
collapses on Run; advanced settings stay collapsed by default. Clicking any part of
a row, or activating its native button with the keyboard, selects the result.
Manual selection stays stable while subsequent results arrive. The details separate
the verdict and readable reason, assertions, expected/actual content, ordered request
messages, raw HTTP request text and received response text. The original request
hash is retained. The UI and its JSON report use previews bounded to 65,536 characters
per text field; truncation, redaction and incomplete SSE capture are distinct and
visible. API key and saved custom-header values are redacted before truncation.
The report does not contain headers. It must not be represented as a complete raw
export when any of those flags is set; the CLI remains the full diagnostic export.
Bridge failures and cancellation are selectable rows with readable errors, never
model-assertion failures. Controls preserve their seed in the report. The malformed
schema case explicitly explains why an HTTP 400/422 can be a passing result.

This is the minimum runnable UI, not the complete D3/D4 laboratory. Custom scenario
editing/import, persistent run history, endpoint comparison, request diff and the
expanded actual-conversation Resources/Evidence/Composition inspector remain open.
T/E/R/criterion now follows P2 reference revision 3; real-model qualification remains NotRun. Host-neutral service/bridge and mocked-browser
checks do not qualify the controller delivery, download or rendering in real Office/WebView2.

### Baseline and remaining qualification

Baseline HEAD: `9e7eccb50fe8e7819a15ff69f3837353c6a69551` on `main`.
`tests/cli/v8_context_baseline.py --output <new-directory> --role tool` uses the
built production CLI against a scripted HTTP endpoint. It captures seven model
requests across read → identical read → patch → read and a second user/run in a
new process, plus the separate title request. It saves exact bytes and a hash
manifest. The 2026-10-30 capture is local at `/tmp/rna-v8-baseline-20261030`;
this temporary directory is not durable release evidence.

The expanded synthetic context baseline supports `--case read-patch|multi-patch|
failure-correction|compaction|skills|all`. For example:

```sh
python3 -B tests/cli/v8_context_baseline.py \
  --output /tmp/rna-context-new --case all --role tool
```

Only disposable local workspaces and a loopback scripted server are used. Compaction
and skill cases reuse `compacted_read_guard_smoke.py` and `skill_packages_smoke.py`;
there is no second context implementation. The skill fixture uses native tool role;
the other cases honor `--role`. Captures include every conversation, compaction and
title HTTP body, hashes, CLI binary hash, HEAD and a dirty-tree flag. The manifest
lists actual roles and delivered source blocks with coverage/body hashes, checks
native pairs and retained-result destinations, and records duplicate selected-skill
bodies separately from selected instructions. These are baseline observations, not
a Pass for the unimplemented target contract.

2026-10-30 capture: `/tmp/rna-v8-context-regression-20261030-r2`, based on `2cb48c28`
plus the uncommitted diagnostics changes. Five fixtures passed: 43 conversation
requests, one compaction request and seven title requests. Exact temporary artifacts
are reproducible; they are not release qualification or model capability evidence.

| D case | Current baseline / remaining target work |
| --- | --- |
| D1–D2 | One complete body after identical read and after patch/read; retained-result references resolve later in the same request |
| D3 | Two consecutive verified patches deliver only OLD → MIDDLE → NEW bodies in order; final read replaces the current view |
| D4 | Existing compiler budget-folding fixture preserves failure details, latest pair and token bound; skill whole-request refusal is captured. Exact HTTP budget-omission baseline remains open |
| D5 | Automatic compaction restores one exact current body; it permits one guarded patch without a redundant read, then a final read |
| D6 | Published selected skill survives approval/restart and oversized selection is rejected. **Confirmed duplicate:** selected instructions also remain in an earlier `core.read` body (requests 02–07 and 09–11); dedup belongs to P3 |
| D7 | Existing durable admission rejects nonterminal, hidden, foreign/guessed references; target eligibility and separate T/E remain NotRun |
| D8 | Two user/run histories survive a new CLI process. Current revision 2 deliberately rejects lowercase aliases; target case-insensitive T/E/R remains NotRun |
| D9 | One failed patch followed by a corrected verified patch finishes Completed; counters retain WriteError=1 and WriteOk=1 |
| D10 | Existing kernel no-replay regression passes after an intervening successful call; exact HTTP unknown-effect/inspection baseline remains open |

Observed in that trace:

- The initial request has developer/user/developer order. The second run retains
  both user messages and prior history.
- Repeated reads leave one source body; older interactions fold in place into
  assistant receipts. The patch creates a current view; the subsequent full read
  replaces that view. This fixture does not reproduce duplicate current bodies.
- Native IDs are canonical `call_*`; result content uses E aliases and native call
  narration adds `EXECUTION_REFERENCE`. The target T/E separation is not implemented.

Existing owners: `ConversationPromptComposer` combines the Office instructions and
JSON runtime context; `ModelContextCompiler` owns folding/materialization/budgets;
`ContextCompactionRuntime` selects history/checkpoints; `ModelRequestPreparation`
and `ModelReferenceProjection` project the wire; `LlmMessageBuilder` and `LlmClient`
serialize/capture/send. Office `ConversationModelSession` and Workspace's model
session share preparation/compiler; CLI consumes Workspace. Inspector reads the
saved trace. `ConversationFinishEvidenceAdmission` still checks the current-run
registry with case-sensitive E lookup and `results[].summary` (reference revision 2).

P0/D0 and D2 remain partial: real Office traces, exact HTTP budget omissions and
unknown-effect/inspection chains are still open. The target skill dedup and
reference/evidence behavior are not claimed as implemented. The A/B/C
headless matrix is implemented for the current contract and verified synthetically;
The remaining Scenario Lab/Inspector work and target reference migration remain separate slices.
Real Office/Windows/WebView2 and revision-2 endpoint qualification are open; only
screen photographs are available from the user's Windows machine.

### Real endpoint probe — Gemma / Ollama

2026-10-30, `http://127.0.0.1:11434`, cloud model `gemma4:31b-cloud`, reported digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`.
Historical scenario revision 1, json_object/non-stream settings; seed `v8-20261030-gemma`, one full suite,
then `v8-pairing-r1`, B5/B6 repeated three times. Every request received HTTP 200
and a raw JSON answer. The repetitions exercised both call orders with reversed
result order. Results are per endpoint/model/configuration, not universal capability
claims about Gemma or native tool transport.

| Cases | Correct marker | Correct call ID | Overall |
| --- | --- | --- | --- |
| B1/B2/B3 null/empty/omitted | 3/3 | 0/3 | Fail |
| B4 tool-only, B7 user decoy | 2/2 | 0/2 | Fail |
| B5 shuffled pairing | 0/4 | 0/4 | Fail |
| B6 missing ModuleB result | null in 1/4 | 0/4 | Fail |
| C5 user result | 1/1 | 1/1 | Pass |
| C6 developer result | 1/1 | 1/1 | Pass |

Thus single-result content was operationally visible, but native ID association
and multi-call pairing were not qualified. This does not establish whether the
proxy changes structure internally. Production role remains the existing explicit
setting (`user` by default). Native generation and V8/schema/SSE compatibility are
not conclusions of this run. A preliminary probe without the explicit no-fences
instruction produced Markdown in all nine cases; those failures were retained,
not normalized or counted as passes.

Local exact exchanges and reports:
`/tmp/rna-v8-diagnostics-gemma-final-20261030` and
`/tmp/rna-v8-diagnostics-pairing-20261030`; preliminary artifacts:
`/tmp/rna-v8-diagnostics-gemma-20261030`. These temporary files are reproducible
diagnostic artifacts, not durable release evidence. Targeted validation: 8
`model diagnostics:` Harness checks, 2 existing `model compatibility:` checks,
production source inclusion and diagnostic CLI export smoke passed. No full Harness,
VSTO/Office validation or Web UI qualification was run.

## Optional capabilities

- Provider token usage; otherwise the UI uses an estimate.
- Model catalog at `GET /v1/models` by default; an absolute or Base-URL-relative override remains configurable.
- SSE streaming.
- Reasoning metadata in supported response fields or one leading `<think>` block. It is stored separately from visible content.
- Model catalog metadata for context/output limits and Vision/Audio support.
- Image, rendered scanned-PDF, and audio content parts when a model is explicitly marked compatible.
- Custom request headers except unsafe transport headers.
- Debug traffic logging without authorization/header values. Message bodies may contain document data, so logging is disabled by default.

The chat model consumes every media modality it declares directly in the normal primary request, without an auxiliary call. Only missing image/scanned-PDF or audio capabilities use isolated request-scoped helper passes. Each helper receives a fixed media-analysis instruction, the current user request, and attachments for its modality; chat history, Office context, tools, and skills are not sent. Its bounded evidence is persisted with the user message and replaces that raw media in the primary prompt. Vision and Audio route independently, so mixed media does not require one helper supporting both. Settings → Model exposes the helper-output and primary-evidence token caps. `0` selects automatic limits (1024 per helper batch; up to 20% of primary input, maximum 2048), while a positive number supplies a custom cap. These controls do not apply when a multimodal chat model receives media directly. A missing required capability fails before the primary call. Endpoint/network failover is not performed.

## Failure behavior

- Invalid conversation JSON in either mode receives bounded clean format repair: `MaxAgentFormatRetries` allows 1–20 total protocol responses including the first (default 10). Rejected raw attempts do not become accepted history; exhausting the limit stops the run.
- `finish_reason=length` permits one shorter complete proposal inside that budget; repeated truncation is a typed failure. A truncated proposal never dispatches tools.
- Typed timeout, network and transient server failures may retry the exact request at most twice per logical step, after 1s and 2s. Authorization, other HTTP errors, 429, invalid provider envelopes and cancellation do not retry. Tools and uncertain effects never replay because a model request was retried.
- An explicit format-support rejection of `json_schema` may switch once to `json_object` when fallback is enabled. Invalid schema and ordinary provider errors do not trigger this fallback.
- Unknown tools and invalid arguments are rejected locally before Office execution.
- A tool failure is returned as typed Tool Result evidence. The model may propose a next action, but runtime guards prevent automatic repetition of an unknown effect.
