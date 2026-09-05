"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");

function createSyncContext() {
  const calls = [];
  const catalogStates = [];
  const fullStates = [];
  const context = vm.createContext({ console });
  context.window = context;
  context.document = { hidden: false, hasFocus: () => true };
  context.state = {
    bridgeUnavailable: false,
    activeChatId: "chat-a",
    chats: [{ Id: "chat-a", Revision: 7, Title: "Active" }],
    documents: [{ DocumentKey: "doc-a", Title: "Book.xlsx", IsActive: true }],
    activeSends: {},
    chatProjectionRevisions: { "chat-a": 7 },
    chatNavigationVersion: 1,
    chatStateApplyVersion: 1,
    chatSyncPromise: null
  };
  context.RNAssistantRunViewState = {
    chatRevision: chat => {
      const revision = chat && (chat.Revision !== undefined ? chat.Revision : chat.revision);
      return Number.isSafeInteger(revision) ? revision : null;
    },
    fromChatSummary: () => null
  };
  context.chatId = chat => chat && (chat.Id || chat.id) || "";
  context.chatTitle = chat => chat && (chat.Title || chat.title) || "";
  context.chatMessageCount = chat => chat && (chat.MessageCount || chat.messageCount) || 0;
  context.chatDocumentTitle = chat => chat && (chat.DocumentTitle || chat.documentTitle) || "";
  context.chatHost = chat => chat && (chat.Host || chat.host) || "";
  context.chatDocumentKey = chat => chat && (chat.DocumentKey || chat.documentKey) || "";
  context.chatJsonlByteLength = () => 0;
  context.chatCasBlobCount = () => 0;
  context.chatCasLogicalByteLength = () => 0;
  context.chatCasStoredByteLength = () => 0;
  context.chatCasMissingBlobCount = () => 0;
  context.chatCasReferenceIssueCount = () => 0;
  context.chatStorageWarningLevel = () => "none";
  context.currentActiveSend = () => null;
  context.applyChatCatalogState = response => {
    catalogStates.push(response);
    context.state.chats = response.chats || response.Chats || [];
    context.state.documents = response.documents || response.Documents || [];
    context.state.chatStateApplyVersion++;
  };
  context.applyChatState = response => {
    fullStates.push(response);
    context.state.activeChatId = response.activeChatId || response.ActiveChatId || "";
    context.state.messages = response.messages || response.Messages || [];
  };
  context.logOnce = () => {};
  context.send = async (type, payload) => {
    calls.push({ type, payload });
    if (type === "listChats") return context.nextCatalog;
    if (type === "getChatState") return context.nextFull;
    throw new Error("unexpected bridge call: " + type);
  };
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8"), context,
    { filename: "app-chat-session.js" });
  return { context, calls, catalogStates, fullStates };
}

(async function () {
  {
    const { context, calls, catalogStates, fullStates } = createSyncContext();
    context.nextCatalog = {
      activeChatId: "chat-a",
      chats: [{ Id: "chat-a", Revision: 7, Title: "Renamed" }],
      documents: [{ DocumentKey: "doc-a", Title: "Book.xlsx", IsActive: false }]
    };
    await context.synchronizeChatState(false);
    assert.deepEqual(calls.map(call => call.type), ["listChats"]);
    assert.equal(catalogStates.length, 1, "catalog-only drift updates sidebar state");
    assert.equal(fullStates.length, 0, "unchanged active revision does not reload transcript");
    console.log("PASS chat sync: unchanged active revision stays catalog-only");
  }

  {
    const { context, calls, catalogStates, fullStates } = createSyncContext();
    context.nextCatalog = {
      activeChatId: "chat-a",
      chats: [{ Id: "chat-a", Revision: 8, Title: "Active" }],
      documents: []
    };
    context.nextFull = {
      activeChatId: "chat-a",
      sessionRevision: 8,
      messages: [{ Content: "new transcript" }]
    };
    await context.synchronizeChatState(false);
    assert.deepEqual(calls.map(call => call.type), ["listChats", "getChatState"]);
    assert.equal(JSON.stringify(calls[1].payload), JSON.stringify({ chatId: "chat-a" }));
    assert.equal(catalogStates.length, 1, "new revision still applies catalog first");
    assert.equal(fullStates.length, 1, "new active revision reloads full state once");
    console.log("PASS chat sync: newer active revision triggers one explicit full reload");
  }

  {
    const posted = [];
    const focusContext = vm.createContext({ console, setTimeout, clearTimeout });
    focusContext.window = focusContext;
    focusContext.document = {
      hasFocus: () => true,
      activeElement: { tagName: "div" }
    };
    focusContext.chrome = {
      webview: {
        addEventListener: () => {},
        postMessage: message => posted.push(message)
      }
    };
    focusContext.getSelection = () => ({
      rangeCount: 1,
      isCollapsed: false,
      toString: () => { throw new Error("selection text was materialized"); }
    });
    vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-core.js"), "utf8"), focusContext,
      { filename: "app-core.js" });
    focusContext.scheduleFocusStateReport();
    focusContext.scheduleFocusStateReport();
    await new Promise(resolve => setTimeout(resolve, 80));
    assert.equal(posted.length, 1, "selection burst is coalesced");
    assert.equal(posted[0].type, "focusState");
    assert.equal(posted[0].payload.wantsKeyboard, true);
    console.log("PASS focus state: selection report is debounced without reading selected text");
  }

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-core.js", "app-chat-run.js", "app-chat-edit.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=chat-sync-20260903-1"), asset + " cache key was bumped");
  });
  assert.ok(index.includes("app-chat-session.js?v=vba-resource-20260906-1"), "chat session resource lifecycle cache key was bumped");
  console.log("OK 4/4");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
