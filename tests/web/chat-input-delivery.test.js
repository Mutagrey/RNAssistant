"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web");
const read = file => fs.readFileSync(path.join(root, file), "utf8");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 390, height: 800 } });
    page.setDefaultTimeout(5000);
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const html = read("index.html").replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, "")
      .replace(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g, (_, file) => "<style>" + read(file) + "</style>");
    await page.setContent(html);
    await page.evaluate(() => {
      window.state = { activeChatId: "chat", messages: [], chatRuns: {}, activeSends: {}, settings: {},
        draftAttachments: [], chatProjectionRevisions: {}, pendingChatSubmits: {}, editingMessageIndex: -1,
        messageStartIndex: 0, messageTotalCount: 0, activeChatMode: "agent" };
      window.$ = id => document.getElementById(id);
      window.RNAssistantAgentApproval = { create: () => ({ pendingActivity: () => null, renderDock() {} }) };
      window.hasActiveMessageEdit = () => false;
      window.canEditMessage = () => false;
      window.smallIconButton = title => { const button = document.createElement("button"); button.title = title; return button; };
      for (const name of ["renderChatResourceNavigation", "appendMessageArtifactCards", "appendQuestionCards",
        "bindAttachmentActions", "createChat", "clearChat", "compactChatContext", "openActiveDocument",
        "cancelMessageEdit", "renderContext", "renderContextMeter", "renderModelControls", "renderAttachmentDrafts",
        "setComposerPickerDisabled", "logOnce", "log"]) window[name] = () => {};
      $("chatLayout").classList.add("is-sidebar-hidden");
    });
    for (const file of ["vendor/purify.min.js", "vendor/marked.min.js", "vendor/highlight.min.js", "app-utils.js",
      "app-markdown.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js", "app-agent-activity.js",
      "app-messages.js", "app-chat-state.js", "app-chat-composer.js", "app-attachments.js", "app-chat-inbox.js", "app-chat-run.js", "app-chat.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      window.renderAgentPlanDock = window.renderChatSessions = window.bindAttachmentActions = () => {};
      window.activeChatUsesCurrentDocument = () => true;
      window.send = (type, payload) => {
        if (type === "getChatInbox") return Promise.resolve(window.serverInbox);
        if (type !== "submitChatInput") throw new Error("Unexpected bridge request: " + type);
        window.submitted = payload;
        return new Promise(resolve => { window.acknowledge = resolve; });
      };
      window.loadChatState = async () => ({ activeChatId: "chat", messages: window.serverMessages,
        messageStartIndex: 0, messageTotalCount: window.serverMessages.length, inbox: window.serverInbox });
      bindChatActions();
    });

    for (const delivery of ["Steer", "Queue"]) {
      await page.evaluate(delivery => {
        state.settings.RunningMessageDelivery = delivery;
        state.chatInputEchoes = {}; state.chatInboxes = {}; state.inboxSubmissions = {};
        state.chatRuns.chat = { runId: "run", activities: [], stream: "Проверяю данные отчёта.\n\n".repeat(60) };
        window.serverMessages = [{ Id: "original", Role: "user", Content: "Проверь отчёт" }];
        state.messages = serverMessages.slice(); state.messageTotalCount = 1;
        window.serverInbox = { chatId: "chat", epoch: "e", revision: 1, running: true, phase: "executing", items: [] };
        window.acknowledge = null;
        applyChatInbox(serverInbox, true); restoreActiveChatRun(false); renderMessages({ forceScroll: true });
      }, delivery);
      await page.locator("#chatInput").fill("Уточнение во время работы: " + delivery);
      await page.evaluate(() => {
        const box = $("messages"); box.dispatchEvent(new WheelEvent("wheel", { deltaY: -500 }));
        box.scrollTop -= 500; box.dispatchEvent(new Event("scroll"));
      });
      await page.locator("#sendButton").click();
      await page.waitForFunction(() => !!window.acknowledge);
      const receiptBeforeAck = await page.evaluate(() => {
        const bubble = document.querySelector(".inbox-message").getBoundingClientRect();
        const viewport = $("messages").getBoundingClientRect();
        return { visible: bubble.top >= viewport.top && bubble.bottom <= viewport.bottom + 1,
          text: document.querySelector(".inbox-pending-text").textContent };
      });
      assert.equal(receiptBeforeAck.text, "Уточнение во время работы: " + delivery);
      assert.equal(receiptBeforeAck.visible, true, "clicking Send reveals the new " + delivery + " bubble before acknowledgement");

      const manualTop = await page.evaluate(() => {
        const box = $("messages"); box.dispatchEvent(new WheelEvent("wheel", { deltaY: -250 }));
        box.scrollTop -= 250; box.dispatchEvent(new Event("scroll"));
        return box.scrollTop;
      });
      await page.evaluate(() => {
        const acceptedInput = { Id: "input", OperationId: submitted.operationId, Text: submitted.text,
          Status: "Applied", Delivery: submitted.delivery, Revision: 2, Attachments: [] };
        // Queue consumption is visible before the history page containing the user message.
        window.serverInbox = { ...serverInbox, revision: 2, items: [] };
        acknowledge({ ...serverInbox, acceptedInput });
      });
      await page.waitForFunction(() => !isPendingChatSubmit("chat") && !state.inboxChatRefreshing.chat);
      assert.equal(await page.locator("#chatInput").inputValue(), "");
      assert.equal(await page.locator(".inbox-message").count(), 1, "history lag cannot make acknowledged text disappear");
      assert.ok(Math.abs(await page.locator("#messages").evaluate(el => el.scrollTop) - manualTop) < 2,
        "late acknowledgement preserves a subsequent manual scroll");
      await page.evaluate(() => {
        state.chatRuns.chat.stream += "Следующие результаты проверки.\n\n".repeat(5);
        state.liveStreamContent = state.chatRuns.chat.stream; renderStreamingMessages();
      });
      assert.ok(Math.abs(await page.locator("#messages").evaluate(el => el.scrollTop) - manualTop) < 2,
        "streaming does not restart following after the user scrolls away");

      await page.evaluate(async () => {
        serverMessages = serverMessages.concat({ Id: "input", Role: "user", RunId: "run", Content: submitted.text });
        await pollChatInbox(); // Inbox revision is unchanged; unresolved echoes still trigger history refresh.
      });
      await page.waitForFunction(() => !state.inboxChatRefreshing.chat);
      assert.equal(await page.locator(".inbox-message").count(), 0);
      assert.equal(await page.locator(".message.user .markdown").filter({ hasText: "Уточнение во время работы: " + delivery }).count(), 1,
        "the exact persisted message replaces its echo once");
    }
    assert.deepEqual(errors, []);
    console.log("PASS chat input delivery: actual Send button, visible pre-ack echo, delayed history, exact handoff and manual scroll");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
