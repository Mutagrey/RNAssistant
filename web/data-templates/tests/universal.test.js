"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const folder = path.resolve(__dirname, "../core");
const context = vm.createContext({ setTimeout, clearTimeout, performance, AbortController });
for (const file of ["selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "expression.js", "template.js", "transform.js", "engine.js", "executor.js"])
    vm.runInContext(fs.readFileSync(path.join(folder, file), "utf8"), context, { filename: file });
const json = value => JSON.parse(JSON.stringify(value));
const fixture = name => JSON.parse(fs.readFileSync(path.join(folder, "examples/" + name + ".json"), "utf8"));
const hooks = { isCancelled: () => false, yieldControl: async () => {}, onProgress: () => {} };
async function run(input) {
    const records = [];
    const result = await context.RNTabularEngine.extract(JSON.stringify(input), { ...hooks, onBatch: async batch => records.push(...json(batch.records)) });
    return { records, result: json(result) };
}
const semantic = records => records.map(({ provenance, ...record }) => record).sort((a, b) => a.parameterId.localeCompare(b.parameterId) || a.period.month - b.period.month);
// Independent business oracle: manually specified, never derived from the template, source adapters or another run.
const expected = [
    { parameterId: "production.loss", dimensions: { entity: "plant-1", variant: "actual" }, period: { kind: "month", year: 2026, month: 1 }, value: { kind: "decimal", value: "0" }, unitId: "t", role: "observation", temporalMeaning: "flow" },
    { parameterId: "production.loss", dimensions: { entity: "plant-1", variant: "actual" }, period: { kind: "month", year: 2026, month: 2 }, value: { kind: "missing", reason: "empty-text" }, unitId: "t", role: "observation", temporalMeaning: "flow" },
    { parameterId: "production.volume", dimensions: { entity: "plant-1", variant: "actual" }, period: { kind: "month", year: 2026, month: 1 }, value: { kind: "decimal", value: "10.12" }, unitId: "t", role: "observation", temporalMeaning: "flow" },
    { parameterId: "production.volume", dimensions: { entity: "plant-1", variant: "actual" }, period: { kind: "month", year: 2026, month: 2 }, value: { kind: "decimal", value: "20.14" }, unitId: "t", role: "observation", temporalMeaning: "flow" }
];
async function refuses(name, change, code) {
    const input = fixture(name); change(input);
    await assert.rejects(run(input), error => error.code === code, code);
}
(async () => {
    for (const name of ["matrix-roles", "matrix-transposed", "records-extraction", "json-extraction"]) {
        const result = await run(fixture(name));
        assert.deepEqual(semantic(result.records), expected, name);
        assert.equal(result.result.recordCount, 4); assert.equal(result.result.missingValueCount, 1);
        assert.equal(result.result.coverage.complete, true);
    }
    const rotated = (await run(fixture("matrix-transposed"))).records[0];
    assert.deepEqual(rotated.provenance.parameter.evidence.map(item => item.location.address), ["C1"]);
    assert.deepEqual(rotated.provenance.period.evidence.map(item => item.location.address), ["A3", "B3"]);
    assert.equal(rotated.provenance.value.evidence[0].location.address, "C3");
    assert.deepEqual(rotated.provenance.parameter.mappings.map(item => [item.source, item.targetId]), [["0012", "production.volume"]]);
    const record = (await run(fixture("records-extraction"))).records[0];
    assert.deepEqual(record.provenance.value.evidence[0].location, { kind: "field", record: 1, field: "amount" });
    assert.equal(record.provenance.dimensions.entity.evidence[0].location.field, "entity");
    const nested = (await run(fixture("json-extraction"))).records[0];
    assert.deepEqual(nested.provenance.value.evidence[0].location, { kind: "json-pointer", pointer: "/report/observations/0/amount" });
    assert.equal(nested.provenance.parameter.evidence[0].location.pointer, "/report/observations/0/measure/code");
    console.log("PASS universal: independent canonical oracle across both matrix orientations, named records and nested JSON, with native lineage");

    const original = fixture("matrix-transposed").template, plan = context.RNTabularTemplate.compile(original);
    original.source.rows[0].bindings.period.expr = "bad";
    assert.ok(Object.isFrozen(plan.regions["rows/columns"].period.ast));
    assert.ok(Object.isFrozen(plan.dependencies)); assert.ok(plan.dependencies.some(item => item.op === "cell" && item.row === "row"));
    for (const [name, change, code] of [
        ["matrix-roles", v => { v.template.source.rows[0].bindings.period = v.template.source.columns[0].bindings.period; }, "EXPRESSION_TYPE"],
        ["matrix-roles", v => { v.template.bindings.role = { expr: '"observation"' }; }, "BINDING_CONFLICT"],
        ["matrix-roles", v => { delete v.template.source.rows[0].bindings.unit; }, "BINDING_MISSING"],
        ["matrix-transposed", v => { v.template.source.rows.push(v.template.source.rows[0]); }, "BINDING_CONFLICT"],
        ["records-extraction", v => { v.template.bindings.value.expr = 'decimal(raw(cell("A", 1)), "en-US")'; }, "EXPRESSION_TYPE"],
        ["records-extraction", v => { v.template.bindings.value.expr = 'decimal(raw(field("missing")), "en-US")'; }, "EXPRESSION_TYPE"],
        ["records-extraction", v => { v.template.context.year = { expr: 'text(field("year"))' }; }, "EXPRESSION_TYPE"],
        ["records-extraction", v => { v.dataset.dimensions.entity = "plant-1"; }, "BINDING_CONFLICT"],
        ["records-extraction", v => { v.snapshot.records.push(v.snapshot.records[0]); }, "DUPLICATE_OBSERVATION"],
        ["records-extraction", v => { v.template.policies.duplicates = "sum"; }, "POLICY_UNSUPPORTED"],
        ["records-extraction", v => { v.template.source.fields.push("code"); }, "SCHEMA_INVALID"],
        ["json-extraction", v => { v.template.bindings.value.expr = 'decimal(raw(path("/bad~2")), "en-US")'; }, "SOURCE_PATH_INVALID"],
        ["json-extraction", v => { v.template.bindings.value.expr = 'decimal(raw(path("/measure")), "en-US")'; }, "SOURCE_TYPE"],
        ["json-extraction", v => { v.template.source.collection = "/missing"; }, "SOURCE_LIMIT"],
        ["json-extraction", v => { v.template.source.maxRecords = 3; }, "SOURCE_LIMIT"]
    ]) await refuses(name, change, code);
    const missing = fixture("json-extraction"), items = missing.snapshot.document.report.observations;
    items[0].amount = null; delete items[1].amount;
    assert.deepEqual((await run(missing)).records.map(item => item.value), [
        { kind: "missing", reason: "null" }, { kind: "missing", reason: "absent" }, { kind: "decimal", value: "0" }, { kind: "missing", reason: "empty-text" }
    ]);
    const escaped = fixture("json-extraction");
    escaped.template.bindings.value.expr = 'decimal(raw(path("/a~1b/~0value")), "en-US")';
    escaped.snapshot.document.report.observations.forEach(item => { item["a/b"] = { "~value": "1.25" }; });
    assert.equal((await run(escaped)).records[0].value.value, "1.25");
    const exact = JSON.stringify(fixture("json-extraction")).replace('"10.125"', '9007199254740993.125');
    const exactRecords = [];
    await context.RNTabularEngine.extract(exact, { ...hooks, onBatch: async batch => exactRecords.push(...json(batch.records)) });
    assert.equal(exactRecords[0].value.value, "9007199254740993.12");
    assert.equal(exactRecords[0].provenance.value.evidence[0].cell.lexeme, "9007199254740993.125");
    await refuses("json-extraction", v => { v.snapshot.document.report.observations[0].amount = { kind: "numeric", lexeme: "1" }; }, "SOURCE_TYPE");
    console.log("PASS universal contracts: frozen AST/plan, model-specific access, explicit conflicts, native null/absent and precision refusals");

    for (const [number, expression, expectedValue] of [
        ["9007199254740993.125", 'scale(decimal(raw(field("amount")), "en-US"), "1000")', "9007199254740993125"],
        ["-2.345", 'round(decimal(raw(field("amount")), "en-US"), 2, "half-even")', "-2.34"],
        ["-2.345", 'round(decimal(raw(field("amount")), "en-US"), 2, "half-away")', "-2.35"],
        ["9.999", 'round(decimal(raw(field("amount")), "en-US"), 2, "half-even")', "10"],
        ["-9.999", 'round(decimal(raw(field("amount")), "en-US"), 2, "truncate")', "-9.99"]
    ]) {
        const v = fixture("records-extraction"); v.snapshot.records[0].amount.value = number; v.template.bindings.value.expr = expression;
        assert.equal((await run(v)).records[0].value.value, expectedValue);
    }
    await refuses("records-extraction", v => { v.template.bindings.value.expr = 'round(decimal(raw(field("amount")), "en-US"), 19, "half-even")'; }, "EXPRESSION_TYPE");
    await refuses("records-extraction", v => { v.template.bindings.value.expr = 'scale(decimal(raw(field("amount")), "en-US"), "99999999999999999999999999999999999999")'; }, "DECIMAL_PRECISION");
    console.log("PASS decimal transformations: exact scaling, signed ties, half-even/half-away/truncate, overflow without implicit rounding");

    const v = fixture("records-extraction"); v.template.source.maxRecords = 200;
    const compiled = context.RNTabularTemplate.compile(v.template), dictionaries = context.RNTabularTemplate.openDictionaries(v.dictionaries);
    let calls = [], release, entered, cancelled = false;
    const waiting = new Promise(resolve => { entered = resolve; });
    const source = context.RNTabularSources.records(compiled, v.snapshot.source, 130, async (offset, count) => {
        calls.push(offset);
        return { offset, done: offset + count === 130, records: Array.from({ length: count }, (_, index) => ({ ...v.snapshot.records[0], entity: { kind: "text", value: "plant-" + (offset + index) } })) };
    });
    let batches = 0;
    const pending = context.RNTabularTransform.execute(compiled, source, dictionaries, v.dataset, { ...hooks, isCancelled: () => cancelled, onBatch: async () => {
        batches++; if (batches === 1) { entered(); await new Promise(resolve => { release = resolve; }); }
    } });
    await waiting; assert.deepEqual(calls, [0], "sink backpressure stops source read-ahead");
    cancelled = true; release(); await assert.rejects(pending, error => error.code === "EXECUTION_CANCELLED");
    assert.deepEqual(calls, [0]);
    cancelled = false; calls = [];
    const summary = await context.RNTabularTransform.execute(compiled, source, dictionaries, v.dataset, { ...hooks, onBatch: async () => {} });
    assert.equal(summary.recordCount, 130); assert.deepEqual(calls, [0, 64, 128]);
    const bad = context.RNTabularSources.records(compiled, v.snapshot.source, 1, async () => ({ offset: 1, records: [v.snapshot.records[0]], done: true }));
    await assert.rejects(context.RNTabularTransform.execute(compiled, bad, dictionaries, v.dataset, { ...hooks, onBatch: async () => {} }), error => error.code === "SOURCE_BATCH_INVALID");
    const unknown = context.RNTabularSources.records(compiled, v.snapshot.source, null, async (offset, limit) => {
        const size = Math.min(limit, 70 - offset);
        return { offset, done: offset + size === 70, records: Array.from({ length: size }, (_, i) => ({ ...v.snapshot.records[0], entity: { kind: "text", value: "plant-" + (offset + i) } })) };
    });
    const progress = [];
    const unknownSummary = await context.RNTabularTransform.execute(compiled, unknown, dictionaries, v.dataset, { ...hooks,
        onProgress: event => progress.push(event), onBatch: async () => {} });
    assert.equal(unknownSummary.recordCount, 70); assert.equal(unknownSummary.coverage.capturedItems, 70);
    assert.ok(progress.some(event => event.phase === "reading-records" && event.totalItems === null));
    const early = context.RNTabularSources.records(compiled, v.snapshot.source, 2, async () => ({ offset: 0, records: [v.snapshot.records[0]], done: true }));
    await assert.rejects(context.RNTabularTransform.execute(compiled, early, dictionaries, v.dataset, { ...hooks, onBatch: async () => {} }), error => error.code === "SOURCE_INCOMPLETE");
    const small = fixture("records-extraction"); small.template.source.maxRecords = 1;
    const smallPlan = context.RNTabularTemplate.compile(small.template);
    const extra = context.RNTabularSources.records(smallPlan, small.snapshot.source, null, async offset => ({ offset, records: [small.snapshot.records[0]], done: false }));
    await assert.rejects(context.RNTabularTransform.execute(smallPlan, extra, dictionaries, small.dataset, { ...hooks, onBatch: async () => {} }), error => error.code === "RECORD_LIMIT");
    console.log("PASS source boundary: bounded pull batches, exact page coverage, sink backpressure and cancellation before further input reads");

    const executor = context.RNTabularExecutor.create({ mode: "cooperative" }), input = JSON.stringify(fixture("records-extraction"));
    let writes = 0, completes = 0, aborts = 0;
    const sink = { writeBatch: async () => { writes++; }, complete: async summary => { assert.equal(summary.recordCount, 4); completes++; }, abort: () => { aborts++; writes = 0; } };
    await executor.extract(input, { sink }); assert.equal(completes, 1); assert.equal(aborts, 0); assert.ok(writes);
    await assert.rejects(executor.extract(input, { sink: { ...sink, complete: async () => { throw new Error("staging failed"); } } }), /staging failed/);
    assert.equal(aborts, 1); assert.equal(writes, 0);
    const stop = new AbortController();
    await assert.rejects(executor.extract(input, { signal: stop.signal, sink: { ...sink, writeBatch: async () => { stop.abort(); } } }), error => error.code === "EXECUTION_CANCELLED");
    assert.equal(completes, 1); assert.equal(aborts, 2);
    let enteredComplete;
    const inComplete = new Promise(resolve => { enteredComplete = resolve; }), cancellation = new AbortController();
    const completing = executor.extract(input, { signal: cancellation.signal, sink: { ...sink, complete: async () => { enteredComplete(); await new Promise(resolve => { release = resolve; }); } } });
    await inComplete; cancellation.abort(); await assert.rejects(completing, error => error.code === "EXECUTION_CANCELLED");
    release(); assert.equal(aborts, 3);
    executor.dispose();
    console.log("PASS result sink: awaited completion, failure/late cancellation revoke staging, no publication or completed summary on error");
})().catch(error => { console.error(error); process.exitCode = 1; });
