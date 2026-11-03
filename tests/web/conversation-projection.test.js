"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const ui = vm.createContext({ state: {}, requestAnimationFrame() { return 1; }, cancelAnimationFrame() {} });
ui.window = ui;
for (const file of ["app-conversation-state", "app-conversation-view", "app-run-view-state", "app-chat-state", "app-chat-composer", "app-workspace-chat"])
  vm.runInContext(fs.readFileSync("web/js/" + file + ".js", "utf8"), ui);
const api = ui.RNAssistantConversationState, workspace = ui.RNAssistantWorkspaceChat;
const box = () => ({ children: [], scrollTop: 0, scrollHeight: 100, clientHeight: 100 });
const a = ui.RNAssistantConversationView.get(box()), b = ui.RNAssistantConversationView.get(box());
const source = sessionId => workspace.snapshotSource({ workspaceId: "w", sessionId });
const data = (sessionId, revision, count = 1) => ({ project: { workspaceId: "w" }, sessionId,
  state: { revision }, messages: Array.from({ length: count }, (_, i) => ({ Id: String(i) })),
  messageStartIndex: 0, messageTotalCount: count });
const first = a.bindProjection(source("same"), workspace.latestSnapshot);
const second = b.bindProjection(ui.officeChatSource("same"), (_, next) => api.messageWindow(null, next, false));
const office = { activeChatId: "same", messages: [{ Id: "office" }], messageStartIndex: 0, messageTotalCount: 1 };
second.accept(office);
const initial = data("same", 4, 1000);
assert.equal(first.accept(initial), initial, "the view owns the accepted object, without a second snapshot copy");
assert.equal(first.snapshot().messages.length, 240);
assert.equal(first.snapshot().messageStartIndex, 760);
assert.equal(first.accept(data("same", 3)), initial, "the source revision guard still rejects older history");
const previousPage = data("same", 4, 80); previousPage.messageTotalCount = 1000;
first.accept(previousPage);
const latestPage = () => ({ ...data("same", 4, 80), messageStartIndex: 920, messageTotalCount: 1000,
  messages: Array.from({ length: 80 }, (_, i) => ({ Id: String(920 + i) })) });
assert.equal(first.accept(latestPage()).messageStartIndex, 0, "refresh preserves the earlier reading window");
assert.equal(first.accept(latestPage(), { latestMessages: true }).messageStartIndex, 920,
  "an explicit jump to latest replaces the earlier window");
assert.equal(second.snapshot(), office, "another source/view retains its own history");
const accepted = first.snapshot();
assert.throws(() => first.accept(data("other", 9)), /другого чата/);
assert.equal(first.snapshot(), accepted);
assert.equal(a.bindProjection(source("same"), workspace.latestSnapshot), first, "refresh preserves owner identity");
a.begin({ source: "workspace", workspaceId: "w", sessionId: "same" });
a.suspend(); a.begin({ source: "workspace", workspaceId: "w", sessionId: "same" });
a.setVisible(false);
assert.equal(a.projection(), first, "hide/reparent retains the bounded snapshot");
a.setVisible(true, () => {});
const oldActions = a.scope();
const next = a.bindProjection(source("next"), workspace.latestSnapshot);
assert.equal(oldActions.isCurrent(), false, "changing the data owner retires actions before the next paint");
next.accept(data("next", 1));
assert.equal(first.isCurrent(), false); assert.equal(first.snapshot(), null);
assert.equal(first.accept(data("same", 99)), null, "a late reply cannot resurrect a replaced owner");
const returned = a.bindProjection(source("same"), workspace.latestSnapshot);
returned.accept(data("same", 5));
assert.notEqual(returned, first, "A→B→A creates a new owner, not a revived callback target");
assert.equal(next.snapshot(), null);
assert.equal(second.snapshot(), office);
for (let i = 0; i < 100; i++) {
  const previous = a.projection();
  const owner = a.bindProjection(source(String(i)), workspace.latestSnapshot);
  owner.accept(data(String(i), 1));
  assert.equal(previous.snapshot(), null, "visited targets do not accumulate retained histories");
}
// A normal refresh is another tail page, not permission to erase visible input.
for (const merge of [
  (current, incoming, options) => workspace.latestSnapshot(current, incoming, options),
  (current, incoming, options) => ui.mergeOfficeChatSnapshot(current, incoming, options)
]) {
  const page = (revision, start, total) => ({ ...data("timeline", revision), activeChatId: "timeline", sessionRevision: revision,
    messages: Array.from({ length: total - start }, (_, i) => ({ Id: String(start + i), Content: "message " + (start + i) })),
    messageStartIndex: start, messageTotalCount: total });
  const before = page(10, 0, 6);
  before.messages[0] = { Id: "0", Role: "user", Content: "Applied queued input" };
  const tail = page(11, 2, 8); tail.messages[0].Content = "Updated terminal result";
  const merged = merge(before, tail);
  assert.equal(merged.messageStartIndex, 0, "new revision retains an overlapping history prefix");
  assert.equal(merged.messages[0].Content, "Applied queued input", "consumed queue input remains visible");
  assert.equal(merged.messages[2].Content, "Updated terminal result", "newer data wins for overlapping identities");
  assert.equal(merged.messages.length, 8);
  assert.equal(merge(merged, page(12, 4, 9), { latestMessages: true }).messageStartIndex, 4,
    "explicit jump still replaces the window");
  assert.equal(merge(merged, page(12, 0, 2)).messages.length, 2, "history truncation does not resurrect removed messages");
  const changed = page(12, 2, 8); changed.messages[0].Id = "replacement";
  assert.equal(merge(merged, changed).messageStartIndex, 2, "identity discontinuity does not join different histories");
  const bounded = merge(page(20, 0, 240), page(21, 220, 260));
  assert.equal(bounded.messages.length, 240, "growing tail remains bounded");
  assert.equal(bounded.messageStartIndex, 20, "tail following retires only the oldest bounded entries");
}
const last = a.projection(); a.dispose();
assert.equal(last.snapshot(), null); assert.equal(last.isCurrent(), false);
assert.equal(a.projection(), null);
assert.equal(a.bindProjection(source("after-close"), workspace.latestSnapshot), null);
assert.equal(second.snapshot(), office, "closing one view does not dispose a sibling");
b.dispose(); assert.equal(second.snapshot(), null);
console.log("PASS conversation projections: independent owners, revision/window bounds, retained hide, A→B→A and disposal");
