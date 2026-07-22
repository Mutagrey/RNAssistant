# Model Endpoint Compatibility

RNAssistant talks to an OpenAI-compatible Chat Completions endpoint. Chat mode uses ordinary assistant text. Agent mode uses a strict JSON planner envelope in assistant text; Office actions still execute locally. Auto mode selects one of these paths before the request.

## Required

- `POST /v1/chat/completions`, or a `BaseUrl` that already ends with `/chat/completions`.
- JSON request body with `model`, `messages`, `max_tokens`, `temperature`, `top_p`, and the configured `stream` value.
- `max_tokens` is capped per request by the configured/model output limit and the remaining context window after prompt and safety reserve.
- The context window and output limit are independent capabilities. RNAssistant budgets `prompt + response + safety <= context window`; for very large windows the estimator's safety reserve is capped at 16,384 tokens instead of reserving a fixed percentage indefinitely.
- Non-stream response compatible with `choices[0].message.content`, or SSE chunks compatible with `choices[0].delta.content` when streaming is enabled.
- Canonical assistant content in Agent mode is exactly one JSON object with `kind`, `intent`, `message`, and `steps`.

## Optional

- `usage.prompt_tokens`, `usage.completion_tokens`, `usage.total_tokens`: stored when present. `input_tokens` and `output_tokens` aliases are also accepted.
- Reasoning may be returned in `message.reasoning_content` / `message.reasoning` and their SSE delta equivalents, or as one leading `<think>...</think>` block in assistant content. It is removed from final Chat/planner content, stored separately, and rendered as a collapsible block. Reasoning token counts are recognized in `completion_tokens_details`, `output_tokens_details`, or a root `reasoning_tokens` usage field.
- Model catalog GET endpoint: defaults to `/config/models.json` derived from `BaseUrl`, but can be set independently in Settings. Catalogs may use `models` with `value`, OpenAI-style `data` with `id`, or a root array with `id`/`display_name`. Context aliases such as `max_context_tokens`, `context_window`, and `context_length` are recognized separately from output aliases such as `max_output_tokens` and `max_completion_tokens`. Manual model entry still works without it.
- SSE streaming for Chat mode when `StreamResponses` is enabled. Text deltas are displayed incrementally in the chat.
- Custom request headers: supported from Settings, except unsafe headers such as `Content-Length` and `Host`.
- Image and scanned-PDF requests use `image_url` content parts. MP3/WAV requests use OpenAI-compatible [`input_audio`](https://developers.openai.com/api/docs/guides/audio) parts with base64 `data` and `format` set to `mp3` or `wav`; RNAssistant still expects a text response.

## Attachment Model Routing

- The chat model remains the base model for ordinary text requests. Adding an attachment never changes the stored chat or global model.
- Only media attached to the current user turn is sent as binary input. Historical images and audio are omitted; historical PDFs are retained as extracted text only.
- Image and scanned-PDF turns require Vision, audio turns require Audio, and mixed turns require one model that explicitly supports every required input modality.
- `AttachmentModelPriority` is evaluated from top to bottom. Manual Vision/Audio overrides take precedence over catalog capabilities; unknown capability values are not treated as supported.
- RNAssistant does not fail over to another model after an endpoint error. If no configured model satisfies the request, it fails before the API call with a settings-oriented error.

## Not Required

- Native remote tool execution. Office actions run locally through RNAssistant tools.
- Server-side state, threads, assistants, files, or vector stores.

## Behavior Matrix

| Endpoint behavior | RNAssistant behavior |
| --- | --- |
| Returns ordinary text in Chat mode | Stored as the assistant response; no planner parsing or local tool execution occurs. |
| Returns strict planner JSON | Router/validator/gates decide whether local tools can execute. |
| Returns plain assistant text in Agent mode | Rejected by strict parser; Agent mode asks once for corrected JSON. |
| Returns one clean `json` fence | Fence is unwrapped and the strict planner object is validated. |
| Returns another fence type, legacy envelope, prose around JSON, or a JSON array | Rejected; Agent mode asks once for corrected JSON. |
| Returns native `tool_calls`, `function_call`, or content-part arrays | Not converted to local tools; missing/invalid assistant text is rejected. |
| Returns malformed planner JSON | Records format/error and a bounded local response preview; Agent mode asks once for a corrected JSON object while preserving the task and available tools. |
| Returns leading `<think>...</think>` before text or planner JSON | Thinking is separated from assistant content, streamed as reasoning progress, and does not invalidate the planner object. Non-leading tags remain ordinary content. |
| A required local tool is unavailable | The endpoint is not called for that iteration; RNAssistant records local route and tool-exclusion diagnostics. |
| Omits token usage | Chat still works; token counters show estimated/context-side data only. |
| Lacks `/config/models.json` | Model catalog load fails, but manually entered model IDs can still be saved. |
| Returns a model list as `data[].id` | The picker imports model IDs and any recognized capability fields. |
| Returns a root model array | The picker imports display names, default model, Reasoning/Vision/Audio flags, limits, and recognized default generation parameters. |
| Receives MP3/WAV `input_audio` content | Audio-capable requests remain in the ordinary Chat Completions flow and return text. |
| Requires another audio/video wire format | The attachment is rejected; provider-specific media contracts and video are not supported. |

## Recommended Endpoint Profile

- Chat Completions compatible response; SSE streaming is recommended for smoother Chat mode output.
- Strong instruction following for exact JSON object responses.
- Long enough context window for document context.
- Stable support for ordinary `user`/`assistant` history. RNAssistant intentionally sends Chat/Agent instructions as `user` by default because some compatible endpoints limit the length or handling of `system` messages; Settings can explicitly switch them to `system`.
