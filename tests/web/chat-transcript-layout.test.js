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
    const page = await browser.newPage({ viewport: { width: 740, height: 800 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}
      body { margin: 0; } main { height: 100vh; display: flex; flex-direction: column; }
      .composer { flex-shrink: 0; } #chatInput { width: 100%; }
    </style><main><div class="chat-scroll-region"><div class="messages" id="messages" tabindex="0" aria-label="Сообщения"></div>
      <button id="chatScrollBottomButton" class="chat-scroll-button" aria-label="К последним сообщениям">↓</button></div>
      <form class="composer" id="chatForm"><span id="inboxAnnouncer" class="inbox-sr-only"></span><span id="inboxStatus"></span>
      <textarea id="chatInput" aria-label="Сообщение"></textarea><button type="button">Отправить</button></form></main>`);
    await page.evaluate(() => {
      window.state = { activeChatId: "chat", messages: [], activeSends: {}, chatRuns: {}, settings: {},
        draftAttachments: [], pendingChatSubmits: {}, editingMessageIndex: -1, messageStartIndex: 0, messageTotalCount: 0 };
      window.$ = id => document.getElementById(id);
      window.RNAssistantAgentApproval = { create: () => ({ pendingActivity: () => null, renderDock() {} }) };
      window.currentActiveSend = () => true;
      window.hasActiveMessageEdit = () => false;
      window.canEditMessage = () => false;
      window.renderChatResourceNavigation = () => {};
      window.renderSendControls = window.renderChatSessions = () => {};
      window.endChatRunTracking = () => {};
      window.smallIconButton = title => { const b = document.createElement("button"); b.textContent = title; return b; };
      window.appendMessageArtifactCards = () => {};
      window.appendQuestionCards = () => {};
      window.logOnce = () => {};
      window.log = () => {};
    });
    for (const file of ["vendor/purify.min.js", "vendor/marked.min.js", "vendor/highlight.min.js", "app-accent.js", "app-utils.js",
      "app-markdown.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js", "app-agent-activity.js", "app-messages.js", "app-chat-inbox.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      window.renderAgentPlanDock = () => {};
      state.messages = [{ Id: "user", Role: "user", Content: "Проверь отчёт и предложи исправления." }];
      state.liveStreamContent = "Изучаю документ.\n\n" + "Проверяю данные и связанные формулы.\n".repeat(12);
      applyChatInbox({ chatId: "chat", epoch: "e", revision: 1, running: true, phase: "executing", items: [
        { Id: "queued", OperationId: "op1", Revision: 1, Status: "Pending", Delivery: "Queue",
          Text: "Добавь ещё один раздел.\nНе потеряй этот текст: <img src=x onerror=alert(1)>\n" + "длинноеслово".repeat(20), Attachments: [{ FileName: "Исходные данные.xlsx" }] },
        { Id: "steer", OperationId: "op2", Revision: 1, Status: "Pending", Delivery: "Steer", Text: "Уточнение к текущей задаче", Attachments: [] }
      ] });
      bindMessageScrollControls();
    });

    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 800 });
      for (const dark of [false, true]) {
        await page.evaluate(dark => {
          document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
          RNAssistantAccents.apply("green", false);
        }, dark);
        const layout = await page.evaluate(() => ({
          order: Array.from($("messages").children).map(n => n.messageUnitKey),
          overflow: Array.from($("messages").children).some(n => n.scrollWidth > n.clientWidth + 1),
          composerQueue: !!$("chatForm").querySelector(".inbox-message"),
          injected: !!document.querySelector(".inbox-message img"),
          clipped: getComputedStyle(document.querySelector(".inbox-pending-text")).webkitLineClamp,
          background: getComputedStyle(document.querySelector(".inbox-pending-text")).backgroundColor,
          streamStatus: document.querySelector(".agent-stream-status").textContent,
          streamColor: getComputedStyle(document.querySelector(".agent-stream-message")).color,
          paragraphColor: getComputedStyle(document.querySelector(".agent-stream-step")).color,
          streamOpacity: getComputedStyle(document.querySelector(".agent-run.live")).opacity,
          divider: getComputedStyle(document.querySelector(".agent-run-actions")).borderBottomWidth
        }));
        assert.deepEqual(layout.order, ["message:user", "live:agent-run", "live:input:op1", "live:input:op2"]);
        assert.equal(layout.overflow, false, `bubbles fit ${width}px in ${dark ? "dark" : "light"} theme`);
        assert.equal(layout.composerQueue, false);
        assert.equal(layout.injected, false);
        assert.equal(layout.clipped, "none", "all queued text remains readable");
        assert.notEqual(layout.background, "rgba(0, 0, 0, 0)", "queued text retains its user bubble background");
        assert.equal(layout.streamStatus, "Формируется…");
        assert.equal(layout.streamColor, layout.paragraphColor, "the draft uses ordinary readable text color");
        assert.equal(layout.streamOpacity, "1", "streamed text is not faded");
        assert.equal(layout.divider, "0px", "a draft has no final-answer divider");
      }
    }
    await page.locator(".inbox-item-menu summary").first().click();
    await page.getByRole("button", { name: "Редактировать", exact: true }).first().click();
    const editor = page.getByRole("textbox", { name: "Текст сообщения в очереди" });
    await editor.fill("Редактирую, пока агент продолжает работать");
    await editor.evaluate(el => { window.editNode = el; el.setSelectionRange(7, 12); });
    await page.evaluate(() => applyChatInbox({ ...chatInbox(), revision: 2, phase: "thinking" }));
    assert.equal(await editor.evaluate(el => el === window.editNode && document.activeElement === el && el.selectionStart === 7 && el.selectionEnd === 12), true,
      "polling preserves the editor, focus and selection");
    await editor.press("Escape");
    assert.equal(await editor.count(), 0);

    const settleLayout = () => page.evaluate(() => new Promise(resolve =>
      requestAnimationFrame(() => requestAnimationFrame(resolve))));

    // Late media/viewer layout can grow a message without resizing the scroll box.
    await page.evaluate(() => {
      state.liveStreamContent = "Длинный ответ\n\n" + "Строка текущего ответа.\n".repeat(90);
      renderMessages({ forceScroll: true });
    });
    await settleLayout();
    const initialHeight = await page.locator("#messages").evaluate(box => box.scrollHeight);
    await page.evaluate(() => {
      const content = document.createElement("div");
      content.id = "lateMessageContent";
      content.style.cssText = "height:240px;flex-shrink:0";
      $("messages").lastElementChild.appendChild(content);
    });
    await settleLayout();
    assert.ok(await page.locator("#messages").evaluate(box => box.scrollHeight) >= initialHeight + 240);
    assert.ok(await page.evaluate(() => chatDistanceFromBottom($("messages"))) <= 2,
      "late message growth keeps the actual end visible without another render");
    await page.evaluate(() => {
      $("lateMessageContent").style.height = "480px";
      state.liveStreamContent += "Новое продолжение ответа.\n";
      renderStreamingMessages();
    });
    assert.ok(await page.evaluate(() => chatDistanceFromBottom($("messages"))) <= 2,
      "a render before resize delivery does not mistake layout growth for manual scrolling");
    await settleLayout();

    // Growing the viewport clamps scrollTop; that is not an upward user gesture.
    for (const height of [1000, 650, 800]) {
      await page.setViewportSize({ width: 360, height });
      await settleLayout();
      assert.ok(await page.evaluate(() => chatDistanceFromBottom($("messages"))) <= 2,
        "following survives viewport growth and shrinkage at " + height + "px");
    }
    const manualTop = await page.evaluate(() => {
      const box = $("messages");
      box.dispatchEvent(new WheelEvent("wheel", { deltaY: -12 })); box.scrollTop -= 12;
      const top = box.scrollTop;
      $("lateMessageContent").style.height = "720px";
      return top;
    });
    await settleLayout();
    assert.ok(Math.abs(manualTop - await page.locator("#messages").evaluate(box => box.scrollTop)) < 2,
      "late message growth respects the reader's manual scroll position");
    await page.evaluate(() => { $("lateMessageContent").remove(); scrollMessagesToBottom(false); });
    await settleLayout();

    // Short upward movement must suspend following even inside the old 64px threshold.
    const smallScroll = await page.evaluate(() => {
      const box = $("messages");
      box.dispatchEvent(new WheelEvent("wheel", { deltaY: -12 })); box.scrollTop -= 12;
      const top = box.scrollTop;
      state.liveStreamContent += "Новая строка ответа.\n".repeat(10);
      renderStreamingMessages();
      return { top, after: box.scrollTop };
    });
    assert.ok(Math.abs(smallScroll.top - smallScroll.after) < 2, "a small manual scroll is never pulled back to bottom");

    const scroll = await page.evaluate(() => {
      const box = $("messages"); box.scrollTop = 800; box.dispatchEvent(new Event("scroll"));
      const top = box.scrollTop;
      for (let i = 0; i < 8; i++) { state.liveStreamContent += "Ещё данные\n".repeat(15); renderStreamingMessages(); }
      return { top, after: box.scrollTop, distance: chatDistanceFromBottom(box) };
    });
    assert.ok(Math.abs(scroll.top - scroll.after) < 2, "stream replacements preserve free scroll inside a tall message");
    assert.ok(scroll.distance > 500, "reader remains well above the moving tail");

    await page.getByRole("button", { name: "К последним сообщениям", exact: true }).click();
    await page.evaluate(() => { state.liveStreamContent += "Ещё строка\n".repeat(10); renderStreamingMessages(); });
    assert.ok(await page.evaluate(() => chatDistanceFromBottom($("messages"))) <= 2, "explicit return resumes following");

    // A render's deferred scroll must yield to an intervening user gesture.
    const deferred = await page.evaluate(async () => {
      const box = $("messages"); syncChatScroll(true, false);
      box.dispatchEvent(new WheelEvent("wheel", { deltaY: -200 })); box.scrollTop -= 200;
      const top = box.scrollTop;
      await new Promise(requestAnimationFrame);
      return { top, after: box.scrollTop };
    });
    assert.ok(Math.abs(deferred.top - deferred.after) < 2, "deferred follow cannot override user scroll");

    // Prepending history preserves the visible message even if the tail is windowed away.
    const anchor = await page.evaluate(() => {
      state.liveStreamContent = ""; state.chatInputEchoes.chat = []; state.chatInboxes.chat.items = [];
      state.messages = Array.from({ length: 30 }, (_, i) => ({ Id: "m" + i, Role: "user", Content: "История " + i + "\nТекст сообщения" }));
      renderMessages({ forceScroll: true });
      const box = $("messages"); box.scrollTop = 700; box.dispatchEvent(new Event("scroll"));
      const target = Array.from(box.children).find(n => n.getBoundingClientRect().bottom > box.getBoundingClientRect().top);
      const offset = target.getBoundingClientRect().top;
      const height = box.scrollHeight;
      state.messages = Array.from({ length: 10 }, (_, i) => ({ Id: "older" + i, Role: "user", Content: "Ранняя история " + i }))
        .concat(state.messages.slice(0, 20));
      renderMessages({ preserveTop: true, previousScrollHeight: height });
      return { before: offset, after: target.getBoundingClientRect().top, attached: target.isConnected };
    });
    assert.equal(anchor.attached, true);
    assert.ok(Math.abs(anchor.before - anchor.after) < 2, "paging anchors the viewport independently of removed tail height");
    await page.evaluate(() => {
      state.activeChatId = "fresh"; state.messages = []; renderMessages();
      state.messages = [{ Id: "fresh-user", Role: "user", Content: "Новый запрос" }];
      state.liveStreamContent = "Первый ответ в новом чате.\n\n".repeat(70); renderMessages();
    });
    assert.ok(await page.evaluate(() => chatDistanceFromBottom($("messages"))) <= 2,
      "an empty new chat does not inherit the previous chat's scroll position");
    if (process.env.LAYOUT_SCREENSHOT) {
      await page.evaluate(() => {
        state.activeChatId = "chat";
        state.messages = [{ Id: "preview-user", Role: "user", Content: "Проверь отчёт и поясни изменения." }];
        state.liveStreamContent = "Проверяю данные и готовлю ответ.";
        applyChatInbox({ chatId: "chat", epoch: "e", revision: 3, running: true, items: [
          { Id: "preview", Revision: 1, Status: "Pending", Delivery: "Queue", Text: "Добавь сравнение с прошлым месяцем.\nИ поясни, откуда взялись отличия.", Attachments: [{ FileName: "Отчёт.xlsx" }] }
        ] }); renderMessages({ forceScroll: true });
      });
      await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    }
    assert.deepEqual(errors, []);
    console.log("PASS chat transcript browser: queue placement, narrow themes, safe full text, edit focus, free/follow scroll, late layout, viewport resizing, deferred gestures and history anchoring");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
