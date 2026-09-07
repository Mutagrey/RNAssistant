"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const read = name => fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8");
class Node {
  constructor(id) { this.id = id; this.children = []; this.parentNode = null; this.removals = 0; }
  get firstChild() { return this.children[0] || null; }
  get nextSibling() { const siblings = this.parentNode.children; return siblings[siblings.indexOf(this) + 1] || null; }
  insertBefore(node, cursor) {
    if (node.parentNode) node.parentNode.removeChild(node);
    const index = cursor === null ? this.children.length : this.children.indexOf(cursor);
    assert.ok(index >= 0); this.children.splice(index, 0, node); node.parentNode = this;
  }
  removeChild(node) { this.children.splice(this.children.indexOf(node), 1); node.parentNode = null; node.removals++; }
}
const box = new Node("box"), cleaned = [], frames = [];
const context = vm.createContext({ console, state: { activeChatId: "a", messages: [] },
  $: () => box, clearMarkdownEnhancements: node => cleaned.push(node.id), isPanelActive: () => true });
context.window = context;
context.requestAnimationFrame = callback => frames.push(callback);
vm.runInContext(read("app-messages.js"), context);
const unit = (key, signature) => ({ key, signature, render: () => new Node(signature) });
const history = Array.from({ length: 1000 }, (_, i) => unit("message:" + i, "message" + i));
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "first")));
const original = box.children.slice(0, 1000);
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "second")));
assert.equal(box.children.length, 1001);
assert.ok(original.every((node, i) => node === box.children[i] && node.removals === 0));
assert.deepEqual(cleaned, ["first"]);
context.renderedMessagesChatId = "a";
context.buildMessageUnits = () => { throw new Error("stream must not serialize history"); };
context.renderChatResourceNavigation = () => { throw new Error("stream must not rebuild resources"); };
context.isChatNearBottom = () => false;
context.syncChatScroll = () => {};
context.buildLiveMessageUnits = () => [unit("live:reasoning", "reasoning"), unit("live:stream", "third")];
for (let i = 0; i < 100; i++) context.scheduleLiveStreamRender();
assert.equal(frames.length, 1); frames.shift()();
assert.equal(box.children.length, 1002);
assert.deepEqual(box.children.slice(-2).map(node => node.id), ["reasoning", "third"]);
assert.ok(original.every(node => node.removals === 0));
context.buildLiveMessageUnits = () => [];
context.renderStreamingMessages();
assert.equal(box.children.length, 1000);
context.reconcileMessageUnits(box, [history[2], history[0]]);
assert.deepEqual(box.children.map(node => node.id), ["message2", "message0"]);
let fullRenders = 0;
context.state.activeChatId = "b"; context.renderMessages = () => fullRenders++;
context.renderStreamingMessages(); assert.equal(fullRenders, 1, "chat change uses full current projection");
console.log("PASS stream: coalesced tail rendering retains 1000 attached history nodes, orders/replaces/removes live nodes and handles navigation");
const disclosure = (key, open) => ({ open, getAttribute: () => key });
function withDetails(signature, details) {
  const item = unit("live:agent", signature);
  item.render = () => { const node = new Node(signature); node.querySelectorAll = () => details; return node; };
  return item;
}
context.reconcileMessageUnits(box, [withDetails("run-first", [disclosure("step:a", true), disclosure("activity:call-a", false)])]);
const changedDetails = [disclosure("step:new", false), disclosure("step:a", false), disclosure("activity:call-a", true)];
context.reconcileMessageUnits(box, [withDetails("run-updated", changedDetails)]);
assert.deepEqual(changedDetails.map(item => item.open), [false, true, false], "open and closed disclosures survive a live update by identity, despite insertion");
console.log("PASS stream: live update preserves explicitly opened and closed action history");
let listener, sidebarRenders = 0;
const timers = [];
const bridge = vm.createContext({ console }); bridge.window = bridge;
bridge.document = { getElementById: () => null };
bridge.setTimeout = callback => timers.push(callback);
bridge.chrome = { webview: { addEventListener: (_, callback) => { listener = callback; } } };
bridge.renderChatSessions = () => sidebarRenders++;
vm.runInContext(read("app-core.js"), bridge);
bridge.state.activeChatId = "foreground";
bridge.state.pending.request = { type: "sendChat", payload: { chatId: "background" } };
for (let i = 0; i < 100; i++) listener({ data: { type: "progress", id: "request", payload: { contentDelta: "x" } } });
assert.equal(bridge.state.chatRuns.background.stream.length, 100, "every delta is retained immediately");
assert.equal(sidebarRenders, 0); assert.equal(timers.length, 1);
timers.shift()(); assert.equal(sidebarRenders, 1);
console.log("PASS background stream: 100 deltas produce one sidebar refresh without dropping tokens");
