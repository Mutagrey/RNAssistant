"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const base = path.resolve(__dirname, "../core"), scope = vm.createContext({ TextEncoder });
for (const name of ["selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "expression.js", "template.js", "template-draft.js", "project-template.js"])
  vm.runInContext(fs.readFileSync(path.join(base, name), "utf8"), scope, { filename: name });
const edits = scope.RNTabularTemplateDraft, language = scope.RNTabularTemplate;
const example = fs.readFileSync(path.join(base, "examples/matrix-xlsx-extraction.json"), "utf8");
const inspect = text => {
  const dict = JSON.parse(text).dictionaries;
  language.openDictionaries(dict);
  return { parameters: dict.parameters.map((item, index) => ({ ...item, path: "/dictionaries/parameters/" + index })),
    units: dict.units.map((item, index) => ({ ...item, path: "/dictionaries/units/" + index })),
    mappings: [
      ...dict.parameterMappings.map((item, index) => ({ ...item, kind: "parameter", path: "/dictionaries/parameterMappings/" + index })),
      ...dict.unitMappings.map((item, index) => ({ ...item, kind: "unit", path: "/dictionaries/unitMappings/" + index }))
    ] };
};
let text = example;
assert.throws(() => edits.removeParameter(text, inspect(text), "/dictionaries/parameters/0"),
  error => error.code === "DRAFT_INVALID" && /соответствия/.test(error.message), "referenced parameter is protected");
text = edits.upsertUnit(text, inspect(text), { id: "kg", name: "Килограммы" });
text = edits.upsertParameter(text, inspect(text), { id: "production.mass", name: "Масса", unitId: "kg",
  dimensions: ["entity", "variant"], temporalMeaning: "flow" });
assert.equal(inspect(text).parameters.length, 3);
const mass = inspect(text).parameters.find(item => item.id === "production.mass");
text = edits.upsertParameter(text, inspect(text), { ...mass, name: "Масса продукции" });
assert.equal(inspect(text).parameters.find(item => item.id === "production.mass").name, "Масса продукции");
assert.throws(() => edits.upsertParameter(text, inspect(text), { ...mass, id: "renamed" }),
  error => error.code === "DRAFT_INVALID", "stable IDs cannot be renamed in place");
text = edits.removeParameter(text, inspect(text), mass.path);
text = edits.removeUnit(text, inspect(text), inspect(text).units.find(item => item.id === "kg").path);
assert.equal(inspect(text).parameters.length, 2);
assert.equal(inspect(text).units.length, 1);
for (const kind of ["parameter", "unit"]) {
  while (inspect(text).mappings.some(item => item.kind === kind)) {
    const item = inspect(text).mappings.find(value => value.kind === kind);
    text = edits.removeMapping(text, inspect(text), item.path);
  }
}
assert.equal(JSON.parse(text).dictionaries.parameterMappings.length, 0);
assert.equal(JSON.parse(text).dictionaries.unitMappings.length, 0);
language.openDictionaries(JSON.parse(text).dictionaries);
const projectTemplates = scope.DataTemplatesProjectTemplate, baseline = JSON.parse(example).dictionaries;
const extended = JSON.parse(JSON.stringify(baseline));
extended.units.push({ id: "kg", name: "Килограммы" });
extended.parameters.push({ id: "production.mass", name: "Масса", valueType: "decimal",
  unitId: "kg", dimensions: ["entity"], temporalMeaning: "flow" });
const merged = projectTemplates.merge(baseline, extended);
assert.equal(merged.revision, baseline.revision + 1);
assert.equal(merged.parameters.length, baseline.parameters.length + 1);
assert.equal(projectTemplates.merge(merged, extended).revision, merged.revision, "idempotent import keeps revision");
const conflict = JSON.parse(JSON.stringify(extended)); conflict.units.at(-1).name = "Другая единица";
assert.throws(() => projectTemplates.merge(merged, conflict), error => error.code === "DICTIONARY_CONFLICT");
const projectId = "tp_" + "a".repeat(32), thin = projectTemplates.detach(example, projectId);
assert.equal(Object.hasOwn(JSON.parse(thin), "dictionaries"), false);
assert.equal(JSON.parse(projectTemplates.materialize(thin, merged, projectId)).dictionaries.revision, merged.revision);
assert.throws(() => projectTemplates.materialize(thin, merged, "tp_" + "b".repeat(32)),
  error => error.code === "TEMPLATE_FORMAT", "template is bound to its data project");
console.log("PASS dictionary editor: parameter/unit CRUD guards, mapping removal and empty mapping lists");
