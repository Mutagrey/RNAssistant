(function () {
  "use strict";
  function node(tag, text, className) { var result = document.createElement(tag); result.textContent = text || ""; if (className) result.className = className; return result; }
  function create(options) {
    var text = options.fullText || options.text, parsed = RNAssistantJsonViewer.parse(text);
    var entries = parsed.ok && parsed.root.type === "object" ? parsed.root.entries.filter(function (entry) { return entry.key.value === "items"; }) : [];
    var items = entries.length === 1 && entries[0].value.type === "array" ? entries[0].value.items : [];
    var body = node("div", "", "collection-view product-viewer"), page = node("div", "", "collection-page product-viewer");
    body.dataset.productViewer = "collection";
    if (!items.length || items.length > 10000) {
      RNAssistantViewerRegistry.mount("json", page, { text: text, completeness: "full", onCopy: options.onCopy }); body.appendChild(page);
      return { element: body, destroy: function () { RNAssistantViewerRegistry.unmount(page); body.replaceChildren(); } };
    }
    function itemTitle(item, index) {
      var title = item.type === "object" && item.entries.find(function (entry) {
        return ["title", "name", "label"].indexOf(entry.key.value) >= 0 && entry.value.type === "string";
      });
      return title ? title.value.value : "Элемент " + (index + 1);
    }
    function select(index) {
      RNAssistantViewerRegistry.mount("json", page, {
        text: RNAssistantJsonViewer.raw(parsed, items[index]), completeness: "full", onCopy: options.onCopy
      });
    }
    var rail = RNAssistantSequenceViewer.createRail({ title: "Элементы", ariaLabel: "Элементы коллекции", count: items.length,
      currentIndex: 0, orientation: "horizontal", className: "collection-rail", showJump: false, showNumbers: false, itemExtent: 156,
      getItem: function (index) { return items[index]; }, itemLabel: function (index, item) { return itemTitle(item, index); }, onSelect: select,
      renderItem: function (index, item, preview) { preview.appendChild(node("strong", itemTitle(item, index))); return "ready"; }
    });
    var toolbar = node("div", "", "material-viewer-toolbar"), modes = node("div", "", "view-switch");
    var collectionButton = node("button", "Элементы"), sourceButton = node("button", "JSON");
    collectionButton.type = sourceButton.type = "button"; collectionButton.setAttribute("aria-pressed", "true"); sourceButton.setAttribute("aria-pressed", "false");
    var source = node("div", "", "collection-page product-viewer"), sourceMounted = false; source.hidden = true;
    function mode(raw) {
      page.hidden = rail.element.hidden = raw; source.hidden = !raw;
      collectionButton.setAttribute("aria-pressed", String(!raw)); sourceButton.setAttribute("aria-pressed", String(raw));
      if (raw && !sourceMounted) { RNAssistantViewerRegistry.mount("json", source, { text: text, completeness: "full", onCopy: options.onCopy }); sourceMounted = true; }
    }
    collectionButton.addEventListener("click", function () { mode(false); }); sourceButton.addEventListener("click", function () { mode(true); });
    modes.append(collectionButton, sourceButton); toolbar.appendChild(modes); body.append(toolbar, rail.element, page, source); select(0);
    return { element: body, destroy: function () { rail.destroy(); RNAssistantViewerRegistry.unmount(page); RNAssistantViewerRegistry.unmount(source); body.replaceChildren(); } };
  }
  RNAssistantViewerRegistry.register("sequence", create);
}());
