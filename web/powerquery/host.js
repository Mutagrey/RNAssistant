"use strict";
(function () {
    const bridge = window.chrome && window.chrome.webview;
    if (!bridge) return;
    let worker, request = null, ready = false, finished = false;
    const maximumCharacters = 32768;
    function send(message) {
        if (finished) return;
        let json;
        try { json = JSON.stringify(message); } catch (error) { fail(); return; }
        if (json.length > maximumCharacters) { fail(); return; }
        bridge.postMessage(json);
    }
    function fail() {
        if (finished) return;
        send({ type: "failed", message: "The local parser did not complete its analysis." });
        finished = true;
        if (worker) worker.terminate();
    }
    bridge.addEventListener("message", function (event) {
        if (finished) return;
        const value = event.data;
        if (!ready || request || !value || value.type !== "analyze" ||
            !value.request || typeof value.request.requestId !== "string" ||
            typeof value.request.source !== "string" || value.request.source.length > 262144 ||
            typeof value.request.sourceSha256 !== "string") { fail(); return; }
        request = value.request;
        worker.postMessage({ type: "analyze", request });
    });
    try {
        worker = new Worker("/worker.js");
        worker.onmessage = function (event) {
            if (finished) return;
            const value = event.data;
            if (value && value.type === "ready" && !ready && !request) {
                ready = true; send({ type: "ready" }); return;
            }
            if (!ready || !request || !value || value.type !== "complete" || !value.result ||
                value.result.requestId !== request.requestId || value.result.sourceSha256 !== request.sourceSha256) {
                fail(); return;
            }
            send({ type: "complete", result: value.result });
            if (!finished) { finished = true; worker.terminate(); }
        };
        worker.onerror = function (event) { event.preventDefault(); fail(); };
        worker.onmessageerror = fail;
    } catch (error) { fail(); }
}());
