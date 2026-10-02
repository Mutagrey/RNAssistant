(function () {
  "use strict";

  // The compact catalog is generated from the checked, pinned vendor manifest.
  // Source carriers register text, not executable globals, in the main WebView.
  if (!Array.isArray(window.RNAssistantHtmlVendorCatalog))
    throw new Error("Bundled HTML vendor catalog is unavailable.");
  var vendors = window.RNAssistantHtmlVendorCatalog.map(function (item) {
    return { id: item.id, version: item.version, global: item.global,
      purpose: item.purpose, whenUseful: item.whenUseful, file: item.file,
      loader: item.loader, scriptOnly: item.scriptOnly,
      source: new RegExp("\\b" + item.global + "\\b") };
  });
  var registered = Object.create(null);
  var pending = Object.create(null);

  function value(source, camel, pascal, fallback) {
    return source && source[camel] !== undefined ? source[camel] :
      source && source[pascal] !== undefined ? source[pascal] : fallback;
  }

  function used(files) {
    var contents = [], scripts = [];
    (files || []).forEach(function (file) {
      var kind = String(value(file, "kind", "Kind", "") || "").toLowerCase();
      var path = String(value(file, "path", "Path", "") || "");
      var content = String(value(file, "content", "Content", "") || "");
      if (kind === "html" || /\.html?$/i.test(path)) {
        contents.push(content);
        var inline = /<script\b[^>]*>([\s\S]*?)<\/script\s*>/gi, match;
        while ((match = inline.exec(content)) !== null) scripts.push(match[1]);
      } else if (kind === "script" || kind === "js" || /\.js$/i.test(path)) {
        contents.push(content);
        scripts.push(content);
      }
    });
    return vendors.filter(function (vendor) {
      return (vendor.scriptOnly ? scripts : contents).some(function (content) { return vendor.source.test(content); });
    });
  }

  function ready(vendor) {
    if (vendor.loader === "echarts-factory") {
      return typeof window.RNAssistantEChartsFactory === "function" &&
        !!window.echarts && window.echarts.version === vendor.version;
    }
    return !!registered[vendor.id];
  }

  function register(id, source) {
    var vendor = vendors.filter(function (item) { return item.id === id && item.loader !== "echarts-factory"; })[0];
    if (!vendor || registered[id] || !source || typeof source.js !== "string" ||
        !source.js || typeof source.css !== "string") {
      throw new Error("Invalid bundled HTML vendor: " + id);
    }
    registered[id] = Object.freeze({ js: source.js, css: source.css });
  }

  function load(vendor) {
    if (ready(vendor)) return Promise.resolve();
    if (pending[vendor.id]) return pending[vendor.id];
    if (vendor.loader === "echarts-factory") {
      var echarts = window.RNAssistantEChartsSandboxRuntime;
      if (!echarts || typeof echarts.load !== "function")
        return Promise.reject(new Error("Bundled ECharts loader is unavailable."));
      pending[vendor.id] = echarts.load().then(function () {
        if (!ready(vendor)) throw new Error("Bundled ECharts " + vendor.version + " is unavailable.");
      }).catch(function (error) { pending[vendor.id] = null; throw error; });
      return pending[vendor.id];
    }
    if (!window.document || !window.document.createElement)
      return Promise.reject(new Error("Bundled HTML vendors can only load in the WebView."));
    pending[vendor.id] = new Promise(function (resolve, reject) {
      var script = document.createElement("script");
      script.src = vendor.loader;
      script.async = true;
      script.onload = function () {
        if (ready(vendor)) resolve();
        else reject(new Error("Bundled " + vendor.id + " " + vendor.version + " did not register."));
      };
      script.onerror = function () { reject(new Error("Bundled " + vendor.id + " " + vendor.version + " failed to load.")); };
      (document.head || document.documentElement).appendChild(script);
    }).catch(function (error) { pending[vendor.id] = null; throw error; });
    return pending[vendor.id];
  }

  function ensure(files) {
    return used(files).reduce(function (chain, vendor) {
      return chain.then(function () { return load(vendor); });
    }, Promise.resolve());
  }

  function missing(files) {
    return used(files).filter(function (vendor) { return !ready(vendor); });
  }

  function dependencies(files) {
    return used(files).map(function (vendor) {
      var id = "runtime/" + vendor.file;
      var loaded = ready(vendor);
      return { id: id, path: id, title: vendor.file, kind: "script", version: vendor.version,
        loaded: loaded, readOnly: true,
        description: loaded ? vendor.purpose + ". Встроенная offline-зависимость preview/export; подключается перед скриптами workspace."
          : "Встроенная зависимость " + vendor.id + " не загрузилась." };
    });
  }

  function source(vendor) {
    if (!ready(vendor)) throw new Error("Bundled " + vendor.id + " " + vendor.version + " is unavailable.");
    if (vendor.loader === "echarts-factory") {
      return { js: "/* Licensed to the Apache Software Foundation under the Apache License, Version 2.0. " +
        "https://www.apache.org/licenses/LICENSE-2.0 */\n(" +
        window.RNAssistantEChartsFactory.toString() + ")(window.echarts={});", css: "" };
    }
    return registered[vendor.id];
  }

  window.RNAssistantHtmlVendorRuntime = Object.freeze({
    used: used, missing: missing, ensure: ensure, dependencies: dependencies,
    source: source, register: register
  });
}());
