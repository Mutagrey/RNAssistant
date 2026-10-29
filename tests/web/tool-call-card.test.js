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
    const page = await browser.newPage({ viewport: { width: 740, height: 700 } });
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style><main style="max-width:680px;margin:24px auto"><div id="feed"></div></main>`);
    await page.evaluate(() => {
      window.state = { activeChatId: "chat", chatProjectionRevisions: { chat: 1 } };
      window.requests = [];
      window.send = (type, payload) => {
        let resolve; const promise = new Promise(done => { resolve = done; });
        promise.requestId = String(requests.length);
        requests.push({ type, payload, resolve }); return promise;
      };
      window.cancelBridgeRequest = () => Promise.resolve();
      window.RNAssistantRunChanges = { renderInline(parent, changes) {
        const text = document.createElement("div"); text.textContent = changes.items[0].title; parent.appendChild(text);
      } };
    });
    for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-agent-data.js",
      "app-run-view-state.js", "app-agent-model.js", "app-tool-result-preview.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      const read = { Kind: "tool", RunId: "run", ToolCallId: "read", ToolId: "core.read", Status: "completed",
        Subtitle: "app.js, строки 81–120", Display: { Action: "Чтение", Operation: "Read" },
        ResultMessage: "Получен фрагмент исходного кода.",
        ResultSummary: { Kind: "resource-read", Representation: "source", ReturnedCharacters: 240, Complete: true },
        ArgumentsJson: '{"target":"app.js","startLine":81,"count":40}' };
      const write = { Kind: "tool", RunId: "run", ToolCallId: "write", ToolId: "core.write", Status: "completed",
        Subtitle: "app.js", Display: { Action: "Запись", Operation: "Write" },
        ResultMessage: "Записан новый файл.", ExecutionEvidence: { Effect: "VerifiedChange" },
        ArgumentsJson: '{"target":"app.js"}' };
      const search = { Kind: "tool", ToolId: "core.find", Status: "completed", Subtitle: "исходники",
        Display: { Action: "Поиск", Operation: "Search" },
        ResultSummary: { Kind: "resource-find", ReturnedItems: 2, Complete: true } };
      const failed = { Kind: "tool", RunId: "run", ToolCallId: "failed", ToolId: "custom.tool", Status: "failed", ErrorCode: "unexpected_failure",
        ResultMessage: "Файл занят другим процессом.", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" },
        Display: { Action: "Запись", Operation: "Write" } };
      [["read", read], ["write", write], ["search", search], ["failed", failed]].forEach(([id, activity]) => {
        const node = renderActivityNode(activity, false, false, { renderInlineArtifacts: false, terminal: true });
        node.id = id; document.getElementById("feed").appendChild(node);
      });
    });
    assert.match(await page.locator("#read > details > summary").innerText(), /Прочитано\s+app\.js, строки 81–120\s+исходный код · символов: 240/);
    assert.match(await page.locator("#write > details > summary").innerText(), /Записано\s+app\.js/);
    assert.doesNotMatch(await page.locator("#write > details > summary").innerText(), /Запись/);
    assert.match(await page.locator("#search > details > summary").innerText(), /Найдено\s+исходники\s+элементов: 2/);
    const otherOutcomes = await page.evaluate(() => [
      { ToolId: "core.delete", Display: { Action: "Удаление", Operation: "Delete" }, ExecutionEvidence: { Effect: "VerifiedChange" } },
      { ToolId: "core.export", Display: { Action: "Экспорт", Operation: "Export" }, ExecutionEvidence: { Effect: "VerifiedChange" } },
      { ToolId: "core.check", Display: { Action: "Проверка", Operation: "Check" } },
      { ToolId: "core.write", Display: { Action: "Запись", Operation: "Write" }, ExecutionEvidence: { Effect: "VerifiedNoChange" } }
    ].map(activity => {
      const row = renderActivityRow(Object.assign({ Kind: "tool", Status: "completed", Subtitle: "документ" }, activity), false, false, null);
      document.getElementById("feed").appendChild(row);
      return row.innerText.replace(/\s+/g, " ").trim();
    }));
    assert.deepEqual(otherOutcomes, ["Удалено документ", "Экспортировано документ", "Проверено документ", "Без изменений документ"]);
    assert.doesNotMatch(await page.locator("#write > details > summary").innerText(), /Изменения подтверждены/);
    assert.match(await page.locator("#failed > details > summary").innerText(), /Файл занят другим процессом/);
    assert.doesNotMatch(await page.locator("#failed > details > summary").innerText(), /Не удалось начать действие/);
    const rowGeometry = await page.locator("#read .agent-activity-row").evaluate(node => ({
      rowRight: node.getBoundingClientRect().right,
      feedRight: document.getElementById("feed").getBoundingClientRect().right,
      captionRight: node.querySelector(".agent-activity-caption").getBoundingClientRect().right,
      caretLeft: node.querySelector(".agent-activity-caret").getBoundingClientRect().left
    }));
    assert.ok(rowGeometry.rowRight < rowGeometry.feedRight - 20 && rowGeometry.caretLeft - rowGeometry.captionRight < 18,
      "the action and its arrow remain compact instead of stretching across the chat");
    await page.locator("#read > details > summary").click();
    await page.waitForFunction(() => requests.length === 1);
    assert.equal(await page.locator("#read .agent-activity-tool-id").count(), 0);
    assert.match(await page.locator("#read .agent-tool-card-header").innerText(), /core\.read · app\.js, строки 81–120/);
    await page.evaluate(() => requests[0].resolve({ chatId: "chat", runId: "run", toolCallId: "read",
      rawJson: JSON.stringify({ message: "Прочитано", text: "a".repeat(2000) }),
      blocks: [{ kind: "text", title: "Содержимое", text: "const x = 1;\nconst y = 2;", complete: true,
        source: { reference: "R1@v1", target: "app.js", line: 81 } }] }));
    await page.locator("#read .agent-tool-tab").getByText("JSON").waitFor();
    assert.match(await page.locator("#read .tool-source-caption").innerText(), /R1@v1 — app\.js, строка 81/,
      "the source reference remains visible for one read");
    assert.match(await page.locator("#read .agent-tool-panel:visible .agent-tool-message").innerText(), /Получен фрагмент исходного кода/);
    assert.equal(await page.locator("#read .agent-tool-panel:visible pre").textContent(), "const x = 1;\nconst y = 2;");
    const positions = await page.locator("#read .agent-tool-card-header").evaluate(node => ({
      receiptRight: node.querySelector(".agent-tool-receipt").getBoundingClientRect().right,
      tabsLeft: node.querySelector(".agent-tool-tabs").getBoundingClientRect().left
    }));
    assert.ok(positions.receiptRight <= positions.tabsLeft + 1, "tabs share the receipt row without overlap");
    if (process.env.TOOL_CARD_SCREENSHOT) await page.locator("#feed").screenshot({ path: process.env.TOOL_CARD_SCREENSHOT });
    await page.locator("#read .agent-tool-tab").getByText("Аргументы").click();
    assert.equal(await page.locator("#read .agent-tool-panel:visible .rn-json-viewer.is-compact").count(), 1);
    assert.equal(await page.locator("#read .agent-tool-panel:visible .rn-json-status:visible").count(), 0,
      "the clean JSON view has no node counter");
    await page.locator("#read .agent-tool-tab").getByText("JSON").click();
    if (process.env.TOOL_CARD_JSON_SCREENSHOT) await page.locator("#read .agent-tool-card").screenshot({ path: process.env.TOOL_CARD_JSON_SCREENSHOT });
    await page.locator("#read .agent-tool-panel:visible .agent-json-expand").click();
    assert.ok((await page.locator("#read .agent-tool-panel:visible .rn-json-text").textContent()).length > 2000,
      "the full-text action reveals the complete retained value");
    assert.equal(await page.locator("#read .agent-tool-panel.is-expanded-json:visible").count(), 1);
    await page.locator("#read .agent-tool-panel:visible .agent-json-expand").click();
    assert.equal(await page.locator("#read .agent-tool-panel:visible .rn-json-tree").count(), 1);
    await page.locator("#write > details > summary").click();
    await page.waitForFunction(() => requests.length === 2);
    await page.evaluate(() => requests[1].resolve({ chatId: "chat", runId: "run", toolCallId: "write",
      rawJson: '{"status":"ok"}',
      blocks: [{ kind: "text_changes", title: "Изменения", complete: true,
        changes: { complete: true, evidenceFound: true, items: [{ title: "app.js" }] } }] }));
    await page.locator("#write .agent-tool-tab").getByText("Изменения").waitFor();
    assert.equal(await page.locator("#write .agent-tool-tab:visible").count(), 3,
      "changes, arguments and raw JSON remain reachable");
    assert.equal(await page.locator("#write .agent-tool-tab.active").innerText(), "Изменения",
      "verified diff is the initial view for a mutation");
    assert.match(await page.locator("#write .agent-tool-panel:visible").textContent(), /app.js/);
    await page.locator("#failed > details > summary").click();
    await page.waitForFunction(() => requests.length === 3);
    await page.evaluate(() => requests[2].resolve({ chatId: "chat", runId: "run", toolCallId: "failed",
      rawJson: '{"code":"unexpected_failure"}', blocks: [] }));
    await page.locator("#failed .agent-tool-tab").getByText("JSON").waitFor();
    assert.equal(await page.locator("#failed .agent-tool-tab.active").innerText(), "Результат");
    assert.equal(await page.locator("#failed .agent-tool-panel:visible").innerText(), "Файл занят другим процессом.",
      "failure details open on the full tool message, with JSON available separately");
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 700 });
      assert.ok(await page.locator("#read .agent-tool-card").evaluate(node => node.scrollWidth <= node.clientWidth + 1),
        "card fits chat width " + width);
    }
    console.log("PASS tool card: compact tabs, visible result, exact JSON and narrow layout");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
