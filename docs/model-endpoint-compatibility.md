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

Chat and Agent both expect the JSON described in [conversation-protocol.md](conversation-protocol.md). Chat receives only read-only resource tools; Agent receives its runnable catalog. The response format and tool-result role are explicit settings; RNAssistant does not auto-select them. Optional `json_schema` fallback is limited to an endpoint rejection and lasts only for the current run.

Settings → Agent → «Запустить тест» checks three exact sentinels using the currently selected instruction role, response format, and result role: `ROLE_OK`, the requested `TOOL_OK` Agent JSON call with exact id/name/arguments, and `RESULT_OK` after the chosen result transport. The probes do not execute Office actions.

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

- Invalid conversation JSON in either mode receives up to `MaxAgentFormatRetries` clean format-correction requests (1–20, default 10). Raw invalid responses and temporary instructions are not persisted; exhausting the limit stops the run.
- An explicit endpoint rejection of selected `json_schema` may make one request-local `json_object` retry when fallback is enabled. Network, timeout, rate-limit, server, or provider-refusal errors are returned without an automatic duplicate request.
- Unknown tools and invalid arguments are rejected locally before Office execution.
- A tool failure is returned to the model as `TOOL_RESULT`; the model decides whether to retry, change arguments, ask the user, or finish.
