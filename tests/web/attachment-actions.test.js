"use strict";

// Real DOM/File/DataTransfer with a controlled bridge; no Office qualification.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");
const read = file => fs.readFileSync(path.join(root, "web", file), "utf8");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 850 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent(read("index.html").replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, "")
      .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, "").replace(/<link\b[^>]*>/g, ""));
    for (const file of ["app.css", "app-chat.css", "app-composer.css"])
      await page.addStyleTag({ content: read("css/" + file) });
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      window.state = { activeChatId: "chat-a", draftAttachments: [], chatDrafts: {}, messages: [] };
      window.chatDraftStore = () => state.chatDrafts;
      window.hasActiveMessageEdit = () => !!state.editing;
      window.isPendingChatSubmit = () => !!state.submitting;
      window.updateComposerInputState = () => {};
      window.log = () => {};
      window.uploads = new Map();
      window.received = [];
      window.posts = 0;
      window.send = async (type, payload) => {
        if (type === "beginChatResourceUpload") {
          const leaseId = String(uploads.size + 1).padStart(64, "0");
          uploads.set(leaseId, { ...payload, parts: [], size: 0 });
          return { leaseId, url: "https://rnassistant.local-resource/v1/upload/" + leaseId,
            byteLength: payload.byteLength, maxChunkBytes: 262144 };
        }
        if (type === "cancelChatResourceUpload" || type === "discardChatResourceDraft") return { closed: true };
        if (type !== "completeChatResourceUpload") throw new Error(type);
        const upload = uploads.get(payload.leaseId);
        if (window.holdCompletion) await new Promise(resolve => { window.finishUpload = resolve; });
        if (upload.chatId !== payload.chatId || upload.size !== upload.byteLength) throw new Error("incomplete upload");
        received.push({ chatId: payload.chatId, name: upload.fileName, text: await new Blob(upload.parts).text() });
        return { resource: { Id: payload.leaseId, FileName: upload.fileName, Kind: "text", Size: upload.size } };
      };
      window.fetch = async (url, options) => {
        if (!(options.body instanceof ArrayBuffer)) throw new Error("Blob request content is unavailable to the native host");
        if (options.headers["Content-Type"] !== "application/octet-stream") throw new Error("wrong MIME");
        const route = new URL(url), id = route.pathname.split("/").pop(), upload = uploads.get(id);
        const count = Number(route.searchParams.get("count")), offset = Number(route.searchParams.get("offset"));
        if (count > 262144 || count !== options.body.byteLength || offset !== upload.size) throw new Error("wrong chunk bounds");
        posts++;
        if (window.failPost) return { ok: false, status: 409,
          json: async () => ({ code: "RESOURCE_UPLOAD_INVALID", message: "The upload body is missing." }) };
        upload.parts.push(options.body); upload.size += count;
        return { ok: true, json: async () => ({ leaseId: id, nextOffset: upload.size }) };
      };
    });
    for (const file of ["app-chat-composer.js", "app-resource-upload.js", "app-attachments.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      window.updateComposerInputState = () => {};
      window.renderSendControls = () => {};
      bindComposerPickers(); bindAttachmentActions();
    });
    const idle = () => page.waitForFunction(() => !pendingChatResourceIngestion("chat-a"));
    const css = "/* Привет */\r\nbody { color: red; }\n" + " ".repeat(262144);
    const chooserReady = page.waitForEvent("filechooser");
    await page.click("#composerAddButton");
    await page.click("#attachFileButton");
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
    await (await chooserReady).setFiles({ name: "styles.css", mimeType: "text/css", buffer: Buffer.from(css) });
    await idle();
    assert.equal(await page.locator(".attachment-name").textContent(), "styles.css");
    assert.equal(await page.evaluate(() => received[0].text), css);
    assert.equal(await page.evaluate(() => posts), 2, "the real File is read in bounded chunks");
    assert.equal(await page.inputValue("#attachmentFileInput"), "", "the same file can be selected again");
    console.log("PASS attachment UI: picker attaches CSS to an empty chat with exact multichunk bytes");

    const drop = (name, target = "messages", text = "body{}") => page.evaluate(({ name, target, text }) => {
      const transfer = new DataTransfer(); transfer.items.add(new File([text], name));
      const node = $(target);
      node.dispatchEvent(new DragEvent("dragenter", { bubbles: true, cancelable: true, dataTransfer: transfer }));
      const highlighted = $("chatForm").classList.contains("is-dragging");
      const prevented = !node.dispatchEvent(new DragEvent("drop", { bubbles: true, cancelable: true, dataTransfer: transfer }));
      return { highlighted, prevented, cleared: !$("chatForm").classList.contains("is-dragging") };
    }, { name, target, text });
    await page.evaluate(() => { $("messages").textContent = "Existing conversation"; });
    assert.deepEqual(await drop("dropped.css"), { highlighted: true, prevented: true, cleared: true });
    await idle();
    assert.deepEqual(await page.locator(".attachment-name").allTextContents(), ["styles.css", "dropped.css"]);
    assert.equal(await page.evaluate(() => received[1].text), "body{}");
    await page.evaluate(() => {
      const transfer = new DataTransfer(); transfer.items.add(new File(["p{}"], "pasted.css", { type: "text/css" }));
      $("chatInput").dispatchEvent(new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData: transfer }));
    });
    await idle();
    assert.equal(await page.locator(".attachment-name").count(), 3);
    assert.equal(await page.evaluate(() => {
      const transfer = new DataTransfer(); transfer.setData("text/plain", "selected text");
      return $("chatInput").dispatchEvent(new DragEvent("drop", { bubbles: true, cancelable: true, dataTransfer: transfer }));
    }), true, "text drag keeps normal editing behavior");
    const before = await page.evaluate(() => uploads.size);
    assert.equal((await drop("outside.css", "chatSessionList")).prevented, true);
    assert.equal(await page.evaluate(() => uploads.size), before, "dropping outside chat prevents navigation without uploading");
    console.log("PASS attachment UI: history drop, clipboard, normal text drag and outside-drop protection");

    await page.evaluate(() => { window.failPost = true; });
    await drop("failed.css"); await idle();
    assert.equal(await page.locator("#attachmentStatus").isVisible(), true);
    assert.match(await page.locator("#attachmentStatus").textContent(), /failed\.css.*409/);
    assert.match(await page.locator("#attachmentStatus").textContent(), /upload body is missing/);
    assert.equal(await page.locator(".attachment-name").count(), 3, "failed upload cannot appear successful");
    await page.evaluate(() => { window.failPost = false; });
    await drop("empty.css", "messages", ""); await idle();
    assert.match(await page.locator("#attachmentStatus").textContent(), /непустым/);
    await page.evaluate(() => { state.editing = true; });
    assert.equal((await drop("editing.css")).highlighted, false);
    assert.match(await page.locator("#attachmentStatus").textContent(), /редактирование/);
    await page.evaluate(() => { state.editing = false; });
    console.log("PASS attachment UI: transport, validation and blocked-input errors stay visible at the composer");

    await page.evaluate(() => { window.holdCompletion = true; });
    await drop("slow.css");
    await page.waitForFunction(() => !!window.finishUpload);
    assert.match(await page.locator("#attachmentStatus").textContent(), /Добавление.*slow\.css/);
    await page.evaluate(() => {
      chatDraftStore()["chat-a"] = { text: "", attachments: state.draftAttachments.slice() };
      state.activeChatId = "chat-b"; state.draftAttachments = []; renderAttachmentDrafts();
      finishUpload();
    });
    await idle();
    assert.equal(await page.locator(".attachment-name").count(), 0);
    assert.equal(await page.locator("#attachmentStatus").isVisible(), false);
    await page.evaluate(() => {
      state.activeChatId = "chat-a"; state.draftAttachments = chatDraftStore()["chat-a"].attachments;
      renderAttachmentDrafts();
    });
    assert.equal(await page.locator(".attachment-name").last().textContent(), "slow.css");
    assert.equal(await page.locator("#attachmentStatus").isVisible(), false);
    assert.deepEqual(errors, []);
    console.log("PASS attachment UI: progress and late completion remain scoped to the selected upload chat");
  } finally { await browser.close(); }
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
