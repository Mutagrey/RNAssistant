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
    const index = read("index.html");
    const css = [...index.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    const section = index.match(/<section class="panel" id="tab-workspace">[\s\S]*?<\/section>/)[0]
      .replace('class="panel"', 'class="panel active"');
    await page.setContent(`<html lang="ru" data-theme="light"><style>${css}</style><div class="shell"><main class="content">${section}</main></div></html>`);
    await page.evaluate(() => {
      window.RNAssistantWorkspaceSources = { bind() {}, unbind() {}, setContext() {} };
      window.RNAssistantWorkspaceDrafts = { bind(container) { container.querySelector("#dtViewTemplates .dt-empty-state")?.remove(); },
        unbind() {}, setContext() {}, dispose() {} };
      window.calls = []; window.holdList = false; window.badList = false; window.releaseList = null;
      window.nativeState = name => ({ type: "rnassistant.workspaceState", contractVersion: 1, revision: name === "A" ? 2 : 3,
        workspace: { bindingId: "binding-" + name, rootPath: "C:\\Reports", readOnly: false },
        project: { name: "Проект " + name, manifestPath: "project-" + name + ".json" } });
      window.send = (type, payload) => {
        calls.push({ type, payload: structuredClone(payload) });
        const name = payload.bindingId.slice(-1), state = nativeState(name);
        const id = i => name + "Template" + String(i).padStart(2, "0");
        const head = i => `template-${id(i)}-1730000000000-${"a".repeat(32)}.json`;
        if (type === "listTabularProjectTemplates") {
          const total = name === "A" ? 55 : 1;
          const response = { state, offset: payload.offset, total,
            items: Array.from({ length: Math.min(50, Math.max(0, total - payload.offset)) }, (_, i) => {
              const number = payload.offset + i;
              return { id: id(number), headFile: head(number), savedAt: 1730000000000,
                archived: false, versions: 1 };
            }) };
          if (badList) { response.state.revision++; badList = false; }
          if (holdList && name === "A") {
            holdList = false;
            return new Promise(resolve => { releaseList = () => resolve(response); });
          }
          return Promise.resolve(response);
        }
        if (type === "readTabularProjectDictionary") return Promise.resolve({ state,
          dictionary: name === "B" ? null : {
            file: `dictionary-1730000000000-${"b".repeat(32)}.json`, format: "dictionary",
            sha256: "c".repeat(64), input: JSON.stringify({ version: 1, revision: 1,
              parameters: [{ id: "revenue", name: '<img onerror="alert(1)">', unitId: "rub" }],
              units: [{ id: "rub", name: "Рубль" }],
              parameterMappings: [{ scope: "book", source: "Доход", targetId: "revenue" }],
              unitMappings: [] }) } });
        if (type === "openTabularProjectTemplate") return Promise.resolve({ state,
          id: payload.id, headFile: payload.headFile,
          template: { file: payload.headFile, format: "project", sha256: "d".repeat(64),
            input: JSON.stringify({ kind: "data-templates-project-template", template: { id: payload.id } }) } });
        throw new Error("Unexpected bridge call: " + type);
      };
    });
    for (const file of ["js/app-navigation-tree.js", "data-templates/ui/shell.js", "js/app-workspace-catalog.js", "js/app-data-templates.js"])
      await page.addScriptTag({ path: path.join(root, file) });
    await page.evaluate(() => { RNAssistantDataTemplates.bind(); RNAssistantDataTemplates.setContext(nativeState("A"), false);
      document.querySelector("#workspaceSettings").dataset.projectOpen = "true";
      document.querySelector("#workspaceSettings").open = false; });
    await page.waitForFunction(() => document.querySelectorAll("#dtNativeCatalogList li").length === 50 &&
      document.querySelectorAll("#dtNativeParametersRows tr").length === 2);
    assert.equal(await page.locator("#dtNativeCatalogRange").innerText(), "1–50 из 55");
    await page.locator('#dtNavTree [data-tree-key="dt:templates"] .navigation-tree-label').click();
    assert.equal(await page.locator('#dtNavTree [data-tree-key="dt:templates"]').getAttribute("aria-selected"), "true");
    assert.equal(await page.locator('#dtNavTree [data-tree-key="dt:sources"]').getAttribute("aria-selected"), "false");
    if (process.env.CAPTURE_UI) await page.screenshot({ path: "/tmp/rna-native-catalog-desktop.png", fullPage: true });
    assert.equal(await page.locator("#dtNativeParametersRows img").count(), 0);
    assert.equal(await page.locator("#dtNativeParametersRows").innerText().then(value => value.includes('<img onerror="alert(1)">')), true);
    assert.equal(await page.evaluate(() => calls.every(call => ["listTabularProjectTemplates", "readTabularProjectDictionary", "openTabularProjectTemplate"].includes(call.type))), true);
    assert.deepEqual(await page.evaluate(() => calls[0].payload),
      { bindingId: "binding-A", manifestPath: "project-A.json", projectRevision: 2, offset: 0 });

    await page.locator('#dtNavTree [data-tree-key="dt:parameters"] .navigation-tree-label').click();
    assert.equal(await page.locator("#dtViewParameters").isVisible(), true);
    await page.locator("#dtNativeParametersSearch").fill("рубль");
    assert.equal(await page.locator("#dtNativeParametersRows tr").count(), 1);
    await page.locator('#dtNavTree [data-tree-key="dt:mapping"] .navigation-tree-label').click();
    assert.equal(await page.locator("#dtNativeMappingRows tr").count(), 1);
    assert.equal(await page.locator('#dtNavTree [data-tree-key="dt:mapping"] .navigation-tree-label').innerText(), "Соответствия");
    await page.locator('#dtNavTree [data-tree-key="dt:mapping"]').press("Home");
    assert.equal(await page.evaluate(() => document.activeElement.dataset.treeKey), "dt:project");
    await page.locator('#dtNavTree [data-tree-key="dt:project"]').press("ArrowDown");
    await page.locator('#dtNavTree [data-tree-key="dt:sources"]').press("Enter");
    assert.equal(await page.locator("#dtViewSources").isVisible(), true);
    await page.setViewportSize({ width: 360, height: 780 });
    await page.evaluate(() => document.querySelector(".shell").classList.add("navigation-open"));
    await page.locator('#dtNavTree [data-tree-key="dt:templates"] .navigation-tree-label').dispatchEvent("click");
    await page.evaluate(() => document.querySelector(".shell").classList.remove("navigation-open"));
    assert.equal(await page.locator("#dtViewTemplates").isVisible(), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    if (process.env.CAPTURE_UI) await page.screenshot({ path: "/tmp/rna-native-catalog-mobile.png", fullPage: true });
    await page.setViewportSize({ width: 1160, height: 940 });

    await page.locator("#dtNativeCatalogNext").click();
    await page.waitForFunction(() => document.querySelectorAll("#dtNativeCatalogList li").length === 5);
    assert.equal(await page.locator("#dtNativeCatalogRange").innerText(), "51–55 из 55");
    await page.locator("#dtNativeCatalogList button").first().click();
    await page.waitForFunction(() => !document.querySelector("#dtNativeCatalogPreview").hidden);
    assert.match(await page.locator("#dtNativeCatalogPreviewMeta").innerText(), /без проверки правил/);
    const open = await page.evaluate(() => calls.find(call => call.type === "openTabularProjectTemplate").payload);
    assert.equal(open.headFile, "template-ATemplate50-1730000000000-" + "a".repeat(32) + ".json");

    await page.evaluate(() => { holdList = true; });
    await page.locator("#dtNativeCatalogRefresh").click();
    await page.waitForFunction(() => !!releaseList);
    await page.evaluate(() => RNAssistantDataTemplates.setContext(nativeState("B"), false));
    await page.waitForFunction(() => document.querySelector("#dtNativeCatalogList li")?.textContent.includes("BTemplate00"));
    await page.evaluate(() => releaseList());
    await page.waitForTimeout(30);
    assert.match(await page.locator("#dtNativeCatalogList").innerText(), /BTemplate00/);
    assert.equal(await page.locator("#dtNativeDictionaryStatus").innerText(), "Общий словарь пока не создан.");
    await page.evaluate(() => { badList = true; });
    await page.locator("#dtNativeCatalogRefresh").click();
    await page.waitForFunction(() => document.querySelector("#dtNativeCatalogStatus").dataset.error === "true");
    assert.equal(await page.locator("#dtNativeCatalogList li").count(), 0);

    await page.evaluate(async () => {
      const next = nativeState("C");
      RNAssistantDataTemplates.setContext(next, false);
      RNAssistantDataTemplates.setContext(next, true);
      await Promise.resolve();
      RNAssistantDataTemplates.setContext(next, false);
    });
    await page.waitForFunction(() => document.querySelector("#dtNativeCatalogList li")?.textContent.includes("CTemplate00"));
    assert.equal(await page.evaluate(() => calls.some(call => call.type === "listTabularProjectTemplates" &&
      call.payload.bindingId === "binding-C")), true, "a busy project operation must not consume the initial refresh");

    await page.evaluate(() => { RNAssistantDataTemplates.unbind(); RNAssistantDataTemplates.bind(); });
    const before = await page.evaluate(() => calls.length);
    await page.locator("#dtNativeCatalogRefresh").click();
    await page.waitForFunction(before => calls.length === before + 1, before);
    assert.deepEqual(errors, []);
    console.log("PASS native catalog: navigation, bounded reads, inert content, exact heads, stale responses and rebind");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
