(function () {
  "use strict";

  var product = document.getElementById("product");
  var demo = document.getElementById("demo");
  var example = document.getElementById("example");
  var scenario = document.getElementById("scenario");
  var compare = document.getElementById("compare");
  var sequence = 0;
  var materials = {
    report: "Итоги сентября.md", html: "Динамика продаж.html", json: "Продажи по регионам.json",
    pdf: "Методика расчёта.pdf", image: "Диаграмма.png", sequence: "Обзор регионов"
  };
  var projectTabs = { sources: "Sources", templates: "Templates", parameters: "Parameters" };
  var settingPages = { diagnostics: "service" };

  function waitFor(view, test, current) {
    return new Promise(function (resolve, reject) {
      var started = Date.now();
      function check() {
        if (current !== sequence) return resolve(null);
        try { var result = test(view); if (result) return resolve(result); } catch (error) { /* Frame is loading. */ }
        if (Date.now() - started > 6000) return reject(new Error("Пример не загрузился"));
        setTimeout(check, 40);
      }
      check();
    });
  }

  function click(view, selector) {
    var element = view.document.querySelector(selector);
    if (element) element.click();
    return element;
  }

  async function navigateProduct(route) {
    var current = ++sequence;
    var view = product.contentWindow;
    var ready = await waitFor(view, function (target) {
      return target.state && !target.state.initializePromise && !target.state.bridgeUnavailable;
    }, current);
    if (!ready || current !== sequence) return;
    var parts = route.split("/"), section = parts[0], sub = parts[1];
    if (section === "chat") {
      view.switchTab("chat");
      var chatId = sub === "summary" ? "preview-summary" : sub === "offline" ? "preview-budget" : "preview-chat";
      if (scenario.value !== "empty" && view.state.activeChatId !== chatId) await view.selectChat(chatId);
      if (["context", "abilities", "usage", "activity"].includes(sub)) {
        if (view.document.getElementById("chatInspector").hidden) click(view, "#toggleChatInspectorButton");
        click(view, "#chatInspectorSwitcher summary");
        click(view, '[data-chat-inspector-view="' + sub + '"]');
      }
      return;
    }
    if (section === "materials") {
      if (scenario.value !== "empty" && view.state.activeChatId !== "preview-chat") await view.selectChat("preview-chat");
      view.switchTab("artifacts");
      click(view, "#artifactCatalogAll");
      if (sub === "all" || scenario.value === "empty") return;
      var title = materials[sub];
      var row = await waitFor(view, function (target) {
        return Array.from(target.document.querySelectorAll("#artifactCatalogOverviewRows .artifact-overview-link"))
          .find(function (button) { return button.textContent === title; });
      }, current);
      if (row) row.click();
      return;
    }
    if (section === "library") {
      view.switchTab("instructions");
      if (scenario.value === "empty") return;
      var key = sub === "prompt" ? "prompt:0" : ["skill", "references"].includes(sub) ? "skill:0" : "tool:0";
      var row = await waitFor(view, function (target) {
        return target.document.querySelector('#instructionsList [data-tree-key="' + key + '"]');
      }, current);
      if (!row) return;
      row.click();
      if (sub === "references") click(view, '[data-skill-page="files"]');
      var toolPage = { tool: "main", schema: "schema", implementation: "implementation", test: "test", docs: "docs" }[sub];
      if (toolPage && toolPage !== "main") click(view, '[data-tool-page="' + toolPage + '"]');
      return;
    }
    if (section === "vba") {
      view.switchTab("vba");
      if (scenario.value === "empty") return;
      click(view, "#refreshVbaEmptyButton");
      await waitFor(view, function (target) { return target.state.vba && target.state.vba.modules.length; }, current);
      click(view, '#vbaModuleList [role="treeitem"]');
      if (sub !== "code") click(view, '[data-vba-mode="' + (sub === "diff" ? "diff" : sub) + '"]');
      return;
    }
    if (section === "projects") {
      var plugin = await waitFor(view, function (target) {
        return target.document.querySelector('#pluginRail [data-plugin-id="data-templates"]');
      }, current);
      if (!plugin) return;
      await view.RNAssistantPlugins.select("data-templates");
      var frame = await waitFor(view, function (target) {
        var embedded = target.document.querySelector("#pluginPreviewMount iframe");
        return embedded && embedded.contentDocument && embedded.contentDocument.querySelector("#dtTabSources") && embedded;
      }, current);
      if (frame && projectTabs[sub]) click(frame.contentWindow, "#dtTab" + projectTabs[sub]);
      return;
    }
    if (section === "settings") {
      view.switchTab("settings");
      click(view, '#settingsNavTree .navigation-tree-label[data-settings-page="' + (settingPages[sub] || sub) + '"]');
    }
  }

  function showDemo(route) {
    if (compare.getAttribute("aria-pressed") === "true") demo.src = "/#/" + route;
  }

  function showSelected() {
    var route = example.value;
    showDemo(route === "chat/summary" || route === "chat/offline" ? "chat/main" : route);
    navigateProduct(route).catch(function (error) { console.error("Preview example:", error); });
  }

  example.addEventListener("change", showSelected);
  scenario.addEventListener("change", function () {
    product.src = "/preview/web/?scenario=" + encodeURIComponent(scenario.value);
    document.getElementById("run-example").disabled = scenario.value === "empty";
  });
  product.addEventListener("load", showSelected);
  document.getElementById("run-example").addEventListener("click", async function () {
    example.value = "chat/main";
    await navigateProduct("chat/main");
    var view = product.contentWindow;
    var input = view.document.getElementById("chatInput");
    var send = view.document.getElementById("sendButton");
    if (!input || !send || !view.PrototypeFixtures) return;
    input.value = view.PrototypeFixtures.chat.user;
    input.dispatchEvent(new view.Event("input", { bubbles: true }));
    if (!send.disabled) send.click();
  });
  compare.addEventListener("click", function () {
    var active = compare.getAttribute("aria-pressed") !== "true";
    compare.setAttribute("aria-pressed", String(active));
    document.getElementById("comparison").dataset.compare = String(active);
    document.getElementById("demo-pane").hidden = !active;
    if (active) showDemo(example.value);
  });
  showSelected();
}());
