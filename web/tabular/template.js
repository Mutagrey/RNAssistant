(function (root) {
    "use strict";
    const selectors = root.RNTabularSelectors, expressions = root.RNTabularExpression, fail = selectors.fail;
    const freeze = Object.freeze, plans = new WeakSet();
    const limits = freeze({ records: 10000, contexts: 32, groups: 32, parameters: 1000, mappings: 4000,
        batchRecords: 64, batchCharacters: 262144, recordCharacters: 65536 });
    function fields(value, names, path) {
        root.RNTabularSnapshot.object(value, names, path);
        if (names.some(name => !Object.prototype.hasOwnProperty.call(value, name))) fail("SCHEMA_INVALID", path, "Missing required field.");
    }
    function text(value, path, maximum = 128) {
        if (typeof value !== "string" || !value || value.length > maximum || /[\u0000-\u001f]/.test(value)) fail("SCHEMA_INVALID", path, "Expected bounded nonempty text.");
        return value;
    }
    function id(value, path) {
        text(value, path);
        if (!/^[A-Za-z][A-Za-z0-9_.-]*$/.test(value)) fail("SCHEMA_INVALID", path, "Expected a stable identifier.");
        return value;
    }
    function revision(value, path) {
        if (!Number.isSafeInteger(value) || value < 1) fail("SCHEMA_INVALID", path, "Expected a positive revision label.");
        return value;
    }
    function list(value, path, maximum) {
        if (!Array.isArray(value) || !value.length || value.length > maximum) fail("SCHEMA_INVALID", path, "Expected a bounded nonempty list.");
        return value;
    }
    function dimensions(value, path) {
        if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).length > 16) fail("SCHEMA_INVALID", path, "Expected at most 16 named dimensions.");
        const output = Object.create(null);
        Object.keys(value).sort().forEach(key => { id(key, path); output[key] = text(value[key], path + "/" + key); });
        return freeze(output);
    }

    function openDictionaries(input) {
        fields(input, ["version", "revision", "parameters", "units", "parameterMappings", "unitMappings"], "/dictionaries");
        if (input.version !== 1) fail("VERSION_UNSUPPORTED", "/dictionaries/version", "Unsupported dictionary format.");
        revision(input.revision, "/dictionaries/revision");
        const parameters = new Map(), units = new Map(), mappings = { parameters: new Map(), units: new Map() };
        for (const entry of list(input.units, "/dictionaries/units", limits.parameters)) {
            fields(entry, ["id", "name"], "/dictionaries/units"); id(entry.id, "/dictionaries/units/id"); text(entry.name, "/dictionaries/units/name");
            if (units.has(entry.id)) fail("DICTIONARY_CONFLICT", "/dictionaries/units", "Duplicate unit identifier.");
            units.set(entry.id, freeze({ id: entry.id, name: entry.name }));
        }
        for (const entry of list(input.parameters, "/dictionaries/parameters", limits.parameters)) {
            fields(entry, ["id", "name", "valueType", "unitId", "dimensions", "temporalMeaning"], "/dictionaries/parameters");
            id(entry.id, "/dictionaries/parameters/id"); text(entry.name, "/dictionaries/parameters/name");
            if (entry.valueType !== "decimal" || !units.has(entry.unitId) ||
                !["flow", "at_period_end", "cumulative", "unspecified"].includes(entry.temporalMeaning))
                fail("SCHEMA_INVALID", "/dictionaries/parameters", "This observation profile requires decimal parameters with an existing unit and explicit temporal meaning.");
            if (!Array.isArray(entry.dimensions) || entry.dimensions.length > 16) fail("SCHEMA_INVALID", "/dictionaries/parameters/dimensions", "Invalid dimension list.");
            entry.dimensions.forEach(name => id(name, "/dictionaries/parameters/dimensions"));
            if (new Set(entry.dimensions).size !== entry.dimensions.length || parameters.has(entry.id))
                fail("DICTIONARY_CONFLICT", "/dictionaries/parameters", "Duplicate parameter or dimension.");
            parameters.set(entry.id, freeze({ ...entry, dimensions: freeze(entry.dimensions.slice().sort()) }));
        }
        for (const [name, source, targets] of [["parameters", input.parameterMappings, parameters], ["units", input.unitMappings, units]]) {
            for (const entry of list(source, "/dictionaries/" + name + "Mappings", limits.mappings)) {
                fields(entry, ["scope", "source", "targetId"], "/dictionaries/mappings");
                id(entry.scope, "/dictionaries/mappings/scope"); text(entry.source, "/dictionaries/mappings/source", 256);
                const key = JSON.stringify([entry.scope, entry.source]);
                if (!targets.has(entry.targetId)) fail("REFERENCE_MISSING", "/dictionaries/mappings", "Unknown mapping target.");
                if (mappings[name].has(key)) fail("DICTIONARY_CONFLICT", "/dictionaries/mappings", "Ambiguous scoped mapping.");
                mappings[name].set(key, targets.get(entry.targetId));
            }
        }
        return freeze({ revision: input.revision, lookup: (name, source, scope, path) => {
            const entry = mappings[name].get(JSON.stringify([scope, source]));
            if (!entry) fail("MAPPING_MISSING", path, "No exact mapping in scope " + scope + " for " + source + ".");
            return entry;
        } });
    }

    function compile(input) {
        if (!input || input.language !== 2) fail("VERSION_UNSUPPORTED", "/template/language", "Expected language 2; the orientation-bound language 1 is not adapted.");
        fields(input, ["language", "id", "revision", "source", "context", "bindings", "output", "policies"], "/template");
        id(input.id, "/template/id"); revision(input.revision, "/template/revision");
        if (input.output !== "observations") fail("VERSION_UNSUPPORTED", "/template/output", "Only canonical observations are implemented.");
        fields(input.policies, ["missing", "mapping", "duplicates", "conflicts"], "/template/policies");
        if (input.policies.missing !== "preserve" || ["mapping", "duplicates", "conflicts"].some(key => input.policies[key] !== "error"))
            fail("POLICY_UNSUPPORTED", "/template/policies", "This profile preserves missing values and refuses unresolved mappings/duplicates/conflicts.");
        const model = input.source && input.source.kind;
        let bounds = null, source;
        if (model === "matrix") {
            fields(input.source, ["kind", "sheet", "bounds", "rows", "columns"], "/template/source");
            text(input.source.sheet, "/template/source/sheet"); bounds = selectors.rectangle(input.source.bounds);
            if (selectors.area(bounds) > selectors.maxCells) fail("SELECTOR_LIMIT", "/template/source/bounds", "Matrix bounds exceed 100000 cells.");
            source = freeze({ kind: model, sheet: input.source.sheet, bounds: input.source.bounds });
        } else if (model === "records" || model === "json") {
            fields(input.source, ["kind", "maxRecords", model === "records" ? "fields" : "collection"], "/template/source");
            if (!Number.isSafeInteger(input.source.maxRecords) || input.source.maxRecords < 1 || input.source.maxRecords > limits.records)
                fail("RECORD_LIMIT", "/template/source/maxRecords", "Expected an explicit 1..10000 record budget.");
            if (model === "records") {
                const names = list(input.source.fields, "/template/source/fields", 64).map(name => text(name, "/template/source/fields"));
                if (new Set(names).size !== names.length || names.some(name => ["__proto__", "constructor", "prototype"].includes(name)))
                    fail("SCHEMA_INVALID", "/template/source/fields", "Field names must be unique and non-reserved.");
                source = freeze({ kind: model, fields: freeze(names), maxRecords: input.source.maxRecords });
            } else {
                root.RNTabularSources.pointer(input.source.collection, "/template/source/collection");
                source = freeze({ kind: model, collection: input.source.collection, maxRecords: input.source.maxRecords });
            }
        } else fail("SOURCE_MODEL_UNSUPPORTED", "/template/source/kind", "Expected matrix, records or json.");
        if (!input.context || typeof input.context !== "object" || Array.isArray(input.context) || Object.keys(input.context).length > limits.contexts)
            fail("SCHEMA_INVALID", "/template/context", "Expected at most 32 context expressions.");
        const context = new Map(), visiting = new Set(), orderedContext = [], dependencies = [];
        function expression(value, path, scope, expected) {
            fields(value, ["expr"], path);
            const compiled = expressions.compile(value.expr, path + "/expr", { ...scope, model, fields: source.fields, context: resolveContext });
            if (expected && compiled.type !== expected) fail("EXPRESSION_TYPE", path, "Expected " + expected + ", got " + compiled.type + ".");
            for (const read of compiled.reads) {
                if (read.op === "cell" && (typeof read.column === "number" && (read.column < bounds.firstColumn || read.column > bounds.lastColumn) ||
                    typeof read.row === "number" && (read.row < bounds.firstRow || read.row > bounds.lastRow)))
                    fail("DEPENDENCY_OUTSIDE_BOUNDS", read.path, "A declared cell dependency exceeds template bounds.");
                dependencies.push(read);
            }
            return compiled;
        }
        function resolveContext(name) {
            if (context.has(name)) return context.get(name);
            if (!Object.prototype.hasOwnProperty.call(input.context, name)) fail("REFERENCE_MISSING", "/template/context/" + name, "Unknown context variable.");
            if (visiting.has(name)) fail("DEPENDENCY_CYCLE", "/template/context/" + name, "Cyclic context dependency.");
            if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) || name.length > 64) fail("SCHEMA_INVALID", "/template/context", "Invalid context name.");
            visiting.add(name);
            const compiled = expression(input.context[name], "/template/context/" + name, {});
            visiting.delete(name); context.set(name, compiled); orderedContext.push(freeze({ name, expression: compiled }));
            return compiled;
        }
        Object.keys(input.context).sort().forEach(resolveContext);
        const types = { parameter: "parameter", unit: "unit", period: "period", value: "value", role: "text" };
        function bindings(inputBindings, path, scope) {
            root.RNTabularSnapshot.object(inputBindings, [...Object.keys(types), "dimensions"], path);
            const output = Object.create(null);
            for (const key of Object.keys(types)) if (Object.prototype.hasOwnProperty.call(inputBindings, key)) output[key] = expression(inputBindings[key], path + "/" + key, scope, types[key]);
            const dims = inputBindings.dimensions === undefined ? {} : inputBindings.dimensions;
            if (!dims || typeof dims !== "object" || Array.isArray(dims) || Object.keys(dims).length > 16) fail("SCHEMA_INVALID", path + "/dimensions", "Expected bounded dimensions.");
            for (const key of Object.keys(dims).sort()) { id(key, path + "/dimensions"); output["dimension:" + key] = expression(dims[key], path + "/dimensions/" + key, scope, "text"); }
            return freeze(output);
        }
        const common = bindings(input.bindings, "/template/bindings", model === "matrix" ? { row: true, col: true } : { item: true });
        function axis(name, property, variable) {
            const entries = [], used = new Set(), groups = new Set();
            list(input.source[name], "/template/source/" + name, limits.groups).forEach((group, index) => {
                const path = "/template/source/" + name + "/" + index;
                fields(group, ["id", "select", "bindings"], path); id(group.id, path + "/id");
                if (groups.has(group.id)) fail("BINDING_CONFLICT", path, "Duplicate axis group.");
                groups.add(group.id);
                const bound = bindings(group.bindings, path + "/bindings", { [variable]: true });
                let positions;
                try { positions = new Set(selectors.select(group.select, source.bounds, source.sheet).map(cell => cell[property])); }
                catch (error) { error.path = path + "/select"; throw error; }
                if (!positions.size) fail("MATRIX_EMPTY", path, "An axis group must be nonempty.");
                for (const position of positions) {
                    if (used.has(position)) fail("AXIS_BINDING_CONFLICT", path, "Physical coordinate belongs to multiple groups.");
                    used.add(position); entries.push(freeze({ position, group: group.id, bindings: bound }));
                }
            });
            return freeze(entries.sort((a, b) => a.position - b.position));
        }
        function merge(parts) {
            const result = Object.create(null);
            for (const part of parts) for (const key of Object.keys(part)) {
                if (Object.prototype.hasOwnProperty.call(result, key)) fail("BINDING_CONFLICT", part[key].path, "Semantic role is bound more than once: " + key);
                result[key] = part[key];
            }
            for (const key of Object.keys(types)) if (!result[key]) fail("BINDING_MISSING", "/template/bindings", "Missing semantic role: " + key);
            return freeze(result);
        }
        const rows = model === "matrix" ? axis("rows", "row", "row") : freeze([]);
        const columns = model === "matrix" ? axis("columns", "column", "col") : freeze([]);
        const regions = Object.create(null);
        if (model === "matrix") {
            if (rows.length * columns.length > limits.records) fail("RECORD_LIMIT", "/template", "Matrix exceeds 10000 output records.");
            for (const row of rows) for (const column of columns) {
                const key = row.group + "/" + column.group;
                if (!regions[key]) regions[key] = merge([common, row.bindings, column.bindings]);
            }
        } else regions.items = merge([common]);
        const plan = freeze({ kind: "extraction-plan", version: 2, output: input.output,
            template: freeze({ id: input.id, revision: input.revision, language: 2 }), source, bounds,
            contexts: freeze(orderedContext), dependencies: freeze(dependencies), rows, columns, regions: freeze(regions),
            policies: freeze({ ...input.policies }), numeric: freeze({ digits: 38, fraction: 18, rounding: "explicit-only" }),
            totalRecords: model === "matrix" ? rows.length * columns.length : null });
        plans.add(plan); return plan;
    }
    root.RNTabularTemplate = freeze({ compile, owns: plan => plans.has(plan), openDictionaries, dimensions, fields, id, limits });
}(globalThis));
