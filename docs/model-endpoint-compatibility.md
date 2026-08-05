# Model Endpoint Compatibility

RNAssistant uses an OpenAI-compatible Chat Completions endpoint. Chat mode expects ordinary assistant text. Agent mode supports three explicit request profiles while all Office actions still execute locally. The complete decision and tool-result contract is in `docs/agent-decision-protocol.md`.

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
| `native_tool_calls` | OpenAI `tools`, strict function schemas, `tool_choice`, `parallel_tool_calls:false`, and `json_schema` for non-tool decisions | Either one `message.tool_calls[]` entry or one terminal/plan AgentDecision v1 object in content. |

The canonical AgentDecision fields are `protocolVersion`, `kind`, `decisionSummary`, `goal`, `plan`, `tool`, and `message`. Every field is present; inactive fields are JSON `null`. Markdown fences, prose around JSON, content arrays as planner output, alternate envelopes, `function_call`, and multiple/parallel tool calls are rejected.

If `json_schema` fails before any tool execution and fallback is enabled, RNAssistant retries via `json_object` and keeps that mode for the rest of the run. It does not switch after a tool has run and does not auto-fallback from `native_tool_calls`, because replay could duplicate a mutation.

## Tool Result History

The default result role is `tool`. RNAssistant sends an assistant message with one `tool_calls` entry, followed by a `role: tool` message whose `tool_call_id` exactly matches it. This pair is generated even when the model selected the tool through AgentDecision JSON rather than native calling.

Endpoints that reject tool-call history can use `developer` or `user` result role. In that mode RNAssistant sends `TOOL_RESULT:` plus the same normalized JSON envelope as ordinary content. Instruction role is independently selectable as `developer` (default), `system`, or `user`.

## Optional Capabilities

- `usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`; `input_tokens`/`output_tokens` aliases are also accepted.
- Reasoning in `message.reasoning_content` / `message.reasoning`, their SSE delta equivalents, or one leading `<think>...</think>` block. It is separated from normal content and never treated as AgentDecision fields.
- A model catalog endpoint, defaulting to `/config/models.json` derived from `BaseUrl`. RNAssistant understands OpenAI-style `data[].id`, `models[].value`, and root arrays plus common context/output capability aliases.
- SSE streaming. RNAssistant detects an SSE body even if a compatible endpoint omits or mislabels `Content-Type`.
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
| Returns a valid AgentDecision object | Locally validated; router, safety gates and phase state decide whether it may continue. |
| Returns plain text/fenced/noisy JSON in Agent mode | Rejected; one bounded correction request may be made. |
| Returns one native tool call in native mode | API name maps back to the exact local tool id; arguments are validated before local execution. |
| Returns native tool calls in a JSON-only mode | Rejected because that mode's contract is AgentDecision content. |
| Returns multiple native calls | Rejected; RNAssistant allows one external tool per model turn. |
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
