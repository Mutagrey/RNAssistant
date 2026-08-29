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
    return String(prop(artifact, "Kind", "kind", "file") || "file").toLowerCase();
  }

  function artifactRevision(artifact) {
    return Number(prop(artifact, "Revision", "revision", 1) || 1);
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
    var labels = { attachment: "Вложение", image: "Изображение", audio: "Аудио", file: "Файл", markdown: "Markdown", plan_document: "План", task_list: "Task list", html_workspace: "HTML workspace", chart: "Диаграмма", compaction: "Checkpoint", tool_result: "Результат" };
    return labels[kind] || kind;
  }

  function planSummary(artifact) {
    try {
      var metadata = JSON.parse(prop(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
      return metadata.status || metadata.Status || "draft";
    } catch (ignore) { return "draft"; }
  }

  function planStatusLabel(status) {
    var labels = { pending: "Ожидает", in_progress: "В работе", completed: "Готово", blocked: "Заблокирован", cancelled: "Отменён" };
    return labels[status] || status;
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
      var body = document.createElement("div");
      body.className = "markdown";
      body.innerHTML = markdown(editorValue || "_План пуст._");
      root.appendChild(body);
      if (typeof enhanceMarkdown === "function") enhanceMarkdown(body);
      return;
    }

    var metadata = document.createElement("dl");
    metadata.className = "artifact-metadata";
    [
      ["Тип", typeLabel(artifactKind(selected.item))],
      ["Формат", prop(selected.item, "MimeType", "mimeType", "—") || "—"],
      ["Путь", prop(selected.item, "RelativePath", "relativePath", "—") || "—"],
      ["Ревизия", "v" + artifactRevision(selected.item)],
      ["Источник", prop(selected.item, "SourceMessageId", "sourceMessageId", "—") || "—"],
      ["Родитель", prop(selected.item, "ParentArtifactId", "parentArtifactId", "—") || "—"]
    ].forEach(function (pair) {
      var term = document.createElement("dt");
      var value = document.createElement("dd");
      term.textContent = pair[0];
      value.textContent = pair[1];
      metadata.appendChild(term);
      metadata.appendChild(value);
    });
    root.appendChild(metadata);
    appendArtifactContent(root, selected.item);
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
