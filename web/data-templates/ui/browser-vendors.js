(function () {
  "use strict";
  // Browser-only composition. Shared UI/domain modules do not depend on these globals.
  const script = document.currentScript;
  const root = new URL("../", script.src);
  const pending = Object.create(null);
  const ready = new Set();
  const definitions = Object.freeze({
    echarts: { js: "vendor/echarts.min.js", global: "echarts" },
    tabulator: { css: "vendor/tabulator.min.css", js: "vendor/tabulator.min.js", global: "Tabulator" },
    codemirror: { css: ["vendor/codemirror/codemirror.min.css", "vendor/codemirror/addon/hint/show-hint.min.css"],
      js: ["vendor/codemirror/codemirror.min.js", "vendor/codemirror/mode/javascript/javascript.min.js",
        "vendor/codemirror/addon/edit/matchbrackets.min.js", "vendor/codemirror/addon/edit/closebrackets.min.js",
        "vendor/codemirror/addon/hint/show-hint.min.js"], global: "CodeMirror" }
  });

  function asset(tag, url) {
    return new Promise((resolve, reject) => {
      const element = document.createElement(tag);
      if (tag === "link") { element.rel = "stylesheet"; element.href = url; }
      else { element.src = url; element.async = true; }
      element.onload = resolve;
      element.onerror = () => { element.remove(); reject(new Error("Не удалось загрузить локальный компонент: " + url)); };
      document.head.appendChild(element);
    });
  }

  function load(name) {
    const definition = definitions[name];
    if (!definition) return Promise.reject(new Error("Неизвестный компонент интерфейса: " + name));
    if (ready.has(name) || name !== "codemirror" && window[definition.global]) return Promise.resolve(window[definition.global]);
    if (!pending[name]) {
      pending[name] = (async () => {
        for (const file of definition.css ? [].concat(definition.css) : []) await asset("link", new URL(file, root).href);
        for (const file of [].concat(definition.js)) await asset("script", new URL(file, root).href);
        if (!window[definition.global]) throw new Error("Компонент не зарегистрирован: " + name);
        ready.add(name);
        return window[definition.global];
      })().catch(error => { delete pending[name]; throw error; });
    }
    return pending[name];
  }

  window.DataTemplatesVendors = Object.freeze({ load });
}());
