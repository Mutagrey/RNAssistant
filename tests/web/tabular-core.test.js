"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const root = path.resolve(__dirname, "../../web/tabular");
const context = vm.createContext({});
for (const file of ["selectors.js", "snapshot.js", "json.js", "expression.js", "template.js", "matrix.js", "engine.js"])
    vm.runInContext(fs.readFileSync(path.join(root, file), "utf8"), context, { filename: file });
const selectors = context.RNTabularSelectors, snapshots = context.RNTabularSnapshot, engine = context.RNTabularEngine;
const json = value => JSON.parse(JSON.stringify(value));
const hooks = { isCancelled: () => false, yieldControl: async () => {}, onProgress: () => {} };
const fixtureText = fs.readFileSync(path.join(root, "examples/disjoint-capture.json"), "utf8");
const request = () => JSON.parse(fixtureText);
const addresses = selection => Array.from(selection, value => value.address);
const rejects = (text, code) => assert.rejects(engine.inspect(text, hooks), error => error.code === code);

(async () => {
    assert.deepEqual(addresses(selectors.select("A8:A*", "A1:C10", "Данные")), ["A8", "A9", "A10"]);
    assert.deepEqual(addresses(selectors.select("(C:H | J:O) \\ (D:F | L:N)", "A12:P12", "Данные")),
        ["C12", "G12", "H12", "J12", "K12", "O12"]);
    assert.deepEqual(addresses(selectors.select("'O''Brien | (2026)'!$B$2 | A1:B2 | B2", "A1:C3", "O'Brien | (2026)")),
        ["A1", "B1", "A2", "B2"]);
    assert.deepEqual(addresses(selectors.select("3 | 1:2", "A1:B3", "Data")), ["A1", "B1", "A2", "B2", "A3", "B3"]);
    assert.deepEqual(addresses(selectors.select("A1:B2 \\ (A1:B2)", "A1:B2", "Data")), []);
    assert.deepEqual(addresses(selectors.select("XFD1048576", "XFD1048576", "Data")), ["XFD1048576"]);
    for (const [selection, bounds, code] of [
        ["A0", "A1:C10", "SELECTOR_INVALID"], ["XFE1", "A1:C10", "SELECTOR_INVALID"],
        ["A1048577", "A1:C10", "SELECTOR_INVALID"], ["B2:A1", "A1:C10", "SELECTOR_INVALID"],
        ["C:A", "A1:C10", "SELECTOR_OUTSIDE_BOUNDS"], ["A1:D1", "A1:C10", "SELECTOR_OUTSIDE_BOUNDS"],
        ["A8:A*", "A1:A1048576", "SELECTOR_LIMIT"], ["Other!A1", "A1:C10", "SELECTOR_SHEET_MISMATCH"],
        ["A1 |", "A1:C10", "SELECTOR_INVALID"], ["A1:", "A1:C10", "SELECTOR_INVALID"],
        ["(A1", "A1:C10", "SELECTOR_INVALID"],
        ["A1)", "A1:C10", "SELECTOR_INVALID"], ["*", "A1:C10", "SELECTOR_INVALID"],
        ["'Data!A1", "A1:C10", "SELECTOR_INVALID"],
        ["(".repeat(17) + "A1" + ")".repeat(17), "A1:C10", "SELECTOR_LIMIT"],
        [Array(65).fill("A1").join("|"), "A1:C10", "SELECTOR_LIMIT"],
        ["A1:A100000 | A1:A100000 | A1:A100000", "A1:A100000", "SELECTOR_LIMIT"]
    ]) assert.throws(() => selectors.select(selection, bounds, "Data"), error => error.code === code, selection);
    assert.throws(() => selectors.select("A1:B2", "A1:B2", "Data", 3), error => error.code === "SELECTOR_LIMIT");
    console.log("PASS tabular selectors: bounded masks, set precedence, physical order, quotes and resource budgets");

    const result = json(await engine.inspect(fixtureText, hooks));
    // Independent oracle: hand-entered addresses and source values, not engine-generated expectations.
    assert.deepEqual(result.cells.map(item => [item.address, item.row, item.column, item.cell]), [
        ["C5", 5, 3, { kind: "text", value: "янв" }], ["D5", 5, 4, { kind: "text", value: "фев" }],
        ["J5", 5, 10, { kind: "text", value: "июл" }], ["K5", 5, 11, { kind: "text", value: "авг" }],
        ["P5", 5, 16, { kind: "number", value: 2026 }], ["C12", 12, 3, { kind: "number", value: 10 }],
        ["D12", 12, 4, { kind: "number", value: 0 }], ["J12", 12, 10, { kind: "blank" }],
        ["K12", 12, 11, { kind: "text", value: "0012" }], ["P12", 12, 16, { kind: "number", value: 1000 }]
    ]);
    assert.deepEqual(result.counts, { number: 4, text: 5, boolean: 0, blank: 1, error: 0, unavailable: 0 });
    assert.equal(result.coverage.complete, true);
    assert.equal(result.coverage.returnedCells, 10);
    const input = request();
    const reader = snapshots.open(input.snapshot);
    input.snapshot.sheet = "Foreign";
    input.snapshot.ranges[0].cells[0][0].value = "changed";
    input.snapshot.source.label = "changed";
    assert.equal(reader.read({ sheet: "Данные", address: "C5" }).value, "янв");
    assert.notEqual(reader.source.label, "changed");
    assert.throws(() => reader.read({ sheet: "Foreign", address: "C5" }), error => error.code === "CAPTURE_SHEET_MISMATCH");
    assert.throws(() => reader.read({ sheet: "Данные", address: "I12" }), error => error.code === "CAPTURE_INCOMPLETE");
    for (const cell of [
        { kind: "text", value: "" }, { kind: "number", value: 0 }, { kind: "blank" },
        { kind: "boolean", value: false }, { kind: "error", value: "#N/A" },
        { kind: "unavailable", reason: "formula-cache-absent" }
    ]) {
        const value = request(); value.snapshot.ranges[0].cells[0][0] = cell;
        assert.deepEqual(json((await engine.inspect(JSON.stringify(value), hooks)).cells[0].cell), cell);
    }
    console.log("PASS tabular snapshot: disjoint coordinates, immutable values, coverage and distinct missing states");

    for (const [change, code] of [
        [value => { value.version = 2; }, "VERSION_UNSUPPORTED"],
        [value => { value.extra = true; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.ranges[0].extra = true; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.source.kind = "live"; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.ranges[0].cells[0][0] = { kind: "blank", value: null }; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.ranges[0].cells[0][0] = { kind: "number", value: null }; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.ranges[0].cells[0][0] = { kind: "unavailable" }; }, "SCHEMA_INVALID"],
        [value => { value.snapshot.ranges[0].cells.pop(); }, "CAPTURE_SHAPE_INVALID"],
        [value => { value.snapshot.ranges[0].cells[0].pop(); }, "CAPTURE_SHAPE_INVALID"],
        [value => { value.snapshot.ranges.push(value.snapshot.ranges[0]); }, "CAPTURE_OVERLAP"],
        [value => { value.selection += " | I12"; }, "CAPTURE_INCOMPLETE"],
        [value => { value.maxCells = 0; }, "SELECTOR_LIMIT"],
        [value => { value.maxCells = null; }, "SELECTOR_LIMIT"]
    ]) {
        const value = request(); change(value);
        await rejects(JSON.stringify(value), code);
    }
    await rejects("{", "JSON_INVALID");
    await rejects(" ".repeat(engine.maxInputCharacters + 1), "INPUT_LIMIT");
    await rejects(fixtureText.replace('"version": 1', '"__proto__": {}, "version": 1'), "SCHEMA_INVALID");
    let cancelled = false;
    await assert.rejects(engine.inspect(fixtureText, { ...hooks, isCancelled: () => cancelled,
        yieldControl: async () => { cancelled = true; } }), error => error.code === "EXECUTION_CANCELLED");
    assert.equal((await engine.inspect(fixtureText, hooks)).cells.length, 10, "cancelled work leaves no shared run state");
    console.log("PASS tabular engine: closed schema, explicit errors and cancellation without partial success");
})().catch(error => { console.error(error); process.exitCode = 1; });
