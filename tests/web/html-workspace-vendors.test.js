"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const web = path.join(__dirname, "../../web");
const read = file => fs.readFileSync(path.join(web, file), "utf8");
const context = vm.createContext({});
context.window = context;
const loaded = [];
context.document = {
  createElement: () => ({}),
  head: { appendChild(script) {
    loaded.push(script.src);
    setImmediate(() => {
      vm.runInContext(read(script.src), context, { filename: script.src, timeout: 3000 });
      script.onload();
    });
  } }
};
vm.runInContext(read("js/app-html-vendor-catalog.js"), context);
vm.runInContext(read("js/app-html-vendor-runtime.js"), context);
vm.runInContext(read("js/app-html-workspace-preview.js"), context);

(async () => {
  const preview = context.RNAssistantHtmlWorkspacePreview;
  const plain = [{ kind: "html", path: "index.html", content: "<main>Plain</main>" }];
  assert.equal(preview.dependencies(plain).length, 0);
  assert.doesNotMatch(preview.build({ files: plain, hostBridge: false }), /data-rn-vendor=/);
  assert.deepEqual(loaded, [], "ordinary workspaces load no vendor code");

  const files = [
    { kind: "html", path: "index.html", content: "<main id=\"grid\"></main>" },
    { kind: "script", path: "app.js", content: "var grid = new Tabulator('#grid', {data: []}); var search = new Fuse([], {keys: ['name']}); var book = XLSX.read(bytes);" }
  ];
  assert.deepEqual(Array.from(preview.missingVendors(files), item => item.id), ["fuse", "tabulator", "sheetjs"]);
  assert.throws(() => preview.build({ files, hostBridge: false }), /Bundled fuse 7\.1\.0 is unavailable/);
  await Promise.all([preview.ensureVendors(files), preview.ensureVendors(files)]);
  assert.deepEqual(loaded, ["js/vendor/fuse.source.js", "js/vendor/tabulator.source.js", "js/vendor/sheetjs.source.js"],
    "concurrent requests load each source carrier once and never fetch a URL");
  assert.deepEqual(Array.from(preview.dependencies(files), item => [item.id, item.loaded]), [
    ["runtime/fuse.min.js", true], ["runtime/tabulator.min.js", true], ["runtime/xlsx.full.min.js", true]
  ]);

  const html = preview.build({ files, hostBridge: false });
  const tabCss = html.indexOf('<style data-rn-vendor="tabulator-6.5.0">');
  const tabJs = html.indexOf('<script data-rn-vendor="tabulator-6.5.0">');
  const fuseJs = html.indexOf('<script data-rn-vendor="fuse-7.1.0">');
  const sheetJs = html.indexOf('<script data-rn-vendor="sheetjs-0.20.3">');
  const workspaceJs = html.indexOf('data-rn-path="app.js"');
  assert.ok(fuseJs >= 0 && fuseJs < tabCss && tabCss < tabJs && tabJs < sheetJs && sheetJs < workspaceJs,
    "selected local CSS and JS precede authored code in the standalone file");
  assert.doesNotMatch(html, /<script[^>]+src=/i, "standalone export has no vendor fetch");
  const fuseSource = html.match(/<script data-rn-vendor="fuse-7\.1\.0">([\s\S]*?)<\/script>/)[1];
  const child = vm.createContext({}); child.window = child;
  vm.runInContext(fuseSource, child, { timeout: 3000 });
  const matches = vm.runInContext("new Fuse([{name:'Invoice'}, {name:'Contract'}], {keys:['name']}).search('Invioce').map(x=>x.item.name)", child);
  assert.deepEqual(Array.from(matches), ["Invoice"], "embedded Fuse works offline on local data");
  const sheetSource = html.match(/<script data-rn-vendor="sheetjs-0\.20\.3">([\s\S]*?)<\/script>/)[1];
  vm.runInContext(sheetSource, child, { timeout: 5000 });
  assert.equal(child.XLSX.version, "0.20.3");
  const restored = vm.runInContext("(function(){var w=XLSX.utils.book_new();XLSX.utils.book_append_sheet(w,XLSX.utils.aoa_to_sheet([['Name'],['Invoice']]),'Data');return ['xlsx','xlsm','xlsb','xls','ods','csv'].map(function(type){var b=XLSX.write(w,{bookType:type,type:'array'});var r=XLSX.read(b,{type:'array'});return {type:type,rows:XLSX.utils.sheet_to_json(r.Sheets[r.SheetNames[0]])};});})()", child, { timeout: 10000 });
  assert.deepEqual(JSON.parse(JSON.stringify(restored)),
    ["xlsx", "xlsm", "xlsb", "xls", "ods", "csv"].map(type => ({ type, rows: [{ Name: "Invoice" }] })),
    "the offline full build reads spreadsheet formats without another package or request");
  assert.equal(preview.dependencies([{ kind: "html", path: "index.html", content: "<p>XLSX report</p>" }]).length, 0,
    "page text alone does not load the large parser");
  const extra = [
    { kind: "html", path: "index.html", content: "<main id='graph'></main>" },
    { kind: "script", path: "report.js", content:
      "new vis.Network(node, {nodes: [], edges: []}); PDFLib.PDFDocument.create(); new JSZip();" }
  ];
  assert.equal(preview.missingVendors([{ kind: "script", path: "words.js", content: "const vis = 'visible';" }]).length, 0,
    "a generic vis variable must not load the graph renderer");
  assert.deepEqual(Array.from(preview.missingVendors(extra), item => item.id), ["vis-network", "pdf-lib", "jszip"]);
  await preview.ensureVendors(extra);
  assert.deepEqual(loaded.slice(3), ["js/vendor/vis-network.source.js", "js/vendor/pdf-lib.source.js",
    "js/vendor/jszip.source.js"]);
  const report = preview.build({ files: extra, hostBridge: false });
  for (const id of ["vis-network-10.1.2", "pdf-lib-1.17.1", "jszip-3.10.2"])
    assert.ok(report.indexOf('data-rn-vendor="' + id + '"') < report.indexOf('data-rn-path="report.js"'),
      id + " must load before authored code");
  assert.doesNotMatch(report, /<script[^>]+src=/i, "graph, PDF and ZIP exports remain self-contained");
  child.setTimeout = setTimeout;
  child.clearTimeout = clearTimeout;
  for (const id of ["vis-network-10.1.2", "pdf-lib-1.17.1", "jszip-3.10.2"]) {
    const source = report.match(new RegExp('<script data-rn-vendor="' + id.replace(/\./g, "\\.") + '">([\\s\\S]*?)<\\/script>'))[1];
    vm.runInContext(source, child, { timeout: 5000 });
  }
  assert.equal(vm.runInContext("typeof vis.Network === 'function' && typeof vis.DataSet === 'function'", child), true);
  const pageCount = await vm.runInContext("(async function(){var p=await PDFLib.PDFDocument.create();p.addPage([200,200]);return (await PDFLib.PDFDocument.load(await p.save())).getPageCount();})()", child);
  assert.equal(pageCount, 1, "embedded pdf-lib creates and reads a PDF without a fetch");
  const zipText = await vm.runInContext("(async function(){var z=new JSZip();z.file('hello.txt','offline');var b=await z.generateAsync({type:'uint8array'});return (await JSZip.loadAsync(b)).file('hello.txt').async('string');})()", child);
  assert.equal(zipText, "offline", "embedded JSZip creates and reads an archive without a fetch");
  console.log("PASS HTML vendors: lazy offline pool, spreadsheet, graph, PDF and ZIP bundles");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
