(function () {
  "use strict";

  function create(options) {
    options = options || {};
    var state = options.state;

    function pendingState() {
      if (options.currentActiveSend()) return null;
      var viewState = state.activeRunViewState || null;
      return viewState && viewState.lifecycle === "awaiting_confirmation" && viewState.pendingConfirmation
        ? { viewState: viewState, pending: viewState.pendingConfirmation }
        : null;
    }

    function renderDock() {
      var dock = $("agentApprovalDock");
      if (!dock) return;
      var pendingStateValue = pendingState();
      if (!pendingStateValue) {
        dock.replaceChildren();
        dock.classList.add("hidden");
        return;
      }

      var pending = pendingStateValue.pending;
      var pendingId = pending.pendingId;
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
      title.textContent = options.primaryText(pending);
      copy.appendChild(title);
      var meta = document.createElement("div");
      meta.className = "agent-approval-meta";
      meta.textContent = "Нужно подтверждение";
      copy.appendChild(meta);
      var reason = pendingStateValue.viewState.currentAction || pendingStateValue.viewState.narrative;
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

    return {
      pendingActivity: function () {
        var result = pendingState();
        return result ? result.pending : null;
      },
      renderDock: renderDock
    };
  }

  window.RNAssistantAgentApproval = { create: create };
}());
