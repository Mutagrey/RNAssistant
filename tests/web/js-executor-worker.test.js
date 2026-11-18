'use strict';
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const source = fs.readFileSync(require('node:path').join(__dirname, '../../src/RNAssistant.Office/WebView/JsExecutorWorker.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
function makeWorker() {
    let receive;
    const outgoing = [];
    const sandbox = { TextEncoder, Uint8Array, Promise, Error,
        postMessage: message => outgoing.push(message),
        addEventListener: (name, fn) => { assert.equal(name, 'message'); receive = fn; } };
    sandbox.self = sandbox;
    vm.runInNewContext(source, sandbox);
    return { send: data => receive({ data }), outgoing, sandbox };
}
(async () => {
    {
        const worker = makeWorker();
        worker.send({ type: 'start', code: 'return {names: RN.resources.names(), sum: args.a + args.b};',
            args: { a: 2, b: 3 }, names: ['one'] });
        await tick();
        assert.equal(worker.outgoing[0].type, 'result');
        assert.deepEqual(JSON.parse(worker.outgoing[0].json), { names: ['one'], sum: 5 });
        worker.send({ type: 'start', code: 'return 99;', args: {}, names: [] });
        await tick(); assert.equal(worker.outgoing.length, 1);
    }
    {
        const worker = makeWorker();
        worker.send({ type: 'start', code: `const h = await RN.resources.open('t'); const result=[];
            for await (const b of h.stream({limit:2})) result.push(b.rows); return result;`,
            args: {}, names: ['t'] });
        await tick();
        assert.equal(worker.outgoing[0].type, 'request');
        assert.equal(worker.outgoing[0].body.operation, 'open');
        worker.send({ type: 'reply', seq: 1,
            response: { ok: true, leaseId: 'lease', descriptor: {view:'table',path:'$',maxBatchItems:500} } });
        await tick();
        assert.equal(worker.outgoing[1].body.operation, 'read');
        worker.send({ type: 'reply', seq: 2,
            response: { ok: true, batch: {rows:[[1],[2]], nextOffset:2, done:true} } });
        await tick();
        assert.equal(worker.outgoing[2].body.operation, 'close');
        worker.send({ type: 'reply', seq: 3, response: {ok:true} });
        await tick();
        assert.equal(worker.outgoing[3].type, 'result');
        assert.deepEqual(JSON.parse(worker.outgoing[3].json), [[[1],[2]]]);
    }
    {
        const worker = makeWorker();
        worker.send({ type:'start', code: 'return undefined;', names: [], args: {} });
        await tick(); assert.equal(worker.outgoing[0].type,'error');
    }
    console.log('PASS Web Worker protocol smoke checks');
})().catch(error => { console.error(error); process.exitCode = 1; });
