'use strict';
(() => {
    const pending = new Map();
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    let nextId = 0;
    let names = [];
    let started = false;

    function bridge(body) {
        return new Promise((resolve, reject) => {
            const seq = ++nextId;
            if (!Number.isSafeInteger(seq)) return reject(Error('JS resource request limit reached.'));
            pending.set(seq, { resolve, reject });
            postMessage({ type: 'request', seq, body });
        });
    }

    const resources = Object.freeze({
        names: () => names.slice(),
        open: async name => {
            const opened = await bridge({ operation: 'open', name });
            if (!opened.ok) throw Error(opened.error);
            let closed = false, offset = 0, done = false;
            const handle = {
                descriptor: Object.freeze(opened.descriptor),
                read: async (options = {}) => {
                    if (closed || done) throw Error('RESOURCE_LEASE_CLOSED');
                    if (options.view && options.view !== opened.descriptor.view) throw Error('RESOURCE_VIEW_UNSUPPORTED');
                    if (options.path && options.path !== opened.descriptor.path) throw Error('RESOURCE_VIEW_PATH_UNSUPPORTED');
                    const response = await bridge({ operation: 'read', leaseId: opened.leaseId,
                        offset, limit: options.limit || opened.descriptor.maxBatchItems, fields: options.fields || null });
                    if (!response.ok) throw Error(response.error);
                    offset = response.batch.nextOffset;
                    done = response.batch.done;
                    if (response.batch.bytes) response.batch.bytes = Uint8Array.from(response.batch.bytes);
                    return response.batch;
                },
                stream: async function* (options = {}) {
                    try { while (!done && !closed) yield await handle.read(options); }
                    finally { await handle.close(); }
                },
                close: async () => {
                    if (closed) return;
                    closed = true;
                    const response = await bridge({ operation: 'close', leaseId: opened.leaseId });
                    if (!response.ok) throw Error(response.error);
                }
            };
            return Object.freeze(handle);
        }
    });
    Object.defineProperty(self, 'RN', { value: Object.freeze({ resources }), writable: false, configurable: false });

    self.addEventListener('message', async event => {
        const message = event.data;
        if (!message || typeof message !== 'object') return;
        if (message.type === 'reply') {
            const request = pending.get(message.seq);
            if (!request) return;
            pending.delete(message.seq);
            request.resolve(message.response);
            return;
        }
        if (message.type !== 'start' || started) return;
        started = true;
        try {
            if (typeof message.code !== 'string' || !message.code.trim()) throw Error('JS source is empty.');
            names = Array.isArray(message.names) ? message.names.slice() : [];
            const execute = new AsyncFunction('args', message.code);
            const result = await execute(message.args || {});
            const json = JSON.stringify(result);
            if (typeof json !== 'string') throw Error('JS must return a JSON-serializable value.');
            if (new TextEncoder().encode(json).length > 1024 * 1024) throw Error('JS result exceeds 1 MiB.');
            postMessage({ type: 'result', json });
        } catch (error) {
            postMessage({ type: 'error', message: String(error && error.message || error).slice(0, 4096) });
        }
    });
})();
