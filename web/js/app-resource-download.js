(function () {
  "use strict";

  async function read(data, options) {
    var payload = data && data.payload;
    if (!data || !/^[a-f0-9]{64}$/.test(data.leaseId) ||
        data.url !== "https://rnassistant.local-resource/v1/download/" + data.leaseId ||
        !payload || !/^[a-f0-9]{64}$/.test(payload.sha256) ||
        !Number.isInteger(payload.byteLength) || payload.byteLength < 0 || payload.byteLength > options.maxBytes ||
        !Number.isInteger(data.maxChunkBytes) || data.maxChunkBytes < 1 || data.maxChunkBytes > 256 * 1024)
      throw new Error("RESOURCE_DOWNLOAD_INVALID");
    if (!window.crypto || !window.crypto.subtle) throw new Error("RESOURCE_INTEGRITY_UNAVAILABLE");
    function active() {
      if (options.signal && options.signal.aborted || !options.isCurrent()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
    }
    active();
    var bytes = new Uint8Array(payload.byteLength), offset = 0;
    while (offset < bytes.length) {
      active();
      var count = Math.min(data.maxChunkBytes, bytes.length - offset);
      var abort = new AbortController();
      var cancel = function () { abort.abort(); };
      if (options.signal) options.signal.addEventListener("abort", cancel, { once: true });
      var timer = setTimeout(cancel, 30000);
      try {
        var response = await options.fetch(data.url + "?offset=" + offset + "&count=" + count,
          { credentials: "omit", cache: "no-store", redirect: "error", signal: abort.signal });
        if (!response.ok) throw new Error("RESOURCE_DOWNLOAD_FAILED: " + response.status);
        if (response.headers.get("Content-Type") !== payload.contentType) throw new Error("RESOURCE_DOWNLOAD_INVALID");
        var reader = response.body.getReader(), received = 0;
        try {
          while (true) {
            active();
            var chunk = await reader.read();
            active();
            if (chunk.done) break;
            if (received + chunk.value.byteLength > count) throw new Error("RESOURCE_BATCH_TOO_LARGE");
            bytes.set(chunk.value, offset + received);
            received += chunk.value.byteLength;
          }
          if (received !== count) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
        } finally { await reader.cancel().catch(function () {}); reader.releaseLock(); }
        offset += count;
      } finally {
        clearTimeout(timer);
        if (options.signal) options.signal.removeEventListener("abort", cancel);
      }
    }
    active();
    var digest = new Uint8Array(await window.crypto.subtle.digest("SHA-256", bytes));
    var hash = Array.from(digest, function (part) { return part.toString(16).padStart(2, "0"); }).join("");
    if (hash !== payload.sha256) throw new Error("RESOURCE_INTEGRITY_MISMATCH");
    active();
    return bytes;
  }

  window.RNAssistantResourceDownload = { read: read };
}());
