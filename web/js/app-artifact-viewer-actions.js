(function () {
  "use strict";

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    function closeData(data, chatId) {
      if (!data || !data.leaseId) return;
      options.send("resourceDataClose", { chatId: chatId || data.chatId || state.activeChatId,
        workspaceId: "viewer", leaseId: data.leaseId }).catch(function () {});
    }

    function releaseViewer(viewer) {
      if (!viewer) return;
      closeData(viewer.data);
      if (viewer.pdfPage) closeData(viewer.pdfPage.data);
      Object.keys(viewer.pdfThumbnails || {}).forEach(function (key) { closeData(viewer.pdfThumbnails[key].data); });
    }

    function closeAll() {
      Object.keys(cache()).forEach(function (uri) { releaseViewer(cache()[uri]); });
      state.artifactViewerPages = {};
      var thumbnails = thumbnailCache();
      cancelThumbnailQueue(thumbnails);
      Object.keys(thumbnails.items).forEach(function (uri) { closeData(thumbnails.items[uri].data); });
      state.artifactViewerThumbnails = null;
    }

    function liveData(data) { return !data || Date.parse(data.expiresUtc) > Date.now(); }

    function cache() {
      state.artifactViewerPages = state.artifactViewerPages || {};
      return state.artifactViewerPages;
    }

    function cacheViewer(uri, viewerState) {
      var viewers = cache();
      if (!viewers[uri] && Object.keys(viewers).length >= 8) {
        var removed = Object.keys(viewers)[0]; releaseViewer(viewers[removed]); delete viewers[removed];
      }
      if (viewers[uri] !== viewerState) releaseViewer(viewers[uri]);
      viewers[uri] = viewerState;
      var mediaUris = Object.keys(viewers).filter(function (key) {
        return viewers[key] && (viewers[key].viewerKind === "image" || viewers[key].viewerKind === "pdf");
      });
      while (mediaUris.length > 2) { var evicted = mediaUris.shift(); releaseViewer(viewers[evicted]); delete viewers[evicted]; }
      return viewerState;
    }

    function artifactViewerState(uri) {
      var viewer = cache()[uri];
      if (viewer && (!liveData(viewer.data) || viewer.pdfPage && !liveData(viewer.pdfPage.data))) {
        releaseViewer(viewer); delete cache()[uri]; return null;
      }
      return viewer || null;
    }

    function thumbnailCache() {
      var thumbnails = state.artifactViewerThumbnails;
      if (!thumbnails || !thumbnails.items || !Array.isArray(thumbnails.order) ||
          !Array.isArray(thumbnails.queue)) {
        thumbnails = { items: {}, order: [], queue: [], pending: 0 };
        state.artifactViewerThumbnails = thumbnails;
      }
      return thumbnails;
    }

    function artifactImageThumbnailState(uri) {
      var store = thumbnailCache();
      var key = String(uri || "");
      var item = store.items[key] || null;
      if (item && !liveData(item.data)) { closeData(item.data); delete store.items[key]; return null; }
      if (item && (item.status === "ready" || item.status === "error")) touchThumbnail(store, key);
      return item;
    }

    function viewerTextContentSha256(viewer) {
      return viewer.textContentSha256 || viewer.contentSha256;
    }

    function pageNewlines(text) {
      var count = 0;
      text = String(text || "");
      for (var index = 0; index < text.length; index += 1) {
        if (text.charAt(index) === "\n") count += 1;
      }
      return count;
    }

    function normalizePage(response, uri, expectedOffset, startLine) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var text = value(response, "Text", "text", "") || "";
      var offset = Number(value(response, "Offset", "offset", -1));
      var returned = Number(value(response, "ReturnedCharacters", "returnedCharacters", -1));
      var total = Number(value(response, "TotalCharacters", "totalCharacters", -1));
      var complete = value(response, "Complete", "complete", false) === true;
      var sourceComplete = value(response, "SourceComplete", "sourceComplete", false) === true;
      var fullReadAllowed = value(response, "FullReadAllowed", "fullReadAllowed", false) === true;
      var nextCursor = value(response, "NextCursor", "nextCursor", null);
      var maximum = Number(value(response, "MaximumDocumentCharacters", "maximumDocumentCharacters", 0) || 0);
      if (returnedUri !== uri || (viewerKind !== "text" && viewerKind !== "markdown" && viewerKind !== "pdf") ||
          !/^[a-f0-9]{64}$/i.test(hash) || offset !== expectedOffset || returned !== text.length ||
          total < offset + returned || !maximum || total < 0 ||
          (complete && (!sourceComplete || nextCursor))) {
        throw new Error("Artifact viewer returned inconsistent exact-read evidence.");
      }
      return {
        resourceUri: returnedUri,
        viewerKind: viewerKind,
        title: value(response, "Title", "title", "Artifact") || "Artifact",
        mimeType: value(response, "MimeType", "mimeType", "text/plain") || "text/plain",
        contentSha256: hash,
        text: text,
        offset: offset,
        returnedCharacters: returned,
        totalCharacters: total,
        nextCursor: nextCursor || null,
        complete: complete,
        truncated: value(response, "Truncated", "truncated", !complete) === true,
        sourceComplete: sourceComplete,
        fullReadAllowed: fullReadAllowed,
        viewerLimitReached: value(response, "ViewerLimitReached", "viewerLimitReached", false) === true,
        maximumDocumentCharacters: maximum,
        startLine: Math.max(1, Number(startLine || 1))
      };
    }

    function resourceData(response, uri) {
      var data = value(response, "Data", "data", null);
      var exact = data && data.descriptor && data.descriptor.reference;
      if (!data || !/^[a-f0-9]{64}$/.test(data.leaseId || "") ||
          data.url !== "https://rnassistant.local-resource/v1/" + data.leaseId ||
          !exact || exact.uri !== uri || !exact.revision ||
          !Number.isFinite(Date.parse(data.expiresUtc)) || Date.parse(data.expiresUtc) <= Date.now()) {
        throw new Error("Artifact view has no exact, scoped resource lease.");
      }
      data.chatId = state.activeChatId;
      return data;
    }

    function binaryData(response, uri, byteLength, hash) {
      var data = resourceData(response, uri);
      var payload = data.binary && data.binary.payload;
      if (!payload || payload.byteLength !== byteLength || payload.sha256 !== hash) {
        throw new Error("Artifact binary payload does not match its exact lease.");
      }
      return data;
    }

    function normalizeImage(response, uri) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var mimeType = String(value(response, "MimeType", "mimeType", "") || "").split(";", 1)[0].toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var byteLength = Number(value(response, "ByteLength", "byteLength", -1));
      var data = binaryData(response, uri, byteLength, hash);
      if (returnedUri !== uri || viewerKind !== "image" ||
          ["image/jpeg", "image/png", "image/gif", "image/webp"].indexOf(mimeType) < 0 ||
          !/^[a-f0-9]{64}$/i.test(hash) || byteLength <= 0 || byteLength > 20 * 1024 * 1024) {
        throw new Error("Artifact image returned inconsistent exact-read evidence.");
      }
      return {
        status: "ready",
        resourceUri: returnedUri,
        viewerKind: viewerKind,
        title: value(response, "Title", "title", "Image") || "Image",
        mimeType: mimeType,
        contentSha256: hash,
        byteLength: byteLength,
        data: data
      };
    }

    function normalizeImageThumbnail(response, uri) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var width = Number(value(response, "Width", "width", 0));
      var height = Number(value(response, "Height", "height", 0));
      var imageMimeType = String(value(response, "ImageMimeType", "imageMimeType", "") || "").toLowerCase();
      var imageHash = value(response, "ImageContentSha256", "imageContentSha256", "") || "";
      var imageByteLength = Number(value(response, "ImageByteLength", "imageByteLength", -1));
      var data = binaryData(response, uri, imageByteLength, imageHash);
      if (returnedUri !== uri || viewerKind !== "image" || !/^[a-f0-9]{64}$/i.test(hash) ||
          !Number.isInteger(width) || width <= 0 || width > 320 ||
          !Number.isInteger(height) || height <= 0 || height > 320 ||
          imageMimeType !== "image/jpeg" || !/^[a-f0-9]{64}$/i.test(imageHash) ||
          imageByteLength <= 0 || imageByteLength > 512 * 1024) {
        throw new Error("Artifact image thumbnail returned inconsistent render evidence.");
      }
      return {
        status: "ready",
        resourceUri: returnedUri,
        viewerKind: viewerKind,
        contentSha256: hash,
        width: width,
        height: height,
        imageMimeType: imageMimeType,
        imageContentSha256: imageHash,
        imageByteLength: imageByteLength,
        data: data
      };
    }

    function touchThumbnail(store, uri) {
      store.order = store.order.filter(function (value) { return value !== uri; });
      store.order.push(uri);
      while (store.order.length > 24) {
        var candidate = store.order.shift();
        var item = store.items[candidate];
        if (item && (item.status === "loading" || item.status === "queued")) {
          continue;
        } else {
          if (item) closeData(item.data);
          delete store.items[candidate];
        }
      }
    }

    function notifyThumbnail(uri, thumbnail) {
      if (typeof options.onArtifactThumbnailChange === "function") {
        options.onArtifactThumbnailChange(uri, thumbnail);
      }
    }

    function cancelThumbnailQueue(store) {
      while (store.queue.length) {
        var entry = store.queue.shift();
        if (entry && typeof entry.resolve === "function") entry.resolve(false);
      }
    }

    function drainThumbnailQueue(store) {
      if (state.artifactViewerThumbnails !== store) {
        cancelThumbnailQueue(store);
        return;
      }
      while (store.pending < 4 && store.queue.length) {
        (function (entry) {
          if (!entry || store.items[entry.resourceUri] !== entry ||
              entry.chatId !== state.activeChatId) {
            if (entry && typeof entry.resolve === "function") entry.resolve(false);
            return;
          }
          entry.status = "loading";
          store.pending += 1;
          notifyThumbnail(entry.resourceUri, entry);
          var completed = false;
          options.send("readArtifactImageThumbnail", {
            chatId: entry.chatId,
            resourceUri: entry.resourceUri
          }).then(function (response) {
            if (state.activeChatId !== entry.chatId || state.artifactViewerThumbnails !== store ||
                store.items[entry.resourceUri] !== entry) {
              closeData(value(response, "Data", "data", null), entry.chatId); return false;
            }
            var thumbnail;
            try { thumbnail = normalizeImageThumbnail(response, entry.resourceUri); }
            catch (error) { closeData(value(response, "Data", "data", null), entry.chatId); throw error; }
            store.items[entry.resourceUri] = thumbnail;
            touchThumbnail(store, entry.resourceUri);
            notifyThumbnail(entry.resourceUri, thumbnail);
            return true;
          }).catch(function (error) {
            if (state.activeChatId === entry.chatId && state.artifactViewerThumbnails === store &&
                store.items[entry.resourceUri] === entry) {
              var failure = {
                status: "error",
                resourceUri: entry.resourceUri,
                message: error.detail || error.message || "Image thumbnail is unavailable."
              };
              store.items[entry.resourceUri] = failure;
              touchThumbnail(store, entry.resourceUri);
              notifyThumbnail(entry.resourceUri, failure);
              if (!store.reportedError) {
                store.reportedError = true;
                options.log(failure.message, "error");
              }
            }
            return false;
          }).then(function (result) {
            completed = result;
          }).finally(function () {
            store.pending = Math.max(0, Number(store.pending || 0) - 1);
            drainThumbnailQueue(store);
            if (typeof entry.resolve === "function") entry.resolve(completed);
          });
        }(store.queue.shift()));
      }
    }

    function loadArtifactImageThumbnail(request) {
      request = request || {};
      var uri = String(request.resourceUri || "");
      if (state.bridgeUnavailable || !uri) return Promise.resolve(false);
      var store = thumbnailCache();
      var current = artifactImageThumbnailState(uri);
      if (current) {
        if (current.status === "ready" || current.status === "error") touchThumbnail(store, uri);
        if (current.status === "ready") return Promise.resolve(true);
        if (current.status === "error") return Promise.resolve(false);
        return current.promise || Promise.resolve(false);
      }
      if (store.queue.length >= 256) return Promise.resolve(false);
      var resolveRequest;
      var promise = new Promise(function (resolve) { resolveRequest = resolve; });
      var entry = {
        status: "queued",
        resourceUri: uri,
        chatId: state.activeChatId,
        promise: promise,
        resolve: resolveRequest
      };
      store.items[uri] = entry;
      store.queue.push(entry);
      notifyThumbnail(uri, entry);
      drainThumbnailQueue(store);
      return promise;
    }

    function normalizePdfInfo(response, uri) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var mimeType = String(value(response, "MimeType", "mimeType", "") || "").split(";", 1)[0].toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var byteLength = Number(value(response, "ByteLength", "byteLength", -1));
      var pageCount = Number(value(response, "PageCount", "pageCount", 0));
      var pageTextLengths = value(response, "PageTextLengths", "pageTextLengths", []);
      var extractedHash = value(response, "ExtractedTextSha256", "extractedTextSha256", "") || "";
      var extractedCharacters = Number(value(response, "ExtractedCharacters", "extractedCharacters", -1));
      var textTruncated = value(response, "TextTruncated", "textTruncated", false) === true;
      if (returnedUri !== uri || viewerKind !== "pdf" || mimeType !== "application/pdf" ||
          !/^[a-f0-9]{64}$/i.test(hash) || !Number.isInteger(byteLength) ||
          byteLength <= 0 || byteLength > 20 * 1024 * 1024 ||
          !Number.isInteger(pageCount) || pageCount <= 0 || pageCount > 10000 ||
          !Array.isArray(pageTextLengths) || pageTextLengths.length > pageCount ||
          pageTextLengths.some(function (length) { return !Number.isInteger(length) || length < 0; }) ||
          !/^[a-f0-9]{64}$/i.test(extractedHash) || !Number.isInteger(extractedCharacters) || extractedCharacters < 0 ||
          extractedCharacters > 1000000 || (!textTruncated && pageTextLengths.length < pageCount)) {
        throw new Error("Artifact PDF returned inconsistent exact-read evidence.");
      }
      return {
        resourceUri: returnedUri,
        viewerKind: viewerKind,
        title: value(response, "Title", "title", "PDF") || "PDF",
        mimeType: mimeType,
        contentSha256: hash,
        byteLength: byteLength,
        pageCount: pageCount,
        pageTextLengths: pageTextLengths.slice(),
        extractedTextSha256: extractedHash,
        extractedCharacters: extractedCharacters,
        textTruncated: textTruncated,
        extractionWarning: value(response, "ExtractionWarning", "extractionWarning", "") || ""
      };
    }

    function normalizePdfRender(response, uri, expectedPageIndex, maximumDimension, maximumBytes, errorMessage) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var pageIndex = Number(value(response, "PageIndex", "pageIndex", -1));
      var pageCount = Number(value(response, "PageCount", "pageCount", 0));
      var width = Number(value(response, "Width", "width", 0));
      var height = Number(value(response, "Height", "height", 0));
      var imageMimeType = String(value(response, "ImageMimeType", "imageMimeType", "") || "").toLowerCase();
      var imageHash = value(response, "ImageContentSha256", "imageContentSha256", "") || "";
      var imageByteLength = Number(value(response, "ImageByteLength", "imageByteLength", -1));
      var data = binaryData(response, uri, imageByteLength, imageHash);
      if (returnedUri !== uri || viewerKind !== "pdf" || !/^[a-f0-9]{64}$/i.test(hash) ||
          pageIndex !== expectedPageIndex || !Number.isInteger(pageCount) || pageCount <= 0 || pageCount > 10000 ||
          !Number.isInteger(width) || width <= 0 || width > maximumDimension ||
          !Number.isInteger(height) || height <= 0 || height > maximumDimension ||
          imageMimeType !== "image/jpeg" || !/^[a-f0-9]{64}$/i.test(imageHash) ||
          imageByteLength <= 0 || imageByteLength > maximumBytes) {
        throw new Error(errorMessage);
      }
      return {
        resourceUri: returnedUri,
        viewerKind: viewerKind,
        contentSha256: hash,
        pageIndex: pageIndex,
        pageCount: pageCount,
        width: width,
        height: height,
        imageMimeType: imageMimeType,
        imageContentSha256: imageHash,
        imageByteLength: imageByteLength,
        data: data
      };
    }

    function normalizePdfPage(response, uri, expectedPageIndex) {
      return normalizePdfRender(
        response, uri, expectedPageIndex, 2048, 10 * 1024 * 1024,
        "Artifact PDF page returned inconsistent render evidence.");
    }

    function normalizePdfThumbnail(response, uri, expectedPageIndex) {
      return normalizePdfRender(
        response, uri, expectedPageIndex, 320, 1024 * 1024,
        "Artifact PDF thumbnail returned inconsistent render evidence.");
    }

    async function readPage(uri, cursor, expectedOffset, startLine, chatId) {
      var response = await options.send("readArtifactViewerPage", {
        chatId: chatId,
        resourceUri: uri,
        cursor: cursor || null
      });
      var data = value(response, "Data", "data", null);
      try {
        if (state.activeChatId !== chatId) throw new Error("Artifact viewer read belongs to another chat.");
        data = resourceData(response, uri);
        if (data.view !== "text" || data.maxBatchItems < 32000 || data.maxBatchBytes <= 0 || data.maxBatchBytes > 8 * 1024 * 1024) {
          throw new Error("Artifact text view has invalid negotiated bounds.");
        }
        var fetchResource = options.fetch || window.fetch.bind(window);
        var fetched = await fetchResource(data.url + "?offset=" + expectedOffset + "&limit=32000", {
          method: "GET", credentials: "omit", cache: "no-store", redirect: "error"
        });
        if (!fetched.ok) throw new Error("Artifact text resource read failed.");
        var json = await fetched.text();
        if (json.length > data.maxBatchBytes) throw new Error("Artifact text batch exceeds its negotiated bound.");
        var batch = JSON.parse(json);
        if (!batch.resource || batch.resource.uri !== uri || batch.resource.revision !== data.descriptor.reference.revision ||
            batch.view !== "text" || batch.offset !== expectedOffset || typeof batch.text !== "string" ||
            batch.text.length > 32000 || batch.nextOffset !== expectedOffset + batch.text.length) {
          throw new Error("Artifact text batch changed its exact revision or continuation.");
        }
        if (state.activeChatId !== chatId) throw new Error("Artifact viewer read belongs to another chat.");
        return normalizePage(Object.assign({}, response, { text: batch.text }), uri, expectedOffset, startLine);
      } finally { closeData(data, chatId); }
    }

    async function loadArtifactViewer(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      if (state.bridgeUnavailable || !uri) return false;
      var current = artifactViewerState(uri);
      if (current && (current.status === "loading" || current.status === "ready")) return current.status === "ready";
      var chatId = state.activeChatId;
      cacheViewer(uri, { status: "loading", resourceUri: uri });
      try {
        var page = await readPage(uri, null, 0, 1, chatId);
        var viewer = cacheViewer(uri, {
          status: "ready",
          resourceUri: uri,
          viewerKind: page.viewerKind,
          title: page.title,
          mimeType: page.mimeType,
          contentSha256: page.contentSha256,
          fullReadAllowed: page.fullReadAllowed,
          sourceComplete: page.sourceComplete,
          viewerLimitReached: page.viewerLimitReached,
          pages: [page],
          pageIndex: 0,
          fullText: page.complete && page.fullReadAllowed ? page.text : null,
          complete: page.complete && page.fullReadAllowed
        });
        if (viewer.complete && typeof options.applyArtifactViewerText === "function") {
          options.applyArtifactViewerText(uri, viewer.contentSha256, viewer.fullText);
        }
        if (options.render) options.render();
        return true;
      } catch (error) {
        if (state.activeChatId !== chatId) {
          delete cache()[uri];
          return false;
        }
        cacheViewer(uri, {
          status: "error",
          resourceUri: uri,
          message: error.detail || error.message || "Artifact source is unavailable."
        });
        options.log(error.detail || error.message, "error");
        if (options.render) options.render();
        return false;
      }
    }

    async function loadArtifactImage(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      if (state.bridgeUnavailable || !uri) return false;
      var current = artifactViewerState(uri);
      if (current && (current.status === "loading" || current.status === "ready")) return current.status === "ready";
      var chatId = state.activeChatId;
      var pending = cacheViewer(uri, { status: "loading", resourceUri: uri, viewerKind: "image" });
      var retained = false;
      try {
        var response = await options.send("readArtifactImage", {
          chatId: chatId,
          resourceUri: uri
        });
        if (state.activeChatId !== chatId || cache()[uri] !== pending) return false;
        cacheViewer(uri, normalizeImage(response, uri));
        retained = true;
        if (options.render) options.render();
        return true;
      } catch (error) {
        if (state.activeChatId !== chatId) {
          delete cache()[uri];
          return false;
        }
        cacheViewer(uri, {
          status: "error",
          resourceUri: uri,
          viewerKind: "image",
          message: error.detail || error.message || "Artifact image is unavailable."
        });
        options.log(error.detail || error.message, "error");
        if (options.render) options.render();
        return false;
      } finally { if (!retained) closeData(value(response, "Data", "data", null), chatId); }
    }

    async function loadArtifactPdf(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      if (state.bridgeUnavailable || !uri) return false;
      var current = artifactViewerState(uri);
      if (current && (current.status === "loading" || current.status === "ready")) {
        return current.status === "ready" && current.viewerKind === "pdf";
      }
      var chatId = state.activeChatId;
      var pending = cacheViewer(uri, { status: "loading", resourceUri: uri, viewerKind: "pdf" });
      var retained = false, abandoned = false, pageResponse = null;
      try {
        var responses = await Promise.all([
          options.send("readArtifactPdfInfo", { chatId: chatId, resourceUri: uri }),
          options.send("readArtifactPdfPage", { chatId: chatId, resourceUri: uri, pageIndex: 0 }).then(function (response) {
            pageResponse = response;
            if (abandoned) closeData(value(response, "Data", "data", null), chatId);
            return response;
          }),
          readPage(uri, null, 0, 1, chatId)
        ]);
        if (state.activeChatId !== chatId || cache()[uri] !== pending) return false;
        var info = normalizePdfInfo(responses[0], uri);
        var pdfPage = normalizePdfPage(responses[1], uri, 0);
        var textPage = responses[2];
        if (info.contentSha256.toLowerCase() !== pdfPage.contentSha256.toLowerCase() ||
            info.pageCount !== pdfPage.pageCount || textPage.viewerKind !== "pdf" ||
            String(textPage.mimeType || "").split(";", 1)[0].toLowerCase() !== info.mimeType ||
            info.extractedTextSha256.toLowerCase() !== textPage.contentSha256.toLowerCase() ||
            info.extractedCharacters !== textPage.totalCharacters ||
            info.textTruncated !== !textPage.sourceComplete) {
          throw new Error("Artifact PDF info, text and page changed exact revision evidence.");
        }
        info.status = "ready";
        info.pdfPage = pdfPage;
        info.pdfThumbnails = {};
        info.pdfThumbnailOrder = [];
        info.pdfThumbnailPendingCount = 0;
        info.pdfThumbnailScrollTop = 0;
        info.textContentSha256 = textPage.contentSha256;
        info.pages = [textPage];
        info.pageIndex = 0;
        info.fullReadAllowed = textPage.fullReadAllowed;
        info.sourceComplete = textPage.sourceComplete;
        info.viewerLimitReached = textPage.viewerLimitReached;
        info.fullText = textPage.complete && textPage.fullReadAllowed ? textPage.text : null;
        info.complete = textPage.complete && textPage.fullReadAllowed;
        cacheViewer(uri, info);
        retained = true;
        if (options.render) options.render();
        return true;
      } catch (error) {
        if (state.activeChatId !== chatId) {
          delete cache()[uri];
          return false;
        }
        cacheViewer(uri, {
          status: "error", resourceUri: uri, viewerKind: "pdf",
          message: error.detail || error.message || "Artifact PDF is unavailable."
        });
        options.log(error.detail || error.message, "error");
        if (options.render) options.render();
        return false;
      } finally {
        if (!retained) { abandoned = true; closeData(value(pageResponse, "Data", "data", null), chatId); }
      }
    }

    async function selectArtifactPdfPage(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      var viewer = artifactViewerState(uri);
      if (!viewer || viewer.status !== "ready" || viewer.viewerKind !== "pdf" || viewer.pending) return false;
      var pageIndex = Number(request.pageIndex);
      if (!Number.isInteger(pageIndex) || pageIndex < 0 || pageIndex >= viewer.pageCount) return false;
      if (pageIndex === viewer.pdfPage.pageIndex) return true;
      var chatId = state.activeChatId;
      viewer.pending = true;
      var retained = false;
      try {
        var response = await options.send("readArtifactPdfPage", {
          chatId: chatId,
          resourceUri: uri,
          pageIndex: pageIndex
        });
        if (state.activeChatId !== chatId || artifactViewerState(uri) !== viewer) return false;
        var page = normalizePdfPage(response, uri, pageIndex);
        if (viewer.contentSha256.toLowerCase() !== page.contentSha256.toLowerCase() ||
            viewer.pageCount !== page.pageCount) {
          throw new Error("Artifact PDF page changed exact revision evidence.");
        }
        closeData(viewer.pdfPage && viewer.pdfPage.data);
        viewer.pdfPage = page;
        retained = true;
        return true;
      } catch (error) {
        if (state.activeChatId === chatId && artifactViewerState(uri) === viewer) {
          options.log(error.detail || error.message, "error");
        }
        return false;
      } finally {
        if (!retained) closeData(value(response, "Data", "data", null), chatId);
        viewer.pending = false;
        if (state.activeChatId === chatId && artifactViewerState(uri) === viewer && options.render) options.render();
      }
    }

    function changeArtifactPdfPage(request) {
      request = request || {};
      var viewer = artifactViewerState(request.resourceUri || "");
      if (!viewer || viewer.status !== "ready" || viewer.viewerKind !== "pdf" || !viewer.pdfPage) {
        return Promise.resolve(false);
      }
      var delta = request.direction === "previous" ? -1 : 1;
      return selectArtifactPdfPage({
        resourceUri: request.resourceUri,
        pageIndex: viewer.pdfPage.pageIndex + delta
      });
    }

    async function loadArtifactPdfThumbnail(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      var viewer = artifactViewerState(uri);
      var pageIndex = Number(request.pageIndex);
      if (!viewer || viewer.status !== "ready" || viewer.viewerKind !== "pdf" ||
          !Number.isInteger(pageIndex) || pageIndex < 0 || pageIndex >= viewer.pageCount) return false;
      viewer.pdfThumbnails = viewer.pdfThumbnails || {};
      viewer.pdfThumbnailOrder = viewer.pdfThumbnailOrder || [];
      var key = String(pageIndex);
      var current = viewer.pdfThumbnails[key];
      if (current && !liveData(current.data)) { closeData(current.data); delete viewer.pdfThumbnails[key]; current = null; }
      if (current && (current.status === "loading" || current.status === "ready" || current.status === "error")) {
        return current.status === "ready";
      }
      var pendingCount = Number(viewer.pdfThumbnailPendingCount || 0);
      viewer.pdfThumbnailPendingCount = Number.isInteger(pendingCount) && pendingCount >= 0 ? pendingCount : 0;
      if (viewer.pdfThumbnailPendingCount >= 4) return false;
      var chatId = state.activeChatId;
      viewer.pdfThumbnailPendingCount += 1;
      var retained = false;
      viewer.pdfThumbnails[key] = { status: "loading", pageIndex: pageIndex };
      try {
        var response = await options.send("readArtifactPdfThumbnail", {
          chatId: chatId,
          resourceUri: uri,
          pageIndex: pageIndex
        });
        if (state.activeChatId !== chatId || artifactViewerState(uri) !== viewer) return false;
        var thumbnail = normalizePdfThumbnail(response, uri, pageIndex);
        if (viewer.contentSha256.toLowerCase() !== thumbnail.contentSha256.toLowerCase() ||
            viewer.pageCount !== thumbnail.pageCount) {
          throw new Error("Artifact PDF thumbnail changed exact revision evidence.");
        }
        thumbnail.status = "ready";
        viewer.pdfThumbnails[key] = thumbnail;
        retained = true;
        viewer.pdfThumbnailOrder = viewer.pdfThumbnailOrder.filter(function (value) { return value !== key; });
        viewer.pdfThumbnailOrder.push(key);
        while (viewer.pdfThumbnailOrder.length > 12) {
          var removed = viewer.pdfThumbnailOrder.shift();
          closeData(viewer.pdfThumbnails[removed].data);
          delete viewer.pdfThumbnails[removed];
        }
        return true;
      } catch (error) {
        if (state.activeChatId === chatId && artifactViewerState(uri) === viewer) {
          viewer.pdfThumbnails[key] = {
            status: "error",
            pageIndex: pageIndex,
            message: error.detail || error.message || "PDF thumbnail is unavailable."
          };
          viewer.pdfThumbnailOrder = viewer.pdfThumbnailOrder.filter(function (value) { return value !== key; });
          viewer.pdfThumbnailOrder.push(key);
          while (viewer.pdfThumbnailOrder.length > 12) {
            var removed = viewer.pdfThumbnailOrder.shift();
            closeData(viewer.pdfThumbnails[removed].data);
            delete viewer.pdfThumbnails[removed];
          }
          options.log(error.detail || error.message, "error");
        }
        return false;
      } finally {
        if (!retained) closeData(value(response, "Data", "data", null), chatId);
        viewer.pdfThumbnailPendingCount = Math.max(0, viewer.pdfThumbnailPendingCount - 1);
        if (state.activeChatId === chatId && artifactViewerState(uri) === viewer && options.render) options.render();
      }
    }

    async function changeArtifactViewerPage(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      var direction = request.direction === "previous" ? "previous" : "next";
      var viewer = artifactViewerState(uri);
      if (!viewer || viewer.status !== "ready" || viewer.pending) return false;
      if (direction === "previous") {
        if (viewer.pageIndex <= 0) return false;
        viewer.pageIndex -= 1;
        if (options.render) options.render();
        return true;
      }
      if (viewer.pageIndex + 1 < viewer.pages.length) {
        viewer.pageIndex += 1;
        if (options.render) options.render();
        return true;
      }
      var previous = viewer.pages[viewer.pages.length - 1];
      if (!previous.nextCursor) return false;
      var chatId = state.activeChatId;
      viewer.pending = true;
      try {
        var page = await readPage(
          uri,
          previous.nextCursor,
          previous.offset + previous.returnedCharacters,
          previous.startLine + pageNewlines(previous.text),
          chatId);
        if (page.contentSha256 !== viewerTextContentSha256(viewer) ||
            page.totalCharacters !== previous.totalCharacters ||
            page.viewerKind !== viewer.viewerKind) {
          throw new Error("Artifact viewer continuation changed exact revision evidence.");
        }
        viewer.pages.push(page);
        viewer.pageIndex = viewer.pages.length - 1;
        viewer.viewerLimitReached = viewer.viewerLimitReached || page.viewerLimitReached;
        return true;
      } catch (error) {
        if (state.activeChatId === chatId) options.log(error.detail || error.message, "error");
        return false;
      } finally {
        viewer.pending = false;
        if (state.activeChatId === chatId && options.render) options.render();
      }
    }

    async function loadArtifactViewerFull(request) {
      request = request || {};
      var uri = request.resourceUri || "";
      var viewer = artifactViewerState(uri);
      if (!viewer || viewer.status !== "ready" || viewer.pending || !viewer.fullReadAllowed) return false;
      if (viewer.complete && typeof viewer.fullText === "string") return true;
      var chatId = state.activeChatId;
      viewer.pending = true;
      try {
        var maximumPages = Math.ceil((viewer.pages[0].maximumDocumentCharacters || 0) / 32000) + 1;
        while (viewer.pages.length <= maximumPages) {
          var previous = viewer.pages[viewer.pages.length - 1];
          if (previous.complete) break;
          if (!previous.nextCursor) throw new Error("Full exact source is outside the admitted viewer bound.");
          var page = await readPage(
            uri,
            previous.nextCursor,
            previous.offset + previous.returnedCharacters,
            previous.startLine + pageNewlines(previous.text),
            chatId);
          if (page.contentSha256 !== viewerTextContentSha256(viewer) ||
              page.totalCharacters !== previous.totalCharacters ||
              page.viewerKind !== viewer.viewerKind) {
            throw new Error("Artifact viewer continuation changed exact revision evidence.");
          }
          viewer.pages.push(page);
        }
        var last = viewer.pages[viewer.pages.length - 1];
        if (!last.complete || !last.sourceComplete) throw new Error("Full exact source is unavailable.");
        var fullText = viewer.pages.map(function (page) { return page.text; }).join("");
        if (fullText.length !== last.totalCharacters) throw new Error("Artifact viewer pages are incomplete.");
        viewer.fullText = fullText;
        viewer.complete = true;
        viewer.pageIndex = 0;
        if (typeof options.applyArtifactViewerText === "function") {
          options.applyArtifactViewerText(uri, viewerTextContentSha256(viewer), fullText);
        }
        return true;
      } catch (error) {
        options.log(error.detail || error.message, "error");
        return false;
      } finally {
        viewer.pending = false;
        if (state.activeChatId === chatId && options.render) options.render();
      }
    }

    function downloadArtifactViewer(request) {
      request = request || {};
      var viewer = artifactViewerState(request.resourceUri || "");
      if (!viewer || !viewer.complete || typeof viewer.fullText !== "string" ||
          typeof options.downloadArtifactText !== "function") {
        throw new Error("Full exact artifact source is not available for download.");
      }
      return options.downloadArtifactText({
        text: viewer.fullText,
        title: viewer.title,
        mimeType: viewer.mimeType,
        resourceUri: viewer.resourceUri,
        contentSha256: viewer.contentSha256
      });
    }

    return {
      closeAll: closeAll,
      artifactImageThumbnailState: artifactImageThumbnailState,
      artifactViewerState: artifactViewerState,
      changeArtifactPdfPage: changeArtifactPdfPage,
      changeArtifactViewerPage: changeArtifactViewerPage,
      downloadArtifactViewer: downloadArtifactViewer,
      loadArtifactImage: loadArtifactImage,
      loadArtifactImageThumbnail: loadArtifactImageThumbnail,
      loadArtifactPdf: loadArtifactPdf,
      loadArtifactPdfThumbnail: loadArtifactPdfThumbnail,
      loadArtifactViewer: loadArtifactViewer,
      loadArtifactViewerFull: loadArtifactViewerFull,
      selectArtifactPdfPage: selectArtifactPdfPage
    };
  }

  window.RNAssistantArtifactViewerActions = { create: create };
}());
