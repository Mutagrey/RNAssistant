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
    const page = await browser.newPage({ viewport: { width: 740, height: 920 } });
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style><main style="padding:24px;max-width:680px;margin:auto">
      <h3>Действия</h3><div id="actions"></div><button id="contextMeter" style="margin-top:20px">Контекст модели</button></main>`);
    await page.evaluate(() => {
      window.state = { activeChatId: "chat-a", messages: [] };
      window.$ = id => document.getElementById(id);
      window.RNAssistantAgentApproval = { create: () => ({ pendingActivity: () => null }) };
      window.currentActiveSend = () => null;
      window.hasActiveMessageEdit = () => false;
      window.canEditMessage = () => false;
      window.smallIconButton = (title, icon, handler) => {
        const button = document.createElement("button"); button.className = "message-action";
        button.title = title; button.textContent = icon === "copy" ? "▣" : icon === "branch" ? "⑂" : "×";
        button.addEventListener("click", handler); return button;
      };
      window.enhanceActivity = () => {};
      window.logOnce = () => {};
      window.appendMessageArtifactCards = () => {};
      window.setPromptContextInspectorOpen = open => { window.inspectorOpened = open; };
    });
    for (const file of ["vendor/purify.min.js", "vendor/marked.min.js", "vendor/highlight.min.js", "app-utils.js", "app-markdown.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js", "app-messages.js", "app-viewer-registry.js", "app-json-viewer.js", "app-agent-data.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      const fixtures = [
        { ToolId: "common.capabilities_read", Subtitle: "excel.write_range", ResultMessage: "Loaded catalogRevision=private-id", DataJson: '{"kind":"tool-schema","complete":true}' },
        { ToolId: "common.resources_find", Subtitle: "Таблицы с продажами · Документ", DataJson: '{"items":[{},{},{},{}],"complete":true}' },
        { ToolId: "common.resources_read", Subtitle: "Продажи!A1:D120 · Таблица", DataJson: JSON.stringify({ kind: "resource-read", table: { rows: Array.from({ length: 120 }, () => ({})) }, complete: true }) },
        { ToolId: "excel.write_range", Subtitle: "Продажи!B2:D121", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "VerifiedChange" } },
        { ToolId: "excel.add_sheet", Subtitle: "Отчёт", Status: "failed", ErrorCode: "excel_sheet_already_exists", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" }, ResultMessage: "Sheet already exists: raw error" },
        { ToolId: "common.office_run_macro", Subtitle: "ОбновитьОтчёт", Status: "failed", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "Unknown" } },
        { ToolId: "common.resources_find", Subtitle: "Архив отчётов", DataJson: '{"items":[],"complete":false,"partial":true}' },
        { ToolId: "common.resources_read", Subtitle: "Папка документа / Очень длинное название раздела с аналитикой продаж по регионам / Итоговые данные за последний квартал!A1:F180", Status: "running" }
      ];
      const displays = [
        ["Изучение", "Learn"], ["Поиск ресурсов", "Search"], ["Чтение ресурса", "Read"],
        ["Запись диапазона", "Write"], ["Создание листа", "Write"], ["Выполнение макроса", "Command"],
        ["Поиск ресурсов", "Search"], ["Чтение ресурса", "Read"]
      ];
      fixtures.forEach((item, i) => { item.Display = { action: displays[i][0], runningAction: displays[i][0], operation: displays[i][1] }; });
      for (const item of fixtures) document.getElementById("actions").appendChild(renderActivityNode(
        Object.assign({ Kind: "tool", Status: "completed", ArgumentsJson: '{"target":"exact model argument"}' }, item), false, false, null));
    });
    await page.evaluate(() => {
      const final = { Role: "assistant", Content: "Готово. Отчёт обновлён.", Id: "final", RunId: "r", RunViewState: {
        RunId: "r", TurnId: "r", Lifecycle: "completed", ExecutionHealth: "clean", Narrative: "Готово.",
        SuccessfulReads: 1, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0 } };
      const item = { message: { Role: "assistant", Id: "call", RunId: "r", Activity: { Kind: "tool", ToolId: "test.any",
        Status: "completed", Subtitle: "Отчёт", Display: { action: "Чтение", operation: "Read" } } }, index: 0 };
      item.activity = item.message.Activity;
      const run = renderAgentRunArticle({ items: [item], finalMessage: { message: final, index: 1 }, live: false });
      run.id = "full-run";
      document.querySelector("main").appendChild(run);
      for (const role of ["assistant", "user"]) {
        const node = document.createElement("article"); node.className = "message " + role; node.id = "footer-" + role;
        appendMessageFooter(node, { Role: role, Content: "Сообщение", Local: true, TotalTokens: 100 }, 0, null);
        document.querySelector("main").appendChild(node);
      }
    });
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 920 });
      const bounds = await page.locator("#actions .agent-activity-row").evaluateAll(rows => rows.map(row => ({
        width: row.clientWidth, scroll: row.scrollWidth, text: row.innerText,
        targetOverflow: getComputedStyle(row.querySelector(".agent-activity-target")).textOverflow,
        colors: [".agent-activity-name", ".agent-activity-target code", ".agent-activity-mark", ".agent-activity-caption"].map(selector => {
          const element = row.querySelector(selector); return element ? getComputedStyle(element).color : null;
        })
      })));
      assert.ok(bounds.every(row => row.scroll <= row.width + 1), "action rows fit the narrow chat");
      assert.ok(bounds.every(row => row.targetOverflow !== "ellipsis"), "semantic targets remain visible");
      assert.equal(new Set(bounds[0].colors).size, 1, "action, target, icon and outcome use one muted color");
      assert.notEqual(bounds[4].colors[3], bounds[4].colors[0], "error remains distinct");
      assert.ok(bounds[4].text.includes("excel_sheet_already_exists"), "failed action shows its stable error code inline");
      assert.equal(new Set(bounds[5].colors).size, 1, "unknown uses a symbol and text with neutral color");
      assert.ok(bounds[0].text.includes("Загружено") && !bounds[0].text.includes("private-id"));
      assert.ok(bounds[2].text.includes("Получено строк: 120"));
      assert.ok(bounds[7].text.includes("квартал!A1:F180"));
      const layout = await page.evaluate(() => {
        const box = selector => document.querySelector(selector).getBoundingClientRect();
        const full = document.getElementById("full-run");
        const footer = full.querySelector(".message-footer");
        const actions = footer.querySelector(".message-actions");
        const meta = footer.querySelector(".message-footer-meta");
        return { assistantLeft: Math.abs(actions.getBoundingClientRect().left - footer.getBoundingClientRect().left),
          metaAfter: meta.getBoundingClientRect().left >= actions.getBoundingClientRect().right,
          userRight: Math.abs(box("#footer-user .message-actions").right - box("#footer-user .message-footer").right),
          plainAssistantLeft: Math.abs(box("#footer-assistant .message-actions").left - box("#footer-assistant .message-footer").left),
          gap: box("#full-run .agent-final-step").top - box("#full-run .agent-run-actions").bottom,
          divider: getComputedStyle(full.querySelector(".agent-run-actions")).borderBottomWidth,
          copyFirst: actions.firstElementChild.title.includes("Копировать"),
          overflow: full.scrollWidth > full.clientWidth + 1 };
      });
      assert.ok(layout.assistantLeft < 1 && layout.plainAssistantLeft < 1 && layout.userRight < 1 && layout.metaAfter);
      assert.ok(layout.copyFirst && !layout.overflow);
      assert.ok(layout.gap >= 0 && layout.gap <= 8 && layout.divider === "1px");
    }
    // Exercise the shipped Markdown renderer, not an identity-string mock.
    await page.evaluate(() => {
      const content = "создай план: Пример плана\r\nсоздай лист\r\nсгенерируй данные\r\nпострой график\n\n- пункт один\n- пункт два\n\n```text\nстрока 1\nстрока 2\n```\n\n<img src=x onerror=alert(1)>";
      const user = renderMessageArticle({ Role: "user", Content: content, Local: true }, 0);
      user.id = "multiline-user"; document.querySelector("main").appendChild(user);
      const assistant = renderMessageArticle({ Role: "assistant", Content: "Первая строка\nпродолжение абзаца\n\nСледующий абзац", Local: true }, 1);
      assistant.id = "markdown-assistant"; document.querySelector("main").appendChild(assistant);
      const raw = "LLM request timed out after 300 seconds.\nEndpoint: https://example.invalid/" + "x".repeat(400) + "\nModel: test; MaxTokens: 32000";
      const view = { RunId: "failed-run", TurnId: "failed-run", Lifecycle: "failed", ExecutionHealth: "errors",
        Reason: "Provider", SuccessfulReads: 1, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 1, UnknownEffects: 0 };
      const activities = [
        { Kind: "step", StepId: "s1", StepMessage: "Создаю план и начинаю выполнение.", Status: "completed" },
        { Kind: "tool", ToolId: "custom.read", ToolCallId: "read-1", StepId: "s1", Status: "completed", Subtitle: "Отчёт", Display: { action: "Чтение", operation: "Read" }, ResultMessage: "Первая строка\nВторая строка" },
        { Kind: "tool", ToolId: "custom.read", ToolCallId: "read-2", StepId: "s1", Status: "completed", Subtitle: "Отчёт", Display: { action: "Чтение", operation: "Read" } },
        { Kind: "diagnostic", Status: "failed", ExecutionStatus: "Provider", ResultMessage: raw }
      ];
      const items = activities.map((activity, index) => ({ activity, index, message: {
        Role: "assistant", Id: "failed-" + index, RunId: "failed-run", RunViewState: view,
        Activity: activity, Content: activity.Kind === "diagnostic" ? raw : "" } }));
      const run = renderAgentRunArticle({ items }); run.id = "failed-run";
      document.querySelector("main").appendChild(run);
      const noCalls = renderAgentRunArticle({ items: [items[3]] }); noCalls.id = "failed-no-calls";
      document.querySelector("main").appendChild(noCalls);
      const bodyOnly = renderActivityNode({ Kind: "diagnostic", Status: "failed" }, false, false,
        { message: { Content: "Сохранено только в теле\nВторая строка" } });
      bodyOnly.id = "body-diagnostic"; document.querySelector("main").appendChild(bodyOnly);
      window.failedFixtures = { raw, items };
    });
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 1400 });
      assert.equal(await page.locator("#multiline-user .markdown p").first().locator("br").count(), 3);
      assert.equal(await page.locator("#multiline-user .markdown li").count(), 2);
      assert.equal(await page.locator("#multiline-user pre code").textContent(), "строка 1\nстрока 2\n");
      assert.equal(await page.locator("#multiline-user [onerror]").count(), 0, "Markdown remains sanitized");
      assert.equal(await page.locator("#markdown-assistant .markdown br").count(), 0, "model Markdown keeps normal soft breaks");
      assert.equal(await page.locator("#markdown-assistant .markdown p").count(), 2);
      assert.equal(await page.locator("#failed-run .agent-run-outcome, #failed-run .agent-diagnostic-message").count(), 0);
      assert.equal(await page.locator("#failed-no-calls .agent-run-overview").count(), 0);
      assert.equal(await page.locator("#failed-no-calls [data-runtime-health]").count(), 1, "zero actions still explains failure");
      assert.match(await page.locator("#failed-no-calls").innerText(), /Не удалось получить ответ/);
      await page.locator("#failed-run .agent-run-overview").evaluate(node => { node.open = true; });
      const visible = await page.locator("#failed-run").innerText();
      assert.ok(!visible.includes("Endpoint:") && !visible.includes("MaxTokens:"), "raw diagnostics stay out of the main transcript");
      assert.equal(await page.locator("#failed-run .kind-tool").count(), 2, "different call ids remain separate even with identical action/target");
      assert.equal(await page.locator("#failed-run .agent-step-message").count(), 1);
      const diagnostic = page.locator("#failed-run .kind-diagnostic details");
      await diagnostic.evaluate(node => { node.open = true; });
      assert.equal(await diagnostic.locator(".agent-activity-result").count(), 1, "same result/body is shown once");
      assert.equal(await diagnostic.locator(".agent-activity-result").textContent(), "Диагностика:\n" + await page.evaluate(() => failedFixtures.raw));
      assert.equal(await diagnostic.locator(".agent-activity-result").evaluate(node => getComputedStyle(node).whiteSpace), "pre-wrap");
      assert.ok(await diagnostic.evaluate(node => node.scrollWidth <= node.clientWidth + 1), "long diagnostic wraps in the narrow chat");
      await diagnostic.evaluate(node => { node.open = false; });
    }
    assert.equal(await page.locator("#body-diagnostic .agent-activity-result").count(), 1, "message-only diagnostic is retained in details");
    assert.deepEqual(await page.evaluate(() => activityDetailTexts({ Kind: "diagnostic", ResultMessage: "Причина" }, { message: { Content: "Пояснение" } })), ["Причина", "Пояснение"]);
    assert.equal(await page.evaluate(() => failedFixtures.items[3].message.Content === failedFixtures.raw && failedFixtures.items[3].activity.ResultMessage === failedFixtures.raw), true, "display never rewrites retained diagnostics");
    assert.match(await page.evaluate(() => RNAssistantRunViewState.failureReasonLabel("PromptBudgetExceeded")), /Контекст/);
    assert.ok(!(await page.evaluate(() => RNAssistantRunViewState.failureReasonLabel("Endpoint: private detail"))).includes("Endpoint:"));
    await page.locator("#failed-run .agent-run-overview").evaluate(node => { node.open = false; });
    await page.setViewportSize({ width: 740, height: 920 });
    await page.locator("#full-run").hover();
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT, fullPage: true });
    await page.locator(".agent-activity-row").first().click();
    await page.getByText("Что войдёт в следующий запрос модели", { exact: true }).first().click();
    assert.equal(await page.evaluate(() => window.inspectorOpened), true);
    assert.ok(await page.locator(".agent-activity-context-note").first().isVisible());
    await page.evaluate(() => { window.inspectorOpened = false; state.activeChatId = "chat-b"; });
    await page.getByText("Что войдёт в следующий запрос модели", { exact: true }).first().click();
    assert.equal(await page.evaluate(() => window.inspectorOpened), false, "stale detail cannot open another chat's context");
    console.log("PASS activity layout: action/target/result, Markdown line breaks, deduplicated diagnostics, narrow wrapping and addressed context link");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
