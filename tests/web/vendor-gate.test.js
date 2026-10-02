"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "../..");
const web = path.join(root, "web");
const manifest = JSON.parse(fs.readFileSync(path.join(web, "vendor-manifest.json"), "utf8"));

function sha256(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

function filesBelow(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const absolute = path.join(directory, entry.name);
    return entry.isDirectory() ? filesBelow(absolute) : [absolute];
  });
}

function webPath(file) {
  return path.relative(web, file).split(path.sep).join("/");
}

function directives(content) {
  const result = {};
  content.split(";").map(value => value.trim()).filter(Boolean).forEach(value => {
    const parts = value.split(/\s+/);
    result[parts.shift()] = parts;
  });
  return result;
}

assert.equal(manifest.schemaVersion, 1);
assert.equal(manifest.sourceUrlsAreProvenanceOnly, true);
const packages = new Map();
for (const item of manifest.packages) {
  assert.ok(item.id && item.version && item.license && item.npmTarball && item.npmIntegrity);
  assert.ok(item.gitHead || (item.sourceRevision === "npm-tarball-only" && item.sourceRevisionNotes),
    "exact source provenance is required for " + item.id);
  assert.equal(packages.has(item.id), false, "duplicate package " + item.id);
  packages.set(item.id, item);
  assert.ok(item.licenseFiles.length > 0, "license files missing for " + item.id);
  for (const license of item.licenseFiles) {
    const absolute = path.join(web, license.path);
    assert.ok(fs.existsSync(absolute), "missing license " + license.path);
    assert.equal(sha256(absolute), license.sha256, "license hash " + license.path);
  }
  assert.deepEqual(item.browserRuntimeDependencies, [], "browser dependency must be bundled or separately manifested: " + item.id);
}
console.log("PASS vendor gate: package versions, tarball integrity, source provenance and license evidence are recorded");

const entries = new Map();
for (const item of manifest.files) {
  assert.ok(packages.has(item.package), "unknown owner " + item.package);
  assert.equal(entries.has(item.path), false, "duplicate runtime path " + item.path);
  assert.match(item.path, /^(?:js|css)\/vendor\//);
  assert.match(item.provenanceUrl, /^https:\/\//);
  const absolute = path.join(web, item.path);
  assert.ok(fs.existsSync(absolute), "missing runtime file " + item.path);
  assert.equal(fs.statSync(absolute).size, item.bytes, "size " + item.path);
  assert.equal(sha256(absolute), item.sha256, "runtime hash " + item.path);
  entries.set(item.path, item);
}
const feather = packages.get("feather-icons");
assert.equal(feather.sourceOnly, true);
assert.equal(manifest.files.some(item => item.package === feather.id), false, "source-only icons must not add a runtime package");
const actual = filesBelow(path.join(web, "js/vendor")).concat(filesBelow(path.join(web, "css/vendor"))).map(webPath).sort();
assert.deepEqual(Array.from(entries.keys()).sort(), actual, "manifest must include every and only vendored runtime file");
assert.ok(packages.has("wunderbaum"));
assert.equal(packages.get("wunderbaum").version, "0.14.1");
assert.deepEqual(packages.get("wunderbaum").packageDependencies, {});
assert.ok(packages.has("viewerjs"));
assert.equal(packages.get("viewerjs").version, "1.12.0");
assert.deepEqual(packages.get("viewerjs").packageDependencies, {});
console.log("PASS vendor gate: " + entries.size + " runtime files have exact size/hash and no unmanifested sibling");

for (const item of manifest.packages.filter(packageItem => packageItem.htmlWorkspace &&
    packageItem.htmlWorkspace.sourceCarrier !== "echarts-factory")) {
  const id = item.id;
  const carrier = fs.readFileSync(path.join(web, item.htmlWorkspace.sourceCarrier), "utf8");
  const match = carrier.match(/\.register\("([^\"]+)", (\{[\s\S]+\})\);\s*$/);
  assert.ok(match, "source carrier must only register pinned source text: " + id);
  assert.equal(match[1], id);
  const source = JSON.parse(match[2]);
  for (const asset of item.upstreamAssets) {
    assert.equal(crypto.createHash("sha256").update(source[asset.role], "utf8").digest("hex"), asset.embeddedSha256,
      "embedded source hash " + id + "/" + asset.role);
    assert.match(asset.sha256, /^[a-f0-9]{64}$/, "upstream source hash is recorded");
  }
  if (item.fontSource) {
    const encoded = source.js.match(/var fontBase64="([A-Za-z0-9+/=]+)";/);
    assert.ok(encoded, "embedded font must be present in " + id);
    const font = Buffer.from(encoded[1], "base64");
    assert.equal(font.length, item.fontSource.bytes);
    assert.equal(crypto.createHash("sha256").update(font).digest("hex"), item.fontSource.sha256,
      "embedded font hash " + id);
    assert.match(item.fontSource.provenanceUrl, /^https:\/\//);
  }
  assert.doesNotMatch(source.js + source.css, /sourceMappingURL=/, "source maps must not trigger extra loads");
  assert.doesNotMatch(source.css, /url\s*\(/i, "workspace CSS must not request unmanifested assets");
}
console.log("PASS vendor gate: HTML source carriers match pinned embedded hashes and have no asset requests");

execFileSync(process.execPath, [path.join(root, "tools/generate-html-vendor-catalog.js"), "--check"]);
assert.deepEqual(manifest.packages.filter(item => item.htmlWorkspace).map(item => item.id),
  ["echarts", "fuse", "tabulator", "sheetjs", "vis-network", "pdf-lib", "pdf-fontkit", "jszip"]);
console.log("PASS vendor gate: HTML runtime catalog is generated from the pinned manifest");

let cssDependencyCount = 0;
for (const item of manifest.files.filter(item => item.path.endsWith(".css"))) {
  const css = fs.readFileSync(path.join(web, item.path), "utf8");
  for (const match of css.matchAll(/url\((?:"([^"]+)"|'([^']+)'|([^)'"\s]+))\)/g)) {
    const target = match[1] || match[2] || match[3];
    if (/^(?:data:|#)/i.test(target)) continue;
    assert.doesNotMatch(target, /^(?:https?:|\/\/)/i, "remote CSS asset " + target);
    const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(item.path), target));
    assert.ok(entries.has(resolved), "CSS dependency is not manifested: " + item.path + " -> " + resolved);
    cssDependencyCount += 1;
  }
}
const fonts = manifest.files.filter(item => /\.woff2$/i.test(item.path));
assert.equal(cssDependencyCount, 20);
assert.equal(fonts.length, manifest.policy.fonts.allowedCount);
assert.deepEqual(manifest.policy.fonts.formats, ["woff2"]);
assert.equal(manifest.files.some(item => /\.(?:wasm|woff|ttf)$/i.test(item.path)), false);
assert.deepEqual(manifest.policy.wasm.allowed, []);
assert.deepEqual(manifest.policy.workers.allowed, []);
console.log("PASS vendor gate: CSS resolves only 20 local WOFF2 files; WASM and workers are absent/denied");

