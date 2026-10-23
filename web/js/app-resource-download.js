(function () {
  "use strict";

  async function read(data, options, binary) {
    var payload = data && (binary ? data.binary && data.binary.payload : data.payload);
    var chunkBytes = data && (binary ? data.maxBatchBytes : data.maxChunkBytes);
    if (!data || !/^[a-f0-9]{64}$/.test(data.leaseId) ||
        data.url !== "https://rnassistant.local-resource/v1/" + (binary ? "" : "download/") + data.leaseId ||
        !payload || !/^[a-f0-9]{64}$/.test(payload.sha256) ||
        !Number.isSafeInteger(payload.byteLength) || payload.byteLength < 0 || payload.byteLength > options.maxBytes ||
        !Number.isInteger(chunkBytes) || chunkBytes < 1 || chunkBytes > 256 * 1024 ||
        binary && data.maxBatchItems !== chunkBytes)
      throw new Error("RESOURCE_DOWNLOAD_INVALID");
    if (!window.crypto || !window.crypto.subtle) throw new Error("RESOURCE_INTEGRITY_UNAVAILABLE");
    function active() {
      if (options.signal && options.signal.aborted || !options.isCurrent()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
    }
    active();
    var bytes = new Uint8Array(payload.byteLength), offset = 0, emptyPending = binary && bytes.length === 0;
    while (offset < bytes.length || emptyPending) {
      active();
      var count = Math.min(chunkBytes, bytes.length - offset);
      var abort = new AbortController();
      var cancel = function () { abort.abort(); };
      if (options.signal) options.signal.addEventListener("abort", cancel, { once: true });
      var timer = setTimeout(cancel, 30000);
      try {
        var response = await options.fetch(data.url + "?offset=" + offset + (binary ? "&limit=" : "&count=") + (count || 1),
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
        emptyPending = false;
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

  // File-backed downloads keep only one transport chunk in browser memory. The
  // source lease has already authenticated its complete CAS body; each chunk is
  // independently checked before it is committed to the destination file.
  async function save(data, options) {
    var payload = data && data.payload, chunkBytes = data && data.maxChunkBytes;
    try {
      if (!data || !/^[a-f0-9]{64}$/.test(data.leaseId) ||
        data.url !== "https://rnassistant.local-resource/v1/download/" + data.leaseId ||
        !payload || !/^[a-f0-9]{64}$/.test(payload.sha256) ||
        !Number.isSafeInteger(payload.byteLength) || payload.byteLength < 0 ||
        !Number.isInteger(chunkBytes) || chunkBytes < 1 || chunkBytes > 256 * 1024 ||
        !options || !options.writable || !window.crypto || !window.crypto.subtle)
        throw new Error("RESOURCE_DOWNLOAD_INVALID");
      function active() {
        if (options.signal && options.signal.aborted || !options.isCurrent()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      }
      for (var offset = 0; offset < payload.byteLength;) {
        active();
        var count = Math.min(chunkBytes, payload.byteLength - offset);
        var abort = new AbortController();
        var cancel = function () { abort.abort(); };
        if (options.signal) options.signal.addEventListener("abort", cancel, { once: true });
        var timer = setTimeout(cancel, 30000);
        try {
          var response = await options.fetch(data.url + "?offset=" + offset + "&count=" + count,
            { credentials: "omit", cache: "no-store", redirect: "error", signal: abort.signal });
          if (!response.ok) throw new Error("RESOURCE_DOWNLOAD_FAILED: " + response.status);
          if (response.headers.get("Content-Type") !== payload.contentType) throw new Error("RESOURCE_DOWNLOAD_INVALID");
          var expected = response.headers.get("X-RNAssistant-Chunk-SHA256");
          if (!expected || !/^[a-f0-9]{64}$/.test(expected)) throw new Error("RESOURCE_INTEGRITY_UNAVAILABLE");
          var bytes = new Uint8Array(count), received = 0, reader = response.body.getReader();
          try {
            while (true) {
              active();
              var part = await reader.read();
              active();
              if (part.done) break;
              if (received + part.value.byteLength > count) throw new Error("RESOURCE_BATCH_TOO_LARGE");
              bytes.set(part.value, received); received += part.value.byteLength;
            }
            if (received !== count) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
          } finally { await reader.cancel().catch(function () {}); reader.releaseLock(); }
          var digest = new Uint8Array(await window.crypto.subtle.digest("SHA-256", bytes));
          var actual = Array.from(digest, function (part) { return part.toString(16).padStart(2, "0"); }).join("");
          if (actual !== expected) throw new Error("RESOURCE_INTEGRITY_MISMATCH");
          active();
          await options.writable.write(bytes);
          offset += count;
        } finally {
          clearTimeout(timer);
          if (options.signal) options.signal.removeEventListener("abort", cancel);
        }
      }
      active();
      await options.writable.close();
    } catch (error) {
      if (options && options.writable && typeof options.writable.abort === "function")
        await options.writable.abort().catch(function () {});
      throw error;
    }
  }

  window.RNAssistantResourceDownload = { read: function (data, options) { return read(data, options, false); },
    readBinary: function (data, options) { return read(data, options, true); }, save: save };
}());
