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
                fail("SCHEMA_INVALID", "/dictionaries/parameters", "This matrix slice requires decimal parameters with an existing unit and explicit temporal meaning.");
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
        fields(input, ["language", "kind", "id", "revision", "sheet", "bounds", "context", "rows", "columns", "value"], "/template");
        if (input.language !== 1 || input.kind !== "matrix") fail("VERSION_UNSUPPORTED", "/template", "Expected language 1 matrix template.");
        id(input.id, "/template/id"); revision(input.revision, "/template/revision"); text(input.sheet, "/template/sheet");
        const bounds = selectors.rectangle(input.bounds);
        if (selectors.area(bounds) > selectors.maxCells) fail("SELECTOR_LIMIT", "/template/bounds", "Matrix bounds exceed this slice's 100000-cell limit.");
        if (!input.context || typeof input.context !== "object" || Array.isArray(input.context) || Object.keys(input.context).length > limits.contexts)
            fail("SCHEMA_INVALID", "/template/context", "Expected at most 32 context expressions.");
        const context = new Map(), visiting = new Set(), orderedContext = [];
        function expression(value, path, scope, expected) {
            fields(value, ["expr"], path);
            const compiled = expressions.compile(value.expr, path + "/expr", { ...scope, context: resolveContext });
            if (expected && compiled.type !== expected) fail("EXPRESSION_TYPE", path, "Expected " + expected + ", got " + compiled.type + ".");
            for (const read of compiled.reads) if (typeof read.column === "number" && (read.column < bounds.firstColumn || read.column > bounds.lastColumn) ||
                typeof read.row === "number" && (read.row < bounds.firstRow || read.row > bounds.lastRow))
                fail("DEPENDENCY_OUTSIDE_BOUNDS", read.path, "A declared cell dependency exceeds template bounds.");
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
        fields(input.rows, ["select", "parameter", "unit"], "/template/rows");
        const axis = (select, property, path) => {
            try { return freeze(Array.from(new Set(selectors.select(select, input.bounds, input.sheet).map(cell => cell[property])))); }
            catch (error) { error.path = path; throw error; }
        };
        const rows = axis(input.rows.select, "row", "/template/rows/select");
        const parameter = expression(input.rows.parameter, "/template/rows/parameter", { row: true }, "parameter");
        const unit = expression(input.rows.unit, "/template/rows/unit", { row: true }, "unit");
        const columns = [], used = new Set(), groups = new Set();
        list(input.columns, "/template/columns", limits.groups).forEach((group, index) => {
            const path = "/template/columns/" + index;
            fields(group, ["id", "select", "period", "role"], path); id(group.id, path + "/id");
            if (groups.has(group.id) || !["observation", "reported_total"].includes(group.role)) fail("SCHEMA_INVALID", path, "Duplicate group or unsupported role.");
            groups.add(group.id);
            const period = expression(group.period, path + "/period", { col: true }, "period");
            for (const column of axis(group.select, "column", path + "/select")) {
                if (used.has(column)) fail("COLUMN_BINDING_CONFLICT", path, "A column belongs to multiple semantic groups.");
                used.add(column); columns.push(freeze({ column, group: group.id, role: group.role, period }));
            }
        });
        columns.sort((a, b) => a.column - b.column);
        if (!rows.length || !columns.length) fail("MATRIX_EMPTY", "/template", "Matrix axes must be nonempty.");
        if (rows.length * columns.length > limits.records) fail("RECORD_LIMIT", "/template", "Matrix exceeds 10000 output records.");
        const value = expression(input.value, "/template/value", { row: true, col: true }, "value");
        const plan = freeze({ kind: "matrix-plan", version: 1, template: freeze({ id: input.id, revision: input.revision }),
            sheet: input.sheet, bounds, contexts: freeze(orderedContext), rows, columns: freeze(columns), parameter, unit, value,
            totalRecords: rows.length * columns.length });
        plans.add(plan); return plan;
    }
    root.RNTabularTemplate = freeze({ compile, owns: plan => plans.has(plan), openDictionaries, dimensions, fields, id, limits });
}(globalThis));
