(function (root) {
    "use strict";
    const templates = root.RNTabularTemplate, expressions = root.RNTabularExpression;
    const geometry = root.RNTabularSelectors, fail = geometry.fail, freeze = Object.freeze;

    // The sink receives provisional batches with backpressure. Only a resolved summary completes a run.
    // This module neither persists batches nor activates a dataset/publication.
    async function execute(plan, reader, dictionaries, dataset, hooks) {
        if (!templates.owns(plan)) fail("PLAN_INVALID", "", "Execution requires a plan produced by this compiler.");
        if (typeof hooks.onBatch !== "function") fail("RESULT_SINK_REQUIRED", "", "Extraction requires a provisional batch consumer.");
        if (reader.sheet !== plan.sheet) fail("CAPTURE_SHEET_MISMATCH", "/template/sheet", "Template and capture sheets differ.");
        templates.fields(dataset, ["id", "dimensions"], "/dataset"); templates.id(dataset.id, "/dataset/id");
        const dimensions = templates.dimensions(dataset.dimensions, "/dataset/dimensions"), dimensionKeys = Object.keys(dimensions);
        const contexts = new Map(), rows = [], columns = [], readCells = new Set(), grain = new Set();
        let completed = 0, missing = 0, batch = [], batchCharacters = 0, sequence = 0, work = 0;
        const guard = () => { if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Extraction was cancelled."); };
        async function checkpoint(phase, force = false) {
            guard();
            if (force || ++work % 64 === 0) {
                hooks.onProgress({ phase, completedRecords: completed, totalRecords: plan.totalRecords });
                await hooks.yieldControl(); guard();
            }
        }
        const environment = { contexts, lookup: dictionaries.lookup, read: (row, column, rule) => {
            const bounds = plan.bounds, address = geometry.address(row, column);
            if (row < bounds.firstRow || row > bounds.lastRow || column < bounds.firstColumn || column > bounds.lastColumn)
                fail("DEPENDENCY_OUTSIDE_BOUNDS", rule, "Cell " + address + " exceeds template bounds.");
            const location = freeze({ kind: "cell", sheet: plan.sheet, row, column, address });
            let cell;
            try { cell = reader.read(location); }
            catch (error) { error.path = rule; error.location = location; throw error; }
            readCells.add(address);
            return freeze({ location, cell });
        } };
        await checkpoint("binding", true);
        for (const item of plan.contexts) {
            contexts.set(item.name, expressions.evaluate(item.expression, environment)); await checkpoint("binding");
        }
        for (const row of plan.rows) {
            const parameter = expressions.evaluate(plan.parameter, { ...environment, row });
            const unit = expressions.evaluate(plan.unit, { ...environment, row });
            if (parameter.value.unitId !== unit.value.id) fail("UNIT_MISMATCH", plan.unit.path, "Source unit differs from the parameter's canonical unit; implicit conversion is forbidden.");
            if (JSON.stringify(parameter.value.dimensions) !== JSON.stringify(dimensionKeys)) fail("GRAIN_MISMATCH", "/dataset/dimensions", "Dataset dimensions must match the parameter grain.");
            rows.push({ row, parameter, unit }); await checkpoint("binding");
        }
        for (const column of plan.columns) {
            columns.push({ ...column, resolvedPeriod: expressions.evaluate(column.period, { ...environment, col: column.column }) });
            await checkpoint("binding");
        }
        async function flush() {
            if (!batch.length) return;
            guard();
            await hooks.onBatch(freeze({ version: 1, sequence: ++sequence, records: freeze(batch) }));
            guard(); batch = []; batchCharacters = 0;
        }
        await checkpoint("extracting", true);
        for (const row of rows) for (const column of columns) {
            await checkpoint("extracting");
            const value = expressions.evaluate(plan.value, { ...environment, row: row.row, col: column.column });
            const parameter = row.parameter.value, period = column.resolvedPeriod.value;
            const key = JSON.stringify([parameter.id, dimensions, period, column.role]);
            if (grain.has(key)) fail("DUPLICATE_OBSERVATION", plan.value.path, "Multiple source cells produce the same parameter/dimensions/period/role.");
            grain.add(key);
            const explain = (expression, result) => freeze({ rule: expression.path, evidence: result.evidence, mappings: result.mappings });
            const record = freeze({ parameterId: parameter.id, dimensions, period, value: value.value,
                unitId: row.unit.value.id, role: column.role, temporalMeaning: parameter.temporalMeaning,
                provenance: freeze({ template: plan.template, block: column.group,
                    value: explain(plan.value, value), parameter: explain(plan.parameter, row.parameter),
                    unit: explain(plan.unit, row.unit), period: explain(column.period, column.resolvedPeriod) }) });
            const characters = JSON.stringify(record).length;
            if (characters > templates.limits.recordCharacters) fail("RESULT_LIMIT", plan.value.path, "One record with provenance exceeds 65536 characters.");
            if (batch.length && (batch.length >= templates.limits.batchRecords || batchCharacters + characters > templates.limits.batchCharacters)) await flush();
            batch.push(record); batchCharacters += characters; completed++;
            if (record.value.kind === "missing") missing++;
        }
        await flush(); await checkpoint("complete", true);
        return freeze({ version: 1, kind: "matrix-extraction-result", template: plan.template,
            dataset: freeze({ id: dataset.id, dimensions }), source: reader.source, dictionaryRevision: dictionaries.revision,
            recordCount: completed, missingValueCount: missing, batchCount: sequence,
            coverage: freeze({ complete: true, capturedCells: reader.count, readCells: readCells.size }) });
    }
    root.RNTabularMatrix = freeze({ execute });
}(globalThis));
