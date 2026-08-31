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
      Array.prototype.slice.call(root.querySelectorAll(".artifact-json-viewer")).forEach(function (target) {
        window.RNAssistantViewerRegistry.unmount(target);
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

  function appendArtifactContent(root, artifact) {
    var inline = artifactInlineText(artifact);
    var metadataJson = prop(artifact, "MetadataJson", "metadataJson", "") || "";
    if (inline) {
      if (isJsonArtifact(artifact)) {
        appendJsonContent(root, inline, artifactInlineTruncated(artifact) ? "preview" : "full", "Содержимое JSON");
      } else {
        appendContentLabel(root, artifactInlineTruncated(artifact) ? "Содержимое · ограниченный preview" : "Содержимое");
        var pre = document.createElement("pre");
        pre.className = "artifact-text-viewer";
        pre.textContent = inline;
        root.appendChild(pre);
      }
      return;
    }
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

  function appendLibraryHistory(root, artifact) {
    var head = libraryHead(artifact);
    var history = prop(head, "History", "history", []) || [];
    var resourceClass = String(prop(head, "ResourceClass", "resourceClass", "") || "").toLowerCase();
    if ((resourceClass !== "versioned_document" && resourceClass !== "versioned_aggregate") || history.length < 2) return;
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
      var button = document.createElement("button");
      button.type = "button";
      button.className = "secondary compact";
      button.textContent = "Копировать";
      button.disabled = !uri;
      button.addEventListener("click", function () {
        if (!uri || typeof window.copyTextResult !== "function") return;
        window.copyTextResult(uri).then(function () { button.textContent = "Скопировано"; }).catch(function () {});
      });
      row.appendChild(button);
      details.appendChild(row);
    });
    root.appendChild(details);
  }

  function renderDetail(root, selected, editorValue) {
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
        handoff.disabled = !!state.activeTaskListArtifactId || !prop(selected.item, "ResourceUri", "resourceUri", "");
        if (state.activeTaskListArtifactId) handoff.title = "Сначала закройте активный Task List.";
        handoff.addEventListener("click", async function () {
          if (!await saveChatMode("agent")) return;
          var input = $("chatInput");
          var form = $("chatForm");
          if (!input || !form) return;
          var revisionUri = prop(selected.item, "ResourceUri", "resourceUri", "") || "";
          if (!revisionUri) return;
          input.value = "Выполни утверждённый план " + revisionUri + ". Перед началом прочитай эту точную ревизию через common.resources_read.";
          updateComposerInputState();
          if (form.requestSubmit) form.requestSubmit();
        });
        root.appendChild(handoff);
      }
      appendMetadata(root, selected.item);
      var body = document.createElement("div");
      body.className = "markdown";
      body.innerHTML = markdown(editorValue || "_План пуст._");
      root.appendChild(body);
      if (typeof enhanceMarkdown === "function") enhanceMarkdown(body);
      appendLibraryHistory(root, selected.item);
      return;
    }

    appendMetadata(root, selected.item);
    appendArtifactContent(root, selected.item);
    appendLibraryHistory(root, selected.item);
  }

  function validatePlanDraft(artifact) {
    var markdownText = String(artifactInlineText(artifact) || "").trim();
    if (!markdownText || markdownText.length > 32000) throw new Error("Markdown-план должен содержать от 1 до 32000 символов.");
    return { id: storedPlanId(artifact), markdown: markdownText, title: prop(artifact, "Title", "title", "План"), expectedRevisionArtifactId: artifactId(artifact) };
  }

  window.RNAssistantHtmlWorkspaceArtifacts = {
    planSummary: planSummary,
    renderDetail: renderDetail,
    typeLabel: typeLabel,
    validatePlanDraft: validatePlanDraft
  };
}());
