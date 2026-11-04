(function () {
  "use strict";
  function sender(message) {
    var context = message && (message.AgentCommunication || message.agentCommunication);
    return message && (message.AgentSender || message.agentSender) || context && (context.Sender || context.sender) || null;
  }
  function field(value, name) { return value && (value[name] || value[name[0].toLowerCase() + name.slice(1)]); }
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
    var recovery = RNAssistantAgentRecovery.create(component);
    return { openRecovery: recovery.open, closeRecovery: recovery.reset,
      settingsChanged: recovery.reset, rebind: recovery.reset, dispose: recovery.dispose };
  }
  window.RNAssistantAgentCommunication = Object.freeze({ sender: sender, appendSender: appendSender, mount: mount,
    settingsChanged: function () { RNAssistantConversationPanel.each(function (component) { component.communication.settingsChanged(); }); } });
})();
