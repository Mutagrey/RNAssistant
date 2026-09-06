"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const listeners = {};
const frames = [];
const writes = [];
let refreshes = 0;

function classList() {
  const values = new Set();
  return {
    add(value) { values.add(value); },
    remove(value) { values.delete(value); },
    contains(value) { return values.has(value); }
  };
}

const handle = {
  dataset: {},
  listeners: {},
  addEventListener(type, callback) { this.listeners[type] = callback; }
};

const layout = {
  id: "chatLayout",
  styleWrites: [],
  getAttribute(name) {
    const values = {
      "data-split-key": "chat-tree",
      "data-default-ratio": "0.27",
      "data-min-left": "240",
      "data-min-ratio": "0.2",
      "data-max-ratio": "0.42"
    };
    return values[name] || "";
  },
  getBoundingClientRect() { return { left: 0, width: 1000 }; },
  querySelector(selector) { return selector === ".splitter" ? handle : null; },
  style: {
    setProperty(name, value) { layout.styleWrites.push({ name, value }); }
  }
};

const context = vm.createContext({
  console,
  window: {
    localStorage: {
      getItem() { return null; },
      setItem(key, value) { writes.push({ key, value }); }
    },
    requestAnimationFrame(callback) { frames.push(callback); return frames.length; },
    addEventListener() {}
  },
  document: {
    body: { classList: classList() },
    querySelectorAll(selector) { return selector === ".split-layout" ? [layout] : []; },
    addEventListener(type, callback) { listeners[type] = callback; },
    removeEventListener(type, callback) {
      if (listeners[type] === callback) delete listeners[type];
    }
  },
  refreshCodeEditors() { refreshes += 1; }
});
context.window.document = context.document;
context.setTimeout = callback => { frames.push(callback); return frames.length; };

vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-layout.js"), "utf8"), context,
  { filename: "app-layout.js" });

context.window.initializeSplitPanes();
assert.equal(writes.length, 0, "restore does not persist");
assert.equal(layout.styleWrites.at(-1).value, "27.000%", "default ratio restored");

handle.listeners.mousedown({ preventDefault() {} });
listeners.mousemove({ clientX: 300 });
listeners.mousemove({ clientX: 350 });
assert.equal(writes.length, 0, "drag move does not persist");
assert.equal(refreshes, 0, "drag move does not refresh editors");
assert.equal(frames.length, 1, "drag moves are coalesced into one frame");

frames.shift()();
assert.equal(layout.styleWrites.at(-1).value, "35.000%", "frame applies latest ratio");
assert.equal(writes.length, 0, "frame does not persist");

listeners.mouseup();
assert.equal(writes.length, 1, "mouseup persists once");
assert.equal(writes[0].key, "rnassistant.split.v3.chat-tree");
assert.equal(writes[0].value, "0.35");
assert.equal(refreshes, 1, "editors refresh after drag");
assert.equal(listeners.mousemove, undefined, "mousemove listener removed");

console.log("PASS splitter preferences: drag paints by frame and persists on mouseup");
