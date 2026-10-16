"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const { Blob, File } = require("node:buffer"), { parts, zip, source } = require("./tabular-xlsx-fixture");
const folder = path.resolve(__dirname, "../../web/tabular"), context = vm.createContext({ Blob, AbortController, TextDecoder, ReadableStream, DecompressionStream });
vm.runInContext(fs.readFileSync(path.join(folder, "../js/vendor/sax.js"), "utf8"), context);
for (const name of ["selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "byte-source.js", "expression.js", "template.js", "transform.js", "engine.js", "xlsx-zip.js", "xlsx-reader.js"])
    vm.runInContext(fs.readFileSync(path.join(folder, name), "utf8"), context, { filename: name });
const json = value => JSON.parse(JSON.stringify(value));
const hooks = { isCancelled: () => false, yieldControl: async () => {}, onProgress: () => {} };
const config = { sheet: "Данные", bounds: "A1:AG20", formulaPolicy: "stored-cache" };
const request = fs.readFileSync(path.join(folder, "examples/matrix-xlsx-extraction.json"), "utf8");
async function withSource(blob, action) {
    const byteSource = context.RNTabularByteSource.fromBlob(blob);
    try { return await action(byteSource); } finally { byteSource.dispose(); }
}
const read = (bytes, extra = {}) => withSource(new File([bytes], "fixture.xlsx"), byteSource => context.RNTabularXlsxReader.open(byteSource, config, { ...hooks, ...extra }));
const at = (reader, address) => { const point = context.RNTabularSelectors.cell(address); return json(reader.read({ ...point, address, sheet: config.sheet })); };
async function refuses(change, code, options) {
    const entries = parts(); change(entries);
    await assert.rejects(read(zip(entries, options)), error => error.code === code, code);
}
(async () => {
    const reader = await read(fs.readFileSync(source));
    assert.deepEqual(at(reader, "A12"), { kind: "text", value: "0012" });
    assert.deepEqual(at(reader, "A13"), { kind: "text", value: "0099" });
    assert.deepEqual(at(reader, "C12"), { kind: "numeric", lexeme: "10" });
    assert.deepEqual(at(reader, "J12"), { kind: "blank" });
    assert.deepEqual(at(reader, "K13"), { kind: "text", value: "" });
    assert.deepEqual(at(reader, "D13"), { kind: "error", value: "#N/A" });
    assert.deepEqual(at(reader, "J13"), { kind: "unavailable", reason: "formula-cache-absent", formula: { text: "SUM(C13:D13)", type: "normal" } });
    assert.deepEqual(at(reader, "AD12"), { kind: "numeric", lexeme: "2000", formula: { text: "SUM(R12:AC12)", type: "normal" } });
    assert.deepEqual(at(reader, "A14"), { kind: "boolean", value: true });
    assert.deepEqual(at(reader, "B14"), { kind: "text", value: " & _x0041_ " });
    assert.equal(at(reader, "C14").value, "");
    assert.equal(reader.diagnostics.missingCaches, 1); assert.equal(reader.diagnostics.formulaCells, 3);
    assert.equal(reader.count, 660); assert.equal(reader.capabilities.cacheFreshness, "unknown");
    assert.throws(() => at(reader, "AH1"), error => error.code === "CAPTURE_INCOMPLETE");
    assert.throws(() => reader.read({ sheet: "Other", row: 1, column: 1, address: "A1" }), error => error.code === "CAPTURE_SHEET_MISMATCH");
    const records = [], progress = [];
    const summary = await withSource(new File([fs.readFileSync(source)], "matrix.xlsx"), byteSource => context.RNTabularEngine.extractXlsx(request, { ...hooks, byteSource,
        onBatch: async batch => records.push(...json(batch.records)), onProgress: item => progress.push(json(item)) }));
    assert.equal(summary.recordCount, 16); assert.equal(summary.missingValueCount, 4); assert.equal(summary.source.kind, "xlsx-file");
    assert.deepEqual(records.map(item => item.value), [
        { kind: "decimal", value: "10" }, { kind: "decimal", value: "0" }, { kind: "missing", reason: "blank" }, { kind: "decimal", value: "1234.5" },
        { kind: "decimal", value: "1000" }, { kind: "decimal", value: "20" }, { kind: "decimal", value: "2000" }, { kind: "decimal", value: "3000" },
        { kind: "decimal", value: "0.125" }, { kind: "missing", reason: "source-error", detail: "#N/A" },
        { kind: "missing", reason: "unavailable", detail: "formula-cache-absent" }, { kind: "missing", reason: "empty-text" },
        { kind: "decimal", value: "1.25" }, { kind: "decimal", value: "2" }, { kind: "decimal", value: "3" }, { kind: "decimal", value: "4" }
    ]);
    assert.equal(records[2].provenance.value.evidence[0].location.address, "J12");
    assert.deepEqual(records[2].provenance.period.evidence.map(item => item.location.address), ["P5", "J5"]);
    assert.equal(records[2].provenance.period.contexts[0].expression, 'parse.year(text(cell("P", 5)))');
    assert.equal(records[2].provenance.parameter.mappings[0].targetId, "production.volume");
    assert.equal(records[2].provenance.unit.evidence[0].location.address, "B12");
    assert.deepEqual(records[6].provenance.value.evidence[0].cell.formula, { text: "SUM(R12:AC12)", type: "normal" });
    assert.ok(progress.some(item => item.part === "xl/worksheets/sheet1.xml" && item.partBytes > 0));
    assert.ok(Object.isFrozen(reader));
    console.log("PASS XLSX: real ZIP/OOXML, exact cell types, shared/inline/rich text, blank coverage, cached formulas and canonical oracle");

    const sheet = "xl/worksheets/sheet1.xml";
    for (const [lexeme, expected] of [["12345678901234567890.123456789012345678", "12345678901234567890.123456789012345678"], ["+.5e+2", "50"], ["-0.000E-2", "0"], ["1.234e-3", "0.001234"]]) {
        const values = parts(); values.set(sheet, values.get(sheet).replace('<v>10</v>', '<v>' + lexeme + '</v>'));
        const output = [];
        await withSource(new Blob([zip(values)]), byteSource => context.RNTabularEngine.extractXlsx(request, { ...hooks, byteSource, onBatch: async batch => output.push(...json(batch.records)) }));
        assert.equal(output[0].value.value, expected); assert.equal(output[0].provenance.value.evidence[0].cell.lexeme, lexeme);
    }
    const v2 = JSON.parse(fs.readFileSync(path.join(folder, "examples/disjoint-capture.json"), "utf8"));
    v2.snapshot.version = 2; v2.snapshot.ranges[0].cells[0][0] = { kind: "numeric", lexeme: "9007199254740993" };
    assert.equal(context.RNTabularSnapshot.open(v2.snapshot).read({ sheet: v2.snapshot.sheet, address: "C5" }).lexeme, "9007199254740993");
    v2.snapshot.version = 1;
    assert.throws(() => context.RNTabularSnapshot.open(v2.snapshot), error => error.code === "SCHEMA_INVALID");
    console.log("PASS XLSX numeric boundary: raw numeric lexemes, exact 38-digit values/exponents and versioned normalized cells");

    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12">', '<c r="C12" r="D12">')), "XLSX_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12">', '<c r="C12"><v>1</v>')), "XLSX_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="D12">', '<c r="C12">')), "XLSX_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<worksheet ', '<!DOCTYPE worksheet [<!ENTITY boom "bad">]><worksheet ')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set(sheet, p.get(sheet).replace('</worksheet>', '')), "XLSX_XML_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<v>10</v>', '<v>NaN</v>')), "SCHEMA_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12">', '<c r="C12" t="d">')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12">', '<c r="C12" vm="1">')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12"><v>10</v></c>', '<x:c xmlns:x="urn:other" r="C12"><x:v>10</x:v></x:c>')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="C12"><v>10</v></c>', '<c r="C12"><v><x>10</x></v></c>')), "XLSX_INVALID");
    await refuses(p => p.set(sheet, p.get(sheet).replace('<c r="A12" t="s"><v>0', '<c r="A12" t="s"><v>999')), "XLSX_INVALID");
    await refuses(p => p.delete("xl/sharedStrings.xml"), "XLSX_PART_MISSING");
    await refuses(p => p.set("xl/_rels/workbook.xml.rels", p.get("xl/_rels/workbook.xml.rels").replace('Target="worksheets/sheet1.xml"', 'Target="https://example.invalid/source.xml" TargetMode="External"')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set("_rels/.rels", p.get("_rels/.rels").replace('Target="xl/workbook.xml"', 'Target="../xl/workbook.xml"')), "XLSX_INVALID");
    await refuses(p => p.set("[Content_Types].xml", p.get("[Content_Types].xml").replace('spreadsheetml.sheet.main+xml', 'spreadsheetml.template.main+xml')), "XLSX_UNSUPPORTED");
    await refuses(p => p.set("xl/workbook.xml", p.get("xl/workbook.xml").replace('name="Данные"', 'name="Another"')), "XLSX_SHEET_MISSING");
    console.log("PASS XLSX refusals: malformed XML/cells, duplicates, missing parts/strings, unsupported types and external/escaping relationships");

    for (const options of [{ stored: true }, { descriptor: true }]) assert.equal((await read(zip(parts(), options))).count, 660);
    await refuses(p => p.set("xl/WORKBOOK.xml", p.get("xl/workbook.xml")), "XLSX_ZIP_INVALID");
    await refuses(p => p.set("../bad", "bad"), "XLSX_ZIP_INVALID");
    await refuses(() => {}, "XLSX_ZIP_UNSUPPORTED", { mutate({ index, entry }) { if (!index) entry.writeUInt16LE(1, 8); } });
    await refuses(() => {}, "XLSX_ZIP_LIMIT", { mutate({ index, entry }) { if (!index) entry.writeUInt32LE(300 * 1048576, 24); } });
    await refuses(() => {}, "XLSX_ZIP_INVALID", { mutate({ index, header, entry }) { if (!index) { header.writeUInt32LE(0, 14); entry.writeUInt32LE(0, 16); } } });
    await refuses(() => {}, "XLSX_PART_LIMIT", { mutate({ index, header, entry }) { if (!index) { header.writeUInt32LE(1, 22); entry.writeUInt32LE(1, 24); } } });
    await assert.rejects(read(fs.readFileSync(source).subarray(0, 100)), error => error.code === "XLSX_ZIP_INVALID");
    let cancelled = false;
    await assert.rejects(read(fs.readFileSync(source), { isCancelled: () => cancelled, onProgress: progress => { if (progress.part === sheet) cancelled = true; } }), error => error.code === "EXECUTION_CANCELLED");
    console.log("PASS XLSX resource boundary: stored/deflated/described ZIP, CRC, actual/declared size budgets, invalid paths and cancellation");

    const extended = parts(), rows = Array.from({ length: 30000 }, (_, i) => '<row r="' + (i + 21) + '"><c r="A' + (i + 21) + '"><v>' + i + '</v></c></row>').join("");
    extended.set(sheet, extended.get(sheet).replace('</sheetData>', rows + '</sheetData>'));
    // A substantial unselected member proves selective IO rather than just bounded retention after a full read.
    extended.set("media/unselected.bin", require("node:crypto").randomBytes(2 * 1048576));
    const bytes = zip(extended), slices = [], events = [];
    class SlicedFile extends File {
        arrayBuffer() { throw new Error("whole source read"); }
        text() { throw new Error("whole source text"); }
        stream() { throw new Error("whole source stream"); }
        slice(start, end) { slices.push(end - start); return super.slice(start, end); }
    }
    const bounded = await withSource(new SlicedFile([bytes], "many-rows.xlsx"), byteSource => context.RNTabularXlsxReader.open(byteSource, config, { ...hooks, onProgress: item => events.push(item) }));
    assert.equal(bounded.count, 660); assert.equal(bounded.diagnostics.scannedCells, 30030);
    assert.equal(bounded.diagnostics.storedCells, 30); assert.ok(bounded.diagnostics.sourceBytesRead < bytes.length / 2);
    assert.ok(Math.max(...slices) <= 65557); assert.ok(events.filter(item => item.part === sheet && item.partBytes > 0).length > 10);
    assert.deepEqual(at(bounded, "C12"), { kind: "numeric", lexeme: "10" });
    console.log("PASS XLSX bounded IO: 30000 unselected rows scanned incrementally, unrelated ZIP member unread, fixed capture and no whole-File reads");

    const manyParts = parts();
    for (let i = 0; i < 1000; i++) manyParts.set("unused/" + i + "-" + "x".repeat(300), "");
    const largeDirectory = zip(manyParts), requests = [];
    const pulled = context.RNTabularByteSource.create({ byteLength: largeDirectory.length, label: "Large directory.xlsx",
        readRange: (offset, count) => { requests.push(count); return largeDirectory.subarray(offset, offset + count); }, close() {} });
    try {
        const reader = await context.RNTabularXlsxReader.open(pulled, config, hooks);
        assert.deepEqual(at(reader, "C12"), { kind: "numeric", lexeme: "10" });
        assert.equal(Math.max(...requests), 262144, "large directory is segmented at the byte source budget");
    } finally { pulled.dispose(); }
    console.log("PASS XLSX pull boundary: directory above 256 KiB uses bounded sequential range requests");

    // Independently produced OOXML exercises ordinary library output, not only handcrafted fixtures.
    const carrier = fs.readFileSync(path.join(folder, "../js/vendor/sheetjs.source.js"), "utf8");
    const vendor = vm.createContext({}); let sheetjsSource;
    vm.runInNewContext(carrier, { window: { RNAssistantHtmlVendorRuntime: { register: (_, value) => { sheetjsSource = value.js; } } } });
    vm.runInContext(sheetjsSource, vendor);
    const book = vendor.XLSX.read(new Uint8Array(fs.readFileSync(source)), { type: "array", cellFormula: true });
    const produced = vendor.XLSX.write(book, { type: "array", bookType: "xlsx", compression: true, bookSST: true });
    const roundtrip = await read(new Uint8Array(produced));
    assert.deepEqual(at(roundtrip, "A12"), { kind: "text", value: "0012" });
    assert.deepEqual(at(roundtrip, "C12"), { kind: "numeric", lexeme: "10" });
    assert.deepEqual(at(roundtrip, "AD12"), { kind: "numeric", lexeme: "2000", formula: { text: "SUM(R12:AC12)", type: "normal" } });
    console.log("PASS XLSX producer interoperability: existing pinned SheetJS writer, shared strings, package relationships and formula cache");
})().catch(error => { console.error(error); process.exitCode = 1; });
