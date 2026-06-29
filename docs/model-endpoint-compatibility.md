# Model Endpoint Compatibility

RNAssistant talks to an OpenAI-compatible Chat Completions endpoint. Agent mode uses a strict JSON planner envelope in assistant text; Office actions still execute locally.

## Required

- `POST /v1/chat/completions`, or a `BaseUrl` that already ends with `/chat/completions`.
- JSON request body with `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream: false`.
- Response shape compatible with `choices[0].message.content`.
- Assistant content in Agent mode must be exactly one JSON object with `kind`, `intent`, `message`, and `steps`.

## Optional

- `choices[0].message.tool_calls`: accepted as compatibility input by the low-level client, but default Agent mode uses the strict planner envelope.
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
| Returns strict planner JSON | Router/validator/gates decide whether local tools can execute. |
| Returns plain assistant text in Agent mode | Rejected by strict parser; Agent mode asks once for corrected JSON. |
| Returns `rnassistant-agent` fenced JSON | Accepted only when explicit legacy compatibility is enabled. |
| Returns native `tool_calls` | Accepted as compatibility input, but not required or preferred. |
| Returns malformed planner JSON | Records diagnostics; Agent mode asks once for a corrected JSON object. |
| Omits token usage | Chat still works; token counters show estimated/context-side data only. |
| Lacks `/config/models.json` | Model catalog load fails, but manually entered model IDs can still be saved. |

## Recommended Endpoint Profile

- Non-streaming Chat Completions compatible response.
- Strong instruction following for exact JSON object responses.
- Long enough context window for document context.
- Stable support for custom system prompts and ordinary `user`/`assistant` message history.
