"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const listeners = {}, frames = new Map(), writes = [];
let nextFrame = 0, refreshes = 0, value = 270;
function classList() {
  const values = new Set();
  return { add: value => values.add(value), remove: value => values.delete(value), contains: value => values.has(value) };
}
const handle = {
  dataset: {}, listeners: {}, attributes: {}, classList: classList(), captured: null,
  addEventListener(type, callback) { this.listeners[type] = callback; },
  setAttribute(name, value) { this.attributes[name] = value; },
  setPointerCapture(id) { this.captured = id; },
  hasPointerCapture(id) { return this.captured === id; },
  releasePointerCapture() { this.captured = null; }
};
const context = vm.createContext({
  window: {
    localStorage: { getItem() { return null; } },
    requestAnimationFrame(callback) { frames.set(++nextFrame, callback); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); },
    addEventListener(type, callback) { listeners['window:'+type] = callback; }
  },
  document: {
    body: { classList: classList() }, querySelector() { return null; },
    addEventListener(type, callback, capture) { listeners[type+(capture?':capture':'')] = callback; }
  },
  refreshCodeEditors() { refreshes += 1; }
});
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-layout.js"), "utf8"), context, { filename: "app-layout.js" });
context.window.RNAssistantSplitter.bind(handle, {
  getValue: () => value,
  setValue: (next, persist) => { value = Math.max(240, Math.min(420, next)); if(persist) writes.push(value); },
  bounds: () => ({min:240,max:420}), defaultValue: () => 270
});
function event(extra) { return {button:0,buttons:1,pointerId:1,clientX:value,preventDefault(){},stopImmediatePropagation(){},...extra}; }
function flushFrame() { for(const [id, callback] of frames) { frames.delete(id); callback(); } }
assert.equal(handle.attributes['aria-valuenow'], '270');
assert.equal(writes.length, 0, "binding does not persist");
handle.listeners.pointerdown(event());
handle.listeners.pointermove(event({clientX:300}));
handle.listeners.pointermove(event({clientX:350}));
assert.equal(value,270,"moves wait for the next frame");
assert.equal(frames.size,1,"moves coalesce into one frame");
flushFrame();
assert.equal(value,350);
assert.equal(writes.length,0,"drag movement does not persist");
assert.equal(refreshes,0,"drag movement does not refresh editors");
handle.listeners.pointermove(event({clientX:370}));
handle.listeners.pointerup();
assert.deepEqual(writes,[370],"release flushes the last movement and persists once");
assert.equal(frames.size,0,"release leaves no delayed movement");
assert.equal(handle.captured,null);
assert.equal(refreshes,1);

handle.listeners.pointerdown(event());
handle.listeners.pointermove(event({clientX:410}));
flushFrame();
listeners['keydown:capture'](event({key:'Escape'}));
assert.equal(value,370,"Escape restores the starting width");
assert.deepEqual(writes,[370],"cancel does not persist");
assert.equal(context.document.body.classList.contains('resizing-pane'),false);
assert.equal(handle.classList.contains('dragging'),false);
handle.listeners.pointerdown(event());
handle.listeners.pointermove(event({clientX:400}));
listeners['window:blur']();
assert.equal(value,400,"window blur flushes and ends dragging");
assert.equal(handle.captured,null);
handle.listeners.keydown(event({key:'ArrowLeft'}));
assert.equal(value,388);
handle.listeners.keydown(event({key:'ArrowLeft',shiftKey:true}));
assert.equal(value,348);
handle.listeners.keydown(event({key:'Home'}));
assert.equal(value,240);
handle.listeners.keydown(event({key:'End'}));
assert.equal(value,420);
handle.listeners.keydown(event({key:'Enter'}));
assert.equal(value,270);
assert.equal(handle.attributes['aria-valuenow'],'270');
console.log("PASS shared splitter: frame coalescing, commit/cancel, capture cleanup, blur, keyboard and ARIA");
