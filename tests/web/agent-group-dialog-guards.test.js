"use strict";

// Exercise the shipped group adapter at its menu/dialog/bridge boundaries. No
// browser is required; native dialog keyboard, focus and layout are tested by
// agent-groups.test.js, not by this presentation-boundary fixture.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "../../web/js/app-chat-groups.js"), "utf8");
const clone = value => JSON.parse(JSON.stringify(value));
const office = { host: "Excel", documentKey: "book-a", sessionId: "chat-a" };
const workspace = { host: "Workspace", documentKey: "project-a", workspaceId: "project-a", sessionId: "project-chat-a" };
const plugin = { host: "Workspace", documentKey: "plugin-a", workspaceId: "plugin-a", sessionId: "plugin-chat-a" };
function snapshot(scope = office, generation = 1, name = "Шахматы", revision = "r1") {
  return Object.assign({}, scope, { generation, groups: [{ id: "group-a", name, revision, members: [scope.sessionId] }] });
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function fixture(scope = office) {
  const fixture = { calls: [], dialogs: [], logs: [], menu: [], paints: 0,
    source: scope.workspaceId ? "projects" : "office" };
  fixture.respond = async () => ({ scope: snapshot(scope, 2) });
  const ui = vm.createContext({
    console,
    document: { activeElement: null, querySelector: () => null, querySelectorAll: () => [],
      getElementById: () => null, addEventListener() {}, removeEventListener() {} },
    state: { activeChatId: scope.sessionId, bridgeUnavailable: false },
    CSS: { escape: value => value },
    addEventListener() {}, removeEventListener() {},
    renderChatSessions: () => { fixture.paints++; },
    log: (message, kind) => fixture.logs.push({ message, kind }),
    RNAssistantNavigationTree: { icon: () => "" },
    RNAssistantAgentCommunication: { marker: () => "" },
    RNAssistantProjectChats: { scope: () => fixture.source, refreshTree() {} },
    RNAssistantActionMenu: {
      closeAll() { fixture.menu = []; },
      open(trigger, items) { fixture.menu = items; }
    },
    RNAssistantDialog: {
      open(options) {
        const dialog = { options, closed: false,
          close() { if (dialog.closed) return; dialog.closed = true; if (options.onClose) options.onClose(); },
          isOpen: () => !dialog.closed };
        fixture.dialogs.push(dialog);
        return dialog;
      }
    },
    send(type, payload) {
      fixture.calls.push({ type, payload: clone(payload) });
      return fixture.respond(payload);
    },
    prompt() { throw new Error("Native prompt must not be called"); },
    confirm() { throw new Error("Native confirm must not be called"); }
  });
  ui.window = ui;
  vm.runInContext(source, ui, { filename: "app-chat-groups.js" });
  fixture.ui = ui;
  fixture.groups = ui.RNAssistantChatGroups;
  fixture.groups.accept([snapshot(scope)]);
  fixture.nodes = target => fixture.groups.decorate([], [], target || scope, "");
  fixture.create = target => {
    const before = fixture.dialogs.length;
    fixture.groups.createAction(target || scope).onClick();
    return fixture.dialogs.length > before ? fixture.dialogs.at(-1) : null;
  };
  fixture.action = (label, target) => {
    const group = fixture.nodes(target).find(node => node.key.startsWith("chat-group:"));
    assert.ok(group, "group remains in the catalog");
    group.actions[0].onClick(group, null);
    const item = fixture.menu.find(item => item.label === label);
    assert.ok(item, "shared menu exposes " + label);
    assert.equal(item.disabled, false, label + " is admitted");
    const before = fixture.dialogs.length;
    item.onSelect();
    assert.equal(fixture.dialogs.length, before + 1, label + " opens the common dialog");
    return fixture.dialogs.at(-1);
  };
  return fixture;
}

const tests = [
  ["Office, workspace and plugin retain exact bridge scope", async () => {
    for (const scope of [office, workspace, plugin]) {
      const f = fixture(scope);
      await f.create().options.onSubmit("  Новая группа  ");
      assert.deepEqual(f.calls, [{ type: "changeChatGroup", payload: {
        sessionId: scope.sessionId, workspaceId: scope.workspaceId || null,
        action: "Create", groupId: null, revision: null, name: "Новая группа"
      } }]);
    }
  }],
  ["rename pins displayed group revision and scope while input is open", async () => {
    const f = fixture(), target = clone(office), original = snapshot(target);
    f.groups.accept([original]);
    const dialog = f.action("Переименовать группу", target);
    target.sessionId = "other-chat";
    original.groups[0].revision = "r2";
    original.groups[0].name = "Внешнее изменение";
    f.groups.accept([snapshot(office, 2, "Внешнее изменение", "r2")]);
    await dialog.options.onSubmit("  Проверка  ");
    assert.deepEqual(f.calls[0], { type: "changeChatGroup", payload: {
      sessionId: "chat-a", workspaceId: null, action: "Rename",
      groupId: "group-a", revision: "r1", name: "Проверка"
    } }, "an intervening read never upgrades the user's displayed revision");
  }],
  ["delete uses the same exact command and never deletes a chat", async () => {
    const f = fixture();
    f.respond = async () => ({ scope: Object.assign(snapshot(office, 2), { groups: [] }) });
    await f.action("Удалить группу").options.onSubmit();
    assert.deepEqual(f.calls, [{ type: "changeChatGroup", payload: {
      sessionId: "chat-a", workspaceId: null, action: "Delete", groupId: "group-a", revision: "r1"
    } }]);
    assert.equal(f.nodes().length, 0);
  }],
  ["unchanged rename does not dispatch", async () => {
    const f = fixture();
    await f.action("Переименовать группу").options.onSubmit("  Шахматы  ");
    assert.equal(f.calls.length, 0);
  }],
  ["cancel, reset and scope changes retire an unsubmitted dialog", async () => {
    for (const retire of [
      (f, dialog) => dialog.close(),
      f => f.groups.reset(),
      f => { f.source = "projects"; f.groups.dismissDialog(); },
      f => { f.source = "projects"; }
    ]) {
      const f = fixture(), dialog = f.create();
      retire(f, dialog);
      await dialog.options.onSubmit("Старый ввод");
      assert.equal(f.calls.length, 0, "a queued submit cannot outlive its opening context");
    }
  }],
  ["an older close or submit callback cannot affect a newer dialog", async () => {
    const f = fixture(), old = f.create(), current = f.create();
    assert.equal(old.closed, true);
    old.options.onClose();
    await old.options.onSubmit("Устаревшее имя");
    assert.equal(f.calls.length, 0);
    await current.options.onSubmit("Актуальное имя");
    assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0].payload.name, "Актуальное имя");
  }],
  ["one scope admits only one pending write", async () => {
    const f = fixture(), pending = deferred();
    f.respond = () => pending.promise;
    const dialog = f.action("Переименовать группу"), first = dialog.options.onSubmit("Первое имя");
    assert.equal(f.groups.createAction(office).disabled, true);
    assert.equal(f.create(), null, "busy scope cannot open another write dialog");
    await assert.rejects(dialog.options.onSubmit("Повторный ввод"), /недоступны/);
    assert.equal(f.calls.length, 1);
    pending.resolve({ scope: snapshot(office, 2, "Первое имя", "r2") });
    await first;
    assert.equal(f.groups.createAction(office).disabled, false);
    assert.equal(f.calls.length, 1);
  }],
  ["uncertain failure requires a catalog read, without replay", async () => {
    for (const response of [
      () => Promise.reject(new Error("Ответ потерян")),
      () => Promise.reject(new Error("Группа изменилась или удалена. Обновите список.")),
      () => Promise.resolve({ scope: snapshot(workspace, 2) })
    ]) {
      const f = fixture(); f.respond = response;
      const dialog = f.action("Переименовать группу");
      await assert.rejects(dialog.options.onSubmit("Проверка"));
      assert.equal(f.calls.length, 1);
      assert.equal(f.groups.createAction(office).disabled, true);
      assert.ok(f.nodes().some(node => node.title === "Группы недоступны"));
      assert.equal(f.create(), null);
      f.nodes(); f.nodes();
      assert.equal(f.calls.length, 1, "rendering never repeats an uncertain mutation");
      f.groups.accept([snapshot(office, 3)]);
      assert.equal(f.groups.createAction(office).disabled, false);
      assert.equal(f.calls.length, 1, "catalog refresh is inspection, not replay");
    }
  }],
  ["a late failure preserves newer groups and keeps uncertainty visible", async () => {
    const f = fixture(), pending = deferred(); f.respond = () => pending.promise;
    const result = f.action("Переименовать группу").options.onSubmit("Моё имя");
    f.groups.accept([snapshot(office, 7, "Новое прочитанное имя", "r7")]);
    pending.reject(new Error("Ответ потерян"));
    await assert.rejects(result, /Ответ потерян/);
    assert.equal(f.nodes()[0].title, "Новое прочитанное имя", "late failure never restores the old group data");
    assert.ok(f.nodes().some(node => node.title === "Группы недоступны"));
    assert.equal(f.groups.createAction(office).disabled, true,
      "a read while dispatch was pending does not prove that mutation's outcome");
    assert.equal(f.calls.length, 1);
  }],
  ["closing a dispatched dialog does not cancel or replay its mutation", async () => {
    const f = fixture(), pending = deferred(); f.respond = () => pending.promise;
    const dialog = f.action("Переименовать группу"), result = dialog.options.onSubmit("Сохранённое имя");
    dialog.close();
    assert.equal(f.groups.createAction(office).disabled, true, "accepted operation retains the scope's busy guard");
    pending.resolve({ scope: snapshot(office, 2, "Сохранённое имя", "r2") });
    await result;
    assert.equal(f.nodes()[0].title, "Сохранённое имя");
    assert.equal(f.groups.createAction(office).disabled, false);
    assert.equal(f.calls.length, 1, "presentation dismissal sends no cancellation or retry command");
  }],
  ["older successful response cannot replace a newer catalog generation", async () => {
    const f = fixture(), pending = deferred(); f.respond = () => pending.promise;
    const result = f.action("Переименовать группу").options.onSubmit("Моё имя");
    f.groups.accept([snapshot(office, 5, "Более новое имя", "r5")]);
    pending.resolve({ scope: snapshot(office, 2, "Моё имя", "r2") });
    await result;
    assert.equal(f.nodes()[0].title, "Более новое имя");
    assert.equal(f.calls.length, 1);
  }],
  ["reset discards late success and failure even after reopening the same scope", async () => {
    for (const fail of [false, true]) {
      const f = fixture(), pending = deferred(); f.respond = () => pending.promise;
      const dialog = f.action("Переименовать группу"), result = dialog.options.onSubmit("Устаревшее имя");
      f.groups.reset();
      f.groups.accept([snapshot(office, 8, "После переподключения", "r8")]);
      const version = f.groups.version(), paints = f.paints, logs = f.logs.length;
      if (fail) pending.reject(new Error("Поздняя ошибка"));
      else pending.resolve({ scope: snapshot(office, 99, "Устаревшее имя", "r99") });
      await result;
      assert.equal(f.nodes()[0].title, "После переподключения");
      assert.equal(f.groups.version(), version, "a retired request does not publish UI changes");
      assert.equal(f.paints, paints);
      assert.equal(f.logs.length, logs);
      assert.equal(f.groups.createAction(office).disabled, false);
      assert.equal(f.calls.length, 1);
    }
  }]
];

(async () => {
  for (const [name, run] of tests) { await run(); console.log("PASS " + name); }
  console.log("PASS group-dialog guards: " + tests.length + " checks (no browser/layout qualification)");
})().catch(error => { console.error(error); process.exitCode = 1; });
