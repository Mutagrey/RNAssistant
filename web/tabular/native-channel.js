(function (root) {
    "use strict";
    const bridge = root.chrome && root.chrome.webview;
    if (!bridge) return; // The test/probe API remains usable without a native host.
    const failure = (code, message) => Object.assign(new Error(message), { code });
    const payloadLimit = 263168;
    let job = null, pending = null, stopped = false, controller = null, sequence = 0;

    function send(kind, fields = {}) {
        const envelope = { version: 1, jobId: job ? job.jobId : "", kind, sequence: 0, recordCount: 0,
            payloadJson: null, progress: null, errorCode: null, message: null, ...fields };
        // Same closed canonical wire representation as the typed native DTO.
        bridge.postMessage(JSON.stringify(envelope).replace(/[\u0085\u2028\u2029]/g,
            value => "\\u" + value.charCodeAt(0).toString(16).padStart(4, "0")));
    }
    function abort(error) {
        if (stopped) return;
        stopped = true;
        if (controller) controller.abort();
        if (pending) { const reject = pending.reject; pending = null; reject(error); }
        send("Failed", { errorCode: String(error.code || "NATIVE_CHANNEL_FAILED").slice(0, 64),
            message: String(error.message || "Native execution failed.").slice(0, 512) });
    }
    function accept(kind, value, count) {
        if (stopped || pending) return Promise.reject(failure("NATIVE_CHANNEL_INVALID", "Output is closed or awaiting acknowledgement."));
        const payloadJson = JSON.stringify(value);
        if (payloadJson.length > payloadLimit) return Promise.reject(failure("RESULT_LIMIT", "Native output exceeds the batch budget."));
        return new Promise((resolve, reject) => {
            pending = { sequence: ++sequence, resolve, reject };
            send(kind, { sequence, recordCount: count, payloadJson });
        });
    }
    async function execute(command) {
        controller = new AbortController();
        try {
            const result = await root.RNTabularNativeRuntime.extractXlsx(command.inputJson, {
                signal: controller.signal, deadlineMs: command.deadlineMs, bundleSha256: command.bundleSha256,
                onProgress: value => {
                    if (stopped) return;
                    const bytes = value.phase === "reading-xlsx";
                    send("Progress", { progress: { phase: value.phase,
                        completed: bytes ? value.sourceBytesRead : value.completedRecords,
                        // Selective ZIP reads can revisit ranges; transferred bytes
                        // are not a percentage of the physical file length.
                        total: bytes ? null : value.totalRecords, unit: bytes ? "Bytes" : "Records" } });
                }, sink: {
                    writeBatch: batch => accept("Batch", batch, batch.records.length),
                    complete: result => accept("Validate", result, result.recordCount),
                    abort // Native sink is revoked independently on host failure/cancel.
                }
            });
            if (stopped) return;
            stopped = true;
            send("Succeeded", { sequence: ++sequence, recordCount: result.recordCount });
        } catch (error) { abort(error); }
    }
    bridge.addEventListener("message", event => {
        if (stopped) return;
        try {
            const command = event.data;
            const fields = ["version", "jobId", "kind", "sequence", "inputJson", "deadlineMs", "bundleSha256"];
            if (!command || Object.keys(command).length !== fields.length || fields.some(key => !Object.hasOwn(command, key)) ||
                command.version !== 1 || !/^[a-f0-9]{32}$/.test(command.jobId))
                throw failure("NATIVE_CHANNEL_INVALID", "Invalid native execution command.");
            if (command.kind === "Start") {
                if (job || command.sequence !== 0 || typeof command.inputJson !== "string" || !command.inputJson.length ||
                    command.inputJson.length > 1000000 || !/^[a-f0-9]{64}$/.test(command.bundleSha256) ||
                    !Number.isInteger(command.deadlineMs) || command.deadlineMs < 1 || command.deadlineMs > 300000)
                    throw failure("NATIVE_CHANNEL_INVALID", "Invalid or repeated start command.");
                job = command; void execute(command);
            } else if (command.kind === "Acknowledge" && job && command.jobId === job.jobId && pending &&
                command.sequence === pending.sequence && command.inputJson === null && command.deadlineMs === 0 && command.bundleSha256 === null) {
                const resolve = pending.resolve; pending = null; resolve();
            } else throw failure("NATIVE_CHANNEL_INVALID", "Stale, foreign or unexpected acknowledgement.");
        } catch (error) { abort(error); }
    });
    send("Ready");
}(globalThis));
