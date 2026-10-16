"use strict";

// Only shipped modules and the two declared operations. No project scripts/URLs.
importScripts("selectors.js", "snapshot.js", "json.js", "expression.js", "template.js", "matrix.js", "engine.js");

let accepted = false, requestId = null, pendingBatch = null;
self.onmessage = async function (event) {
    const message = event.data;
    if (message && message.type === "ack" && accepted && pendingBatch && message.id === requestId &&
        message.sequence === pendingBatch.sequence && message.protocol === RNTabularEngine.protocol) {
        const resolve = pendingBatch.resolve; pendingBatch = null; resolve(); return;
    }
    if (accepted || !message || !["inspect", "extract"].includes(message.type) || message.protocol !== RNTabularEngine.protocol ||
        message.revision !== RNTabularEngine.revision || !Number.isSafeInteger(message.id)) {
        self.postMessage({ type: "error", id: message && message.id,
            error: { code: "EXECUTOR_PROTOCOL_INVALID", path: "", message: "Invalid execution request." } });
        return;
    }
    accepted = true; requestId = message.id;
    try {
        const result = await RNTabularEngine[message.type](message.input, {
            isCancelled: () => false, // The owning executor terminates this dedicated Worker on cancel/deadline.
            yieldControl: () => new Promise(resolve => setTimeout(resolve, 0)),
            onProgress: progress => self.postMessage({ type: "progress", id: message.id, progress }),
            onBatch: batch => new Promise(resolve => {
                pendingBatch = { sequence: batch.sequence, resolve };
                self.postMessage({ type: "batch", id: message.id, batch });
            })
        });
        self.postMessage({ type: "result", id: message.id, result });
    } catch (error) {
        self.postMessage({ type: "error", id: message.id, error: {
            code: error.code || "EXECUTION_FAILED", path: error.path || "", message: error.message, location: error.location || null
        } });
    }
};
self.postMessage({ type: "ready", protocol: RNTabularEngine.protocol, revision: RNTabularEngine.revision });
