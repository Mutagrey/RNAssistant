"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const crypto = require("node:crypto");
class Node {
  constructor(tag = "div") { this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {}; this.value = ""; this.isConnected = true; this.classes = new Set(); this.classList = {
    add: x => this.classes.add(x), remove: x => this.classes.delete(x), contains: x => this.classes.has(x),
    toggle: (x, force) => force ? this.classes.add(x) : this.classes.delete(x)
  }; }
  set textContent(value) { this.text = value; this.children = []; }
  get textContent() { return this.text || ""; }
  appendChild(node) { this.children.push(node); node.parent = this; return node; }
  replaceChildren() { this.children.forEach(n => n.isConnected = false); this.children = []; this.text = ""; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(n => n !== this); this.isConnected = false; }
  setAttribute() {}
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  fire(type) { return Promise.all((this.listeners[type] || []).map(fn => fn.call(this, { key: "", preventDefault() {}, stopPropagation() {} }))); }
  focus() {}
  querySelectorAll() { return []; }
}
async function flush() { for (let i = 0; i < 25; i++) await new Promise(resolve => setImmediate(resolve)); }
function fixture(withAliases = false) {
  const nodes = new Map(), mounts = [], calls = [], closes = [], timers = new Map(), leases = new Map(); let timer = 0;
  const verification = { availability: "Verified", hold: null };
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const requestText = '{"model":"test","messages":[{"role":"system","content":"rules"},{"role":"user","content":"' + "語😀".repeat(110000) + '"}],"seed":9007199254740993123456789}';
  const events = [{ eventId: "r1", sequence: 3, createdUtc: "2026-10-02T10:00:00Z", type: "llm.request", hasPayload: true, trace: { RequestId: "one", Model: "test", Streaming: true } }];
  let revision = 3, hold = null, leaseCount = 0;
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
          observations: [{ requestAlias: "Q1", operationAlias: "O1", evidenceAlias: "E1", view: "source",
            coverage: { kind: "line-range", start: 1, end: 2 }, observationComplete: false, bodyIncludedInMessage: null,
            readMetadata: { requestedCoverage: { kind: "line-range", start: 1, end: 6 },
              sourceCoverage: { kind: "line-range", start: 1, end: 2 }, selectionComplete: true,
              representationComplete: false, sourceComplete: null } },
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
        if (query.verifyResourceAlias) {
          if (verification.hold) await verification.hold;
          assert.equal(query.requestEventId, "r1");
          assert.equal(query.verifyResourceAlias, "R1@v1");
          resources[0].payloadAvailability = verification.availability;
          resources[0].payloadCheckedUtc = "2026-10-02T11:00:00Z";
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
      const text = type === "inspectModelContextPreview" ? '{"messages":[{"role":"user","content":"draft"}]}' : query.eventId === "response1" ? '{"Content":"# Answer"}' : requestText;
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
  c.RNAssistantViewerRegistry = { mount(kind, node, options) { mounts.push({ kind, node, options }); }, unmount() {} };
  for (const name of ["app-resource-download.js", "app-model-context.js"]) vm.runInContext(fs.readFileSync("web/js/" + name, "utf8"), c);
  c.bindModelContextActions();
  return { c, get, mounts, calls, closes, requestText, events, timers, verification,
    bump() { revision++; }, hold(value) { hold = value; },
    async tick() { const entry = Array.from(timers).find(([, t]) => t.ms === 2000); timers.delete(entry[0]); entry[1].fn(); await flush(); }
  };
}
(async () => {
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
  const userMessage = f.get("modelContextBody").children[0].children.filter(node => node.tag === "details")[1];
  userMessage.open = true; await userMessage.fire("toggle");
  assert.ok(userMessage.children[1].children.some(node => /Операция: call-1/.test(node.textContent)),
    "typed operation link appears in request details");
  assert.ok(userMessage.children[1].children.some(node => /evidence-1/.test(node.textContent) &&
    /доставка тела в сообщении не установлена/.test(node.textContent)),
    "observed evidence does not claim unproved body delivery");
  assert.ok(userMessage.children[1].children.some(node => /раздельная полнота не сохранены/.test(node.textContent)),
    "old traces do not infer requested selectors or completeness");
  const excluded = f.get("modelContextBody").children.find(node => node.tag === "details" &&
    node.children.some(child => /Не включено отдельными сообщениями/.test(child.textContent)));
  assert.ok(excluded && excluded.children.some(node => node.children.some(child =>
    /не вошло в бюджет/.test(child.textContent))), "inspector shows the compiler's recorded omission reason");
  assert.equal(f.closes.length, 1, "exact download lease closes after reading");
  assert.equal(f.get("modelContextAttempt").value, "r1");
  f.get("modelContextTab").value = "json"; await f.get("modelContextTab").fire("change"); await flush();
  assert.equal(f.mounts.at(-1).options.text, f.requestText, "large request is not clipped or reserialized");
  const count = f.mounts.length;
  f.bump(); await f.tick();
  assert.equal(f.mounts.length, count, "same request keeps expanded viewers while other events arrive");
  f.events.push({ eventId: "response1", sequence: 6, type: "llm.response", hasPayload: true, createdUtc: "2026-10-02T10:00:30Z", trace: { RequestId: "one" } });
  f.bump(); await f.tick();
  f.get("modelContextTab").value = "response"; await f.get("modelContextTab").fire("change"); await flush();
  const responseDetails = f.get("modelContextBody").children[0].children.find(n => n.tag === "details");
  responseDetails.open = true; await responseDetails.fire("toggle"); await flush();
  assert.equal(f.mounts.at(-1).kind, "markdown", "response message renders as Markdown independently of envelope");
  assert.equal(f.mounts.at(-1).options.fullText, "# Answer");
  f.get("modelContextTab").value = "json"; await f.get("modelContextTab").fire("change"); await flush();
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

  const linked = fixture(true);
  linked.events[0].alias = "Q1";
  linked.events[0].trace.ContextMessages = [{ MessageIndex: 0, Presentation: "Full", ToolCallId: "call-1",
    EvidenceLinks: [{ EvidenceId: "evidence-1", ScopeId: { kind: "conversation", id: "chat" },
      Resource: { uri: "rna://vba/module", revision: "r1" }, View: "source",
      ObservedCoverage: { kind: "line-range", start: 1, end: 2 }, BodyIncludedInMessage: null }] }];
  linked.c.RNAssistantModelContext.open(); await flush();
  assert.match(linked.get("modelContextAttempt").children[0].textContent, /Q1/);
  linked.get("modelContextTab").value = "resources";
  await linked.get("modelContextTab").fire("change"); await flush();
  let resource = linked.get("modelContextBody").children.find(node => node.tag === "details");
  assert.match(resource.children[0].textContent, /R1@v1 · Устаревшая/);
  assert.match(resource.children[1].children.map(node => node.textContent).join(" "),
    /Q1 · O1 · E1 · source.*частичное наблюдение.*доставка неизвестна/);
  const resourceText = () => linked.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.map(node => node.textContent).join(" ");
  const checkButton = f => f.get("modelContextBody").children.find(node => node.tag === "details")
    .children[1].children.find(node => node.tag === "button" && node.textContent === "Проверить сохранённое тело");
  assert.match(resourceText(), /Предыдущая ревизия:.*prior.*Восстановлена из:.*original.*Зависимость:.*dependency/);
  assert.match(resourceText(), /1234 байт.*text\/plain.*payload-hash.*сохранённое тело не проверено/);
  assert.match(resourceText(), /Запрошено:.*"end":6.*Покрытие в источнике:.*"end":2/);
  assert.match(resourceText(), /Выбранный фрагмент: полностью.*Представление: не полностью.*Источник: неизвестно/);
  assert.match(resourceText(), /Запрошено: процедура Value \(property-get\)/);
  assert.match(resourceText(), /JSON Pointer "\/data", глубина 2, дети \[1, 3\)/);
  assert.match(resourceText(), /YAML pointer "\/config", глубина 1, дети \[0, 2\)/);
  assert.equal(linked.calls.filter(call => call.query.verifyResourceAlias).length, 0, "opening resources does not start expensive verification");
  await checkButton(linked).fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.verifyResourceAlias, "R1@v1");
  assert.match(resourceText(), /хеш и длина сохранённого тела проверены/);
  assert.match(resourceText(), /доставка неизвестна/, "CAS validity never manufactures model delivery");
  await linked.get("modelContextRefresh").fire("click"); await flush();
  assert.match(resourceText(), /сохранённое тело не проверено/, "refresh does not leave a stale verification label on screen");
  linked.verification.availability = "Unavailable";
  await checkButton(linked).fire("click"); await flush();
  assert.match(resourceText(), /отсутствует, повреждено или недоступно/);
  linked.get("modelContextAliasSearch").value = "E1";
  await linked.get("modelContextAliasFind").fire("click"); await flush();
  assert.equal(linked.calls.at(-1).query.alias, "E1", "search resolves through the typed bridge");
  assert.equal(linked.get("modelContextTab").value, "resources");
  resource = linked.get("modelContextBody").children.find(node => node.tag === "details");
  assert.equal(resource.open, true, "evidence search focuses the linked resource revision");
  assert.match(linked.get("modelContextStatus").textContent, /Найдено E1 в Q1/);
  linked.get("modelContextAliasSearch").value = "R1@v999";
  await linked.get("modelContextAliasFind").fire("click"); await flush();
  assert.match(linked.get("modelContextStatus").textContent, /RESOURCE_NOT_FOUND/);
  console.log("PASS model context: stable aliases, resource evidence and scoped lookup");

  const leaving = fixture(true);
  leaving.c.RNAssistantModelContext.open(); await flush();
  leaving.get("modelContextTab").value = "resources";
  await leaving.get("modelContextTab").fire("change"); await flush();
  let finishVerification;
  leaving.verification.hold = new Promise(resolve => finishVerification = resolve);
  const checking = checkButton(leaving).fire("click"); await flush();
  leaving.c.state.activeChatId = "other-chat"; leaving.c.RNAssistantModelContext.sync();
  finishVerification(); await checking; await flush();
  assert.equal(leaving.get("modelContextBody").children.length, 0, "late verification cannot render into another chat");
  assert.ok(leaving.get("modelContextDialog").classList.contains("hidden"));
  console.log("PASS model context: explicit payload verification, refresh and late-response isolation");
})().catch(error => { console.error(error); process.exitCode = 1; });
