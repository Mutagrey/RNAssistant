(function () {
  "use strict";

  var bindings = new WeakMap();

  // The layout owns visibility; the shared button owns its icon and interaction.
  function bind(button, options) {
    if (bindings.has(button)) return bindings.get(button);
    var left = options.side === "left";
    button.type = "button";
    button.classList.add("icon-btn", "panel-toggle");
    button.dataset.panelToggle = left ? "left" : "right";
    button.innerHTML = window.RNAssistantIcons.render(left ? "panelLeft" : "panel");

    function controls() {
      return typeof options.controls === "function" ? options.controls() : options.controls;
    }
    function sync() {
      var open = !!options.isOpen(), target = controls();
      var label = left ? (open ? "Свернуть навигацию" : "Открыть навигацию")
        : (open ? "Скрыть боковую панель" : "Показать боковую панель");
      button.setAttribute("aria-expanded", String(open));
      button.setAttribute("aria-label", label);
      button.title = label;
      if (target) button.setAttribute("aria-controls", target);
      else button.removeAttribute("aria-controls");
    }
    function setOpen(open) {
      var panel = document.getElementById(controls());
      if (!open && panel && panel.contains(document.activeElement)) button.focus({ preventScroll: true });
      options.setOpen(!!open);
      sync();
    }
    button.addEventListener("click", function () { setOpen(!options.isOpen()); });
    var binding = { sync: sync, setOpen: setOpen };
    bindings.set(button, binding);
    sync();
    return binding;
  }

  window.RNAssistantPanelToggle = { bind: bind };
}());
