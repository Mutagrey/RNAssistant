(function (root) {
    "use strict";
    const maximumReadBytes = 256 * 1024, sources = new WeakSet();
    function failure(code, message) { return Object.assign(new Error(message), { code, path: "/source" }); }

    // An owned read capability, never a path/URL resolver or resource authority.
    // The caller pins bytes before admission. close must synchronously revoke access.
    function create({ byteLength, label, readRange, close }) {
        if (!Number.isSafeInteger(byteLength) || byteLength < 1 || typeof readRange !== "function" ||
            typeof close !== "function" || typeof label !== "string" || !label || label.length > 256)
            throw failure("SOURCE_CONTRACT_INVALID", "A byte source needs a fixed length, label, bounded reader and synchronous close.");
        const lifetime = new AbortController();
        let closed = false, busy = false;
        const source = Object.freeze({ byteLength, label, signal: lifetime.signal, get closed() { return closed; },
            readRange(offset, count) {
                if (closed) return Promise.reject(failure("SOURCE_CLOSED", "Source access has closed."));
                if (busy) return Promise.reject(failure("SOURCE_READ_PENDING", "Only one source read may be outstanding."));
                if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(count) || offset < 0 || count < 1 ||
                    count > maximumReadBytes || offset > byteLength - count)
                    return Promise.reject(failure("SOURCE_RANGE_INVALID", "Source read exceeds the fixed length or chunk budget."));
                busy = true;
                return new Promise((resolve, reject) => {
                    let settled = false;
                    const finish = (error, value) => {
                        if (settled) return;
                        settled = true; busy = false; lifetime.signal.removeEventListener("abort", cancelled);
                        if (error) reject(error); else resolve(value);
                    };
                    const cancelled = () => finish(failure("SOURCE_CLOSED", "Source closed during a read."));
                    lifetime.signal.addEventListener("abort", cancelled, { once: true });
                    Promise.resolve().then(() => {
                        if (closed) throw failure("SOURCE_CLOSED", "Source access has closed.");
                        return readRange(offset, count, lifetime.signal);
                    }).then(value => {
                        if (settled || closed) return;
                        try {
                            const bytes = ArrayBuffer.isView(value) && value.BYTES_PER_ELEMENT === 1
                                ? new Uint8Array(value.buffer, value.byteOffset, value.byteLength)
                                : Object.prototype.toString.call(value) === "[object ArrayBuffer]" ? new Uint8Array(value) : null;
                            if (!bytes || bytes.length !== count || Object.prototype.toString.call(bytes.buffer) !== "[object ArrayBuffer]")
                                throw failure("SOURCE_READ_INVALID", "Source returned a different byte range length or type.");
                            // Detach from provider-owned/reused buffers before transfer to an executor.
                            finish(null, bytes.slice());
                        } catch (error) { finish(error); }
                    }, error => finish(error));
                });
            },
            dispose() {
                if (closed) return;
                closed = true; lifetime.abort();
                const result = close();
                if (result && typeof result.then === "function") {
                    Promise.resolve(result).catch(() => {});
                    throw failure("SOURCE_CLOSE_INVALID", "Source close must revoke access synchronously.");
                }
            }
        });
        sources.add(source); return source;
    }
    function fromBlob(blob) {
        if (!(blob instanceof Blob)) throw failure("SOURCE_CONTRACT_INVALID", "Expected a selected Blob.");
        return create({ byteLength: blob.size, label: typeof blob.name === "string" ? blob.name.slice(0, 256) : "Selected XLSX",
            readRange: (offset, count) => blob.slice(offset, offset + count).arrayBuffer(), close: () => {} });
    }
    root.RNTabularByteSource = Object.freeze({ create, fromBlob, isSource: value => sources.has(value), maximumReadBytes });
}(globalThis));
