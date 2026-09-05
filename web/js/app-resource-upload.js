(function () {
  "use strict";

  async function write(lease, body, options) {
    options = options || {};
    function active() {
      if (options.signal && options.signal.aborted || options.isCurrent && !options.isCurrent())
        throw new Error("RESOURCE_UPLOAD_CANCELLED");
    }
    active();
    if (!lease || !body || !Number.isSafeInteger(body.size) || body.size < 0 || body.size > options.maxBytes ||
        !Number.isSafeInteger(options.maxBytes) || !/^[a-f0-9]{64}$/.test(lease.leaseId) ||
        lease.url !== "https://rnassistant.local-resource/v1/upload/" + lease.leaseId ||
        lease.byteLength !== body.size || !Number.isInteger(lease.maxChunkBytes) ||
        lease.maxChunkBytes < 1 || lease.maxChunkBytes > 256 * 1024)
      throw new Error("RESOURCE_UPLOAD_INVALID");
    for (var offset = 0; offset < body.size;) {
      active();
      var count = Math.min(lease.maxChunkBytes, body.size - offset);
      var chunk = new AbortController();
      var abortChunk = function () { chunk.abort(); };
      if (options.signal) options.signal.addEventListener("abort", abortChunk, { once: true });
      var timer = setTimeout(abortChunk, 30000);
      try {
        var response = await fetch(lease.url + "?offset=" + offset + "&count=" + count, {
          method: "POST", body: body.slice(offset, offset + count, "application/octet-stream"),
          credentials: "omit", cache: "no-store", redirect: "error", signal: chunk.signal
        });
        if (!response.ok) throw new Error("RESOURCE_UPLOAD_FAILED: " + response.status);
        var ack = await response.json();
        active();
        if (ack.leaseId !== lease.leaseId || ack.nextOffset !== offset + count)
          throw new Error("RESOURCE_CURSOR_INVALID");
        offset = ack.nextOffset;
      } finally {
        clearTimeout(timer);
        if (options.signal) options.signal.removeEventListener("abort", abortChunk);
      }
    }
    active();
  }

  window.RNAssistantResourceUpload = { write: write };
}());
