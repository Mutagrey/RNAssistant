"use strict";

// Static inspection uses the shipped language compiler. No workbook or project code is loaded.
importScripts("selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "expression.js", "template.js");

self.onmessage = event => {
    try {
        const input = RNTabularJson.parse(event.data);
        RNTabularTemplate.fields(input, ["version", "kind", "template", "source", "dictionaries", "dataset"], "/");
        if (input.version !== 2 || input.kind !== "xlsx-extraction")
            RNTabularSelectors.fail("VERSION_UNSUPPORTED", "/", "Expected xlsx-extraction version 2.");
        RNTabularTemplate.fields(input.source, ["formulaPolicy"], "/source");
        if (input.source.formulaPolicy !== "stored-cache")
            RNTabularSelectors.fail("POLICY_UNSUPPORTED", "/source/formulaPolicy", "Only stored-cache formula policy is supported.");
        const plan = RNTabularTemplate.compile(input.template);
        if (plan.source.kind !== "matrix")
            RNTabularSelectors.fail("SOURCE_MODEL_MISMATCH", "/template/source", "XLSX requires a matrix source plan.");
        RNTabularTemplate.openDictionaries(input.dictionaries);
        RNTabularTemplate.fields(input.dataset, ["id", "dimensions"], "/dataset");
        RNTabularTemplate.id(input.dataset.id, "/dataset/id");
        RNTabularTemplate.dimensions(input.dataset.dimensions, "/dataset/dimensions");
        const unitNames = new Map(input.dictionaries.units.map(unit => [unit.id, unit.name]));
        const pointer = name => name.replace(/~/g, "~0").replace(/\//g, "~1");
        const rules = (bindings, base) => Object.entries(bindings).flatMap(([role, value]) => role === "dimensions"
            ? Object.entries(value).map(([name, binding]) => ({ role: "dimension:" + name, expression: binding.expr,
                path: base + "/dimensions/" + pointer(name) + "/expr" }))
            : [{ role, expression: value.expr, path: base + "/" + pointer(role) + "/expr" }]);
        const groups = axis => input.template.source[axis].map((group, index) => ({ id: group.id, select: group.select,
            rules: rules(group.bindings, "/template/source/" + axis + "/" + index + "/bindings") }));
        self.postMessage({ type: "result", inspection: {
            template: plan.template, dataset: input.dataset.id, sheet: plan.source.sheet, bounds: plan.source.bounds,
            records: plan.totalRecords, rows: groups("rows"), columns: groups("columns"),
            contexts: plan.contexts.map(item => ({ name: item.name, expression: input.template.context[item.name].expr,
                path: "/template/context/" + pointer(item.name) + "/expr" })),
            commonRules: rules(input.template.bindings, "/template/bindings"),
            parameters: input.dictionaries.parameters.map(item => ({ id: item.id, name: item.name, unitId: item.unitId,
                unitName: unitNames.get(item.unitId), dimensions: item.dimensions })),
            units: input.dictionaries.units.map(item => ({ id: item.id, name: item.name })),
            mappings: [
                ...input.dictionaries.parameterMappings.map((item, index) => ({ kind: "parameter", ...item,
                    path: "/dictionaries/parameterMappings/" + index })),
                ...input.dictionaries.unitMappings.map((item, index) => ({ kind: "unit", ...item,
                    path: "/dictionaries/unitMappings/" + index }))
            ]
        } });
    } catch (error) {
        self.postMessage({ type: "error", error: { code: error.code || "TEMPLATE_INVALID",
            path: error.path || "", message: error.message || "Template validation failed." } });
    }
};
