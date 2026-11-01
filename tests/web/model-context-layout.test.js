"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: path.resolve(__dirname, "../..") });
  server.stderr.resume(); // Drain the preview access log; an unread pipe can stall asset delivery.
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject); server.once("exit", code => reject(new Error("preview exited " + code)));
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    page.on("requestfailed", request => console.error("Failed asset", request.url(), request.failure()));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    const screenshot = async name => {
      if (!process.env.INSPECTOR_SCREENSHOTS) return;
      fs.mkdirSync(process.env.INSPECTOR_SCREENSHOTS, { recursive: true });
      await page.waitForFunction(() => !document.getElementById("chatLayout").classList.contains("inspector-transition") &&
        !document.querySelector(".shell").classList.contains("navigation-transition"));
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.mouse.move(0, 0);
      await page.screenshot({ path: path.join(process.env.INSPECTOR_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    const body = page.locator("#modelContextBody"), messages = body.locator(".is-message");
    assert.equal(await page.locator("#modelContextBody").evaluate(node => getComputedStyle(node).overflowY), "auto", "request stylesheet loads");
    await page.evaluate(() => {
      const old = send; window.layoutPayloadReads = 0;
      send = (type, args) => { if (type === "getModelContextPayload") layoutPayloadReads++; return old(type, args); };
    });
    const openInput = async () => {
      await page.evaluate(() => RNAssistantModelContext.open({ requestEventId: "preview-request" }));
      await page.locator("#modelContextSummaryTab[aria-selected=true]").waitFor();
      await page.locator("#modelContextMessagesTab").click();
      await messages.first().waitFor();
    };
    await page.evaluate(() => {
      const original = send;
      send = async (type, args) => {
        const reply = await original(type, args);
        if (type !== "getModelContext") return reply;
        const request = reply.events.find(event => event.type === "llm.request");
        if (!request) return reply;
        const resource = { uri: "rna://document/table/sales", revision: "sales-r1" }, scope = { kind: "conversation", id: "preview-chat" };
        request.trace.ContextMessages = [
          { MessageIndex: 0, Presentation: "Full" },
          { MessageIndex: 1, Presentation: "Unknown", Parts: [{ Kind: "image_url", SourceAttachmentId: "scan-2",
            Presentation: "Excluded", Reason: "Изображение не выбрано: достигнут лимит запроса." }] },
          { MessageIndex: 2, Presentation: "Full" },
          { MessageIndex: 3, Presentation: "Fragment", EvidenceLinks: [{ EvidenceId: "sales-observation", ScopeId: scope, Resource: resource,
            View: "text", BodyIncludedInMessage: null, ObservedCoverage: { kind: "line-range", start: 1, end: 5 },
            ReadMetadata: { requestedCoverage: { kind: "line-range", start: 1, end: 20 }, selectionComplete: true,
              representationComplete: false, sourceComplete: null } }] },
          { MessageIndex: 4, Presentation: "Full" },
          { Presentation: "Excluded", Role: "tool", ExclusionKind: "Budget", Reason: "Подробные строки не вошли в бюджет запроса." }
        ];
        request.trace.RequestComposition = { Version: 1, Method: "base_text_media_v1", HasMedia: false, TotalEstimatedTokens: 213, Items: [
          { MessageIndex: 0, Group: "InstructionsAndCatalog", EstimatedTokens: 40 },
          { MessageIndex: 1, Group: "Conversation", EstimatedTokens: 45 },
          { MessageIndex: 2, Group: "Conversation", EstimatedTokens: 25 },
          { MessageIndex: 3, Group: "Resources", EstimatedTokens: 60 },
          { MessageIndex: 4, Group: "Unattributed", EstimatedTokens: 35 },
          { MessageIndex: null, Group: "ResponseFormat", EstimatedTokens: 8 }
        ] };
        request.trace.EstimatedPromptTokens = 260;
        const response = reply.events.find(event => event.type === "llm.response");
        if (response) response.trace.PromptTokens = 310;
        reply.resources = [{ alias: "R1@v1", resource, scope, currentness: "Unknown", payloadAvailability: "Unknown", observations: [] }];
        return reply;
      };
    });
    await openInput();
    const composition = body.locator(".model-context-composition");
    assert.match(await composition.innerText(), /213 ток\. · базовая оценка/);
    assert.doesNotMatch(await composition.innerText(), /260|310/, "composition is not normalized to admission or API usage");
    const grouping = body.getByLabel("Группировка контекста");
    assert.equal(await grouping.inputValue(), "wire");
    await grouping.selectOption("groups");
    const historyGroup = composition.locator('[data-composition-group="Conversation"]');
    assert.equal(await historyGroup.locator(":scope > summary").isVisible(), true, "composition is visible without opening an extra disclosure");
    assert.match(await historyGroup.locator(":scope > summary").innerText(), /История сообщений[\s\S]*Сообщений: 2[\s\S]*70 ток/);
    await screenshot("request-composition-overview");
    const beforeComposition = await page.evaluate(() => layoutPayloadReads);
    await historyGroup.locator(":scope > summary").click();
    const historyMessage = historyGroup.locator('[data-message-index="1"]');
    await historyMessage.locator(":scope > summary").click();
    await historyMessage.locator(".rn-markdown-viewer-rendered").waitFor();
    assert.match(await historyMessage.locator(":scope > summary").innerText(), /Пользователь[\s\S]*45 ток/);
    assert.equal(await historyGroup.locator(".is-message").count(), 2, "history is grouped by exact wire messages");
    await screenshot("request-history-messages");
    await composition.getByRole("button", { name: /^Контекст ресурсов ·/ }).click();
    const resourceMessage = body.locator('[data-message-index="3"]');
    await resourceMessage.locator(":scope > summary").waitFor();
    await screenshot("request-composition-groups");
    await resourceMessage.locator(":scope > summary").click();
    assert.equal(await resourceMessage.evaluate(node => node.open), true);
    assert.equal(await page.evaluate(() => layoutPayloadReads), beforeComposition, "grouped messages reuse the loaded request");
    const sources = body.locator('[data-context-source="message/3"]'), evidence = body.locator('[data-context-source="message/3/resource/0"]');
    await sources.locator(":scope > summary").click(); await evidence.locator(":scope > summary").click();
    assert.match(await evidence.innerText(), /Включение не установлено/);
    const facts = await evidence.locator(":scope > dl").innerText();
    assert.match(facts, /Строки · начало 1 · конец 5/); assert.match(facts, /Строки · начало 1 · конец 20/);
    assert.doesNotMatch(facts, /"kind"/, "readable coverage keeps recorded bounds without a raw JSON paragraph");
    assert.match(facts, /Выбранный фрагмент\s+Да/); assert.match(facts, /Представление целиком\s+Нет/);
    assert.match(facts, /Источник целиком\s+Не установлено/);
    const sourceReads = await page.evaluate(() => layoutPayloadReads);
    await screenshot("request-source-coverage");
    await evidence.getByRole("button", { name: "Открыть R1@v1", exact: true }).click();
    await screenshot("references-resource");
    await body.locator(".model-context-resource[open]").waitFor();
    await page.locator("#modelContextMessagesTab").click(); await evidence.waitFor();
    assert.equal(await evidence.evaluate(node => node.open), true, "source expansion survives resource navigation");
    assert.equal(await page.evaluate(() => layoutPayloadReads), sourceReads, "source navigation reuses the same immutable request");
    await grouping.selectOption("wire");
    assert.deepEqual(await messages.evaluateAll(nodes => nodes.map(node => Number(node.dataset.messageIndex))), [0, 1, 2, 3, 4]);
    assert.equal(await resourceMessage.evaluate(node => node.open), true, "switching layout preserves expanded messages");
    await composition.getByRole("button", { name: /^Сообщение 4 ·/ }).click();
    assert.equal(await page.evaluate(() => layoutPayloadReads), sourceReads, "layout and chart navigation do not reread the payload");
    await grouping.selectOption("groups"); await evidence.waitFor();
    assert.equal(await evidence.evaluate(node => node.open), true, "nested source expansion survives both layouts");
    const omissions = body.locator('[data-context-source="omissions"]');
    await omissions.locator(":scope > summary").click();
    await omissions.locator(":scope > details > summary").first().click();
    await omissions.locator(":scope > details > summary").last().click();
    assert.match(await omissions.innerText(), /В составе сообщения 2/);
    assert.match(await omissions.innerText(), /Подробные строки не вошли в бюджет/);
    await omissions.scrollIntoViewIfNeeded(); await screenshot("request-omissions");
    await page.setViewportSize({ width: 390, height: 844 });
    await evidence.scrollIntoViewIfNeeded();
    for (const theme of ["light", "dark"]) {
      await page.evaluate(value => document.documentElement.setAttribute("data-theme", value), theme);
      assert.equal(await body.evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
      await screenshot("request-sources-" + theme + "-narrow");
      await body.evaluate(node => node.scrollTop = 0);
      await screenshot("request-composition-" + theme + "-narrow");
      await evidence.scrollIntoViewIfNeeded();
    }
    await page.locator("#modelContextHistory > summary").click();
    await page.locator("#modelContextAliasSearch").fill("Q1");
    await page.locator("#modelContextAliasFind").click();
    await page.waitForFunction(() => document.getElementById("modelContextStatus").textContent.includes("Найдено Q1"));
    await page.locator("#modelContextMode").selectOption("preview");
    await page.waitForFunction(() => document.getElementById("modelContextStatus").textContent.includes("Предпросмотр, ещё не отправлено"));
    assert.equal(await page.locator("#modelContextAttempt").isHidden(), true);
    for (const id of ["modelContextSummaryTab", "modelContextResponseTab", "modelContextResourcesTab", "modelContextEvidenceTab", "modelContextCompareTab"])
      assert.equal(await page.locator("#" + id).isDisabled(), true, "preview has no saved evidence: " + id);
    assert.equal(await page.locator("#modelContextDownload").isDisabled(), false);
    assert.equal(await body.locator(".model-context-composition").count(), 0, "preview cannot reuse saved request composition");
    await page.locator("#modelContextMode").selectOption("actual");
    await messages.first().waitFor();
    await page.evaluate(() => {
      RNAssistantModelContext.close();
      const oldSend = send, oldRead = RNAssistantResourceDownload.read;
      window.captionFixturePdf = false;
      const coverage = () => captionFixturePdf ? { kind: "character-range", address: "pdf-page:1", start: 4, end: 17, fields: [] }
        : { kind: "line-range", start: 42, end: 43, fields: [] };
      const resource = () => ({ uri: captionFixturePdf ? "rna://workspace/files/report.pdf" : "rna://workspace/files/src/A.cs", revision: "exact-a" });
      const scope = { kind: "workspace", id: "project-a" };
      const wire = () => JSON.stringify({ messages: [{ role: "user", content: "TOOL_RESULT:\n" + JSON.stringify({
        tool_call_id: "T7", evidence: "E3", name: "core.read", status: "ok", message: "Read.",
        data: { kind: "resource-read", ref: "R3@v1", target: captionFixturePdf ? "report.pdf" : "src/A.cs", representation: "text", coverage: coverage(),
          pageIndex: captionFixturePdf ? 1 : undefined,
          text: "искомый текст", complete: false }
      }) }] });
      RNAssistantResourceDownload.read = async (data, options) => data.captionFixture ? new TextEncoder().encode(wire()) : oldRead(data, options);
      send = async (type, args) => {
        if (type === "getModelContextPayload" && args.eventId === "preview-request")
          return { chatId: args.chatId, eventId: args.eventId, data: { captionFixture: true, payload: { contentType: "application/json" } } };
        const reply = await oldSend(type, args);
        if (type === "getModelContext") {
          const request = reply.events.find(event => event.type === "llm.request");
          request.trace = { RunId: "preview-run", StepId: "preview-step", ContextMessages: [{ MessageIndex: 0, Presentation: "Full",
            ToolCallId: "caption-call", EvidenceLinks: [{ EvidenceId: "caption-evidence", ScopeId: scope, Resource: resource(),
              View: "text", ObservedCoverage: coverage(), BodyIncludedInMessage: true }] }] };
          reply.resources = [{ alias: "R3@v1", resource: resource(), scope, currentness: "Unknown", payloadAvailability: "Unknown", observations: [] }];
          reply.operations = [{ alias: "T7", canonicalId: "caption-call" }];
        }
        return reply;
      };
    });
    await openInput();
    await page.setViewportSize({ width: 1440, height: 900 });
    const exactMessage = body.locator(".is-message").first();
    await exactMessage.locator(":scope > summary").click();
    await exactMessage.locator('[data-context-source="message/0"] > summary').click();
    const exactSource = exactMessage.locator('[data-context-source="message/0/resource/0"]');
    assert.match(await exactSource.locator(":scope > summary").innerText(), /R3@v1 — src\/A.cs, строка 42: «искомый текст»/);
    await exactSource.locator(":scope > summary").click();
    await screenshot("source-caption-diagnostics");
    await page.evaluate(() => { RNAssistantModelContext.close(); captionFixturePdf = true; });
    await openInput();
    await exactMessage.locator(":scope > summary").click();
    await exactMessage.locator('[data-context-source="message/0"] > summary').click();
    assert.match(await exactSource.locator(":scope > summary").innerText(), /R3@v1 — report.pdf, страница 2, символы \[4, 17\)/,
      "actual-request diagnostics preserve the typed PDF page coordinate");
    await page.evaluate(() => {
      RNAssistantModelContext.close();
      const oldSend = send;
      send = async (type, args) => type === "getToolResultPresentation" && args.toolCallId === "caption-call"
        ? { chatId: args.chatId, runId: args.runId, toolCallId: args.toolCallId, toolCallReference: "T7", evidenceReference: "E3", blocks: [{
          kind: "text", title: "Содержимое", complete: false, text: "искомый текст\nточный сохранённый фрагмент",
          source: { reference: "R3@v1", target: "src/A.cs", view: "text", coverage: { kind: "line-range", start: 42, end: 43 },
            excerpt: "искомый текст", excerptTruncated: false }
        }] } : oldSend(type, args);
      const activity = { Kind: "tool", ToolId: "core.read", ToolCallId: "caption-call", RunId: "preview-run",
        Status: "completed", Title: "Чтение исходника", ResultMessage: "Фрагмент прочитан.", ArgumentsJson: '{"target":"src/A.cs"}' };
      const card = renderActivityNode(activity, false, false, { message: { RunId: "preview-run" } });
      card.id = "caption-activity"; document.getElementById("messages").replaceChildren(card);
    });
    await page.locator("#diagnosticsReturnChat").click();
    await page.locator("#caption-activity > details > summary").click();
    await page.locator("#caption-activity .tool-source-caption").waitFor();
    await screenshot("source-caption-chat");
    await page.evaluate(() => RNAssistantChatInspector.setOpen(false));
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator("#caption-activity .tool-source-caption").scrollIntoViewIfNeeded();
    assert.equal(await page.locator("#caption-activity").evaluate(node => node.closest(".chat-main").inert), false,
      "narrow source caption is in the interactive chat, not behind Inspector");
    assert.equal(await page.locator("#caption-activity").evaluate(node => node.scrollWidth > node.clientWidth + 1), false,
      "source caption fits narrow chat without horizontal overflow");
    await screenshot("source-caption-chat-narrow");
    await page.evaluate(() => RNAssistantChatInspector.setOpen(true));
    await openInput();
    await page.evaluate(() => { state.activeChatId = "other-chat"; RNAssistantModelContext.sync(); });
    await page.locator("#modelContextDialog").waitFor({ state: "hidden" });
    assert.equal(await page.locator("#modelContextDialog").isVisible(), false);
    assert.equal(await page.locator("#chatLayout > .chat-main").evaluate(node => node.inert), false);
    assert.deepEqual(errors, []);
    console.log("PASS model context layout: recorded composition, source coverage, omissions, exact resource/message links, retained state, typed PDF captions and narrow themes");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
