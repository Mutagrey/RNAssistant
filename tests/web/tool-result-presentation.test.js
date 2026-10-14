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
      appendToolResultPreview(document.getElementById("preview"), { ToolCallId: "call", RunId: "run" }, null, document.getElementById("step"));
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
    await page.locator("#step > summary").click();
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 5);
    await page.evaluate(() => { state.activeChatId = "other"; document.getElementById("step").remove(); });
    await page.waitForFunction(() => cancelled.includes("4"));
    await page.evaluate(() => requests[4].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [{ kind: "text", title: "Late", text: "foreign", complete: true }] }));
    assert.equal(await page.locator(".tool-result-preview").count(), 0, "removed card never receives foreign chat delivery");
    for (const file of ["app-agent-model.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8") });
    await page.evaluate(() => {
      state.activeChatId = "chat"; state.messages = [];
      window.$ = id => document.getElementById(id);
      window.updateComposerInputState = () => {};
      document.body.innerHTML = '<div id="questions"></div><form id="chatForm"><textarea id="chatInput"></textarea></form>';
      document.getElementById("chatForm").addEventListener("submit", event => {
        event.preventDefault(); window.submittedAnswer = document.getElementById("chatInput").value;
      });
      appendQuestionCards(document.getElementById("questions"), {
        ToolId: "common.questions_ask", ToolCallId: "ask", RunId: "run", Status: "waiting", DataJson: null
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
    assert.ok((await page.locator("#questions fieldset p").textContent()).length > 8192,
      "externalized question body remains visible outside action details");
    await page.locator("#questions input[value=a]").check();
    await page.locator("#questions textarea").fill("Сначала Core");
    await page.getByRole("button", { name: "Ответить", exact: true }).click();
    const answer = await page.evaluate(() => submittedAnswer);
    assert.match(answer, /^PLAN_ANSWERS:/);
    assert.deepEqual(JSON.parse(answer.split("\n")[1]).answers[0].selections, ["Core"]);
    assert.doesNotMatch(answer, /questionSetId|optionIds|questionId/);
    await page.evaluate(() => {
      const missing = document.createElement("div"); missing.id = "missing-question-source"; document.body.appendChild(missing);
      appendQuestionCards(missing, { ToolId: "common.questions_ask", Status: "waiting" }, null);
    });
    assert.match(await page.locator("#missing-question-source").textContent(), /Не удалось определить источник вопросов/);
    assert.equal(await page.evaluate(() => requests.length), 7, "unaddressable questions show an error without guessing a source");
    console.log("PASS tool presentation: exact addressing, lazy reads, cancellation, retry, revision race, inert blocks and retained questions");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
