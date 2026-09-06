(function () {
  "use strict";

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

  function isJsonArtifact(artifact) {
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var kind = artifactKind(artifact);
    return mediaType === "application/json" || /\+json$/.test(mediaType) || kind === "json" || kind === "data";
  }

  function isChartArtifact(artifact) {
    return artifactKind(artifact) === "chart" ||
      artifactMimeType(artifact).split(";", 1)[0].trim() === "application/vnd.rnassistant.chart+json";
  }

  function clearDetail(root) {
    if (typeof root.__rnArtifactDetailCleanup === "function") root.__rnArtifactDetailCleanup();
    root.__rnArtifactDetailCleanup = null;
    if (window.RNAssistantViewerRegistry) {
      [".artifact-viewer-host", ".artifact-json-viewer", ".artifact-typed-viewer"].forEach(function (selector) {
        Array.prototype.slice.call(root.querySelectorAll(selector)).forEach(function (target) {
          window.RNAssistantViewerRegistry.unmount(target);
        });
      });
    }
    root.replaceChildren();
  }

  function appendContentLabel(root, text) {
    var label = document.createElement("div");
    label.className = "artifact-content-label";
    label.textContent = text;
    root.appendChild(label);
  }

  function appendJsonContent(root, text, completeness, label) {
    if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
      throw new Error("JSON viewer is unavailable.");
    }
    appendContentLabel(root, label);
    var target = document.createElement("div");
    target.className = "artifact-viewer-host artifact-json-viewer";
    root.appendChild(target);
    window.RNAssistantViewerRegistry.mount("json", target, {
      text: String(text),
      completeness: completeness,
      mode: "tree",
      onCopy: window.copyTextResult
    });
  }

  function artifactViewerKind(artifact) {
    if (isUploadedHtmlArtifact(artifact)) return "text";
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var kind = artifactKind(artifact);
    var title = String(prop(artifact, "Title", "title", "") || "");
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
      draftTarget.className = "artifact-viewer-host artifact-typed-viewer";
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
    if (expectedKind === "task_list") {
      var taskTarget = document.createElement("div");
      taskTarget.className = "artifact-viewer-host artifact-typed-viewer";
      root.appendChild(taskTarget);
      window.RNAssistantViewerRegistry.mount("task_list", taskTarget, {
        text: artifactInlineText(artifact)
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
      imageTarget.className = "artifact-viewer-host artifact-typed-viewer";
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
      pdfTarget.className = "artifact-viewer-host artifact-typed-viewer";
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
          : null
      });
      return true;
    }
    var pages = viewer.pages || [];
    var pageIndex = Math.max(0, Math.min(Number(viewer.pageIndex || 0), Math.max(0, pages.length - 1)));
    var page = pages[pageIndex];
    if (!page || viewer.viewerKind !== expectedKind) {
      appendViewerError(root, "Artifact viewer state does not match the selected revision.");
      return true;
    }
    var target = document.createElement("div");
    target.className = "artifact-viewer-host artifact-typed-viewer";
    root.appendChild(target);
    window.RNAssistantViewerRegistry.mount(expectedKind, target, {
      text: page.text,
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

  function appendArtifactContent(root, artifact, actions) {
    var inline = artifactInlineText(artifact);
    if (inline && isChartArtifact(artifact) &&
        typeof window.tryRenderChartArtifactJson === "function") {
      var chart = window.tryRenderChartArtifactJson(inline, {});
      if (chart) {
        root.appendChild(chart);
        return;
      }
    }
    if (appendTypedArtifactViewer(root, artifact, actions)) return;
    if (inline) {
      if (isJsonArtifact(artifact)) {
        appendJsonContent(root, inline, artifactInlineTruncated(artifact) ? "preview" : "full", "Содержимое JSON");
      } else {
        appendContentLabel(root, "Для этого формата typed viewer ещё не подключён.");
      }
      return;
    }
    appendContentLabel(root, "Для этого формата preview ещё не подключён.");
  }

  function planStableId(artifact) {
    return storedPlanId(artifact);
  }

  function storedPlanId(artifact) {
    try {
      var metadata = JSON.parse(prop(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
      if (metadata.planId || metadata.PlanId) return metadata.planId || metadata.PlanId;
    } catch (ignore) {}
    return artifactId(artifact);
  }

  function typeLabel(kind) {
    var labels = { attachment: "Вложение", image: "Изображение", audio: "Аудио", file: "Файл", markdown: "Markdown", plan: "План", plan_document: "План", task_list: "Task list", html_workspace: "HTML workspace", chart: "Диаграмма", compaction: "Checkpoint", tool_result: "Результат" };
    return labels[kind] || kind;
  }

  function planSummary(artifact) {
    try {
      var metadata = JSON.parse(prop(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
      return metadata.status || metadata.Status || "draft";
    } catch (ignore) { return "draft"; }
  }

  function libraryHead(artifact) {
    var visuals = window.RNAssistantArtifactVisuals;
    return visuals && typeof visuals.libraryHead === "function" ? visuals.libraryHead(artifact) : null;
  }

  function libraryRevision(artifact) {
    var id = String(artifactId(artifact) || "").toLowerCase();
    var history = prop(libraryHead(artifact), "History", "history", []) || [];
    return history.filter(function (revision) {
      return String(prop(revision, "ArtifactId", "artifactId", "")).toLowerCase() === id;
    })[0] || null;
  }

  function isUploadedHtmlArtifact(artifact) {
    var head = libraryHead(artifact);
    var resourceClass = String(prop(head, "ResourceClass", "resourceClass", "") || "").toLowerCase();
    var kind = artifactKind(artifact);
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var title = String(prop(artifact, "Title", "title", "") || "");
    var htmlName = /\.html?$/i.test(title);
    return resourceClass === "immutable_original" &&
      (kind === "attachment" || kind === "file") &&
      (mediaType === "text/html" || htmlName);
  }

  function uploadedHtmlTargetPath(artifact) {
    var title = String(prop(artifact, "Title", "title", "index.html") || "index.html").split(/[\\/]/).pop();
    return /\.html?$/i.test(title) ? title : "index.html";
  }

  function appendUploadedHtml(root, artifact, actions) {
    actions = actions || {};
    var uri = exactArtifactUri(artifact);
    var notice = document.createElement("div");
    notice.className = "artifact-inert-html-note";
    notice.textContent = "Загруженный HTML инертен: до явного импорта показывается только экранированный исходник.";
    root.appendChild(notice);

    var actionBox = document.createElement("div");
    actionBox.className = "artifact-inert-html-actions";
    var viewer = typeof actions.artifactViewerState === "function" ? actions.artifactViewerState(uri) : null;
    var load = document.createElement("button");
    load.type = "button";
    load.className = "secondary";
    load.textContent = viewer && viewer.status === "loading" ? "Загружаю…" : "Показать исходник";
    load.disabled = !uri || !!viewer && (viewer.status === "loading" || viewer.status === "ready") ||
      typeof actions.loadArtifactViewer !== "function" || !!state.bridgeUnavailable;
    load.addEventListener("click", function () {
      if (typeof actions.loadArtifactViewer === "function") {
        actions.loadArtifactViewer({ resourceUri: uri });
      }
    });
    actionBox.appendChild(load);
    var importButton = document.createElement("button");
    importButton.type = "button";
    importButton.className = "accent-soft";
    importButton.textContent = "Импортировать в HTML workspace";
    importButton.disabled = !uri || typeof actions.importUploadedHtml !== "function" || !!state.bridgeUnavailable;
    importButton.addEventListener("click", function () {
      if (typeof actions.importUploadedHtml === "function") {
        actions.importUploadedHtml({
          sourceResourceUri: uri,
          targetPath: uploadedHtmlTargetPath(artifact)
        });
      }
    });
    actionBox.appendChild(importButton);
    root.appendChild(actionBox);

    if (viewer) appendTypedArtifactViewer(root, artifact, actions);
  }

  function versionLabel(artifact) {
    var visuals = window.RNAssistantArtifactVisuals;
    return visuals && typeof visuals.versionLabel === "function" ? visuals.versionLabel(artifact) : "";
  }

  function appendMetadata(root, artifact) {
    var exact = libraryRevision(artifact);
    var head = libraryHead(artifact);
    var resourceUri = prop(exact, "ResourceUri", "resourceUri", prop(artifact, "ResourceUri", "resourceUri", "")) || "";
    var parent = prop(exact, "ParentResourceUri", "parentResourceUri", "") || "";
    var metadata = document.createElement("dl");
    metadata.className = "artifact-metadata";
    [
      ["Тип", typeLabel(artifactKind(artifact))],
      ["Формат", prop(artifact, "MimeType", "mimeType", "—") || "—"],
      ["Путь", prop(artifact, "RelativePath", "relativePath", "") || ""],
      ["Метка", versionLabel(artifact)],
      ["Точная ссылка", resourceUri],
      ["Производный от", prop(head, "DerivedFromResourceUri", "derivedFromResourceUri", "") || ""],
      ["Источник", prop(artifact, "SourceMessageId", "sourceMessageId", "") || ""],
      ["Родитель", parent]
    ].filter(function (pair) { return !!pair[1]; }).forEach(function (pair) {
      var term = document.createElement("dt");
      var itemValue = document.createElement("dd");
      term.textContent = pair[0];
      itemValue.textContent = pair[1];
      metadata.appendChild(term);
      metadata.appendChild(itemValue);
    });
    root.appendChild(metadata);
  }

  function appendLibraryHistory(root, artifact, actions) {
    actions = actions || {};
    var head = libraryHead(artifact);
    var history = prop(head, "History", "history", []) || [];
    var resourceClass = String(prop(head, "ResourceClass", "resourceClass", "") || "").toLowerCase();
    if ((resourceClass !== "versioned_document" && resourceClass !== "versioned_aggregate") || history.length < 2) return;
    var isPlan = artifactKind(artifact) === "plan";
    var headArtifactId = prop(head, "ArtifactId", "artifactId", "") || "";
    var details = document.createElement("details");
    details.className = "artifact-history";
    var summary = document.createElement("summary");
    summary.textContent = "История · " + history.length;
    details.appendChild(summary);
    history.forEach(function (revision) {
      var row = document.createElement("div");
      row.className = "artifact-history-row";
      var copy = document.createElement("div");
      copy.className = "artifact-history-copy";
      var title = document.createElement("strong");
      var number = Number(prop(revision, "Revision", "revision", 1) || 1);
      var relation = String(prop(revision, "Relation", "relation", "") || "").toLowerCase();
      title.textContent = "v" + number + (relation === "head" ? " · текущая" : (relation === "branch" ? " · другая ветка" : ""));
      var uri = prop(revision, "ResourceUri", "resourceUri", "") || "";
      var exact = document.createElement("code");
      exact.textContent = uri;
      copy.appendChild(title);
      var provenance = [];
      var createdUtc = prop(revision, "CreatedUtc", "createdUtc", "") || "";
      if (createdUtc) {
        var created = new Date(createdUtc);
        provenance.push(isNaN(created.getTime()) ? createdUtc : created.toLocaleString());
      }
      var sourceMessageId = prop(revision, "SourceMessageId", "sourceMessageId", "") || "";
      var runId = prop(revision, "RunId", "runId", "") || "";
      if (sourceMessageId) provenance.push("message " + sourceMessageId);
      if (runId) provenance.push("run " + runId);
      if (provenance.length) {
        var source = document.createElement("span");
        source.className = "artifact-history-source";
        source.textContent = provenance.join(" · ");
        copy.appendChild(source);
      }
      copy.appendChild(exact);
      var parentUri = prop(revision, "ParentResourceUri", "parentResourceUri", "") || "";
      var restoredFromUri = prop(revision, "RestoredFromResourceUri", "restoredFromResourceUri", "") || "";
      if (parentUri || restoredFromUri) {
        var relationText = document.createElement("span");
        relationText.className = "artifact-history-relation";
        relationText.textContent = restoredFromUri
          ? "Восстановлено из " + restoredFromUri
          : "Родитель " + parentUri;
        copy.appendChild(relationText);
      }
      row.appendChild(copy);
      var actionBox = document.createElement("div");
      actionBox.className = "artifact-history-actions";
      var revisionArtifactId = prop(revision, "ArtifactId", "artifactId", "") || "";
      var isHead = !!prop(revision, "IsHead", "isHead", relation === "head");
      if (isPlan && !isHead && typeof actions.restorePlanRevision === "function") {
        var restoreButton = document.createElement("button");
        restoreButton.type = "button";
        restoreButton.className = "secondary compact";
        restoreButton.textContent = "Восстановить";
        restoreButton.disabled = !!state.bridgeUnavailable || !revisionArtifactId || !headArtifactId ||
          String(state.activePlanDocumentArtifactId || "").toLowerCase() !== String(headArtifactId).toLowerCase();
        restoreButton.addEventListener("click", function () {
          actions.restorePlanRevision({
            planId: planStableId(artifact),
            expectedRevisionArtifactId: headArtifactId,
            sourceRevisionArtifactId: revisionArtifactId,
            revision: number
          });
        });
        actionBox.appendChild(restoreButton);
      }
      var button = document.createElement("button");
      button.type = "button";
      button.className = "secondary compact";
      button.textContent = "Копировать";
      button.disabled = !uri;
      button.addEventListener("click", function () {
        if (!uri || typeof window.copyTextResult !== "function") return;
        window.copyTextResult(uri).then(function () { button.textContent = "Скопировано"; }).catch(function () {});
      });
      actionBox.appendChild(button);
      row.appendChild(actionBox);
      details.appendChild(row);
    });
    root.appendChild(details);
  }

  function appendArtifactDetails(root, artifact, actions) {
    appendMetadata(root, artifact);
    var inline = artifactInlineText(artifact);
    if (inline && (artifactKind(artifact) === "task_list" || isJsonArtifact(artifact))) {
      appendJsonContent(root, inline, artifactInlineTruncated(artifact) ? "preview" : "full",
        artifactKind(artifact) === "task_list" ? "Данные Task List" : "Содержимое JSON");
    }
    var metadataJson = prop(artifact, "MetadataJson", "metadataJson", "") || "";
    if (metadataJson) appendJsonContent(root, metadataJson, "full", "Metadata JSON");
    appendLibraryHistory(root, artifact, actions);
  }

  function appendDetailTabs(root, buildPreview, buildDetails) {
    var tabs = document.createElement("div");
    tabs.className = "artifact-detail-tabs";
    var previewButton = document.createElement("button");
    var detailsButton = document.createElement("button");
    previewButton.type = detailsButton.type = "button";
    previewButton.className = "secondary compact active";
    detailsButton.className = "secondary compact";
    previewButton.textContent = "Просмотр";
    detailsButton.textContent = "Детали";
    var preview = document.createElement("div");
    var details = document.createElement("div");
    preview.className = "artifact-detail-pane artifact-detail-pane-preview";
    details.className = "artifact-detail-pane artifact-detail-pane-details hidden";

    function show(showDetails) {
      preview.classList.toggle("hidden", showDetails);
      details.classList.toggle("hidden", !showDetails);
      previewButton.classList.toggle("active", !showDetails);
      detailsButton.classList.toggle("active", showDetails);
      previewButton.setAttribute("aria-selected", showDetails ? "false" : "true");
      detailsButton.setAttribute("aria-selected", showDetails ? "true" : "false");
    }
    previewButton.addEventListener("click", function () { show(false); });
    detailsButton.addEventListener("click", function () { show(true); });
    tabs.appendChild(previewButton);
    tabs.appendChild(detailsButton);
    root.appendChild(tabs);
    root.appendChild(preview);
    root.appendChild(details);
    buildPreview(preview);
    buildDetails(details);
    show(false);
  }

  function collectionLabel(collectionId) {
    if (typeof window.artifactCollectionLabel === "function") {
      return window.artifactCollectionLabel(collectionId);
    }
    return {
      "artifact-plans": "Планы",
      "artifact-authored": "Документы",
      "artifact-files": "Файлы и медиа",
      "artifact-generated": "Созданные снимки",
      "artifact-system": "Служебные данные"
    }[String(collectionId || "")] || "Ресурсы";
  }

  function collectionItems(collectionId, actions) {
    return actions && typeof actions.collectionItems === "function"
      ? actions.collectionItems(collectionId) || []
      : [];
  }

  function collectionCount(collectionId, actions) {
    return collectionItems(collectionId, actions).length;
  }

  function appendArtifactCollection(root, selected, actions) {
    var collectionId = selected && selected.item ? selected.item.id : "";
    var items = collectionItems(collectionId, actions);
    var heading = document.createElement("div");
    heading.className = "artifact-collection-heading";
    var title = document.createElement("strong");
    title.textContent = collectionLabel(collectionId);
    var count = document.createElement("span");
    count.textContent = items.length + " ресурсов";
    heading.appendChild(title);
    heading.appendChild(count);
    root.appendChild(heading);
    if (!items.length) {
      appendContentLabel(root, "В этой коллекции ничего не найдено.");
      return;
    }

    var observer = null;
    if (typeof window.IntersectionObserver === "function") {
      observer = new window.IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          observer.unobserve(entry.target);
          if (typeof actions.loadArtifactImageThumbnail === "function") {
            actions.loadArtifactImageThumbnail({ resourceUri: entry.target.dataset.resourceUri });
          }
        });
      }, { rootMargin: "220px" });
    }
    root.__rnArtifactDetailCleanup = function () {
      if (observer) observer.disconnect();
      observer = null;
    };

    var grid = document.createElement("div");
    grid.className = "artifact-collection-grid";
    items.forEach(function (artifact, index) {
      var kind = artifactKind(artifact);
      var removed = window.RNAssistantArtifactVisuals &&
        typeof window.RNAssistantArtifactVisuals.removed === "function" &&
        window.RNAssistantArtifactVisuals.removed(artifact);
      var card = document.createElement("button");
      card.type = "button";
      card.className = "artifact-collection-card kind-" + kind;
      card.disabled = !!removed;
      card.title = removed ? "Ресурс удалён" : "Открыть «" + prop(artifact, "Title", "title", "Артефакт") + "»";
      var visual = document.createElement("span");
      visual.className = "artifact-collection-visual";
      if (artifactViewerKind(artifact) === "image") {
        var uri = exactArtifactUri(artifact);
        var thumbnail = typeof actions.artifactImageThumbnailState === "function"
          ? actions.artifactImageThumbnailState(uri)
          : null;
        var thumbnailNode = document.createElement("span");
        thumbnailNode.dataset.title = prop(artifact, "Title", "title", "Изображение");
        renderThumbnailProjection(thumbnailNode, uri, thumbnail, thumbnailNode.dataset.title);
        visual.appendChild(thumbnailNode);
        if (uri && !thumbnail) {
          if (observer) observer.observe(thumbnailNode);
          else if (index < 24 && typeof actions.loadArtifactImageThumbnail === "function") {
            actions.loadArtifactImageThumbnail({ resourceUri: uri });
          }
        }
      } else {
        var icon = document.createElement("span");
        icon.className = "artifact-type-icon";
        icon.innerHTML = window.RNAssistantArtifactVisuals &&
          typeof window.RNAssistantArtifactVisuals.iconSvg === "function"
          ? window.RNAssistantArtifactVisuals.iconSvg(kind)
          : "";
        visual.appendChild(icon);
      }
      var copy = document.createElement("span");
      copy.className = "artifact-collection-copy";
      var name = document.createElement("strong");
      name.textContent = prop(artifact, "Title", "title", "Артефакт");
      var meta = document.createElement("span");
      meta.textContent = window.RNAssistantArtifactVisuals &&
        typeof window.RNAssistantArtifactVisuals.meta === "function"
        ? window.RNAssistantArtifactVisuals.meta(artifact)
        : typeLabel(kind);
      copy.appendChild(name);
      copy.appendChild(meta);
      card.appendChild(visual);
      card.appendChild(copy);
      if (!removed) {
        card.addEventListener("click", function () {
          if (typeof actions.openCollectionArtifact === "function") {
            actions.openCollectionArtifact(artifact, items, collectionId);
          }
        });
      }
      grid.appendChild(card);
    });
    root.appendChild(grid);
  }

  function renderDetail(root, selected, editorValue, actions) {
    actions = actions || {};
    clearDetail(root);
    if (selected.type === "collection") {
      root.classList.remove("is-image-preview", "is-media-preview");
      appendArtifactCollection(root, selected, actions);
      return;
    }
    var selectedViewerKind = selected.type === "artifact" ? artifactViewerKind(selected.item) : "";
    root.classList.toggle("is-image-preview", selectedViewerKind === "image");
    root.classList.toggle("is-media-preview", selectedViewerKind === "image" || selectedViewerKind === "pdf");
    appendDetailTabs(root, function (preview) {
      if (selected.type === "plan") {
        var planMetadata = {};
        try { planMetadata = JSON.parse(prop(selected.item, "MetadataJson", "metadataJson", "{}") || "{}"); } catch (ignore) {}
        if (String(planMetadata.status || planMetadata.Status || "draft").toLowerCase() === "ready" &&
            artifactId(selected.item) === state.activePlanDocumentArtifactId) {
          var handoff = document.createElement("button");
          handoff.type = "button";
          handoff.className = "primary";
          handoff.textContent = "Начать выполнение";
          handoff.disabled = !!state.activeTaskListArtifactId || !!state.htmlWorkspaceDirty ||
            !prop(selected.item, "ResourceUri", "resourceUri", "") ||
            typeof actions.handoffPlan !== "function";
          if (state.activeTaskListArtifactId) handoff.title = "Сначала закройте активный Task List.";
          handoff.addEventListener("click", function () {
            var revisionUri = prop(selected.item, "ResourceUri", "resourceUri", "") || "";
            if (!revisionUri || typeof actions.handoffPlan !== "function") return;
            actions.handoffPlan({
              expectedRevisionArtifactId: artifactId(selected.item),
              revisionUri: revisionUri
            });
          });
          preview.appendChild(handoff);
        }
        appendTypedArtifactViewer(preview, selected.item, actions,
          state.htmlWorkspaceDirty ? String(editorValue || "") : null);
        return;
      }
      if (isUploadedHtmlArtifact(selected.item)) appendUploadedHtml(preview, selected.item, actions);
      else appendArtifactContent(preview, selected.item, actions);
    }, function (details) {
      appendArtifactDetails(details, selected.item, actions);
    });
  }

  function validatePlanDraft(artifact) {
    var markdownText = String(artifactInlineText(artifact) || "");
    if (!markdownText.trim() || markdownText.length > 32000) throw new Error("Markdown-план должен содержать от 1 до 32000 символов.");
    return { id: storedPlanId(artifact), markdown: markdownText, title: prop(artifact, "Title", "title", "План"), expectedRevisionArtifactId: artifactId(artifact) };
  }

  window.RNAssistantHtmlWorkspaceArtifacts = {
    collectionCount: collectionCount,
    collectionLabel: collectionLabel,
    planSummary: planSummary,
    isUploadedHtmlArtifact: isUploadedHtmlArtifact,
    renderDetail: renderDetail,
    typeLabel: typeLabel,
    validatePlanDraft: validatePlanDraft
  };
}());
