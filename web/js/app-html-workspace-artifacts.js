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

  function clearDetail(root) {
    if (window.RNAssistantViewerRegistry) {
      [".artifact-json-viewer", ".artifact-typed-viewer"].forEach(function (selector) {
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
    target.className = "artifact-json-viewer";
    root.appendChild(target);
    window.RNAssistantViewerRegistry.mount("json", target, {
      text: String(text),
      completeness: completeness,
      mode: "tree",
      onCopy: window.copyTextResult
    });
  }

  function artifactViewerKind(artifact) {
    var mediaType = artifactMimeType(artifact).split(";", 1)[0].trim();
    var kind = artifactKind(artifact);
    var title = String(prop(artifact, "Title", "title", "") || "");
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
      draftTarget.className = "artifact-typed-viewer";
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
    var uri = exactArtifactUri(artifact);
    if (!uri) {
      appendViewerError(root, "Artifact has no exact revision URI.");
      return true;
    }
    var viewer = typeof actions.artifactViewerState === "function" ? actions.artifactViewerState(uri) : null;
    if (!viewer) {
      appendContentLabel(root, "Загружаю exact source…");
      if (typeof actions.loadArtifactViewer === "function") actions.loadArtifactViewer({ resourceUri: uri });
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
    var pages = viewer.pages || [];
    var pageIndex = Math.max(0, Math.min(Number(viewer.pageIndex || 0), Math.max(0, pages.length - 1)));
    var page = pages[pageIndex];
    if (!page || viewer.viewerKind !== expectedKind) {
      appendViewerError(root, "Artifact viewer state does not match the selected revision.");
      return true;
    }
    var target = document.createElement("div");
    target.className = "artifact-typed-viewer";
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
    var metadataJson = prop(artifact, "MetadataJson", "metadataJson", "") || "";
    if (inline) {
      if (isJsonArtifact(artifact)) {
        appendJsonContent(root, inline, artifactInlineTruncated(artifact) ? "preview" : "full", "Содержимое JSON");
      } else if (!appendTypedArtifactViewer(root, artifact, actions)) {
        appendContentLabel(root, "Для этого формата typed viewer ещё не подключён.");
      }
      return;
    }
    if (appendTypedArtifactViewer(root, artifact, actions)) return;
    if (metadataJson) appendJsonContent(root, metadataJson, "full", "Metadata JSON");
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

  function uploadedHtmlResourceUri(artifact) {
    var exact = libraryRevision(artifact);
    return prop(exact, "ResourceUri", "resourceUri", prop(artifact, "ResourceUri", "resourceUri", "")) || "";
  }

  function uploadedHtmlTargetPath(artifact) {
    var title = String(prop(artifact, "Title", "title", "index.html") || "index.html").split(/[\\/]/).pop();
    return /\.html?$/i.test(title) ? title : "index.html";
  }

  function appendUploadedHtml(root, artifact, actions) {
    actions = actions || {};
    var uri = uploadedHtmlResourceUri(artifact);
    var notice = document.createElement("div");
    notice.className = "artifact-inert-html-note";
    notice.textContent = "Загруженный HTML инертен: до явного импорта показывается только экранированный исходник.";
    root.appendChild(notice);

    var actionBox = document.createElement("div");
    actionBox.className = "artifact-inert-html-actions";
    var preview = typeof actions.uploadedHtmlPreview === "function" ? actions.uploadedHtmlPreview(uri) : null;
    var load = document.createElement("button");
    load.type = "button";
    load.className = "secondary";
    load.textContent = preview && preview.status === "loading" ? "Загружаю…" : "Показать исходник";
    load.disabled = !uri || !!preview && (preview.status === "loading" || preview.status === "ready") ||
      typeof actions.loadUploadedHtmlSource !== "function";
    load.addEventListener("click", function () {
      if (typeof actions.loadUploadedHtmlSource === "function") {
        actions.loadUploadedHtmlSource({ sourceResourceUri: uri });
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

    if (preview && preview.status === "error") {
      var error = document.createElement("div");
      error.className = "artifact-detail-error";
      error.textContent = preview.message || "Исходник HTML недоступен.";
      root.appendChild(error);
      return;
    }
    if (!preview || preview.status !== "ready" || preview.sourceResourceUri !== uri) return;
    appendContentLabel(root, preview.truncated
      ? "HTML source · ограниченный preview " + preview.returnedCharacters + " из " + preview.totalCharacters + " символов"
      : "HTML source · полный");
    var source = document.createElement("pre");
    source.className = "artifact-text-viewer artifact-html-source-viewer";
    source.textContent = preview.text || "";
    root.appendChild(source);
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

  function renderDetail(root, selected, editorValue, actions) {
    actions = actions || {};
    clearDetail(root);
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
        root.appendChild(handoff);
      }
      appendMetadata(root, selected.item);
      appendTypedArtifactViewer(root, selected.item, actions,
        state.htmlWorkspaceDirty ? String(editorValue || "") : null);
      appendLibraryHistory(root, selected.item, actions);
      return;
    }

    appendMetadata(root, selected.item);
    if (isUploadedHtmlArtifact(selected.item)) {
      appendUploadedHtml(root, selected.item, actions);
      return;
    }
    appendArtifactContent(root, selected.item, actions);
    appendLibraryHistory(root, selected.item, actions);
  }

  function validatePlanDraft(artifact) {
    var markdownText = String(artifactInlineText(artifact) || "");
    if (!markdownText.trim() || markdownText.length > 32000) throw new Error("Markdown-план должен содержать от 1 до 32000 символов.");
    return { id: storedPlanId(artifact), markdown: markdownText, title: prop(artifact, "Title", "title", "План"), expectedRevisionArtifactId: artifactId(artifact) };
  }

  window.RNAssistantHtmlWorkspaceArtifacts = {
    planSummary: planSummary,
    isUploadedHtmlArtifact: isUploadedHtmlArtifact,
    renderDetail: renderDetail,
    typeLabel: typeLabel,
    validatePlanDraft: validatePlanDraft
  };
}());
