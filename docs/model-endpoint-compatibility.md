# Model endpoint compatibility

RNAssistant uses an OpenAI-compatible Chat Completions endpoint.

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
results are oppositely ordered in B5. The expected marker is never placed in the
user prompt. B6 omits ModuleB's result; rejection of this intentionally incomplete
pair is `Inconclusive`, not proof that the model can detect missing data.

Implemented cases: B1 null content, B2 empty content, B3 omitted content, B4 tool-only
marker, B5 shuffled pairing, B6 negative control, B7 user-text decoy, C5 user result
and C6 developer result. All currently use explicit `json_object`, non-streaming,
512 output tokens, 8192 context tokens, temperature 0 and reasoning disabled.
These are diagnostic settings; production defaults are unchanged. Native call
content variants are request-local and reject nonempty narration rather than
discarding it. Diagnostic T IDs are synthetic, not a migration of production E IDs.

`Pass` requires exact JSON target/marker/call ID (using the existing strict JSON
value reader) and a captured successful HTTP
exchange. HTTP acceptance, JSON shape, marker, ID and target assertions are recorded
separately. Invalid JSON leaves semantic assertions untested; fenced output is not
repaired into a pass. HTTP 500, auth/network failures, timeouts and malformed provider
envelopes are `Inconclusive`; a normal probe rejected with HTTP 400 fails. Only the
existing typed format-support rejection can produce `Unsupported`. No retries or
format/role fallback occur. Native generation, V8 parser and SSE remain `NotRun`.

Each case saves the actual post-serialization UTF-8 request, its SHA-256, provider
response text, expected/actual answer, status, assertion verdicts, duration and
token usage. These come from the existing `LlmClient.TraceSink`; invalid HTTP 200
envelopes now retain their original body/status as well. No headers are exported.
Known API-key values are redacted, including escaped occurrences; changed exports
are explicitly marked and must not be represented as exact wire. `RequestSha256`
always describes the original bytes. This explicit diagnostic export only contains
synthetic scenario bodies; ordinary sensitive-body logging stays disabled by default.

### Baseline and remaining qualification

Baseline HEAD: `9e7eccb50fe8e7819a15ff69f3837353c6a69551` on `main`.
`tests/cli/v8_context_baseline.py --output <new-directory> --role tool` uses the
built production CLI against a scripted HTTP endpoint. It captures seven model
requests across read → identical read → patch → read and a second user/run in a
new process, plus the separate title request. It saves exact bytes and a hash
manifest. The 2026-10-30 capture is local at `/tmp/rna-v8-baseline-20261030`;
this temporary directory is not durable release evidence.

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

P0/D0 remains partial: Office exact traces, compaction, multiple patches, skills,
budget omissions and failure/unknown chains need their own baseline. A/C protocol,
schema and SSE probes, native generation, UI Scenario Lab/Inspector and the target
reference migration remain separate slices. Real Office/Windows/WebView2 evidence
is open; only screen photographs are available from the user's Windows machine.

### Real endpoint probe — Gemma / Ollama

2026-10-30, `http://127.0.0.1:11434`, cloud model `gemma4:31b-cloud`, reported digest
`ef09f235533c96cd75e8deed88c628335cb69e2b3ce96275d0d7a67fe9887aba`.
Scenario revision 1, settings above; seed `v8-20261030-gemma`, one full suite,
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
