(function () {
  "use strict";
  var generation = 0, timer = null, key = "", opened = false, chat = null, latest = null;
  var activeTab = "skills", expanded = {}, scrolls = { skills: 0, tools: 0 };
  var statuses = { selected: "Подключён", pending: "Подключается", unavailable: "Недоступен", available: "Доступен" };
  var paths = {
    skill: '<path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5Z"/>',
    tool: '<path d="M14 6a5 5 0 0 0-6 6L3 17a2.8 2.8 0 0 0 4 4l5-5a5 5 0 0 0 6-6l-3 3-4-4Z"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6Z"/><path d="m8 12 3 3 5-6"/>',
    chevron: '<path d="m9 5 7 7-7 7"/>'
  };
  function el(tag, text, cls) {
    var node = document.createElement(tag);
    if (text !== undefined) node.textContent = text;
    if (cls) node.className = cls;
    return node;
  }
  function icon(name, cls) {
    var node = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    node.setAttribute("viewBox", "0 0 24 24"); node.setAttribute("aria-hidden", "true");
    if (cls) node.setAttribute("class", cls);
    node.innerHTML = paths[name]; return node;
  }
  function disclosure(id, cls) {
    var node = el("details", undefined, cls); node.dataset.arsenalKey = id;
    node.open = !!expanded[id];
    node.addEventListener("toggle", function () { expanded[id] = node.open; });
    return node;
  }
  function matches(item, query) {
    return ((item.Name || "") + " " + item.Id + " " + (item.Description || "")).toLocaleLowerCase().indexOf(query) >= 0;
  }
  function skillName(id) {
    var details = latest.details, item = details.Selected.concat(details.Available).find(function (skill) { return skill.Id === id; });
    return item && item.Name || id;
  }
  function technical(parent, item, prefix) {
    var row = disclosure("technical:" + prefix + item.Id, "arsenal-technical");
    row.appendChild(el("summary", "Технические сведения"));
    row.appendChild(el("code", item.Id));
    if (item.Revision) row.appendChild(el("span", "Версия " + item.Revision.slice(0, 12)));
    parent.appendChild(row);
  }
  function appendSkill(parent, skill) {
    var row = disclosure("skill:" + skill.Id, "arsenal-skill"), summary = el("summary"), copy = el("div", undefined, "arsenal-skill-copy");
    summary.appendChild(icon("skill", "arsenal-item-icon"));
    var title = el("div", undefined, "arsenal-item-heading");
    title.appendChild(el("strong", skill.Name || skill.Id));
    title.appendChild(el("span", statuses[skill.Status] || "Недоступен", "arsenal-badge is-" + (statuses[skill.Status] ? skill.Status : "unavailable")));
    copy.appendChild(title);
    if (skill.Description) copy.appendChild(el("p", skill.Description, "arsenal-description"));
    if (skill.Reason) copy.appendChild(el("p", skill.Reason, "arsenal-reason"));
    summary.appendChild(copy); summary.appendChild(icon("chevron", "arsenal-chevron")); row.appendChild(summary);
    var detail = el("div", undefined, "arsenal-skill-detail"), tools = skill.Tools || [];
    if (tools.length) {
      detail.appendChild(el("h3", "Инструменты навыка"));
      var list = el("ul", undefined, "arsenal-related-tools");
      tools.forEach(function (id) {
        var tool = latest.details.Tools.find(function (item) { return item.Id === id; }), item = el("li", tool && tool.Name || id);
        if (tool && (tool.Skills || []).length > 1) item.appendChild(el("span", "Общий инструмент", "arsenal-meta"));
        list.appendChild(item);
      });
      detail.appendChild(list);
    } else detail.appendChild(el("p", "Инструкции без дополнительных инструментов.", "arsenal-meta"));
    if ((skill.References || []).length) {
      detail.appendChild(el("h3", "Материалы"));
      var refs = el("ul", undefined, "arsenal-references");
      skill.References.forEach(function (reference) { refs.appendChild(el("li", reference)); }); detail.appendChild(refs);
    }
    technical(detail, skill, "skill:"); row.appendChild(detail); parent.appendChild(row);
  }
  function empty(parent, title, description) {
    var node = el("div", undefined, "arsenal-empty"); node.appendChild(icon("skill"));
    node.appendChild(el("strong", title)); if (description) node.appendChild(el("p", description)); parent.appendChild(node);
  }
  function heading(parent, label, count) {
    var node = el("h3", label, "arsenal-section-title"); node.appendChild(el("span", String(count))); parent.appendChild(node);
  }
  function draw() {
    if (!opened || !latest || !latest.details) return;
    var content = $("agentArsenalContent"), body = $("agentArsenalBody"), focused = document.activeElement;
    var focusedKey = focused && focused.tagName === "SUMMARY" && focused.parentElement.dataset.arsenalKey;
    content.querySelectorAll("details[data-arsenal-key]").forEach(function (node) { expanded[node.dataset.arsenalKey] = node.open; });
    var scroll = body.scrollTop, query = $("agentArsenalSearch").value.trim().toLocaleLowerCase(), value = latest;
    content.replaceChildren();
    if (value.restriction) content.appendChild(el("p", value.restriction, "arsenal-restriction"));
    if (activeTab === "skills") {
      var selected = value.details.Selected.filter(function (skill) { return matches(skill, query); });
      heading(content, value.historical ? "В последней задаче" : "В этой задаче", selected.length);
      selected.forEach(function (skill) { appendSkill(content, skill); });
      if (!selected.length) empty(content, query ? "Навыки не найдены" : "Пока без навыков", query ? "Попробуйте другое название." : "Агент подключит подходящие навыки по ходу задачи. Базовые инструменты уже доступны.");
      var available = value.details.Available.filter(function (skill) {
        return !value.details.Selected.some(function (item) { return item.Id === skill.Id; }) && matches(skill, query);
      });
      var catalog = disclosure("available", "arsenal-catalog"), label = el("summary");
      label.appendChild(icon("chevron", "arsenal-chevron")); label.appendChild(el("span", "Другие навыки"));
      label.appendChild(el("span", String(available.length), "arsenal-meta")); catalog.appendChild(label);
      var list = el("div", undefined, "arsenal-catalog-list"), filled = false;
      function fill() {
        if (!catalog.open || filled) return; filled = true;
        available.forEach(function (skill) { appendSkill(list, skill); });
        if (!available.length) empty(list, query ? "Совпадений нет" : "Других навыков пока нет");
      }
      catalog.appendChild(list); catalog.addEventListener("toggle", fill);
      if (query) catalog.open = true;
      fill(); content.appendChild(catalog);
    } else {
      var found = value.details.Tools.filter(function (tool) { return matches(tool, query); });
      ["direct", "skill"].forEach(function (origin) {
        var items = found.filter(function (tool) { return tool.Origin === origin; });
        if (!items.length) return;
        heading(content, origin === "direct" ? "Базовые инструменты" : "Из подключённых навыков", items.length);
        items.forEach(function (tool) {
          var row = disclosure("tool:" + tool.Id, "arsenal-tool"), summary = el("summary"), copy = el("div", undefined, "arsenal-tool-copy");
          summary.appendChild(icon("tool", "arsenal-item-icon")); copy.appendChild(el("strong", tool.Name || tool.Id));
          if (tool.Description) copy.appendChild(el("p", tool.Description, "arsenal-description"));
          if (tool.RequiresConfirmation) copy.appendChild(el("span", (value.details.Policy || "").indexOf("/auto") >= 0 ? "Автоподтверждение" : "С разрешения", "arsenal-permission"));
          summary.appendChild(copy); summary.appendChild(icon("chevron", "arsenal-chevron")); row.appendChild(summary);
          var detail = el("div", undefined, "arsenal-tool-detail"), skills = tool.Skills || [];
          if (skills.length) detail.appendChild(el("p", (skills.length > 1 ? "Общий инструмент: " : "Навык: ") + skills.map(skillName).join(", "), "arsenal-meta"));
          technical(detail, tool, "tool:"); row.appendChild(detail); content.appendChild(row);
        });
      });
      if (!found.length) empty(content, query ? "Инструменты не найдены" : "Без инструментов", query ? "Попробуйте другое название." : "В этом запросе агент отвечает без инструментов.");
    }
    body.scrollTop = scroll;
    if (focusedKey) content.querySelectorAll("details[data-arsenal-key]").forEach(function (node) {
      if (node.dataset.arsenalKey === focusedKey) node.querySelector("summary").focus({ preventScroll: true });
    });
  }
  function render(value) {
    latest = value;
    $("agentArsenalCounts").textContent = value.toolCount;
    var label = "Арсенал · навыков: " + value.skillCount + " · инструментов: " + value.toolCount;
    $("agentArsenalButton").title = label; $("agentArsenalButton").setAttribute("aria-label", label);
    if (!opened || !value.details) return;
    $("agentArsenalStatus").textContent = value.historical ? "Последняя задача · завершена" : value.prepared ? "Для следующего шага" : value.pending ? "Новые навыки подключаются" : "Текущая задача";
    $("agentArsenalSkillCount").textContent = value.details.Selected.length;
    $("agentArsenalToolCount").textContent = value.details.Tools.length;
    var policy = value.details.Policy || "", line = $("agentArsenalPolicy"); line.replaceChildren(icon("shield"));
    line.appendChild(el("span", policy.indexOf("plan/") === 0 || policy.indexOf("/Deny") >= 0 ? "Изменения запрещены" : policy.indexOf("/auto") >= 0 ? "Изменения — автоматически" : "Изменения — с разрешения"));
    draw();
  }
  function selectTab(tab, focus) {
    scrolls[activeTab] = $("agentArsenalBody").scrollTop; activeTab = tab;
    document.querySelectorAll("[data-arsenal-tab]").forEach(function (node) {
      var current = node.dataset.arsenalTab === tab;
      node.setAttribute("aria-selected", String(current)); node.tabIndex = current ? 0 : -1;
      if (current) { $("agentArsenalContent").setAttribute("aria-labelledby", node.id); if (focus) node.focus(); }
    });
    var label = tab === "skills" ? "Найти навык" : "Найти инструмент";
    $("agentArsenalSearch").placeholder = label; $("agentArsenalSearch").setAttribute("aria-label", label);
    draw(); $("agentArsenalBody").scrollTop = scrolls[tab];
  }
  async function load() {
    var version = ++generation, id = state.activeChatId, details = opened;
    if (!id || state.bridgeUnavailable) return;
    try {
      var response = await send("getAgentArsenal", { chatId: id, includeDetails: details });
      if (version !== generation || id !== state.activeChatId || details !== opened) return;
      if (!response || response.type !== "rnassistant.agentArsenal" || response.contractVersion !== 1 || response.chatId !== id)
        throw new Error("Некорректный снимок арсенала.");
      render(response);
    } catch (error) {
      if (version === generation && id === state.activeChatId && opened)
        $("agentArsenalStatus").textContent = "Не удалось обновить арсенал";
    }
  }
  function setOpen(value, returnFocus) {
    opened = value; generation++; clearTimeout(timer);
    $("agentArsenalPanel").classList.toggle("hidden", !value);
    $("agentArsenalPanel").setAttribute("aria-hidden", value ? "false" : "true");
    $("agentArsenalButton").setAttribute("aria-expanded", value ? "true" : "false");
    if (value) {
      if (typeof closePromptContextInspector === "function") closePromptContextInspector();
      if (!latest || !latest.details) $("agentArsenalStatus").textContent = "Загрузка…";
      load(); $("closeAgentArsenalButton").focus();
    } else if (returnFocus !== false) $("agentArsenalButton").focus();
  }
  function sync() {
    if (!$("agentArsenalButton")) return;
    var id = state.activeChatId || "", summary = typeof activeChatSummary === "function" ? activeChatSummary() : null;
    if (chat !== id) {
      chat = id; generation++; latest = null; key = ""; expanded = {}; scrolls = { skills: 0, tools: 0 };
      $("agentArsenalCounts").textContent = ""; $("agentArsenalContent").replaceChildren();
      $("agentArsenalPolicy").replaceChildren(); $("agentArsenalSearch").value = "";
      $("agentArsenalSkillCount").textContent = "0"; $("agentArsenalToolCount").textContent = "0";
      $("agentArsenalStatus").textContent = "Загрузка…"; selectTab("skills", false);
    }
    $("agentArsenalButton").disabled = !id || !!state.bridgeUnavailable;
    if (!id && opened) setOpen(false, false);
    var next = id + ":" + (state.chatProjectionRevisions && state.chatProjectionRevisions[id] || summary && summary.Revision || 0) +
      ":" + (summary && summary.IsCurrentDocument) + ":" + (summary && summary.Mode);
    if (next === key) return;
    key = next; clearTimeout(timer); timer = setTimeout(load, 150);
  }
  window.RNAssistantArsenal = { sync: sync, refresh: load, close: function () { setOpen(false); } };
  document.addEventListener("DOMContentLoaded", function () {
    $("agentArsenalButton").addEventListener("click", function () { setOpen(!opened); });
    $("closeAgentArsenalButton").addEventListener("click", function () { setOpen(false); });
    $("agentArsenalSearch").addEventListener("input", function () { $("agentArsenalBody").scrollTop = 0; draw(); });
    document.querySelectorAll("[data-arsenal-tab]").forEach(function (node) {
      node.addEventListener("click", function () { selectTab(node.dataset.arsenalTab, false); });
      node.addEventListener("keydown", function (event) {
        if (["ArrowLeft", "ArrowRight", "Home", "End"].indexOf(event.key) < 0) return;
        event.preventDefault(); selectTab(event.key === "Home" ? "skills" : event.key === "End" ? "tools" : activeTab === "skills" ? "tools" : "skills", true);
      });
    });
    document.addEventListener("pointerdown", function (event) {
      if (opened && !$("agentArsenalPanel").contains(event.target) && !$("agentArsenalButton").contains(event.target)) setOpen(false, false);
    });
    document.addEventListener("keydown", function (event) {
      if (opened && event.key === "Escape") { event.preventDefault(); event.stopPropagation(); setOpen(false); }
    });
    sync();
  });
})();
