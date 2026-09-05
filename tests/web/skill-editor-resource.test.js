"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
const referencePath = "references/rules.md";

function fixture(text = "# Справка\r\n😀\r\n", sourcePath = referencePath, builtIn = false) {
  const bytes = new TextEncoder().encode(text), calls = [], closes = [], errors = [], cancelled = [], elements = {};
  const skill = { Id: "common.editor", Revision: "package", _baseRevision: builtIn ? "" : "package", _baseId: builtIn ? "" : "common.editor", BuiltIn: builtIn,
    Body: { sha256: sha(bytes), byteLength: bytes.length, characters: text.length },
    _selectedReferencePath: sourcePath, References: [{ Path: referencePath, ByteLength: bytes.length + 3, Revision: "b".repeat(64) }] };
  const metadata = { type: "rnassistant.skillSourceRead", contractVersion: 1, chatId: "chat", skillId: skill.Id,
    packageRevision: "package", path: sourcePath, reference: sourcePath ? { path: referencePath, byteLength: bytes.length + 3, revision: "b".repeat(64) } : null,
    resource: { uri: "rna://catalog/" + (builtIn ? "builtin-skills-word" : "skills") + "/common.editor/" + (sourcePath ? "reference/rules.md" : "body"), revision: "r_published" }, totalCharacters: text.length,
    data: { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/download/" + "a".repeat(64), maxChunkBytes: 65536,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/markdown; charset=utf-8" } } };
  const context = vm.createContext({ AbortController, TextDecoder, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    state: { skills: [skill], selectedSkillIndex: 0, selectedInstructionKind: "skill", activeChatId: "chat", bridgeUnavailable: false },
    $: id => elements[id] || (elements[id] = { value: "", readOnly: true }), log: error => errors.push(error),
    cancelBridgeRequest: async id => cancelled.push(id),
    send(type, payload) {
      calls.push(type);
      if (type === "resourceDataClose") { closes.push(payload); return Promise.resolve(); }
      assert.equal(type, "readSkillSource"); assert.equal(payload.chatId, "chat");
      return Object.assign(Promise.resolve(metadata), { requestId: "read" });
    },
    async fetch(url, config) {
      calls.push("fetch"); assert.equal(config.redirect, "error");
      const params = new URL(url).searchParams, offset = Number(params.get("offset")), count = Number(params.get("count"));
      return new Response(bytes.slice(offset, offset + count), { headers: { "Content-Type": "text/markdown; charset=utf-8" } });
    } });
  context.window = context;
  ["app-resource-download.js", "app-skills.js"].forEach(file => vm.runInContext(read("js/" + file), context));
  context.renderSkillPreview = () => {};
  context.syncSelectedSkillFromEditor = () => {};
  return { context, skill, text, bytes, metadata, calls, closes, errors, cancelled, elements,
    load: () => context.loadSelectedSkillSource(skill, sourcePath) };
}

