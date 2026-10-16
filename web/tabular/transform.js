(function (root) {
    "use strict";
    const templates = root.RNTabularTemplate, expressions = root.RNTabularExpression;
    const fail = root.RNTabularSelectors.fail, freeze = Object.freeze;

    // One semantic executor for all source shapes. Output is provisional until the sink completes.
    async function execute(plan, source, dictionaries, dataset, hooks) {
        if (!templates.owns(plan) || !root.RNTabularSources.owns(source) || source.plan !== plan)
            fail("PLAN_INVALID", "", "Execution requires a compiled plan and inputs prepared for that exact plan.");
        if (typeof hooks.onBatch !== "function") fail("RESULT_SINK_REQUIRED", "", "Extraction requires a provisional batch consumer.");
        templates.fields(dataset, ["id", "dimensions"], "/dataset"); templates.id(dataset.id, "/dataset/id");
        const external = templates.dimensions(dataset.dimensions, "/dataset/dimensions");
        const contexts = new Map(), readLocations = new Set(), grain = new Set();
        let completed = 0, missing = 0, batch = [], batchCharacters = 0, sequence = 0, work = 0;
        const guard = () => { if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "", "Extraction was cancelled."); };
        async function checkpoint(phase, force = false) {
            guard();
            if (force || ++work % 64 === 0) {
                hooks.onProgress({ phase, completedRecords: completed, totalRecords: source.totalRecords });
                await hooks.yieldControl(); guard();
            }
        }
        const environment = { contexts, lookup: dictionaries.lookup, read: (node, frame) => {
            const item = source.read(node, frame); readLocations.add(JSON.stringify(item.location)); return item;
        } };
        await checkpoint("binding", true);
        for (const item of plan.contexts) {
            contexts.set(item.name, expressions.evaluate(item.expression, environment)); await checkpoint("binding");
        }
        async function flush() {
            if (!batch.length) return;
            guard();
            await hooks.onBatch(freeze({ version: 1, sequence: ++sequence, records: freeze(batch) }));
            guard(); batch = []; batchCharacters = 0;
        }
        const contextRules = new Map(plan.contexts.map(item => [item.name, item.expression]));
        function explain(expression, result) {
            const dependencies = new Map();
            function collect(current) {
                for (const name of current.contexts) if (!dependencies.has(name)) {
                    const dependency = contextRules.get(name);
                    dependencies.set(name, freeze({ name, rule: dependency.path, expression: dependency.text })); collect(dependency);
                }
            }
            collect(expression);
            return freeze({ rule: expression.path, expression: expression.text, contexts: freeze(Array.from(dependencies.values())),
                evidence: result.evidence, mappings: result.mappings });
        }
        await checkpoint("extracting", true);
        for await (const frame of source.scan(hooks)) {
            await checkpoint("extracting");
            const bindings = plan.regions[frame.region], resolved = Object.create(null), lineage = Object.create(null);
            for (const key of Object.keys(bindings)) {
                resolved[key] = expressions.evaluate(bindings[key], { ...environment, ...frame });
                lineage[key] = explain(bindings[key], resolved[key]);
            }
            const parameter = resolved.parameter.value, unit = resolved.unit.value, period = resolved.period.value;
            const role = resolved.role.value, dims = { ...external }, dimensionEvidence = Object.create(null);
            for (const key of Object.keys(external)) dimensionEvidence[key] = freeze({ rule: "/dataset/dimensions/" + key, value: external[key] });
            for (const key of Object.keys(bindings).filter(key => key.startsWith("dimension:"))) {
                const name = key.slice(10);
                if (Object.prototype.hasOwnProperty.call(dims, name)) fail("BINDING_CONFLICT", bindings[key].path, "Dataset and template both bind dimension " + name + ".");
                dims[name] = resolved[key].value; dimensionEvidence[name] = lineage[key];
            }
            const dimensions = templates.dimensions(dims, "/record/dimensions");
            if (parameter.unitId !== unit.id) fail("UNIT_MISMATCH", bindings.unit.path, "Source unit differs from canonical unit; implicit conversion is forbidden.");
            if (JSON.stringify(parameter.dimensions) !== JSON.stringify(Object.keys(dimensions))) fail("GRAIN_MISMATCH", "/dataset/dimensions", "Dimensions must match the parameter grain.");
            if (!["observation", "reported_total"].includes(role)) fail("ROLE_INVALID", bindings.role.path, "Unsupported observation role.");
            const key = JSON.stringify([parameter.id, dimensions, period, role]);
            if (grain.has(key)) fail("DUPLICATE_OBSERVATION", bindings.value.path, "Repeated parameter/dimensions/period/role; no implicit deduplication or aggregation.");
            grain.add(key);
            const record = freeze({ parameterId: parameter.id, dimensions, period, value: resolved.value.value,
                unitId: unit.id, role, temporalMeaning: parameter.temporalMeaning,
                provenance: freeze({ template: plan.template, source: source.source, dictionaryRevision: dictionaries.revision,
                    block: frame.region, numeric: plan.numeric,
                    value: lineage.value, parameter: lineage.parameter, unit: lineage.unit, period: lineage.period,
                    role: lineage.role, dimensions: freeze(dimensionEvidence) }) });
            const characters = JSON.stringify(record).length;
            if (characters > templates.limits.recordCharacters) fail("RESULT_LIMIT", bindings.value.path, "Record with provenance exceeds 65536 characters.");
            if (batch.length && batchCharacters + characters > templates.limits.batchCharacters) await flush();
            batch.push(record); batchCharacters += characters; completed++;
            if (record.value.kind === "missing") missing++;
            // Flush before pulling another source page: a stalled sink never causes read-ahead.
            if (batch.length >= templates.limits.batchRecords) await flush();
        }
        await flush(); await checkpoint("validating", true);
        if (source.totalRecords !== null && completed !== source.totalRecords) fail("SOURCE_INCOMPLETE", "", "Prepared source coverage differs from execution.");
        return freeze({ version: 2, kind: "extraction-result", output: plan.output, template: plan.template,
            dataset: freeze({ id: dataset.id, dimensions: external }), source: source.source, dictionaryRevision: dictionaries.revision,
            recordCount: completed, missingValueCount: missing, batchCount: sequence,
            coverage: freeze({ complete: true, model: source.kind, capturedItems: source.count === null ? completed : source.count, readLocations: readLocations.size }) });
    }
    root.RNTabularTransform = freeze({ execute });
}(globalThis));
