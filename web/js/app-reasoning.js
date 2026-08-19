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
  details.className = "reasoning-block" + (live ? " is-live" : "");
  details.open = !!live;
  var summary = document.createElement("summary");
  summary.textContent = "Ход рассуждения" +
    (tokens !== null && tokens !== undefined ? " · " + tokens + " токенов" : "") +
    (truncated ? " · обрезано" : "");
  details.appendChild(summary);
  if (text) {
    var body = document.createElement("pre");
    body.textContent = text;
    details.appendChild(body);
  }
  return details;
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

function renderLiveReasoningMessage() {
  if (!state.liveReasoning) return null;
  var article = document.createElement("article");
  article.className = "message assistant pending reasoning-live-message";
  article.appendChild(reasoningBlock(
    state.liveReasoning,
    null,
    !state.liveReasoningComplete,
    state.liveReasoning.length >= MAX_LIVE_REASONING_CHARS));
  return article;
}

function resetLiveReasoning() {
  state.liveReasoning = "";
  state.liveReasoningComplete = false;
}
