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
      return viewerState;
    }

    function artifactViewerState(uri) {
      return cache()[uri] || null;
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
      if (returnedUri !== uri || (viewerKind !== "text" && viewerKind !== "markdown") ||
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
        if (page.contentSha256 !== viewer.contentSha256 || page.totalCharacters !== previous.totalCharacters ||
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
          if (page.contentSha256 !== viewer.contentSha256 || page.totalCharacters !== previous.totalCharacters ||
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
          options.applyArtifactViewerText(uri, viewer.contentSha256, fullText);
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
      changeArtifactViewerPage: changeArtifactViewerPage,
      downloadArtifactViewer: downloadArtifactViewer,
      loadArtifactViewer: loadArtifactViewer,
      loadArtifactViewerFull: loadArtifactViewerFull
    };
  }

  window.RNAssistantArtifactViewerActions = { create: create };
}());
