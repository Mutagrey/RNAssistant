"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { MessageChannel } = require("node:worker_threads");
const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-preview.js"), "utf8");
const build = vm.createContext({}); build.window = build;
vm.runInContext(source, build);
const html = build.RNAssistantHtmlWorkspacePreview.build({
  files: [{ id: "index", path: "index.html", kind: "html", content: "<main>Bounded</main>" }],
  activeFileId: "index", dataSources: [{ name: "sales", binding: { resource: { uri: "rna://private/source", revision: "r1" }, policy: "exact", view: "table" } }]
});
assert.doesNotMatch(html, /RNAssistantData|RNAssistant\.data|rna:\/\/private/);
const scripts = [...html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)].map(match => match[1]);
assert.equal(scripts.length, 2, "resource and network scripts have real closing tags");
const calls = [], fetches = [], listeners = new Map();
let rawMode = false;
const page = vm.createContext({ MessageChannel, setTimeout, clearTimeout, URL, Headers, Response,
  addEventListener: (name, callback) => listeners.set(name, callback),
  fetch: async (url, options) => {
    fetches.push({ url, options });
    if (rawMode) return new Response(new Uint8Array([0, 1, 254, 255]), { headers: { "Content-Type": "application/octet-stream" } });
    const offset = Number(new URL(url).searchParams.get("offset"));
    return { ok: true, json: async () => ({ resource: { revision: "r1" }, rows: [{ sales: offset + 10 }],
      offset, nextOffset: offset + 1, done: offset === 1 }) };
  },
  parent: { postMessage(message, origin, ports) {
    calls.push(message);
    const value = message.operation === "open" ? rawMode ? {
      leaseId: "raw-lease", url: "https://rnassistant.local-resource/v1/raw-lease",
      descriptor: { reference: { revision: "raw-r1" } }, view: "raw", maxBatchItems: 32000, maxBatchBytes: 4,
      binary: { payload: { byteLength: 4, contentType: "application/octet-stream" } }
    } : { leaseId: "lease", url: "https://rnassistant.local-resource/v1/lease",
      descriptor: { reference: { revision: "r1" } }, view: "table", path: "$", maxBatchItems: 2 } : { closed: true };
    ports[0].postMessage({ ok: true, value }); ports[0].close();
  } }
});
page.window = page;
for (const script of scripts) vm.runInContext(script, page);
(async () => {
  assert.deepEqual(Array.from(page.RN.resources.names()), ["sales"]);
  await assert.rejects(page.RN.resources.open("guessed-name"), /RESOURCE_BINDING_UNKNOWN/);
  assert.equal(calls.length, 0, "unknown bindings never reach the parent");
  const handle = await page.RN.resources.open("sales");
  await assert.rejects(handle.read({ view: "text" }), /RESOURCE_VIEW_UNSUPPORTED/);
  await assert.rejects(handle.read({ limit: 3 }), /RESOURCE_BATCH_BOUNDS/);
  const rows = [];
  for await (const batch of handle.stream({ view: "table", limit: 1, offset: 0 })) rows.push(...batch.rows);
  assert.deepEqual(rows.map(row => row.sales), [10, 11]);
  assert.equal(fetches.length, 2);
  assert.ok(fetches.every(item => item.options.credentials === "omit" && item.url.startsWith("https://rnassistant.local-resource/v1/")));
  assert.deepEqual(calls.map(call => call.operation), ["open", "close"]);
  await assert.rejects(handle.read(), /RESOURCE_LEASE_CLOSED/);
  assert.equal(page.RNAssistantData, undefined);
  console.log("PASS resource data plane: reference-only bootstrap, bounded sequential stream, exact binding and close");
  rawMode = true;
  const raw = await page.RN.resources.open("sales");
  const original = await raw.read({ view: "raw" });
  assert.equal(original.resource.revision, "raw-r1");
  assert.equal(original.mimeType, "application/octet-stream");
  assert.deepEqual(Array.from(new Uint8Array(original.bytes)), [0, 1, 254, 255]);
  assert.equal(original.done, true);
  await raw.close();
  await assert.rejects(raw.read(), /RESOURCE_LEASE_CLOSED/);
  console.log("PASS resource data plane: raw originals use the same binary reader and lease lifecycle");
})().catch(error => { console.error(error); process.exitCode = 1; });
