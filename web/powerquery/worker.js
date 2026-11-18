"use strict";
importScripts("/parser.bundle.js", "/analyze.js");
let started = false;
self.onmessage = async function (event) {
    if (started) { self.postMessage({ type: "failed" }); self.close(); return; }
    started = true;
    try {
        if (!event.data || event.data.type !== "analyze" || !event.data.request)
            throw new Error("Invalid analysis request.");
        const result = await self.RNPowerQueryAnalysis.analyze(event.data.request);
        self.postMessage({ type: "complete", result });
    } catch (error) {
        self.postMessage({ type: "failed" });
    }
    self.close();
};
self.postMessage({ type: "ready" });
