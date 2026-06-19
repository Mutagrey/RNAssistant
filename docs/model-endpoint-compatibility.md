# Model Endpoint Compatibility

RNAssistant talks to an OpenAI-compatible Chat Completions endpoint and keeps its local tool protocol text-first.

## Required

- `POST /v1/chat/completions`, or a `BaseUrl` that already ends with `/chat/completions`.
- JSON request body with `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and `stream: false`.
- Response shape compatible with `choices[0].message.content`.
- Assistant content should be plain text and may include fenced `rnassistant-agent` JSON blocks for local Office actions.

## Optional

- `choices[0].message.tool_calls`: accepted as compatibility input. RNAssistant converts it into the same local `rnassistant-agent` text block before parsing.
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
| Returns plain assistant text | Shows text in chat. |
| Returns `rnassistant-agent` fenced JSON | Parses and runs local tools if auto-run policy allows it. |
| Returns native `tool_calls` | Converts to fenced RNAssistant JSON and runs the same parser. |
| Returns malformed tool JSON | Records diagnostics; Agent mode may ask the model once for repaired executable JSON. |
| Omits token usage | Chat still works; token counters show estimated/context-side data only. |
| Lacks `/config/models.json` | Model catalog load fails, but manually entered model IDs can still be saved. |

## Recommended Endpoint Profile

- Non-streaming Chat Completions compatible response.
- Strong instruction following for fenced JSON.
- Long enough context window for document context.
- Stable support for custom system prompts and ordinary `user`/`assistant` message history.
