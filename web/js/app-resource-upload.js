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
        // WebView2 Request.Content can omit Blob/File-backed request bodies.
        // Materialize only this bounded chunk, never the whole attachment.
        var bytes = await body.slice(offset, offset + count).arrayBuffer();
        active();
        if (chunk.signal.aborted) throw new Error("RESOURCE_UPLOAD_TIMEOUT");
        if (bytes.byteLength !== count) throw new Error("RESOURCE_UPLOAD_INVALID");
        var response = await fetch(lease.url + "?offset=" + offset + "&count=" + count, {
          method: "POST", body: bytes, headers: { "Content-Type": "application/octet-stream" },
          credentials: "omit", cache: "no-store", redirect: "error", signal: chunk.signal
        });
        if (!response.ok) {
          var failure = await response.json().catch(function () { return null; });
          throw new Error("RESOURCE_UPLOAD_FAILED: " + response.status +
            (failure && typeof failure.message === "string" ? " · " + failure.message : ""));
        }
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
