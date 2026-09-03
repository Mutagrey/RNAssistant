(function () {
  "use strict";

  var previousDefine;
  var restored = true;
  var captured = false;
  var loadPromise = null;

  function restoreDefine() {
    if (restored) return;
    restored = true;
    if (previousDefine === undefined) {
      delete window.define;
    } else {
      window.define = previousDefine;
    }
  }

  function captureECharts(dependencies, factory) {
    restoreDefine();
    if (!Array.isArray(dependencies) || dependencies.length !== 1 || dependencies[0] !== "exports" ||
        typeof factory !== "function") {
      if (typeof previousDefine === "function") return previousDefine.apply(this, arguments);
      throw new Error("Unexpected ECharts browser bundle.");
    }
    var exports = {};
    factory(exports);
    window.echarts = exports;
    captured = true;
    if (typeof window.RNAssistantEChartsFactory !== "function") {
      Object.defineProperty(window, "RNAssistantEChartsFactory", {
        configurable: false,
        enumerable: false,
        value: factory,
        writable: false
      });
    }
  }

  function ready() {
    return typeof window.RNAssistantEChartsFactory === "function" &&
      !!window.echarts && window.echarts.version === "5.6.0";
  }

  function begin() {
    if (ready() || !restored) return;
    previousDefine = window.define;
    restored = false;
    captured = false;
    captureECharts.amd = {};
    window.define = captureECharts;
  }

  function finish() {
    restoreDefine();
    if (!captured || !ready()) {
      throw new Error("Bundled ECharts 5.6.0 failed to initialize.");
    }
    return window.echarts;
  }

  function load() {
    if (ready()) return Promise.resolve(window.echarts);
    if (loadPromise) return loadPromise;
    if (!window.document || !window.document.createElement) {
      return Promise.reject(new Error("Bundled ECharts can only be loaded in the WebView."));
    }
    begin();
    loadPromise = new Promise(function (resolve, reject) {
      var script = window.document.createElement("script");
      script.src = "js/vendor/echarts.min.js";
      script.async = true;
      script.onload = function () {
        try {
          resolve(finish());
        } catch (error) {
          reject(error);
        }
      };
      script.onerror = function () {
        restoreDefine();
        reject(new Error("Bundled ECharts 5.6.0 failed to load."));
      };
      (window.document.head || window.document.documentElement).appendChild(script);
    }).catch(function (error) {
      loadPromise = null;
      throw error;
    });
    return loadPromise;
  }

  window.RNAssistantEChartsSandboxRuntime = Object.freeze({
    begin: begin,
    finish: finish,
    load: load,
    ready: ready
  });
}());
