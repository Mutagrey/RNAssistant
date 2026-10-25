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
