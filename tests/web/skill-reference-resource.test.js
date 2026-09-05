"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
const referencePath = "references/rules.md";

function fixture(text = "# Справка\r\n😀\r\n") {
  const bytes = new TextEncoder().encode(text), calls = [], closes = [], errors = [], cancelled = [], elements = {};
  const skill = { Id: "common.editor", Revision: "package", _baseRevision: "package", BuiltIn: false,
    _selectedReferencePath: referencePath, References: [{ Path: referencePath, ByteLength: bytes.length + 3, Revision: "b".repeat(64) }] };
  const metadata = { type: "rnassistant.skillReferenceRead", contractVersion: 1, chatId: "chat", skillId: skill.Id,
    packageRevision: "package", reference: { path: referencePath, byteLength: bytes.length + 3, revision: "b".repeat(64) },
    resource: { uri: "rna://catalog/skills/common.editor/reference/rules.md", revision: "r_published" }, totalCharacters: text.length,
    data: { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/download/" + "a".repeat(64), maxChunkBytes: 65536,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/markdown; charset=utf-8" } } };
  const context = vm.createContext({ AbortController, TextDecoder, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    state: { skills: [skill], selectedSkillIndex: 0, activeChatId: "chat", bridgeUnavailable: false },
    $: id => elements[id] || (elements[id] = { value: "", readOnly: true }), log: error => errors.push(error),
    cancelBridgeRequest: async id => cancelled.push(id),
    send(type, payload) {
      calls.push(type);
      if (type === "resourceDataClose") { closes.push(payload); return Promise.resolve(); }
      assert.equal(type, "readSkillReference"); assert.equal(payload.chatId, "chat");
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
    load: () => context.loadSelectedSkillReference(skill, referencePath) };
}

(async () => {
  {
    for (const text of ["# Справка\r\n" + "ж".repeat(140000) + "\r\n", "", "\ufeff# Meaningful leading character\r\n"]) {
      const f = fixture(text); await f.load();
      assert.equal(f.skill._referenceDrafts[referencePath], text); assert.equal(f.skill._referenceLoaded[referencePath], true);
      assert.equal(f.elements.skillBodyInput.readOnly, false); assert.equal(f.skill._baseRevision, "package");
      assert.equal(f.closes.length, 1); assert.equal(f.closes[0].workspaceId, "skill-reference-editor");
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
      assert.equal(!!f.skill._referenceLoaded[referencePath], false); assert.equal(f.elements.skillBodyInput.readOnly, true);
      f.context.captureSelectedSkillResource(f.skill);
      assert.equal(!!f.skill._referenceDirty[referencePath], false, "failed read never becomes an empty editable draft");
      assert.equal(f.closes.length, 1); assert.equal(f.errors.length, 1);
    }
    console.log("PASS skill reference: wrong metadata, bounds, partial/corrupt bytes and old inline response stay read-only");
  }
  {
    for (const change of [f => { f.context.state.activeChatId = "other"; }, f => { f.skill._selectedReferencePath = ""; },
      f => { f.skill._baseRevision = "new"; }, f => { f.skill.References[0].Revision = "changed"; },
      f => { f.context.state.skills = []; }, f => { f.context.cancelSkillReferenceRead(); }]) {
      const f = fixture(), send = f.context.send; let release;
      f.context.send = (type, payload) => type === "readSkillReference"
        ? Object.assign(new Promise(resolve => { release = resolve; }), { requestId: "late" }) : send(type, payload);
      const reading = f.load(); await f.load(); assert.equal(f.context.skillReferenceReadPending, 1);
      change(f); release(f.metadata); await reading;
      assert.equal(!!f.skill._referenceLoaded[referencePath], false); assert.equal(f.closes.length, 1);
      assert.equal(f.context.skillReferenceReadPending, 0);
    }
    console.log("PASS skill reference: late chat/selection/package/reference responses close without applying source");
  }
  {
    const f = fixture(); let started;
    const fetching = new Promise(resolve => { started = resolve; });
    f.context.fetch = (_, options) => new Promise((resolve, reject) => {
      options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }); started();
    });
    const reading = f.load(); await fetching; f.context.cancelSkillReferenceRead(); await reading;
    assert.equal(f.closes.length, 1); assert.equal(!!f.skill._referenceLoaded[referencePath], false);
    const bounded = fixture(); bounded.context.skillReferenceReadPending = 2; await bounded.load();
    assert.equal(bounded.calls.length, 0, "cancelled slow captures cannot create an unbounded producer queue");
    console.log("PASS skill reference: active fetch cancellation and pending-capture bound");
  }
  {
    for (const dirty of [false, true]) {
      const f = fixture(); await f.load();
      if (dirty) { f.skill._referenceDrafts[referencePath] = "user draft"; f.skill._referenceDirty[referencePath] = true; }
      const next = { Id: f.skill.Id, Revision: "new", _baseRevision: "new", References: [{ Path: referencePath, Revision: "changed", ByteLength: 1 }] };
      f.context.state.skills = f.context.preserveSkillReferenceState([next]);
      if (dirty) {
        assert.equal(next._referenceDrafts[referencePath], "user draft");
        assert.throws(() => f.context.requireUnconflictedSkillReferences(), /Reference изменился/);
        const count = f.calls.length; await assert.rejects(f.context.saveSelectedSkillResource(), /Reference изменился/);
        assert.equal(f.calls.length, count, "a dirty stale reference is not silently rebased into a save");
      } else {
        assert.equal(!!next._referenceLoaded[referencePath], false); assert.equal(next._referenceDrafts[referencePath], undefined);
      }
    }
    const f = fixture(); await f.load();
    const same = { Id: f.skill.Id, _baseRevision: "metadata-only-change", References: f.skill.References.slice() };
    f.context.state.skills = f.context.preserveSkillReferenceState([same]);
    assert.equal(same._referenceDrafts[referencePath], f.text, "same exact reference can retain its editor draft after unrelated metadata changes");
    console.log("PASS skill reference: catalog changes invalidate clean cache and preserve conflicting user drafts without writes");
  }
  {
    for (const file of ["app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("js/" + file).includes("cancelSkillReferenceRead()"));
    assert.ok(read("js/app-skills.js").includes('window.addEventListener("pagehide", cancelSkillReferenceRead)'));
    for (const file of ["app-skills.js", "app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("index.html").includes(file + "?v=skill-resource-20260906-1"));
    console.log("PASS skill reference: lifecycle and changed assets are delivered together");
  }
  console.log("OK 6/6");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
