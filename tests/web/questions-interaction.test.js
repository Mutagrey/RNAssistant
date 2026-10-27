"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");

(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    server.stdout.on("data", chunk => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if (match) resolve(match[1]);
    });
    server.once("exit", code => reject(Error("Preview server exited: " + code)));
  });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 900 } }), errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      const original = send;
      window.questionTest = { calls: [], failNext: true, pending: true, generation: 1,
        set: { questionSetId: "set", questions: [
          { id: "format", header: "Формат", prompt: "В каком формате подготовить отчёт?", selection: "single", allowFreeText: false,
            options: [{ id: "md", label: "Markdown", description: "Удобно читать и редактировать", recommended: true },
              { id: "html", label: "HTML", description: "Открыть в браузере" }] },
          { id: "sections", header: "Разделы", prompt: "Что включить в отчёт?", selection: "multiple", allowFreeText: true,
            options: [{ id: "steps", label: "Шаги", description: "План действий" }, { id: "risks", label: "Риски", description: "Что требует внимания" }] }
        ] } };
      window.send = async function (type, payload) {
        let answerInput = null, omitReceipt = false, wrongChat = false;
        if (type === "submitProjectChat" && payload.input.answers) {
          questionTest.calls.push(payload);
          if (questionTest.failNext) { questionTest.failNext = false; throw new Error("Проверка: соединение прервано"); }
          if (questionTest.holdSubmit) await new Promise(resolve => { questionTest.releaseSubmit = resolve; });
          answerInput = payload.input;
          omitReceipt = !!questionTest.omitReceiptOnce;
          questionTest.omitReceiptOnce = false;
          wrongChat = !!questionTest.wrongChatOnce;
          questionTest.wrongChatOnce = false;
          questionTest.pending = omitReceipt || !!questionTest.keepPending;
          type = "getProjectChat";
        }
        const result = await original(type, payload);
        if (answerInput && !omitReceipt) result.inputReceipt = { SessionId: payload.sessionId,
          RequestId: answerInput.requestId, InputId: "answer-" + answerInput.requestId,
          RunId: "run-answer-" + answerInput.requestId, AcceptedCursor: "cursor-answer",
          AcceptedSequence: 1, DeliveryRecorded: true };
        if (answerInput && wrongChat) result.project.workspaceId = "wrong-workspace";
        if ((type === "getProjectChat" || type === "getPluginChat") && result.project && questionTest.pending)
          result.state.userInput = { runId: "run-" + questionTest.generation, questionSet: questionTest.set };
        return result;
      };
      return RNAssistantProjectChats.setScope("projects");
    });
    const form = page.locator("#workspaceChatQuestions .plan-question-cards");
    await form.waitFor();
    assert.equal(await form.locator(".question-card:visible").count(), 1, "only one question is shown");
    assert.equal(await form.locator(".question-counter").textContent(), "1 / 2");
    assert.equal(await form.locator(".question-previous").isDisabled(), true);
    assert.equal(await form.locator("input:checked").count(), 0, "recommendations never submit themselves");
    assert.equal(await form.locator("textarea").count(), 1, "free text respects the question contract");
    await form.locator('button[type="submit"]').click();
    await form.locator(".question-status.is-error").waitFor();
    assert.equal(await page.evaluate(() => questionTest.calls.length), 0, "blank form is not silently submitted");
    await form.locator('input[value="md"]').check();
    assert.equal(await form.locator(".question-counter").textContent(), "2 / 2", "single choice advances immediately");
    assert.equal(await page.evaluate(() => questionTest.calls.length), 0, "intermediate answers stay local");
    await form.locator(".question-previous").click();
    assert.equal(await form.locator('input[value="md"]').isChecked(), true, "back retains the selected answer");
    await form.locator('input[value="md"]').focus();
    await page.keyboard.press("ArrowRight");
    assert.equal(await form.locator(".question-counter").textContent(), "1 / 2", "radio keyboard navigation does not leave the question");
    await form.locator('input[value="md"]').click();
    await form.locator('input[value="steps"]').check();
    await form.locator('input[value="risks"]').check();
    assert.equal(await page.evaluate(() => questionTest.calls.length), 0, "multiple choice waits for Done");
    await form.locator("textarea").fill("С примерами");
    await page.evaluate(() => RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review"));
    assert.equal(await form.locator('input[value="md"]').isChecked(), true);
    assert.equal(await form.locator("textarea").inputValue(), "С примерами", "poll/reopen keeps the form draft");
    assert.equal(await form.locator(".question-counter").textContent(), "2 / 2", "redraw keeps the active page");
    await form.locator('button[type="submit"]').click();
    await form.getByText("Проверка: соединение прервано", { exact: true }).waitFor();
    assert.equal(await form.locator("textarea").inputValue(), "С примерами");
    assert.equal(await form.locator('button[type="submit"]').isDisabled(), false);
    await form.locator("textarea").press("Control+Enter");
    await page.waitForFunction(() => questionTest.calls.length === 2);
    const calls = await page.evaluate(() => questionTest.calls);
    assert.equal(calls[0].input.requestId, calls[1].input.requestId, "uncertain retries keep one operation identity");
    assert.deepEqual(calls[1].input.answers, { runId: "run-1", questionSetId: "set", answers: [
      { questionId: "format", optionIds: ["md"], freeText: "" },
      { questionId: "sections", optionIds: ["steps", "risks"], freeText: "С примерами" }
    ] });
    await page.locator("#workspaceChatQuestions").waitFor({ state: "hidden" });

    await page.evaluate(() => { questionTest.pending = true; questionTest.generation++;
      return RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review"); });
    await form.waitFor();
    await form.locator(".question-skip").first().click();
    await form.locator("textarea").fill("Только выводы");
    await form.locator('button[type="submit"]').click();
    await page.waitForFunction(() => questionTest.calls.length === 3);
    assert.deepEqual(await page.evaluate(() => questionTest.calls[2].input.answers.answers), [
      { questionId: "format", optionIds: [], freeText: "", skipped: true },
      { questionId: "sections", optionIds: [], freeText: "Только выводы" }
    ]);
    await page.locator("#workspaceChatQuestions").waitFor({ state: "hidden" });

    // Final single choice submits automatically, but the card closes only on acknowledgement.
    await page.evaluate(() => {
      questionTest.pending = true; questionTest.generation++;
      questionTest.set.questions[1].selection = "single";
      questionTest.holdSubmit = true; questionTest.keepPending = true;
      return RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review");
    });
    await form.locator(".question-next").click();
    await form.locator('input[value="steps"]').click();
    assert.equal(await form.locator(".question-counter").textContent(), "1 / 2", "final submit returns to an unanswered question");
    await form.locator(".question-status.is-error").waitFor();
    assert.equal(await page.evaluate(() => questionTest.calls.length), 3);
    await form.locator('input[value="html"]').click();
    await form.locator('input[value="steps"]').click();
    await page.waitForFunction(() => questionTest.calls.length === 4);
    assert.equal(await form.isVisible(), true, "unacknowledged submission keeps the card visible");
    assert.equal(await form.locator('button[type="submit"]').isDisabled(), true);
    await page.evaluate(() => questionTest.releaseSubmit());
    await page.locator("#workspaceChatQuestions").waitFor({ state: "hidden" });
    await page.evaluate(() => RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review"));
    assert.equal(await form.isVisible(), false, "stale pending snapshots cannot reopen an accepted card");

    await page.evaluate(() => { questionTest.pending = true; questionTest.generation++;
      questionTest.holdSubmit = false; questionTest.keepPending = false;
      questionTest.set.questions[1].selection = "multiple";
      return RNAssistantPlugins.select("data-templates"); });
    await form.waitFor();
    assert.equal(await form.locator("input:checked").count(), 0, "another owner never adopts the project answer");
    assert.equal(await form.locator("textarea").inputValue(), "");
    await form.getByRole("button", { name: "Пропустить всё", exact: true }).click();
    await page.waitForFunction(() => questionTest.calls.length === 5);
    assert.equal(await page.evaluate(() => questionTest.calls[4].workspaceId), "preview-plugin-workspace");
    assert.ok((await page.evaluate(() => questionTest.calls[4].input.answers.answers)).every(a => a.skipped && !a.optionIds.length && !a.freeText));

    await page.evaluate(async () => {
      RNAssistantProjectChats.setScope("projects");
      questionTest.pending = true; questionTest.generation++; questionTest.omitReceiptOnce = true;
      await RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review");
    });
    await form.waitFor();
    await form.getByRole("button", { name: "Пропустить всё", exact: true }).click();
    await form.getByText("Доставка сообщения не подтверждена. Черновик сохранён.", { exact: true }).waitFor();
    assert.equal(await form.isVisible(), true, "a successful transport reply without receipt keeps question answers");
    await form.getByRole("button", { name: "Пропустить всё", exact: true }).click();
    await page.waitForFunction(() => questionTest.calls.length === 7);
    const answerRetry = await page.evaluate(() => questionTest.calls.slice(-2));
    assert.equal(answerRetry[0].input.requestId, answerRetry[1].input.requestId,
      "missing answer receipt retries with the same operation ID");
    await page.locator("#workspaceChatQuestions").waitFor({ state: "hidden" });

    await page.evaluate(async () => {
      questionTest.pending = true; questionTest.generation++; questionTest.wrongChatOnce = true;
      await RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review");
    });
    await form.waitFor();
    await form.getByRole("button", { name: "Пропустить всё", exact: true }).click();
    await form.getByText("Ответ получен для другого чата. Обновите беседу перед повтором.", { exact: true }).waitFor();
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId), "preview-project-review",
      "wrong-chat response cannot replace the selected conversation");
    await form.getByRole("button", { name: "Пропустить всё", exact: true }).click();
    await page.waitForFunction(() => questionTest.calls.length === 9);
    const addressedRetry = await page.evaluate(() => questionTest.calls.slice(-2));
    assert.equal(addressedRetry[0].input.requestId, addressedRetry[1].input.requestId,
      "wrong-chat response preserves the answer operation ID");
    await page.locator("#workspaceChatQuestions").waitFor({ state: "hidden" });

    // Use the actual Office transcript and inbox, including the render during intake.
    await page.evaluate(() => RNAssistantProjectChats.setScope("office"));
    await page.locator('.rail-button[data-tab="chat"]').click();
    await page.evaluate(() => {
      window.questionTest.officeCalls = [];
      questionTest.set.questions[0].allowFreeText = true;
      const original = send;
      window.send = async function (type, payload) {
        if (type === "getToolResultPresentation") return { chatId: state.activeChatId, runId: "office-run", toolCallId: "ask", blocks: [], questions: questionTest.set };
        if (type === "getRunChanges") return { chatId: payload.chatId, runId: payload.runId, complete: true, items: [] };
        if (type === "submitChatInput" && payload.answers) {
          questionTest.officeCalls.push(payload);
          await new Promise(resolve => setTimeout(resolve, 100));
          return { chatId: payload.chatId, epoch: "question-test", revision: 1, running: false, items: [],
            acceptedInput: { Id: "answer", OperationId: payload.operationId, Status: "Applied", Revision: 1, Text: "Ответ принят" } };
        }
        return original(type, payload);
      };
      state.messages = [
        { Id: "request", Role: "user", Content: "Подготовь отчёт по продажам за сентябрь." },
        { Id: "intro", Role: "assistant", RunId: "office-run", Content: "Уточню формат и содержание отчёта." },
        { Id: "question", Role: "assistant", RunId: "office-run", Content: "",
        RunViewState: { RunId: "office-run", TurnId: "turn", Lifecycle: "awaiting_user", ExecutionHealth: "clean", GoalVerification: "not_assessed",
          SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0, Reason: "awaiting_user" },
        Activity: { RunId: "office-run", ToolId: "common.questions_ask", ToolCallId: "ask", Kind: "tool", Status: "waiting", Title: "Уточнение задачи" } }];
      state.messages[1].RunViewState = state.messages[2].RunViewState;
      renderMessages();
    });
    const office = page.locator("#messages .plan-question-cards");
    await office.waitFor();
    await page.locator("#chatInput").fill("Черновик следующего сообщения");
    await office.locator('input[value="html"]').check();
    await office.locator("textarea:visible").fill("С примерами и краткими выводами");
    await page.evaluate(() => { state.messages.find(item => item.Id === "question").Activity.Subtitle = "Обновлено"; renderMessages(); });
    await office.waitFor();
    assert.equal(await office.locator('input[value="html"]').isChecked(), true, "Office redraw retains the selected answer");
    assert.equal(await office.locator("textarea:visible").inputValue(), "С примерами и краткими выводами");
    await office.locator(".question-previous").click();
    const colors = [];
    for (const theme of ["light", "dark"]) {
      await page.evaluate(value => applyUiTheme({ UiTheme: value }, false), theme);
      const metrics = await office.locator(".question-option:visible").first().evaluate(node => {
        const css = getComputedStyle(node), radio = node.querySelector("input").getBoundingClientRect();
        const panel = node.closest(".plan-question-cards");
        return { background: getComputedStyle(panel).backgroundColor, panelHeight: panel.getBoundingClientRect().height,
          radius: getComputedStyle(panel).borderRadius, expectedRadius: getComputedStyle(panel).getPropertyValue("--radius-lg").trim(),
          rowHeight: node.getBoundingClientRect().height, display: css.display, width: radio.width, height: radio.height };
      });
      assert.notEqual(metrics.background, "rgba(0, 0, 0, 0)");
      assert.equal(metrics.display, "flex"); assert.equal(metrics.width, 18); assert.equal(metrics.height, 18);
      assert.equal(metrics.radius, metrics.expectedRadius, "outer rounding uses the demo token");
      assert.ok(metrics.rowHeight <= 56 && metrics.panelHeight <= 360, "one compact rounded card");
      colors.push(metrics.background);
      if (process.env.QUESTION_SCREENSHOTS) await page.locator("#chatDropZone").screenshot({ animations: "disabled", path: path.join(process.env.QUESTION_SCREENSHOTS, "questions-" + theme + ".png") });
    }
    assert.notEqual(colors[0], colors[1], "cards use the active theme");
    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(await office.evaluate(node => node.scrollWidth <= node.clientWidth + 1), "question form fits a narrow chat");
    if (process.env.QUESTION_SCREENSHOTS) await office.screenshot({ animations: "disabled", path: path.join(process.env.QUESTION_SCREENSHOTS, "questions-narrow.png") });
    await office.locator(".question-next").click();
    await office.locator("textarea:visible").fill("<img src=x onerror=alert(1)> Свой ответ");
    await office.locator('button[type="submit"]').click();
    await page.waitForFunction(() => questionTest.officeCalls.length === 1);
    await page.waitForFunction(() => !isPendingChatSubmit("preview-chat") &&
      chatInputEchoes("preview-chat").some(item => item.Id === "answer" && item.Status === "Applied"));
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик следующего сообщения");
    assert.deepEqual(await page.evaluate(() => questionTest.officeCalls[0].answers.answers), [
      { questionId: "format", optionIds: ["html"], freeText: "" },
      { questionId: "sections", optionIds: [], freeText: "<img src=x onerror=alert(1)> Свой ответ" }
    ]);
    assert.equal(await office.locator("img").count(), 0);
    assert.deepEqual(errors, []);
    console.log("PASS questions: pagination, auto advance, confirmed close, retries, skips, owner isolation, Office intake, drafts, themes and narrow layout");
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
