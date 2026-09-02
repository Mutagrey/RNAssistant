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

const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
const captureIndex = index.indexOf("app-echarts-sandbox-runtime.js?v=html-echarts-20260902-2");
const vendorIndex = index.indexOf("js/vendor/echarts.min.js");
const finishIndex = index.indexOf("RNAssistantEChartsSandboxRuntime.finish()");
const previewIndex = index.indexOf("app-html-workspace-preview.js?v=html-echarts-20260902-1");
assert.ok(captureIndex >= 0 && captureIndex < vendorIndex && vendorIndex < finishIndex && finishIndex < previewIndex);
console.log("PASS HTML ECharts: trusted capture spans vendor load and finalizes before preview assembly");

console.log("OK 3/3");
