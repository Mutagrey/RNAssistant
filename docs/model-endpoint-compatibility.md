# Model Endpoint Compatibility

RNAssistant uses an OpenAI-compatible Chat Completions endpoint. Chat mode expects ordinary assistant text. Agent mode supports three explicit request profiles while all Office actions still execute locally. The complete decision and tool-result contract is in `docs/agent-decision-protocol.md`.

Settings → Agent → «Запустить тест» sends safe, non-Office probes with the real AgentDecision schema and reports support for `user`, `system`, `developer`, tool-result history, one tool decision through `json_object`/`json_schema`, and one native tool call. Multi-tool batching is an optional optimization and is not required for compatibility.

## Required Base Transport

- `POST /v1/chat/completions`, or a `BaseUrl` that already ends with `/chat/completions`.
- JSON request fields `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream`.
- A non-stream response compatible with `choices[0].message`, or SSE chunks compatible with `choices[0].delta`.
- Ordinary `user` and `assistant` history. Depending on settings, the endpoint may also need `developer`, `system`, and `tool` roles.

RNAssistant independently budgets prompt and output tokens so `messages + response_format/tools schemas + response + safety <= context window`. For very large contexts the estimator caps its safety reserve at 16,384 tokens. Provider usage wins when available; otherwise the UI uses a provider-neutral estimate.

## Agent Request Profiles

| Setting | Endpoint features required | Response accepted by RNAssistant |
| --- | --- | --- |
| `json_schema` | `response_format: {type:"json_schema", json_schema:{strict:true,...}}` | Exactly one AgentDecision v1 object in `message.content`. This is the default. |
| `json_object` | `response_format: {type:"json_object"}` and reliable prompt following | Exactly one AgentDecision v1 object in `message.content`; local semantic/schema validation remains strict. |
| `native_tool_calls` | OpenAI `tools`, strict function schemas, `tool_choice`, `parallel_tool_calls:true`, and `json_schema` for non-tool decisions | Either 1–8 `message.tool_calls[]` entries or one terminal/plan AgentDecision v1 object in content. |

The canonical AgentDecision fields are `protocolVersion`, `kind`, `decisionSummary`, `goal`, `plan`, `tool`, and `message`; canonical output includes inactive fields as JSON `null`. For `kind=tool`, `tool` is an array of 1–8 canonical calls. The local parser also normalizes harmless omissions, common plan-title/tool aliases, advisory goal/plan fields, a legacy single tool object, and legacy `toolCalls`. Markdown fences, prose around JSON, content arrays as the root response, unknown root fields, conflicting actions, legacy `function_call`, empty batches, and batches above the limit are rejected. Every normalized tool is validated against the current local tool slice and argument schema; multi-call execution is restricted to independent read-only tools.

If an endpoint explicitly rejects `json_schema` before any tool execution and fallback is enabled, RNAssistant retries via `json_object` and keeps that mode for the rest of the run. Timeouts, network failures, rate limits and server errors are propagated without changing the protocol or duplicating the request. It does not switch after a tool has run and does not auto-fallback from `native_tool_calls`, because replay could duplicate a mutation.

## Tool Result History

The default result role is `tool`. RNAssistant sends one assistant message containing the accepted `tool_calls[]` batch and string `content` (an empty string when there is no native visible text), followed by one `role: tool` message per call with an exact matching `tool_call_id`. `content` is never serialized as JSON `null`, which keeps replay compatible with strict OpenAI-compatible validators. The same shape is generated when the model selected tools through AgentDecision JSON rather than native calling.

Endpoints that reject tool-call history can use `developer` or `user` result role. In that mode RNAssistant sends `TOOL_RESULT:` plus the same normalized JSON envelope as ordinary content. Instruction role is independently selectable as `developer` (default), `system`, or `user`.

## Optional Capabilities

