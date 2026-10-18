"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const html = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
assert.match(html, /data-vba-mode="history">История/);
assert.doesNotMatch(html, /reviewVbaButton|vbaMetaBox/);

const select = {
  options: [], _value: "", disabled: false,
  set innerHTML(_) { this.options = []; this._value = ""; },
  get value() { return this.options.some(option => option.value === this._value) ? this._value : (this.options[0] || {}).value || ""; },
  set value(value) { this._value = value; },
  appendChild(option) { this.options.push(option); }
};
const context = vm.createContext({
  state: { bridgeUnavailable: false, vba: { backups: [
    { BackupId: "a", ModuleName: "Module1", CreatedUtc: "2026-10-18T10:00:00Z" },
    { BackupId: "b", ModuleName: "Module2", CreatedUtc: "2026-10-18T11:00:00Z" },
    { BackupId: "c", ModuleName: "Module1", CreatedUtc: "2026-10-18T12:00:00Z" }
  ] } },
  document: { createElement: () => ({ value: "", textContent: "" }) },
  $: id => id === "vbaBackupSelect" ? select : null
});
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-vba-project.js"), "utf8"), context);

context.renderVbaBackupChoices({ name: "Module1" });
assert.deepEqual(Array.from(select.options, option => option.value), ["a", "c"]);
select.value = "c";
context.renderVbaBackupChoices({ name: "Module1" });
assert.equal(select.value, "c", "rerender preserves the selected backup");
context.renderVbaBackupChoices({ name: "Module2" });
assert.deepEqual(Array.from(select.options, option => option.value), ["b"]);
assert.equal(select.value, "b", "switching modules cannot retain another module's backup");
context.renderVbaBackupChoices({ name: "Missing" });
assert.equal(select.value, "");
assert.equal(select.disabled, true);
console.log("PASS VBA history: restore choices belong to the selected module");
