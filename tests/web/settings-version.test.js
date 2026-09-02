"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const context = vm.createContext({});
vm.runInContext(
  fs.readFileSync(path.join(__dirname, "../../web/js/app-settings.js"), "utf8"),
  context,
  { filename: "app-settings.js" }
);

assert.equal(
  context.formatAppVersionLabel("16.1.0-dev+g2a2f69c38d90"),
  "v16.1.0-dev · 2a2f69c"
);
assert.equal(
  context.formatAppVersionLabel("16.1.0-dev+g2a2f69c38d90.dirty"),
  "v16.1.0-dev · 2a2f69c · dirty"
);
assert.equal(
  context.formatAppVersionLabel("16.1.0-dev+source-archive.unknown"),
  "v16.1.0-dev+source-archive.unknown"
);
assert.equal(context.formatAppVersionLabel("16.0.4"), "v16.0.4");
assert.equal(context.formatAppVersionLabel(""), "—");

console.log("PASS settings version shows explicit short commit identity");
