(function (root) {
    "use strict";
    const freeze = Object.freeze, geometry = root.RNTabularSelectors, snapshots = root.RNTabularSnapshot, fail = geometry.fail;
    const prepared = new WeakSet(), own = (value, key) => Object.prototype.hasOwnProperty.call(value, key);
    const limits = freeze({ batchItems: 64, batchCharacters: 262144 });
    function pointer(value, path) {
        if (typeof value !== "string" || value.length > 1024 || value && value[0] !== "/" || /~(?![01])/.test(value))
            fail("SOURCE_PATH_INVALID", path, "Expected a bounded JSON pointer, without wildcards or expressions.");
        const parts = value === "" ? [] : value.slice(1).split("/").map(part => part.replace(/~1/g, "/").replace(/~0/g, "~"));
        if (parts.length > 16 || parts.some(part => ["__proto__", "constructor", "prototype"].includes(part)))
            fail("SOURCE_PATH_INVALID", path, "Unsupported JSON pointer segment or depth.");
        return freeze(parts);
    }
    function resolve(value, parts, isLeaf = () => false) {
        for (const part of parts) {
            if (value === null || typeof value !== "object" || isLeaf(value) || !own(value, part) || Array.isArray(value) && !/^(0|[1-9]\d*)$/.test(part)) return undefined;
            value = value[part];
        }
        return value;
    }
    function metadata(value) {
        root.RNTabularTemplate.fields(value, ["kind", "label"], "/snapshot/source");
        if (!["fixture", "retained-snapshot"].includes(value.kind) || typeof value.label !== "string" || !value.label || value.label.length > 256)
            fail("SCHEMA_INVALID", "/snapshot/source", "Expected normalized source metadata, not native authority.");
        return freeze({ ...value });
    }
    function scalar(value, path) {
        if (value && ["null", "absent"].includes(value.kind)) {
            root.RNTabularTemplate.fields(value, ["kind"], path); return freeze({ kind: value.kind });
        }
        if (value && own(value, "formula")) fail("SCHEMA_INVALID", path, "Formula evidence belongs to matrix cells.");
        return snapshots.copyCell(value, path, 2);
    }
    function admit(result) { const value = freeze(result); prepared.add(value); return value; }
    function check(hooks) { if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Source reading was cancelled."); }
    function matrix(plan, reader) {
        if (!root.RNTabularTemplate.owns(plan) || plan.source.kind !== "matrix") fail("PLAN_INVALID", "", "Expected a compiled matrix plan.");
        if (reader.sheet !== plan.source.sheet) fail("CAPTURE_SHEET_MISMATCH", "/template/source/sheet", "Template and source sheets differ.");
        return admit({ plan, kind: "matrix", source: reader.source, count: reader.count, totalRecords: plan.totalRecords,
            read(node, frame) {
                const row = node.row === "row" ? frame.row : node.row, column = node.column === "col" ? frame.col : node.column;
                const box = plan.bounds, address = geometry.address(row, column);
                if (row < box.firstRow || row > box.lastRow || column < box.firstColumn || column > box.lastColumn)
                    fail("DEPENDENCY_OUTSIDE_BOUNDS", node.path, "Cell " + address + " exceeds template bounds.");
                const location = freeze({ kind: "cell", sheet: reader.sheet, row, column, address });
                try { return freeze({ location, cell: reader.read(location) }); }
                catch (error) { error.path = node.path; error.location = location; throw error; }
            },
            async *scan(hooks) {
                for (const row of plan.rows) for (const column of plan.columns) {
                    check(hooks); yield freeze({ row: row.position, col: column.position, region: row.group + "/" + column.group });
                }
            }
        });
    }
    // Pull only the next bounded page. The trusted adapter owns exact byte/source pins and stream disposal.
    function records(plan, source, count, readBatch) {
        if (!root.RNTabularTemplate.owns(plan) || plan.source.kind !== "records") fail("PLAN_INVALID", "", "Expected a compiled record plan.");
        if (count !== null && (!Number.isSafeInteger(count) || count < 0 || count > plan.source.maxRecords) || typeof readBatch !== "function")
            fail("SOURCE_LIMIT", "/snapshot/records", "Invalid bounded record source.");
        source = metadata(source);
        return admit({ plan, kind: "records", source, count, totalRecords: count,
            read(node, frame) {
                return freeze({ location: freeze({ kind: "field", record: frame.index + 1, field: node.key }),
                    cell: own(frame.record, node.key) ? frame.record[node.key] : freeze({ kind: "absent" }) });
            },
            async *scan(hooks) {
                let offset = 0;
                while (true) {
                    check(hooks);
                    // One EOF probe is allowed at the budget; any extra record fails before it is emitted.
                    const remaining = (count === null ? plan.source.maxRecords : count) - offset;
                    const size = Math.max(1, Math.min(limits.batchItems, remaining)), batch = await readBatch(offset, size, hooks);
                    check(hooks);
                    root.RNTabularTemplate.fields(batch, ["offset", "records", "done"], "/source/batch");
                    if (batch.offset !== offset || typeof batch.done !== "boolean" || !Array.isArray(batch.records) || batch.records.length > size ||
                        !batch.done && !batch.records.length || JSON.stringify(batch).length > limits.batchCharacters)
                        fail("SOURCE_BATCH_INVALID", "/source/batch", "Source page has wrong coverage/order or exceeds its budget.");
                    const received = batch.records.length;
                    if (offset + received > plan.source.maxRecords) fail("RECORD_LIMIT", "/source/batch", "Source exceeds the declared record budget.");
                    if (count !== null && (offset + received > count || batch.done && offset + received !== count))
                        fail("SOURCE_INCOMPLETE", "/source/batch", "Source EOF/coverage differs from its declared count.");
                    const copied = batch.records.map((record, index) => {
                        const path = "/source/records/" + (offset + index); snapshots.object(record, plan.source.fields, path);
                        const result = Object.create(null);
                        for (const key of Object.keys(record)) result[key] = scalar(record[key], path + "/" + key);
                        return freeze(result);
                    });
                    hooks.onProgress({ phase: "reading-records", completedItems: offset + received, totalItems: count });
                    for (let index = 0; index < received; index++) { check(hooks); yield freeze({ index: offset + index, record: copied[index], region: "items" }); }
                    offset += received;
                    if (batch.done) return;
                }
            }
        });
    }
    function open(plan, input) {
        if (!root.RNTabularTemplate.owns(plan)) fail("PLAN_INVALID", "", "Expected a compiled plan.");
        if (plan.source.kind === "matrix") return matrix(plan, snapshots.open(input));
        const kind = plan.source.kind, body = kind === "records" ? "records" : "document";
        root.RNTabularTemplate.fields(input, ["version", "kind", "source", body], "/snapshot");
        if (input.version !== 1 || input.kind !== kind) fail("SOURCE_MODEL_MISMATCH", "/snapshot", "Snapshot and plan source models differ.");
        const source = metadata(input.source);
        // Detached, bounded normalized capture. This is not a CSV/JSON byte reader.
        if (kind === "records") {
            const copy = root.RNTabularJson.parse(JSON.stringify(input.records));
            if (!Array.isArray(copy)) fail("SCHEMA_INVALID", "/snapshot/records", "Expected records.");
            return records(plan, source, copy.length, async (offset, size) => ({ offset, records: copy.slice(offset, offset + size), done: offset + size >= copy.length }));
        }
        let nodes = 0, characters = 0;
        const numericNodes = new WeakSet();
        function immutable(value, depth = 0) {
            if (++nodes > 200000 || depth > 32) fail("INPUT_LIMIT", "/snapshot/document", "JSON structure exceeds its budget.");
            const lexeme = root.RNTabularJson.numericLexeme(value);
            if (lexeme !== undefined) { const node = snapshots.copyCell({ kind: "numeric", lexeme }, "/snapshot/document", 2); numericNodes.add(node); return node; }
            if (typeof value === "string") {
                characters += value.length;
                if (characters > 1000000) fail("INPUT_LIMIT", "/snapshot/document", "JSON strings exceed the capture budget.");
                return value;
            }
            // Direct API callers may supply only exact safe integers; file lexemes come from the bounded JSON decoder.
            if (typeof value === "number" && !Number.isSafeInteger(value)) fail("JSON_PRECISION", "/snapshot/document", "Already-decoded JSON numbers may have lost precision; use the exact decoder.");
            if (value && typeof value === "object") {
                const result = Array.isArray(value) ? [] : Object.create(null);
                for (const key of Object.keys(value)) {
                    if (["__proto__", "constructor", "prototype"].includes(key)) fail("SCHEMA_INVALID", "/snapshot/document", "Reserved JSON key.");
                    result[key] = immutable(value[key], depth + 1);
                }
                return freeze(result);
            }
            return value;
        }
        const copy = immutable(input.document);
        const collection = resolve(copy, pointer(plan.source.collection, "/template/source/collection"));
        if (!Array.isArray(collection) || collection.length > plan.source.maxRecords) fail("SOURCE_LIMIT", "/snapshot/document", "JSON collection must be a bounded array.");
        return admit({ plan, kind, source, count: collection.length, totalRecords: collection.length,
            read(node, frame) {
                const value = resolve(collection[frame.index], pointer(node.key, node.path), value => numericNodes.has(value));
                const location = freeze({ kind: "json-pointer", pointer: plan.source.collection + "/" + frame.index + node.key });
                let cell;
                if (value === undefined) cell = freeze({ kind: "absent" });
                else if (value === null) cell = freeze({ kind: "null" });
                else if (numericNodes.has(value)) cell = value;
                else if (["string", "boolean", "number"].includes(typeof value))
                    cell = scalar({ kind: typeof value === "string" ? "text" : typeof value, value }, node.path);
                else { const error = new Error("A scalar JSON path resolved to a structure."); error.code = "SOURCE_TYPE"; error.path = node.path; error.location = location; throw error; }
                return freeze({ location, cell });
            },
            async *scan(hooks) { for (let index = 0; index < collection.length; index++) { check(hooks); yield freeze({ index, region: "items" }); } }
        });
    }
    root.RNTabularSources = freeze({ pointer, open, matrix, records, owns: value => prepared.has(value), limits });
}(globalThis));
