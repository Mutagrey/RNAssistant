(function () {
  "use strict";

  function pendingConfirmation(activity) {
    if (!activity) return null;
    if (activityPendingId(activity) && activityStatus(activity) === "waiting") return activity;
    var children = activityChildren(activity);
    for (var index = children.length - 1; index >= 0; index -= 1) {
      var child = pendingConfirmation(children[index]);
      if (child) return child;
    }
    return null;
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    function pendingActivity() {
      if (options.currentActiveSend()) return null;
      var live = state.liveAgentRun || [];
      for (var liveIndex = live.length - 1; liveIndex >= 0; liveIndex -= 1) {
        var liveMatch = pendingConfirmation(live[liveIndex]);
        if (liveMatch) return liveMatch;
      }
      for (var messageIndex = state.messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
        var match = pendingConfirmation(messageActivity(state.messages[messageIndex]));
        if (match) return match;
      }
      return null;
    }

    function renderDock() {
      var dock = $("agentApprovalDock");
      if (!dock) return;
      var activity = pendingActivity();
      if (!activity) {
        dock.replaceChildren();
        dock.classList.add("hidden");
        return;
      }

      var pendingId = activityPendingId(activity);
      var panel = document.createElement("section");
      panel.className = "agent-approval-panel";
      panel.setAttribute("aria-label", "Подтверждение действия агента");

      var mark = document.createElement("span");
      mark.className = "agent-approval-mark";
      mark.setAttribute("aria-hidden", "true");
      mark.textContent = "!";
      panel.appendChild(mark);

      var copy = document.createElement("div");
      copy.className = "agent-approval-copy";
      var title = document.createElement("div");
      title.className = "agent-approval-title";
      title.textContent = options.primaryText(activity);
      copy.appendChild(title);
      var meta = document.createElement("div");
      meta.className = "agent-approval-meta";
      meta.textContent = "Нужно подтверждение";
      copy.appendChild(meta);
      var reason = activityResultMessage(activity);
      if (reason) {
        var reasonNode = document.createElement("div");
        reasonNode.className = "agent-approval-reason";
        reasonNode.textContent = reason;
        copy.appendChild(reasonNode);
      }
      panel.appendChild(copy);

      var actions = document.createElement("div");
      actions.className = "agent-approval-actions";
      actions.appendChild(createAgentTextButton("Отменить", "secondary", function () { options.cancel(pendingId); }));
      actions.appendChild(createAgentTextButton("Подтвердить", "primary", function () { options.confirm(pendingId); }));
      panel.appendChild(actions);

      dock.replaceChildren(panel);
      dock.classList.remove("hidden");
    }

    return { pendingActivity: pendingActivity, renderDock: renderDock };
  }

  window.RNAssistantAgentApproval = { create: create };
}());
