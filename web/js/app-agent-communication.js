(function () {
  "use strict";
  function sender(message) {
    var context = message && (message.AgentCommunication || message.agentCommunication);
    return message && (message.AgentSender || message.agentSender) || context && (context.Sender || context.sender) || null;
  }
  function field(value, name) { return value && (value[name] || value[name[0].toLowerCase() + name.slice(1)]); }
  function enabled() { return compatibilityValue(state.settings, "MaxAgentMessages", "maxAgentMessages", 20) > 0; }
  function appendSender(node, message) {
    var from = sender(message); if (!from) return;
    node.classList.add("agent-message");
    var link = document.createElement("button"); link.type = "button"; link.className = "agent-message-origin";
    link.textContent = "Агент " + field(from, "AgentId") + " · " + (field(from, "Title") || "Чат");
    link.title = "Открыть чат отправителя";
    link.addEventListener("click", function () {
      RNAssistantConversationPanel.open({ source: field(from, "Source"), workspaceId: field(from, "WorkspaceId"), sessionId: field(from, "ChatId") });
    });
    node.appendChild(link);
  }
  function mount(component) {
    var target = component.target, closed = false;
    var recovery = RNAssistantAgentRecovery.create(component);
    var checking = null, signature = null, generation = 0, budget = null, retryOperation = null, lastRunning = false;
    var box = document.createElement("div"); box.className = "agent-communication-status"; box.hidden = true;
    var label = document.createElement("span"), button = document.createElement("button");
    button.type = "button"; button.textContent = "Продолжить общение";
    box.setAttribute("role", "status"); box.append(label, button);
    component.node("chatForm").before(box);
    function available() { return !closed && !!target.sessionId && !state.initializePromise && !state.bridgeUnavailable && !component.loading && !component.loadError; }
    function refresh(running) {
      lastRunning = !!running;
      if (closed) return;
      if (!enabled()) { signature = null; ++generation; budget = null; box.hidden = true; return; }
      if (!available()) return;
      if (running) { signature = null; ++generation; box.hidden = true; return; }
      var data = component.snapshot(), revision = field(data, "SessionRevision") || field(field(data, "State"), "Revision");
      var next = String(revision || "idle");
      if (checking || signature === next) return;
      signature = next; var version = ++generation;
      checking = send("getAgentCommunicationState", Object.assign({}, target)).then(function (result) {
        if (closed || version !== generation) return;
        if (!budget || budget.chainId !== result.chainId) retryOperation = null;
        budget = result; box.hidden = result.enabled === false || !result.limitReached; button.hidden = false;
        label.textContent = "Лимит общения исчерпан (" + result.sent + " сообщений).";
      }).catch(function (error) {
        if (closed || version !== generation) return;
        box.hidden = false; button.hidden = true;
        label.textContent = "Состояние общения недоступно: " + (error.detail || error.message);
      }).finally(function () { checking = null; if (!closed && version !== generation) refresh(lastRunning); });
    }
    button.addEventListener("click", async function () {
      if (!enabled() || !budget || button.disabled) return;
      button.disabled = true; retryOperation = retryOperation || "agent-continue-" + crypto.randomUUID();
      try {
        await send("continueAgentCommunication", Object.assign({}, target, { chainId: budget.chainId, operationId: retryOperation }));
        if (closed) return;
        retryOperation = null; signature = null; box.hidden = true; await component.refresh();
      } catch (error) { if (!closed) label.textContent = error.detail || error.message; }
      finally { button.disabled = false; }
    });
    refresh(false);
    return { refresh: refresh, openRecovery: recovery.open, settingsChanged: function () { signature = null; ++generation; refresh(lastRunning); },
      rebind: function () { recovery.reset(); signature = null; ++generation; refresh(lastRunning); }, dispose: function () {
      if (closed) return; closed = true; ++generation; recovery.dispose(); box.remove();
    } };
  }
  window.RNAssistantAgentCommunication = Object.freeze({ sender: sender, appendSender: appendSender, mount: mount,
    settingsChanged: function () { RNAssistantConversationPanel.each(function (component) { component.communication.settingsChanged(); }); } });
})();
