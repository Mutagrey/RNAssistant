(function () {
  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
  }

  function artifactId(artifact) { return value(artifact, "Id", "id", ""); }
  function artifactKind(artifact) { return value(artifact, "Kind", "kind", "file"); }
  function artifactTitle(artifact) { return value(artifact, "Title", "title", "Артефакт"); }
  function artifactRevision(artifact) { return Number(value(artifact, "Revision", "revision", 1) || 1); }
  function artifactInlineText(artifact) { return value(artifact, "InlineText", "inlineText", "") || ""; }
  function artifactById(id) {
    return (state.artifacts || []).filter(function (artifact) { return artifactId(artifact) === id; })[0] || null;
  }

  function messageArtifactIds(message) {
    return value(message, "ArtifactIds", "artifactIds", []) || [];
  }

  function kindLabel(kind) {
    var labels = {
      plan: "План",
      markdown: "Markdown",
      html_workspace: "HTML",
      image: "Изображение",
      attachment: "Вложение",
      file: "Файл",
      chart: "Диаграмма",
      compaction: "Контекст",
      tool_result: "Результат"
    };
    return labels[kind] || "Артефакт";
  }

  function planDetails(artifact) {
    var parsed;
    try { parsed = JSON.parse(artifactInlineText(artifact)); } catch (error) { return null; }
    var steps = parsed && (parsed.steps || parsed.Steps);
    if (!Array.isArray(steps)) return null;
    var list = document.createElement("ol");
    list.className = "artifact-plan-list";
    steps.forEach(function (step) {
      var item = document.createElement("li");
      var status = String((step && (step.status || step.Status)) || "pending").toLowerCase();
      item.className = "status-" + status;
      item.textContent = (step && (step.text || step.Text)) || String(step || "");
      list.appendChild(item);
    });
    return list;
  }

  function artifactCard(artifact) {
    var kind = artifactKind(artifact);
    var card = document.createElement("section");
    card.className = "chat-artifact-card kind-" + kind;
    card.dataset.artifactId = artifactId(artifact);

    var header = document.createElement("div");
    header.className = "chat-artifact-header";
    var badge = document.createElement("span");
    badge.className = "chat-artifact-badge";
    badge.textContent = kindLabel(kind);
    var title = document.createElement("strong");
    title.className = "chat-artifact-title";
    title.textContent = artifactTitle(artifact);
    var meta = document.createElement("span");
    meta.className = "chat-artifact-meta";
    meta.textContent = "v" + artifactRevision(artifact);
    header.appendChild(badge);
    header.appendChild(title);
    header.appendChild(meta);
    card.appendChild(header);

    var detail = kind === "plan" ? planDetails(artifact) : null;
    if (detail) card.appendChild(detail);

    if (kind === "html_workspace" && artifactId(artifact) === state.activeHtmlArtifactId) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "chat-artifact-action";
      button.textContent = "Открыть текущий HTML";
      button.addEventListener("click", function () { switchTab("html"); });
      card.appendChild(button);
    }
    return card;
  }

  function appendMessageArtifactCards(parent, message) {
    if (!parent || !message) return;
    var artifacts = messageArtifactIds(message).map(artifactById).filter(Boolean).filter(function (artifact) {
      var kind = artifactKind(artifact);
      return kind !== "attachment" && kind !== "image";
    });
    if (!artifacts.length) return;
    var wrap = document.createElement("div");
    wrap.className = "chat-artifact-list";
    artifacts.forEach(function (artifact) { wrap.appendChild(artifactCard(artifact)); });
    parent.appendChild(wrap);
  }

  window.messageArtifactIds = messageArtifactIds;
  window.appendMessageArtifactCards = appendMessageArtifactCards;
}());
