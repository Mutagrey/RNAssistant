(function (root) {
    "use strict";

    function failure(code, message, path = "") {
        const error = new Error(message);
        error.code = code;
        error.path = path;
        return error;
    }

    // Application lifetime, independent of any preview DOM. One job per owner.
    // Cooperative mode is explicit and only for the bounded shipped core.
    function create(options) {
        if (!options || !["worker", "cooperative"].includes(options.mode))
            throw failure("EXECUTOR_MODE_INVALID", "Choose an explicit execution mode.");
        const mode = options.mode, workerUrl = options.workerUrl;
        const engine = root.RNTabularEngine, expectedRevision = options.expectedRevision || engine.revision;
        let active = null, disposed = false, sequence = 0;

        function inspect(input, settings = {}) {
            if (disposed) return Promise.reject(failure("EXECUTOR_DISPOSED", "Executor is disposed."));
            if (active) return Promise.reject(failure("EXECUTOR_BUSY", "A run is already active."));
            if (typeof input !== "string" || input.length > engine.maxInputCharacters)
                return Promise.reject(failure("INPUT_LIMIT", "Input is not bounded JSON text."));
            const deadlineMs = settings.deadlineMs === undefined ? 5000 : settings.deadlineMs;
            if (!Number.isInteger(deadlineMs) || deadlineMs < 1 || deadlineMs > 30000)
                return Promise.reject(failure("EXECUTOR_DEADLINE_INVALID", "Deadline must be 1 to 30000 ms."));
            if (settings.signal && settings.signal.aborted)
                return Promise.reject(failure("EXECUTION_CANCELLED", "Execution was cancelled."));
            return new Promise((resolve, reject) => {
                const job = { id: ++sequence, stopped: false, worker: null, timer: null };
                active = job;
                const abort = () => finish(failure("EXECUTION_CANCELLED", "Execution was cancelled."));
                function finish(error, result) {
                    if (job.stopped) return;
                    job.stopped = true;
                    clearTimeout(job.timer);
                    if (settings.signal) settings.signal.removeEventListener("abort", abort);
                    if (job.worker) {
                        job.worker.onmessage = job.worker.onerror = job.worker.onmessageerror = null;
                        job.worker.terminate();
                    }
                    if (active === job) active = null;
                    if (error) reject(error); else resolve(result);
                }
                job.stop = finish;
                const started = performance.now();
                function current() {
                    if (!job.stopped && performance.now() - started >= deadlineMs)
                        finish(failure("EXECUTION_TIMEOUT", "Execution deadline exceeded."));
                    if (!job.stopped && settings.signal && settings.signal.aborted) abort();
                    return !job.stopped;
                }
                const progress = value => {
                    if (current() && settings.onProgress) settings.onProgress(value);
                };
                job.timer = setTimeout(() => finish(failure("EXECUTION_TIMEOUT", "Execution deadline exceeded.")), deadlineMs);
                try {
                    if (settings.signal) settings.signal.addEventListener("abort", abort, { once: true });
                    if (mode === "cooperative") {
                        if (engine.revision !== expectedRevision)
                            throw failure("EXECUTOR_REVISION_MISMATCH", "Reload the shipped runtime before executing.");
                        engine.inspect(input, { isCancelled: () => !current(),
                            yieldControl: () => new Promise(done => setTimeout(done, 0)), onProgress: progress
                        }).then(result => { if (current()) finish(null, result); }, error => finish(error));
                        return;
                    }
                    if (typeof root.Worker !== "function" || !workerUrl)
                        throw failure("EXECUTOR_UNAVAILABLE", "Worker execution is unavailable in this context.");
                    job.worker = new root.Worker(workerUrl);
                    let ready = false;
                    job.worker.onmessage = event => {
                        if (!current()) return;
                        try {
                            const message = event.data;
                            if (!message || typeof message !== "object")
                                throw failure("EXECUTOR_PROTOCOL_INVALID", "Invalid Worker message.");
                            if (message.type === "ready" && !ready) {
                                if (message.protocol !== engine.protocol || message.revision !== expectedRevision)
                                    throw failure("EXECUTOR_REVISION_MISMATCH", "Worker runtime revision differs from its owner.");
                                ready = true;
                                job.worker.postMessage({ type: "inspect", id: job.id, protocol: engine.protocol,
                                    revision: expectedRevision, input });
                            } else if (!ready || message.id !== job.id) {
                                throw failure("EXECUTOR_PROTOCOL_INVALID", "Worker response belongs to another run.");
                            } else if (message.type === "progress") progress(message.progress);
                            else if (message.type === "result") {
                                if (!message.result || message.result.version !== 1 || message.result.engineRevision !== expectedRevision)
                                    throw failure("EXECUTOR_PROTOCOL_INVALID", "Worker returned a different result contract.");
                                finish(null, message.result);
                            } else if (message.type === "error" && message.error) {
                                finish(failure(message.error.code || "EXECUTION_FAILED", message.error.message, message.error.path));
                            } else throw failure("EXECUTOR_PROTOCOL_INVALID", "Unexpected Worker message.");
                        } catch (error) { finish(error); }
                    };
                    job.worker.onerror = event => {
                        event.preventDefault();
                        finish(failure("EXECUTOR_UNAVAILABLE", "Worker failed to load or execute; no fallback was used."));
                    };
                    job.worker.onmessageerror = () => finish(failure("EXECUTOR_PROTOCOL_INVALID", "Worker response cannot be decoded."));
                } catch (error) { finish(error); }
            });
        }

        return Object.freeze({ inspect, dispose: () => {
            disposed = true;
            if (active) active.stop(failure("EXECUTOR_DISPOSED", "Executor was disposed during the run."));
        } });
    }

    root.RNTabularExecutor = Object.freeze({ create });
}(globalThis));
