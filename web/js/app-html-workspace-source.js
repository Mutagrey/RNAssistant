(function () {
  "use strict";
  function key(file) { return file && file.source && file.source.uri + "@" + file.source.revision; }
  function ready(file) { return !!key(file) && file.sourceReadKey === key(file) && typeof file.content === "string"; }

  function create(options) {
    var state = options.state, pending = null;
    function currentWorkspace(workspace) {
      return !state.bridgeUnavailable && state.htmlWorkspace === workspace &&
        typeof workspace.revisionArtifactId === "string" && workspace.revisionArtifactId === state.activeHtmlArtifactId;
    }
    async function close(job) {
      if (job.data && !job.closed && /^[a-f0-9]{64}$/.test(job.data.leaseId)) {
        job.closed = true;
        await options.send("resourceDataClose", { chatId: job.chatId, workspaceId: "html-editor", leaseId: job.data.leaseId }).catch(function () {});
      }
    }
    function cancel(notify) {
      if (!pending) return;
      pending.notify = !!notify;
      if (pending.abort.signal.aborted) return;
      pending.abort.abort();
      if (pending.requestId) options.cancelRequest(pending.requestId).catch(function () {});
      close(pending);
    }
    function validate(workspace, wanted) {
      var files = workspace.files || [];
      if (files.length > 100 || files.some(function (file) { return !Number.isInteger(file.characters) || file.characters < 0 || file.characters > 300000; }) ||
          files.reduce(function (total, file) { return total + file.characters; }, 0) > 1500000)
        throw new Error("RESOURCE_BATCH_TOO_LARGE");
      wanted.forEach(function (file) {
        if (files.indexOf(file) < 0 || !file.source || typeof file.source.uri !== "string" || !file.source.uri.startsWith("rna://chat/") ||
            typeof file.source.revision !== "string" || !file.source.revision || !Number.isInteger(file.characters) ||
            file.characters < 0 || file.characters > 300000 || !Number.isInteger(file.byteLength) || file.byteLength < 0 ||
            file.byteLength > 1200000 || !/^[a-f0-9]{64}$/.test(file.sha256)) throw new Error("RESOURCE_SOURCE_METADATA_INVALID");
      });
    }
    function demandKey(workspace, wanted) { return workspace.revisionArtifactId + ":" + wanted.map(key).join("|"); }
    function start(workspace, wanted, isCurrent, exporting) {
      var job = { workspace: workspace, chatId: state.activeChatId, key: demandKey(workspace, wanted),
        abort: new AbortController(), exporting: exporting, notify: true };
      function current() { return pending === job && !job.abort.signal.aborted && state.activeChatId === job.chatId &&
        currentWorkspace(workspace) && job.key === demandKey(workspace, wanted) && isCurrent(); }
      function active() { if (!current()) throw new Error("RESOURCE_SOURCE_CANCELLED"); }
      pending = job;
      job.promise = (async function () {
        var file;
        try {
          validate(workspace, wanted);
          for (var index = 0; index < wanted.length; index++) {
            file = wanted[index]; active();
            if (ready(file)) continue;
            var exact = { uri: file.source.uri, revision: file.source.revision };
            job.data = null; job.closed = false;
            var request = options.send("readHtmlWorkspaceSource", { chatId: job.chatId, resource: exact });
            job.requestId = request.requestId;
            var response = await request; job.requestId = null; job.data = response && response.data; active();
            if (!response || response.chatId !== job.chatId || !response.resource || response.resource.uri !== exact.uri ||
                response.resource.revision !== exact.revision || response.totalCharacters !== file.characters || !job.data ||
                !job.data.payload || job.data.payload.sha256 !== file.sha256 || job.data.payload.byteLength !== file.byteLength)
              throw new Error("RESOURCE_SOURCE_MISMATCH");
            var bytes = await window.RNAssistantResourceDownload.read(job.data,
              { maxBytes: 1200000, fetch: window.fetch.bind(window), signal: job.abort.signal, isCurrent: current });
            var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
            if (text.length !== file.characters) throw new Error("RESOURCE_SOURCE_INCOMPLETE");
            await close(job); active();
            file.content = text; file.sourceReadKey = key(file); delete file.sourceError;
          }
          return true;
        } catch (error) {
          job.error = error;
          if (!job.abort.signal.aborted && currentWorkspace(workspace)) {
            (file ? [file] : wanted).forEach(function (item) { item.sourceError = error.message || "Исходник недоступен."; });
          }
          return false;
        } finally {
          await close(job);
          if (pending === job) pending = null;
          if (job.notify) options.changed();
        }
      }());
      return job;
    }
    function ensure(wanted) {
      var workspace = state.htmlWorkspace || {};
      if (!currentWorkspace(workspace) || state.htmlWorkspaceExportPending || !wanted.length) { if (pending && !pending.exporting) cancel(false); return !wanted.length; }
      if (pending && (pending.workspace !== workspace || pending.key !== demandKey(workspace, wanted))) { cancel(true); return false; }
      if (wanted.every(ready)) return true;
      if (pending || wanted.some(function (file) { return file.sourceError; })) return false;
      start(workspace, wanted, function () { return !state.htmlWorkspaceExportPending; }, false);
      return false;
    }
    async function exportSources(workspace, isCurrent) {
      if (pending) { var old = pending; cancel(false); await old.promise; }
      if (!currentWorkspace(workspace) || !isCurrent()) throw new Error("RESOURCE_EXPORT_CANCELLED");
      var job = start(workspace, (workspace.files || []).slice(), isCurrent, true);
      if (!await job.promise) throw job.error || new Error("RESOURCE_SOURCE_UNAVAILABLE");
    }
    function message() {
      if (!currentWorkspace(state.htmlWorkspace || {})) return "Workspace изменился. Скопируйте правки и перезагрузите исходники.";
      var error = (state.htmlWorkspace.files || []).find(function (file) { return file.sourceError; });
      return error ? "Исходник не загружен: " + error.sourceError + ". Нажмите «Исходники ↻»." : "Загрузка исходников…";
    }
    return { ensure: ensure, ready: ready, current: currentWorkspace, exportSources: exportSources,
      message: message, release: function () { cancel(false); } };
  }
  window.RNAssistantHtmlWorkspaceSource = { create: create, ready: ready };
}());
