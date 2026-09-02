(function () {
  "use strict";

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function create(options) {
    options = options || {};
    var state = options.state;

    function cache() {
      state.artifactViewerPages = state.artifactViewerPages || {};
      return state.artifactViewerPages;
    }

    function cacheViewer(uri, viewerState) {
      var viewers = cache();
      if (!viewers[uri] && Object.keys(viewers).length >= 8) delete viewers[Object.keys(viewers)[0]];
      viewers[uri] = viewerState;
      var mediaUris = Object.keys(viewers).filter(function (key) {
        return viewers[key] && (viewers[key].viewerKind === "image" || viewers[key].viewerKind === "pdf");
      });
      while (mediaUris.length > 2) delete viewers[mediaUris.shift()];
      return viewerState;
    }

    function artifactViewerState(uri) {
      return cache()[uri] || null;
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

    function base64ByteLength(content) {
      content = String(content || "");
      if (!content || content.length % 4 !== 0 || !/^[A-Za-z0-9+/]+={0,2}$/.test(content)) return -1;
      var padding = content.slice(-2) === "==" ? 2 : (content.slice(-1) === "=" ? 1 : 0);
      return content.length / 4 * 3 - padding;
    }

    function normalizeImage(response, uri) {
      response = response || {};
      var returnedUri = value(response, "ResourceUri", "resourceUri", "") || "";
      var viewerKind = String(value(response, "ViewerKind", "viewerKind", "") || "").toLowerCase();
      var mimeType = String(value(response, "MimeType", "mimeType", "") || "").split(";", 1)[0].toLowerCase();
      var hash = value(response, "ContentSha256", "contentSha256", "") || "";
      var byteLength = Number(value(response, "ByteLength", "byteLength", -1));
      var base64Content = value(response, "Base64Content", "base64Content", "") || "";
      if (returnedUri !== uri || viewerKind !== "image" ||
          ["image/jpeg", "image/png", "image/gif", "image/webp"].indexOf(mimeType) < 0 ||
          !/^[a-f0-9]{64}$/i.test(hash) || byteLength <= 0 || byteLength > 20 * 1024 * 1024 ||
          base64ByteLength(base64Content) !== byteLength) {
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
        base64Content: base64Content
      };
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
      var imageBase64Content = value(response, "ImageBase64Content", "imageBase64Content", "") || "";
      if (returnedUri !== uri || viewerKind !== "pdf" || !/^[a-f0-9]{64}$/i.test(hash) ||
          pageIndex !== expectedPageIndex || !Number.isInteger(pageCount) || pageCount <= 0 || pageCount > 10000 ||
          !Number.isInteger(width) || width <= 0 || width > maximumDimension ||
          !Number.isInteger(height) || height <= 0 || height > maximumDimension ||
          imageMimeType !== "image/jpeg" || !/^[a-f0-9]{64}$/i.test(imageHash) ||
          imageByteLength <= 0 || imageByteLength > maximumBytes ||
          base64ByteLength(imageBase64Content) !== imageByteLength) {
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
        imageBase64Content: imageBase64Content
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
      if (state.activeChatId !== chatId) throw new Error("Artifact viewer read belongs to another chat.");
      return normalizePage(response, uri, expectedOffset, startLine);
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
      cacheViewer(uri, { status: "loading", resourceUri: uri, viewerKind: "image" });
      try {
        var response = await options.send("readArtifactImage", {
          chatId: chatId,
          resourceUri: uri
        });
        if (state.activeChatId !== chatId) throw new Error("Artifact image read belongs to another chat.");
        cacheViewer(uri, normalizeImage(response, uri));
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
      }
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
      cacheViewer(uri, { status: "loading", resourceUri: uri, viewerKind: "pdf" });
      try {
        var responses = await Promise.all([
          options.send("readArtifactPdfInfo", { chatId: chatId, resourceUri: uri }),
          options.send("readArtifactPdfPage", { chatId: chatId, resourceUri: uri, pageIndex: 0 }),
          readPage(uri, null, 0, 1, chatId)
        ]);
        if (state.activeChatId !== chatId) throw new Error("Artifact PDF read belongs to another chat.");
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
        viewer.pdfPage = page;
        return true;
      } catch (error) {
        if (state.activeChatId === chatId && artifactViewerState(uri) === viewer) {
          options.log(error.detail || error.message, "error");
        }
        return false;
      } finally {
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
      if (current && (current.status === "loading" || current.status === "ready" || current.status === "error")) {
        return current.status === "ready";
      }
      var pendingCount = Number(viewer.pdfThumbnailPendingCount || 0);
      viewer.pdfThumbnailPendingCount = Number.isInteger(pendingCount) && pendingCount >= 0 ? pendingCount : 0;
      if (viewer.pdfThumbnailPendingCount >= 4) return false;
      var chatId = state.activeChatId;
      viewer.pdfThumbnailPendingCount += 1;
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
        viewer.pdfThumbnailOrder = viewer.pdfThumbnailOrder.filter(function (value) { return value !== key; });
        viewer.pdfThumbnailOrder.push(key);
        while (viewer.pdfThumbnailOrder.length > 24) {
          var removed = viewer.pdfThumbnailOrder.shift();
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
          while (viewer.pdfThumbnailOrder.length > 24) {
            delete viewer.pdfThumbnails[viewer.pdfThumbnailOrder.shift()];
          }
          options.log(error.detail || error.message, "error");
        }
        return false;
      } finally {
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
      artifactViewerState: artifactViewerState,
      changeArtifactPdfPage: changeArtifactPdfPage,
      changeArtifactViewerPage: changeArtifactViewerPage,
      downloadArtifactViewer: downloadArtifactViewer,
      loadArtifactImage: loadArtifactImage,
      loadArtifactPdf: loadArtifactPdf,
      loadArtifactPdfThumbnail: loadArtifactPdfThumbnail,
      loadArtifactViewer: loadArtifactViewer,
      loadArtifactViewerFull: loadArtifactViewerFull,
      selectArtifactPdfPage: selectArtifactPdfPage
    };
  }

  window.RNAssistantArtifactViewerActions = { create: create };
}());
