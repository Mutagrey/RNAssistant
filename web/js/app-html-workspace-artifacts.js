(function () {
  "use strict";
  var prop = window.RNAssistantArtifactViewers.prop;
  var artifactId = window.RNAssistantArtifactViewers.artifactId;
  var artifactKind = window.RNAssistantArtifactViewers.artifactKind;
  var artifactInlineText = window.RNAssistantArtifactViewers.artifactInlineText;
  var artifactInlineTruncated = window.RNAssistantArtifactViewers.artifactInlineTruncated;
  var artifactMimeType = window.RNAssistantArtifactViewers.artifactMimeType;
  var appendContentLabel = window.RNAssistantArtifactViewers.appendContentLabel;
  var artifactViewerKind = window.RNAssistantArtifactViewers.artifactViewerKind;
  var exactArtifactUri = window.RNAssistantArtifactViewers.exactArtifactUri;
  var appendViewerError = window.RNAssistantArtifactViewers.appendViewerError;
  var renderThumbnailProjection = window.RNAssistantArtifactViewers.renderThumbnailProjection;
  var appendTypedArtifactViewer = window.RNAssistantArtifactViewers.appendTypedArtifactViewer;
  var libraryHead = window.RNAssistantArtifactViewers.libraryHead;
  var libraryRevision = window.RNAssistantArtifactViewers.libraryRevision;
  var isHtmlFileArtifact = window.RNAssistantArtifactViewers.isHtmlFileArtifact;

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

  function appendJsonContent(root, text, completeness, label) {
    if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
      throw new Error("JSON viewer is unavailable.");
    }
    appendContentLabel(root, label);
    var target = document.createElement("div");
    target.className = "artifact-viewer-host artifact-json-viewer product-viewer";
    root.appendChild(target);
    window.RNAssistantViewerRegistry.mount("json", target, {
      text: String(text),
      completeness: completeness,
      mode: "tree",
      onCopy: window.copyTextResult
    });
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

  function isUploadedHtmlArtifact(artifact) {
    var resourceClass = String(prop(libraryHead(artifact), "ResourceClass", "resourceClass", "") || "").toLowerCase();
    return resourceClass === "immutable_original" && isHtmlFileArtifact(artifact);
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
    importButton.textContent = "Импортировать и открыть для аннотаций";
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
    var historyCount = Number(prop(head, "HistoryCount", "historyCount",
      (prop(head, "History", "history", []) || []).length) || 0);
    var resourceClass = String(prop(head, "ResourceClass", "resourceClass", "") || "").toLowerCase();
    if ((resourceClass !== "versioned_document" && resourceClass !== "versioned_aggregate") || historyCount < 2) return;
    var isPlan = artifactKind(artifact) === "plan";
    var headArtifactId = prop(head, "ArtifactId", "artifactId", "") || "";
    var details = document.createElement("details");
    details.className = "artifact-history";
    var summary = document.createElement("summary");
    summary.textContent = "История · " + historyCount;
    details.appendChild(summary);
    var rows = document.createElement("div");
    details.appendChild(rows);
    var loadedIds = Object.create(null);
    function appendRevision(revision) {
      var rowId = String(prop(revision, "ArtifactId", "artifactId", "") || "").toLowerCase();
      if (!rowId || loadedIds[rowId]) return;
      loadedIds[rowId] = true;
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
      rows.appendChild(row);
    }
    var loaded = false, pending = false, nextCursor = null;
    var chatId = state.activeChatId, navigation = state.chatNavigationVersion;
    var sessionRevision = Number(prop(state.artifactLibrary, "SessionRevision", "sessionRevision", 0));
    async function load(cursor, targetArtifactId) {
      if (pending) return;
      pending = true;
      try {
        var response = await send("getArtifactLibraryHistory", {
          chatId: chatId, expectedSessionRevision: sessionRevision,
          headArtifactId: headArtifactId, targetArtifactId: targetArtifactId || null, cursor: cursor || null
        });
        if (chatId !== state.activeChatId || navigation !== state.chatNavigationVersion ||
            sessionRevision !== Number(prop(state.artifactLibrary, "SessionRevision", "sessionRevision", 0)) ||
            !details.isConnected) return;
        if (response.chatId !== chatId || response.sessionRevision !== sessionRevision ||
            response.headArtifactId !== headArtifactId) throw new Error("История ресурса устарела.");
        if (!cursor && !targetArtifactId) { rows.replaceChildren(); loadedIds = Object.create(null); }
        (response.items || []).forEach(appendRevision);
        if (!targetArtifactId) nextCursor = response.nextCursor || null;
        var more = rows.querySelector(".artifact-history-more");
        if (more) more.remove();
        if (nextCursor) {
          more = document.createElement("button");
          more.type = "button"; more.className = "secondary compact artifact-history-more";
          more.textContent = "Следующие 50";
          more.addEventListener("click", function () { load(nextCursor); });
          rows.appendChild(more);
        }
        loaded = true;
      } catch (error) {
        if (chatId === state.activeChatId && navigation === state.chatNavigationVersion && details.isConnected) {
          var note = document.createElement("p");
          note.textContent = error.detail || error.message || "История недоступна";
          rows.appendChild(note);
        }
      } finally { pending = false; }
    }
    if (String(artifactId(artifact)).toLowerCase() !== String(headArtifactId).toLowerCase()) {
      var findSelected = document.createElement("button");
      findSelected.type = "button";
      findSelected.className = "secondary compact";
      findSelected.textContent = "Найти открытую ревизию";
      findSelected.addEventListener("click", function () { load(null, artifactId(artifact)); });
      details.appendChild(findSelected);
    }
    details.addEventListener("toggle", function () { if (details.open && !loaded) load(null); });
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

  function appendDetailTabs(root, buildPreview, buildDetails, copySource) {
    var tabs = document.createElement("div");
    tabs.className = "artifact-detail-tabs";
    var modes = document.createElement("div");
    modes.className = "view-switch artifact-detail-modes";
    modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "Раздел материала");
    var previewButton = document.createElement("button");
    var detailsButton = document.createElement("button");
    previewButton.type = detailsButton.type = "button";
    previewButton.className = "button compact active";
    detailsButton.className = "button compact";
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
    modes.appendChild(previewButton);
    modes.appendChild(detailsButton);
    tabs.appendChild(modes);
    if (copySource && typeof window.copyTextResult === "function") {
      var copy = document.createElement("button");
      var feedback = document.createElement("span");
      copy.type = "button"; copy.className = "icon-btn material-copy-button";
      copy.setAttribute("aria-label", copySource.truncated ? "Копировать фрагмент" : "Копировать исходные данные");
      copy.title = copy.getAttribute("aria-label");
      copy.innerHTML = window.RNAssistantIcons.render("copy");
      feedback.className = "material-copy-status"; feedback.setAttribute("role", "status");
      copy.addEventListener("click", function () {
        try { Promise.resolve(window.copyTextResult(copySource.text)).then(function () { feedback.textContent = "Скопировано"; })
          .catch(function () { feedback.textContent = "Не удалось скопировать"; }); }
        catch (error) { feedback.textContent = "Не удалось скопировать"; }
      });
      tabs.appendChild(copy); tabs.appendChild(feedback);
    }
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
    root.classList.toggle("is-plan-preview", selected.type === "plan");
    root.classList.toggle("is-image-preview", selectedViewerKind === "image");
    root.classList.toggle("is-media-preview", selectedViewerKind === "image" || selectedViewerKind === "pdf");
    function renderPreview(preview) {
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
    }
    if (selected.type === "plan" || selectedViewerKind === "markdown") {
      renderPreview(root);
      return;
    }
    appendDetailTabs(root, renderPreview, function (details) {
      appendArtifactDetails(details, selected.item, actions);
    }, selected.type === "artifact" && !selectedViewerKind &&
      (!isJsonArtifact(selected.item) || isChartArtifact(selected.item)) && artifactInlineText(selected.item)
      ? { text: artifactInlineText(selected.item), truncated: artifactInlineTruncated(selected.item) } : null);
  }

  function validatePlanDraft(artifact) {
    var markdownText = String(artifactInlineText(artifact) || "");
    if (!markdownText.trim()) throw new Error("Markdown-план не должен быть пустым.");
    return { id: storedPlanId(artifact), markdown: markdownText, title: prop(artifact, "Title", "title", "План"), expectedRevisionArtifactId: artifactId(artifact) };
  }

  window.RNAssistantHtmlWorkspaceArtifacts = {
    collectionCount: collectionCount,
    collectionLabel: collectionLabel,
    planSummary: planSummary,
    isUploadedHtmlArtifact: isUploadedHtmlArtifact,
    renderDetail: renderDetail,
    appendTypedViewer: appendTypedArtifactViewer,
    typeLabel: typeLabel,
    validatePlanDraft: validatePlanDraft
  };
}());
