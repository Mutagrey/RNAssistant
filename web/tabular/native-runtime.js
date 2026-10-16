(function (root) {
    "use strict";

    const failure = (code, message) => Object.assign(new Error(message), { code });
    let admitted = false;

    // One native resource session, one extraction. The native host owns the source
    // and deadline independently of this page. No paths, arbitrary URLs or scripts.
    async function extractXlsx(input, settings = {}) {
        if (admitted) throw failure("NATIVE_SESSION_USED", "A native session accepts one extraction.");
        if (!/^https?:$/.test(root.location.protocol) || !/^\/r\/[a-f0-9]{64}\/tabular\/runtime\.html$/.test(root.location.pathname) ||
            root.location.search || root.location.hash)
            throw failure("NATIVE_CONTEXT_REQUIRED", "Open the native runtime resource session.");
        admitted = true;
        const base = new URL("../", root.location.href), sourceUrl = new URL("source", base).href;
        const request = { credentials: "omit", cache: "no-store", redirect: "error", mode: "same-origin" };
        let source, executor, problem, result, sequence = 0;
        try {
            const response = await fetch(new URL("descriptor.json", base), { ...request, signal: settings.signal });
            if (!response.ok || response.headers.get("content-type") !== "application/json" ||
                !/^[1-9][0-9]{0,3}$/.test(response.headers.get("content-length") || ""))
                throw failure("NATIVE_DESCRIPTOR_INVALID", "Native source descriptor is unavailable.");
            const descriptor = await response.json();
            const fields = ["version", "bundleSha256", "workerProtocol", "engineRevision", "byteLength", "label"];
            if (!descriptor || Object.keys(descriptor).length !== fields.length || fields.some(key => !Object.prototype.hasOwnProperty.call(descriptor, key)) ||
                descriptor.version !== 1 || !/^[a-f0-9]{64}$/.test(descriptor.bundleSha256) ||
                descriptor.workerProtocol !== root.RNTabularEngine.protocol || descriptor.engineRevision !== root.RNTabularEngine.revision ||
                !Number.isSafeInteger(descriptor.byteLength) || descriptor.byteLength < 1 || descriptor.byteLength > root.RNTabularEngine.maxSourceBytes ||
                typeof descriptor.label !== "string" || !descriptor.label.length || descriptor.label.length > 256)
                throw failure("NATIVE_DESCRIPTOR_INVALID", "Native source descriptor does not match the runtime.");
            source = root.RNTabularByteSource.create({ byteLength: descriptor.byteLength, label: descriptor.label,
                readRange: async (offset, count, signal) => {
                    const readSequence = ++sequence;
                    const reply = await fetch(sourceUrl + `?sequence=${readSequence}&offset=${offset}&count=${count}`, { ...request, signal });
                    if (!reply.ok || reply.headers.get("content-type") !== "application/octet-stream" ||
                        reply.headers.get("content-length") !== String(count) ||
                        reply.headers.get("x-rna-source-sequence") !== String(readSequence) ||
                        reply.headers.get("x-rna-source-offset") !== String(offset))
                        throw failure("NATIVE_SOURCE_READ_FAILED", "Native source range was refused or mismatched.");
                    return reply.arrayBuffer();
                }, close: () => {} }); // Byte-source disposal aborts its fetch before native drain below.
            executor = root.RNTabularExecutor.create({ mode: "worker", workerUrl: new URL("tabular/worker.js", base).href,
                expectedRevision: descriptor.engineRevision });
            result = await executor.extractXlsxSource(input, source, settings);
        } catch (error) {
            problem = settings.signal && settings.signal.aborted ? failure("EXECUTION_CANCELLED", "Execution was cancelled.")
                : typeof error.code === "string" ? error : failure("NATIVE_TRANSPORT_FAILED", "Native resource transport failed.");
        }
        finally {
            if (executor) executor.dispose();
            if (source) source.dispose();
            const closing = new AbortController(), closeTimer = setTimeout(() => closing.abort(), 5000);
            try {
                // This awaited drain is outside the synchronous byte-source close.
                // Native token/deadline/disposal also closes it if this page is lost.
                const closed = await fetch(sourceUrl, { ...request, method: "DELETE", signal: closing.signal });
                if (closed.status !== 204) throw failure("NATIVE_SOURCE_CLOSE_FAILED", "Native source cleanup failed.");
            } catch (error) {
                if (!problem) {
                    problem = failure("NATIVE_SOURCE_CLOSE_FAILED", "Native source cleanup was not confirmed.");
                    // A completed portable sink still holds provisional output only.
                    try { if (settings.sink) settings.sink.abort(problem); }
                    catch (abortError) { problem.sinkAbortFailure = abortError.message; }
                }
            } finally { clearTimeout(closeTimer); }
        }
        if (problem) throw problem;
        return result;
    }

    root.RNTabularNativeRuntime = Object.freeze({ extractXlsx });
}(globalThis));
