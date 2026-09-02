"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class ClassList {
  constructor(owner) { this.owner = owner; }
  values() { return new Set(String(this.owner.className || "").split(/\s+/).filter(Boolean)); }
  write(values) { this.owner.className = Array.from(values).join(" "); }
  add(...names) { const values = this.values(); names.forEach(name => values.add(name)); this.write(values); }
  remove(...names) { const values = this.values(); names.forEach(name => values.delete(name)); this.write(values); }
  toggle(name, force) { const values = this.values(); const enabled = force === undefined ? !values.has(name) : !!force; if (enabled) values.add(name); else values.delete(name); this.write(values); return enabled; }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.handlers = {}; this.attributes = {};
    this.disabled = false; this.value = ""; this._text = ""; this._html = ""; this.style = {};
    this.clientWidth = 0; this.clientHeight = 0; this.scrollLeft = 0; this.scrollTop = 0;
    this.naturalWidth = tag === "img" ? 640 : 0; this.naturalHeight = tag === "img" ? 480 : 0;
  }
  get firstElementChild() { return this.childNodes[0] || null; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); this._text = ""; this._html = ""; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name, event = {}) { (this.handlers[name] || []).forEach(handler => handler(event)); }
  click() { if (!this.disabled) (this.handlers.click || []).forEach(handler => handler({})); }
  remove() { if (this.parentNode) this.parentNode.removeChild(this); }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; return child; }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".") ? node.classList.contains(selector.slice(1)) : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.childNodes = []; this._html = ""; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
  set innerHTML(value) { this._html = String(value); this._text = ""; this.childNodes = []; }
  get innerHTML() { return this._html; }
}

const root = path.join(__dirname, "../..");
const copied = [];
const downloads = [];
const objectUrls = [];
const revokedUrls = [];
const vendorViewerInstances = [];
const body = new Element("body");
const context = vm.createContext({
  document: { body, createElement: tag => new Element(tag) },
  state: {},
  Promise,
  Blob,
  Uint8Array,
  URL: {
    createObjectURL() { const url = "blob:test-" + (objectUrls.length + 1); objectUrls.push(url); return url; },
    revokeObjectURL(url) { revokedUrls.push(url); }
  }
});
context.window = context;
context.atob = value => Buffer.from(String(value), "base64").toString("binary");
context.addEventListener = () => {};
context.removeEventListener = () => {};
context.copyTextResult = text => { copied.push(String(text)); return Promise.resolve(); };
context.markdown = text => "<p>" + String(text).replace(/</g, "&lt;") + "</p>";
context.enhanceMarkdown = node => { node.attributes.enhanced = "true"; };
context.clearMarkdownEnhancements = () => {};
context.Viewer = class ViewerStub {
  constructor(image, options) {
    this.image = image; this.options = options; this.calls = []; this.destroyed = false;
    this.imageData = { ratio: 1 };
    vendorViewerInstances.push(this);
  }
  zoom(value, tooltip) { this.calls.push(["zoom", value, tooltip]); }
  zoomTo(value, tooltip) { this.calls.push(["zoomTo", value, tooltip]); }
  reset() { this.calls.push(["reset"]); }
  destroy() { this.destroyed = true; }
};
for (const file of ["app-viewer-registry.js", "app-text-viewer.js", "app-resource-viewer.js", "app-html-workspace-artifacts.js"]) {
  vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), context, { filename: file });
}

function button(node, label) {
  return node.querySelectorAll("button").find(item => item.textContent === label);
}
function settle() { return new Promise(resolve => setImmediate(resolve)); }

