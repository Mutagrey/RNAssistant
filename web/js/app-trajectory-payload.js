(function () {
  "use strict";
  var active = new Set();
  var maximumCharacters = 512 * 1024;

  async function read(chatId, eventId, options) {
    if (!chatId || !eventId) throw new Error("RESOURCE_ACCESS_DENIED");
    if (active.size >= 2) throw new Error("RESOURCE_BACKPRESSURE");
    var abort = new AbortController(), requestId = null, data = null, closed = false, result;
    function current() { return !abort.signal.aborted && options.isCurrent(); }
    function close() {
      if (closed || !data || !/^[a-f0-9]{64}$/.test(data.leaseId)) return Promise.resolve();
      closed = true;
      return options.send("resourceDataClose", { chatId: chatId, workspaceId: "trajectory-payload",
        leaseId: data.leaseId }).catch(function () {});
    }
    function cancel() {
      abort.abort();
      if (requestId) options.cancelRequest(requestId).catch(function () {});
      close();
    }
    active.add(cancel);
    if (options.signal) options.signal.addEventListener("abort", cancel, { once: true });
    try {
      if (options.signal && options.signal.aborted) cancel();
      if (!current()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      var request = options.send("getChatEventPayload", { chatId: chatId, eventId: eventId });
      requestId = request.requestId;
      var response = await request;
      requestId = null;
      data = response && response.data;
      if (!current()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      var payload = data && data.payload;
      if (!response || response.chatId !== chatId || response.eventId !== eventId ||
          !/^[a-f0-9]{64}$/.test(response.sha256) || !Number.isInteger(response.byteLength) ||
          response.byteLength < 0 || response.byteLength > 32 * 1024 * 1024 ||
          !Number.isInteger(response.returnedCharacters) || response.returnedCharacters < 0 ||
          response.returnedCharacters > maximumCharacters || typeof response.textTruncated !== "boolean" ||
          typeof response.contentType !== "string" || !payload || payload.contentType !== "text/plain; charset=utf-8" ||
          (response.textTruncated ? payload.byteLength >= response.byteLength :
            payload.byteLength !== response.byteLength || payload.sha256 !== response.sha256))
        throw new Error("RESOURCE_DOWNLOAD_INVALID");
      var bytes = await window.RNAssistantResourceDownload.read(data, { fetch: options.fetch,
        signal: abort.signal, isCurrent: current, maxBytes: 4 * (maximumCharacters + 1) });
      var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
      if (text.length !== response.returnedCharacters) throw new Error("RESOURCE_DOWNLOAD_INVALID");
      if (!current()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      result = { text: text, contentType: response.contentType, textTruncated: response.textTruncated };
    } finally {
      if (options.signal) options.signal.removeEventListener("abort", cancel);
      try { await close(); }
      finally { active.delete(cancel); }
    }
    if (!current() || options.signal && options.signal.aborted) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
    return result;
  }

  window.RNAssistantTrajectoryPayload = { read: read,
    cancelAll: function () { active.forEach(function (cancel) { cancel(); }); } };
}());
