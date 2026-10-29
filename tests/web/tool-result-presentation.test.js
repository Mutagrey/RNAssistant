"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage();
    await page.setContent('<details id="step"><summary>Шаг</summary><div id="preview"></div><details id="raw"><summary>Результат JSON</summary>raw source</details></details>');
    await page.evaluate(() => {
      window.state = { activeChatId: "chat", chatProjectionRevisions: { chat: 1 } };
      window.activityValue = (a, upper, lower, fallback) => a[upper] || a[lower] || fallback;
      window.activityToolCallId = a => a.ToolCallId;
      window.messageRunId = m => m.RunId;
      window.requests = []; window.cancelled = [];
      window.send = (type, payload) => {
        let resolve; const promise = new Promise(done => { resolve = done; }); promise.requestId = String(requests.length);
        requests.push({ type, payload, resolve }); return promise;
      };
      window.cancelBridgeRequest = id => { cancelled.push(id); return Promise.resolve(); };
    });
    await page.addScriptTag({ content: fs.readFileSync(path.join(__dirname, "../../web/js/app-tool-result-preview.js"), "utf8") });
    await page.evaluate(() => {
      window.renderPreview = (target, response) => RNAssistantToolResultPresentation.renderCompact(target, response.blocks);
      appendToolResultPreview(document.getElementById("preview"), { ToolCallId: "call", RunId: "run" }, null, document.getElementById("step"), renderPreview);
    });
    assert.equal(await page.evaluate(() => requests.length), 0, "collapsed card performs no fetch");
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 1);
    assert.deepEqual(await page.evaluate(() => requests[0].payload), { chatId: "chat", runId: "run", toolCallId: "call" });
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => cancelled.includes("0"));
    await page.evaluate(() => requests[0].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [{ kind: "text", title: "Содержимое", text: "stale", complete: true }] }));
    assert.equal(await page.locator("#preview").textContent(), "", "collapsed late response is discarded");
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 2);
    await page.evaluate(() => requests[1].resolve({ chatId: "wrong", runId: "run", toolCallId: "call", blocks: [] }));
    await page.getByText("Не удалось загрузить представление · Повторить").waitFor();
    await page.getByText("Не удалось загрузить представление · Повторить").click();
    await page.waitForFunction(() => requests.length === 3);
    await page.evaluate(() => {
      state.chatProjectionRevisions.chat++;
      requests[2].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [] });
    });
    await page.waitForFunction(() => requests.length === 4);
    await page.evaluate(() => requests[3].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [
      { kind: "text", title: "Содержимое", text: "<img src=x onerror=alert(1)>", complete: true },
      { kind: "future_kind", title: "Unknown" }
    ] }));
    await page.locator(".tool-result-preview").waitFor();
    assert.equal(await page.locator("#preview img").count(), 0);
    assert.match(await page.locator("#preview").textContent(), /Этот вид представления недоступен/);
    assert.equal(await page.locator("#raw").textContent(), "Результат JSONraw source", "presentation never rewrites original results");
    await page.evaluate(() => {
      const card = document.createElement("div"); card.id = "source-captions"; document.body.appendChild(card);
      RNAssistantToolResultPresentation.renderCompact(card, [
        { kind: "text", title: "Содержимое", complete: false, text: "искомый текст\nfull retained slice", source: {
          reference: "R3@v1", target: "src/A.cs", view: "text", coverage: { kind: "line-range", start: 42, end: 43 },
          excerpt: "искомый текст", excerptTruncated: false } },
        { kind: "list", title: "Совпадения", complete: true, items: [{ title: "src/B.cs", detail: "другое совпадение", source: {
          target: "src/B.cs", line: 17, excerpt: "<img src=x onerror=alert(1)>" } }] }
      ]);
    });
    assert.equal(await page.locator("#source-captions > p").count(), 0,
      "the compact content does not repeat the call title or receipt");
    const sourceHeading = page.locator("#source-captions .tool-source-caption").first();
    assert.equal(await sourceHeading.textContent(), "R3@v1 — src/A.cs, строка 42");
    assert.equal(await page.locator("#source-captions pre").first().textContent(), "искомый текст\nfull retained slice",
      "retained slice is visible below its exact source reference");
    assert.equal(await page.locator("#source-captions img").count(), 0, "source paths and snippets are inert text");
    assert.match(await page.locator("#source-captions .tool-source-caption").last().textContent(), /^src\/B.cs, строка 17:/,
      "unbound search item has a useful label without a minted R reference");
    assert.equal(await page.evaluate(() => RNAssistantToolResultPresentation.sourceLabel({ target: "A", coverage: {
      kind: "line-range", start: 42, end: 45 } })), "A, строки 42–44", "exclusive line bounds are displayed correctly");
    assert.equal(await page.evaluate(() => RNAssistantToolResultPresentation.sourceLabel({ target: "report.pdf", pageIndex: 1, coverage: {
      kind: "character-range", address: "pdf-page:1", start: 4, end: 7 } })), "report.pdf, страница 2, символы [4, 7)",
      "PDF character window uses typed page coordinates instead of a technical address");
    assert.equal(await page.evaluate(() => RNAssistantToolResultPresentation.sourceLabel({ target: "report.pdf", coverage: {
      kind: "page-range", start: 1, end: 2 } })), "report.pdf, страница 2", "single PDF page uses a singular caption");
    assert.equal(await page.evaluate(() => RNAssistantToolResultPresentation.sourceLabel({ target: "report.pdf", coverage: {
      kind: "page-range", start: 2, end: 4 } })), "report.pdf, страницы 3–4", "PDF range is one-based with exclusive end preserved");
    await page.evaluate(() => RNAssistantToolResultPresentation.renderCompact(document.getElementById("source-captions"), [{
      kind: "text", title: "Медиа", complete: true, source: { target: "photo.png", view: "media" },
      note: "<img src=x onerror=alert(1)> Подготовка не подтверждает отправку.", text: "Сохранены сведения о медиа."
    }]));
    assert.match(await page.locator("#source-captions").innerText(), /Сохранены сведения о медиа/);
    assert.equal(await page.locator("#source-captions img").count(), 0, "typed block notes remain inert text");
    assert.equal(await page.evaluate(() => requests.length), 4, "expanding a retained media receipt never rereads the live resource");
    await page.locator("#source-captions").evaluate(node => node.remove());
    await page.locator("#step > summary").click();
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 5);
    await page.evaluate(() => { state.activeChatId = "other"; document.getElementById("step").remove(); });
    await page.waitForFunction(() => cancelled.includes("4"));
    await page.evaluate(() => requests[4].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [{ kind: "text", title: "Late", text: "foreign", complete: true }] }));
    assert.equal(await page.locator(".tool-result-preview").count(), 0, "removed card never receives foreign chat delivery");
    for (const file of ["app-icons.js", "app-agent-model.js", "app-questions.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8") });
    await page.evaluate(() => {
      state.activeChatId = "chat"; state.messages = [{}, { Role: "user", ProtocolMessage: true }];
      window.messageProtocolMessage = m => !!m.ProtocolMessage;
      window.$ = id => document.getElementById(id);
      window.updateComposerInputState = () => {};
      document.body.innerHTML = '<div id="questions"></div><form id="chatForm"><textarea id="chatInput"></textarea></form>';
      document.getElementById("chatInput").value = "Unsent draft";
      window.submitInboxMessage = async (...args) => { window.submittedAnswer = args; return true; };
      window.inboxOperationId = () => "answer-operation";
      appendQuestionCards(document.getElementById("questions"), {
        ToolId: "core.ask", ToolCallId: "ask", RunId: "run", Status: "waiting", DataJson: null
      }, { index: 0 });
    });
    await page.waitForFunction(() => requests.length === 6);
    assert.deepEqual(await page.evaluate(() => requests[5].payload), { chatId: "chat", runId: "run", toolCallId: "ask" });
    await page.evaluate(() => requests[5].resolve({ chatId: "chat", runId: "run", toolCallId: "ask", blocks: [],
      questions: null, questionsUnavailableReason: "Сохранённые вопросы недоступны." }));
    await page.getByText("Повторить загрузку", { exact: true }).waitFor();
    assert.match(await page.locator("#questions").textContent(), /Сохранённые вопросы недоступны/);
    await page.getByText("Повторить загрузку", { exact: true }).click();
    await page.waitForFunction(() => requests.length === 7);
    await page.evaluate(() => requests[6].resolve({ chatId: "chat", runId: "run", toolCallId: "ask", blocks: [], questions: {
      type: "rnassistant.questions", questionSetId: "set", questions: [{ id: "q1", header: "Scope",
        prompt: "<img src=x onerror=alert(1)> " + "Важный контекст. ".repeat(700), selection: "single", allowFreeText: true,
        options: [{ id: "a", label: "Core", description: "Core only" }, { id: "b", label: "UI", description: "Include UI" }] }]
    } }));
    await page.locator(".plan-question-cards").waitFor();
    assert.equal(await page.locator("#questions img").count(), 0, "question text is inert");
    assert.ok((await page.locator("#questions .question-prompt").textContent()).length > 8192,
      "externalized question body remains visible outside action details");
    await page.locator("#questions textarea").fill("Сначала Core");
    await page.locator("#questions input[value=a]").click();
    await page.locator(".plan-question-cards").waitFor({ state: "hidden" });
    const answer = await page.evaluate(() => submittedAnswer);
    assert.deepEqual(answer, [null, [], "chat", "Steer", { runId: "run", questionSetId: "set",
      answers: [{ questionId: "q1", optionIds: ["a"], freeText: "Сначала Core" }] }]);
    assert.equal(await page.locator("#chatInput").inputValue(), "Unsent draft");
    await page.evaluate(() => {
      const missing = document.createElement("div"); missing.id = "missing-question-source"; document.body.appendChild(missing);
      appendQuestionCards(missing, { ToolId: "core.ask", Status: "waiting" }, null);
    });
    assert.match(await page.locator("#missing-question-source").textContent(), /Не удалось определить источник вопросов/);
    assert.equal(await page.evaluate(() => requests.length), 7, "unaddressable questions show an error without guessing a source");
    await page.evaluate(() => {
      window.workspaceTarget = { workspaceId: "workspace", sessionId: "session" };
      window.RNAssistantChatInspector = { target: () => workspaceTarget };
      const details = document.createElement("details"); details.id = "workspace-result";
      details.innerHTML = '<summary>Шаг workspace</summary><div id="workspace-preview"></div>';
      document.body.appendChild(details);
      appendToolResultPreview(document.getElementById("workspace-preview"),
        { ToolCallId: "workspace-call", RunId: "workspace-run" }, { workspace: true }, details, renderPreview);
    });
    await page.locator("#workspace-result > summary").click();
    await page.waitForFunction(() => requests.length === 8);
    assert.deepEqual(await page.evaluate(() => [requests[7].type, requests[7].payload]),
      ["getProjectToolResultPresentation", { workspaceId: "workspace", sessionId: "session",
        runId: "workspace-run", toolCallId: "workspace-call" }]);
    await page.evaluate(() => requests[7].resolve({ workspaceId: "foreign", chatId: "session",
      runId: "workspace-run", toolCallId: "workspace-call", blocks: [] }));
    await page.locator("#workspace-preview").getByText("Не удалось загрузить представление · Повторить").click();
    await page.waitForFunction(() => requests.length === 9);
    await page.evaluate(() => requests[8].resolve({ workspaceId: "workspace", chatId: "session",
      runId: "workspace-run", toolCallId: "workspace-call", blocks: [
        { kind: "text", title: "Содержимое", text: "Workspace content", complete: true }] }));
    await page.locator("#workspace-preview").getByText("Workspace content").waitFor();
    await page.locator("#workspace-result > summary").click();
    await page.locator("#workspace-result > summary").click();
    await page.waitForFunction(() => requests.length === 10);
    await page.evaluate(() => {
      window.workspaceTarget = { owner: "plugin", workspaceId: "workspace", sessionId: "session" };
      requests[9].resolve({ workspaceId: "workspace", chatId: "session", runId: "workspace-run", toolCallId: "workspace-call",
        blocks: [{ kind: "text", title: "Late", text: "stale workspace result", complete: true }] });
    });
    assert.equal(await page.locator("#workspace-preview .tool-result-preview").count(), 0,
      "a late response cannot cross workspace/plugin ownership even for the same canonical IDs");
    console.log("PASS tool presentation: Office/workspace scope, lazy reads, cancellation, retry, revision race, inert blocks and retained questions");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
