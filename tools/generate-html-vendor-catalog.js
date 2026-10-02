"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "web/vendor-manifest.json"), "utf8"));
const outputPath = path.join(root, "web/js/app-html-vendor-catalog.js");
const files = new Map(manifest.files.map(file => [file.path, file]));
const globals = new Set();
const catalog = manifest.packages.filter(item => item.htmlWorkspace).map(item => {
  const option = item.htmlWorkspace;
  assert.match(item.id, /^[a-z][a-z0-9-]*$/);
  assert.match(option.global, /^[A-Za-z_][A-Za-z0-9_]*$/);
  if (option.activation) {
    assert.match(option.activation, /^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$/);
    assert.ok(option.activation.startsWith(option.global + "."),
      "activation must name a member of the declared global: " + item.id);
  }
  for (const field of ["purpose", "whenUseful"])
    assert.ok(typeof option[field] === "string" && option[field].length > 0 && option[field].length <= 160 &&
      !/https?:\/\//i.test(option[field]), "short offline selection metadata is required for " + item.id);
  assert.match(option.dependencyFile, /^[A-Za-z0-9._-]+\.js$/);
  assert.equal(typeof option.scriptOnly, "boolean");
  assert.equal(globals.has(option.global), false, "duplicate HTML vendor global " + option.global);
  globals.add(option.global);
  if (option.sourceCarrier === "echarts-factory") {
    assert.equal(item.id, "echarts");
    assert.equal(files.get("js/vendor/echarts.min.js").package, item.id);
  } else {
    assert.match(option.sourceCarrier, /^js\/vendor\/[A-Za-z0-9._/-]+\.source\.js$/);
    assert.equal(files.get(option.sourceCarrier)?.package, item.id,
      "HTML vendor carrier must be an exact manifested file owned by " + item.id);
  }
  return { id: item.id, version: item.version, global: option.global,
    purpose: option.purpose, whenUseful: option.whenUseful,
    file: option.dependencyFile, loader: option.sourceCarrier, scriptOnly: option.scriptOnly,
    activation: option.activation || option.global };
});

const source = "/* Generated from web/vendor-manifest.json by tools/generate-html-vendor-catalog.js. */\n" +
  "(function () { \"use strict\"; window.RNAssistantHtmlVendorCatalog = Object.freeze(" +
  JSON.stringify(catalog) + "); }());\n";
if (process.argv.includes("--check")) {
  assert.equal(fs.readFileSync(outputPath, "utf8"), source,
    "HTML vendor catalog is stale; run node tools/generate-html-vendor-catalog.js");
} else {
  fs.writeFileSync(outputPath, source);
}