const index = fs.readFileSync(path.join(web, "index.html"), "utf8");
assert.ok(index.indexOf("app-html-vendor-catalog.js?v=") < index.indexOf("app-html-vendor-runtime.js?v="),
  "the manifest-derived catalog loads before the workspace registry");
const loadedVendorPaths = Array.from(index.matchAll(/(?:src|href)="((?:js|css)\/vendor\/[^"?#]+)[^"]*"/g), match => match[1]);
for (const loaded of loadedVendorPaths) assert.ok(entries.has(loaded), "index loads unmanifested vendor file " + loaded);
assert.equal(/(?:src|href)="(?:https?:)?\/\//i.test(index), false, "main UI contains a remote asset URL");
const cspMatch = index.match(/<meta\s+http-equiv="Content-Security-Policy"\s+content="([^"]+)"/i);
assert.ok(cspMatch, "main UI CSP is required");
const csp = directives(cspMatch[1]);
assert.deepEqual(csp["connect-src"], ["https://rnassistant.local-resource/v1/"]);
assert.deepEqual(csp["worker-src"], ["'none'"]);
assert.deepEqual(csp["font-src"], ["'self'"]);
assert.deepEqual(csp["img-src"], ["'self'", "data:", "blob:", "https://rnassistant.local-resource/v1/"]);
assert.deepEqual(manifest.policy.csp.connectSrc, csp["connect-src"]);
assert.deepEqual(manifest.policy.csp.workerSrc, csp["worker-src"]);
assert.deepEqual(manifest.policy.csp.fontSrc, csp["font-src"]);
console.log("PASS vendor gate: index loads only manifested local assets; CSP admits local image Blobs and denies connect/workers");

assert.equal(manifest.policy.runtimeNetwork, "deny");
assert.equal(manifest.policy.telemetry, "deny");
assert.equal(manifest.policy.autoUpdate, "deny");
assert.equal(manifest.policy.dynamicImport, "deny");
assert.equal(manifest.policy.workers.mode, "deny-until-manifested-host-factory");
assert.deepEqual(manifest.policy.workers.requiredLifecycle, ["create-by-manifest-id", "cancel", "terminate"]);
console.log("PASS vendor gate: new vendor/worker admission is fail-closed and lifecycle-owned");
console.log("OK 7/7");
