"use strict";

// Real image decoding, layout and IntersectionObserver; no Office qualification.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");
const read = file => fs.readFileSync(path.join(root, "web", file), "utf8");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 430, height: 700 }, deviceScaleFactor: 2 });
    page.setDefaultTimeout(4000);
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent('<div id="messages" style="height:350px;overflow:auto"></div><div id="draft"></div><div id="viewer"></div>');
    for (const file of ["app-artifacts.css", "app-composer.css", "vendor/viewer.min.css", "app-resource-viewer.css"])
      await page.addStyleTag({ content: read("css/" + file) });
    for (const file of ["app-artifacts.js", "app-messages.js", "app-attachments.js", "app-viewer-registry.js",
      "vendor/viewer.min.js", "app-resource-viewer.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(async () => {
      window.$ = id => document.getElementById(id);
      window.makeJpeg = async (width, height) => {
        const canvas = document.createElement("canvas");
        canvas.width = width; canvas.height = height;
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "white"; ctx.fillRect(0, 0, width, height);
        ctx.fillStyle = "red"; ctx.fillRect(0, 0, width / 3, height / 3);
        ctx.fillStyle = "blue"; ctx.fillRect(width * 2 / 3, height * 2 / 3, width / 3, height / 3);
        return new Promise(resolve => canvas.toBlob(resolve, "image/jpeg"));
      };
      window.jpeg = await makeJpeg(1280, 320);
      const uri = "rna://chat/chat-image/artifact/image/revision/1";
      window.state = { activeChatId: "chat-image", messages: [{ ArtifactIds: ["image"] }],
        artifacts: [{ id: "image", kind: "image", title: "panorama.jpeg", revision: 1,
          mimeType: "image/jpeg", resourceUri: uri }],
        artifactLibrary: { heads: [{ artifactId: "image", resourceClass: "immutable_original",
          history: [{ artifactId: "image", revision: 1, resourceUri: uri }] }], removedResourceUris: [] } };
      window.thumbnails = {};
      window.reads = 0;
      window.RNAssistantArtifactThumbnailRuntime = {
        state: uri => thumbnails[uri] || null,
        load: async ({ resourceUri }) => {
          if (thumbnails[resourceUri]) return true;
          reads++;
          const thumbnail = { status: "ready", resourceUri,
            data: { objectUrl: URL.createObjectURL(jpeg) } };
          thumbnails[resourceUri] = thumbnail;
          updateArtifactThumbnailViews(resourceUri, thumbnail);
          return true;
        }
      };
      window.messageProtocolMessage = () => false;
      window.renderChatResourceNavigation = window.renderAgentPlanDock = window.renderAgentApprovalDock = () => {};
      window.syncChatScroll = () => {};
      window.buildMessageUnits = () => [
        { key: "spacer", signature: "spacer", render: () => {
          const node = document.createElement("div"); node.style.height = "1000px"; return node;
        } },
        { key: "image", signature: "image", render: () => {
          const node = document.createElement("article");
          appendMessageMediaGallery(node, state.messages[0]); return node;
        } }
      ];
      renderMessages();
      window.originalCell = document.querySelector(".chat-media-item");
      renderMessages(); // Retains the message node while the image is offscreen.
    });
    assert.equal(await page.evaluate(() => originalCell === document.querySelector(".chat-media-item")), true);
    assert.equal(await page.evaluate(() => reads), 0, "offscreen images stay lazy");
    await page.locator(".chat-media-item").scrollIntoViewIfNeeded();
    await page.waitForFunction(() => {
      const img = document.querySelector(".chat-media-item img");
      return img && img.complete && img.naturalWidth === 1280;
    });
    assert.equal(await page.evaluate(() => reads), 1, "retained messages still load on scroll after a chat refresh");
    const layout = await page.locator(".chat-media-item img").evaluate(img => ({
      fit: getComputedStyle(img).objectFit,
      sourceWidth: img.naturalWidth,
      pixelRatio: devicePixelRatio,
      image: img.getBoundingClientRect().toJSON(),
      cell: img.closest("button").getBoundingClientRect().toJSON()
    }));
    assert.equal(layout.fit, "contain", "the entire panorama is visible");
    assert.ok(layout.image.height <= layout.cell.height + 1 && layout.image.width <= layout.cell.width + 1);
    assert.ok(layout.sourceWidth >= layout.image.width * layout.pixelRatio,
      "the panorama preview has enough source pixels at 2x display scaling");
    console.log("PASS chat images: refresh preserves lazy loading and the whole high-DPI JPEG thumbnail");

    await page.evaluate(() => { $("messages").scrollTop = 0; });
    // Let the observer deliver the offscreen transition before returning.
    await page.waitForFunction(() => document.querySelector(".chat-media-item").getBoundingClientRect().top > innerHeight);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await page.evaluate(() => {
      const uri = state.artifacts[0].resourceUri;
      URL.revokeObjectURL(thumbnails[uri].data.objectUrl);
      delete thumbnails[uri]; // Same cache miss as bounded eviction or an expired lease.
    });
    await page.locator(".chat-media-item").scrollIntoViewIfNeeded();
    await page.waitForFunction(() => reads === 2 && document.querySelector(".chat-media-item img").naturalWidth === 1280);
    await page.evaluate(() => {
      $("draft").appendChild(attachmentCard({ previewUrl: URL.createObjectURL(jpeg),
        FileName: "panorama.jpeg", Kind: "image", Size: jpeg.size }, true));
    });
    assert.equal(await page.locator(".attachment-thumb img").evaluate(img => getComputedStyle(img).objectFit), "contain");
    console.log("PASS chat images: returning to an evicted thumbnail reloads it; drafts preserve aspect ratio");

    // A narrow Office pane and a long screenshot need a fit below the zoom floor.
    await page.addStyleTag({ content: "#viewer .rn-image-viewer-stage-shell { height:120px; min-height:120px; }" });
    await page.evaluate(async () => {
      const blob = await makeJpeg(80, 16000);
      window.fullImageUrl = URL.createObjectURL(blob);
      RNAssistantViewerRegistry.mount("image", $("viewer"), {
        title: "long.jpeg", byteLength: blob.size, data: { objectUrl: fullImageUrl }
      });
    });
    await page.waitForFunction(() => {
      const source = document.querySelector(".rn-viewerjs-source");
      return source && source.viewer && source.viewer.viewed;
    });
    await page.locator(".viewer-reset").click();
    await page.waitForFunction(() => {
      const viewer = document.querySelector(".rn-viewerjs-source").viewer;
      return !viewer.zooming && !viewer.viewing && viewer.image.getBoundingClientRect().height <= 121;
    });
    const fit = await page.evaluate(() => {
      const viewer = document.querySelector(".rn-viewerjs-source").viewer;
      return { image: viewer.image.getBoundingClientRect().toJSON(),
        stage: document.querySelector(".rn-image-viewer-stage-shell").getBoundingClientRect().toJSON() };
    });
    assert.ok(fit.image.top >= fit.stage.top - 1 && fit.image.bottom <= fit.stage.bottom + 1,
      "Fit shows both ends of the long screenshot, even below 1%");
    await page.evaluate(() => { RNAssistantViewerRegistry.unmount($("viewer")); URL.revokeObjectURL(fullImageUrl); });
    assert.deepEqual(errors, []);
    console.log("PASS image viewer: a long JPEG fits the stage without cropping");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
