"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os"), path = require("node:path");
const crypto = require("node:crypto"), http = require("node:http"), { pathToFileURL } = require("node:url");
const vm = require("node:vm");
const { execFileSync } = require("node:child_process");
const { chromium } = require("playwright");
const { capture, exportApplication } = require("../../web/data-templates/tools/export");
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), "data-templates-export-"));
const output = path.join(temporary, "application");
const hash = bytes => crypto.createHash("sha256").update(bytes).digest("hex");

(async () => {
    let browser, server;
    try {
        exportApplication(output);
        const lock = JSON.parse(fs.readFileSync(path.join(output, "application.lock.json"), "utf8"));
        for (const file of lock.files) {
            const bytes = fs.readFileSync(path.join(output, file.path));
            assert.equal(bytes.length, file.bytes); assert.equal(hash(bytes), file.sha256);
            assert.doesNotMatch(file.path, /native-|runtime-lock|\.cs$|project\.json$/);
        }
        assert.throws(() => exportApplication(output), /EEXIST/, "never overwrite an existing destination");
        const exportedTool = require(path.join(output, "tools/export.js"));
        assert.deepEqual(exportedTool.capture().files.map(file => file.sha256), lock.files.map(file => file.sha256));
        const alias = path.join(temporary, "source-alias");
        fs.symlinkSync(output, alias);
        assert.throws(() => exportedTool.exportApplication(path.join(alias, "nested")), /outside the application source/);
        const manifestPath = path.join(output, "plugin.json"), original = fs.readFileSync(manifestPath, "utf8");
        const manifest = JSON.parse(original);
        manifest.files.push("../private.json"); fs.writeFileSync(manifestPath, JSON.stringify(manifest));
        assert.throws(() => capture(output), /Invalid application member/);
        manifest.files[manifest.files.length - 1] = "outside.js";
        fs.writeFileSync(path.join(temporary, "outside.js"), "outside package");
        fs.symlinkSync(path.join(temporary, "outside.js"), path.join(output, "outside.js"));
        fs.writeFileSync(manifestPath, JSON.stringify(manifest));
        assert.throws(() => capture(output), /regular files/);
        fs.unlinkSync(path.join(output, "outside.js")); fs.writeFileSync(manifestPath, original);
        const vendorFile = path.join(output, "vendor/sheetjs.full.min.js");
        const vendorSource = fs.readFileSync(vendorFile);
        fs.appendFileSync(vendorFile, "\n");
        assert.throws(() => capture(output), /Vendor hash mismatch/);
        fs.writeFileSync(vendorFile, vendorSource);
        const vendorContext = vm.createContext({});
        vm.runInContext(vendorSource.toString("utf8"), vendorContext);
        const XLSX = vendorContext.XLSX;
        assert.equal(XLSX.version, "0.20.3");
        const workbook = XLSX.read(new Uint8Array(fs.readFileSync(path.join(output, "core/examples/matrix-source.xlsx"))),
            { type: "array", dense: true });
        const xlsb = XLSX.write(workbook, { type: "array", bookType: "xlsb" });
        const reopened = XLSX.read(xlsb, { type: "array", dense: true });
        assert.deepEqual(Array.from(reopened.SheetNames), Array.from(workbook.SheetNames));
        assert.equal(reopened.Sheets[reopened.SheetNames[0]]["!data"][11][0].v, "0012");
        // The exported source project includes runnable semantic tests, not a repository dependency.
        execFileSync(process.execPath, [path.join(output, "tests/matrix.test.js")], { cwd: temporary });
        console.log("PASS export: exact closure, hashes, editable source/tests, overwrite/traversal/symlink refusals");

        browser = await chromium.launch({ headless: true,
            ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
        const page = await browser.newPage({ viewport: { width: 1160, height: 940 } });
        const errors = [], unexpected = [];
        page.on("pageerror", error => errors.push(error.message));
        const filePrefix = pathToFileURL(output + path.sep).href;
        let httpPrefix = null;
        page.on("request", request => {
            if (!request.url().startsWith(filePrefix) && !(httpPrefix && request.url().startsWith(httpPrefix))) unexpected.push(request.url());
        });
        await page.goto(pathToFileURL(path.join(output, "index.html")).href);
        assert.deepEqual(await page.evaluate(() => [typeof window.echarts, typeof window.Tabulator, typeof window.XLSX,
            typeof window.CodeMirror]), ["undefined", "undefined", "undefined", "undefined"]);
        const chartAndGrid = await page.evaluate(async () => {
            const echarts = await DataTemplatesVendors.load("echarts");
            const chartNode = document.createElement("div");
            chartNode.style.cssText = "width:320px;height:160px";
            document.body.appendChild(chartNode);
            const chart = echarts.init(chartNode);
            chart.setOption({ xAxis: { type: "category", data: ["A", "B"] }, yAxis: {}, series: [{ type: "bar", data: [1, 2] }] });
            const chartReady = chart.getOption().series[0].data.length === 2;
            chart.dispose(); chartNode.remove();
            const Tabulator = await DataTemplatesVendors.load("tabulator");
            const gridNode = document.createElement("div");
            document.body.appendChild(gridNode);
            const table = new Tabulator(gridNode, { height: 160, data: [{ id: 1, label: "A" }],
                columns: [{ title: "Name", field: "label" }] });
            await new Promise(resolve => table.on("tableBuilt", resolve));
            const gridReady = gridNode.querySelector(".tabulator-cell")?.textContent === "A";
            table.destroy(); gridNode.remove();
            return [chartReady, gridReady, typeof window.XLSX];
        });
        assert.deepEqual(chartAndGrid, [true, true, "undefined"]);
        console.log("PASS standalone vendors: lazy local chart/grid, XLSB library fixture and inactive SheetJS UI");
        await page.locator("#dtFiles").setInputFiles({ name: "source.csv", mimeType: "text/csv", buffer: Buffer.from("a,b\n1,2\n") });
        await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "1");
        assert.equal(await page.evaluate(() => typeof RNAssistantWorkspace), "undefined");
        assert.equal(await page.locator("#workspaceSettings").count(), 0);
        for (const width of [1160, 360]) {
            await page.setViewportSize({ width, height: 940 });
            for (const colorScheme of ["light", "dark"]) {
                await page.emulateMedia({ colorScheme });
                assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
                if (process.env.RNA_DATA_SCREENSHOTS) {
                    fs.mkdirSync(process.env.RNA_DATA_SCREENSHOTS, { recursive: true });
                    await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, `standalone-${width}-${colorScheme}.png`), animations: "disabled" });
                }
            }
        }
        await page.goto(pathToFileURL(path.join(output, "core/index.html")).href);
        await page.locator("#operation").selectOption("extract");
        await page.locator("#capture").setInputFiles(path.join(output, "core/examples/matrix-extraction.json"));
        await page.locator("#run").click();
        await page.waitForFunction(() => document.getElementById("status").textContent.startsWith("Получено 16"));
        const normalized = JSON.parse(await page.locator("#result").innerText());
        assert.equal(normalized.preview.records.length, 16);
        assert.deepEqual(normalized.preview.records[0].value, { kind: "decimal", value: "10" });
        const downloadPromise = page.waitForEvent("download");
        await page.locator("#download").click();
        const download = await downloadPromise;
        const downloaded = JSON.parse(fs.readFileSync(await download.path(), "utf8"));
        assert.deepEqual(downloaded, normalized);
        console.log("PASS standalone file: shared loader, narrow/wide light/dark UI, normalized extraction and downloaded report");

        // Static test delivery only: no RNAssistant, native bridge or server-side application.
        const allowed = new Set(lock.files.map(file => "/" + file.path));
        server = http.createServer((request, response) => {
            if (!allowed.has(request.url)) { response.writeHead(404); response.end(); return; }
            response.setHeader("Content-Type", request.url.endsWith(".js") ? "text/javascript" : request.url.endsWith(".css") ? "text/css" : "text/html");
            response.end(fs.readFileSync(path.join(output, request.url.slice(1))));
        });
        await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
        httpPrefix = "http://127.0.0.1:" + server.address().port + "/";
        await page.goto(httpPrefix + "core/index.html");
        const sheetjsWorker = await page.evaluate(async bytes => {
            const worker = new Worker("../tests/vendor-worker.js");
            try {
                return await new Promise((resolve, reject) => {
                    const timer = setTimeout(() => reject(new Error("SheetJS Worker timed out")), 5000);
                    worker.onerror = error => { clearTimeout(timer); reject(new Error(error.message)); };
                    worker.onmessage = event => { clearTimeout(timer); resolve(event.data); };
                    const buffer = new Uint8Array(bytes).buffer;
                    worker.postMessage(buffer, [buffer]);
                });
            } finally { worker.terminate(); }
        }, Array.from(new Uint8Array(xlsb)));
        assert.deepEqual(sheetjsWorker, { version: "0.20.3", firstCode: "0012" });
        await page.setViewportSize({ width: 1160, height: 940 });
        await page.addInitScript(() => {
            Object.defineProperty(window, "showDirectoryPicker", { configurable: true, value: async options => {
                const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test", { create: true });
                if (options.id === "data-templates-parent") return parent;
                for await (const [name, handle] of parent.entries())
                    if (name.startsWith("data-templates-") && handle.kind === "directory") return handle;
                throw new DOMException("Project folder missing", "NotFoundError");
            } });
        });
        await page.goto(httpPrefix + "index.html");
        assert.deepEqual(await page.evaluate(() => {
            const sample = '{"kind":"data-templates","formatVersion":2,"projectId":"tp_' + "a".repeat(32) +
                '","name":"Test","sources":[]}';
            return [sample.replace('"name":"Test"', '"name":"Test","name":"Other"'),
                sample.replace('"formatVersion":2', '"formatVersion":2.0'),
                sample.replace('"sources":[]', '"sources":[{"sourceId":"src_' + "b".repeat(32) + '","path":"../bad.xlsx"}]')]
                .map(input => { try { DataTemplatesProjectManifest.parse(input); return "accepted"; }
                    catch (error) { return error.code; } });
        }), ["project_invalid", "project_invalid", "project_invalid"]);
        assert.equal(await page.evaluate(async () => {
            const text = DataTemplatesProjectManifest.create("Read-back check");
            const projectHandle = { getFile: async () => new File([text], "project.json") };
            const draftHandle = { createWritable: async () => ({ write: async () => {}, close: async () => {} }),
                getFile: async () => new File(["wrong"], "draft") };
            const drafts = { getFileHandle: async (_, options) => {
                if (!options?.create) throw new DOMException("Missing", "NotFoundError");
                return draftHandle;
            } };
            const directory = { getFileHandle: async () => projectHandle, getDirectoryHandle: async () => drafts };
            const project = await DataTemplatesBrowserProjectFiles.open(directory);
            return DataTemplatesBrowserProjectFiles.saveDraft(project, { name: "template.json", input: "{}" })
                .then(() => "accepted", error => error.code);
        }), "effect_unknown");
        await page.locator("#dtProjectToggle").click();
        await page.locator("#dtProjectName").fill("Production reports");
        await page.locator("#dtProjectCreate").click();
        await page.waitForFunction(() => document.getElementById("dtProjectNotice").textContent.includes("Проект создан"));
        const createdProject = await page.evaluate(async () => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            for await (const [, handle] of parent.entries()) if (handle.kind === "directory") {
                const text = await (await handle.getFileHandle("project.json")).getFile().then(file => file.text());
                return { folder: handle.name, manifest: JSON.parse(text) };
            }
        });
        assert.equal(createdProject.manifest.name, "Production reports");
        assert.match(createdProject.manifest.projectId, /^tp_[0-9a-f]{32}$/);
        assert.deepEqual(createdProject.manifest.sources, []);
        const dictionaryVersions = () => page.evaluate(async folder => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            const project = await parent.getDirectoryHandle(folder);
            let directory;
            try { directory = await project.getDirectoryHandle("dictionaries"); }
            catch (error) { if (error.name === "NotFoundError") return []; throw error; }
            const names = [];
            for await (const [name] of directory.entries()) names.push(name);
            return Promise.all(names.sort().map(async name => ({ name,
                text: await (await (await directory.getFileHandle(name)).getFile()).text() })));
        }, createdProject.folder);
        const waitForSavedMapping = () => page.waitForFunction(() =>
            document.getElementById("dtMappingEditor").hidden &&
            !document.getElementById("dtStartProcessing").disabled &&
            document.getElementById("dtProcessNotice").textContent.includes("Общий справочник сохранён как новая версия проекта"));
        await page.locator("#dtProjectOpen").click();
        await page.waitForFunction(() => document.getElementById("dtProjectNotice").textContent === "Проект открыт.");
        await page.locator("#dtFiles").setInputFiles([
            { name: "matrix-source.xlsx", mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                buffer: fs.readFileSync(path.join(output, "core/examples/matrix-source.xlsx")) },
            { name: "unsupported.csv", mimeType: "text/csv", buffer: Buffer.from("a,b\n1,2\n") },
            { name: "broken.xlsx", mimeType: "application/octet-stream", buffer: Buffer.from("not an xlsx") }
        ]);
        await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "3");
        await page.locator("#dtTabTemplates").click();
        assert.match(await page.locator("#dtStorageNote").innerText(), /256 МиБ/);
        const invalidTemplate = JSON.parse(fs.readFileSync(path.join(output, "core/examples/matrix-xlsx-extraction.json"), "utf8"));
        invalidTemplate.template.source.rows[0].bindings.parameter.expr = "unknown()";
        await page.locator("#dtTemplateFile").setInputFiles({ name: "invalid.json", mimeType: "application/json",
            buffer: Buffer.from(JSON.stringify(invalidTemplate)) });
        await page.waitForFunction(() => document.getElementById("dtProcessNotice").textContent.includes("EXPRESSION_TYPE"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        assert.equal(await page.locator("#dtTemplateInspection").isVisible(), false);
        await page.locator("#dtOpenTemplateEditor").click();
        await page.waitForFunction(() => !!document.querySelector("#dtEditorJsonCode .CodeMirror"));
        assert.equal(await page.locator("#dtEditorJsonPane").isVisible(), true);
        await page.evaluate(value => document.querySelector("#dtEditorJsonCode .CodeMirror").CodeMirror.setValue(value),
            fs.readFileSync(path.join(output, "core/examples/matrix-xlsx-extraction.json"), "utf8"));
        await page.waitForFunction(() => !document.getElementById("dtStartProcessing").disabled);
        await page.locator("#dtCloseTemplateEditor").click();
        invalidTemplate.template.source.rows[0].bindings.parameter.expr = "lookup(\"parameters\", text(cell(\"A\", row)), \"production-form\")";
        invalidTemplate.dictionaries.parameterMappings[0].targetId = "missing";
        page.once("dialog", dialog => dialog.accept());
        await page.locator("#dtTemplateFile").setInputFiles({ name: "invalid-mapping.json", mimeType: "application/json",
            buffer: Buffer.from(JSON.stringify(invalidTemplate)) });
        await page.waitForFunction(() => document.getElementById("dtProcessNotice").textContent.includes("REFERENCE_MISSING"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        const manyMappings = JSON.parse(fs.readFileSync(path.join(output, "core/examples/matrix-xlsx-extraction.json"), "utf8"));
        for (let index = 0; index < 30; index++) manyMappings.dictionaries.parameterMappings.push({
            scope: "production-form", source: "extra-" + index, targetId: "production.volume" });
        await page.locator("#dtTemplateFile").setInputFiles({ name: "many-mappings.json", mimeType: "application/json",
            buffer: Buffer.from(JSON.stringify(manyMappings)) });
        await page.waitForFunction(() => !document.getElementById("dtStartProcessing").disabled);
        await page.locator("#dtTabMapping").click();
        assert.equal(await page.locator("#dtMappingRows tr").count(), 25);
        await page.locator("#dtMappingNext").click();
        assert.match(await page.locator("#dtMappingRows").innerText(), /extra-29/);
        await page.locator("#dtTabTemplates").click();
        await page.locator("#dtTemplateFile").setInputFiles(path.join(output, "core/examples/matrix-xlsx-extraction.json"));
        await page.waitForFunction(() => !document.getElementById("dtStartProcessing").disabled);
        assert.match(await page.locator("#dtInspectionTitle").innerText(), /production-form · ревизия 2/);
        assert.match(await page.locator("#dtInspectionMetrics").innerText(), /Данные · A1:AG20/);
        assert.match(await page.locator("#dtInspectionMetrics").innerText(), /16/);
        await page.locator("#dtTemplateInspection summary").click();
        assert.match(await page.locator("#dtInspectionRules").innerText(), /lookup\("parameters"/);
        assert.match(await page.locator("#dtInspectionRules").innerText(), /decimal\(raw/);
        await page.locator('#dtInspectionRules button[data-rule-path="/template/source/rows/0/bindings/parameter/expr"]').click();
        await page.waitForFunction(() => !!document.querySelector("#dtEditorExpressionCode .CodeMirror"));
        assert.equal(await page.evaluate(() => typeof window.CodeMirror), "function");
        assert.equal(await page.locator("#dtEditorRulePath").innerText(), "/template/source/rows/0/bindings/parameter/expr");
        if (process.env.RNA_DATA_SCREENSHOTS) {
            await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "editor-wide.png"), fullPage: true, animations: "disabled" });
            await page.locator("#dtTemplateEditor").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "editor-wide-panel.png"), animations: "disabled" });
            await page.emulateMedia({ colorScheme: "light" });
            await page.locator("#dtTemplateEditor").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "editor-light.png"), animations: "disabled" });
            await page.emulateMedia({ colorScheme: "dark" });
            await page.setViewportSize({ width: 360, height: 940 });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            await page.locator("#dtTemplateEditor").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "editor-narrow.png"), animations: "disabled" });
            await page.setViewportSize({ width: 1160, height: 940 });
        }
        const originalRule = await page.evaluate(() => document.querySelector("#dtEditorExpressionCode .CodeMirror").CodeMirror.getValue());
        await page.evaluate(() => document.querySelector("#dtEditorExpressionCode .CodeMirror").CodeMirror.setValue("unknown()"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        await page.locator('#dtInspectionRules button[data-rule-path="/template/source/rows/0/bindings/unit/expr"]').click();
        assert.equal(await page.evaluate(() => document.querySelector("#dtEditorExpressionCode .CodeMirror").CodeMirror.getValue()), "unknown()");
        assert.equal(await page.locator("#dtEditorRulePath").innerText(), "/template/source/rows/0/bindings/parameter/expr");
        await page.locator("#dtEditorApplyRule").click();
        await page.waitForFunction(() => document.getElementById("dtEditorRuleNotice").textContent.includes("EXPRESSION_TYPE"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        await page.evaluate(value => document.querySelector("#dtEditorExpressionCode .CodeMirror").CodeMirror.setValue(value + " "), originalRule);
        await page.locator("#dtEditorApplyRule").click();
        await page.waitForFunction(() => document.getElementById("dtProcessNotice").textContent.includes("Правило применено"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), false);
        const draftDownload = page.waitForEvent("download");
        await page.locator("#dtDownloadTemplateDraft").click();
        const draftFile = await draftDownload;
        assert.match(draftFile.suggestedFilename(), /\.draft\.json$/);
        const draftText = fs.readFileSync(await draftFile.path(), "utf8");
        assert.equal(JSON.parse(draftText).template.source.rows[0].bindings.parameter.expr, originalRule + " ");
        await page.locator("#dtEditorJsonTab").click();
        await page.evaluate(() => document.querySelector("#dtEditorJsonCode .CodeMirror").CodeMirror.setValue("{"));
        await page.waitForFunction(() => document.getElementById("dtProcessNotice").textContent.includes("JSON_INVALID"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        await page.locator("#dtProjectSave").click();
        await page.waitForFunction(() => document.getElementById("dtProjectNotice").textContent.includes("Черновик сохранён"));
        assert.equal(await page.locator("#dtProjectDraftList button[data-file]").count(), 1);
        assert.equal(await page.evaluate(async () => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            const directory = await parent.getDirectoryHandle(document.querySelector(".dt-project-card").title.split("/")[0]);
            const name = document.querySelector("#dtProjectDraftList button[data-file]").dataset.file;
            return (await (await (await directory.getDirectoryHandle("drafts")).getFileHandle(name)).getFile()).text();
        }), "{");
        await page.evaluate(value => document.querySelector("#dtEditorJsonCode .CodeMirror").CodeMirror.setValue(value), draftText);
        await page.waitForFunction(() => !document.getElementById("dtStartProcessing").disabled);
        assert.equal(await page.locator("#dtTemplateInspection").isVisible(), true);
        await page.locator("#dtCloseTemplateEditor").click();
        await page.locator("#dtTabMapping").click();
        // File selection only checks the draft. Its first structured edit creates
        // the shared dictionary; later edits append project-wide versions.
        const beforeMapping = await dictionaryVersions();
        const originalDictionary = JSON.parse(draftText).dictionaries;
        assert.deepEqual(beforeMapping, []);
        await page.locator("#dtMappingAdd").click();
        await page.locator("#dtMappingEditScope").fill("production-form");
        await page.locator("#dtMappingEditSource").fill("0012");
        await page.locator("#dtMappingEditTarget").selectOption("production.volume");
        if (process.env.RNA_DATA_SCREENSHOTS) {
            await page.locator("#dtMappingEditor").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "mapping-editor-wide.png"), animations: "disabled" });
            await page.setViewportSize({ width: 360, height: 940 });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            await page.locator("#dtMappingEditor").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "mapping-editor-narrow.png"), animations: "disabled" });
            await page.setViewportSize({ width: 1160, height: 940 });
        }
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        await page.locator("#dtMappingEditApply").click();
        await page.waitForFunction(() => document.getElementById("dtMappingEditNotice").textContent.includes("DICTIONARY_CONFLICT"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        assert.deepEqual(await dictionaryVersions(), beforeMapping, "a rejected duplicate must not write a dictionary version");
        await page.locator("#dtMappingEditSource").fill("unused-synonym");
        await page.locator("#dtMappingEditApply").click();
        await waitForSavedMapping();
        const afterAddMapping = await dictionaryVersions();
        await page.locator("#dtMappingSearch").fill("unused-synonym");
        const addedMapping = page.locator("#dtMappingRows tr").filter({
            has: page.getByRole("cell", { name: "unused-synonym", exact: true }) });
        assert.equal(await addedMapping.count(), 1);
        assert.match(await addedMapping.innerText(), /production\.volume/);
        await addedMapping.getByRole("button", { name: "Править", exact: true }).click();
        await page.locator("#dtMappingEditTarget").selectOption("production.loss");
        await page.locator("#dtMappingEditApply").click();
        await waitForSavedMapping();
        await page.locator("#dtMappingAdd").click();
        await page.locator("#dtMappingEditKind").selectOption("unit");
        await page.locator("#dtMappingEditScope").fill("production-form");
        await page.locator("#dtMappingEditSource").fill("tonnes");
        await page.locator("#dtMappingEditTarget").selectOption("t");
        await page.locator("#dtMappingEditApply").click();
        await waitForSavedMapping();
        const afterMapping = await dictionaryVersions();
        assert.equal(afterAddMapping.length, 1);
        assert.deepEqual(afterMapping.slice(0, afterAddMapping.length), afterAddMapping, "saved dictionary versions stay immutable");
        const newDictionaries = afterMapping.map(file => JSON.parse(file.text));
        assert.deepEqual(newDictionaries.map(dictionary => dictionary.revision), [1, 2, 3]);
        const parameterMapping = { scope: "production-form", source: "unused-synonym", targetId: "production.volume" };
        const unitMapping = { scope: "production-form", source: "tonnes", targetId: "t" };
        assert.deepEqual(newDictionaries[0], { ...originalDictionary, revision: 1,
            parameterMappings: [...originalDictionary.parameterMappings, parameterMapping] });
        parameterMapping.targetId = "production.loss";
        assert.deepEqual(newDictionaries[1], { ...originalDictionary, revision: 2,
            parameterMappings: [...originalDictionary.parameterMappings, parameterMapping] });
        assert.deepEqual(newDictionaries[2], { ...newDictionaries[1], revision: 3,
            unitMappings: [...originalDictionary.unitMappings, unitMapping] });
        await page.locator("#dtTabTemplates").click();
        await page.locator("#dtOpenTemplateEditor").click();
        const mappedDownload = page.waitForEvent("download");
        await page.locator("#dtDownloadTemplateDraft").click();
        const mappedDraft = JSON.parse(fs.readFileSync(await (await mappedDownload).path(), "utf8"));
        assert.deepEqual(mappedDraft.dictionaries, newDictionaries[2]);
        await page.locator("#dtProjectSave").click();
        await page.waitForFunction(() => document.querySelectorAll("#dtProjectDraftList button[data-file]").length === 2);
        const persisted = await page.evaluate(async () => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            const directory = await parent.getDirectoryHandle(document.querySelector(".dt-project-card").title.split("/")[0]);
            const names = [];
            for await (const [name] of (await directory.getDirectoryHandle("drafts")).entries()) names.push(name);
            names.sort();
            const latest = await (await (await directory.getDirectoryHandle("drafts")).getFileHandle(names.at(-1))).getFile();
            return { count: names.length, text: await latest.text() };
        });
        assert.equal(persisted.count, 2);
        assert.deepEqual(JSON.parse(persisted.text).dictionaries.parameterMappings.at(-1),
            mappedDraft.dictionaries.parameterMappings.at(-1));
        if (process.env.RNA_DATA_SCREENSHOTS) {
            await page.locator(".dt-project-card").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "project-wide.png"), animations: "disabled" });
            await page.setViewportSize({ width: 360, height: 940 });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            await page.locator(".dt-project-card").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "project-narrow.png"), animations: "disabled" });
            await page.setViewportSize({ width: 1160, height: 940 });
        }
        await page.evaluate(async () => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            const directory = await parent.getDirectoryHandle(document.querySelector(".dt-project-card").title.split("/")[0]);
            const handle = await directory.getFileHandle("project.json"), file = await handle.getFile();
            window.__projectManifestText = await file.text();
            const writer = await handle.createWritable(); await writer.write(window.__projectManifestText + " "); await writer.close();
        });
        await page.locator("#dtProjectSave").click();
        await page.waitForFunction(() => document.getElementById("dtProjectNotice").textContent.includes("project_changed"));
        assert.equal(await page.locator("#dtProjectDraftList button[data-file]").count(), 2);
        await page.evaluate(async () => {
            const parent = await (await navigator.storage.getDirectory()).getDirectoryHandle("dt-project-browser-test");
            const directory = await parent.getDirectoryHandle(document.querySelector(".dt-project-card").title.split("/")[0]);
            const writer = await (await directory.getFileHandle("project.json")).createWritable();
            await writer.write(window.__projectManifestText); await writer.close();
        });
        page.once("dialog", dialog => dialog.accept());
        await page.locator("#dtProjectDraftList button[data-file]").first().click();
        await page.waitForFunction(() => document.getElementById("dtProjectNotice").textContent.includes("Черновик открыт"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), false);
        await page.locator("#dtProjectToggle").click();
        await page.locator("#dtInspectionOpenMapping").click();
        assert.equal(await page.locator("#dtMappingInspection").isVisible(), true);
        await page.locator("#dtMappingSearch").fill("unused-synonym");
        assert.equal(await addedMapping.count(), 1);
        assert.match(await addedMapping.innerText(), /production\.loss/);
        await page.locator("#dtMappingSearch").fill("");
        assert.match(await page.locator("#dtMappingRows").innerText(), /production.volume/);
        await page.locator("#dtMappingKind").selectOption("unit");
        assert.match(await page.locator("#dtMappingRows").innerText(), /т/);
        await page.locator("#dtTabParameters").click();
        assert.equal(await page.locator("#dtParameterInspection").isVisible(), true);
        await page.locator("#dtParameterSearch").fill("loss");
        assert.match(await page.locator("#dtParameterRows").innerText(), /production.loss/);
        assert.match(await page.locator("#dtParameterRows").innerText(), /Тонны/);
        assert.doesNotMatch(await page.locator("#dtParameterRows").innerText(), /production.volume/);
        await page.locator("#dtTabTemplates").click();
        for (const width of [1160, 360]) {
            await page.setViewportSize({ width, height: 940 });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            if (process.env.RNA_DATA_SCREENSHOTS)
                await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, `template-${width}.png`), fullPage: true, animations: "disabled" });
            if (width === 360) {
                if (process.env.RNA_DATA_SCREENSHOTS) {
                    await page.locator("#dtTemplateInspection").scrollIntoViewIfNeeded();
                    await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "template-360-detail.png"), animations: "disabled" });
                }
                await page.locator("#dtViewSelect").selectOption("mapping");
                assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
                if (process.env.RNA_DATA_SCREENSHOTS)
                    await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "mapping-360.png"), animations: "disabled" });
                await page.locator("#dtViewSelect").selectOption("templates");
            }
        }
        await page.setViewportSize({ width: 1160, height: 940 });
        await page.locator("#dtStartProcessing").click();
        await page.waitForFunction(() => document.getElementById("dtResultCompleted").textContent === "1" &&
            document.getElementById("dtResultFailed").textContent === "2");
        assert.equal(await page.locator("#dtViewResults").isVisible(), true);
        assert.equal(await page.locator("#dtResultRecords").innerText(), "16");
        assert.match(await page.locator("#dtRunList").innerText(), /READER_UNAVAILABLE/);
        assert.equal(await page.locator("#dtPreviewRows tr").count(), 16);
        page.once("dialog", dialog => dialog.accept());
        await page.locator("#dtTemplateFile").setInputFiles({ name: "other.json", mimeType: "application/json",
            buffer: fs.readFileSync(path.join(output, "core/examples/matrix-xlsx-extraction.json")) });
        await page.waitForFunction(() => document.getElementById("dtProcessNotice").textContent.includes("DICTIONARY_MISMATCH"));
        assert.equal(await page.locator("#dtStartProcessing").isDisabled(), true);
        const resultDownload = page.waitForEvent("download");
        await page.locator("#dtDownloadResult").click();
        const fullResult = JSON.parse(fs.readFileSync(await (await resultDownload).path(), "utf8"));
        assert.equal(fullResult.kind, "data-templates-extraction");
        assert.equal(fullResult.templateFile, "matrix-xlsx-extraction.json");
        assert.equal(fullResult.records.length, fullResult.summary.recordCount);
        assert.equal(fullResult.records[0].provenance.value.evidence[0].location.address, "C12");
        assert.deepEqual(fullResult.records[0].value, normalized.preview.records[0].value);
        if (process.env.RNA_DATA_SCREENSHOTS) {
            fs.mkdirSync(process.env.RNA_DATA_SCREENSHOTS, { recursive: true });
            await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "results-wide.png"), animations: "disabled" });
            await page.emulateMedia({ colorScheme: "light" });
            await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "results-light.png"), animations: "disabled" });
            await page.emulateMedia({ colorScheme: "dark" });
        }
        await page.setViewportSize({ width: 360, height: 940 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
        if (process.env.RNA_DATA_SCREENSHOTS)
            await page.locator("#dtResultPreview").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "results-narrow.png"), animations: "disabled" });
        await page.locator("#dtTemplateFile").setInputFiles({ name: "mapped.json", mimeType: "application/json",
            buffer: Buffer.from(JSON.stringify(mappedDraft)) });
        await page.waitForFunction(() => !document.getElementById("dtStartProcessing").disabled);
        await page.evaluate(() => {
            // Project dictionary admission is asynchronous. Cancel the actual run
            // as soon as its control is rendered, before this tiny fixture finishes.
            const operation = document.getElementById("dtProcessOperation");
            const observer = new MutationObserver(() => {
                if (operation.dataset.state !== "running") return;
                observer.disconnect();
                operation.querySelector("[data-operation-part=cancel]").click();
            });
            observer.observe(operation, { attributes: true, attributeFilter: ["data-state"] });
            document.getElementById("dtStartProcessing").click();
        });
        await page.waitForFunction(() => document.getElementById("dtRunFilter").querySelector('[value="cancelled"]') &&
            document.getElementById("dtRunList").textContent.includes("Отменено") &&
            !document.getElementById("dtStartProcessing").disabled);
        assert.equal(await page.locator("#dtResultCompleted").innerText(), "0");
        assert.equal(await page.locator("#dtDownloadResult").isVisible(), false);
        console.log("PASS standalone workflow: selected sources → sequential XLSX queue, explicit errors, preview and complete JSON export");
        const staging = await page.evaluate(async () => {
            const api = DataTemplatesResultStaging;
            const folder = await (await navigator.storage.getDirectory()).getDirectoryHandle("data-templates-provisional-v1");
            const entries = async () => { let count = 0; for await (const _ of folder.entries()) count++; return count; };
            const initial = await entries();
            const options = { source: { name: "large.xlsx", path: "large.xlsx", size: 80 * 1048576 },
                templateName: "template.json", remainingBytes: api.totalMaximumBytes };
            const store = await api.create(options);
            const records = Array.from({ length: 18 }, (_, id) => ({ id, value: "x".repeat(40000) }));
            for (let batch = 0; batch < 12; batch++) await store.sink.writeBatch({ records });
            await store.sink.complete({ recordCount: 216 });
            const output = await store.file(), parsed = JSON.parse(await output.text());
            const large = { mode: store.snapshot().mode, bytes: output.size, records: parsed.records.length,
                template: parsed.templateFile, source: parsed.source.name };
            await store.discard();
            const afterComplete = await entries();
            const cancelled = await api.create(options);
            const pending = cancelled.sink.writeBatch({ records: [{ id: 1 }] });
            cancelled.sink.abort();
            const cancelledCode = await pending.then(() => "accepted", error => error.code);
            await cancelled.discard();
            const afterCancel = await entries();
            const corrupted = await api.create(options);
            await corrupted.sink.writeBatch({ records: [{ id: 1 }] });
            const completion = corrupted.sink.complete({ recordCount: 1 });
            const lateCode = await corrupted.sink.writeBatch({ records: [{ id: 2 }] })
                .then(() => "accepted", error => error.code);
            await completion;
            for await (const [, handle] of folder.entries()) {
                const writer = await handle.createWritable({ keepExistingData: false });
                await writer.write("damaged"); await writer.close();
            }
            const corruptionCode = await corrupted.file().then(() => "accepted", error => error.code);
            await corrupted.discard();
            const afterCorruption = await entries();
            Object.defineProperty(navigator.storage, "getDirectory", { value: undefined, configurable: true });
            let memory;
            try {
                memory = await api.create(options);
                await memory.sink.writeBatch({ records: [{ id: 1 }] });
                await memory.sink.complete({ recordCount: 1 });
                const parsedMemory = JSON.parse(await (await memory.file()).text());
                const limited = await api.create({ ...options, remainingBytes: 16 });
                const limitCode = await limited.sink.writeBatch({ records: [{ value: "over budget" }] })
                    .then(() => "accepted", error => error.code);
                limited.sink.abort(); await limited.discard();
                return { large, initial, afterComplete, afterCancel, afterCorruption, cancelledCode, lateCode, corruptionCode,
                    memoryMode: memory.snapshot().mode, memoryRecords: parsedMemory.records.length, limitCode };
            } finally {
                if (memory) await memory.discard();
                delete navigator.storage.getDirectory;
            }
        });
        assert.equal(staging.large.mode, "disk");
        assert.equal(staging.large.records, 216);
        assert.equal(staging.large.template, "template.json");
        assert.equal(staging.large.source, "large.xlsx");
        assert.ok(staging.large.bytes > 8 * 1048576);
        assert.equal(staging.initial, 0);
        assert.equal(staging.afterComplete, staging.initial);
        assert.equal(staging.afterCancel, staging.initial);
        assert.equal(staging.afterCorruption, staging.initial);
        assert.equal(staging.cancelledCode, "RESULT_STAGING_CLOSED");
        assert.equal(staging.lateCode, "RESULT_STAGING_CLOSED");
        assert.equal(staging.corruptionCode, "RESULT_STAGING_INVALID");
        assert.equal(staging.memoryMode, "memory");
        assert.equal(staging.memoryRecords, 1);
        assert.equal(staging.limitCode, "RESULT_STAGING_LIMIT");
        console.log("PASS result staging: >8 MiB disk output, exact JSON, revoke/cleanup, read-back refusal and memory fallback");
        await page.goto(httpPrefix + "core/index.html");
        await page.locator("#operation").selectOption("extractXlsx");
        await page.locator("#capture").setInputFiles(path.join(output, "core/examples/matrix-xlsx-extraction.json"));
        await page.locator("#sourceFile").setInputFiles(path.join(output, "core/examples/matrix-source.xlsx"));
        await page.locator("#run").click();
        await page.waitForFunction(() => document.getElementById("status").textContent.startsWith("Получено 16"));
        const xlsx = JSON.parse(await page.locator("#result").innerText());
        assert.equal(xlsx.recordCount, 16); assert.equal(xlsx.preview.records.length, 16);
        assert.equal(xlsx.preview.records[0].provenance.value.evidence[0].location.address, "C12");
        assert.deepEqual(xlsx.preview.records[0].value, normalized.preview.records[0].value);
        assert.deepEqual(errors, []); assert.deepEqual(unexpected, []);
        console.log("PASS standalone Worker: exported XLSX + template → canonical records without host or outside assets");
    } finally {
        if (browser) await browser.close();
        if (server) await new Promise(resolve => server.close(resolve));
        fs.rmSync(temporary, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
