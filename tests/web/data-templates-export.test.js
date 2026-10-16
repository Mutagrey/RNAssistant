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
        const manifestPath = path.join(output, "application.json"), original = fs.readFileSync(manifestPath, "utf8");
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
        assert.deepEqual(await page.evaluate(() => [typeof window.echarts, typeof window.Tabulator, typeof window.XLSX]),
            ["undefined", "undefined", "undefined"]);
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
