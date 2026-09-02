(function () {
  "use strict";

  var previousDefine = window.define;
  var restored = false;
  var captured = false;

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
    Object.defineProperty(window, "RNAssistantEChartsFactory", {
      configurable: false,
      enumerable: false,
      value: factory,
      writable: false
    });
  }

  captureECharts.amd = {};
  window.RNAssistantEChartsSandboxRuntime = Object.freeze({
    finish: function () {
      restoreDefine();
      if (!captured || typeof window.RNAssistantEChartsFactory !== "function" ||
          !window.echarts || window.echarts.version !== "5.6.0") {
        throw new Error("Bundled ECharts 5.6.0 failed to initialize.");
      }
    }
  });
  window.define = captureECharts;
}());
