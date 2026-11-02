(function () {
  "use strict";
  // Presentation of recorded composition and exact wire fields. No protocol admission or bridge calls.
  var groups = { InstructionsAndCatalog: "Инструкции и каталог", Conversation: "История сообщений",
    ToolActivity: "Вызовы и результаты", ResourceReferences: "Ссылки и состояние ресурсов", Resources: "Контекст ресурсов",
    MessagesWithAttachments: "Сообщения с вложениями", Unattributed: "Не распределено" };
  var roles = { system: "Системное", developer: "Приложение", user: "Пользователь", assistant: "Модель", tool: "Инструмент", function: "Функция" };
  function node(tag, text, cls) { var n = document.createElement(tag); if (text != null) n.textContent = text; if (cls) n.className = cls; return n; }
  function field(parts, name) { var item = parts.find(function (p) { return p.key === name; }); return item && item.raw; }
  function value(raw) { try { return JSON.parse(raw); } catch (_) { return undefined; } }
  function composition(trace, count) {
    var source = trace && trace.RequestComposition, seen = new Set(), total = 0, options = 0;
    var valid = source && source.Version === 1 && source.Method === "base_text_media_v1" && typeof source.HasMedia === "boolean" &&
      Number.isSafeInteger(source.TotalEstimatedTokens) && source.TotalEstimatedTokens >= 0 && Number.isInteger(count) && count >= 0 &&
      Array.isArray(source.Items) && source.Items.length <= count + 1 && source.Items.every(function (item) {
        if (!item || !Number.isSafeInteger(item.EstimatedTokens) || item.EstimatedTokens < 0) return false;
        total += item.EstimatedTokens;
        if (item.Group === "ResponseFormat") return item.MessageIndex == null && ++options === 1;
        if (!groups[item.Group] || !Number.isInteger(item.MessageIndex) || item.MessageIndex < 0 || item.MessageIndex >= count || seen.has(item.MessageIndex)) return false;
        seen.add(item.MessageIndex); return true;
      }) && seen.size === count && total === source.TotalEstimatedTokens;
    if (!valid) return null;
    var result = { groups: {}, costs: new Map(), byIndex: new Map(), total: total, media: source.HasMedia };
    source.Items.forEach(function (item) {
      if (item.MessageIndex == null) return;
      (result.groups[item.Group] || (result.groups[item.Group] = [])).push(item.MessageIndex);
      result.costs.set(item.MessageIndex, item.EstimatedTokens); result.byIndex.set(item.MessageIndex, item.Group);
    });
    Object.keys(result.groups).forEach(function (key) { result.groups[key].sort(function (a, b) { return a - b; }); });
    return result;
  }
  function documentModel(text, entries, split) {
    var fields = split(text), raw = field(fields, "messages");
    if (!raw || raw.trim()[0] !== "[") throw new Error("В сохранённом запросе нет массива messages. Откройте исходник.");
    var list = split(raw), byIndex = new Map();
    (entries || []).forEach(function (entry) {
      if (!entry || !Number.isInteger(entry.MessageIndex) || entry.MessageIndex < 0 || entry.MessageIndex >= list.length) return;
      var at = byIndex.get(entry.MessageIndex) || []; at.push(entry); byIndex.set(entry.MessageIndex, at);
    });
    return { text: text, fields: fields, list: list, byIndex: byIndex, split: split };
  }
  function message(doc, index) {
    if (!doc.list[index]) throw new Error("Сообщение отсутствует в сохранённом запросе.");
    var raw = doc.list[index].raw, fields = doc.split(raw), content = field(fields, "content"), role = value(field(fields, "role"));
    var entries = doc.byIndex.get(index) || [];
    return { raw: raw, fields: fields, content: content, role: typeof role === "string" ? role : "не указана",
      entry: entries.length === 1 && entries[0].Presentation !== "Excluded" ? entries[0] : null, ambiguous: entries.length > 1 };
  }
  function outline(root, options) {
    var state = options.state, doc = options.document, trace = options.trace || {}, count = doc ? doc.list.length : trace.MessageCount;
    var structure = composition(trace, count), grouped = state.order !== "wire" && !!structure;
    var active = grouped ? (state.group && structure.groups[state.group] ? state.group : Object.keys(structure.groups)[0]) : "wire";
    if (grouped) state.group = active;
    var indices = grouped ? structure.groups[active] || [] : null, total = grouped ? indices.length : count || 0;
    if (options.reveal && Number.isInteger(state.index)) {
      state.collapsed = false;
      if (grouped) { active = structure.byIndex.get(state.index); state.group = active; indices = structure.groups[active] || []; total = indices.length; }
      var at = grouped ? indices.indexOf(state.index) : state.index; state.page = Math.max(0, Math.floor(at / 50));
    }
    state.page = Math.max(0, Math.min(state.page || 0, Math.max(0, Math.ceil(total / 50) - 1)));
    var start = state.page * 50, children = [];
    var recorded = new Map(); (trace.ContextMessages || []).forEach(function (entry) { if (entry && entry.MessageIndex != null) recorded.set(entry.MessageIndex, entry); });
    for (var offset = start; offset < Math.min(start + 50, total); offset++) {
      var index = grouped ? indices[offset] : offset, entry = recorded.get(index), role = entry && entry.Role, excerpt = "";
      if (doc) {
        var item = message(doc, index); role = item.role;
        if (item.content && item.content.length < 16000) { var text = value(item.content); if (typeof text === "string") excerpt = text.slice(0, 90).replace(/\s+/g, " ").trim(); }
      }
      var cost = structure && structure.costs.get(index);
      children.push({ key: "message:" + index, title: (index + 1) + " · " + (roles[role] || role || "Сообщение"),
        tooltip: excerpt || "Сообщение " + (index + 1), badge: cost == null ? "" : "≈ " + cost,
        selected: options.tab === "messages" && state.index === index });
    }
    var nodes = [{ key: "summary", title: "Обзор запроса", selected: options.tab === "summary", iconHtml: RNAssistantIcons.render("info") }];
    if (grouped) Object.keys(groups).forEach(function (key) {
      if (!structure.groups[key]) return;
      nodes.push({ key: "group:" + key, kind: "parent", selectable: true, title: groups[key], badge: String(structure.groups[key].length),
        expanded: active === key && !state.collapsed, selected: options.tab === "group" && active === key, children: active === key && !state.collapsed ? children : [] });
    });
    else nodes = nodes.concat(children);
    if (!count) nodes.push({ key: "messages", title: "Открыть сообщения" });
    nodes.push({ key: "parameters", title: "Параметры и формат", selected: options.tab === "parameters" },
      { key: "omissions", title: "Не отправлено", selected: options.tab === "omissions" });
    if (options.actual) nodes.push({ key: "resources", title: "Ресурсы", selected: options.tab === "resources" },
      { key: "evidence", title: "Доказательства", selected: options.tab === "evidence" });
    RNAssistantNavigationTree.render(root, nodes, { label: "Состав запроса", onSelect: function (item) {
      if (item.key.indexOf("message:") === 0) options.choose("messages", Number(item.key.slice(8)));
      else if (item.key.indexOf("group:") === 0) { state.group = item.key.slice(6); state.page = 0; state.collapsed = false; options.choose("group"); }
      else options.choose(item.key);
    }, onToggle: function (item, expanded) { state.collapsed = !expanded; if (expanded) { state.group = item.key.slice(6); state.page = 0; options.choose("group"); } } });
    return { count: count, total: total, start: start, end: Math.min(start + 50, total), grouped: grouped, structure: structure,
      notice: structure ? "Группы — сохранённая оценка; номера — порядок отправки." : "Классификация не сохранена или не соответствует запросу. Показан порядок отправки." };
  }
  function group(root, trace, count, key) {
    var data = composition(trace, count);
    root.appendChild(node("h3", groups[key] || "Сообщения запроса"));
    if (!data || !data.groups[key]) { root.appendChild(node("p", "Классификация недоступна. Сообщения можно открыть в порядке отправки.")); return; }
    var indices = data.groups[key], cost = indices.reduce(function (sum, index) { return sum + data.costs.get(index); }, 0);
    root.appendChild(node("p", indices.length + " сообщений · ≈ " + cost.toLocaleString("ru-RU") + " токенов. Базовая оценка, не usage API."));
    if (key === "InstructionsAndCatalog") root.appendChild(node("p", "Инструкции, навыки и каталог внутри общего сообщения не разделены на доли. Выберите сообщение в оглавлении."));
    if (key === "Unattributed") root.appendChild(node("p", "Размер учтён, но точная принадлежность сообщения к смысловому разделу не записана."));
  }
  window.RNAssistantRequestContents = { model: documentModel, message: message, composition: composition, outline: outline, group: group,
    roles: roles, field: field, value: value };
}());
