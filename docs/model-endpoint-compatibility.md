# Model endpoint compatibility

RNAssistant uses an OpenAI-compatible Chat Completions endpoint.

## Required transport

- `POST /v1/chat/completions`, or a `BaseUrl` already ending in `/chat/completions`;
- request fields `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream`;
- non-stream `choices[0].message` or SSE `choices[0].delta`;
- `user` and `assistant` roles;
- the configured Agent instruction role: `developer` by default, optionally `system` or `user`;
- `response_format: {"type":"json_object"}` for Agent mode.

Chat mode expects ordinary assistant text. Agent mode expects the JSON described in [agent-protocol.md](agent-protocol.md). RNAssistant does not switch between structured-output profiles, native tool calls, result roles, or fallback transports.

Settings → Agent → «Запустить тест» checks only three things: the selected instruction role, one `json_object` tool-shaped response, and consumption of a `TOOL_RESULT` JSON string. The probes do not execute Office actions.

## Optional capabilities

- Provider token usage; otherwise the UI uses an estimate.
- SSE streaming.
- Reasoning metadata in supported response fields or one leading `<think>` block. It is stored separately from visible content.
- Model catalog metadata for context/output limits and Vision/Audio support.
- Image, rendered scanned-PDF, and audio content parts when the selected model is explicitly marked compatible.
- Custom request headers except unsafe transport headers.
- Debug traffic logging without authorization/header values. Message bodies may contain document data, so logging is disabled by default.

Attachments never trigger automatic model routing or failover. RNAssistant uses the selected model and fails clearly when its declared capabilities do not support the current media.

## Failure behavior

- Invalid Agent JSON receives up to `MaxAgentFormatRetries` clean format-correction requests (1–5, default 2). Raw invalid responses and temporary instructions are not persisted; exhausting the limit stops the run.
- Network, timeout, rate-limit, server, or provider-refusal errors are returned without an automatic duplicate request.
- Unknown tools and invalid arguments are rejected locally before Office execution.
- A tool failure is returned to the model as `TOOL_RESULT`; the model decides whether to retry, change arguments, ask the user, or finish.
