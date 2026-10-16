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
    const page = await browser.newPage({ viewport: { width: 1160, height: 940 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    const index = read("index.html"), css = [...index.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)].map(match => read(match[1])).join("\n");
    const section = index.match(/<section class="panel" id="tab-workspace">[\s\S]*?<\/section>/)[0].replace('class="panel"', 'class="panel active"');
    await page.setContent(`<html lang="ru" data-theme="light"><style>${css}</style><div class="shell"><main class="content">${section}</main></div></html>`);
    await page.addScriptTag({ path: path.join(root, "js/app-accent.js") });
    await page.evaluate(() => {
      window.calls = [];
      window.nativeState = { type: "rnassistant.workspaceState", contractVersion: 1, revision: 2, sourceQueueId: null,
        workspace: { bindingId: "binding-a", workspaceId: "ws-a", rootPath: "C:\\Reports", readOnly: true },
        project: { name: "Отчётность", manifestPath: "project.json", formatVersion: 2, sources: [] } };
      window.q = null; window.losePrepare = false; window.malformed = false; window.holdBrowse = false;
      window.loseSave = false; window.invalidSave = false; window.savedProject = null;
      window.catalog = Array.from({ length: 61 }, (_, i) => ({ name: "Отчёт-" + String(i).padStart(2, "0") + ".xlsb",
        path: "reports/Отчёт-" + String(i).padStart(2, "0") + ".xlsb", isDirectory: false, byteLength: 80 * 1048576 }));
      catalog[1].name = '<img onerror="alert(1)">.json'; catalog[1].path = "reports/" + catalog[1].name;
      window.projectPage = payload => {
        let items = q.items.filter(item => (!payload.state || item.state === payload.state) && (!payload.query || item.path.includes(payload.query)));
        if (payload.sort === "Path") items = [...items].sort((a, b) => a.path.localeCompare(b.path));
        const offset = items.length ? Math.min(payload.offset || 0, Math.floor((items.length - 1) / 50) * 50) : 0;
        return { type: "rnassistant.workspaceSources", contractVersion: 1, queueId: q.id, revision: q.revision,
          bindingId: nativeState.workspace.bindingId, projectRevision: nativeState.revision, manifestPath: nativeState.project.manifestPath,
          state: q.state, total: q.items.length,
          ...Object.fromEntries(["Pending", "Running", "Prepared", "Failed", "Cancelled"].map(state => [state.toLowerCase(), q.items.filter(item => item.state === state).length])),
          capturedBytes: q.items.filter(item => item.state === "Prepared").length * 80 * 1048576,
          maximumFileBytes: q.maximumFileBytes, maximumBatchBytes: q.maximumBatchBytes,
          errorCode: null, errorMessage: null, currentItem: q.items.find(item => item.state === "Running") || null,
          matched: items.length, offset: offset, items: items.slice(offset, offset + 50) };
      };
      window.send = async (type, payload) => {
        calls.push({ type, payload: structuredClone(payload) });
        if (type === "getWorkspaceState") return structuredClone(nativeState);
        if (type === "saveTabularProjectSources") {
          const previous = new Map(nativeState.project.sources.map(source => [source.path, source.sourceId]));
          nativeState.project.sources = payload.paths.map((path, i) => ({ path,
            sourceId: previous.get(path) || "src_" + (i + 1).toString(16).padStart(32, "0") }));
          nativeState.revision++; nativeState.sourceQueueId = null;
          savedProject = structuredClone(nativeState.project);
          if (loseSave) { loseSave = false; throw Object.assign(new Error("Ответ записи потерян"), { code: "bridge_response_timeout" }); }
          const result = structuredClone(nativeState);
          if (invalidSave) { invalidSave = false; result.project.sources = []; }
          return result;
        }
        if (type === "reconcileWorkspaceFile") {
          nativeState.project = null; nativeState.revision++;
          return { state: structuredClone(nativeState), exists: true, outcome: "AlreadyPublished" };
        }
        if (type === "openTabularProject") {
          nativeState.project = structuredClone(savedProject); nativeState.revision++;
          return structuredClone(nativeState);
        }
        if (type === "browseWorkspaceFiles") {
          const all = payload.directory ? catalog.filter(item => !payload.query || item.name.includes(payload.query))
            : [{ name: "reports", path: "reports", isDirectory: true, byteLength: null }];
          const result = { type: "rnassistant.workspaceFiles", contractVersion: 1, bindingId: nativeState.workspace.bindingId,
            directory: payload.directory, offset: payload.offset || 0, matched: all.length, scanLimitReached: false,
            items: all.slice(payload.offset || 0, (payload.offset || 0) + 50) };
          if (holdBrowse) { holdBrowse = false; return new Promise(resolve => { window.releaseBrowse = () => resolve(result); }); }
          return structuredClone(result);
        }
        if (type === "prepareWorkspaceSources") {
          q = { id: "queue-" + calls.length, revision: 1, state: "Running",
            maximumFileBytes: payload.maximumFileBytes, maximumBatchBytes: payload.maximumBatchBytes,
            items: payload.paths.map((path, i) => ({ id: "item-" + i,
            path, state: i === 0 ? "Running" : "Pending", phase: i === 0 ? "Capturing" : null, byteLength: null,
            progress: i === 0 ? { stage: "Hashing", completedBytes: 1048576, totalBytes: 80 * 1048576 } : null,
            errorCode: null, errorMessage: null })) };
          nativeState.sourceQueueId = q.id;
          if (losePrepare) { losePrepare = false; throw Object.assign(new Error("Ответ потерян"), { code: "bridge_response_timeout" }); }
          return structuredClone(projectPage({}));
        }
        if (type === "getWorkspaceSourceQueue") {
          const result = structuredClone(projectPage(payload));
          if (malformed) result.state = "Finished";
          return result;
        }
        if (type === "controlWorkspaceSourceQueue") {
          const current = q.items.find(item => item.state === "Running");
          if (payload.command === "Pause") {
            q.state = "Paused";
            if (current) { current.state = "Prepared"; current.phase = null; current.byteLength = 80 * 1048576; }
          }
          if (payload.command === "Resume") {
            q.state = "Running";
            const item = q.items.find(item => item.state === "Pending"); if (item) { item.state = "Running"; item.phase = "WaitingForReader"; }
          }
          if (payload.command === "CancelAll") {
            q.items.filter(item => ["Running", "Pending"].includes(item.state)).forEach(item => { item.state = "Cancelled"; item.phase = null; }); q.state = "Finished";
          }
          if (payload.command === "RetrySelected") payload.itemIds.forEach(id => {
            const item = q.items.find(item => item.id === id); item.state = "Pending"; item.errorCode = item.errorMessage = null;
          });
          q.revision++; return structuredClone(projectPage({}));
        }
        throw new Error("Unexpected bridge call: " + type);
      };
    });
    for (const file of ["data-templates/ui/source-intake.js", "data-templates/ui/operation-view.js", "data-templates/ui/loader-view.js", "data-templates/ui/shell.js", "data-templates/ui/loader.js", "js/app-workspace-sources-view.js", "js/app-workspace-sources.js", "js/app-data-templates.js", "js/app-workspace-projects.js"])
      await page.addScriptTag({ path: path.join(root, file) });
    await page.evaluate(() => { RNAssistantDataTemplates.bind(); RNAssistantWorkspace.bind(); return RNAssistantWorkspace.show(); });
    await page.locator("#wsSourceBrowse").click();
    await page.locator("[data-ws-directory]").click();
    assert.equal(await page.locator("#wsBrowseList li").count(), 50);
    assert.equal(await page.locator("#wsBrowseList img").count(), 0);
    await page.locator("#wsBrowseSelectPage").check();
    await page.locator("#wsBrowseNext").click();
    await page.waitForFunction(() => document.getElementById("wsBrowseList").children.length === 11);
    await page.locator("#wsBrowseSelectPage").check();
    assert.match(await page.locator("#wsSourceSelected").innerText(), /61/);
    assert.equal(await page.locator("#wsSourceSave").isDisabled(), true, "read-only selection cannot persist changes");
    await page.locator("#wsSourcePrepare").click();
    await page.waitForFunction(() => document.getElementById("wsQueueList").children.length === 50);
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "prepareWorkspaceSources").length), 1);
    const prepared = await page.evaluate(() => calls.find(call => call.type === "prepareWorkspaceSources").payload);
    assert.equal(prepared.paths.length, 61); assert.equal(prepared.paths.every(name => name.startsWith("reports/")), true);
    assert.equal(prepared.maximumFileBytes, 96 * 1048576);
    assert.equal(await page.locator("#workspaceOpen").isDisabled(), true, "active native queue pins workspace selection");
    assert.equal(await page.locator("#wsSourcePrepare").isDisabled(), true);
    console.log("PASS source UI: explicit workspace selection, paged inert metadata, read-only admission and bounded dispatch");

    await page.evaluate(() => {
      q.items[0].state = "Failed"; q.items[0].phase = null; q.items[0].errorMessage = "Файл изменился";
      q.items[55].state = "Running"; q.items[55].phase = "Capturing";
      q.items[55].progress = { stage: "Verifying", completedBytes: 40 * 1048576, totalBytes: 80 * 1048576 }; q.revision++;
    });
    await page.locator("#wsQueueState").selectOption("Failed");
    await page.waitForFunction(() => document.getElementById("wsQueueList").children.length === 1);
    await page.waitForFunction(() => document.getElementById("wsQueuePercent").textContent === "50%");
    assert.match(await page.locator("#wsQueueCurrent").innerText(), /55/);
    await page.locator("#dtTabTemplates").click();
    assert.equal(await page.locator("#wsSourceActivity").isVisible(), true, "operation is independent of the visible section");
    await page.evaluate(() => { RNAssistantDataTemplates.unbind(); RNAssistantDataTemplates.bind(); });
    assert.match(await page.locator("#wsQueueCurrent").innerText(), /55/, "remount keeps native progress");
    await page.locator("#dtTabSources").click();
    await page.locator("#wsQueuePause").click();
    await page.waitForFunction(() => document.getElementById("wsQueueTitle").textContent.includes("приостановлена"));
    assert.equal(await page.locator("#workspaceOpen").isDisabled(), true, "paused pending work still pins its project");
    await page.locator("#wsQueueList input").check();
    await page.locator("#wsQueueRetry").click();
    await page.waitForFunction(() => document.getElementById("wsQueueList").children.length === 0);
    assert.deepEqual(await page.evaluate(() => calls.filter(call => call.type === "controlWorkspaceSourceQueue").at(-1).payload.itemIds), ["item-0"]);
    assert.match(await page.locator("#wsQueueTitle").innerText(), /приостановлена/, "retry does not silently resume");
    await page.locator("#wsQueueResume").click();
    await page.waitForFunction(() => document.getElementById("wsQueueTitle").textContent === "Подготавливаем файлы");
    await page.evaluate(() => { malformed = true; });
    await page.locator("#wsQueueRefresh").click();
    await page.waitForFunction(() => document.getElementById("wsSourceStatus").getAttribute("role") === "alert");
    assert.match(await page.locator("#wsQueueTitle").innerText(), /требует обновления/);
    assert.equal(await page.locator("#wsQueueCancel").isDisabled(), true, "malformed finished state cannot authorize more work");
    await page.evaluate(() => { malformed = false; });
    await page.locator("#wsQueueRefresh").click();
    await page.waitForFunction(() => !document.getElementById("wsQueueCancel").disabled);
    await page.locator("#wsQueueCancel").click();
    await page.waitForFunction(() => !document.getElementById("workspaceOpen").disabled);
    assert.match(await page.locator("#wsQueueCounts").innerText(), /Подготовлено 1 из 61/);
    console.log("PASS source UI: filter-independent progress, remount, pause/retry/cancel and invalid receipt recovery");

    await page.evaluate(() => { losePrepare = true; });
    const before = await page.evaluate(() => calls.filter(call => call.type === "prepareWorkspaceSources").length);
    await page.locator("#wsSourcePrepare").click();
    await page.waitForFunction(() => document.getElementById("wsSourceStatus").textContent.includes("Запуск не подтверждён"));
    assert.equal(await page.locator("#wsSourcePrepare").isDisabled(), true);
    await page.locator("#wsQueueRefresh").click();
    await page.waitForFunction(() => document.getElementById("wsQueueTitle").textContent === "Подготавливаем файлы");
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "prepareWorkspaceSources").length), before + 1, "lost reply recovers by reading, not replaying preparation");
    await page.locator("#wsQueueState").selectOption("");
    await page.waitForFunction(() => document.getElementById("wsQueueList").children.length === 50);
    await page.locator("#wsQueueNext").click();
    await page.waitForFunction(() => document.getElementById("wsQueueList").children.length === 11);
    for (const width of [360, 740, 1160]) {
      await page.setViewportSize({ width, height: 940 });
      for (const theme of ["light", "dark"]) {
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; document.getElementById("workspaceSettings").open = false; }, theme);
        await page.evaluate(() => { document.querySelectorAll("*").forEach(element => { if (element.scrollTop) element.scrollTop = 0; }); window.scrollTo(0, 0); });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "queue fits " + width + " " + theme);
        if (process.env.RNA_DATA_SCREENSHOTS) {
          fs.mkdirSync(process.env.RNA_DATA_SCREENSHOTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, `workspace-queue-${width}-${theme}.png`), fullPage: true, animations: "disabled" });
        }
      }
    }
    console.log("PASS source UI: lost admission reply, bounded queue pages and responsive light/dark layouts");

    await page.locator("#wsQueueCancel").click();
    await page.waitForFunction(() => !document.getElementById("workspaceOpen").disabled);
    await page.evaluate(() => { holdBrowse = true; });
    await page.locator("#wsSourceBrowse").click();
    await page.waitForFunction(() => !!window.releaseBrowse);
    await page.evaluate(async () => {
      nativeState = { ...nativeState, revision: 3, sourceQueueId: null, project: { ...nativeState.project, manifestPath: "other/project.json" } };
      await RNAssistantWorkspace.show(); releaseBrowse();
    });
    await page.waitForFunction(() => document.getElementById("wsBrowseList").children.length === 0);
    assert.equal(await page.locator("#wsSourceSelection").isVisible(), false, "late folder page cannot refill a new project");
    assert.equal(await page.locator("#wsSourceActivity").isVisible(), false);
    assert.deepEqual(errors, []);
    console.log("PASS source UI: changed project rejects late directory results and clears transient choices");

    const admissions = await page.evaluate(() => calls.filter(call => call.type === "prepareWorkspaceSources").length);
    await page.evaluate(async () => {
      nativeState = { ...nativeState, revision: 4, workspace: { ...nativeState.workspace, bindingId: "binding-b", readOnly: false },
        project: { ...nativeState.project, sources: catalog.map((item, i) => ({ path: item.path, sourceId: "src_" + (i + 1).toString(16).padStart(32, "0") })) } };
      await RNAssistantWorkspace.show();
    });
    assert.match(await page.locator("#wsSourcePersistence").innerText(), /Состав сохранён/);
    assert.equal(await page.locator("#wsSourceSave").isDisabled(), true);
    await page.locator("#wsSelectedDetails summary").click();
    assert.equal(await page.locator("#wsSelectedList li").count(), 50);
    assert.equal(await page.locator("#wsSelectedList img").count(), 0);
    await page.locator("#wsSelectedNext").click();
    assert.equal(await page.locator("#wsSelectedList li").count(), 11);
    await page.locator("#wsSelectedSearch").fill("Отчёт-55");
    await page.locator("[data-ws-remove]").click();
    assert.match(await page.locator("#wsSourcePersistence").innerText(), /несохранённые/);
    await page.locator("#wsSourceSave").click();
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent === "Состав источников сохранён.");
    const save = await page.evaluate(() => calls.filter(call => call.type === "saveTabularProjectSources").at(-1));
    assert.equal(save.payload.paths.length, 60); assert.equal(save.payload.projectRevision, 4);
    assert.equal(save.payload.manifestPath, "other/project.json");
    assert.match(await page.locator("#wsSourcePersistence").innerText(), /Состав сохранён/);
    await page.evaluate(() => { RNAssistantDataTemplates.unbind(); RNAssistantDataTemplates.bind(); });
    assert.match(await page.locator("#wsSourceSelected").innerText(), /60/);
    await page.locator("#wsSourceClear").click();
    assert.equal(await page.locator("#wsSourceSave").isEnabled(), true, "empty composition can be saved explicitly");
    assert.equal(await page.locator("#wsSourcePrepare").isDisabled(), true);
    await page.locator("#wsSourceRevert").click();
    assert.match(await page.locator("#wsSourceSelected").innerText(), /60/);
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "prepareWorkspaceSources").length), admissions, "opening/saving/remounting never starts preparation");
    await page.locator("#wsSelectedDetails summary").click();
    for (const width of [360, 1160]) {
      await page.setViewportSize({ width, height: 940 });
      for (const theme of ["light", "dark"]) {
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; }, theme);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "saved composition fits " + width + " " + theme);
        if (process.env.RNA_DATA_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, `workspace-selection-${width}-${theme}.png`), fullPage: true, animations: "disabled" });
      }
    }
    console.log("PASS source UI: persisted composition restores with bounded navigation, explicit save and revert");

    await page.locator("#wsSourceClear").click();
    await page.evaluate(() => { loseSave = true; });
    await page.locator("#wsSourceSave").click();
    await page.waitForFunction(() => !document.getElementById("workspaceReconcile").hidden);
    assert.equal(await page.locator("#workspaceProjectResult").isVisible(), false, "lost save receipt is not project success");
    const saves = await page.evaluate(() => calls.filter(call => call.type === "saveTabularProjectSources").length);
    await page.locator("#workspaceProjectPath").fill("unrelated/project.json");
    await page.locator("#workspaceReconcile").click();
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent.startsWith("Файл существует"));
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "reconcileWorkspaceFile").at(-1).payload.manifestPath), "other/project.json");
    assert.equal(await page.locator("#wsSourceSelection").isVisible(), false);
    await page.locator("#workspaceProjectPath").fill("other/project.json");
    await page.locator("#workspaceProjectOpen").click();
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent === "Проект открыт.");
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "saveTabularProjectSources").length), saves, "inspection/open never replay a save");
    assert.equal(await page.locator("#wsSourceSelection").isVisible(), false, "verified empty saved composition is restored");
    await page.locator("#wsSourceBrowse").click();
    await page.locator("[data-ws-directory]").click();
    await page.locator("#wsBrowseSelectPage").check();
    await page.evaluate(() => { invalidSave = true; });
    await page.locator("#wsSourceSave").click();
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent.startsWith("Не удалось проверить"));
    assert.equal(await page.locator("#workspaceProjectResult").isVisible(), false, "mismatched returned selection cannot report success");
    assert.equal(await page.locator("#workspaceReconcile").isVisible(), true);
    assert.deepEqual(errors, []);
    console.log("PASS source UI: lost/mismatched save receipts pin inspection path and never replay writes");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
