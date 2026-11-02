(function () {
  "use strict";
  // Shared artifact presentation; data and scope are supplied by the reader owner.

  function prop(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function artifactId(artifact) {
    return prop(artifact, "Id", "id", "");
  }

  function artifactKind(artifact) {
    var kind = String(prop(artifact, "DisplayKind", "displayKind", prop(artifact, "Kind", "kind", "file")) || "file").toLowerCase();
    return kind === "plan_document" ? "plan" : kind;
  }

  function artifactInlineText(artifact) {
    return prop(artifact, "InlineText", "inlineText", "") || "";
  }

  function artifactInlineTruncated(artifact) {
    return !!prop(artifact, "InlineTruncated", "inlineTruncated", false);
  }

  function artifactMimeType(artifact) {
    return String(prop(artifact, "MimeType", "mimeType", "") || "").toLowerCase();
  }

  function appendContentLabel(root, text) {
    var label = document.createElement("div");
    label.className = "artifact-content-label";
    label.textContent = text;
    root.appendChild(label);
  }

  function artifactViewerKind(artifact) {
    if (isHtmlFileArtifact(artifact)) return "text";
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var kind = artifactKind(artifact);
    var title = String(prop(artifact, "Title", "title", "") || "");
    if (kind === "sequence") return "sequence";
    if (mediaType === "application/json" || /\+json$/.test(mediaType)) return kind === "task_list" ? "task_list" : "json";
    if (kind === "task_list" || mediaType === "application/vnd.rnassistant.task-list+json") return "task_list";
    if (kind === "image" || /^image\/(?:jpeg|png|gif|webp)$/.test(mediaType)) return "image";
    if (mediaType === "application/pdf") return "pdf";
    if (mediaType === "text/markdown" || mediaType === "text/x-markdown" || kind === "plan" || kind === "markdown" ||
        /\.(?:md|markdown|mdx)$/i.test(title)) return "markdown";
    if (mediaType === "text/html" || mediaType === "application/xhtml+xml" ||
        mediaType === "application/json" || /\+json$/.test(mediaType)) return "";
    if (/^text\//.test(mediaType) || mediaType === "application/xml" || /\+xml$/.test(mediaType) ||
        mediaType === "application/javascript" || mediaType === "application/ecmascript" ||
        mediaType === "application/sql" || mediaType === "application/yaml" || mediaType === "application/x-yaml" ||
        /\.(?:txt|log|csv|tsv|xml|ya?ml|ini|cfg|conf|cs|vb|[cm]?js|css|scss|less|py|rb|java|kts?|c|h|cpp|hpp|sql|sh|ps1|bat)$/i.test(title)) {
      return "text";
    }
    return "";
  }

  function exactArtifactUri(artifact) {
    var exact = libraryRevision(artifact);
    return prop(exact, "ResourceUri", "resourceUri", prop(artifact, "ResourceUri", "resourceUri", "")) || "";
  }

  function appendViewerError(root, message) {
    var error = document.createElement("div");
    error.className = "artifact-detail-error";
    error.textContent = message;
    root.appendChild(error);
  }

  function renderThumbnailProjection(root, resourceUri, thumbnail, title) {
    if (typeof window.renderArtifactThumbnailNode === "function") {
      window.renderArtifactThumbnailNode(root, resourceUri, thumbnail, title);
      return thumbnail ? thumbnail.status : "";
    }
    root.className = "rn-artifact-thumbnail";
    root.dataset.resourceUri = resourceUri;
    if (thumbnail && thumbnail.status === "ready") {
      var image = document.createElement("img");
      image.alt = title || "";
      image.src = thumbnail.data.objectUrl;
      root.appendChild(image);
      return "ready";
    }
    return thumbnail ? thumbnail.status : "";
  }

  function appendTypedArtifactViewer(root, artifact, actions, draftText) {
    actions = actions || {};
    var expectedKind = artifactViewerKind(artifact);
    if (!expectedKind) return false;
    if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has(expectedKind)) {
      appendViewerError(root, "Typed artifact viewer is unavailable.");
      return true;
    }
    if (typeof draftText === "string") {
      var draftTarget = document.createElement("div");
      draftTarget.className = "artifact-viewer-host artifact-typed-viewer product-viewer";
      root.appendChild(draftTarget);
      window.RNAssistantViewerRegistry.mount("markdown", draftTarget, {
        text: draftText,
        fullText: draftText,
        complete: true,
        sourceComplete: true,
        draft: true,
        onCopy: window.copyTextResult
      });
      return true;
    }
    if (expectedKind === "task_list" && artifactInlineText(artifact)) {
      var taskTarget = document.createElement("div");
      taskTarget.className = "artifact-viewer-host artifact-typed-viewer product-viewer";
      root.appendChild(taskTarget);
      window.RNAssistantViewerRegistry.mount("task_list", taskTarget, {
        text: artifactInlineText(artifact), onCopy: window.copyTextResult
      });
      return true;
    }
    var uri = exactArtifactUri(artifact);
    if (!uri) {
      appendViewerError(root, "Artifact has no exact revision URI.");
      return true;
    }
    var viewer = typeof actions.artifactViewerState === "function" ? actions.artifactViewerState(uri) : null;
    if (!viewer) {
      appendContentLabel(root, expectedKind === "image" ? "Загружаю изображение…" :
        (expectedKind === "pdf" ? "Готовлю PDF preview…" : "Загружаю exact source…"));
      if (expectedKind === "image" && typeof actions.loadArtifactImage === "function") {
        actions.loadArtifactImage({ resourceUri: uri });
      } else if (expectedKind === "pdf" && typeof actions.loadArtifactPdf === "function") {
        actions.loadArtifactPdf({ resourceUri: uri });
      } else if (typeof actions.loadArtifactViewer === "function") {
        actions.loadArtifactViewer({ resourceUri: uri });
      }
      return true;
    }
    if (viewer.status === "loading") {
      appendContentLabel(root, "Загружаю exact source…");
      return true;
    }
    if (viewer.status === "error") {
      appendViewerError(root, viewer.message || "Artifact source is unavailable.");
      return true;
    }
    if (expectedKind === "image") {
      if (viewer.viewerKind !== "image") {
        appendViewerError(root, "Artifact viewer state does not match the selected revision.");
        return true;
      }
      var imageTarget = document.createElement("div");
      imageTarget.className = "artifact-viewer-host artifact-typed-viewer product-viewer";
      root.appendChild(imageTarget);
      var gallery = typeof actions.imageGalleryContext === "function"
        ? actions.imageGalleryContext(artifact)
        : null;
      var galleryItems = gallery && Array.isArray(gallery.items) ? gallery.items : [];
      var galleryIndex = galleryItems.length
        ? Math.max(0, Math.min(Number(gallery.currentIndex || 0), galleryItems.length - 1))
        : 0;
      var galleryItem = galleryItems[galleryIndex] || null;
      if (galleryItem && galleryItem.contentSha256 &&
          String(galleryItem.contentSha256).toLowerCase() !== String(viewer.contentSha256 || "").toLowerCase()) {
        appendViewerError(root, "Image gallery changed exact revision evidence.");
        return true;
      }
      var selectGallery = function (index) {
        return typeof actions.selectImageGalleryItem === "function"
          ? actions.selectImageGalleryItem(index)
          : false;
      };
      var sequence = galleryItems.length > 1 ? {
        ariaLabel: "Изображения коллекции",
        title: "Изображения",
        count: galleryItems.length,
        currentIndex: galleryIndex,
        scrollOffset: Number(gallery.scrollOffset || 0),
        getItem: function (index) { return galleryItems[index] || null; },
        itemLabel: function (index, item) {
          return (item && item.title || "Изображение") + " · " + (index + 1) + " из " + galleryItems.length;
        },
        onScroll: function (offset) { gallery.scrollOffset = Number(offset || 0); },
        onSelect: function (index) { return selectGallery(index); },
        onRequest: typeof actions.loadArtifactImageThumbnail === "function"
          ? function (index, item) {
            if (item && item.resourceUri) actions.loadArtifactImageThumbnail({ resourceUri: item.resourceUri });
          }
          : null,
        renderItem: function (index, item, preview) {
          if (!item || !item.resourceUri) return { status: "error", message: "Exact image URI is unavailable." };
          var thumbnail = typeof actions.artifactImageThumbnailState === "function"
            ? actions.artifactImageThumbnailState(item.resourceUri)
            : null;
          if (thumbnail && thumbnail.status === "ready" && item.contentSha256 &&
              String(thumbnail.contentSha256 || "").toLowerCase() !== String(item.contentSha256).toLowerCase()) {
            return { status: "error", message: "Thumbnail changed exact revision evidence." };
          }
          var node = document.createElement("span");
          node.dataset.title = item.title || "";
          preview.appendChild(node);
          return renderThumbnailProjection(node, item.resourceUri, thumbnail, item.title);
        }
      } : null;
      window.RNAssistantViewerRegistry.mount("image", imageTarget, {
        title: viewer.title,
        mimeType: viewer.mimeType,
        contentSha256: viewer.contentSha256,
        byteLength: viewer.byteLength,
        data: viewer.data,
        navigation: sequence ? {
          label: (galleryIndex + 1) + " / " + galleryItems.length,
          hasPrevious: galleryIndex > 0,
          hasNext: galleryIndex + 1 < galleryItems.length,
          onPrevious: function () { return selectGallery(galleryIndex - 1); },
          onNext: function () { return selectGallery(galleryIndex + 1); },
          previousLabel: "Предыдущее изображение",
          nextLabel: "Следующее изображение",
          unavailableLabel: "Изображение недоступно"
        } : null,
        sequence: sequence
      });
      return true;
    }
    if (expectedKind === "pdf") {
      var pdfTextPages = viewer.pages || [];
      var pdfTextPageIndex = Math.max(0, Math.min(
        Number(viewer.pageIndex || 0), Math.max(0, pdfTextPages.length - 1)));
      var pdfTextPage = pdfTextPages[pdfTextPageIndex];
      if (viewer.viewerKind !== "pdf" || !viewer.pdfPage || !pdfTextPage) {
        appendViewerError(root, "Artifact viewer state does not match the selected revision.");
        return true;
      }
      var pdfTarget = document.createElement("div");
      pdfTarget.className = "artifact-viewer-host artifact-typed-viewer product-viewer";
      root.appendChild(pdfTarget);
      window.RNAssistantViewerRegistry.mount("pdf", pdfTarget, {
        title: viewer.title,
        pageCount: viewer.pageCount,
        pageTextLengths: viewer.pageTextLengths,
        extractedCharacters: viewer.extractedCharacters,
        textTruncated: viewer.textTruncated,
        extractionWarning: viewer.extractionWarning,
        pending: viewer.pending,
        initialTab: viewer.activeTab || "pages",
        onTabChange: function (tab) { viewer.activeTab = tab; },
        page: viewer.pdfPage,
        thumbnails: viewer.pdfThumbnails || {},
        thumbnailScrollTop: Number(viewer.pdfThumbnailScrollTop || 0),
        onThumbnailScroll: function (scrollTop) { viewer.pdfThumbnailScrollTop = Number(scrollTop || 0); },
        onThumbnailRequest: typeof actions.loadArtifactPdfThumbnail === "function"
          ? function (pageIndex) {
            return actions.loadArtifactPdfThumbnail({ resourceUri: uri, pageIndex: pageIndex });
          }
          : null,
        onPageSelect: typeof actions.selectArtifactPdfPage === "function"
          ? function (pageIndex) {
            return actions.selectArtifactPdfPage({ resourceUri: uri, pageIndex: pageIndex });
          }
          : null,
        textPage: pdfTextPage,
        fullText: viewer.complete ? viewer.fullText : null,
        textComplete: viewer.complete === true,
        sourceComplete: viewer.sourceComplete,
        viewerLimitReached: viewer.viewerLimitReached,
        fullReadAllowed: viewer.fullReadAllowed && !viewer.pending,
        hasTextPrevious: !viewer.pending && pdfTextPageIndex > 0,
        hasTextNext: !viewer.pending &&
          (pdfTextPageIndex + 1 < pdfTextPages.length || !!pdfTextPage.nextCursor),
        onPrevious: function () { return actions.changeArtifactPdfPage({ resourceUri: uri, direction: "previous" }); },
        onNext: function () { return actions.changeArtifactPdfPage({ resourceUri: uri, direction: "next" }); },
        onTextPrevious: typeof actions.changeArtifactViewerPage === "function"
          ? function () { return actions.changeArtifactViewerPage({ resourceUri: uri, direction: "previous" }); }
          : null,
        onTextNext: typeof actions.changeArtifactViewerPage === "function"
          ? function () { return actions.changeArtifactViewerPage({ resourceUri: uri, direction: "next" }); }
          : null,
        onLoadTextFull: typeof actions.loadArtifactViewerFull === "function"
          ? function () { return actions.loadArtifactViewerFull({ resourceUri: uri }); }
          : null,
        onCopy: window.copyTextResult
      });
      return true;
    }
    var pages = viewer.pages || [];
    var pageIndex = Math.max(0, Math.min(Number(viewer.pageIndex || 0), Math.max(0, pages.length - 1)));
    var page = pages[pageIndex];
    var sourceKind = ["json", "task_list", "sequence"].indexOf(expectedKind) >= 0 ? "text" : expectedKind;
    if (!page || viewer.viewerKind !== sourceKind) {
      appendViewerError(root, "Artifact viewer state does not match the selected revision.");
      return true;
    }
    var target = document.createElement("div");
    target.className = "artifact-viewer-host artifact-typed-viewer product-viewer";
    root.appendChild(target);
    var needsComplete = ["json", "task_list", "sequence"].indexOf(expectedKind) >= 0;
    var format = needsComplete && !viewer.complete ? "text" : expectedKind;
    if (needsComplete && !viewer.complete) appendContentLabel(root, "Структурный просмотр доступен после полного чтения. Сейчас показана страница исходника.");
    window.RNAssistantViewerRegistry.mount(format, target, {
      text: viewer.complete ? viewer.fullText : page.text,
      pageText: page.text,
      completeness: viewer.complete ? "full" : "fragment",
      fullText: viewer.complete ? viewer.fullText : null,
      complete: viewer.complete === true,
      offset: page.offset,
      startLine: page.startLine,
      totalCharacters: page.totalCharacters,
      sourceComplete: viewer.sourceComplete,
      viewerLimitReached: viewer.viewerLimitReached,
      fullReadAllowed: viewer.fullReadAllowed && !viewer.pending,
      hasPrevious: !viewer.pending && pageIndex > 0,
      hasNext: !viewer.pending && (pageIndex + 1 < pages.length || !!page.nextCursor),
      onPrevious: function () { return actions.changeArtifactViewerPage({ resourceUri: uri, direction: "previous" }); },
      onNext: function () { return actions.changeArtifactViewerPage({ resourceUri: uri, direction: "next" }); },
      onLoadFull: function () { return actions.loadArtifactViewerFull({ resourceUri: uri }); },
      onCopy: window.copyTextResult,
      onDownload: function () { return actions.downloadArtifactViewer({ resourceUri: uri }); }
    });
    return true;
  }

  function libraryHead(artifact) {
    var visuals = window.RNAssistantArtifactVisuals;
    return visuals && typeof visuals.libraryHead === "function" ? visuals.libraryHead(artifact) : null;
  }

  function libraryRevision(artifact) {
    var embedded = prop(artifact, "LibraryRevision", "libraryRevision", null);
    if (embedded) return embedded;
    var id = String(artifactId(artifact) || "").toLowerCase();
    var history = prop(libraryHead(artifact), "History", "history", []) || [];
    return history.filter(function (revision) {
      return String(prop(revision, "ArtifactId", "artifactId", "")).toLowerCase() === id;
    })[0] || null;
  }

  function isHtmlFileArtifact(artifact) {
    var kind = artifactKind(artifact);
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var title = String(prop(artifact, "Title", "title", "") || "");
    var htmlName = /\.html?$/i.test(title);
    return (kind === "attachment" || kind === "file") &&
      (mediaType === "text/html" || htmlName);
  }

  window.RNAssistantArtifactViewers = {
    prop: prop,
    artifactId: artifactId,
    artifactKind: artifactKind,
    artifactInlineText: artifactInlineText,
    artifactInlineTruncated: artifactInlineTruncated,
    artifactMimeType: artifactMimeType,
    appendContentLabel: appendContentLabel,
    artifactViewerKind: artifactViewerKind,
    exactArtifactUri: exactArtifactUri,
    appendViewerError: appendViewerError,
    renderThumbnailProjection: renderThumbnailProjection,
    appendTypedArtifactViewer: appendTypedArtifactViewer,
    libraryHead: libraryHead,
    libraryRevision: libraryRevision,
    isHtmlFileArtifact: isHtmlFileArtifact
  };
}());
