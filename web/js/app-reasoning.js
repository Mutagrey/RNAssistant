var MAX_LIVE_REASONING_CHARS = 24000;

function reasoningValue(message, pascal, camel, fallback) {
  message = message || {};
  return message[pascal] !== undefined ? message[pascal] : (message[camel] !== undefined ? message[camel] : fallback);
}

function messageHasReasoning(message) {
  var text = reasoningValue(message, "ReasoningContent", "reasoningContent", "");
  var tokens = reasoningValue(message, "ReasoningTokens", "reasoningTokens", null);
  return !!text || tokens !== null && tokens !== undefined;
}

function reasoningBlock(text, tokens, live, truncated) {
  if (!text && (tokens === null || tokens === undefined)) return null;
  var details = document.createElement("details");
  details.open = !!live;
  var summary = document.createElement("summary");
  var title = document.createElement("span");
  title.className = "reasoning-title";
  summary.appendChild(title);
  var caret = document.createElement("span");
  caret.className = "reasoning-caret";
  caret.setAttribute("aria-hidden", "true");
  summary.appendChild(caret);
  details.appendChild(summary);
  updateReasoningBlock(details, text, tokens, live, truncated);
  return details;
}

function updateReasoningBlock(details, text, tokens, live, truncated) {
  var className = "reasoning-block" + (live ? " is-live" : "");
  if (details.className !== className) details.className = className;
  var title = details.querySelector(".reasoning-title");
  var label = "Ход рассуждения" +
    (tokens !== null && tokens !== undefined ? " · " + tokens + " токенов" : "") +
    (truncated ? " · обрезано" : "");
  if (title.textContent !== label) title.textContent = label;
  var body = details.querySelector("pre");
  if (text) {
    if (!body) {
      body = document.createElement("pre");
      details.appendChild(body);
    }
    if (body.textContent !== text) body.textContent = text;
  } else if (body) details.removeChild(body);
}

function appendMessageReasoning(parent, message) {
  if (!parent || !messageHasReasoning(message)) return null;
  var block = reasoningBlock(
    reasoningValue(message, "ReasoningContent", "reasoningContent", ""),
    reasoningValue(message, "ReasoningTokens", "reasoningTokens", null),
    false,
    !!reasoningValue(message, "ReasoningTruncated", "reasoningTruncated", false));
  if (block) parent.appendChild(block);
  return block;
}

function resetLiveReasoning() {
  state.liveReasoning = "";
  state.liveReasoningComplete = false;
}
