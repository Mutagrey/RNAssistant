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

  function htmlWorkspaceRevisionId(message) {
    var activity = value(message, "Activity", "activity", null);
    var dataJson = value(activity, "DataJson", "dataJson", "") || "";
    if (!dataJson) return "";
    try {
      var data = JSON.parse(dataJson);
      var type = value(data, "Type", "type", "");
      return type === "rnassistant.htmlWorkspaceMutation"
        ? value(data, "RevisionArtifactId", "revisionArtifactId", "") || ""
        : "";
    } catch (error) {
      return "";
    }
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

  function planValue(artifact) {
    if (artifactKind(artifact) !== "plan") return null;
    try { return JSON.parse(artifactInlineText(artifact)); } catch (error) { return null; }
  }

  function planId(artifact) {
    var plan = planValue(artifact);
    return plan && (plan.id || plan.Id) || "";
  }

  function latestPlanRevision(artifact) {
    var id = planId(artifact);
    if (!id) return true;
    return !(state.artifacts || []).some(function (candidate) {
      return artifactKind(candidate) === "plan" && planId(candidate) === id &&
        artifactRevision(candidate) > artifactRevision(artifact);
    });
  }

  function planMeta(artifact) {
    var plan = planValue(artifact);
    var steps = plan && (plan.steps || plan.Steps);
    if (!Array.isArray(steps) || !steps.length) return "План";
    var completed = steps.filter(function (step) {
      return String((step && (step.status || step.Status)) || "pending").toLowerCase() === "completed";
    }).length;
    var blocked = steps.some(function (step) {
      return String((step && (step.status || step.Status)) || "pending").toLowerCase() === "blocked";
    });
    var status = completed === steps.length ? "выполнен" : (blocked ? "есть блокировка" : "сохранён");
    return completed + "/" + steps.length + " · " + status;
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
    meta.textContent = kind === "plan" ? planMeta(artifact) : "v" + artifactRevision(artifact);
    header.appendChild(badge);
    header.appendChild(title);
    header.appendChild(meta);
    card.appendChild(header);

    if (kind === "html_workspace" && artifactId(artifact) === state.activeHtmlArtifactId) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "chat-artifact-action";
      button.textContent = "Открыть текущий HTML";
      button.addEventListener("click", function () { switchTab("artifacts"); });
      card.appendChild(button);
    }
    return card;
  }

  function appendMessageArtifactCards(parent, message) {
    if (!parent || !message) return;
    var artifacts = messageArtifactIds(message).map(artifactById).filter(Boolean).filter(function (artifact) {
      var kind = artifactKind(artifact);
      if (kind === "html_workspace") return artifactId(artifact) === htmlWorkspaceRevisionId(message);
      return kind !== "attachment" && kind !== "image" && (kind !== "plan" || latestPlanRevision(artifact));
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
