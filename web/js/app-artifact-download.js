(function () {
  "use strict";
  async function save(target, options) {
    var transfer, writable;
    function current() { return options.isCurrent() && !(options.signal && options.signal.aborted); }
    var status = options.status;
    try {
      // Acquire the destination during the user gesture; stream through the existing reader.
      if (typeof window.showSaveFilePicker === "function") {
        var handle = await window.showSaveFilePicker({ suggestedName: target.title });
        if (!current()) return;
        writable = await handle.createWritable();
      }
      status("Подготовка оригинала…");
      var operation = options.send("exportArtifact", { chatId: target.chatId, documentId: target.documentId,
        resourceUri: target.resourceUri, preview: false });
      transfer = await operation;
      if (!current()) return;
      var readOptions = { maxBytes: 8 * 1024 * 1024, writable: writable,
        fetch: options.fetch, signal: options.signal, isCurrent: function () { return current(); } };
      if (writable) {
        await RNAssistantResourceDownload.save(transfer.data, readOptions); writable = null; status(""); return;
      }
      if (transfer.data && transfer.data.payload && transfer.data.payload.byteLength > 8 * 1024 * 1024)
        throw new Error("Скачивание больше 8 МБ требует поддержки потокового сохранения в этом браузере.");
      var bytes = await RNAssistantResourceDownload.read(transfer.data, readOptions);
      if (!current()) return;
      var url = URL.createObjectURL(new Blob([bytes], { type: transfer.contentType }));
      var link = document.createElement("a"); link.href = url; link.download = transfer.fileName; link.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 0); status("");
    } catch (error) { if (current()) status(error.name === "AbortError" ? "Сохранение отменено." : error.detail || error.message); }
    finally {
      if (writable) await writable.abort().catch(function () {});
      if (transfer && transfer.data) options.send("closeArtifactTransfer", { chatId: target.chatId, leaseId: transfer.data.leaseId }).catch(function () {});
    }
  }
  window.RNAssistantArtifactDownload = { save: save };
}());
