"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
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
  assert.ok(item.id && item.version && item.license && item.gitHead && item.npmTarball && item.npmIntegrity);
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
console.log("PASS vendor gate: exact package versions, tarball integrity, commits and license texts are recorded");

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
assert.equal(entries.size, 38);
console.log("PASS vendor gate: 38 runtime files have exact size/hash and no unmanifested sibling");

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
const loadedVendorPaths = Array.from(index.matchAll(/(?:src|href)="((?:js|css)\/vendor\/[^"?#]+)[^"]*"/g), match => match[1]);
for (const loaded of loadedVendorPaths) assert.ok(entries.has(loaded), "index loads unmanifested vendor file " + loaded);
assert.equal(/(?:src|href)="(?:https?:)?\/\//i.test(index), false, "main UI contains a remote asset URL");
const cspMatch = index.match(/<meta\s+http-equiv="Content-Security-Policy"\s+content="([^"]+)"/i);
assert.ok(cspMatch, "main UI CSP is required");
const csp = directives(cspMatch[1]);
assert.deepEqual(csp["connect-src"], ["'none'"]);
assert.deepEqual(csp["worker-src"], ["'none'"]);
assert.deepEqual(csp["font-src"], ["'self'"]);
assert.deepEqual(manifest.policy.csp.connectSrc, csp["connect-src"]);
assert.deepEqual(manifest.policy.csp.workerSrc, csp["worker-src"]);
assert.deepEqual(manifest.policy.csp.fontSrc, csp["font-src"]);
console.log("PASS vendor gate: index loads only manifested local assets and CSP denies connect/workers by default");

assert.equal(manifest.policy.runtimeNetwork, "deny");
assert.equal(manifest.policy.telemetry, "deny");
assert.equal(manifest.policy.autoUpdate, "deny");
assert.equal(manifest.policy.dynamicImport, "deny");
assert.equal(manifest.policy.workers.mode, "deny-until-manifested-host-factory");
assert.deepEqual(manifest.policy.workers.requiredLifecycle, ["create-by-manifest-id", "cancel", "terminate"]);
console.log("PASS vendor gate: new vendor/worker admission is fail-closed and lifecycle-owned");
console.log("OK 5/5");
