"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const attributes = { "data-theme": "light" };
const properties = {};
const saved = {};
const context = {
  document: { documentElement: {
    getAttribute(name) { return attributes[name]; },
    setAttribute(name, value) { attributes[name] = value; },
    style: { setProperty(name, value) { properties[name] = value; } }
  } },
  localStorage: {
    getItem(key) { return saved[key] || null; },
    setItem(key, value) { saved[key] = value; }
  }
};
context.window = context;
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-accent.js"), "utf8"), context);

assert.equal(properties["--accent"], "#35684B");
assert.equal(attributes["data-accent"], "green");
assert.deepEqual(Array.from(context.RNAssistantAccents.choices, choice => choice.id), [
  "blue", "turquoise", "green", "lime", "yellow", "orange", "pink", "magenta", "violet", "white"
]);
context.RNAssistantAccents.apply("turquoise");
assert.equal(properties["--accent"], "#007AAE");
assert.equal(saved["rnassistant.ui.accent"], "turquoise");
attributes["data-theme"] = "dark";
context.RNAssistantAccents.apply("turquoise", false);
assert.equal(properties["--accent"], "#03B0FD");
context.RNAssistantAccents.apply("white", false);
assert.equal(properties["--accent"], "#FFFFFF");
assert.equal(properties["--accent-on"], "#101216");
assert.equal(properties["--button-primary-text"], "#101216");
attributes["data-theme"] = "light";
context.RNAssistantAccents.apply("white", false);
assert.equal(properties["--accent"], "#30343B");
assert.equal(context.RNAssistantAccents.apply("invalid"), "green");
console.log("PASS accent palette persists and follows light/dark theme");
