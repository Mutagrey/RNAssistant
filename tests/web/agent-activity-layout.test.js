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
        const button = document.createElement("button"); button.className = "message-action icon-btn";
        button.title = title; button.textContent = icon === "copy" ? "▣" : icon === "branch" ? "⑂" : "×";
        button.addEventListener("click", handler); return button;
      };
      window.enhanceActivity = () => {};
      window.logOnce = () => {};
      window.appendMessageArtifactCards = () => {};
      window.setPromptContextInspectorOpen = open => { window.inspectorOpened = open; };
    });
    for (const file of ["vendor/purify.min.js", "vendor/marked.min.js", "vendor/highlight.min.js", "app-utils.js", "app-markdown.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js", "app-messages.js", "app-viewer-registry.js", "app-json-viewer.js", "app-agent-data.js", "app-text-diff.js", "app-run-changes.js", "app-tool-result-preview.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      const fixtures = [
        { ToolId: "common.capabilities_read", Subtitle: "excel.write_range", ResultMessage: "Loaded catalogRevision=private-id", ResultSummary: { Kind: "capability-read", Representation: "tool-schema", Complete: true } },
        { ToolId: "common.resources_find", Subtitle: "Таблицы с продажами · Документ", ResultSummary: { Kind: "resource-find", ReturnedItems: 4, Complete: true } },
        { ToolId: "common.resources_read", Subtitle: "Продажи!A1:D120 · Таблица", ResultSummary: { Kind: "resource-read", Representation: "table", ReturnedRows: 120, Complete: true } },
        { ToolId: "excel.write_range", Subtitle: "Продажи!B2:D121", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "VerifiedChange" } },
        { ToolId: "excel.add_sheet", Subtitle: "Отчёт", Status: "failed", ErrorCode: "excel_sheet_already_exists", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" }, ResultMessage: "Sheet already exists: raw error" },
        { ToolId: "common.office_run_macro", Subtitle: "ОбновитьОтчёт", Status: "failed", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "Unknown" } },
        { ToolId: "common.resources_find", Subtitle: "Архив отчётов", ResultSummary: { Kind: "resource-find", ReturnedItems: 0, Complete: false, Partial: true } },
        { ToolId: "common.resources_read", Subtitle: "Папка документа / Очень длинное название раздела с аналитикой продаж по регионам / Итоговые данные за последний квартал!A1:F180", Status: "running" },
        { ToolId: "common.resources_read", Subtitle: "Закрытый ресурс", Status: "failed", ErrorCode: "resource_access_denied", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" } }
      ];
      const displays = [
        ["Изучение", "Learn"], ["Поиск ресурсов", "Search"], ["Чтение ресурса", "Read"],
        ["Запись диапазона", "Write"], ["Создание листа", "Write"], ["Выполнение макроса", "Command"],
        ["Поиск ресурсов", "Search"], ["Чтение ресурса", "Read"], ["Чтение ресурса", "Read"]
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
        targetOverflow: getComputedStyle(row.closest(".agent-activity").querySelector(".agent-activity-target")).textOverflow,
        colors: [".agent-activity-name", ".agent-activity-mark", ".agent-activity-caption"].map(selector => {
          const element = row.querySelector(selector); return element ? getComputedStyle(element).color : null;
        })
      })));
      assert.ok(bounds.every(row => row.scroll <= row.width + 1), "action rows fit the narrow chat");
      assert.ok(bounds.every(row => row.targetOverflow !== "ellipsis"), "semantic targets remain visible");
      assert.equal(new Set(bounds[0].colors).size, 1, "action, target, icon and outcome use one muted color");
      assert.notEqual(bounds[4].colors[2], bounds[4].colors[0], "error remains distinct");
      assert.ok(bounds[4].text.includes("Лист уже существует"), "failed action shows its readable outcome");
      assert.notEqual(bounds[5].colors[2], bounds[5].colors[0], "unknown effect stands out from the muted action");
      assert.equal(bounds[5].colors[2], bounds[4].colors[2], "unknown effect and correctable conflicts use the warning color");
      assert.notEqual(bounds[5].colors[2], bounds[8].colors[2], "unknown effect is distinct from a hard failure");
      assert.equal(bounds[6].colors[2], bounds[5].colors[2], "incomplete reads use the same warning color");
      assert.ok(bounds[0].text.includes("Загружено") && !bounds[0].text.includes("private-id"));
      assert.ok(bounds[2].text.includes("Получено строк: 120"));
      assert.ok((await page.locator("#actions .agent-activity-target").nth(7).textContent()).includes("квартал!A1:F180"));
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
      assert.ok(layout.gap >= 0 && layout.gap <= 8 && layout.divider === "0px");
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
      assert.equal(await page.locator("#failed-run .agent-step-message:visible").count(), 1,
        "a failed run without a final answer retains its commentary");
      assert.equal(await page.locator("#failed-run .agent-run-overview").count(), 0);
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
    await page.setViewportSize({ width: 740, height: 920 });
    await page.locator("#full-run").hover();
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT, fullPage: true });
    await page.locator(".agent-activity-row").first().click();
    assert.equal(await page.getByText("Что войдёт в следующий запрос модели", { exact: true }).count(), 0);
    assert.equal(await page.locator(".agent-activity-context-note").count(), 0);
    assert.equal(await page.locator("#actions .agent-activity-tool-id").first().textContent(), "common.capabilities_read");
    assert.match(await page.locator("#actions").textContent(), /Код ошибки: excel_sheet_already_exists/);
    assert.equal(await page.locator("#actions .agent-activity-result").first().textContent(), "Loaded catalogRevision=private-id");
    await page.evaluate(() => {
      const host = document.createElement("div"); host.id = "previews"; document.querySelector("main").appendChild(host);
      RNAssistantToolResultPresentation.render(host, { blocks: [
        { kind: "list", title: "Список", complete: false, items: Array.from({ length: 45 }, (_, i) => ({ title: "Запись " + i, detail: "<img src=x onerror=alert(1)>" })) },
        { kind: "table", title: "Таблица", complete: true, columns: ["Значение"], rows: [["ДлинноеЗначение".repeat(60)]] },
        { kind: "text_changes", title: "Изменения этого шага", complete: true, changes: { toolCallId: "call", complete: true, evidenceFound: true, items: [
          { title: "Module1", before: "old\n", after: "new\n", beforeExists: true, afterExists: true, availability: "available" }
        ] } }
      ] });
    });
    assert.equal(await page.locator("#previews > details").count(), 3, "typed blocks select independent renderers");
    assert.equal(await page.locator("#previews .tool-result-item").count(), 20);
    await page.locator("#previews button").first().click();
    assert.equal(await page.locator("#previews .tool-result-item").count(), 40);
    await page.locator("#previews .tool-result-item summary").first().click();
    assert.equal(await page.locator("#previews img").count(), 0, "preview strings remain inert");
    assert.match(await page.locator("#previews").innerText(), /Показана доступная часть/);
    await page.locator("#previews .run-change-file-summary").click();
    await page.locator("#previews .vba-diff-line.add").waitFor();
    assert.equal(await page.locator("#previews .vba-diff-line.add").count(), 1);
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => document.documentElement.dataset.theme = theme, theme);
      await page.setViewportSize({ width: 360, height: 920 });
      assert.ok(await page.locator("#previews").evaluate(el => el.scrollWidth <= el.clientWidth + 1), "previews fit narrow " + theme + " theme");
    }
    await page.evaluate(() => {
      const view = { RunId: "sparse", TurnId: "sparse", Lifecycle: "running", ExecutionHealth: "unknown",
        SuccessfulReads: 2, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 1, FailedCalls: 1, UnknownEffects: 1 };
      const states = [
        { Status: "completed" }, { Status: "completed" },
        { Status: "completed", ExecutionEvidence: { Effect: "Unknown" } },
        { Status: "failed" }, { Status: "waiting" }, { Status: "running" }
      ];
      window.sparseItems = states.map((extra, index) => {
        const activity = Object.assign({ Kind: "tool", RunId: "sparse", StepId: "sparse-" + index,
          ToolId: "common.resources_read", ToolCallId: "sparse-call-" + index,
          Subtitle: "Источник " + index, StepMessage: index === 0 ? "Проверю исходные данные перед изменением." : "",
          Display: { action: "Чтение", running: "Читаю", operation: "Read" } }, extra);
        return { activity, index, message: { Id: "sparse-message-" + index, Role: "assistant",
          RunId: "sparse", RunViewState: view, Activity: activity, Content: "" } };
      });
      const run = renderAgentRunArticle({ live: true, items: sparseItems }); run.id = "sparse-run";
      document.querySelector("main").appendChild(run);
    });
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 920 });
      const group = page.locator("#sparse-run .agent-completed-actions");
      await group.evaluate(node => { node.open = false; });
      assert.equal(await page.locator("#sparse-run .agent-step-message:visible").count(), 1);
      assert.match(await group.locator(":scope > summary").innerText(), /Есть замечания/);
      assert.equal(await group.locator(".kind-tool:visible").count(), 0, "completed calls are hidden until expanded");
      assert.equal(await page.locator("#sparse-run .kind-tool:visible").count(), 2, "waiting and running remain visible outside the manually folded stage");
      assert.equal(await page.locator("#sparse-run .is-live-current:visible").count(), 1);
      await group.locator(":scope > summary").focus();
      await page.keyboard.press("Enter");
      assert.equal(await page.locator("#sparse-run .kind-tool:visible").count(), 6, "keyboard expansion reveals every original call");
      await page.evaluate(() => {
        const previous = document.getElementById("sparse-run");
        const snapshot = messageDisclosureSnapshot(previous);
        // The saved running lifecycle must render the same group after reload.
        const restored = renderAgentRunArticle({ live: false, items: sparseItems }); restored.id = "sparse-run";
        restoreMessageDisclosures(restored, snapshot); previous.replaceWith(restored);
      });
      assert.equal(await page.locator("#sparse-run .kind-tool:visible").count(), 6, "refresh preserves explicit expansion");
      assert.ok(await page.locator("#sparse-run").evaluate(node => node.scrollWidth <= node.clientWidth + 1), "group fits narrow chat");
    }
    assert.equal(await page.locator("#sparse-run .agent-status-shimmer").evaluate(node => getComputedStyle(node).animationName), "agent-text-shimmer");
    assert.equal(await page.locator("#sparse-run .agent-work-spinner").evaluate(node => getComputedStyle(node).animationName), "agent-work-spin");
    await page.emulateMedia({ reducedMotion: "reduce" });
    for (const selector of [".agent-status-shimmer", ".agent-work-spinner"]) {
      assert.equal(await page.locator("#sparse-run " + selector).evaluate(node => getComputedStyle(node).animationName), "none");
    }
    await page.emulateMedia({ reducedMotion: "no-preference" });
    await page.evaluate(() => {
      const items = JSON.parse(JSON.stringify(sparseItems));
      items[items.length - 1].activity.StepMessage = items[0].activity.StepMessage;
      items.forEach(item => { item.activity.Status = "completed"; item.message.RunViewState.Lifecycle = "completed"; });
      const finalMessage = { index: items.length, message: { Id: "sparse-final", Role: "assistant", RunId: "sparse",
        Content: "Проверка завершена.", RunViewState: items[0].message.RunViewState } };
      window.savedCommentRun = { items, finalMessage };
      const previous = document.getElementById("sparse-run");
      const completed = renderAgentRunArticle(savedCommentRun); completed.id = "sparse-run";
      previous.replaceWith(completed);
    });
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 920 });
      const run = page.locator("#sparse-run");
      assert.equal(await run.locator(".agent-step-message:visible").count(), 0,
        "completion folds progress comments and actions together");
      assert.equal(await run.locator(".agent-run-final:visible").count(), 1);
      assert.equal(await run.locator(".kind-tool:visible").count(), 0);
      await run.locator(".agent-run-overview > summary").click();
      await run.locator(".agent-run-history-content").waitFor({ state: "visible" });
      await run.locator(".agent-completed-actions").evaluateAll(nodes => nodes.forEach(node => { node.open = true; }));
      assert.equal(await run.locator(".kind-tool:visible").count(), 6);
      assert.equal(await run.locator(".agent-step-message:visible").count(), 2,
        "expanded history retains each original comment with its own step");
      assert.ok(await run.evaluate(node => node.scrollWidth <= node.clientWidth + 1), "saved comments fit narrow chat");
      await page.evaluate(() => {
        const previous = document.getElementById("sparse-run");
        const restored = renderAgentRunArticle(JSON.parse(JSON.stringify(savedCommentRun))); restored.id = "sparse-run";
        previous.replaceWith(restored);
      });
      assert.equal(await run.locator(".agent-step-message:visible").count(), 0, "reload keeps progress and actions folded together");
    }
    // Real DOM reconciliation must retain nested expansions and keyboard focus.
    await page.evaluate(() => {
      const host = document.createElement("div"); host.id = "disclosure-refresh";
      document.querySelector("main").appendChild(host);
      savedCommentRun.items[0].activity.ArgumentsJson = '{"target":"sales"}';
      window.refreshDisclosureFixture = signature => reconcileMessageUnits(host, [{ key: "focus-run", signature,
        render: () => renderAgentRunArticle(savedCommentRun) }], false);
      renderedMessageUnits = {};
      refreshDisclosureFixture("before");
    });
    const refreshed = page.locator("#disclosure-refresh");
    await refreshed.locator(".agent-run-overview > summary").click();
    await refreshed.locator(".agent-run-history-content").waitFor({ state: "visible" });
    await refreshed.locator(".agent-completed-actions > summary").first().click();
    const actionSummary = refreshed.locator(".agent-activity-toggle > summary").first();
    for (const summary of [refreshed.locator('.agent-completed-actions > summary').first(), actionSummary]) {
      assert.ok(await summary.evaluate(node => {
        const caret = node.querySelector('.agent-run-history-caret, .agent-activity-caret');
        return caret && node.getBoundingClientRect().right - caret.getBoundingClientRect().right >= 7;
      }), 'hover area extends beyond the disclosure arrow');
    }
    await actionSummary.click();
    await actionSummary.focus();
    await page.evaluate(() => refreshDisclosureFixture("after"));
    assert.equal(await refreshed.locator(".agent-run-overview").evaluate(node => node.open), true);
    assert.equal(await refreshed.locator(".agent-completed-actions").first().evaluate(node => node.open), true);
    assert.equal(await refreshed.locator(".agent-activity-toggle").first().evaluate(node => node.open), true);
    assert.equal(await refreshed.locator(".agent-activity-toggle > summary").first().evaluate(node => document.activeElement === node), true,
      "stream refresh restores focused action without scrolling");
    await page.evaluate(async () => {
      window.copyTextResult = () => Promise.resolve();
      await copyChatMessage("Ответ");
    });
    assert.equal(await page.locator("#chatCopyFeedback").getAttribute("role"), "status");
    assert.match(await page.locator("#chatCopyFeedback").innerText(), /скопировано/);
    await page.evaluate(async () => {
      window.copyTextResult = () => Promise.reject(new Error("clipboard unavailable"));
      await copyChatMessage("Ответ");
    });
    assert.equal(await page.locator("#chatCopyFeedback").getAttribute("role"), "alert");
    await page.locator("#chatCopyFeedback button").click();
    assert.equal(await page.locator("#chatCopyFeedback").isVisible(), false);
    console.log("PASS activity layout: action/target/result, Markdown, diagnostics, narrow wrapping and complete step disclosures");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
