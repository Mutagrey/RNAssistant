"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const context = vm.createContext({ Deno: {} });
context.window = context;

vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-echarts-sandbox-runtime.js"), "utf8"), context,
  { filename: "app-echarts-sandbox-runtime.js" });
assert.equal(context.define, undefined, "capture is not installed during ordinary startup");
context.RNAssistantEChartsSandboxRuntime.begin();
assert.equal(typeof context.define, "function", "capture remains installed until the vendor executes");
vm.runInContext(fs.readFileSync(path.join(root, "web/js/vendor/echarts.min.js"), "utf8"), context,
  { filename: "echarts.min.js", timeout: 5000 });
context.RNAssistantEChartsSandboxRuntime.finish();
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-preview.js"), "utf8"), context,
  { filename: "app-html-workspace-preview.js" });

assert.equal(context.echarts.version, "5.6.0");
assert.equal(typeof context.RNAssistantEChartsFactory, "function");
assert.equal(context.define, undefined, "temporary AMD capture is removed");

const plain = context.RNAssistantHtmlWorkspacePreview.build({
  activeFileId: "index.html",
  files: [{ id: "index.html", path: "index.html", kind: "html", content: "<main>Plain</main>" }],
  hostBridge: false
});
assert.doesNotMatch(plain, /data-rn-vendor="echarts/);
console.log("PASS HTML ECharts: ordinary workspaces do not embed the chart runtime");

const chart = context.RNAssistantHtmlWorkspacePreview.build({
  activeFileId: "index.html",
  files: [
    { id: "index.html", path: "index.html", kind: "html", content: "<main id=\"chart\"></main>" },
    { id: "app.js", path: "app.js", kind: "script", content: "var chart = echarts.init(document.getElementById('chart')); chart.setOption({});" }
  ],
  hostBridge: false
});
const runtime = chart.match(/<script data-rn-vendor="echarts-5\.6\.0">([\s\S]*?)<\/script>/);
assert.ok(runtime, "chart workspace embeds the pinned runtime");
assert.match(runtime[1], /Licensed to the Apache Software Foundation/);
assert.doesNotMatch(chart, /https?:\/\/[^\s\"']*(?:chart|echarts)/i);

const child = vm.createContext({ Deno: {} });
child.window = child;
vm.runInContext(runtime[1], child, { timeout: 5000 });
assert.equal(child.echarts.version, "5.6.0");
console.log("PASS HTML ECharts: sandbox/export assembly receives the exact local bundle without CDN");

const documentChart = context.RNAssistantHtmlWorkspacePreview.build({
  activeFileId: "dashboard.html",
  files: [
    {
      id: "dashboard.html", path: "dashboard.html", kind: "html",
      content: "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><main id=\"chart\"></main></body></html>"
    },
    {
      id: "dashboard.js", path: "dashboard.js", kind: "script",
      content: "window.__workspaceScriptRan={version:echarts.version,name:RN.resources.names()[0]};"
    }
  ],
  dataSources: [{
    id: "sales", name: "sales",
    binding: { resource: { uri: "rna://test/data", revision: "r1" }, policy: "exact", view: "table" }
  }],
  hostBridge: false
});
const vendorClose = documentChart.indexOf("</script>", documentChart.indexOf('data-rn-vendor="echarts-5.6.0"'));
const workspaceOpen = documentChart.indexOf('data-rn-path="dashboard.js"');
const bodyClose = documentChart.toLowerCase().lastIndexOf("</body>");
assert.ok(vendorClose >= 0 && vendorClose < workspaceOpen && workspaceOpen < bodyClose,
  "workspace scripts follow the complete vendor script and precede the actual body close");
const documentScripts = Array.from(documentChart.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi), match => match[1]);
const documentRuntime = vm.createContext({ Deno: {}, addEventListener() {} });
documentRuntime.window = documentRuntime;
for (const source of documentScripts) vm.runInContext(source, documentRuntime, { timeout: 5000 });
assert.equal(documentRuntime.echarts.version, "5.6.0");
assert.deepEqual(JSON.parse(JSON.stringify(documentRuntime.__workspaceScriptRan)),
  { version: "5.6.0", name: "sales" });
assert.doesNotMatch(documentChart, /rnassistant-html-fetch/);
console.log("PASS HTML ECharts: assembly installs the local runtime and resource API before workspace code");

const fallbackChart = context.RNAssistantHtmlWorkspacePreview.build({
  activeFileId: "dashboard.html",
  files: [
    { id: "dashboard.html", path: "dashboard.html", kind: "html", content: "<main></main>" },
    { id: "dashboard.js", path: "dashboard.js", kind: "script", content: "window.__knownNames=RN.resources.names();" }
  ],
  dataSources: [{
    id: "sales", name: "sales",
    binding: { resource: { uri: "rna://test/data", revision: "r1" }, policy: "exact", view: "table" }
  }],
  hostBridge: false
});
const fallbackRuntime = vm.createContext({ Deno: {}, addEventListener() {} });
fallbackRuntime.window = fallbackRuntime;
Array.from(fallbackChart.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi), match => match[1])
  .forEach(source => vm.runInContext(source, fallbackRuntime, { timeout: 5000 }));
assert.deepEqual(Array.from(fallbackRuntime.__knownNames), ["sales"]);
assert.equal(fallbackRuntime.RNAssistantData, undefined);
console.log("PASS HTML ECharts: only exact binding names are exposed, without data-name fallback");

const missingRuntime = vm.createContext({});
missingRuntime.window = missingRuntime;
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-preview.js"), "utf8"), missingRuntime,
  { filename: "app-html-workspace-preview.js" });
assert.throws(() => missingRuntime.RNAssistantHtmlWorkspacePreview.build({
  activeFileId: "index.html",
  files: [
    { id: "index.html", path: "index.html", kind: "html", content: "<main id=\"chart\"></main>" },
    { id: "app.js", path: "app.js", kind: "script", content: "echarts.init(document.getElementById('chart'));" }
  ],
  hostBridge: false
}), /requires the loaded bundled ECharts 5\.6\.0 dependency/);
console.log("PASS HTML ECharts: standalone export fails closed when the pinned dependency is unavailable");

assert.deepEqual(Array.from(context.RNAssistantHtmlWorkspacePreview.dependencies([
  { id: "dashboard.js", path: "dashboard.js", kind: "script", content: "echarts.init(node);" }
])).map(item => ({ id: item.id, version: item.version, loaded: item.loaded, readOnly: item.readOnly })), [
  { id: "runtime/echarts.min.js", version: "5.6.0", loaded: true, readOnly: true }
]);
assert.equal(context.RNAssistantHtmlWorkspacePreview.dependencies([
  { id: "plain.js", path: "plain.js", kind: "script", content: "render();" }
]).length, 0);
console.log("PASS HTML ECharts: used runtime is projected as one read-only workspace dependency");

const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
const captureIndex = index.indexOf("app-echarts-sandbox-runtime.js?v=ui-lazy-20260903-1");
const vendorIndex = index.indexOf("js/vendor/echarts.min.js");
const finishIndex = index.indexOf("RNAssistantEChartsSandboxRuntime.finish()");
const previewIndex = index.indexOf("app-html-workspace-preview.js?v=ui-lazy-20260903-1");
assert.ok(captureIndex >= 0 && captureIndex < previewIndex);
assert.equal(vendorIndex, -1, "ECharts vendor is not parsed during main WebView startup");
assert.equal(finishIndex, -1, "ECharts capture is finalized by the on-demand loader");
console.log("PASS HTML ECharts: main WebView startup defers the chart vendor until first use");

console.log("OK 7/7");
