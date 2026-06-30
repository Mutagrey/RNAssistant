(function () {
  state.liveReasoning = state.liveReasoning || "";

  function reasoningValue(message, pascal, camel, fallback) {
    message = message || {};
    return message[pascal] !== undefined ? message[pascal] : (message[camel] !== undefined ? message[camel] : fallback);
  }

  function reasoningBlock(text, tokens, live, truncated) {
    if (!text) return null;
    var details = document.createElement("details");
    details.className = "reasoning-block" + (live ? " is-live" : "");
    details.open = !!live;
    var summary = document.createElement("summary");
    summary.textContent = "Ход рассуждения" +
      (tokens !== null && tokens !== undefined ? " · " + tokens + " токенов" : "") +
      (truncated ? " · обрезано" : "");
    details.appendChild(summary);
    var body = document.createElement("pre");
    body.textContent = text;
    details.appendChild(body);
    return details;
  }

  var baseRenderMessageArticle = renderMessageArticle;
  renderMessageArticle = function (message, index) {
    var article = baseRenderMessageArticle(message, index);
    var text = reasoningValue(message, "ReasoningContent", "reasoningContent", "");
    var block = reasoningBlock(
      text,
      reasoningValue(message, "ReasoningTokens", "reasoningTokens", null),
      false,
      !!reasoningValue(message, "ReasoningTruncated", "reasoningTruncated", false));
    if (block) {
      var body = article.querySelector(".agent-activity-wrap, .markdown");
      article.insertBefore(block, body || article.firstChild);
    }
    return article;
  };

  var baseRenderLiveActivity = renderLiveActivity;
  renderLiveActivity = function () {
    var article = baseRenderLiveActivity();
    if (!state.liveReasoning) return article;
    if (!article) {
      article = document.createElement("article");
      article.className = "message assistant pending agent-live";
    }
    var block = reasoningBlock(state.liveReasoning, null, true, state.liveReasoning.length >= 100000);
    article.insertBefore(block, article.firstChild);
    return article;
  };

  var baseClearActivity = clearActivity;
  clearActivity = function () {
    state.liveReasoning = "";
    return baseClearActivity();
  };

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", function (event) {
      var response = event.data;
      if (typeof response === "string") {
        try { response = JSON.parse(response); } catch (error) { return; }
      }
      if (!response || response.type !== "progress") return;
      var payload = response.payload || {};
      var delta = payload.reasoningDelta || payload.ReasoningDelta || "";
      if (!delta) return;
      state.liveReasoning += delta;
      if (state.liveReasoning.length > 100000) {
        state.liveReasoning = state.liveReasoning.substring(0, 100000);
      }
      renderMessages();
    });
  }
}());
