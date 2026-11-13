(function (global) {
  "use strict";
  if (global.RNAssistantBrowserActionView) return;
  // Presentation only. The native semantic owner supplies actual elements and verified outcomes.
  global.RNAssistantBrowserActionView = { create: function (styles, options) {
    var host = document.createElement("div"), root = host.attachShadow({ mode: "closed" });
    host.setAttribute("aria-hidden", "true"); host.setAttribute("inert", "");
    host.setAttribute("data-rna-browser-action-view", "");
    host.style.cssText = "all:initial!important;position:fixed!important;inset:0!important;display:block!important;pointer-events:none!important;z-index:2147483647!important;overflow:hidden!important;";
    var sheet = document.createElement("style"); sheet.textContent = styles; root.appendChild(sheet);
    var layer = document.createElement("div"); layer.className = "action-view";
    layer.innerHTML = '<div class="action-target"></div><div class="action-pointer"><span class="action-pulse"></span>' +
      '<svg viewBox="0 0 28 32" aria-hidden="true"><path d="M3 2.5 3.5 24l6.1-5.6 5.2 10.1 5-2.6-5.2-9.7 8.4-.9Z"/></svg></div>' +
      '<div class="action-label"><span class="action-dot"></span><span class="action-caption"></span></div>';
    root.appendChild(layer);
    var target = layer.querySelector(".action-target"), pointer = layer.querySelector(".action-pointer"),
      label = layer.querySelector(".action-label"), caption = layer.querySelector(".action-caption"),
      pulse = layer.querySelector(".action-pulse"), timer = null, fade = null, frame = null,
      activeId = null, activeElement = null, positioned = false, generation = 0;
    function reduced() { return matchMedia("(prefers-reduced-motion: reduce)").matches; }
    function duration(name) { return Math.max(0, parseFloat(getComputedStyle(layer).getPropertyValue(name)) || 0); }
    function clear() { clearTimeout(timer); clearTimeout(fade); cancelAnimationFrame(frame); timer = fade = frame = null; }
    function hide() {
      clear(); generation++; activeId = activeElement = null; positioned = false;
      host.remove(); layer.classList.remove("is-visible"); pulse.getAnimations().forEach(function (animation) { animation.cancel(); });
    }
    function idle() {
      clearTimeout(timer); clearTimeout(fade);
      timer = setTimeout(function () {
        layer.classList.remove("is-visible");
        fade = setTimeout(hide, reduced() ? 0 : duration("--action-fade"));
      }, duration("--action-idle"));
    }
    function position(element, immediate) {
      if (!element || !element.isConnected) { hide(); return false; }
      var rect = element.getBoundingClientRect(), x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
      if (!rect.width || !rect.height || x < 0 || y < 0 || x > innerWidth || y > innerHeight) { hide(); return false; }
      target.style.cssText = "left:" + rect.left + "px;top:" + rect.top + "px;width:" + rect.width + "px;height:" + rect.height + "px;";
      var transform = "translate(" + (x - 3) + "px," + (y - 2.5) + "px)";
      pointer.style.transitionDuration = immediate || reduced() ? "0ms" : "";
      pointer.style.transform = transform;
      var width = label.offsetWidth, height = label.offsetHeight;
      label.style.left = Math.max(8, Math.min(innerWidth - width - 8, x + 18)) + "px";
      label.style.top = Math.max(8, Math.min(innerHeight - height - 8, y + 28)) + "px";
      return true;
    }
    var view = {
      show: function (id, element, action, visible) {
        if (!visible || document.hidden) { hide(); return 0; }
        clear(); var current = ++generation;
        activeId = id; activeElement = element; layer.dataset.state = "working";
        caption.textContent = action === "fill" ? "Заполняю" : "Нажимаю";
        if (!host.isConnected) document.documentElement.appendChild(host);
        if (!positioned) {
          if (!position(element, true)) return 0;
          var rect = element.getBoundingClientRect();
          pointer.style.transform = "translate(" + Math.max(0, rect.left + rect.width / 2 - 35) + "px," + Math.min(innerHeight - pointer.offsetHeight, rect.top + rect.height / 2 + 24) + "px)";
        }
        layer.classList.add("is-visible");
        if (reduced()) position(element, true);
        else {
          // Commit the starting point before the single short transition; no continuous JS animation loop.
          pointer.getBoundingClientRect();
          frame = requestAnimationFrame(function () { if (generation === current) position(element, false); });
        }
        positioned = true; idle(); return reduced() ? 0 : duration("--action-move");
      },
      dispatch: function (id, element, action) {
        if (activeId !== id || !host.isConnected) return;
        caption.textContent = "Проверяю";
        if (!position(element, true)) return;
        if (action === "click" && !reduced()) pulse.animate([
          { transform: "scale(.3)", opacity: .65 }, { transform: "scale(1.6)", opacity: 0 }
        ], { duration: duration("--action-pulse"), easing: "ease-out" });
      },
      finish: function (id, effect) {
        if (activeId !== id) return;
        if (!host.isConnected || !activeElement?.isConnected) { hide(); return; }
        layer.dataset.state = effect === "Unknown" ? "unknown" : effect === "Rejected" ? "rejected" : "verified";
        caption.textContent = effect === "Unknown" ? "Результат неизвестен" : effect === "Rejected" ? "Действие не выполнено" : "Проверено";
        position(activeElement, true); idle();
      },
      hide: hide
    };
    ["pointerdown", "pointermove", "keydown", "input", "change"].forEach(function (type) {
      document.addEventListener(type, function (event) { if (event.isTrusted && !(options && options.nativeOwned)) hide(); }, true);
    });
    ["resize", "scroll", "pagehide"].forEach(function (type) { global.addEventListener(type, hide, true); });
    document.addEventListener("visibilitychange", function () { if (document.hidden) hide(); });
    return view;
  } };
}(globalThis));
