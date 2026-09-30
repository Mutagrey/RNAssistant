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
  context.currentActiveSend = () => context.activeSend || null;
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
    const { context, calls } = createSyncContext();
    context.activeSend = { requestId: "running" };
    await context.synchronizeChatState(false);
    assert.equal(calls.length, 0, "background catalog scan waits for the active send");
    context.nextCatalog = { activeChatId: "chat-a", chats: context.state.chats, documents: context.state.documents };
    await context.synchronizeChatState(true);
    assert.deepEqual(calls.map(call => call.type), ["listChats"], "explicit refresh still runs");
    console.log("PASS chat sync: background poll skips active send");
  }

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
    const { context } = createSyncContext();
    const applied = [];
    context.confirmDiscardHtmlWorkspaceChanges = () => true;
    context.renderChatSessions = () => {};
    context.clearSendError = () => {};
    context.log = () => {};
    context.applyInitState = init => applied.push(init);
    context.send = async type => {
      assert.equal(type, "selectChat");
      return { chatId: "word-chat", init: { host: "Word", activeChatId: "word-chat" } };
    };
    await context.selectChat("word-chat");
    assert.equal(applied.length, 1, "existing foreign chat reloads the target host in this pane");
    console.log("PASS Office chat selection: current pane applies target initialization");
  }

  {
    const { context, calls } = createSyncContext();
    let releaseSync;
    context.state.chatSyncPromise = new Promise(resolve => { releaseSync = resolve; });
    context.confirmDiscardHtmlWorkspaceChanges = () => true;
    context.renderChatSessions = () => {};
    context.clearSendError = () => {};
    context.log = () => {};
    context.applyInitState = () => {};
    context.send = async (type, payload) => {
      calls.push({ type, payload });
      return { chatId: "word-chat", init: { host: "Word", activeChatId: "word-chat" } };
    };
    const selection = context.selectChat("word-chat");
    assert.equal(calls.length, 0, "selection waits for an active catalog request");
    await context.synchronizeChatState(true);
    assert.equal(calls.length, 0, "new catalog requests pause during selection");
    releaseSync();
    await selection;
    assert.deepEqual(calls.map(call => call.type), ["selectChat"]);
    console.log("PASS Office chat selection: catalog sync drains before host switch");
  }

  {
    const applied = [];
    const chatContext = vm.createContext({ console });
    chatContext.window = chatContext;
    chatContext.state = {
      bridgeUnavailable: false, officeHostChatAvailable: true,
      officeHostChatPending: false, chatNavigationVersion: 0, chatSyncPromise: null
    };
    chatContext.currentActiveSend = () => null;
    chatContext.hasActiveMessageEdit = () => false;
    chatContext.beginChatNavigation = () => ++chatContext.state.chatNavigationVersion;
    chatContext.renderChatSessions = () => {};
    chatContext.confirmDiscardHtmlWorkspaceChanges = () => true;
    chatContext.send = async () => ({ host: "Outlook", chatId: "outlook-chat", init: {
      host: "Outlook", activeChatId: "outlook-chat"
    } });
    chatContext.applyInitState = init => applied.push(init);
    chatContext.applyChatNavigationState = () => { throw new Error("old host state applied"); };
    chatContext.synchronizeChatState = () => { throw new Error("catalog-only update applied"); };
    chatContext.log = () => {};
    vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat.js"), "utf8"), chatContext,
      { filename: "app-chat.js" });
    await chatContext.createOfficeHostChat("Outlook");
    assert.equal(applied.length, 1, "cross-host creation applies full target initialization");
    assert.equal(applied[0].activeChatId, "outlook-chat");
    console.log("PASS Office host chat: current pane applies target initialization");
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
  assert.ok(index.includes("app-core.js?v=office-chat-20260930-3"), "app-core.js cache key was bumped");
  assert.ok(index.includes("app-chat-run.js?v=response-render-timing-20260928-1"), "chat run cache key was bumped");
  assert.ok(index.includes("app-chat-edit.js?v=chat-sync-20260903-1"), "chat edit cache key was bumped");
  assert.ok(index.includes("app-chat-session.js?v=office-chat-20260930-4"), "chat session cache key was bumped");
  assert.ok(fs.readFileSync(path.join(root, "web/js/app.js"), "utf8")
    .includes("window.setInterval(synchronizeChatState, 60000)"), "background catalog scan is limited to once per minute");
  console.log("OK 7/7");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
