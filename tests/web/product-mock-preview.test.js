"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const { chromium } = require("playwright");

const root = path.resolve(__dirname, "../..");

async function serve() {
  const process = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    let output = "";
    process.stdout.on("data", chunk => {
      output += chunk.toString();
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)\n/.exec(output);
      if (match) resolve(match[1]);
    });
    process.once("exit", code => reject(new Error("Preview server exited: " + code)));
  });
  return { process, address };
}

(async () => {
  const server = await serve();
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const normal = await browser.newPage();
    const errors = [];
    normal.on("pageerror", error => errors.push(error.message));
    const native = await normal.request.get(server.address + "web/");
    const preview = await normal.request.get(server.address + "preview/web/");
    const comparison = await normal.request.get(server.address + "product-preview.html");
    assert.equal(native.status(), 200);
    assert.equal(preview.status(), 200);
    assert.equal(comparison.status(), 200);
    assert.equal((await native.text()).includes("mock-bridge.js"), false);
    assert.equal((await native.text()).includes("preview/fixtures.js"), false);
    assert.equal((await preview.text()).includes("mock-bridge.js"), true);
    assert.equal((await preview.text()).includes('src="/preview/fixtures.js"'), true);
    assert.equal((await comparison.text()).includes('src="/preview/web/?scenario=ready"'), true);

    await normal.goto(server.address + "preview/web/?scenario=ready");
    await normal.waitForFunction(() => state.activeChatId === "preview-chat" && state.skills.length === 1 && !state.initializePromise);
    assert.equal(await normal.evaluate(() => state.bridgeUnavailable), false);
    const previewExamples = await normal.evaluate(() => ({
      skill: PrototypeFixtures.skill,
      report: PrototypeFixtures.report,
      chat: PrototypeFixtures.chat
    }));
    assert.deepEqual(await normal.evaluate(() => ({
      title: state.chats[0].Title,
      question: state.messages[0].content || state.messages[0].Content,
      answer: state.messages[1].content || state.messages[1].Content
    })), await normal.evaluate(() => ({
      title: PrototypeFixtures.chat.title,
      question: PrototypeFixtures.chat.user,
      answer: PrototypeFixtures.chat.assistant
    })));
    assert.equal(await normal.evaluate(() => state.activeRunViewState.lifecycle), "completed");
    assert.equal(await normal.evaluate(() => state.activeRunViewState.reason), "model_done");
    assert.equal(await normal.locator("#messages .message-outcome, #messages .agent-result-card").count(), 0,
      "a real model_done projection does not add a technical completion card");
    assert.equal(await normal.locator("#messages .chat-artifact-card").count(), 1);
    assert.deepEqual(await normal.evaluate(() => send("getRunChanges", {
      chatId: "preview-chat", runId: "preview-run-initial"
    })), { chatId: "preview-chat", runId: "preview-run-initial", complete: true, items: [] });
    assert.equal(await normal.locator("#chatSessionList [data-tree-key='chat:preview-chat']").count(), 1);
    assert.equal(await normal.locator("#chatSessionList [data-tree-key='chat:preview-summary']").count(), 1);
    assert.equal(await normal.locator("#agentArsenalButton, #clearChatButton").count(), 0,
      "chat header only keeps the demo's inspector action");
    assert.equal(await normal.locator("#productMobileMenuButton svg").count(), 1);
    assert.equal(await normal.locator("#toggleChatInspectorButton svg").count(), 1);
    assert.equal(await normal.locator("#productMobileMenuButton").isVisible(), true,
      "the central heading keeps its navigation button at wide widths");
    assert.ok(await normal.locator('#chatForm').evaluate(node => node.getBoundingClientRect().height) < 140,
      'the composer keeps its former two-line height');
    await normal.locator('#composerAddButton').click();
    assert.deepEqual(await normal.locator('#composerAddMenu .composer-menu-label').allTextContents(),
      ['Файл с компьютера', 'Выделение из Office', 'Материал чата', 'Планирование', 'Сжать контекст']);
    assert.equal(await normal.locator('#composerAddMenu').evaluate(node => getComputedStyle(node).borderRadius), '9px',
      'composer action menu uses the demo shape');
    await normal.locator('#composerOpenMaterialsButton').click();
    assert.equal(await normal.locator('#tab-artifacts').evaluate(node => node.classList.contains('active')), true);
    await normal.locator('.section-rail [data-section="chat"]').click();
    const chatHeaderInsets = () => normal.evaluate(() => {
      const rect = selector => document.querySelector(selector).getBoundingClientRect();
      const header = rect("#chatDropZone .chatbar"), left = rect("#productMobileMenuButton"),
        title = rect("#chatDropZone .chat-title-block"), right = rect("#toggleChatInspectorButton");
      return [Math.round(left.left - header.left), Math.round(title.left - left.right),
        Math.round(header.right - right.right)];
    });
    assert.deepEqual(await chatHeaderInsets(), [10, 6, 10], "both panel buttons sit close to the header edges");
    assert.equal(await normal.locator("#chatScopeIcon svg").count(), 1);
    const railIcons = await normal.evaluate(() => {
      const center = selector => {
        const rect = document.querySelector(selector).getBoundingClientRect();
        return rect.left + rect.width / 2;
      };
      return [center('.section-rail [data-section="chat"] svg'),
        center('#pluginRail [data-plugin-id="data-templates"] svg')];
    });
    assert.equal(railIcons[0], railIcons[1], "plugin icon uses the same rail alignment as built-in sections");
    await normal.locator("#toggleChatInspectorButton").click();
    await normal.locator("#chatInspector").waitFor({ state: "visible" });
    await normal.waitForFunction(() => Math.abs(document.getElementById("chatInspector").getBoundingClientRect().width - 292) < 0.5);
    assert.equal(await normal.locator("#chatInspector").isVisible(), true);
    assert.equal(await normal.locator("#productMobileMenuButton").isVisible(), true,
      "the left navigation button remains visible beside the chat title with the inspector open");
    assert.deepEqual(await chatHeaderInsets(), [10, 6, 10], "the buttons keep their spacing beside an open inspector");
    assert.match(await normal.locator("#chatInspectorBody").textContent(), /Продажи · сентябрь\.xlsx/);
    const inspectorGeometry = await normal.evaluate(() => {
      const main = document.querySelector("#chatDropZone").getBoundingClientRect();
      const divider = document.querySelector("#chatInspectorSplitter").getBoundingClientRect();
      const panel = document.querySelector("#chatInspector").getBoundingClientRect();
      return { joined: Math.abs(main.right - divider.left) < 1 && Math.abs(divider.right - panel.left) < 1,
        header: document.querySelector("#chatInspector .chat-inspector-head").getBoundingClientRect().height };
    });
    assert.deepEqual(inspectorGeometry, { joined: true, header: 52 });
    await normal.locator("#chatInspectorSwitcher summary").click();
    await normal.locator('[data-chat-inspector-view="abilities"]').click();
    assert.match(await normal.locator("#chatInspectorBody").textContent(), /Анализ продаж/);
    await normal.locator("#toggleChatInspectorButton").click();
    await normal.locator("#chatInspector").waitFor({ state: "hidden" });
    for (const width of [900, 560]) {
      await normal.setViewportSize({ width, height: 720 });
      await normal.locator("#toggleChatInspectorButton").click();
      await normal.waitForFunction(() => {
        const layout = document.getElementById("chatLayout").getBoundingClientRect();
        const panel = document.getElementById("chatInspector").getBoundingClientRect();
        return panel.width > 300 && Math.abs(panel.right - layout.right) < 1 &&
          panel.left >= layout.left - 1;
      });
      assert.equal(await normal.locator("#toggleChatInspectorButton").getAttribute("aria-expanded"), "true",
        "the overlay inspector opens at " + width + "px");
      if (width === 900) {
        await normal.locator("#chatInspectorSwitcher summary").focus();
        await normal.keyboard.press("Escape");
      } else {
        await normal.locator("#closeChatInspectorButton").click();
      }
      await normal.locator("#chatInspector").waitFor({ state: "hidden" });
      assert.equal(await normal.locator("#toggleChatInspectorButton").evaluate(node => node === document.activeElement), true,
        "closing the overlay returns focus to its toggle");
    }
    await normal.setViewportSize({ width: 1280, height: 720 });
    const progress = [];
    await normal.evaluate(() => {
      window.__previewProgress = [];
      chrome.webview.addEventListener("message", event => {
        if (event.data.type === "progress" && event.data.id === "inbox:preview-chat")
          window.__previewProgress.push(event.data.payload);
      });
    });
    await normal.locator("#chatInput").fill("Что в отчёте?");
    await normal.locator("#sendButton").click();
    await normal.waitForFunction(() => state.messages.some(message =>
      String(message.Content || message.content || "").includes("Это смоделированный ответ")));
    progress.push(...await normal.evaluate(() => window.__previewProgress));
    assert.ok(progress.some(item => item.phase === "thinking"));
    assert.equal(progress.filter(item => item.phase === "streaming").length, 2);
    assert.equal(await normal.locator("#chatInput").inputValue(), "");
    await normal.locator('.section-rail [data-section="library"]').click();
    assert.deepEqual(await normal.evaluate(() => ['common.resources_read', 'common.html_workspace_write_file',
      'common.plan_doc_save', 'excel.inspect'].map(id => RNAssistantLibraryGroups.category(id).title)),
      ['Ресурсы', 'HTML', 'Планы', 'Excel']);
    assert.equal(await normal.locator('#instructionsList [data-tree-key="library:skills:excel"]').count(), 1,
      "skills use the shared Excel namespace group");
    assert.equal(await normal.locator('#instructionsList [data-tree-key="library:tools:excel"]').count(), 1,
      "tools use the shared Excel namespace group");
    await normal.locator('#instructionsList [data-tree-key="skill:0"]').click();
    assert.equal(await normal.evaluate(() => {
      const box = document.querySelector('#instructionsList [data-tree-key="skill:0"] .navigation-tree-icon svg').getBBox();
      return box.x >= 0 && box.y >= 0 && box.x + box.width <= 24 && box.y + box.height <= 24;
    }), true, "skill icon stays inside the shared 24px viewBox");
    assert.match(await normal.locator('#instructionsList [data-tree-key="skill:0"]').textContent(), /Анализ продаж/);
    assert.equal(await normal.locator("#skillTitle").textContent(), "Анализ продаж");
    async function libraryEdges() {
      return normal.evaluate(() => {
        const nav = document.querySelector("#instructionsLayout > .global-nav .product-nav-heading").getBoundingClientRect();
        const header = document.querySelector("#instructionsLayout .library-editor-pane:not(.hidden) > .resource-editor-head").getBoundingClientRect();
        const tabs = document.querySelector("#instructionsLayout .library-editor-pane:not(.hidden) > :is(.rna-editor-tabs, .library-editor-mode-row)").getBoundingClientRect();
        const divider = document.querySelector("#instructionsLayout > .splitter").getBoundingClientRect();
        return { navBottom: nav.bottom, headerBottom: header.bottom, navEnd: nav.right,
          headerStart: header.left, headerEnd: header.right, mainStart: divider.right,
          tabsStart: tabs.left, tabsEnd: tabs.right, tabsTop: tabs.top };
      });
    }
    const skillEdges = await libraryEdges();
    assert.equal(skillEdges.navBottom, skillEdges.headerBottom);
    assert.equal(skillEdges.headerStart, skillEdges.mainStart);
    assert.equal(skillEdges.tabsStart, skillEdges.mainStart);
    assert.equal(skillEdges.tabsEnd, skillEdges.headerEnd);
    assert.equal(skillEdges.tabsTop, skillEdges.headerBottom);
    assert.equal(await normal.locator("#skillEditorPanel .rna-editor-tabs.skill-page-tabs").count(), 1);
    assert.equal(await normal.locator("#skillActionsMenu.rna-action-menu .rna-action-menu-body > button svg").count(), 4);
    assert.equal(await normal.locator('.skill-page-tabs [data-skill-page="instruction"][aria-current="page"]').count(), 1);
    const productTabStyle = await normal.locator(".skill-page-tabs").evaluate(tabs => {
      const style = getComputedStyle(tabs);
      return { background: style.backgroundColor, border: style.borderBottomColor,
        borderWidth: style.borderBottomWidth, width: tabs.getBoundingClientRect().width,
        paneWidth: tabs.closest(".skill-editor-pane").getBoundingClientRect().width,
        contentGap: document.querySelector("#skillForm label").getBoundingClientRect().top - tabs.getBoundingClientRect().bottom };
    });
    assert.ok(productTabStyle.width >= productTabStyle.paneWidth - 1);
    assert.ok(productTabStyle.contentGap >= 31 && productTabStyle.contentGap <= 33);
    await normal.waitForFunction(() => state.skills[0]._sourceLoaded[""] === true);
    const original = await normal.evaluate(() => getCodeEditorValue("skillBodyInput"));
    assert.equal(original, await normal.evaluate(() => PrototypeFixtures.skill));
    await normal.locator("#skillEditorPanel .CodeMirror").click();
    await normal.keyboard.press("ControlOrMeta+End");
    await normal.keyboard.type("\nПроверка mock bridge.");
    assert.equal(await normal.locator("#saveSkillsButton").isVisible(), true);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills[0].Revision === "preview-skill-2" && !state.skillLibraryDirty);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /Проверка mock bridge/);
    await normal.locator("#skillActionsMenu summary").click();
    const productMenuStyle = await normal.locator("#skillActionsMenu .rna-action-menu-body").evaluate(menu => {
      const style = getComputedStyle(menu);
      return { background: style.backgroundColor, border: style.borderColor,
        radius: style.borderRadius, padding: style.padding, shadow: style.boxShadow };
    });
    await normal.locator("#skillPreviewToggleButton").click();
    assert.equal(await normal.locator("#skillActionsMenu[open]").count(), 0);
    assert.equal(await normal.locator("#skillPreviewToggleButton svg").count(), 1);
    assert.equal(await normal.locator("#skillPreviewToggleButton .rna-action-menu-label").textContent(), "Вернуться к редактору");
    await normal.locator("#skillActionsMenu summary").click();
    await normal.locator("#skillPreviewToggleButton").click();
    await normal.locator("#skillActionsMenu summary").click();
    await normal.locator("#cloneSkillButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && state.skillLibraryDirty);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && !state.skillLibraryDirty &&
      state.skills.some(skill => skill.Id === "excel.sales_analysis.copy" && skill.Revision === "preview-skill-3"));
    await normal.locator('[data-skill-page="files"]').click();
    assert.equal(await normal.locator('.skill-page-tabs [data-skill-page="files"][aria-current="page"]').count(), 1);
    normal.once("dialog", dialog => dialog.accept("details.md"));
    await normal.locator("#addSkillReferenceButton").click();
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => !state.skillLibraryDirty &&
      state.skills[state.selectedSkillIndex].References.some(reference => reference.Path === "references/details.md" && reference.Revision));
    await normal.locator("#skillFileList .skill-file-row").nth(1).getByText("Открыть").click();
    await normal.waitForFunction(() => state.skills[state.selectedSkillIndex]._sourceLoaded["references/details.md"] === true);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /# details/);
    assert.equal(await normal.locator("#skillForm.rna-skill-fields").count(), 1);
    await normal.locator('#instructionsList [data-tree-key="tool:0"]').click();
    assert.deepEqual(await libraryEdges(), skillEdges, "tool and skill use one full-width editor grid");
    assert.equal(await normal.locator("#toolEditorPanel .rna-editor-tabs.tool-page-tabs").count(), 1);
    assert.equal(await normal.locator("#toolActionsMenu .rna-action-menu-check #toolEnabledInput").count(), 1);
    await normal.locator('.tool-page-tabs [data-tool-page="schema"]').click();
    assert.equal(await normal.locator('.tool-page-tabs [data-tool-page="schema"][aria-current="page"]').count(), 1);
    assert.equal(await normal.locator('[data-tool-page-view="schema"]').isVisible(), true);
    await normal.locator('#instructionsList [data-tree-key="prompt:0"]').click();
    assert.deepEqual(await libraryEdges(), skillEdges, "instruction header and controls meet the tree line");
    await normal.locator('.section-rail [data-section="vba"]').click();
    assert.equal(await normal.locator("#tab-vba .rna-editor-tabs.vba-mode-toggle").count(), 1);
    await normal.locator("#refreshVbaEmptyButton").click();
    await normal.waitForFunction(() => state.vba.modules.length > 0);
    assert.equal(await normal.evaluate(() => state.vba.modules[0].code),
      await normal.evaluate(() => PrototypeFixtures.vba));
    await normal.locator('.vba-mode-toggle [data-vba-mode="diff"]').click();
    assert.equal(await normal.locator('.vba-mode-toggle [data-vba-mode="diff"][aria-current="page"]').count(), 1);
    assert.equal(await normal.locator('[data-vba-view="diff"]').isVisible(), true);
    await normal.locator('.section-rail [data-section="artifacts"]').click();
    await normal.locator("#artifactCatalogOverviewRows .artifact-overview-link").first().waitFor();
    assert.equal(await normal.locator("#artifactCatalogOverviewRows tr").count(), 9);
    assert.equal(await normal.locator("#artifactCatalogAll").evaluate(node => node.closest('[role="tree"]')), null,
      "the catalog shortcut stays a button outside the tree");
    assert.equal(await normal.locator('#htmlWorkspaceTree [data-tree-key="catalog-owner::chat"] > .navigation-tree-row > .navigation-tree-icon').count(), 0,
      "owner groups use the demo tree spacing without a redundant icon");
    assert.deepEqual(await normal.evaluate(() => {
      const styles = selector => {
        const style = getComputedStyle(document.querySelector(selector));
        return [style.minHeight, style.borderRadius, style.paddingRight, style.transitionProperty];
      };
      return styles("#artifactCatalogAll");
    }), await normal.evaluate(() => {
      const style = getComputedStyle(document.querySelector('#htmlWorkspaceTree [data-tree-key="catalog-owner::chat"] > .navigation-tree-row'));
      return [style.minHeight, style.borderRadius, style.paddingRight, style.transitionProperty];
    }), "the standalone button inherits the shared tree navigation geometry and motion");
    assert.equal(await normal.locator("#htmlWorkspaceTree .navigation-tree-row.selected").count(), 0,
      "the overview does not select a second tree row");
    await normal.locator("#artifactCatalogOverviewRows tr").first().locator("td").nth(1).click();
    await normal.waitForFunction(() => document.querySelector("#artifactDetailPreview").textContent.includes("Москва"));
    assert.ok((await normal.locator("#artifactDetailPreview").textContent()).includes("18,4%"));
    assert.equal(await normal.locator("#artifactCatalogAll").getAttribute("aria-current"), null);
    const allMaterialsNormal = await normal.locator("#artifactCatalogAll").evaluate(node => getComputedStyle(node).backgroundColor);
    await normal.locator("#artifactCatalogAll").hover();
    await normal.waitForTimeout(180);
    assert.notEqual(await normal.locator("#artifactCatalogAll").evaluate(node => getComputedStyle(node).backgroundColor),
      allMaterialsNormal, "the separate catalog button has the shared tinted hover");
    await normal.locator("#artifactCatalogAll").click();
    assert.equal(await normal.locator("#htmlWorkspaceTree .navigation-tree-row.selected").count(), 0);
    await normal.locator("#artifactCatalogOverviewRows").getByRole("button", { name: "Продажи по регионам.json" }).click();
    await normal.locator("#artifactCatalogPreviewBody .rn-json-viewer").waitFor();
    await normal.locator("#artifactCatalogPreviewBody").getByRole("button", { name: "Текст", exact: true }).click();
    assert.ok((await normal.locator("#artifactCatalogPreviewBody").textContent()).includes("7420000"));
    await normal.locator("#artifactCatalogAll").click();
    await normal.locator("#artifactCatalogOverviewRows").getByRole("button", { name: "Диаграмма.png" }).click();
    await normal.locator("#artifactCatalogPreviewBody .rn-image-viewer").waitFor();
    assert.equal(await normal.locator("#artifactCatalogStatus").textContent(), "");
    await normal.locator('#pluginRail [data-plugin-id="data-templates"]').hover();
    await normal.locator('.rail-hover-card').getByRole('button', { name: 'Удалить с панели' }).waitFor();
    await normal.locator('#pluginRail [data-plugin-id="data-templates"]').click();
    await normal.locator('#pluginNavigationTree [data-tree-key="sources"]').waitFor();
    await normal.locator('#pluginNavigationTree [data-tree-key="sources"] > .navigation-tree-row').click();
    await normal.waitForFunction(() => document.querySelector("#pluginPreviewMount iframe")?.contentDocument?.documentElement?.dataset.previewReady === "true");
    await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtViewSources").waitFor({ state: "visible" });
    await normal.locator('[data-nav-search-toggle="pluginNavigationSearch"]').click();
    await normal.locator("#pluginNavigationSearch").fill("production-form");
    assert.equal(await normal.locator('#pluginNavigationTree [data-tree-key="template:production-form"]').count(), 1);
    assert.equal(await normal.locator('#pluginNavigationTree [data-tree-key="sources"]').count(), 0);
    await normal.locator("#pluginNavigationSearch").press("Escape");
    assert.equal(await normal.locator('#pluginNavigationTree [data-tree-key="sources"]').count(), 1);
    assert.equal(await normal.locator("#pluginNavigationToggle, #pluginNavigationClose").count(), 0,
      "Data Templates uses the shared panel button");
    assert.equal(await normal.locator("#productMobileMenuButton").getAttribute("aria-controls"), "pluginNavigationPane");
    assert.deepEqual(await normal.evaluate(() => {
      const rect = selector => document.querySelector(selector).getBoundingClientRect();
      const nav = rect("#pluginNavigationPane .product-nav-heading"), header = rect("#pluginWorkbench .plugin-toolbar"),
        toggle = rect("#productMobileMenuButton"), title = rect("#pluginWorkbench .plugin-title");
      return [Math.round(nav.height), Math.round(header.height),
        Math.round(toggle.left - header.left), Math.round(title.left - toggle.right)];
    }), [52, 52, 10, 6], "plugin headers and the shared button follow the demo spacing");
    await normal.locator('#pluginNavigationTree [data-tree-key="template:production-form"] > .navigation-tree-row').click();
    await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtTemplateEditor").waitFor({ state: "visible" });
    await normal.waitForFunction(() => document.querySelector("#pluginPreviewMount iframe")?.contentDocument?.querySelector("#dtViewTemplates .dt-section-heading h3")?.textContent === "production-form");
    assert.equal(await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtViewTemplates .dt-section-heading h3").textContent(), "production-form");
    await normal.locator('#pluginNavigationTree [data-tree-key="templates"] > .navigation-tree-row > .navigation-tree-label').click();
    await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtTemplateEditor").waitFor({ state: "hidden" });
    await normal.waitForFunction(() => document.querySelector("#pluginPreviewMount iframe")?.contentDocument?.querySelector("#dtViewTemplates .dt-section-heading h3")?.textContent === "Шаблоны преобразования");
    assert.equal(await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtViewTemplates .dt-section-heading h3").textContent(), "Шаблоны преобразования");
    await normal.setViewportSize({ width: 750, height: 720 });
    await normal.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-compact"));
    await normal.locator("#pluginNavigationPane").waitFor({ state: "hidden" });
    assert.equal(await normal.locator("#pluginNavigationPane").isVisible(), false);
    await normal.locator("#productMobileMenuButton").click();
    await normal.waitForFunction(() => Math.abs(document.querySelector("#pluginNavigationPane").getBoundingClientRect().width - 290) < 0.5);
    assert.equal(await normal.evaluate(() => {
      const main = document.querySelector(".plugin-data-templates .plugin-content").getBoundingClientRect();
      const button = document.querySelector("#productMobileMenuButton").getBoundingClientRect();
      return Math.round(button.left - main.left);
    }), 10, "the project tree and shared button expand together");
    await normal.setViewportSize({ width: 390, height: 720 });
    await normal.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-single"));
    await normal.locator("#productMobileMenuButton").click();
    await normal.locator("#pluginNavigationPane").waitFor({ state: "visible" });
    assert.equal(await normal.locator("#pluginNavigationPane [data-plugin-nav-icon]").count(), 0,
      "the tree heading does not duplicate the panel icon");
    await normal.locator("#productMobileMenuButton").click();
    await normal.setViewportSize({ width: 1280, height: 720 });
    await normal.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-wide"));
    await normal.waitForFunction(() => document.querySelector("#pluginPreviewMount iframe")?.contentDocument?.documentElement?.dataset.previewReady === "true");
    await normal.locator('#pluginNavigationTree [data-tree-key="templates"] > .navigation-tree-row > .navigation-tree-label').click();
    await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtViewTemplates").waitFor({ state: "visible" });
    assert.equal(await normal.frameLocator("#pluginPreviewMount iframe").locator("#dtViewTemplates").isVisible(), true);
    await normal.locator('.section-rail [data-section="artifacts"]').click();
    await normal.locator("#artifactCatalogAll").click();
    await normal.locator("#artifactCatalogOverviewRows").getByRole("button", { name: "Методика расчёта.pdf" }).click();
    await normal.locator("#artifactCatalogPreviewBody .rn-pdf-viewer").waitFor();
    await normal.locator("#artifactCatalogPreviewBody .rn-pdf-thumbnail-rail").waitFor();
    assert.equal(await normal.locator("#artifactCatalogStatus").textContent(), "");
    await normal.locator('.section-rail [data-section="chat"]').click();
    await normal.locator("#chatScopeSwitcher summary").click();
    await normal.locator('#chatScopeSwitcher [data-workspace-scope="projects"]').click();
    assert.deepEqual(await normal.locator('#projectChatTree [data-tree-key^="project:"]').evaluateAll(nodes =>
      nodes.map(node => node.dataset.treeKey).sort()),
    ['project:preview-data-workspace', 'project:preview-plan-workspace', 'project:preview-plugin-workspace'],
      'the workspace tree includes the plugin source workspace opened earlier');
    await normal.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"] > .navigation-tree-row > .navigation-tree-label').click();
    await normal.locator('#projectChatTitle').getByText('План запуска').waitFor();
    await normal.locator('#projectChatSearch').fill('Подготовка');
    assert.equal(await normal.locator('#projectChatTree [data-tree-key^="project:"]').count(), 1,
      'search finds a chat inside its workspace');
    await normal.locator('#projectChatSearch').fill('');
    await normal.locator('#projectChatTree [data-tree-key="project:preview-data-workspace"] > .navigation-tree-row > .navigation-tree-label').click();
    await normal.locator("#projectChatTitle").getByText("Подготовка отчёта").waitFor();
    await normal.locator("#projectChatMessages .project-chat-suggest").click();
    assert.match(await normal.locator("#projectChatInput").inputValue(), /Сравни сентябрь с августом/);
    await normal.locator("#projectChatInput").fill("");
    await normal.locator("#projectChatNew").click();
    await normal.waitForFunction(() => document.querySelector("#projectChatTree [aria-current='page']")?.textContent.includes("Новый чат"));
    await normal.locator("#projectChatInput").fill("Проверь источники");
    await normal.locator("#projectChatSend").click();
    await normal.locator("#projectChatStop").waitFor({ state: "visible" });
    await normal.locator("#projectChatStop").click();
    await normal.waitForFunction(() => document.querySelector("#projectChatNotice").textContent.includes("прервано"));
    assert.match(await normal.locator("#projectChatMessages").textContent(), /Проверь источники/);
    await normal.locator('#projectChatConnect').click();
    await normal.locator('#projectChatConnectionPath').fill('C:\\Projects\\Archive');
    await normal.waitForFunction(() => !document.querySelector('#projectChatConnectionSubmit').disabled);
    await normal.locator('#projectChatConnectionSubmit').click();
    await normal.locator('#projectChatTree .navigation-tree-label').getByText('Archive', { exact: true }).waitFor();
    await normal.locator('#projectChatCreate').click();
    await normal.locator('#projectChatConnectionPath').fill('C:\\Projects\\NewWorkspace');
    await normal.locator('#projectChatConnectionSubmit').click();
    await normal.locator('#projectChatTree .navigation-tree-label').getByText('NewWorkspace', { exact: true }).waitFor();
    const workspaceIsolation = await normal.evaluate(async () => {
      const second = await send('createProjectChat', { workspaceId: 'preview-plan-workspace', title: 'Другой проект' });
      let crossWorkspace = false;
      try { await send('getProjectChat', { workspaceId: 'preview-data-workspace', sessionId: second.sessionId }); }
      catch (_) { crossWorkspace = true; }
      const projects = (await send('getProjectChats', {})).projects;
      return { crossWorkspace, secondCount: projects.find(p => p.workspaceId === 'preview-plan-workspace').chats.length,
        firstCount: projects.find(p => p.workspaceId === 'preview-data-workspace').chats.length };
    });
    assert.deepEqual(workspaceIsolation, { crossWorkspace: true, secondCount: 2, firstCount: 3 });
    await normal.locator('#projectChatRefresh').click();
    await normal.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"] > .navigation-tree-row').hover();
    normal.once('dialog', dialog => dialog.accept());
    await normal.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"] > .navigation-tree-row').getByRole('button', { name: 'Удалить историю «План продаж»' }).click();
    await normal.waitForFunction(async () => !(await send('getProjectChats', {})).projects.some(p => p.workspaceId === 'preview-plan-workspace'));
    assert.equal(await normal.locator('#projectChatTree [data-tree-key="project:preview-plan-workspace"]').count(), 0,
      'deleting history removes the workspace from the tree');
    const reconnected = await normal.evaluate(() => send('connectProjectWorkspace', { rootPath: 'C:\\Projects\\SalesPlan' }));
    assert.equal(reconnected.selectedWorkspaceId, 'preview-plan-workspace', 'reconnecting keeps the workspace identity');
    assert.equal(reconnected.projects.find(p => p.workspaceId === 'preview-plan-workspace').chats.length, 0);
    const mockProjectCycle = await normal.evaluate(async () => {
      const before = (await send("getProjectChats", {})).projects.map(p => p.workspaceId).sort();
      await send("disablePlugin", { pluginId: "data-templates" });
      const disabled = await send("getProjectChats", {});
      const candidate = await send("checkPlugin", { pluginId: "data-templates" });
      await send("applyPlugin", { pluginId: "data-templates", candidateId: candidate.candidateId });
      const unpublishedProject = await send("getProjectChats", {});
      await send("openPlugin", { pluginId: "data-templates" });
      const reopened = await send("getProjectChats", {});
      return { disabledPreserves: JSON.stringify(disabled.projects.map(p => p.workspaceId).sort()) === JSON.stringify(before),
        unpublishedPreserves: JSON.stringify(unpublishedProject.projects.map(p => p.workspaceId).sort()) === JSON.stringify(before),
        reopened: reopened.projects.find(p => p.workspaceId === 'preview-data-workspace').name,
        count: reopened.projects.find(p => p.workspaceId === 'preview-data-workspace').chats.length };
    });
    assert.deepEqual(mockProjectCycle, { disabledPreserves: true, unpublishedPreserves: true,
      reopened: "Управленческая отчётность", count: 3 });
    await normal.locator('#pluginAdd').click();
    await normal.locator('#pluginCreateForm').waitFor({ state: 'visible' });
    await normal.locator('#pluginName').fill('Сводка продаж');
    await normal.locator('#pluginTask').fill('Собрать сводку продаж');
    assert.equal(await normal.locator('#pluginRoot').isVisible(), false, 'the default source folder needs no path input');
    await normal.locator('#pluginIconTrigger').click();
    await normal.locator('#pluginBuiltinIcons [data-icon-id="draw"]').click();
    await normal.locator('#pluginIconDescription').fill('Три растущих столбца');
    await normal.locator('#pluginIconDrawButton').click();
    const drawnIcons = await normal.evaluate(() => send('getPluginIcons', {}));
    assert.equal(drawnIcons.items.length, 1, 'preview saves a simulated drawing immediately');
    await normal.locator('#pluginCreateSubmit').click();
    await normal.locator('#pluginChatMessages .message.assistant').getByText('Изменение исходников смоделировано в preview. Файлы плагина не менялись.', { exact: true }).waitFor();
    assert.match(await normal.locator('#pluginSource').textContent(), /plugin-workspaces[\\/]Сводка продаж-/, 'preview uses the default source folder');
    await normal.locator('#pluginPreview').click();
    await normal.frameLocator('#pluginPreviewMount iframe').getByRole('heading', { name: 'Сводка продаж' }).waitFor();
    await normal.locator('#pluginApply').click();
    assert.equal(await normal.locator('#pluginRail [data-plugin-id="preview-plugin-1"] svg path').count(), drawnIcons.items[0].paths.length,
      'the simulated publication displays its vector icon');
    await normal.evaluate(id => send('deletePluginIcon', { id }), drawnIcons.items[0].id);
    assert.equal(await normal.locator('#pluginRail [data-plugin-id="preview-plugin-1"] svg path').count(), drawnIcons.items[0].paths.length,
      'removing a saved icon leaves the plugin drawing intact');
    assert.equal(await normal.locator('#pluginRail [data-plugin-id="data-templates"] svg path').first().getAttribute('d'),
      'M3 3v18h18M7 16v-5M12 16V7M17 16V4', 'Data Templates retains its original glyph');
    await normal.locator('#pluginRail [data-plugin-id="preview-plugin-1"]').hover();
    normal.once('dialog', dialog => dialog.accept());
    await normal.locator('.rail-hover-card').getByRole('button', { name: 'Удалить с панели' }).click();
    await normal.locator('#pluginRail [data-plugin-id="preview-plugin-1"]').waitFor({ state: 'detached' });
    await normal.locator('#pluginAdd').hover();
    await normal.locator('.rail-hover-card').getByRole('button', { name: 'Сводка продаж' }).click();
    await normal.locator('#pluginRail [data-plugin-id="preview-plugin-1"]').waitFor();
    const connectedBeforeRemoval = await normal.evaluate(async () =>
      (await send('getProjectChats', {})).projects.map(p => p.workspaceId).sort());
    await normal.locator('#pluginRail [data-plugin-id="data-templates"]').hover();
    normal.once('dialog', dialog => dialog.accept());
    await normal.locator('.rail-hover-card').getByRole('button', { name: 'Удалить с панели' }).click();
    await normal.locator('#pluginRail [data-plugin-id="data-templates"]').waitFor({ state: 'detached' });
    assert.deepEqual(await normal.evaluate(async () => (await send('getProjectChats', {})).projects.map(p => p.workspaceId).sort()), connectedBeforeRemoval,
      'removing the shipped plugin preserves connected workspaces');
    await normal.locator('#pluginAdd').hover();
    await normal.locator('.rail-hover-card').getByRole('button', { name: 'Данные и шаблоны' }).click();
    await normal.locator('#pluginRail [data-plugin-id="data-templates"]').waitFor();
    assert.deepEqual(errors, []);
    await normal.close();

    const demo = await browser.newPage();
    await demo.goto(server.address + "#/library/skill");
    assert.deepEqual(await demo.evaluate(() => ({
      skill: Prototype.fixtures.skill,
      report: Prototype.fixtures.report,
      chat: Prototype.fixtures.chat
    })), previewExamples);
    assert.deepEqual(await demo.evaluate(() => {
      const host = document.createElement('div');
      host.innerHTML = Prototype.renderAgentMessage({ id: 'notice-regression', done: true, blocks: [
        { type: 'text', phase: 'commentary', text: 'Проверяю источники.' },
        { type: 'notice', title: 'Нет данных за август', text: 'Сравнение недоступно.' },
        { type: 'text', phase: 'final', text: 'Доступен только сентябрь.' }
      ] });
      return { notices: host.querySelectorAll('.timeline-notice').length,
        hiddenInHistory: !!host.querySelector('.run-history .timeline-notice'),
        history: host.querySelectorAll('.run-history').length };
    }), { notices: 1, hiddenInHistory: false, history: 1 }, 'demo limitations remain visible outside completed history');
    await demo.locator("#main .rna-skill-fields").waitFor({ state: "attached" });
    assert.equal(await demo.locator("#main .rna-editor-tabs.tabs").count(), 1);
    assert.equal(await demo.locator("#main .rna-action-menu").count(), 1);
    await demo.locator("#main .rna-action-menu summary").click();
    assert.equal(await demo.locator("#main .rna-action-menu[open]").count(), 1);
    assert.deepEqual(await demo.locator("#main .rna-action-menu-body").evaluate(menu => {
      const style = getComputedStyle(menu);
      return { background: style.backgroundColor, border: style.borderColor,
        radius: style.borderRadius, padding: style.padding, shadow: style.boxShadow };
    }), productMenuStyle);
    await demo.keyboard.press("Escape");
    assert.equal(await demo.locator("#main .rna-action-menu[open]").count(), 0);
    assert.equal(await demo.locator('#main .tabs [data-go="library/skill"][aria-current="page"]').count(), 1);
    assert.deepEqual(await demo.locator("#main .tabs").evaluate(tabs => {
      const style = getComputedStyle(tabs);
      return { background: style.backgroundColor, border: style.borderBottomColor, borderWidth: style.borderBottomWidth };
    }), { background: productTabStyle.background, border: productTabStyle.border,
      borderWidth: productTabStyle.borderWidth });
    assert.deepEqual(await demo.locator("#main .rna-skill-fields label").allTextContents(),
      ["ID навыка", "ПриложениеCommonExcelWordPowerPointOutlook", "Описание навыкаСравнение периодов и анализ продаж по регионам", " Доступен агенту"]);
    await demo.close();

    const sideBySide = await browser.newPage();
    await sideBySide.goto(server.address + "product-preview.html");
    await sideBySide.locator("#compare").click();
    assert.equal(await sideBySide.locator("#compare").getAttribute("aria-pressed"), "true");
    assert.equal(await sideBySide.locator("#demo-pane").isVisible(), true);
    await sideBySide.frameLocator("#demo").locator("#section-rail").waitFor({ state: "attached" });
    await sideBySide.waitForFunction(() => {
      const view = document.getElementById("product").contentWindow;
      return view.state && view.state.activeChatId === "preview-chat" && !view.state.initializePromise;
    });
    await sideBySide.locator("#run-example").click();
    await sideBySide.waitForFunction(() => document.getElementById("product").contentWindow.state.messages.length >= 4);
    await sideBySide.locator("#example").selectOption("library/skill");
    await sideBySide.frameLocator("#product").locator("#skillEditorPanel .rna-skill-fields").waitFor();
    await sideBySide.locator("#example").selectOption("materials/image");
    await sideBySide.frameLocator("#product").locator("#artifactCatalogPreviewBody .rn-image-viewer").waitFor();
    await sideBySide.close();

    const failed = await browser.newPage();
    failed.on("pageerror", error => errors.push(error.message));
    await failed.goto(server.address + "preview/web/?scenario=error");
    await failed.locator('.section-rail [data-section="library"]').click();
    await failed.locator('#instructionsList [data-tree-key="skill:0"]').click();
    await failed.waitForFunction(() => document.querySelector("#logBox").textContent.includes("Демонстрационная ошибка чтения навыка"));
    assert.equal(await failed.evaluate(() => !!state.skills[0]._sourceLoaded[""]), false);
    assert.deepEqual(errors, []);
    await failed.close();

    const empty = await browser.newPage();
    await empty.goto(server.address + "preview/web/?scenario=empty");
    await empty.waitForFunction(() => !state.initializePromise && !state.bridgeUnavailable);
    assert.deepEqual(await empty.evaluate(() => ({ chats: state.chats.length, skills: state.skills.length })), { chats: 0, skills: 0 });
    await empty.close();
    console.log("PASS product mock preview: isolated bridge, shared fixtures/components, animated chat, actions, comparison, empty/error scenarios");
  } finally {
    await browser.close();
    server.process.kill("SIGTERM");
    if (server.process.exitCode === null) await once(server.process, "exit");
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
