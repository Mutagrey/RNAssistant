// Test-only data adapter. Production panels call the same shared transcript owner.
(function () {
  window.renderWorkspaceFixture = function (list, chat, options) {
    var target = { source: "workspace", workspaceId: chat.project && chat.project.workspaceId || "", sessionId: chat.sessionId };
    return renderConversationTranscript(list, target, RNAssistantWorkspaceChat.transcriptData(chat), null, options);
  };
  window.paintWorkspaceFixture = function (list, chat) {
    return paintConversationStream(list, createMessagePresentation(messageView(list),
      RNAssistantWorkspaceChat.transcriptData(chat), null), [], true);
  };
}());
