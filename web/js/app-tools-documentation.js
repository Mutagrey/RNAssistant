(function () {
  "use strict";

  var CONTRACT_VERSION = 1;
  var REQUEST_TYPE = "rnassistant.toolLibraryDocumentationRequest";
  var RESPONSE_TYPE = "rnassistant.toolLibraryDocumentation";

  function fromContract(response, tool) {
    if (!response || response.type !== RESPONSE_TYPE ||
        response.contractVersion !== CONTRACT_VERSION ||
        typeof response.toolId !== "string" ||
        typeof response.revision !== "string" ||
        typeof response.markdown !== "string" || !response.markdown ||
        !tool || response.toolId !== tool.Id ||
        response.revision !== tool.Revision) {
      throw new Error("Некорректный typed contract документации инструмента.");
    }
    return response.markdown;
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    function render(markdownText) {
      var target = $("toolDocumentationMarkdown");
      if (!target) return;
      if (typeof markdown === "function") {
        target.innerHTML = markdown(markdownText || "");
        if (typeof enhanceMarkdown === "function") {
          enhanceMarkdown(target, { sourceText: markdownText || "" });
        }
      } else {
        target.textContent = markdownText || "";
      }
    }

    function prepare(tool) {
      var builtIn = !!(tool && tool.BuiltIn);
      if ($("toolReadmeEditor")) $("toolReadmeEditor").classList.toggle("hidden", builtIn);
      if ($("toolBuiltInDocs")) $("toolBuiltInDocs").classList.toggle("hidden", !builtIn);
      if (!builtIn) return;
      state.toolDocumentationCache = state.toolDocumentationCache || {};
      var key = tool.Id + "\n" + tool.Revision;
      var cached = state.toolDocumentationCache[key];
      $("toolDocumentationStatus").textContent = cached ? "" : "Документация загрузится при открытии вкладки.";
      render(cached || "");
    }

    async function ensure() {
      if ((state.toolEditorPage || "main") !== "docs") return;
      var tool = state.tools[state.selectedToolIndex] || null;
      if (!tool || !tool.BuiltIn) return;
      state.toolDocumentationCache = state.toolDocumentationCache || {};
      state.toolDocumentationRequests = state.toolDocumentationRequests || {};
      var key = tool.Id + "\n" + tool.Revision;
      if (state.toolDocumentationCache[key]) {
        $("toolDocumentationStatus").textContent = "";
        render(state.toolDocumentationCache[key]);
        return;
      }
      if (state.toolDocumentationRequests[key]) return state.toolDocumentationRequests[key];
      $("toolDocumentationStatus").textContent = "Загружаю документацию…";
      var selected = tool;
      var request = options.send("getToolDocumentation", {
        type: REQUEST_TYPE,
        contractVersion: CONTRACT_VERSION,
        toolId: tool.Id,
        expectedRevision: tool.Revision
      }).then(function (response) {
        var text = fromContract(response, selected);
        state.toolDocumentationCache[key] = text;
        var current = state.tools[state.selectedToolIndex] || null;
        if (current === selected && (state.toolEditorPage || "main") === "docs") {
          $("toolDocumentationStatus").textContent = "";
          render(text);
        }
      }).catch(function (error) {
        var current = state.tools[state.selectedToolIndex] || null;
        if (current === selected) {
          $("toolDocumentationStatus").textContent = error.detail || error.message;
          render("");
        }
        options.log(error.message, "error");
      }).finally(function () {
        delete state.toolDocumentationRequests[key];
      });
      state.toolDocumentationRequests[key] = request;
      return request;
    }

    return { ensure: ensure, prepare: prepare };
  }

  window.RNAssistantToolDocumentation = {
    create: create,
    fromContract: fromContract
  };
}());
