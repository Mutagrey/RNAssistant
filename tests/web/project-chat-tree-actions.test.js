"use strict";
// Focused host-neutral check for project/chat row commands without Playwright.
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");

class Element {
  constructor() { this.hidden = false; this.disabled = false; this.textContent = ""; this.title = ""; this.value = ""; }
  addEventListener() {}
  focus() {}
}

(async () => {
  const elements = new Map(), updates = [], opens = [], sent = [], accepted = [], mountCalls = [], folders = [];
  let remembered = "chat-2", tab = "office";
  const project = { workspaceId: "draw", name: "Рисовалка", rootPath: "/draw", available: true,
    chats: [{ Id: "chat-1", Title: "Набросок", RunStatus: "completed" },
      { Id: "chat-2", Title: "Модель контекста", RunStatus: "completed" }] };
  const panel = {
    selected: () => null,
    newPlacement: () => undefined,
    open: target => opens.push(Object.assign({}, target)),
    find: target => target.sessionId === "chat-3" ? { snapshot: () => null, accept: value => accepted.push(value) } : null
  };
  const context = vm.createContext({
    console, setTimeout, clearTimeout, document: {
      hidden: false,
      addEventListener() {},
      getElementById(id) {
        if (!elements.has(id)) elements.set(id, new Element());
        return elements.get(id);
      }
    },
    state: { pending: {} },
    send: async (type, payload) => {
      sent.push({ type, payload });
      if (type === "getProjectChats") return { selectedWorkspaceId: "draw", projects: [project] };
      if (type === "createProjectChat") return { project: { workspaceId: "draw" }, sessionId: "chat-3" };
      throw new Error("Unexpected command: " + type);
    },
    switchTab: name => { tab = name; },
    openContainingFolder: target => folders.push({ ...target }),
    log() {},
    RNAssistantConversationPanel: panel,
    RNAssistantWorkspaceNavigation: {
      update: (source, nodes, options) => updates.push({ source, nodes, options }),
      notice() {}
    },
    RNAssistantWorkspaceChat: {
      node: () => null,
      mount: value => mountCalls.push(value),
      runStatus: value => value || "completed",
      selectedSession: () => remembered,
      rememberSession: (workspaceId, sessionId) => { if (workspaceId === "draw") remembered = sessionId; },
      snapshotSource: () => ({ accept() {} })
    },
    RNAssistantConversationState: {
      status: () => ({ status: "", label: "" }),
      lifecycle: value => value || "completed"
    },
    RNAssistantIcons: { render: name => '<svg data-icon="' + name + '"></svg>' },
    RNAssistantNavigationTree: { icon: name => '<svg data-icon="' + name + '"></svg>' },
    RNAssistantActionMenu: {
      treeActions(label, actions) {
        return [{ title: label, menu: true, iconHtml: '<svg></svg>', onClick(node, button) {
          opens.push({ menu: label, actions, node, button });
        } }];
      },
      open(button, items, options) { opens.push({ menu: options && options.label, items, button }); },
      closeAll() {}
    },
    RNAssistantAgentCommunication: { marker: () => '<span class="agent-color-dot"></span>' },
    renderChatSessions() {},
    RNAssistantDialog: { open: () => ({ close() {} }) },
    RNAssistantShellLayout: { refresh() {} },
    RNAssistantDockWorkspace: { refreshPresentation() {}, chatNavigationPlacement() {} },
    RNAssistantProjectMarkdownGraph: { open: (workspaceId, name) => opens.push({ workspaceId, name }) }
  });
  context.window = context;
  vm.runInContext(read("js/app-chat-groups.js"), context, { filename: "app-chat-groups.js" });
  context.RNAssistantChatGroups.accept([]);
  vm.runInContext(read("js/app-project-chats.js"), context, { filename: "app-project-chats.js" });

  await context.RNAssistantProjectChats.refresh();
  const current = updates.at(-1), treeProject = current.nodes[0];
  assert.equal(treeProject.selectable, true, "project title opens the project");
  assert.deepEqual(Array.from(treeProject.actions, action => action.title), [
    "Новый чат в «Рисовалка»",
    "Открыть папку проекта «Рисовалка»",
    "Удалить историю «Рисовалка»",
    "Другие действия проекта «Рисовалка»"
  ]);
  assert.equal(treeProject.actions[2].confirmTitle, "Подтвердить удаление истории «Рисовалка»");

  const chat = treeProject.children.find(node => node.sessionId === "chat-1");
  assert.deepEqual(Array.from(chat.actions, action => action.title), [
    "Удалить чат",
    "Другие действия чата «Набросок»"
  ]);
  assert.equal(chat.actions[0].danger, true);
  assert.equal(chat.actions[0].confirmTitle, "Подтвердить удаление чата");
  chat.actions[1].onClick(chat, {});
  const chatMenu = opens.at(-1);
  assert.deepEqual(Array.from(chatMenu.items).filter(item => item.label).map(item => item.label), [
    "Открыть папку проекта", "Диагностика чата", "Переименовать чат", "Группы чата…"
  ]);
  assert.equal(chatMenu.items.some(item => item.label === "Удалить чат"), false, "delete is direct on the row");

  opens.length = 0;
  current.options.onSelect(treeProject);
  assert.equal(tab, "chat");
  assert.equal(mountCalls.at(-1), "project");
  assert.deepEqual(opens.at(-1), { source: "workspace", workspaceId: "draw", sessionId: "chat-2" });

  opens.length = 0;
  treeProject.actions[1].onClick(treeProject, {});
  chatMenu.items[0].onSelect();
  treeProject.actions[3].onClick(treeProject, {});
  const projectMenu = opens.at(-1);
  projectMenu.actions[0].onClick(treeProject, {});
  projectMenu.actions.find(action => action.title === "Граф Markdown").onClick(treeProject, {});
  assert.deepEqual(opens.at(-1), { workspaceId: "draw", name: "Рисовалка" });
  assert.deepEqual(folders, Array(3).fill({ workspaceId: "draw" }), "open commands address the same project folder");

  sent.length = 0;
  treeProject.actions[0].onClick(treeProject, {});
  await Promise.resolve();
  assert.deepEqual(JSON.parse(JSON.stringify(sent.find(call => call.type === "createProjectChat").payload)),
    { workspaceId: "draw", title: "Новый чат" });
  console.log("PASS project/chat tree: label and open command, direct new/open/delete, remaining chat actions under …");
})().catch(error => { console.error(error); process.exitCode = 1; });
