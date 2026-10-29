"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-skills.js"), "utf8");
const chatStateSource = fs.readFileSync(path.join(root, "web/js/app-chat-state.js"), "utf8");
const chatSessionSource = fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8");
const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
const context = vm.createContext({ console });
context.window = context;
context.state = {
  skills: [], selectedSkillIndex: -1,
  skillLibraryBaselineItems: [], skillLibraryBaseline: ""
};
context.$ = () => null;
context.updateSkillSaveButton = () => {};
vm.runInContext(source, context, { filename: "app-skills.js" });

function item(id, revision, description = "Description") {
  return {
    revision, id, host: "Common", name: id,
    description, version: "1.0.0", body: { sha256: "b".repeat(64), byteLength: 10, characters: 10 },
    enabled: true, builtIn: false, references: [],
    package: { formatVersion: 1, packageId: "fixture", requiredTools: [{ id: "core.read", contractVersion: "1" }], optionalTools: [] }
  };
}

function library(skills) {
  return {
    type: "rnassistant.skillLibrary",
    contractVersion: 1,
    skills
  };
}

{
  const skills = context.skillLibraryItemsFromContract(library([
    item("common.one", "a".repeat(64))
  ]));
  assert.equal(skills[0].Id, "common.one");
  assert.equal(skills[0]._baseRevision, "a".repeat(64));
  const inline = item("legacy", "b".repeat(64)); delete inline.body; inline.bodyMarkdown = "# Inline";
  assert.throws(() => context.skillFromContract(inline), /typed package/);
  assert.throws(() => context.skillLibraryItemsFromContract([item("legacy", "b".repeat(64))]),
    /typed Skill Library/);
  assert.throws(() => context.skillLibraryItemsFromContract({
    Type: "rnassistant.skillLibrary", ContractVersion: 1, Skills: []
  }), /typed Skill Library/);
  console.log("PASS skill contract: lowercase versioned library is the only accepted source");
}

{
  context.state.skills = context.skillLibraryItemsFromContract(library([
    item("common.update", "1".repeat(64), "Before"),
    item("common.delete", "2".repeat(64))
  ]));
  context.setSkillLibraryBaseline(context.state.skills);
  context.state.skills[0].Description = "After";
  context.state.skills.splice(1, 1);
  const created = context.skillFromContract(item("common.new", "3".repeat(64)));
  created.Revision = "";
  created._baseId = "";
  created._baseRevision = "";
  context.skillWithBodyDraft(created, "# New");
  context.state.skills.push(created);
  const mutations = context.skillLibraryMutations();
  assert.deepEqual(Array.from(mutations, mutation => mutation.kind),
    ["upsert", "upsert", "delete"]);
  assert.equal(mutations[0].baseId, "common.update");
  assert.equal(mutations[0].expectedRevision, "1".repeat(64));
  assert.equal(mutations[0].preserveBody, true, "metadata edits do not need to fetch or send the body");
  assert.equal(mutations[0].bodyMarkdown, undefined);
  assert.equal(mutations[1].bodyMarkdown, "# New");
  assert.equal(mutations[0].package.requiredTools[0].id, "core.read", "metadata edits retain dependencies");
  assert.equal(mutations[1].package.packageId, "fixture", "new copies retain their manifest");
  assert.equal(mutations[1].baseId, "");
  assert.equal(mutations[1].expectedRevision, "");
  assert.equal(mutations[2].baseId, "common.delete");
  assert.equal(mutations[2].expectedRevision, "2".repeat(64));
  assert.equal(Object.prototype.hasOwnProperty.call(mutations[0], "Skills"), false);
  console.log("PASS skill contract: editor emits explicit guarded mutations without catalog reconcile delete");
}

{
  const response = {
    type: "rnassistant.skillReferenceResult",
    contractVersion: 1,
    result: {
      type: "rnassistant.skillMutationResult", contractVersion: 1,
      status: "ok", message: "updated", dispatch: "may_have_dispatched",
      effect: "verified_change", operation: "update_reference"
    },
    skill: Object.assign(item("common.one", "4".repeat(64)), {
      references: [{
        path: "references/rules.md",
        byteLength: 7,
        revision: "5".repeat(64)
      }]
    }),
    path: "references/rules.md", deleted: false,
    reference: { path: "references/rules.md", byteLength: 7, revision: "5".repeat(64) }
  };
  const parsed = context.skillReferenceFromResponse(response, "update_reference");
  assert.equal(Object.prototype.hasOwnProperty.call(parsed, "content"), false, "reference mutation results contain no inline echo");
  assert.equal(parsed.skill.References[0].Path, "references/rules.md");
  assert.throws(() => context.skillReferenceFromResponse(
    Object.assign({}, response, { contractVersion: 0 }), "update_reference"),
  /typed/);
  console.log("PASS skill contract: reference mutation result is exact and versioned");
}

{
  assert.match(index, /src="js\/app-skills\.js\?v=[^"]+"/);
  assert.equal(/StoragePath|storagePath|response\s*\|\|\s*\[\]/.test(source), false);
  assert.match(source, /expectedPackageRevision/);
  assert.match(source, /skillLibraryMutationRequestType/);
  assert.match(chatSessionSource, /applyChatState\(init\)/);
  assert.match(chatStateSource, /applyLibraryCatalogState\(response\)/);
  assert.match(chatStateSource,
    /skillLibraryItemsFromContract\(response\.skills\)/);
  assert.doesNotMatch(chatStateSource, /response\.Skills/);
  console.log("PASS skill contract: shipped UI has no path identity or unversioned response fallback");
}

{
  vm.runInContext(chatStateSource, context, { filename: "app-chat-state.js" });
  context.state.skills = [];
  context.state.selectedSkillIndex = -1;
  context.state.skillLibraryDirty = false;
  assert.equal(context.applyLibraryCatalogState({ skills: library([item("skills.workspace.files", "6".repeat(64))]) }), true);
  assert.equal(context.state.skills[0].Id, "skills.workspace.files");
  context.state.selectedSkillIndex = 0;
  context.applyLibraryCatalogState({ skills: library([
    item("skills.chat.visualization", "7".repeat(64)), item("skills.workspace.files", "8".repeat(64), "Updated")
  ]) });
  assert.equal(context.state.selectedSkillIndex, 1, "selection follows identity after catalog refresh");
  assert.equal(context.state.skills[1].Description, "Updated");
  const accepted = context.state.skills;
  assert.equal(context.applyLibraryCatalogState({}), false);
  assert.equal(context.state.skills, accepted, "unrelated chat updates do not clear the catalog");
  assert.throws(() => context.applyLibraryCatalogState({ skills: [] }), /typed Skill Library/);
  assert.equal(context.state.skills, accepted, "invalid legacy payload cannot replace accepted state");
  console.log("PASS skill contract: shared chat projection applies typed catalogs and retains selection");
}

console.log("OK 5/5");