(async () => {
  {
    for (const text of ["# Справка\r\n" + "ж".repeat(140000) + "\r\n", "", "\ufeff# Meaningful leading character\r\n"]) {
      const f = fixture(text); await f.load();
      assert.equal(f.skill._sourceDrafts[referencePath], text); assert.equal(f.skill._sourceLoaded[referencePath], true);
      assert.equal(f.elements.skillBodyInput.readOnly, false); assert.equal(f.skill._baseRevision, "package");
      assert.equal(f.closes.length, 1); assert.equal(f.closes[0].workspaceId, "skill-editor");
      assert.equal(f.errors.length, 0);
    }
    console.log("PASS skill reference: exact Unicode/CRLF, empty source and separate source-file hash");
  }
  {
    for (const mutate of [m => { m.chatId = "foreign"; }, m => { m.packageRevision = "new"; },
      m => { m.type = "rnassistant.skillReferenceResult"; m.content = "legacy"; }, m => { m.reference.revision = "foreign"; },
      m => { m.resource.uri = "rna://catalog/skills/other/reference/rules.md"; }, m => { m.totalCharacters = 500001; },
      m => { m.data.url = "https://foreign/source"; }, m => { m.data.payload.byteLength = 2100001; },
      m => { m.totalCharacters--; }, m => { m.data.payload.sha256 = "f".repeat(64); }]) {
      const f = fixture(); mutate(f.metadata); await f.load();
      assert.equal(!!f.skill._sourceLoaded[referencePath], false); assert.equal(f.elements.skillBodyInput.readOnly, true);
      f.context.captureSelectedSkillResource(f.skill);
      assert.equal(!!f.skill._sourceDirty[referencePath], false, "failed read never becomes an empty editable draft");
      assert.equal(f.closes.length, 1); assert.equal(f.errors.length, 1);
    }
    console.log("PASS skill reference: wrong metadata, bounds, partial/corrupt bytes and old inline response stay read-only");
  }
  {
    for (const change of [f => { f.context.state.activeChatId = "other"; }, f => { f.skill._selectedReferencePath = ""; },
      f => { f.skill.Revision = "new"; }, f => { f.skill.References[0].Revision = "changed"; },
      f => { f.context.state.skills = []; }, f => { f.context.state.selectedInstructionKind = "tool"; }, f => { f.context.cancelSkillSourceRead(); }]) {
      const f = fixture(), send = f.context.send; let release;
      f.context.send = (type, payload) => type === "readSkillSource"
        ? Object.assign(new Promise(resolve => { release = resolve; }), { requestId: "late" }) : send(type, payload);
      const reading = f.load(); await f.load(); assert.equal(f.context.skillSourceReadPending, 1);
      change(f); release(f.metadata); await reading;
      assert.equal(!!f.skill._sourceLoaded[referencePath], false); assert.equal(f.closes.length, 1);
      assert.equal(f.context.skillSourceReadPending, 0);
    }
    console.log("PASS skill reference: late chat/selection/package/reference responses close without applying source");
  }
  {
    const f = fixture(); let started;
    const fetching = new Promise(resolve => { started = resolve; });
    f.context.fetch = (_, options) => new Promise((resolve, reject) => {
      options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }); started();
    });
    const reading = f.load(); await fetching; f.context.cancelSkillSourceRead(); await reading;
    assert.equal(f.closes.length, 1); assert.equal(!!f.skill._sourceLoaded[referencePath], false);
    const bounded = fixture(); bounded.context.skillSourceReadPending = 2; await bounded.load();
    assert.equal(bounded.calls.length, 0, "cancelled slow captures cannot create an unbounded producer queue");
    console.log("PASS skill reference: active fetch cancellation and pending-capture bound");
  }
  {
    for (const dirty of [false, true]) {
      const f = fixture(); await f.load();
      if (dirty) { f.skill._sourceDrafts[referencePath] = "user draft"; f.skill._sourceDirty[referencePath] = true; }
      const next = { Id: f.skill.Id, Revision: "new", _baseRevision: "new", References: [{ Path: referencePath, Revision: "changed", ByteLength: 1 }] };
      f.context.state.skills = f.context.preserveSkillSourceState([next]);
      if (dirty) {
        assert.equal(next._sourceDrafts[referencePath], "user draft");
        assert.throws(() => f.context.requireUnconflictedSkillSources(), /Источник навыка изменился/);
        const count = f.calls.length; await assert.rejects(f.context.saveSelectedSkillResource(), /Источник навыка изменился/);
        assert.equal(f.calls.length, count, "a dirty stale reference is not silently rebased into a save");
      } else {
        assert.equal(!!next._sourceLoaded[referencePath], false); assert.equal(next._sourceDrafts[referencePath], undefined);
      }
    }
    const f = fixture(); await f.load();
    const same = { Id: f.skill.Id, _baseRevision: "metadata-only-change", References: f.skill.References.slice() };
    f.context.state.skills = f.context.preserveSkillSourceState([same]);
    assert.equal(same._sourceDrafts[referencePath], f.text, "same exact reference can retain its editor draft after unrelated metadata changes");
    console.log("PASS skill reference: catalog changes invalidate clean cache and preserve conflicting user drafts without writes");
  }
  {
    for (const file of ["app-chat-state.js", "app-chat-session.js", "app-prompts.js"])
      assert.ok(read("js/" + file).includes("cancelSkillSourceRead()"));
    assert.ok(read("js/app-skills.js").includes('window.addEventListener("pagehide", cancelSkillSourceRead)'));
    for (const file of ["app-skills.js", "app-chat-state.js", "app-chat-session.js", "app-prompts.js"])
      assert.ok(read("index.html").includes(file + "?v=skill-core-20260906-1"));
    console.log("PASS skill reference: lifecycle and changed assets are delivered together");
  }
  {
    for (const builtIn of [false, true]) {
      const f = fixture("\ufeff# Core\r\n" + "Ж".repeat(70000), "", builtIn);
      f.context.setSkillLibraryBaseline([f.skill]); const baseline = f.context.skillLibrarySnapshot([f.skill]);
      assert.equal(f.calls.length, 0, "catalog metadata never eagerly fetches a body");
      await f.load();
      assert.ok(f.skill._sourceDrafts[""] === f.text); assert.equal(f.elements.skillBodyInput.readOnly, builtIn);
      assert.equal(f.context.skillLibrarySnapshot([f.skill]), baseline, "hydration is not a user edit");
      assert.equal(f.context.hasDirtySkillSource(), false); assert.equal(f.closes.length, 1);
      assert.equal(f.errors.length, 0);
    }
    console.log("PASS skill core: lazy exact Unicode/BOM source and read-only builtin share one reader");
  }
  {
    for (const change of [m => { m.reference = { path: referencePath }; }, m => { m.data.payload.sha256 = "f".repeat(64); },
      m => { m.data.payload.byteLength++; }, m => { m.totalCharacters++; }, m => { m.path = referencePath; },
      m => { m.resource.uri = "rna://catalog/builtin-skills-word/common.editor/body"; }]) {
      const f = fixture("# Core", ""); change(f.metadata); await f.load();
      assert.equal(!!f.skill._sourceLoaded[""], false); assert.equal(f.elements.skillBodyInput.readOnly, true);
      f.context.captureSelectedSkillResource(f.skill); assert.equal(!!f.skill._sourceDirty[""], false);
    }
    const f = fixture("# Core", ""), send = f.context.send; let release;
    f.context.send = (type, payload) => type === "readSkillSource" ? new Promise(resolve => { release = resolve; }) : send(type, payload);
    const reading = f.load(); f.skill.Body = { sha256: "changed" }; release(f.metadata); await reading;
    assert.equal(!!f.skill._sourceLoaded[""], false); assert.equal(f.closes.length, 1);
    console.log("PASS skill core: mismatched source metadata and late body revision stay read-only");
  }
  {
    for (const dirty of [false, true]) {
      const f = fixture("# Core", ""); await f.load();
      if (dirty) { f.skill._sourceDrafts[""] = "user draft"; f.skill._sourceDirty[""] = true; }
      const next = { Id: f.skill.Id, Body: { sha256: "changed" }, Revision: "new", _baseRevision: "new", References: [] };
      f.context.state.skills = f.context.preserveSkillSourceState([next]);
      assert.equal(!!next._sourceLoaded[""], dirty);
      if (dirty) assert.throws(() => f.context.requireUnconflictedSkillSources(), /Источник навыка изменился/);
    }
    const f = fixture("# Core", ""); await f.load();
    f.skill._sourceDrafts[referencePath] = "draft"; f.skill._sourceLoaded[referencePath] = true; f.skill._sourceDirty[referencePath] = true;
    f.context.trimSkillSourceCache(null, "");
    assert.equal(f.skill._sourceDrafts[""], undefined); assert.equal(f.skill._sourceDrafts[referencePath], "draft");
    console.log("PASS skill core: changed clean source is invalidated, dirty drafts survive and clean cache stays selection-bounded");
  }
  {
    for (const mode of ["saved", "late-edit", "partial"]) {
      const lateEdit = mode === "late-edit";
      const f = fixture("# Core", ""); await f.load(); f.context.setSkillLibraryBaseline([f.skill]);
      f.elements.skillBodyInput.value = "# Edit"; f.context.captureSelectedSkillResource(f.skill);
      f.context.renderSkills = () => {};
      const body = new TextEncoder().encode("# Edit"); let sent;
      f.context.send = async (type, payload) => {
        assert.equal(type, "saveSkills"); sent = payload.mutations[0];
        if (lateEdit) f.skill._sourceDrafts[""] = "# Later user edit";
        return { type: "rnassistant.skillLibraryMutationResult", contractVersion: 1,
          results: [{ type: "rnassistant.skillMutationResult", contractVersion: 1, status: "ok", message: "saved", dispatch: "may_have_dispatched",
            effect: "verified_change", id: f.skill.Id, revision: "saved" }].concat(mode === "partial" ? [{
              type: "rnassistant.skillMutationResult", contractVersion: 1, status: "error", message: "another package failed",
              dispatch: "not_dispatched", effect: "none", id: "other" }] : []),
          library: { type: "rnassistant.skillLibrary", contractVersion: 1, skills: [{ id: f.skill.Id, host: "Common", name: f.skill.Id,
            description: "", version: "1.0.0", enabled: true, builtIn: false, revision: "saved", references: [],
            body: { sha256: sha(body), byteLength: body.length, characters: 6 } }] } };
      };
      if (lateEdit) await assert.rejects(f.context.saveSelectedSkillResource(), /Источник навыка изменился/);
      else if (mode === "partial") await assert.rejects(f.context.saveSelectedSkillResource(), /another package failed/);
      else await f.context.saveSelectedSkillResource();
      assert.equal(sent.bodyMarkdown, "# Edit"); assert.equal(sent.preserveBody, false);
      const saved = f.context.state.skills[0];
      assert.equal(!!saved._sourceDirty[""], lateEdit);
      if (lateEdit) assert.equal(saved._sourceDrafts[""], "# Later user edit");
      else {
        assert.equal(!!saved._sourceLoaded[""], false, "successful save refreshes from exact published metadata, not an inline echo");
        assert.equal(saved.Revision, "saved", "a verified successful member keeps its exact revision even if a later package failed");
      }
    }
    console.log("PASS skill core: explicit replacement save acknowledges only the submitted draft and preserves later edits");
  }
  console.log("OK 10/10");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
