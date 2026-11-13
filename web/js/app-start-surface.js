(function () {
  "use strict";
  // Presentation only. Callers supply bounded entries and own search/navigation.
  function mount(parent, initial, callbacks) {
    var root = document.createElement("section"); root.className = "start-surface";
    var body = document.createElement("div"); body.className = "start-surface-body";
    var title = document.createElement("h2"), input = document.createElement("input"), sections = document.createElement("div"), status = document.createElement("p");
    input.type = "search"; status.setAttribute("role", "status");
    body.append(title, input, sections, status); root.appendChild(body); parent.appendChild(root);
    var model, disposed = false, selecting = false;
    function update(value) {
      if (disposed) return;
      var focused = sections.contains(document.activeElement) && document.activeElement.dataset.entryKey;
      model = value; title.textContent = value.title; input.placeholder = value.placeholder; input.setAttribute("aria-label", value.placeholder);
      if (input.value !== (value.search || "")) input.value = value.search || "";
      sections.replaceChildren(); var count = 0;
      (value.sections || []).forEach(function (section) {
        if (!section.entries.length) return;
        var group = document.createElement("section"), heading = document.createElement("h3"), list = document.createElement("div");
        list.className = "start-surface-list"; heading.textContent = section.title; group.append(heading, list);
        section.entries.forEach(function (entry) {
          var row = document.createElement("button"), text = document.createElement("span"), label = document.createElement("span"), description = document.createElement("small");
          row.type = "button"; row.dataset.entryKey = entry.key; row.setAttribute("aria-label", entry.title);
          row.innerHTML = RNAssistantIcons.render(entry.icon || "file"); label.textContent = entry.title;
          description.textContent = entry.disabledReason || entry.description || "";
          text.append(label, description); row.appendChild(text); row.disabled = !!entry.disabledReason; row.title = entry.disabledReason || entry.description || entry.title;
          row.onclick = async function () {
            if (disposed || selecting) return; selecting = true;
            try { await callbacks.onSelect(entry.payload); }
            catch (error) { if (!disposed) { status.textContent = error.detail || error.message || String(error); status.setAttribute("role", "alert"); } }
            finally { selecting = false; }
          };
          list.appendChild(row); count++;
        });
        sections.appendChild(group);
      });
      status.setAttribute("role", value.error ? "alert" : "status");
      status.textContent = value.error || (value.loading ? "Загрузка…" : !count ? value.empty || "Ничего не найдено" : value.note || "");
      root.setAttribute("aria-busy", String(!!value.loading));
      if (focused) { var row = Array.from(sections.querySelectorAll("button")).find(function (node) { return node.dataset.entryKey === focused; });
        if (row && !row.disabled) row.focus({ preventScroll: true }); else input.focus({ preventScroll: true }); }
    }
    input.oninput = function () { callbacks.onSearch(input.value); };
    root.onkeydown = function (event) {
      var rows = Array.from(sections.querySelectorAll("button:not(:disabled)")), index = rows.indexOf(document.activeElement);
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault(); var next = event.key === "ArrowDown" ? index + 1 : index < 0 ? rows.length - 1 : index - 1;
        if (rows[next]) rows[next].focus(); else input.focus();
      } else if (event.key === "Enter" && event.target === input && rows[0]) { event.preventDefault(); rows[0].click(); }
      else if (event.key === "Escape") { event.preventDefault(); input.focus(); if (input.value) callbacks.onSearch(""); }
    };
    update(initial);
    return { element: root, update: update, focus: function () { input.focus({ preventScroll: true }); },
      dispose: function () { disposed = true; root.remove(); sections.replaceChildren(); input.oninput = root.onkeydown = null; } };
  }
  window.RNAssistantStartSurface = { mount: mount };
}());