(async function () {
  const hostile = "first\n<script>window.run()</script>";
  let nextCount = 0;
  const partialHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("text", partialHost, {
    text: hostile,
    offset: 32000,
    startLine: 8,
    totalCharacters: 50000,
    sourceComplete: true,
    fullReadAllowed: true,
    hasNext: true,
    onNext() { nextCount += 1; },
    onLoadFull() {},
    onCopy: context.copyTextResult
  });
  assert.equal(partialHost.querySelector(".rn-text-viewer-content").textContent, hostile);
  assert.equal(partialHost.querySelector(".rn-text-viewer-lines").textContent, "8\n9");
  assert.match(partialHost.textContent, /Страница 32001/);
  assert.equal(button(partialHost, "Скачать"), undefined);
  button(partialHost, "Копировать страницу").click();
  button(partialHost, "→").click();
  await settle();
  assert.equal(copied.at(-1), hostile);
  assert.equal(nextCount, 1);
  const search = partialHost.querySelector(".rn-text-viewer-search");
  search.value = "script";
  button(partialHost, "Найти").click();
  assert.match(partialHost.textContent, /Совпадений: 2/);
  console.log("PASS artifact text viewer: bounded page is inert, numbered, searchable and page-copy only");

  const fullHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("text", fullHost, {
    text: hostile,
    fullText: hostile,
    complete: true,
    sourceComplete: true,
    onCopy: context.copyTextResult,
    onDownload(text) { downloads.push(text); }
  });
  button(fullHost, "Скачать").click();
  await settle();
  assert.equal(downloads[0], hostile);
  assert.match(fullHost.textContent, /Полный exact source/);
  console.log("PASS artifact text viewer: full copy/download appears only for a complete exact read");

  const markdownHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("markdown", markdownHost, {
    text: "# Safe\n<script>x</script>",
    fullText: "# Safe\n<script>x</script>",
    complete: true,
    sourceComplete: true,
    onCopy: context.copyTextResult
  });
  const rendered = markdownHost.querySelector(".rn-markdown-viewer-rendered");
  assert.ok(rendered);
  assert.match(rendered.innerHTML, /&lt;script>/);
  button(markdownHost, "Источник").click();
  assert.equal(markdownHost.querySelector(".rn-text-viewer-content").textContent, "# Safe\n<script>x</script>");
  console.log("PASS artifact Markdown viewer: sanitized rendered view and exact Source share one controller");

  const incompleteMarkdown = new Element("div");
  context.RNAssistantViewerRegistry.mount("markdown", incompleteMarkdown, {
    text: "# Partial",
    complete: false,
    sourceComplete: false,
    fullReadAllowed: false,
    onCopy: context.copyTextResult
  });
  assert.equal(incompleteMarkdown.querySelector(".rn-markdown-viewer-rendered"), null);
  assert.match(incompleteMarkdown.textContent, /preview отключён/);
  assert.equal(button(incompleteMarkdown, "Скачать"), undefined);
  console.log("PASS artifact Markdown viewer: truncated source never becomes rendered or full-download authority");

  const imageHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("image", imageHost, {
    title: "pixel.png", mimeType: "image/png", byteLength: 3, base64Content: "AQID"
  });
  const image = imageHost.querySelector("img");
  const imageStage = imageHost.querySelector(".rn-image-viewer-stage-shell");
  image.dispatch("load");
  const imageVendor = vendorViewerInstances.at(-1);
  assert.match(imageHost.textContent, /640 × 480 px/);
  assert.equal(image.classList.contains("rn-viewerjs-source"), true);
  assert.equal(button(imageHost, "+"), undefined);
  assert.equal(imageVendor.options.inline, true);
  assert.equal(imageVendor.options.initialCoverage, 1);
  assert.equal(imageVendor.options.zoomOnWheel, true);
  assert.equal(imageVendor.options.zoomOnTouch, true);
  let zoomKeyPrevented = false;
  imageStage.dispatch("keydown", { key: "+", preventDefault() { zoomKeyPrevented = true; }, stopPropagation() {} });
  assert.equal(zoomKeyPrevented, true);
  assert.deepEqual(imageVendor.calls.at(-1), ["zoom", 0.15, true]);
  imageVendor.options.zoomed({ detail: { ratio: 1.25 } });
  assert.match(imageHost.textContent, /125%/);
  button(imageHost, "Скачать").click();
  assert.equal(body.querySelector("a"), null);
  context.RNAssistantViewerRegistry.unmount(imageHost);
  assert.equal(imageVendor.destroyed, true);
  assert.deepEqual(revokedUrls, [objectUrls[0]]);
  const imageUri = "rna://chat/chat-text/artifact/image/revision/1";
  const detail = new Element("div");
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(detail, {
    type: "artifact",
    item: { Kind: "image", Title: "pixel.png", MimeType: "image/png", ResourceUri: imageUri, Revision: 1 }
  }, "", {
    artifactViewerState() {
      return {
        status: "ready", resourceUri: imageUri, viewerKind: "image", title: "pixel.png",
        mimeType: "image/png", contentSha256: "d".repeat(64), byteLength: 3, base64Content: "AQID"
      };
    }
  });
  assert.ok(detail.querySelector(".artifact-detail-pane-preview").querySelector("img"));
  assert.equal(detail.classList.contains("is-image-preview"), true);
  assert.equal(detail.classList.contains("is-media-preview"), true);
  const resourceViewerCss = fs.readFileSync(path.join(root, "web/css/app-resource-viewer.css"), "utf8");
  assert.match(resourceViewerCss, /\.artifact-detail-preview\.is-media-preview\s*\{[^}]*grid-template-rows:\s*auto minmax\(0,\s*1fr\)/s);
  assert.match(resourceViewerCss, /\.rn-vendor-image-viewer\.viewer-container/);
  assert.match(resourceViewerCss, /\.rn-viewerjs-source\s*\{[^}]*display:\s*none/s);
  assert.match(resourceViewerCss, /\.rn-image-viewer-stage-shell:hover \.rn-preview-nav:not\(:disabled\)/);
  assert.match(resourceViewerCss, /\.rn-pdf-pages-layout\s*\{[^}]*grid-template-columns:\s*150px minmax\(0,\s*1fr\)/s);
  assert.match(resourceViewerCss, /\.rn-pdf-thumbnail-item\s*\{[^}]*position:\s*absolute/s);
  assert.ok(detail.querySelector(".artifact-detail-pane-details").classList.contains("hidden"));
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(detail, {
    type: "artifact", item: { Kind: "file", Title: "unknown.bin", MimeType: "application/octet-stream", Revision: 1 }
  }, "", {});
  assert.equal(detail.classList.contains("is-image-preview"), false);
  assert.equal(detail.classList.contains("is-media-preview"), false);
  assert.deepEqual(revokedUrls, [objectUrls[0], objectUrls[1]]);
  console.log("PASS artifact image viewer: local Blob preview supports dimensions, zoom, download and URL revocation");

  let nextPdfPage = 0;
  let selectedPdfPage = -1;
  const requestedPdfThumbnails = [];
  let activePdfTab = "";
  const pdfHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("pdf", pdfHost, {
    title: "exact.pdf", pageCount: 10000, pageTextLengths: [12, 0],
    extractedCharacters: 20, textTruncated: false, sourceComplete: true,
    textComplete: true, fullText: "[PDF page 1]\nVisible",
    textPage: {
      text: "[PDF page 1]\nVisible", offset: 0, startLine: 1, totalCharacters: 20
    },
    extractionWarning: "Page 2 has little or no extractable text.",
    onTabChange(tab) { activePdfTab = tab; },
    page: {
      pageIndex: 0, width: 800, height: 600, imageMimeType: "image/jpeg",
      imageByteLength: 4, imageBase64Content: "/9j/2Q=="
    },
    onNext() { nextPdfPage += 1; return true; },
    onPageSelect(pageIndex) { selectedPdfPage = pageIndex; return true; },
    onThumbnailRequest(pageIndex) { requestedPdfThumbnails.push(pageIndex); return true; }
  });
  assert.equal(pdfHost.querySelector(".rn-preview-page-label").textContent, "1 / 10000");
  assert.ok(pdfHost.querySelector(".rn-pdf-thumbnail-rail"));
  assert.equal(pdfHost.querySelectorAll(".rn-pdf-thumbnail-item").length, 8);
  assert.equal(pdfHost.querySelector(".rn-pdf-thumbnail-track").style.height, "1260000px");
  assert.equal(objectUrls.length, 3, "current thumbnail reuses the main-page Blob URL");
  assert.deepEqual(requestedPdfThumbnails, [1, 2, 3, 4, 5, 6, 7]);
  pdfHost.querySelectorAll(".rn-pdf-thumbnail-item")[1].click();
  assert.equal(selectedPdfPage, 1);
  const pageInput = pdfHost.querySelector(".rn-pdf-page-input");
  pageInput.value = "2";
  pageInput.dispatch("change");
  assert.equal(selectedPdfPage, 1);
  assert.equal(activePdfTab, "pages");
  assert.match(pdfHost.textContent, /little or no extractable text/);
  const pdfImage = pdfHost.querySelector("img");
  pdfImage.dispatch("load");
  const pdfVendor = vendorViewerInstances.at(-1);
  assert.equal(pdfVendor.options.className, "rn-vendor-image-viewer");
  assert.equal(button(pdfHost, "‹").disabled, true);
  assert.equal(button(pdfHost, "›").disabled, false);
  button(pdfHost, "›").click();
  await settle();
  assert.equal(nextPdfPage, 1);
  let keyPrevented = false;
  pdfHost.querySelector(".rn-image-viewer-stage-shell").dispatch("keydown", {
    key: "ArrowRight", preventDefault() { keyPrevented = true; }, stopPropagation() {}
  });
  await settle();
  assert.equal(nextPdfPage, 2);
  assert.equal(keyPrevented, true);
  button(pdfHost, "Текст").click();
  assert.equal(pdfVendor.destroyed, true);
  assert.equal(activePdfTab, "text");
  assert.equal(pdfHost.querySelector(".rn-text-viewer-content").textContent, "[PDF page 1]\nVisible");
  context.RNAssistantViewerRegistry.unmount(pdfHost);
  assert.ok(revokedUrls.includes(objectUrls[2]));

  const pdfUri = "rna://chat/chat-text/artifact/pdf/revision/1";
  const pdfDetail = new Element("div");
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(pdfDetail, {
    type: "artifact",
    item: { Kind: "attachment", Title: "exact.pdf", MimeType: "application/pdf", ResourceUri: pdfUri, Revision: 1 }
  }, "", {
    artifactViewerState() {
      return {
        status: "ready", resourceUri: pdfUri, viewerKind: "pdf", title: "exact.pdf",
        pageCount: 1, pageTextLengths: [7], extractedCharacters: 7,
        textTruncated: false, sourceComplete: true, fullReadAllowed: true,
        complete: true, fullText: "visible", pageIndex: 0,
        pages: [{ text: "visible", offset: 0, startLine: 1, totalCharacters: 7 }],
        pdfPage: {
          pageIndex: 0, width: 800, height: 600, imageMimeType: "image/jpeg",
          imageByteLength: 4, imageBase64Content: "/9j/2Q=="
        }
      };
    },
    changeArtifactPdfPage() {}
  });
  assert.ok(pdfDetail.querySelector(".rn-pdf-viewer"));
  assert.equal(pdfDetail.classList.contains("is-media-preview"), true);
  assert.ok(pdfDetail.querySelector(".rn-pdf-thumbnail-rail"));
  const detailMainUrl = objectUrls.at(-1);
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(pdfDetail, {
    type: "artifact", item: { Kind: "file", Title: "unknown.bin", MimeType: "application/octet-stream", Revision: 1 }
  }, "", {});
  assert.ok(revokedUrls.includes(detailMainUrl));
  console.log("PASS artifact PDF viewer: pages are primary, extracted text is secondary and page URLs are revoked");

  const actionContext = vm.createContext({ Promise });
  actionContext.window = actionContext;
  actionContext.alert = () => {};
  for (const file of ["app-artifact-viewer-actions.js", "app-html-workspace-actions.js"]) {
    vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), actionContext, { filename: file });
  }
  const uri = "rna://chat/chat-text/artifact/notes/revision/1";
  const exact = "x".repeat(32000) + "tail exact";
  const pdfExtracted = "[PDF page 1]\n" + "v".repeat(32000) + "\n[PDF page 2]\nVisible";
  const calls = [];
  const applied = [];
  const downloaded = [];
  const deferredPdfThumbnails = [];
  let deferPdfThumbnails = false;
  const state = { activeChatId: "chat-text", bridgeUnavailable: false };
  const actions = actionContext.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      if (method === "readArtifactImage") {
        return {
          resourceUri: payload.resourceUri,
          viewerKind: "image",
          title: "pixel.png",
          mimeType: "image/png",
          contentSha256: "d".repeat(64),
          byteLength: 3,
          base64Content: "AQID"
        };
      }
      if (method === "readArtifactPdfInfo") {
        return {
          resourceUri: payload.resourceUri,
          viewerKind: "pdf",
          title: "exact.pdf",
          mimeType: "application/pdf",
          contentSha256: "e".repeat(64),
          byteLength: 100,
          pageCount: 30,
          pageTextLengths: [32000, 7].concat(Array(28).fill(0)),
          extractedTextSha256: "f".repeat(64),
          extractedCharacters: pdfExtracted.length,
          textTruncated: false,
          extractionWarning: "Page 2 is scanned."
        };
      }
      if (method === "readArtifactPdfPage") {
        return {
          resourceUri: payload.resourceUri,
          viewerKind: "pdf",
          contentSha256: "e".repeat(64),
          pageIndex: payload.pageIndex,
          pageCount: 30,
          width: payload.pageIndex ? 600 : 800,
          height: payload.pageIndex ? 800 : 600,
          imageMimeType: "image/jpeg",
          imageContentSha256: "a".repeat(64),
          imageByteLength: 4,
          imageBase64Content: "/9j/2Q=="
        };
      }
      if (method === "readArtifactPdfThumbnail") {
        const response = {
          resourceUri: payload.resourceUri,
          viewerKind: "pdf",
          contentSha256: "e".repeat(64),
          pageIndex: payload.pageIndex,
          pageCount: 30,
          width: 160,
          height: 120,
          imageMimeType: "image/jpeg",
          imageContentSha256: "b".repeat(64),
          imageByteLength: 4,
          imageBase64Content: "/9j/2Q=="
        };
        if (deferPdfThumbnails) {
          return new Promise(resolve => deferredPdfThumbnails.push({ resolve, response }));
        }
        return response;
      }
      if (method === "readArtifactViewerPage" && payload.resourceUri !== uri) {
        const offset = payload.cursor ? 32000 : 0;
        const text = pdfExtracted.slice(offset, offset + 32000);
        return {
          resourceUri: payload.resourceUri,
          viewerKind: "pdf",
          title: "exact.pdf",
          mimeType: "application/pdf",
          contentSha256: "f".repeat(64),
          text,
          offset,
          returnedCharacters: text.length,
          totalCharacters: pdfExtracted.length,
          nextCursor: offset ? null : "pdf-32000",
          complete: offset > 0,
          truncated: offset === 0,
          sourceComplete: true,
          fullReadAllowed: true,
          viewerLimitReached: false,
          maximumDocumentCharacters: 512000
        };
      }
      const offset = payload.cursor ? 32000 : 0;
      const text = exact.slice(offset, offset + 32000);
      return {
        resourceUri: uri,
        viewerKind: "text",
        title: "notes.txt",
        mimeType: "text/plain",
        contentSha256: "c".repeat(64),
        text,
        offset,
        returnedCharacters: text.length,
        totalCharacters: exact.length,
        nextCursor: offset ? null : "32000",
        complete: offset > 0,
        truncated: offset === 0,
        sourceComplete: true,
        fullReadAllowed: true,
        viewerLimitReached: false,
        maximumDocumentCharacters: 512000
      };
    },
    applyArtifactViewerText: (resourceUri, hash, text) => applied.push({ resourceUri, hash, text }),
    downloadArtifactText: value => downloaded.push(value),
    log: () => {},
    render: () => {}
  });
  assert.equal(await actions.loadArtifactViewer({ resourceUri: uri }), true);
  assert.equal(actions.artifactViewerState(uri).pages.length, 1);
  assert.equal(await actions.loadArtifactViewerFull({ resourceUri: uri }), true);
  assert.equal(actions.artifactViewerState(uri).fullText, exact);
  assert.equal(calls[1].payload.cursor, "32000");
  assert.equal(applied.at(-1).text, exact);
  actions.downloadArtifactViewer({ resourceUri: uri });
  assert.equal(downloaded[0].resourceUri, uri);
  assert.equal(downloaded[0].text, exact);
  const actionImageUri = "rna://chat/chat-text/artifact/image-action/revision/1";
  assert.equal(await actions.loadArtifactImage({ resourceUri: actionImageUri }), true);
  assert.equal(actions.artifactViewerState(actionImageUri).viewerKind, "image");
  assert.equal(calls.at(-1).method, "readArtifactImage");
  const actionPdfUri = "rna://chat/chat-text/artifact/pdf-action/revision/1";
  assert.equal(await actions.loadArtifactPdf({ resourceUri: actionPdfUri }), true);
  assert.equal(actions.artifactViewerState(actionPdfUri).pdfPage.pageIndex, 0);
  assert.equal(actions.artifactViewerState(actionPdfUri).pages.length, 1);
  assert.equal(await actions.changeArtifactViewerPage({ resourceUri: actionPdfUri, direction: "next" }), true);
  assert.equal(actions.artifactViewerState(actionPdfUri).pages.length, 2);
  assert.equal(calls.at(-1).method, "readArtifactViewerPage");
  assert.equal(await actions.loadArtifactPdfThumbnail({ resourceUri: actionPdfUri, pageIndex: 1 }), true);
  assert.equal(actions.artifactViewerState(actionPdfUri).pdfThumbnails["1"].width, 160);
  assert.equal(calls.at(-1).method, "readArtifactPdfThumbnail");
  for (let pageIndex = 2; pageIndex < 27; pageIndex += 1) {
    assert.equal(await actions.loadArtifactPdfThumbnail({ resourceUri: actionPdfUri, pageIndex }), true);
  }
  assert.equal(actions.artifactViewerState(actionPdfUri).pdfThumbnailOrder.length, 24);
  assert.equal(actions.artifactViewerState(actionPdfUri).pdfThumbnails["1"], undefined);
  const actionPdfViewer = actions.artifactViewerState(actionPdfUri);
  actionPdfViewer.pdfThumbnails = {};
  actionPdfViewer.pdfThumbnailOrder = [];
  actionPdfViewer.pdfThumbnailPendingCount = 0;
  deferPdfThumbnails = true;
  const pendingThumbnailLoads = [1, 2, 3, 4, 5].map(pageIndex =>
    actions.loadArtifactPdfThumbnail({ resourceUri: actionPdfUri, pageIndex }));
  assert.equal(actionPdfViewer.pdfThumbnailPendingCount, 4);
  assert.equal(await pendingThumbnailLoads[4], false);
  assert.equal(deferredPdfThumbnails.length, 4);
  deferredPdfThumbnails.forEach(item => item.resolve(item.response));
  assert.deepEqual(await Promise.all(pendingThumbnailLoads.slice(0, 4)), [true, true, true, true]);
  assert.equal(actionPdfViewer.pdfThumbnailPendingCount, 0);
  deferPdfThumbnails = false;
  assert.equal(await actions.selectArtifactPdfPage({ resourceUri: actionPdfUri, pageIndex: 1 }), true);
  assert.equal(actions.artifactViewerState(actionPdfUri).pdfPage.pageIndex, 1);
  assert.equal(calls.at(-1).method, "readArtifactPdfPage");
  console.log("PASS artifact viewer owner: exact pinned pages assemble contiguously before full copy/download");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-text-viewer.js?v=artifact-text-20260831-1"));
  assert.ok(index.includes("app-text-viewer.css?v=artifact-text-20260831-1"));
  assert.ok(index.includes("app-resource-viewer.js?v=artifact-thumbnails-20260902-1"));
  assert.ok(index.includes("app-resource-viewer.css?v=artifact-thumbnails-20260902-1"));
  assert.ok(index.includes("js/vendor/viewer.min.js"));
  assert.ok(index.includes("css/vendor/viewer.min.css"));
  assert.ok(index.indexOf("js/vendor/viewer.min.js") < index.indexOf("app-resource-viewer.js"));
  assert.ok(index.includes("app-artifact-viewer-actions.js?v=artifact-thumbnails-20260902-1"));
  assert.ok(index.indexOf("app-viewer-registry.js") < index.indexOf("app-text-viewer.js"));
  assert.ok(index.indexOf("app-artifact-viewer-actions.js") < index.indexOf("app-html-workspace-actions.js"));
  const viewerSource = fs.readFileSync(path.join(root, "web/js/app-text-viewer.js"), "utf8");
  const resourceSource = fs.readFileSync(path.join(root, "web/js/app-resource-viewer.js"), "utf8");
  const actionSource = fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8");
  assert.doesNotMatch(viewerSource, /send\(|chrome\.webview|fetch\(|XMLHttpRequest|createObjectURL/);
  assert.match(viewerSource, /window\.markdown\(String\(options\.fullText\)\)/);
  assert.match(resourceSource, /new window\.Viewer\(image/);
  assert.match(resourceSource, /rn-pdf-thumbnail-rail/);
  assert.doesNotMatch(resourceSource, /fittedScale|addEventListener\("wheel"|image\.style\.width/);
  assert.match(actionSource, /readArtifactViewerPage/);
  assert.match(actionSource, /readArtifactPdfThumbnail/);
  console.log("PASS artifact viewers: allowlisted modules are UI-only and loaded after their registry");
  console.log("OK 8/8");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
