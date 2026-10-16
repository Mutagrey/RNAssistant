(function (root) {
  "use strict";
  // Fixed application modules only. Project JSON is data and never becomes a script URL.
  const modules = ["selectors", "snapshot", "json", "decimal", "sources", "byte-source",
    "expression", "template", "transform", "engine", "executor"];
  let loading = null;
  function add(path) {
    return new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "core/" + path + ".js";
      script.onload = resolve;
      script.onerror = () => reject(new Error("Не удалось загрузить модуль ядра: " + path + "."));
      document.head.append(script);
    });
  }
  function load() {
    if (!loading) loading = modules.reduce((ready, path) => ready.then(() => add(path)), Promise.resolve())
      .then(() => {
        if (!root.RNTabularEngine || !root.RNTabularExecutor) throw new Error("Ядро приложения недоступно.");
        return root.RNTabularExecutor;
      });
    return loading;
  }
  root.DataTemplatesBrowserCore = Object.freeze({ load: load });
}(window));
