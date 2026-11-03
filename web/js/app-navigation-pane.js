(function () {
  "use strict";
  // Presentation only: adapters own data, scope, selection and command authority.
  var searches = new WeakMap(), panes = new WeakMap();
  function bindSearch(button, input, heading, signal) {
    if (searches.has(button)) return;
    searches.set(button, true);
    var title = button.title || button.getAttribute("aria-label") || "Поиск";
    function update() {
      var query = input.value.trim();
      button.classList.toggle("has-search-query", !!query);
      button.title = query ? "Фильтр: " + input.value + " · изменить поиск" : title;
      button.setAttribute("aria-description", query ? "Активен фильтр: " + input.value : "");
    }
    function close(clear) {
      if (clear && input.value) { input.value = ""; input.dispatchEvent(new Event("input", { bubbles: true })); }
      input.hidden = true; heading.classList.remove("searching");
      button.setAttribute("aria-expanded", "false"); update();
    }
    var events = signal ? { signal: signal } : undefined;
    button.addEventListener("pointerdown", function (event) { if (document.activeElement === input) event.preventDefault(); }, events);
    button.addEventListener("click", function () {
      if (input.disabled) { close(true); return; }
      if (!input.hidden) { close(true); button.focus(); return; }
      input.hidden = false; heading.classList.add("searching");
      button.setAttribute("aria-expanded", "true"); input.focus();
    }, events);
    input.addEventListener("input", update, events);
    input.addEventListener("blur", function () { close(false); }, events);
    input.addEventListener("keydown", function (event) {
      if (event.key === "Escape") { event.preventDefault(); close(true); button.focus(); }
    }, events);
    if (signal) signal.addEventListener("abort", function () { searches.delete(button); }, { once: true });
    update();
  }
  function node(tag, className, text) {
    var element = document.createElement(tag); element.className = className;
    if (text) element.textContent = text;
    return element;
  }
  function mount(options, adapter) {
    var root = options.root;
    if (panes.has(root)) return panes.get(root);
    var controller = new AbortController(), events = { signal: controller.signal }, timer;
    var heading = options.heading, tree = options.tree, input = options.search;
    heading.className = "product-nav-heading product-search-heading navigation-pane-heading";
    var toolbar = node("div", "tree-toolbar"), search = node("button", "product-nav-search-toggle icon-btn");
    search.type = "button"; search.dataset.navSearchToggle = input.id;
    search.setAttribute("aria-label", input.getAttribute("aria-label") || "Поиск");
    search.setAttribute("aria-controls", input.id); search.setAttribute("aria-expanded", "false");
    search.innerHTML = RNAssistantIcons.render("search");
    input.classList.add("product-nav-search-input"); input.hidden = true;
    toolbar.append(search, input);
    if (options.actions) toolbar.appendChild(options.actions);
    if (options.commands) {
      var create = node("button", "icon-btn"); create.type = "button";
      create.title = options.commandLabel; create.setAttribute("aria-label", options.commandLabel);
      create.setAttribute("aria-haspopup", "menu"); create.setAttribute("aria-expanded", "false");
      create.innerHTML = RNAssistantIcons.render("plus");
      create.addEventListener("click", function () { RNAssistantActionMenu.openButtons(create, options.commands); }, events);
      toolbar.appendChild(create);
    }
    heading.replaceChildren(node("strong", "", options.title), toolbar);
    var footer = node("div", "navigation-pane-state");
    var message = options.status || node("p", "muted");
    message.setAttribute("role", "status"); message.setAttribute("aria-live", "polite");
    var retry = node("button", "button compact secondary", "Повторить загрузку"); retry.type = "button"; retry.hidden = true;
    var more = options.more || node("button", "link-button", "Показать ещё"); more.type = "button"; more.hidden = true;
    footer.append(message, retry, more); root.appendChild(footer);
    bindSearch(search, input, heading, controller.signal);
    input.addEventListener("input", function () {
      clearTimeout(timer);
      if (adapter.onQueryPending) adapter.onQueryPending();
      if (adapter.searchDelay) timer = setTimeout(function () { adapter.onQuery(input.value); }, adapter.searchDelay);
      else adapter.onQuery(input.value);
    }, events);
    retry.addEventListener("click", function () { if (adapter.onRetry) adapter.onRetry(); }, events);
    more.addEventListener("click", function () { if (adapter.onMore) adapter.onMore(); }, events);
    var pane = {
      render: function (nodes, treeOptions) {
        RNAssistantNavigationTree.render(tree, nodes, Object.assign({ label: options.title, positionKey: tree.id }, treeOptions));
      },
      setState: function (value) {
        tree.setAttribute("aria-busy", value.busy ? "true" : "false");
        message.textContent = value.message || "";
        retry.hidden = !value.retry; retry.disabled = !!value.busy;
        more.hidden = !value.more; more.disabled = !!value.busy;
        footer.hidden = !value.message && !value.retry && !value.more;
      },
      dispose: function () {
        clearTimeout(timer); controller.abort(); RNAssistantActionMenu.closeWithin(heading);
        RNAssistantNavigationTree.unmount(tree, tree.id); footer.remove(); panes.delete(root);
      }
    };
    window.addEventListener("pagehide", function (event) { if (!event.persisted) pane.dispose(); }, events);
    panes.set(root, pane); return pane;
  }
  window.RNAssistantNavigationPane = { mount: mount, bindSearches: function (root) {
    root.querySelectorAll("[data-nav-search-toggle]").forEach(function (button) {
      var input = document.getElementById(button.dataset.navSearchToggle), heading = button.closest(".product-search-heading");
      if (input && heading) bindSearch(button, input, heading);
    });
  } };
}());
