(function () {
  "use strict";

  var CONTRACT_VERSION = 1;
  var REQUEST_TYPE = "rnassistant.toolLibraryDocumentationRequest";
  var RESPONSE_TYPE = "rnassistant.toolLibraryDocumentation";
  var MAXIMUM_BYTES = 2 * 1024 * 1024;

  function fromContract(response, operation) {
    var resource = response && response.resource;
    var parts = resource && typeof resource.uri === "string" ? resource.uri.split("/") : [];
    if (!response || response.type !== RESPONSE_TYPE ||
        response.contractVersion !== CONTRACT_VERSION ||
        !operation || response.chatId !== operation.chatId || response.toolId !== operation.toolId || response.revision !== operation.revision ||
        Object.prototype.hasOwnProperty.call(response, "markdown") || !response.data || !response.data.payload ||
        response.data.payload.contentType !== "text/markdown; charset=utf-8" ||
        parts.length !== 6 || parts[0] !== "rna:" || parts[1] !== "" || parts[2] !== "catalog" ||
        parts[3] !== "builtin-tools-" + operation.host || decodeURIComponent(parts[4]) !== operation.toolId || parts[5] !== "documentation" ||
        typeof resource.revision !== "string" || !resource.revision) {
      throw new Error("Некорректный typed contract документации инструмента.");
    }
    return response;
  }

  function create(options) {
    options = options || {};
    var state = options.state;
    var cached = null, reading = null, pending = 0;

    function selected(operation, requirePage) {
      return !!operation && !state.bridgeUnavailable && state.selectedInstructionKind === "tool" &&
        !!state.activeChatId && state.activeChatId === operation.chatId && state.tools[state.selectedToolIndex] === operation.tool &&
        operation.tool.Id === operation.toolId && operation.tool.Revision === operation.revision &&
        String(state.host || "common").toLowerCase() === operation.host && (!requirePage || state.toolEditorPage === "docs");
    }

    function close(operation) {
      if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
      operation.closed = true;
      return options.send("resourceDataClose", { chatId: operation.chatId, workspaceId: "tool-editor", leaseId: operation.data.leaseId }).catch(function () {});
    }

    function cancelRead() {
      var operation = reading;
      if (!operation) return;
      reading = null; operation.abort.abort();
      if (operation.requestId) options.cancelRequest(operation.requestId).catch(function () {});
      close(operation);
    }

    function cancel() {
      cancelRead(); cached = null; render("");
      if ($("toolDocumentationStatus")) $("toolDocumentationStatus").textContent = "";
    }

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
      if (!selected(reading, true)) cancelRead();
      if (!selected(cached, false)) cached = null;
      var builtIn = !!(tool && tool.BuiltIn);
      if ($("toolReadmeEditor")) $("toolReadmeEditor").classList.toggle("hidden", builtIn);
      if ($("toolBuiltInDocs")) $("toolBuiltInDocs").classList.toggle("hidden", !builtIn);
      if (!builtIn) { cancel(); return; }
      $("toolDocumentationStatus").textContent = cached ? "" : reading ? "Загружаю документацию…" : "Документация загрузится при открытии вкладки.";
      render(cached ? cached.text : "");
    }

    async function ensure() {
      if (!selected(reading, true)) cancelRead();
      if (!selected(cached, false)) cached = null;
      if (state.toolEditorPage !== "docs" || state.selectedInstructionKind !== "tool" || !state.activeChatId || state.bridgeUnavailable) return;
      var tool = state.tools[state.selectedToolIndex] || null;
      if (!tool || !tool.BuiltIn) { cancel(); return; }
      if (cached) {
        $("toolDocumentationStatus").textContent = "";
        render(cached.text);
        return;
      }
      if (reading) return reading.promise;
      if (pending >= 2) {
        $("toolDocumentationStatus").textContent = "Предыдущее чтение ещё закрывается. Откройте вкладку повторно после завершения.";
        return;
      }
      $("toolDocumentationStatus").textContent = "Загружаю документацию…";
      render("");
      var operation = { tool: tool, toolId: tool.Id, revision: tool.Revision, chatId: state.activeChatId,
        host: String(state.host || "common").toLowerCase(), abort: new AbortController() };
      reading = operation; pending++;
      function current() { return reading === operation && !operation.abort.signal.aborted && selected(operation, true); }
      function active() { if (!current()) throw new Error("RESOURCE_READ_CANCELLED"); }
      operation.promise = (async function () {
        try {
          active();
          var opening = options.send("getToolDocumentation", { type: REQUEST_TYPE, contractVersion: CONTRACT_VERSION,
            chatId: operation.chatId, toolId: operation.toolId, expectedRevision: operation.revision });
          operation.requestId = opening.requestId;
          var response = await opening; operation.requestId = null; operation.data = response && response.data;
          active();
          var typed = fromContract(response, operation);
          var bytes = await window.RNAssistantResourceDownload.read(typed.data, { maxBytes: MAXIMUM_BYTES,
            fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current });
          var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
          await close(operation); active();
          cached = { tool: tool, toolId: operation.toolId, revision: operation.revision, chatId: operation.chatId,
            host: operation.host, resource: typed.resource, text: text };
          $("toolDocumentationStatus").textContent = "";
          render(text);
        } catch (error) {
          if (current()) { $("toolDocumentationStatus").textContent = error.detail || error.message; render(""); options.log(error.message, "error"); }
        } finally {
          await close(operation); pending--;
          if (reading === operation) reading = null;
        }
      })();
      return operation.promise;
    }

    return { ensure: ensure, prepare: prepare, cancel: cancel };
  }

  window.RNAssistantToolDocumentation = {
    create: create,
    fromContract: fromContract
  };
}());
