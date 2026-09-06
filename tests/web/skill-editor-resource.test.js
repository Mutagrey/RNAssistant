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
  const context = vm.createContext({ AbortController, TextDecoder, TextEncoder, Blob, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
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
  ["app-resource-download.js", "app-resource-upload.js", "app-skills.js"].forEach(file => vm.runInContext(read("js/" + file), context));
  context.renderSkillPreview = () => {};
  context.syncSelectedSkillFromEditor = () => {};
  return { context, skill, text, bytes, metadata, calls, closes, errors, cancelled, elements,
    load: () => context.loadSelectedSkillSource(skill, sourcePath) };
}

function uploads(f, save, hooks = {}) {
  const send = f.context.send, fetch = f.context.fetch, pending = {}, bodies = [], closed = [], controls = [];
  let sequence = 0;
  f.context.fetch = async (url, options) => {
    if (options.method !== "POST") return fetch(url, options);
    const address = new URL(url), id = address.pathname.split("/").pop(), current = pending[id];
    assert.equal(options.redirect, "error"); assert.equal(options.credentials, "omit");
    const offset = Number(address.searchParams.get("offset")), count = Number(address.searchParams.get("count"));
    assert.equal(current.bytes.length, offset); assert.ok(count <= 262144);
    current.bytes = Buffer.concat([current.bytes, Buffer.from(await options.body.arrayBuffer())]);
    if (hooks.chunk) await hooks.chunk(options);
    return new Response(JSON.stringify({ leaseId: id, nextOffset: offset + count }));
  };
  f.context.send = (type, payload) => {
    let response;
    if (type === "beginSkillMutationUpload") {
      assert.deepEqual(Object.keys(payload).sort(), ["byteLength", "chatId"]); assert.equal(payload.chatId, "chat");
      const id = (++sequence).toString(16).padStart(64, "0"), lease = { leaseId: id, byteLength: payload.byteLength,
        url: "https://rnassistant.local-resource/v1/upload/" + id, maxChunkBytes: 65536 };
      pending[id] = { bytes: Buffer.alloc(0), lease };
      response = hooks.open ? hooks.open(lease) : lease;
    } else if (type === "cancelSkillMutationUpload") {
      closed.push(payload); assert.equal(payload.chatId, "chat"); response = { closed: true };
    } else if (type === "saveSkills" || type === "saveSkillReference") {
      assert.deepEqual(Object.keys(payload).sort(), ["chatId", "sha256", "uploadLeaseId"]);
      const upload = pending[payload.uploadLeaseId];
      assert.equal(upload.bytes.length, upload.lease.byteLength); assert.equal(payload.sha256, sha(upload.bytes));
      const body = JSON.parse(upload.bytes.toString("utf8")); bodies.push(body); controls.push(payload);
      response = save(type, body);
    } else return send(type, payload);
    return Object.assign(Promise.resolve(response), { requestId: type + "-request" });
  };
  return { bodies, closed, controls, pending };
}

function catalog(f, revision = "package", references = f.skill.References) {
  return { type: "rnassistant.skillLibrary", contractVersion: 1, skills: [{
    id: f.skill.Id, host: "Common", name: f.skill.Id, description: "", version: "1.0.0", enabled: true,
    builtIn: false, revision, body: f.skill.Body,
    references: references.map(item => ({ path: item.Path, revision: item.Revision, byteLength: item.ByteLength }))
  }] };
}

function coreResponse(f) {
  return { type: "rnassistant.skillLibraryMutationResult", contractVersion: 1, results: [], library: catalog(f) };
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
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
    for (const file of ["app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("js/" + file).includes("cancelSkillSourceWrite()"));
    assert.ok(read("js/app-skills.js").includes('window.addEventListener("pagehide", cancelSkillSourceWrite)'));
    for (const file of ["app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("index.html").includes(file + "?v=prompt-source-20260906-1"));
    assert.ok(read("index.html").includes("app-skills.js?v=skill-upload-20260906-1"));
    assert.ok(read("index.html").includes("app-prompts.js?v=prompt-source-20260906-1"));
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
      const transport = uploads(f, async (type, payload) => {
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
      });
      if (lateEdit) await assert.rejects(f.context.saveSelectedSkillResource(), /Источник навыка изменился/);
      else if (mode === "partial") await assert.rejects(f.context.saveSelectedSkillResource(), /another package failed/);
      else await f.context.saveSelectedSkillResource();
      assert.equal(sent.bodyMarkdown, "# Edit"); assert.equal(sent.preserveBody, false);
      assert.equal(transport.closed.length, 1); assert.equal(transport.bodies.length, 1);
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
  {
    for (const stage of ["begin", "chunk", "dispatched"]) {
      const f = fixture("# Core", ""); await f.load(); f.context.setSkillLibraryBaseline([f.skill]);
      f.elements.skillBodyInput.value = "# Edit"; f.context.captureSelectedSkillResource(f.skill);
      const reached = deferred(), release = deferred();
      const transport = uploads(f, async () => { reached.resolve(); await release.promise; return coreResponse(f); }, {
        open: async lease => { if (stage === "begin") { reached.resolve(); await release.promise; } return lease; },
        chunk: async options => { if (stage === "chunk") {
          reached.resolve(); await new Promise((resolve, reject) => options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }));
        } }
      });
      const saving = f.context.saveSelectedSkillResource();
      const stopped = assert.rejects(saving, error => {
        if (stage === "dispatched") assert.match(error.detail, /результат записи не подтверждён/);
        return true;
      });
      await reached.promise; f.context.state.activeChatId = "other"; f.context.cancelSkillSourceWrite();
      assert.ok(f.context.skillWriteOperation, "cancelled work retains the single writer slot until it exits");
      await assert.rejects(f.context.saveSelectedSkillResource(), /Дождитесь завершения/);
      release.resolve(); await stopped;
      assert.equal(transport.closed.length, 1); assert.equal(transport.closed[0].chatId, "chat");
      assert.equal(transport.controls.length, stage === "dispatched" ? 1 : 0, "no dispatch after cancellation and no automatic retry");
      assert.equal(f.context.state.skills[0], f.skill); assert.equal(f.skill._sourceDrafts[""], "# Edit");
      assert.equal(f.context.skillWriteOperation, null);
      assert.equal(f.elements.copySkillContextButton.disabled, false, "writer completion restores loaded-source controls");
      if (stage !== "chunk") assert.ok(f.cancelled.includes((stage === "begin" ? "beginSkillMutationUpload" : "saveSkills") + "-request"));
    }
    console.log("PASS skill upload: begin/fetch/late-dispatch cancellation closes the captured lease, preserves drafts and never retries");
  }
  {
    for (const text of ["", "# Reference\r\n" + "Ж".repeat(40000) + "😀", "late-edit"]) {
      const f = fixture(); await f.load(); f.context.setSkillLibraryBaseline([f.skill]);
      f.skill._sourceDrafts[referencePath] = text; f.skill._sourceDirty[referencePath] = true;
      f.context.renderSkills = () => {};
      const laterPath = "references/later.md";
      const transport = uploads(f, async (type, body) => {
        if (type === "saveSkills") {
          assert.equal(body.mutations.length, 0);
          // A new edit made after Save was pressed must not join the in-flight plan.
          f.skill._sourceDrafts[laterPath] = "later draft"; f.skill._sourceDirty[laterPath] = true;
          f.skill._sourceLoaded[laterPath] = true; f.skill.References.push({ Path: laterPath, Revision: "", ByteLength: 0, Pending: true });
          return coreResponse(f);
        }
        assert.equal(type, "saveSkillReference"); assert.equal(body.content, text); assert.equal(body.path, referencePath);
        assert.equal(body.expectedPackageRevision, "package");
        if (text === "late-edit") f.context.state.skills[0]._sourceDrafts[referencePath] = "new unsaved draft";
        const reference = { Path: referencePath, Revision: sha(Buffer.from(text)), ByteLength: Buffer.byteLength(text) };
        return { type: "rnassistant.skillReferenceResult", contractVersion: 1, path: referencePath, deleted: false,
          skill: catalog(f, "saved", [reference]).skills[0],
          reference: { path: referencePath, revision: reference.Revision, byteLength: reference.ByteLength },
          result: { type: "rnassistant.skillMutationResult", contractVersion: 1, status: "ok", message: "saved",
            operation: "update_reference", dispatch: "may_have_dispatched", effect: "verified_change", id: f.skill.Id,
            previousRevision: "package", revision: "saved" } };
      });
      await f.context.saveSelectedSkillResource();
      assert.equal(transport.bodies.length, 2); assert.equal(transport.closed.length, 2);
      const saved = f.context.state.skills[0];
      assert.equal(saved.Revision, "saved"); assert.equal(saved._sourceDrafts[laterPath], "later draft");
      assert.equal(saved._sourceDirty[laterPath], true, "later new references stay unsaved");
      if (text === "late-edit") {
        assert.equal(saved._sourceDrafts[referencePath], "new unsaved draft"); assert.equal(saved._sourceConflicts[referencePath], true);
      } else assert.equal(saved._sourceDrafts[referencePath], undefined, "success rereads the published source without an echo");
    }
    console.log("PASS skill upload: empty/Unicode reference bodies, exact guards, frozen plans and later drafts without inline echo");
  }
  {
    for (const mode of ["oversize", "unicode", "count", "invalid-response", "unknown"]) {
      const f = fixture("# Core", ""); await f.load(); f.context.setSkillLibraryBaseline([f.skill]);
      f.skill._sourceDrafts[""] = mode === "oversize" ? "x".repeat(500001) : mode === "unicode" ? "\ud800" : "# Edit";
      f.skill._sourceDirty[""] = true; f.context.renderSkills = () => {};
      if (mode === "count") f.context.skillLibraryMutations = () => new Array(257).fill({ kind: "delete" });
      const transport = uploads(f, () => mode === "invalid-response" ? {} : {
        ...coreResponse(f), results: [{ type: "rnassistant.skillMutationResult", contractVersion: 1, status: "unknown",
          message: "read-back failed", dispatch: "may_have_dispatched", effect: "unknown", id: f.skill.Id }] });
      await assert.rejects(f.context.saveSelectedSkillResource(), error => {
        if (["unknown", "invalid-response"].includes(mode)) assert.match(error.detail, /результат записи не подтверждён/);
        else assert.match(error.message, /RESOURCE_(BATCH_TOO_LARGE|UPLOAD_INVALID)/);
        return true;
      });
      const dispatched = ["unknown", "invalid-response"].includes(mode);
      assert.equal(Object.keys(transport.pending).length, dispatched ? 1 : 0);
      assert.equal(transport.closed.length, dispatched ? 1 : 0);
      assert.equal(transport.controls.length, dispatched ? 1 : 0);
      assert.equal(f.context.state.skills[0]._sourceDirty[""], true); assert.equal(f.context.skillWriteOperation, null);
    }
    console.log("PASS skill upload: source/count bounds fail before allocation; malformed/unknown results preserve drafts and stop");
  }
  console.log("OK 13/13");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