- `usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`; `input_tokens`/`output_tokens` aliases are also accepted.
- Reasoning in `message.reasoning_content` / `message.reasoning`, their SSE delta equivalents, or one leading `<think>...</think>` block. It is separated from normal content and never treated as AgentDecision fields.
- Per-chat reasoning toggle for compatible models. `ReasoningRequestMode` selects the raw Chat Completions JSON shape: OpenAI `reasoning_effort` (`medium`/`none`), Qwen-style `enable_thinking` boolean, vLLM `chat_template_kwargs.enable_thinking` boolean, OpenRouter-style `reasoning.enabled` boolean, or `custom_json`. `auto` uses `reasoning_effort`. In `custom_json`, the object from `ReasoningCustomJson` is merged into the top level of the raw HTTP body only while reasoning is enabled; core request fields such as `model`, `messages`, `tools`, and `response_format` remain protected. Python SDK examples often place non-standard fields in `extra_body`; because RNAssistant owns the raw HTTP body, enter the actual fields that the endpoint expects rather than an SDK `extra_body` wrapper unless the endpoint explicitly requires that wrapper.
- A model catalog endpoint, defaulting to `/config/models.json` derived from `BaseUrl`. RNAssistant prefers OpenAI-style `data[].id`, also understands `models[].value` and root arrays, and accepts catalogs that contain only model IDs. Per-model context/output limits, reasoning support/transport, Vision, Audio, and image count are editable in Settings; saved values are not erased when a later catalog omits or changes capability metadata.
- SSE streaming. RNAssistant detects an SSE body even if a compatible endpoint omits or mislabels `Content-Type`.
- Optional Settings → Service model-traffic debug logging records pretty-printed request JSON and response JSON/SSE chunks with a shared correlation id. It never records the API key or HTTP header values, but request messages may contain document data.
- Custom request headers except unsafe transport headers such as `Content-Length` and `Host`.
- `image_url` content parts for image/scanned-PDF turns and OpenAI-compatible `input_audio` parts for MP3/WAV. The response is still text.

## Attachment Model Routing

- Attachments do not change the stored chat/global model. A compatible model is selected only for the current request.
- Only media from the current user turn is sent as binary input. Historical PDFs retain extracted text; historical image/audio binaries are omitted.
- Images/scanned PDFs require Vision, audio requires Audio, and a mixed turn requires one model that supports every needed modality.
- `AttachmentModelPriority` is evaluated in order. Explicit Vision/Audio overrides beat catalog metadata; unknown capability is not treated as supported.
- RNAssistant does not fail over to another model after an endpoint error.

## Behavior Matrix

| Endpoint behavior | RNAssistant behavior |
| --- | --- |
| Returns ordinary text in Chat mode | Stored as the assistant response; no agent parser or tool execution runs. |
| Returns a valid AgentDecision object | Locally validated; tool metadata, explicit runtime mode, visible plan state and safety gates decide whether it may continue. |
| Returns plain text/fenced/noisy JSON in Agent mode | Rejected; one bounded correction request may be made. |
| Returns one native tool call in native mode | API name maps back to the exact local tool id; arguments are validated before local execution. |
| Returns native tool calls in a JSON-only mode | Rejected because that mode's contract is AgentDecision content. |
| Returns 2–8 independent read-only native calls | Validated, executed locally in order, and replayed as one assistant batch with matching results. |
| Returns a multi-call batch containing mutation/confirmation | Rejected before execution; the model is asked to select those actions one at a time. |
| A later call needs an earlier call result | Must use a later model turn; runtime does not interpolate intermediate batch results into arguments. |
| Rejects strict response schema before tool execution | `json_schema` may fall back once to `json_object`. |
| Rejects `developer` or `role: tool` | Select a supported instruction/result role in Settings; RNAssistant does not guess endpoint semantics. |
| Omits token usage | Completion still works; counters use estimates. |
| Lacks model catalog | Manual model ids still work. |
| Requires a provider-specific tool/media/state protocol | Unsupported; use the compatible profile or a provider adapter in a future change. |

## Recommended Profiles

- Prefer `json_schema + role: tool + developer` when all three are supported.
- Prefer `json_object + role: tool + developer` for local models that accept tool history but not strict Structured Outputs. This is the most flexible prompt-driven harness profile.
- Use `json_object + developer/user tool results` for servers that reject `tool_calls` history entirely.
- Use `native_tool_calls` only after testing both tool calls and terminal structured responses on the exact endpoint/model pair.
