(function () {
  "use strict";

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function boundedInteger(value, fallback, minimum, maximum) {
    value = Number(value);
    if (!Number.isInteger(value)) value = fallback;
    return Math.max(minimum, Math.min(maximum, value));
  }

  function createRail(options) {
    options = options || {};
    var count = boundedInteger(options.count, 1, 1, 10000);
    var currentIndex = boundedInteger(options.currentIndex, 0, 0, count - 1);
    var horizontal = options.orientation === "horizontal";
    var extent = boundedInteger(options.itemExtent, horizontal ? 92 : 126, 56, 320);
    var overscan = boundedInteger(options.overscan, 2, 1, 8);
    var root = element("aside", "rn-sequence-rail is-" + (horizontal ? "horizontal" : "vertical"));
    if (options.className) root.className += " " + options.className;
    root.setAttribute("aria-label", options.ariaLabel || options.title || "Последовательность");
    var header = element("div", "rn-sequence-header");
    var title = element("span", "rn-sequence-title", options.title || "Элементы");
    header.appendChild(title);
    var input = null;
    if (options.showJump !== false) {
      input = element("input", "rn-sequence-input");
      input.type = "number";
      input.min = "1";
      input.max = String(count);
      input.value = String(currentIndex + 1);
      input.disabled = options.pending === true || typeof options.onSelect !== "function";
      input.setAttribute("aria-label", options.jumpLabel || "Перейти к элементу");
      header.appendChild(input);
      header.appendChild(element("span", "rn-sequence-count", "/ " + count));
    } else {
      header.appendChild(element("span", "rn-sequence-count", String(count)));
    }
    var list = element("div", "rn-sequence-list");
    list.tabIndex = 0;
    var track = element("div", "rn-sequence-track");
    if (horizontal) track.style.width = (count * extent) + "px";
    else track.style.height = (count * extent) + "px";
    list.appendChild(track);
    root.appendChild(header);
    root.appendChild(list);
    var resizeObserver = null;
    var destroyed = false;

    function itemAt(index) {
      return typeof options.getItem === "function" ? options.getItem(index) : null;
    }

    function select(index) {
      if (index === currentIndex || options.pending === true || typeof options.onSelect !== "function") return;
      options.onSelect(index, itemAt(index));
    }

    function commitInput() {
      if (!input) return;
      var requested = Number(input.value);
      if (!Number.isInteger(requested)) {
        input.value = String(currentIndex + 1);
        return;
      }
      requested = Math.max(1, Math.min(count, requested));
      input.value = String(requested);
      select(requested - 1);
    }

    function scrollOffset() {
      return Number(horizontal ? list.scrollLeft : list.scrollTop) || 0;
    }

    function viewportExtent() {
      return Number(horizontal ? list.clientWidth : list.clientHeight) || (horizontal ? 560 : 560);
    }

    function render() {
      if (destroyed) return;
      var offset = scrollOffset();
      var first = Math.max(0, Math.floor(offset / extent) - overscan);
      var last = Math.min(count - 1, Math.ceil((offset + viewportExtent()) / extent) + overscan);
      var requested = [];
      track.replaceChildren();
      for (var index = first; index <= last; index += 1) {
        var item = itemAt(index);
        var active = index === currentIndex;
        var button = element("button", "rn-sequence-item" + (active ? " active" : ""));
        button.type = "button";
        button.disabled = options.pending === true;
        if (horizontal) button.style.left = (index * extent) + "px";
        else button.style.top = (index * extent) + "px";
        button.setAttribute("data-sequence-index", String(index));
        var label = typeof options.itemLabel === "function"
          ? options.itemLabel(index, item)
          : "Элемент " + (index + 1);
        button.setAttribute("aria-label", label);
        button.title = label;
        if (active) button.setAttribute("aria-current", options.currentRole || "true");
        if (options.showNumbers !== false) {
          button.appendChild(element("span", "rn-sequence-number", String(index + 1)));
        }
        var preview = element("span", "rn-sequence-preview");
        var rendered = typeof options.renderItem === "function"
          ? options.renderItem(index, item, preview)
          : null;
        var status = typeof rendered === "string" ? rendered : (rendered && rendered.status || "");
        if (status === "error") {
          if (!preview.childNodes.length) preview.appendChild(element("span", "rn-sequence-unavailable", "×"));
          if (rendered && rendered.message) button.title = rendered.message;
        } else if (status !== "ready") {
          if (!preview.childNodes.length) preview.appendChild(element("span", "rn-sequence-loading", "…"));
          if (!status && typeof options.onRequest === "function") requested.push(index);
        }
        button.appendChild(preview);
        (function (selectedIndex) {
          button.addEventListener("click", function () { select(selectedIndex); });
        }(index));
        track.appendChild(button);
      }
      requested.forEach(function (index) { options.onRequest(index, itemAt(index)); });
    }

    function onScroll() {
      if (typeof options.onScroll === "function") options.onScroll(scrollOffset());
      render();
    }

    if (input) {
      input.addEventListener("change", commitInput);
      input.addEventListener("keydown", function (event) {
        if (event && event.key === "Enter") {
          commitInput();
          if (typeof event.preventDefault === "function") event.preventDefault();
        }
      });
    }
    list.addEventListener("scroll", onScroll);
    var initial = Math.max(0, Number(options.scrollOffset || 0));
    var firstVisible = Math.floor(initial / extent);
    var visibleItems = Math.max(1, Math.floor(viewportExtent() / extent));
    if (currentIndex < firstVisible || currentIndex >= firstVisible + visibleItems) {
      initial = Math.max(0, (currentIndex - Math.floor(visibleItems / 2)) * extent);
    }
    if (horizontal) list.scrollLeft = initial;
    else list.scrollTop = initial;
    render();
    if (typeof window.ResizeObserver === "function") {
      resizeObserver = new window.ResizeObserver(render);
      resizeObserver.observe(list);
    }
    return {
      element: root,
      render: render,
      destroy: function () {
        if (destroyed) return;
        destroyed = true;
        if (resizeObserver) resizeObserver.disconnect();
        resizeObserver = null;
        list.removeEventListener("scroll", onScroll);
        root.replaceChildren();
      }
    };
  }

  window.RNAssistantSequenceViewer = { createRail: createRail };
}());
