"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm"), path = require("node:path");
const frames = new Map(), observers = [], window = { next: 0,
  requestAnimationFrame(fn) { frames.set(++this.next, fn); return this.next; },
  cancelAnimationFrame(id) { frames.delete(id); },
  ResizeObserver: class { constructor(fn) { this.fn = fn; this.nodes = new Set(); observers.push(this); }
    observe(node) { this.nodes.add(node); } unobserve(node) { this.nodes.delete(node); } disconnect() { this.nodes.clear(); } },
  MutationObserver: class { observe() {} disconnect() {} }
};
class Box {
  constructor() { this.children = []; this.listeners = new Map(); this.scrollTop = 0; this.scrollHeight = 2000; this.clientHeight = 400; }
  getBoundingClientRect() { return { top: 0, height: this.clientHeight }; }
  addEventListener(name, fn) { this.listeners.set(name, fn); }
  removeEventListener(name, fn) { if (this.listeners.get(name) === fn) this.listeners.delete(name); }
}
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-conversation-view.js"), "utf8"), { window });
const one = new Box(), two = new Box(), first = window.RNAssistantConversationView.get(one), second = window.RNAssistantConversationView.get(two);
const office = { source: "office", sessionId: "same-id" }, workspace = { source: "workspace", workspaceId: "p", sessionId: "same-id" };
first.bind(); first.bind(); second.bind();
assert.equal(observers.length, 2, "one observer owner per view even after rebinding");
first.begin(office); second.begin(workspace);
first.cache.units.old = { node: {} }; second.cache.units.other = { node: {} };
one.scrollTop = 250; one.listeners.get("scroll")();
two.scrollTop = 650; two.listeners.get("scroll")();
assert.equal(first.shouldFollow(), false); assert.equal(second.shouldFollow(), false);
let painted = 0;
first.schedule("stream", () => painted++);
first.schedule("stream", () => painted += 100);
const late = [...frames.values()][0];
first.suspend(); one.scrollTop = 0;
first.begin(workspace); const returnFrame = first.begin(office);
late(); assert.equal(painted, 0, "an old frame cannot paint after A→B→A");
assert.equal(second.cache.units.other.node !== undefined, true, "changing the first view preserves the second projection");
assert.equal(first.cache.units.old, undefined, "old transcript DOM is released, not retained per visited chat");
assert.equal(returnFrame.viewport.top, 250, "returning restores the same target's reading position");
assert.equal(first.begin(office).viewport.top, 250, "loading state does not consume the pending position");
first.restore(returnFrame.viewport);
assert.equal(two.scrollTop, 650, "restoring one view does not scroll another");
let refresh = 0;
first.schedule("stream", () => painted++);
first.setVisible(false);
first.setVisible(true, () => refresh++);
assert.equal(refresh, 1, "a frame cancelled by hide still refreshes on return without another event");
first.setVisible(false); assert.equal(observers[0].nodes.size, 0, "hidden view releases resize observation");
assert.equal(first.canRender(), false);
first.schedule("stream", () => painted++);
assert.equal(frames.size, 0, "hidden view schedules no token paint");
first.setVisible(true, () => refresh++);
first.setVisible(true, () => refresh++);
assert.equal(refresh, 2, "one refresh of the latest state per activation");
assert.equal(observers[0].nodes.has(one), true);
// No full histories/DOM are retained for the remembered positions; visits are bounded.
for (let i = 0; i < 18; i++) {
  first.begin({ source: "office", sessionId: String(i) });
  one.scrollTop = 100 + i; one.listeners.get("scroll")();
}
assert.equal(first.begin(office).follow, true, "evicted presentation position starts at latest");
second.schedule("stream", () => painted++);
const disposedFrame = [...frames.values()].at(-1);
second.dispose(); second.dispose(); disposedFrame();
assert.equal(painted, 0); assert.equal(two.listeners.size, 0);
assert.equal(observers[1].nodes.size, 0);
assert.equal(Object.keys(second.cache.units).length, 0);
assert.equal(second.canRender(), false);
assert.throws(() => second.begin(workspace), /disposed/);
first.dispose();
console.log("conversation-view: isolated targets, scroll, frames, visibility, bounded retention and disposal passed");
