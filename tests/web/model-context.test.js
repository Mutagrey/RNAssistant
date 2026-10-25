"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const crypto = require("node:crypto");
class Node {
  constructor(tag = "div") { this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {}; this.style = {}; this.value = ""; this.isConnected = true; this.classes = new Set(); this.classList = {
    add: x => this.classes.add(x), remove: x => this.classes.delete(x), contains: x => this.classes.has(x),
    toggle: (x, force) => force ? this.classes.add(x) : this.classes.delete(x)
  }; }
  set textContent(value) { this.text = value; this.children = []; }
  get textContent() { return this.text || ""; }
  appendChild(node) { this.children.push(node); node.parent = this; return node; }
  insertBefore(node, before) { const index = this.children.indexOf(before); this.children.splice(index < 0 ? this.children.length : index, 0, node); node.parent = this; return node; }
  get parentElement() { return this.parent; }
  replaceChildren() { this.children.forEach(n => n.isConnected = false); this.children = []; this.text = ""; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(n => n !== this); this.isConnected = false; }
  setAttribute() {}
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  fire(type) { return Promise.all((this.listeners[type] || []).map(fn => fn.call(this, { key: "", preventDefault() {}, stopPropagation() {} }))); }
  focus() {}
  querySelectorAll() { return []; }
}
async function flush() { for (let i = 0; i < 25; i++) await new Promise(resolve => setImmediate(resolve)); }
function descendants(node) { return [node, ...node.children.flatMap(descendants)]; }
function fixture(withAliases = false, pdf = false) {
  const nodes = new Map(), mounts = [], calls = [], closes = [], timers = new Map(), leases = new Map(); let timer = 0;
  const verification = { availability: "Verified", partsAvailability: "Verified", hold: null };
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const requestText = '{"model":"test","messages":[{"role":"system","content":"rules"},{"role":"user","content":"' + "語😀".repeat(110000) + '"}],"seed":9007199254740993123456789}';
  const events = [{ eventId: "r1", sequence: 3, createdUtc: "2026-10-02T10:00:00Z", type: "llm.request", hasPayload: true, trace: { RequestId: "one", Model: "test", Streaming: true } }];
  let revision = 3, hold = null, leaseCount = 0, responseText = '{"Content":"# Answer"}';
  const c = vm.createContext({ AbortController, TextDecoder, TextEncoder, Uint8Array, Blob, URL, Response,
    setTimeout: (fn, ms) => { timers.set(++timer, { fn, ms }); return timer; }, clearTimeout: id => timers.delete(id),
    crypto: crypto.webcrypto, state: { activeChatId: "chat", draftAttachments: [] }, $: get,
    document: { createElement: tag => new Node(tag), body: new Node(), activeElement: new Node() },
    activeChatSummary: () => ({ revision }), promptContextResourceDraftIds: () => [], closePromptContextInspector() {},
    cancelBridgeRequest: async () => {}, copyTextResult() {},
    send: async (type, query) => {
      calls.push({ type, query });
      if (type === "resourceDataClose") { closes.push(query); return {}; }
      if (type === "getModelContext") {
        const resources = withAliases ? [{ alias: "R1@v1", resource: { uri: "rna://vba/module", revision: "r1" },
          scope: { kind: "conversation", id: "chat" }, currentness: "Superseded", revisionRecorded: true,
          revisionMetadata: { contentSha256: "revision-hash", createdUtc: "2026-10-02T09:00:00Z",
            payload: { sha256: "payload-hash", byteLength: 1234, contentType: "text/plain" },
            parent: { uri: "rna://vba/module", revision: "prior" }, restoredFrom: { uri: "rna://vba/module", revision: "original" },
            dependencies: [{ resource: { uri: "rna://state/source", revision: "dependency" }, kind: "source", view: "text" }] },
          payloadAvailability: "Unchecked",
          retainedViews: [{ view: "source", coverage: { kind: "whole" }, payloadAvailability: "Unchecked",
            partsCount: 2, partsAvailability: "Unchecked" }],
          observations: [{ requestAlias: "Q1", operationAlias: "O1", evidenceAlias: "E1", view: "source",
            coverage: { kind: "line-range", start: 1, end: 2 }, observationComplete: false, bodyIncludedInMessage: null,
            estimatedWireMessageTokens: 72,
            readMetadata: { requestedCoverage: { kind: "line-range", start: 1, end: 6 },
              sourceCoverage: { kind: "line-range", start: 1, end: 2 }, selectionComplete: true,
              representationComplete: false, sourceComplete: null,
              descriptorAtRead: { provider: "vba", kind: "module", mimeType: "text/plain", byteLength: 1234,
                representations: ["source", "outline"], capabilities: ["read"],
                viewCapabilities: [{ view: "source", supportsOffset: true, supportsStream: true,
                  maxBatchBytes: 65536 }] } } },
            { requestAlias: "Q1", operationAlias: "O2", evidenceAlias: "E2", view: "source",
              coverage: { kind: "character-range", start: 10, end: 80 }, observationComplete: false,
              readMetadata: { requestedProcedure: "Value", requestedProcedureKind: "property-get",
                selectionComplete: true, representationComplete: false, sourceComplete: false } },
            { requestAlias: "Q1", operationAlias: "O3", evidenceAlias: "E3", view: "tree",
              coverage: { kind: "json-path", path: "/data", start: 1, end: 2 }, observationComplete: false,
              readMetadata: { requestedCoverage: { kind: "json-path", path: "/data", start: 1, end: 3 },
                requestedDepth: 2, selectionComplete: true, representationComplete: false, sourceComplete: false } },
            { requestAlias: "Q1", operationAlias: "O4", evidenceAlias: "E4", view: "tree",
              coverage: { kind: "yaml-path", path: "/config", start: 0, end: 1 }, observationComplete: false,
              readMetadata: { requestedCoverage: { kind: "yaml-path", path: "/config", start: 0, end: 2 },
                requestedDepth: 1, selectionComplete: true, representationComplete: false, sourceComplete: false } }] }] : [];
        if (pdf) resources.splice(0, resources.length, {
          alias: "R1@v1", resource: { uri: "rna://attachment/pdf", revision: "pdf-r1" },
          scope: { kind: "conversation", id: "chat" }, currentness: "Current",
          retainedViews: [{ view: "binary:render-page:1", coverage: null,
            payloadAvailability: "Unchecked", partsCount: 1, partsAvailability: "Unchecked" }],
          observations: [{ requestAlias: "Q1", operationAlias: "O1", evidenceAlias: "E1", view: "text",
            coverage: { kind: "page-range", start: 1, end: 3 }, observationComplete: false,
            readMetadata: { requestedCoverage: { kind: "page-range", start: 1, end: 3 },
              extractionProfile: "pdf-page-range-text:content-order-v1", sourcePageCount: 12,
              selectionComplete: true, representationComplete: false, sourceComplete: false } }] });
        if (query.verifyViewVariantOrdinal != null) {
          if (verification.hold) await verification.hold;
          assert.equal(query.requestEventId, "r1");
          assert.equal(query.verifyResourceAlias, "R1@v1");
          assert.equal(query.verifyViewVariantSnapshotCount, 3);
          const ordinal = query.verifyViewVariantOrdinal;
          assert.ok(ordinal === 0 || ordinal === 1);
          return { chatId: "chat", revision, events: [events[0]], resources,
            viewVariantVerification: { resourceAlias: "R1@v1", ordinal, snapshotCount: 3,
              variant: { view: "source", coverage: ordinal === 0 ? { kind: "whole" } :
                { kind: "character-range", start: 10, end: 80 },
                payloadAvailability: "Verified", partsCount: ordinal === 0 ? 2 : 0,
                estimatedTextBodyTokens: ordinal === 0 ? undefined : 5,
                partsAvailability: ordinal === 0 ? verification.partsAvailability : null,
                checkedUtc: "2026-10-02T11:00:00Z" } } };
        }
        if (query.verifyResourceAlias) {
          if (verification.hold) await verification.hold;
          assert.equal(query.requestEventId, "r1");
          assert.equal(query.verifyResourceAlias, "R1@v1");
          if (query.verifyRetainedView) {
            assert.equal(query.verifyRetainedView, "source");
            resources[0].retainedViews[0].payloadAvailability = "Verified";
            resources[0].retainedViews[0].partsAvailability = verification.partsAvailability;
            resources[0].retainedViews[0].checkedUtc = "2026-10-02T11:00:00Z";
          } else {
            resources[0].payloadAvailability = verification.availability;
            resources[0].payloadCheckedUtc = "2026-10-02T11:00:00Z";
          }
        }
        if (query.viewInventoryAlias) {
          assert.equal(query.requestEventId, "r1");
          assert.equal(query.viewInventoryAlias, "R1@v1");
          const all = [
            { ordinal: 0, view: "source", coverage: { kind: "whole" }, payloadRegistered: true, partsCount: 2 },
            { ordinal: 1, view: "source", coverage: { kind: "character-range", start: 10, end: 80 },
              payloadRegistered: true, partsCount: 0, estimatedTextBodyTokens: 5 },
            { ordinal: 2, view: "internal", coverage: { kind: "whole" }, payloadRegistered: false, partsCount: 0 }
          ];
          const offset = query.viewInventoryOffset || 0, snapshotCount = query.viewInventorySnapshotCount ?? all.length;
          const items = all.slice(offset, Math.min(offset + 2, snapshotCount));
          return { chatId: "chat", revision, events: [events[0]], resources,
            viewInventory: { resourceAlias: "R1@v1", snapshotCount,
              nextOffset: offset + items.length < snapshotCount ? offset + items.length : null, items } };
        }
        if (query.alias) {
          if (!withAliases || !["Q1", "O1", "E1", "R1@v1"].includes(query.alias)) throw new Error("RESOURCE_NOT_FOUND");
          return { chatId: "chat", revision, events: [events[0]], resources,
            operations: [{ canonicalId: "call-1", alias: "O1" }], evidence: [{ canonicalId: "evidence-1", alias: "E1" }],
            selectedAlias: query.alias, selectedResourceAlias: ["E1", "R1@v1"].includes(query.alias) ? "R1@v1" : null };
        }
        if (query.requestEventId) return { chatId: "chat", revision,
          events: events.filter(e => e.eventId === query.requestEventId || e.trace.RequestId === events.find(r => r.eventId === query.requestEventId).trace.RequestId),
          resources: query.requestEventId === "r1" ? resources : [],
          operations: withAliases ? [{ canonicalId: "call-1", alias: "O1" }] : [],
          evidence: withAliases ? [{ canonicalId: "evidence-1", alias: "E1" }] : [] };
        return query.knownRevision === revision ? { chatId: "chat", revision, unchanged: true } : { chatId: "chat", revision, events: events.filter(e => e.type === "llm.request").slice().reverse() };
      }
      if (hold) await hold;
      const text = type === "inspectModelContextPreview" ? '{"messages":[{"role":"user","content":"draft"}]}' : query.eventId === "response1" ? responseText : requestText;
      const bytes = Buffer.from(text), id = (++leaseCount).toString(16).padStart(64, "0"); leases.set(id, bytes);
      const data = { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 65536,
        payload: { sha256: crypto.createHash("sha256").update(bytes).digest("hex"), byteLength: bytes.length, contentType: "application/json" } };
      return type === "inspectModelContextPreview" ? { chatId: "chat", rawData: data, generatedUtc: "2026-10-02T10:00:00Z" } : { chatId: "chat", eventId: query.eventId, data };
    },
    fetch: async url => { const parsed = new URL(url), bytes = leases.get(parsed.pathname.split("/").pop());
      const offset = Number(parsed.searchParams.get("offset")), count = Number(parsed.searchParams.get("count"));
      const chunk = bytes.subarray(offset, offset + count);
      return new Response(chunk, { headers: { "Content-Type": "application/json",
        "X-RNAssistant-Chunk-SHA256": crypto.createHash("sha256").update(chunk).digest("hex") } }); }
  });
  c.window = c;
  c.RNAssistantIcons = { render: name => '<svg data-icon="' + name + '"></svg>' };
  get("tab-chat").classList.add("active");
  c.RNAssistantChatInspector = { showRequest() {}, hideRequest() {}, setRequestLayout() {}, restoreRequestLayout() {} };
  c.RNAssistantViewerRegistry = { mount(kind, node, options) { mounts.push({ kind, node, options }); }, unmount() {} };
  for (const name of ["app-resource-download.js", "app-model-context.js"]) vm.runInContext(fs.readFileSync("web/js/" + name, "utf8"), c);
  c.bindModelContextActions();
  return { c, get, mounts, calls, closes, requestText, events, timers, verification,
    bump() { revision++; }, hold(value) { hold = value; }, response(value) { responseText = value; },
    async tick() { const entry = Array.from(timers).find(([, t]) => t.ms === 2000); timers.delete(entry[0]); entry[1].fn(); await flush(); }
  };
}
(async () => {
  const workspace = fixture();
  let selectedOwner = "project", selectedWorkspace = "workspace-one", replyWorkspace = "workspace-one";
  workspace.c.RNAssistantWorkspaceChat = { owner: () => selectedOwner,
    snapshot: () => ({ project: { workspaceId: selectedWorkspace }, sessionId: "chat" }) };
  const workspaceSend = workspace.c.send;
  workspace.c.send = async (type, query) => {
    const result = await workspaceSend(type, query);
    if (type.startsWith("getModelContext")) result.workspaceId = replyWorkspace;
    return result;
  };
  const route = { owner: "project", workspaceId: "workspace-one", sessionId: "chat" };
  workspace.c.RNAssistantModelContext.open(); await flush();
  assert.equal(workspace.calls.length, 0, "a bare Office route cannot select a workspace session");
  workspace.c.RNAssistantModelContext.open(route); await flush();
  assert.equal(workspace.get("modelContextAttempt").value, "r1");
  assert.ok(workspace.calls.filter(call => call.type.startsWith("getModelContext")).every(call => call.query.workspaceId === "workspace-one"));
  assert.equal(workspace.closes[0].workspaceId, "model-context:workspace-one", "lease cleanup preserves its workspace owner");
  workspace.get("modelContextMode").value = "preview";
  await workspace.get("modelContextMode").fire("change");
  assert.equal(workspace.get("modelContextMode").value, "actual");
  assert.ok(!workspace.calls.some(call => call.type === "inspectModelContextPreview"));
  workspace.c.RNAssistantModelContext.close();
  let finishWorkspacePayload;
  workspace.hold(new Promise(resolve => { finishWorkspacePayload = resolve; }));
  workspace.c.RNAssistantModelContext.open(route); await flush();
  const workspaceClosed = workspace.closes.length;
  selectedOwner = "plugin"; workspace.c.RNAssistantModelContext.sync();
  assert.equal(workspace.get("modelContextDialog").classList.contains("hidden"), true, "owner change closes even the same workspace/session");
  finishWorkspacePayload(); workspace.hold(null); await flush();
  assert.equal(workspace.closes.length, workspaceClosed + 1, "a late workspace payload releases its lease after owner change");
  assert.equal(workspace.closes.at(-1).workspaceId, "model-context:workspace-one");
  selectedOwner = "project"; workspace.c.RNAssistantModelContext.open(route); await flush();
  selectedWorkspace = "workspace-two"; workspace.c.RNAssistantModelContext.sync();
  assert.equal(workspace.get("modelContextDialog").classList.contains("hidden"), true, "workspace change invalidates the session route");
  selectedWorkspace = "workspace-one"; replyWorkspace = "workspace-two";
  workspace.c.RNAssistantModelContext.open(route); await flush();
  assert.match(workspace.get("modelContextStatus").textContent, /не соответствует выбранному чату/);
  workspace.c.RNAssistantModelContext.close();
  replyWorkspace = null; workspace.c.RNAssistantModelContext.open(route); await flush();
  assert.match(workspace.get("modelContextStatus").textContent, /не соответствует выбранному чату/);
  workspace.c.RNAssistantModelContext.close();
  console.log("PASS model context: exact workspace identity, owner changes, lease cleanup and missing/foreign scope refusal");
  const scoped = fixture();
  scoped.events[0].trace.RunId = "run-1"; scoped.events[0].trace.StepId = "step-1";
  scoped.events.push({ ...scoped.events[0], eventId: "other", sequence: 4,
    trace: { RunId: "run-2", StepId: "step-1", RequestId: "other" } });
  scoped.c.RNAssistantModelContext.open({ chatId: "chat", runId: "run-1", stepId: "step-1" }); await flush();
  assert.equal(scoped.get("modelContextAttempt").children.length, 1);
  assert.equal(scoped.get("modelContextAttempt").value, "r1", "exact run+step never opens a newer unrelated request");
  scoped.c.RNAssistantModelContext.close();
  scoped.c.RNAssistantModelContext.open({ chatId: "chat", runId: "absent", stepId: "step-1" }); await flush();
  assert.match(scoped.get("modelContextStatus").textContent, /Точный запрос этого шага не найден/);
  assert.equal(scoped.get("modelContextAttempt").children.length, 0);
  scoped.c.RNAssistantModelContext.close();
  scoped.c.RNAssistantModelContext.open({ chatId: "foreign", runId: "run-1", stepId: "step-1" });
  assert.equal(scoped.get("modelContextDialog").classList.contains("hidden"), true);
  console.log("PASS model context: exact step navigation and missing/foreign request refusal");
  const history = fixture();
  history.c.state.chatReadOnly = true;
  history.c.RNAssistantModelContext.open(); await flush();
  assert.equal(history.get("modelContextAttempt").value, "r1", "saved requests remain readable in history");
  assert.equal(history.closes.length, 1, "historical payloads remain downloadable");
  assert.equal(history.get("modelContextPreviewOption").disabled, true);
  history.get("modelContextMode").value = "preview";
  await history.get("modelContextMode").fire("change"); await flush();
  await history.get("modelContextRefresh").fire("click"); await flush();
  assert.equal(history.calls.filter(call => call.type === "inspectModelContextPreview").length, 0);
  assert.match(history.get("modelContextStatus").textContent, /создайте новый чат/);
  assert.equal(history.get("modelContextRefresh").disabled, true);
  assert.equal(history.get("modelContextDownload").disabled, true);
  const callsBeforeDownload = history.calls.length;
  await history.get("modelContextDownload").fire("click"); await flush();
  assert.equal(history.calls.length, callsBeforeDownload, "unavailable preview cannot download the last actual request");
  history.get("modelContextMode").value = "actual";
  await history.get("modelContextMode").fire("change"); await flush();
  assert.equal(history.get("modelContextRefresh").disabled, false);
  assert.equal(history.get("modelContextDownload").disabled, false);
  history.c.state.chatReadOnly = false;
  history.c.RNAssistantModelContext.sync();
  assert.equal(history.get("modelContextPreviewOption").disabled, false);
  history.c.RNAssistantModelContext.close();
  console.log("PASS model context: history keeps saved requests and refuses future-request previews locally");

  const streamed = fixture(), saved = []; let chosen = false, committed = false;
  streamed.c.showSaveFilePicker = async () => {
    chosen = streamed.calls.filter(call => call.type === "getModelContextPayload").length === 1;
    return { async createWritable() { return { async write(chunk) { saved.push(Buffer.from(chunk)); },
      async close() { committed = true; }, async abort() { throw new Error("unexpected abort"); } }; } };
  };
  streamed.c.RNAssistantModelContext.open(); await flush();
  const previousCloses = streamed.closes.length;
  await streamed.get("modelContextDownload").fire("click"); await flush();
  for (let attempt = 0; attempt < 100 && !committed && streamed.closes.length === previousCloses; attempt++)
    await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(chosen, true, "file picker starts before the new download lease is opened");
  assert.equal(committed, true, streamed.get("modelContextStatus").textContent);
  assert.equal(Buffer.concat(saved).toString(), streamed.requestText);
  assert.equal(streamed.closes.length, previousCloses + 1, "streamed save closes its one-off lease");
  streamed.c.RNAssistantModelContext.close();
  console.log("PASS model context: file-backed save keeps an exact chunked body and closes its lease");

  const emptyHistory = fixture();
  emptyHistory.events.length = 0;
  emptyHistory.c.state.chatReadOnly = true;
  emptyHistory.c.RNAssistantModelContext.open(); await flush();
  assert.match(emptyHistory.get("modelContextStatus").textContent, /создайте новый чат/);
  emptyHistory.c.RNAssistantModelContext.close();

  const retired = fixture();
  retired.c.RNAssistantModelContext.open(); await flush();
  let releasePreview;
  retired.hold(new Promise(resolve => releasePreview = resolve));
  retired.get("modelContextMode").value = "preview";
  await retired.get("modelContextMode").fire("change"); await flush();
  const mountsBeforeRetiring = retired.mounts.length;
  retired.c.state.chatReadOnly = true;
  retired.c.RNAssistantModelContext.sync();
  releasePreview(); await flush();
  assert.equal(retired.closes.length, 2, "late preview lease is released after a read-only transition");
  assert.equal(retired.mounts.length, mountsBeforeRetiring);
  assert.match(retired.get("modelContextStatus").textContent, /создайте новый чат/);
  assert.equal(retired.get("modelContextDownload").disabled, true);
  retired.c.RNAssistantModelContext.close();
  console.log("PASS model context: empty history and late previews retain the read-only state");

  const f = fixture(), api = f.c.RNAssistantModelContext;
  f.events[0].trace.ContextMessages = [{ MessageIndex: 1, Presentation: "Unknown", ToolCallId: "call-1",
    EvidenceLinks: [{ EvidenceId: "evidence-1", Resource: { uri: "rna://vba/module", revision: "r1" },
      View: "source", ObservedCoverage: { kind: "line-range", start: 1, end: 2 }, BodyIncludedInMessage: null }] },
    { SourceMessageId: "archived", Presentation: "Excluded", ExclusionKind: "Budget",
      Role: "tool", Reason: "Тело источника не вошло в бюджет этого запроса." }];
  const raw = '{"n":9007199254740993123456789,"n":2,"text":"escaped \\\" }, : [ ","nested":[{"x":true}]}';
  const parts = api.members(raw);
  assert.equal(parts[0].raw, "9007199254740993123456789");
  assert.equal(parts[1].key, "n", "duplicate keys retained");
  assert.equal(parts[3].raw, '[{"x":true}]');
  assert.equal(api.members('["a,b",{"nested":[1,2]}]').length, 2);
  api.open(); await flush();
  const userMessage = descendants(f.get("modelContextBody")).find(node => node.dataset.messageIndex === "1");
  userMessage.open = true; await userMessage.fire("toggle");
  const userMetadata = userMessage.children[1].children.find(node => node.className === "model-context-meta");
  assert.ok(userMetadata.children.some(node => /Операция: call-1/.test(node.textContent)),
    "typed operation link appears in request details");
  const userSources = userMessage.children[1].children.find(node => node.dataset.contextSource === "message/1");
  assert.ok(descendants(userSources).some(node => /evidence-1/.test(node.textContent) &&
    /доставка тела в сообщении не установлена/.test(node.textContent)),
    "observed evidence does not claim unproved body delivery");
  assert.ok(descendants(userSources).some(node => /раздельная полнота не сохранены/.test(node.textContent)),
    "old traces do not infer requested selectors or completeness");
  const excluded = descendants(f.get("modelContextBody")).find(node => node.dataset.contextSource === "omissions");
  assert.equal(excluded.children.length, 1, "omission records render only on expansion");
  excluded.open = true; await excluded.fire("toggle");
  assert.ok(descendants(excluded).some(node => /не вошло в бюджет/.test(node.textContent)),
    "inspector shows the compiler's recorded omission reason");
  assert.equal(f.closes.length, 1, "exact download lease closes after reading");
  assert.equal(f.get("modelContextAttempt").value, "r1");
  await f.get("modelContextJsonTab").fire("click"); await flush();
  assert.equal(f.mounts.at(-1).options.text, f.requestText, "large request is not clipped or reserialized");
  const count = f.mounts.length;
  f.bump(); await f.tick();
  assert.equal(f.mounts.length, count, "same request keeps expanded viewers while other events arrive");
  f.events.push({ eventId: "response1", sequence: 6, type: "llm.response", hasPayload: true, createdUtc: "2026-10-02T10:00:30Z", trace: { RequestId: "one" } });
  f.bump(); await f.tick();
  await f.get("modelContextResponseTab").fire("click"); await flush();
  const responseDetails = f.get("modelContextBody").children[0].children.find(n => n.tag === "details");
  responseDetails.open = true; await responseDetails.fire("toggle"); await flush();
  assert.equal(f.mounts.at(-1).kind, "markdown", "response message renders as Markdown independently of envelope");
  assert.equal(f.mounts.at(-1).options.fullText, "# Answer");
  await f.get("modelContextJsonTab").fire("click"); await flush();
  f.events.push({ eventId: "r2", sequence: 9, type: "llm.request", hasPayload: true, createdUtc: "2026-10-02T10:01:00Z", trace: { RequestId: "two" } });
  f.bump(); await f.tick();
  assert.equal(f.get("modelContextAttempt").value, "r2", "follow newest attempt");
  f.get("modelContextAttempt").value = "r1"; await f.get("modelContextAttempt").fire("change"); await flush();
  f.events.push({ eventId: "r3", sequence: 12, type: "llm.request", createdUtc: "2026-10-02T10:02:00Z", trace: { RequestId: "three" } });
  f.bump(); await f.tick(); assert.equal(f.get("modelContextAttempt").value, "r1", "historical selection pauses following");
  f.get("modelContextMode").value = "preview"; await f.get("modelContextMode").fire("change"); await flush();
  assert.match(f.get("modelContextStatus").textContent, /Предпросмотр/);
  f.get("chatInput").value = "changed"; await f.tick(); assert.match(f.get("modelContextStatus").textContent, /устарел/);
  f.c.state.activeChatId = "another"; api.sync(); assert.ok(f.get("modelContextDialog").classList.contains("hidden"));
  const late = fixture(); let release;
  late.hold(new Promise(resolve => release = resolve)); late.c.RNAssistantModelContext.open(); await flush();
  late.c.RNAssistantModelContext.close(); release(); await flush();
  assert.equal(late.closes.length, 1, "late payload acquisition closes its lease even after dialog closed");
  assert.equal(late.mounts.length, 0, "late payload never renders into a closed or other chat");
  console.log("PASS model context: exact JSON, full download, following, preview staleness and cancellation");

  const ambiguous = fixture();
  ambiguous.events[0].trace.ContextMessages = [null,
    { MessageIndex: 0, Presentation: "Full", Reason: "first-source" },
    { MessageIndex: 0, Presentation: "Fragment", Reason: "second-source" },
    { MessageIndex: 99, Presentation: "Full", Reason: "out-of-range-source" },
    { MessageIndex: 1, Presentation: "Unknown", Parts: [{ Kind: "image_url", SourceAttachmentId: "scan",
      Presentation: "Excluded", Reason: "Лимит изображений", OmittedCount: null }] }];
  ambiguous.c.RNAssistantModelContext.open(); await flush();
  const firstMessage = descendants(ambiguous.get("modelContextBody")).find(node => node.dataset.messageIndex === "0");
  firstMessage.open = true; await firstMessage.fire("toggle");
  const ambiguousText = descendants(firstMessage).map(node => node.textContent).join(" ");
  assert.match(ambiguousText, /Неоднозначная привязка/);
  assert.doesNotMatch(ambiguousText, /first-source|second-source|out-of-range-source|Полностью/,
    "duplicate or out-of-range provenance never picks a source or a completeness claim");
  const omittedParts = descendants(ambiguous.get("modelContextBody")).find(node => node.dataset.contextSource === "omissions");
  omittedParts.open = true; await omittedParts.fire("toggle");
  const omissionText = descendants(omittedParts).map(node => node.textContent).join(" ");
  assert.match(omissionText, /Лимит изображений/); assert.match(omissionText, /В составе сообщения 2/);
  assert.doesNotMatch(omissionText, /Не выбрано/, "an unknown omitted-item count is not invented");
  assert.equal(ambiguous.calls.filter(call => call.type === "getModelContextPayload").length, 1,
    "source and omission disclosures use recorded metadata without new body reads");
  ambiguous.c.RNAssistantModelContext.close();
  console.log("PASS model context: missing/ambiguous provenance and attachment omissions stay explicit");

  for (const variant of ["valid", "sum", "duplicate", "missing", "method"]) {
    const cost = fixture();
    const composition = { Version: 1, Method: "base_text_media_v1", HasMedia: false, TotalEstimatedTokens: 30,
      Items: [{ MessageIndex: 0, Group: "InstructionsAndCatalog", EstimatedTokens: 10 },
        { MessageIndex: 1, Group: "Unattributed", EstimatedTokens: 20 }] };
    if (variant === "sum") composition.TotalEstimatedTokens = 99;
    if (variant === "duplicate") composition.Items[1].MessageIndex = 0;
    if (variant === "missing") { composition.Items.pop(); composition.TotalEstimatedTokens = 10; }
    if (variant === "method") composition.Method = "unsupported";
    cost.events[0].trace.RequestComposition = composition;
    cost.events[0].trace.PromptTokens = 999;
    cost.c.RNAssistantModelContext.open(); await flush();
    const nodes = descendants(cost.get("modelContextBody"));
    const bars = nodes.filter(node => node.className === "chat-inspector-composition-chart model-context-composition-chart");
    assert.equal(bars.length, variant === "valid" ? 1 : 0, "only a complete, supported, balanced composition gets a chart: " + variant);
    if (variant === "valid") assert.ok(nodes.some(node => /≈ 30 ток/.test(node.textContent)));
    else assert.ok(nodes.some(node => /Сводка не соответствует/.test(node.textContent)));
    cost.c.RNAssistantModelContext.close();
  }
  console.log("PASS model context: saved composition conservation, schema and message scope");

  const pdfRead = fixture(true, true);
  pdfRead.c.RNAssistantModelContext.open(); await flush();
  await pdfRead.get("modelContextResourcesTab").fire("click"); await flush();
  const pdfDetails = pdfRead.get("modelContextBody").children.find(node => node.tag === "details");
  assert.match(pdfDetails.children[1].children.map(node => node.textContent).join(" "),
    /Профиль извлечения: pdf-page-range-text:content-order-v1 · Страниц в оригинале: 12/);
  assert.match(pdfDetails.children[1].children.map(node => node.textContent).join(" "),
    /Сохранённое view binary:render-page:1 · покрытие неизвестно/);
  pdfRead.c.RNAssistantModelContext.close();

  const linked = fixture(true);
  linked.events[0].alias = "Q1";
  linked.events[0].trace.ContextMessages = [{ MessageIndex: 0, Presentation: "Full", ToolCallId: "call-1",
    EstimatedWireMessageTokens: 72,
    EvidenceLinks: [{ EvidenceId: "evidence-1", ScopeId: { kind: "conversation", id: "chat" },
      Resource: { uri: "rna://vba/module", revision: "r1" }, View: "source",
      ObservedCoverage: { kind: "line-range", start: 1, end: 2 }, BodyIncludedInMessage: null }] }];
  linked.c.RNAssistantModelContext.open(); await flush();
  assert.match(linked.get("modelContextAttempt").children[0].textContent, /Q1/);
  await linked.get("modelContextResourcesTab").fire("click"); await flush();
  let resource = linked.get("modelContextBody").children.find(node => node.tag === "details");
  assert.match(resource.children[0].textContent, /R1@v1 · Устаревшая/);
  assert.match(resource.children[1].children.map(node => node.textContent).join(" "),
    /Q1 · O1 · E1 · source.*частичное наблюдение.*доставка неизвестна/);
  const resourceText = () => linked.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.map(node => node.textContent).join(" ");
  assert.match(resourceText(), /Оценка всего сериализованного read-сообщения для этого view: ≈ 72 токенов/);
  assert.match(resourceText(), /Оценка сериализованного read-сообщения для этого view неизвестна/);
  const checkButton = f => f.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.find(node => node.tag === "button" && node.textContent === "Проверить сохранённое тело");
  const viewButton = f => f.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.find(node => node.tag === "button" && node.textContent === "Проверить view source");
  const inventoryButton = (f, label) => f.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.find(node => node.tag === "button" && node.textContent === label);
  const variantButton = (f, ordinal) => f.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.find(node => node.tag === "button" && node.textContent === "Проверить вариант #" + ordinal);
  assert.match(resourceText(), /Предыдущая ревизия:.*prior.*Восстановлена из:.*original.*Зависимость:.*dependency/);
  assert.match(resourceText(), /1234 байт.*text\/plain.*payload-hash.*сохранённое тело не проверено/);
  assert.match(resourceText(), /Запрошено:.*"end":6.*Покрытие в источнике:.*"end":2/);
  assert.match(resourceText(), /Выбранный фрагмент: полностью.*Представление: не полностью.*Источник: неизвестно/);
  assert.match(resourceText(), /Заявлено провайдером при чтении: провайдер vba; тип module; MIME text\/plain; 1234 байт; представления source, outline; операции read/);
  assert.match(resourceText(), /параметры views source \(смещение, поток, байт за раз ≤ 65536\); текущая доступность не проверена; возможность чтения моделью не подтверждена/);
  assert.match(resourceText(), /Сохранённое view source · покрытие \{"kind":"whole"\} · оценка текста неизвестна · тело не проверено · частей: 2 · части не проверены/);
  assert.match(resourceText(), /Запрошено: процедура Value \(property-get\)/);
  assert.match(resourceText(), /JSON Pointer "\/data", глубина 2, дети \[1, 3\)/);
  assert.match(resourceText(), /YAML pointer "\/config", глубина 1, дети \[0, 2\)/);
  assert.equal(linked.calls.filter(call => call.query.verifyResourceAlias).length, 0, "opening resources does not start expensive verification");
  assert.equal(linked.calls.filter(call => call.query.viewInventoryAlias).length, 0, "inventory is loaded on demand");
  await inventoryButton(linked, "Показать сохранённые views").fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.viewInventoryOffset, 0);
  assert.match(resourceText(), /Сохранённые views: 1–2 из 3/);
  assert.match(resourceText(), /#2 · source · покрытие.*character-range.*start.*10.*end.*80/);
  assert.match(resourceText(), /#2 · source.*оценка текста ≈ 5 токенов \(по метаданным, UTF-8 байты\/4; без обвязки запроса\)/);
  assert.match(resourceText(), /#1 · source.*оценка текста неизвестна/);
  assert.equal(linked.calls.filter(call => call.query.verifyResourceAlias).length, 0,
    "inventory metadata does not trigger CAS verification");
  await variantButton(linked, 2).fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.verifyViewVariantOrdinal, 1);
  assert.equal(linked.calls.at(-1).query.verifyViewVariantSnapshotCount, 3);
  assert.match(resourceText(), /#2 · source.*хеш и длина тела проверены/);
  assert.match(resourceText(), /сохранённое тело не проверено/,
    "variant verification does not claim to check the revision payload");
  await variantButton(linked, 1).fire("click"); await flush();
  assert.match(resourceText(), /#1 · source.*все части проверены/);
  let releaseVariant;
  linked.verification.hold = new Promise(resolve => releaseVariant = resolve);
  const lateVariant = variantButton(linked, 2).fire("click"); await flush();
  await inventoryButton(linked, "Следующая страница views").fire("click"); await flush();
  releaseVariant(); await lateVariant; await flush();
  linked.verification.hold = null;
  assert.equal(linked.calls.at(-1).query.viewInventoryOffset, 2);
  assert.equal(linked.calls.at(-1).query.viewInventorySnapshotCount, 3);
  assert.match(resourceText(), /Сохранённые views: 3–3 из 3.*#3 · internal/);
  assert.doesNotMatch(resourceText(), /#2 · source/, "older pages leave the DOM");
  assert.equal(variantButton(linked, 3), undefined, "metadata-only variant has no CAS-check action");
  assert.equal(inventoryButton(linked, "Следующая страница views"), undefined,
    "complete inventory has no forward continuation");
  await inventoryButton(linked, "Предыдущая страница views").fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.viewInventoryOffset, 0);
  assert.equal(linked.calls.at(-1).query.viewInventorySnapshotCount, 3);
  assert.match(resourceText(), /Сохранённые views: 1–2 из 3/);
  assert.match(resourceText(), /#2 · source.*байты не проверены/,
    "a late check from a prior page cannot mark the revisited page as verified");
  await checkButton(linked).fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.verifyResourceAlias, "R1@v1");
  assert.match(resourceText(), /хеш и длина сохранённого тела проверены/);
  assert.match(resourceText(), /Сохранённое view source.*части не проверены/,
    "revision check does not claim view-part availability");
  await viewButton(linked).fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.verifyRetainedView, "source");
  assert.match(resourceText(), /Сохранённое view source.*хеш и длина тела проверены.*хеши и длины всех частей проверены/);
  assert.match(resourceText(), /доставка неизвестна/, "CAS validity never manufactures model delivery");
  await linked.get("modelContextRefresh").fire("click"); await flush();
  assert.match(resourceText(), /сохранённое тело не проверено/, "refresh does not leave a stale verification label on screen");
  assert.doesNotMatch(resourceText(), /Сохранённые views: 1–2 из 3/, "refresh starts a new inventory snapshot");
  linked.verification.availability = "Unavailable";
  linked.verification.partsAvailability = "Unavailable";
  await checkButton(linked).fire("click"); await flush();
  assert.match(resourceText(), /отсутствует, повреждено или недоступно/);
  await viewButton(linked).fire("click"); await flush();
  assert.match(resourceText(), /одна или более частей отсутствует, повреждена или недоступна/);
  linked.get("modelContextAliasSearch").value = "E1";
  await linked.get("modelContextAliasFind").fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.alias, "E1", "search resolves through the typed bridge");
  assert.equal(linked.get("modelContextResourcesTab").classList.contains("active"), true);
  resource = linked.get("modelContextBody").children.find(node => node.tag === "details");
  assert.equal(resource.open, true, "evidence search focuses the linked resource revision");
  assert.match(linked.get("modelContextStatus").textContent, /Найдено E1 в Q1/);
  linked.get("modelContextAliasSearch").value = "R1@v999";
  await linked.get("modelContextAliasFind").fire("click"); await flush();
  assert.match(linked.get("modelContextStatus").textContent, /RESOURCE_NOT_FOUND/);
  console.log("PASS model context: stable aliases, resource evidence and scoped lookup");

  const leaving = fixture(true);
  leaving.c.RNAssistantModelContext.open(); await flush();
  await leaving.get("modelContextResourcesTab").fire("click"); await flush();
  let finishVerification;
  leaving.verification.hold = new Promise(resolve => finishVerification = resolve);
  const checking = checkButton(leaving).fire("click"); await flush();
  leaving.c.state.activeChatId = "other-chat"; leaving.c.RNAssistantModelContext.sync();
  finishVerification(); await checking; await flush();
  assert.equal(leaving.get("modelContextBody").children.length, 0, "late verification cannot render into another chat");
  assert.ok(leaving.get("modelContextDialog").classList.contains("hidden"));
  console.log("PASS model context: explicit payload verification, refresh and late-response isolation");

  const switchingTab = fixture(true); let finishPayload;
  switchingTab.hold(new Promise(resolve => finishPayload = resolve));
  switchingTab.c.RNAssistantModelContext.open(); await flush();
  await switchingTab.get("modelContextResourcesTab").fire("click"); await flush();
  const resourceRows = switchingTab.get("modelContextBody").children.slice();
  finishPayload(); await flush();
  assert.equal(switchingTab.get("modelContextBody").children.length === resourceRows.length &&
    resourceRows.every((node, index) => switchingTab.get("modelContextBody").children[index] === node), true,
    "late message payload cannot replace the selected Resources tab");
  assert.equal(switchingTab.closes.length, 1, "the superseded view still closes its payload lease");
  switchingTab.c.RNAssistantModelContext.close();
  console.log("PASS model context: late payload respects the selected tab");

  const refreshed = fixture();
  refreshed.c.RNAssistantModelContext.open(); await flush();
  await refreshed.get("modelContextRefresh").fire("click"); await flush();
  const parameter = descendants(refreshed.get("modelContextBody")).find(node => node.tag === "details" &&
    node.children.some(child => child.textContent === "Параметр запроса: seed"));
  parameter.open = true; await parameter.fire("toggle"); await flush();
  assert.equal(refreshed.mounts.some(mount => mount.options.text === "9007199254740993123456789"), true,
    "refresh keeps unopened lazy bodies readable and exact");
  assert.equal(refreshed.calls.filter(call => call.type === "getModelContextPayload").length, 1,
    "refresh reuses the selected immutable request payload");
  refreshed.c.RNAssistantModelContext.close();

  const changingMode = fixture();
  changingMode.events[0].trace.RunId = "run"; changingMode.events[0].trace.StepId = "step";
  const modeSend = changingMode.c.send; let rejectOldList, heldList = false;
  changingMode.c.send = (type, query) => {
    if (!heldList && type === "getModelContext" && !query.requestEventId) {
      heldList = true; return new Promise((resolve, reject) => { rejectOldList = reject; });
    }
    return modeSend(type, query);
  };
  changingMode.c.RNAssistantModelContext.open({ chatId: "chat", runId: "run", stepId: "step" }); await flush();
  changingMode.get("modelContextMode").value = "preview";
  await changingMode.get("modelContextMode").fire("change"); await flush();
  changingMode.get("modelContextMode").value = "actual";
  await changingMode.get("modelContextMode").fire("change"); await flush();
  assert.equal(changingMode.get("modelContextAttempt").value, "r1",
    "an obsolete pending list cannot block returning to a scoped request");
  const currentStatus = changingMode.get("modelContextStatus").textContent;
  rejectOldList(new Error("old-list-failed")); await flush();
  assert.equal(changingMode.get("modelContextStatus").textContent, currentStatus, "obsolete list errors stay out of the current view");
  changingMode.c.RNAssistantModelContext.close();

  const failedSelection = fixture();
  failedSelection.c.RNAssistantModelContext.open(); await flush();
  const oldContent = failedSelection.get("modelContextBody").children[0], selectionSend = failedSelection.c.send;
  failedSelection.c.send = (type, query) => type === "getModelContext" && query.requestEventId === "r2"
    ? Promise.reject(new Error("selected-request-missing")) : selectionSend(type, query);
  failedSelection.events.push({ ...failedSelection.events[0], eventId: "r2", sequence: 4, trace: { RequestId: "two" } });
  failedSelection.bump(); await failedSelection.tick();
  assert.equal(failedSelection.get("modelContextAttempt").value, "r2");
  assert.equal(failedSelection.get("modelContextBody").children.includes(oldContent), false,
    "the failed selection never displays the old request beneath the new ID");
  assert.equal(failedSelection.get("modelContextDownload").disabled, true);
  assert.match(failedSelection.get("modelContextStatus").textContent, /selected-request-missing/);
  failedSelection.c.RNAssistantModelContext.close();

  const cancelledSave = fixture(); let cancelPicker;
  cancelledSave.c.RNAssistantModelContext.open(); await flush();
  cancelledSave.c.showSaveFilePicker = () => new Promise((resolve, reject) => { cancelPicker = reject; });
  await cancelledSave.get("modelContextDownload").fire("click"); await flush();
  cancelledSave.get("modelContextMode").value = "preview";
  await cancelledSave.get("modelContextMode").fire("change"); await flush();
  const previewStatus = cancelledSave.get("modelContextStatus").textContent;
  cancelPicker(new Error("old-export-cancelled")); await flush();
  assert.equal(cancelledSave.get("modelContextStatus").textContent, previewStatus,
    "cancelling an old save dialog cannot replace the new preview status");
  cancelledSave.c.RNAssistantModelContext.close();
  console.log("PASS model context: refresh disclosures, mode-change races, stale errors and failed request selection");

  for (const action of ["execute", "reflection", "finish"]) {
    const wrapped = action !== "execute", decision = fixture(), inner = action === "finish"
      ? { action: "finish", reason: "Исходные данные неполны.", results: [{ summary: "Подсчитаны только доступные строки.",
        assessment: "partially_satisfied", evidence: ["call-1"] }], message: "" }
      : action === "reflection"
      ? { action: "reflection", reason: "Нужно уточнить диапазон.", decision: "Проверить заголовки таблицы.", message: "" }
      : { action: "execute", reason: "Нужно прочитать таблицу.", tool_calls: [{ name: "common.resources_read", arguments: {} }], message: "" };
    const source = JSON.stringify({ Content: JSON.stringify(wrapped ? { response: inner } : inner) });
    decision.response(source);
    decision.events.push({ eventId: "response1", sequence: 6, type: "llm.response", hasPayload: true,
      createdUtc: "2026-10-02T10:00:30Z", trace: { RequestId: "one" } });
    decision.c.RNAssistantModelContext.open(); await flush();
    await decision.get("modelContextResponseTab").fire("click");
    const content = decision.get("modelContextBody").children[0].children.find(node => node.tag === "details");
    assert.equal(content.open, true, "saved response opens without another disclosure click");
    await content.fire("toggle"); await flush();
    const nodes = descendants(content), info = nodes.find(node => node.className === "model-context-decision");
    assert.equal(info.open, true, "an empty v8 message exposes its recorded action and decision basis");
    assert.ok(descendants(info).some(node => node.textContent === inner.reason));
    assert.ok(descendants(info).some(node => node.textContent === (action === "finish" ? inner.results[0].summary :
      action === "reflection" ? inner.decision : "common.resources_read")));
    if (action === "finish") assert.ok(descendants(info).some(node => /Оценка модели: выполнено частично/.test(node.textContent)));
    assert.equal(decision.mounts.some(mount => mount.kind === "markdown" && !mount.options.text), false);
    const raw = nodes.find(node => node.tag === "details" && node.children[0]?.textContent === "Полный ответ · исходный JSON");
    raw.open = true; await raw.fire("toggle"); await flush();
    assert.equal(decision.mounts.at(-1).options.text, source, "readable v8 projection preserves the exact transport");
    decision.c.RNAssistantModelContext.close();
  }
  console.log("PASS model context: readable v8 empty messages, strict wrapper and exact raw response");
})().catch(error => { console.error(error); process.exitCode = 1; });
