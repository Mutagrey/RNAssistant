# Model Endpoint Compatibility

RNAssistant talks to an OpenAI-compatible Chat Completions endpoint. Chat mode uses ordinary assistant text. Agent mode uses a strict JSON planner envelope in assistant text; Office actions still execute locally. Auto mode selects one of these paths before the request.

## Required

- `POST /v1/chat/completions`, or a `BaseUrl` that already ends with `/chat/completions`.
- JSON request body with `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream: false`.
- Response shape compatible with `choices[0].message.content`, where `content` is a string or null.
- Canonical assistant content in Agent mode is exactly one JSON object with `kind`, `intent`, `message`, and `steps`.

## Optional

- `usage.prompt_tokens`, `usage.completion_tokens`, `usage.total_tokens`: stored when present. `input_tokens` and `output_tokens` aliases are also accepted.
- `GET /config/models.json`: used only by the model picker. Manual model entry still works without it.
- Custom request headers: supported from Settings, except unsafe headers such as `Content-Length` and `Host`.

## Not Required

- Native remote tool execution. Office actions run locally through RNAssistant tools.
- Streaming responses. The current runtime sends `stream: false`.
- Server-side state, threads, assistants, files, or vector stores.

## Behavior Matrix

| Endpoint behavior | RNAssistant behavior |
| --- | --- |
| Returns ordinary text in Chat mode | Stored as the assistant response; no planner parsing or local tool execution occurs. |
| Returns strict planner JSON | Router/validator/gates decide whether local tools can execute. |
| Returns plain assistant text in Agent mode | Rejected by strict parser; Agent mode asks once for corrected JSON. |
| Returns a fence, legacy envelope, prose around JSON, or a JSON array | Rejected; Agent mode asks once for corrected JSON. |
| Returns native `tool_calls`, `function_call`, or content-part arrays | Not converted to local tools; missing/invalid assistant text is rejected. |
| Returns malformed planner JSON | Records format/error and a bounded local response preview; Agent mode asks once for a corrected JSON object while preserving the task and available tools. |
| Omits token usage | Chat still works; token counters show estimated/context-side data only. |
| Lacks `/config/models.json` | Model catalog load fails, but manually entered model IDs can still be saved. |

## Recommended Endpoint Profile

- Non-streaming Chat Completions compatible response.
- Strong instruction following for exact JSON object responses.
- Long enough context window for document context.
- Stable support for ordinary `user`/`assistant` history. RNAssistant sends planner instructions as `user` by default; Settings can switch them to `system`.
