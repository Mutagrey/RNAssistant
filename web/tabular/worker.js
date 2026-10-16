"use strict";

// Only shipped, fixed modules. Requests cannot select scripts, URLs or operations.
importScripts("selectors.js", "snapshot.js", "engine.js");

let accepted = false;
self.onmessage = async function (event) {
    const message = event.data;
    if (accepted || !message || message.type !== "inspect" || message.protocol !== RNTabularEngine.protocol ||
        message.revision !== RNTabularEngine.revision || !Number.isSafeInteger(message.id)) {
        self.postMessage({ type: "error", id: message && message.id,
            error: { code: "EXECUTOR_PROTOCOL_INVALID", path: "", message: "Invalid execution request." } });
        return;
    }
    accepted = true;
    try {
        const result = await RNTabularEngine.inspect(message.input, {
            isCancelled: () => false, // The owning executor terminates this dedicated Worker on cancel/deadline.
            yieldControl: () => new Promise(resolve => setTimeout(resolve, 0)),
            onProgress: progress => self.postMessage({ type: "progress", id: message.id, progress })
        });
        self.postMessage({ type: "result", id: message.id, result });
    } catch (error) {
        self.postMessage({ type: "error", id: message.id, error: {
            code: error.code || "EXECUTION_FAILED", path: error.path || "", message: error.message
        } });
    }
};
self.postMessage({ type: "ready", protocol: RNTabularEngine.protocol, revision: RNTabularEngine.revision });
