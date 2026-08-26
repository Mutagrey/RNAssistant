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
    root.replaceChildren();
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
          await saveChatMode("agent");
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
    var content = artifactInlineText(selected.item) || prop(selected.item, "MetadataJson", "metadataJson", "");
    if (content) {
      var pre = document.createElement("pre");
      try { pre.textContent = JSON.stringify(JSON.parse(content), null, 2); }
      catch (error) { pre.textContent = content; }
      root.appendChild(pre);
    }
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
