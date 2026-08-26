(function () {
  "use strict";

  var resourceNavigationChatId = "";

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function artifactId(artifact) { return value(artifact, "Id", "id", ""); }
  function artifactKind(artifact) { return String(value(artifact, "Kind", "kind", "file") || "file").toLowerCase(); }
  function artifactTitle(artifact) { return value(artifact, "Title", "title", "Артефакт") || "Артефакт"; }
  function artifactRevision(artifact) { return Number(value(artifact, "Revision", "revision", 1) || 1); }
  function artifactInlineText(artifact) { return value(artifact, "InlineText", "inlineText", "") || ""; }
  function artifactParentId(artifact) { return value(artifact, "ParentArtifactId", "parentArtifactId", "") || ""; }

  function artifactById(id) {
    var normalized = String(id || "").toLowerCase();
    return (state.artifacts || []).filter(function (artifact) {
      return String(artifactId(artifact) || "").toLowerCase() === normalized;
    })[0] || null;
  }

  function messageResourceRefs(message) {
    return value(message, "ResourceRefs", "resourceRefs", []) || [];
  }

  function resourceRefUri(reference) {
    return value(reference, "Uri", "uri", "") || "";
  }

  function artifactIdentityFromReference(reference) {
    var uri = resourceRefUri(reference);
    if (uri.indexOf("rna://chat/") !== 0) return null;
    var path = uri.slice("rna://chat/".length).split("/");
    if (path.length < 5 || path[1] !== "artifact" || path[3] !== "revision") return null;
    try {
      return { id: decodeURIComponent(path[2]), revision: Number(path[4] || 0) };
    } catch (error) {
      return null;
    }
  }

  function artifactByReference(reference) {
    var identity = artifactIdentityFromReference(reference);
    if (!identity) return null;
    var artifact = artifactById(identity.id);
    return artifact && artifactRevision(artifact) === identity.revision ? artifact : null;
  }

  function kindLabel(kind) {
    var labels = {
      plan: "План",
      markdown: "Markdown",
      html_workspace: "HTML workspace",
      image: "Изображение",
      audio: "Аудио",
      attachment: "Вложение",
      file: "Файл",
      chart: "Диаграмма",
      compaction: "Checkpoint",
      tool_result: "Результат",
      html: "HTML",
      css: "CSS",
      js: "JavaScript",
      script: "JavaScript",
      json: "JSON",
      data: "Данные"
    };
    return labels[String(kind || "").toLowerCase()] || "Артефакт";
  }

  function kindCategory(kind) {
    kind = String(kind || "").toLowerCase();
    if (["attachment", "image", "audio", "file"].indexOf(kind) >= 0) return "files";
    if (["tool_result", "compaction"].indexOf(kind) >= 0) return "system";
    return "created";
  }

  function categoryLabel(category) {
    return { created: "Созданные", files: "Файлы", system: "Служебные" }[category] || "Ресурсы";
  }

  function iconSvg(kind) {
    kind = String(kind || "file").toLowerCase();
    var icons = {
      plan: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"4\" y=\"3\" width=\"16\" height=\"18\" rx=\"2\"/><path d=\"m8 9 1.5 1.5L12 8\"/><path d=\"M14 9h3\"/><path d=\"m8 15 1.5 1.5L12 14\"/><path d=\"M14 15h3\"/></svg>",
      html_workspace: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M3 8h18\"/><path d=\"m9 12-2 2 2 2\"/><path d=\"m15 12 2 2-2 2\"/></svg>",
      html: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z\"/><path d=\"M14 2v6h6\"/><path d=\"m10 13-2 2 2 2\"/><path d=\"m14 13 2 2-2 2\"/></svg>",
      css: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z\"/><path d=\"M14 2v6h6\"/><path d=\"M8 14c0-1 1-2 2-2\"/><path d=\"M8 14c0 1 1 2 2 2\"/><path d=\"M16 12h-2v4h2\"/></svg>",
      js: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z\"/><path d=\"M14 2v6h6\"/><path d=\"M9 12v4a1 1 0 0 1-1 1H7\"/><path d=\"M13 16c.5.7 2.7.7 3 0 .5-1-3-1-2.5-2 .3-.7 2.2-.7 2.8 0\"/></svg>",
      json: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M8 3H6a2 2 0 0 0-2 2v4a2 2 0 0 1-2 2 2 2 0 0 1 2 2v6a2 2 0 0 0 2 2h2\"/><path d=\"M16 3h2a2 2 0 0 1 2 2v4a2 2 0 0 0 2 2 2 2 0 0 0-2 2v6a2 2 0 0 1-2 2h-2\"/></svg>",
      chart: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M4 20V10\"/><path d=\"M10 20V4\"/><path d=\"M16 20v-7\"/><path d=\"M22 20H2\"/></svg>",
      image: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><circle cx=\"9\" cy=\"10\" r=\"2\"/><path d=\"m21 15-4-4L5 20\"/></svg>",
      audio: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 18V5l10-2v13\"/><circle cx=\"6\" cy=\"18\" r=\"3\"/><circle cx=\"16\" cy=\"16\" r=\"3\"/></svg>",
      markdown: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z\"/><path d=\"M14 2v6h6\"/><path d=\"M7 17v-5l2 2 2-2v5\"/><path d=\"m14 14 2 2 2-2\"/><path d=\"M16 12v4\"/></svg>",
      tool_result: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"m7 9 3 3-3 3\"/><path d=\"M13 15h4\"/></svg>",
      compaction: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m4 9 5-5\"/><path d=\"M4 4h5v5\"/><path d=\"m20 9-5-5\"/><path d=\"M20 4h-5v5\"/><path d=\"m4 15 5 5\"/><path d=\"M4 20h5v-5\"/><path d=\"m20 15-5 5\"/><path d=\"M20 20h-5v-5\"/></svg>",
      file: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z\"/><path d=\"M14 2v6h6\"/><path d=\"M8 13h8\"/><path d=\"M8 17h6\"/></svg>"
    };
    if (kind === "attachment") kind = "file";
    if (kind === "script") kind = "js";
    if (kind === "data") kind = "json";
    return icons[kind] || icons.file;
  }

  function planValue(artifact) {
    if (artifactKind(artifact) !== "plan") return null;
    try { return JSON.parse(artifactInlineText(artifact)); } catch (error) { return null; }
  }

  function planId(artifact) {
    var plan = planValue(artifact);
    return plan && (plan.id || plan.Id) || "";
  }

  function planMeta(artifact) {
    var plan = planValue(artifact);
    var steps = plan && (plan.steps || plan.Steps);
    if (!Array.isArray(steps) || !steps.length) return "План";
    var completed = steps.filter(function (step) {
      return String((step && (step.status || step.Status)) || "pending").toLowerCase() === "completed";
    }).length;
    return completed + "/" + steps.length + " шагов";
  }

  function formatBytes(bytes) {
    bytes = Number(bytes || 0);
    if (!bytes) return "";
    if (bytes < 1024) return bytes + " Б";
    if (bytes < 1024 * 1024) return Math.round(bytes / 102.4) / 10 + " КБ";
    return Math.round(bytes / 1024 / 102.4) / 10 + " МБ";
  }

  function artifactMeta(artifact) {
    var kind = artifactKind(artifact);
    if (kind === "plan") return planMeta(artifact) + " · v" + artifactRevision(artifact);
    var mimeType = value(artifact, "MimeType", "mimeType", "") || "";
    var bytes = formatBytes(value(artifact, "ContentByteLength", "contentByteLength", 0));
    var parts = [kindLabel(kind)];
    if (["html_workspace", "markdown", "chart"].indexOf(kind) >= 0 || artifactRevision(artifact) > 1) {
      parts.push("v" + artifactRevision(artifact));
    } else if (bytes) {
      parts.push(bytes);
    } else if (mimeType && mimeType.indexOf("/") >= 0) {
      parts.push(mimeType.split("/").pop());
    }
    return parts.join(" · ");
  }

  function artifactLineageRoot(artifact, byId) {
    var current = artifact;
    var visited = {};
    while (current && artifactParentId(current)) {
      var id = String(artifactId(current) || "").toLowerCase();
      if (!id || visited[id]) break;
      visited[id] = true;
      var parent = byId[String(artifactParentId(current)).toLowerCase()];
      if (!parent || artifactKind(parent) !== artifactKind(artifact)) break;
      current = parent;
    }
    return current ? artifactId(current) : artifactId(artifact);
  }

  function artifactResourceHeads(sourceArtifacts) {
    var artifacts = (Array.isArray(sourceArtifacts) ? sourceArtifacts : (state.artifacts || []))
      .filter(function (artifact) { return !!artifact && !!artifactId(artifact); });
    var lineageArtifacts = (state.artifacts || []).filter(function (artifact) { return !!artifact && !!artifactId(artifact); });
    var byId = {};
    var heads = {};
    lineageArtifacts.concat(artifacts).forEach(function (artifact) {
      byId[String(artifactId(artifact)).toLowerCase()] = artifact;
    });
    artifacts.forEach(function (artifact) {
      var kind = artifactKind(artifact);
      var stableId = kind === "plan" ? planId(artifact) : "";
      var key = kind + ":" + (kind === "html_workspace" ? "active" : (stableId || artifactLineageRoot(artifact, byId)));
      var current = heads[key];
      var isActive = artifactId(artifact) === state.activeHtmlArtifactId || artifactId(artifact) === state.activePlanArtifactId;
      var currentIsActive = current && (artifactId(current) === state.activeHtmlArtifactId || artifactId(current) === state.activePlanArtifactId);
      if (!current || (isActive && !currentIsActive) || (!currentIsActive && (
        artifactRevision(artifact) > artifactRevision(current) ||
        (artifactRevision(artifact) === artifactRevision(current) && String(value(artifact, "CreatedUtc", "createdUtc", "")) > String(value(current, "CreatedUtc", "createdUtc", "")))
      ))) {
        heads[key] = artifact;
      }
    });
    return Object.keys(heads).map(function (key) { return heads[key]; }).sort(function (left, right) {
      var leftCategory = ["created", "files", "system"].indexOf(kindCategory(artifactKind(left)));
      var rightCategory = ["created", "files", "system"].indexOf(kindCategory(artifactKind(right)));
      if (leftCategory !== rightCategory) return leftCategory - rightCategory;
      return String(value(right, "CreatedUtc", "createdUtc", "")).localeCompare(String(value(left, "CreatedUtc", "createdUtc", "")));
    });
  }

  function openArtifactResource(artifact) {
    if (!artifact) return;
    var kind = artifactKind(artifact);
    if (kind === "html_workspace" && artifactId(artifact) === state.activeHtmlArtifactId) {
      var workspace = state.htmlWorkspace || {};
      var activeFileId = value(workspace, "ActiveFileId", "activeFileId", "") || "";
      var workspaceFiles = value(workspace, "Files", "files", []) || [];
      var fallbackFile = workspaceFiles.filter(function (file) {
        return String(value(file, "Kind", "kind", "")).toLowerCase() === "html";
      })[0] || workspaceFiles[0];
      if (!activeFileId && fallbackFile) activeFileId = value(fallbackFile, "Id", "id", value(fallbackFile, "Path", "path", ""));
      state.htmlWorkspaceSelection = activeFileId
        ? { type: "file", id: activeFileId }
        : { type: "artifact", id: artifactId(artifact) };
    } else {
      state.htmlWorkspaceSelection = { type: kind === "plan" ? "plan" : "artifact", id: artifactId(artifact) };
    }
    setChatResourcePopoverOpen(false);
    switchTab("artifacts");
    if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace();
  }

  function artifactCard(artifact) {
    var kind = artifactKind(artifact);
    var card = document.createElement("button");
    card.type = "button";
    card.className = "chat-artifact-card kind-" + kind + " category-" + kindCategory(kind);
    card.dataset.artifactId = artifactId(artifact);
    card.title = "Открыть во вкладке «Артефакты»";
    card.setAttribute("aria-label", "Открыть " + kindLabel(kind) + " «" + artifactTitle(artifact) + "»");

    var icon = document.createElement("span");
    icon.className = "artifact-type-icon";
    icon.innerHTML = iconSvg(kind);
    card.appendChild(icon);

    var copy = document.createElement("span");
    copy.className = "chat-artifact-copy";
    var title = document.createElement("strong");
    title.className = "chat-artifact-title";
    title.textContent = artifactTitle(artifact);
    var meta = document.createElement("span");
    meta.className = "chat-artifact-meta";
    meta.textContent = artifactMeta(artifact);
    copy.appendChild(title);
    copy.appendChild(meta);
    card.appendChild(copy);

    var arrow = document.createElement("span");
    arrow.className = "chat-artifact-open";
    arrow.setAttribute("aria-hidden", "true");
    arrow.textContent = "›";
    card.appendChild(arrow);
    card.addEventListener("click", function () { openArtifactResource(artifact); });
    return card;
  }

  function messageArtifacts(message) {
    var references = messageResourceRefs(message).slice();
    var checkpoint = value(message, "HtmlWorkspaceCheckpoint", "htmlWorkspaceCheckpoint", null);
    if (checkpoint) references.push(checkpoint);
    var seen = {};
    return references.map(artifactByReference).filter(Boolean).filter(function (artifact) {
      var kind = artifactKind(artifact);
      if (kind === "attachment" || kind === "image" || kind === "audio") return false;
      var key = String(artifactId(artifact)).toLowerCase();
      if (seen[key]) return false;
      seen[key] = true;
      return true;
    });
  }

  function appendMessageArtifactCards(parent, message, seenArtifactIds) {
    if (!parent || !message) return;
    var artifacts = messageArtifacts(message).filter(function (artifact) {
      var key = "$" + String(artifactId(artifact) || "").toLowerCase();
      return !seenArtifactIds || !seenArtifactIds[key];
    });
    if (!artifacts.length) return;
    var wrap = document.createElement("div");
    wrap.className = "chat-artifact-list";
    artifacts.forEach(function (artifact) {
      if (seenArtifactIds) seenArtifactIds["$" + String(artifactId(artifact) || "").toLowerCase()] = true;
      wrap.appendChild(artifactCard(artifact));
    });
    parent.appendChild(wrap);
  }

  function collectRunArtifacts(items, finalMessage) {
    var seen = {};
    var artifacts = [];
    var messages = (items || []).map(function (item) { return item && item.message; });
    if (finalMessage && finalMessage.message) messages.push(finalMessage.message);
    messages.filter(Boolean).forEach(function (message) {
      messageArtifacts(message).forEach(function (artifact) {
        var key = String(artifactId(artifact) || "").toLowerCase();
        if (!key || seen[key]) return;
        seen[key] = true;
        artifacts.push(artifact);
      });
    });
    return artifactResourceHeads(artifacts);
  }

  function resourceBundleMeta(artifacts) {
    var labels = [];
    var seen = {};
    (artifacts || []).forEach(function (artifact) {
      var label = kindLabel(artifactKind(artifact));
      if (!seen[label]) {
        seen[label] = true;
        labels.push(label);
      }
    });
    return labels.slice(0, 3).join(" · ") + (labels.length > 3 ? " · …" : "");
  }

  function appendAgentRunResourceCards(parent, items, finalMessage) {
    if (!parent) return;
    var artifacts = collectRunArtifacts(items, finalMessage);
    if (!artifacts.length) return;

    if (artifacts.length === 1) {
      var single = document.createElement("div");
      single.className = "chat-artifact-list agent-run-resource-list";
      single.appendChild(artifactCard(artifacts[0]));
      parent.appendChild(single);
      return;
    }

    var details = document.createElement("details");
    details.className = "chat-resource-bundle";
    var summary = document.createElement("summary");
    summary.className = "chat-resource-bundle-summary";

    var icon = document.createElement("span");
    icon.className = "chat-resource-bundle-icon";
    icon.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"7\" y=\"3\" width=\"13\" height=\"15\" rx=\"2\"/><path d=\"M17 21H6a2 2 0 0 1-2-2V8\"/><path d=\"M10 8h7M10 12h5\"/></svg>";
    summary.appendChild(icon);

    var copy = document.createElement("span");
    copy.className = "chat-resource-bundle-copy";
    var title = document.createElement("strong");
    title.textContent = "Ресурсы · " + artifacts.length;
    var meta = document.createElement("span");
    meta.textContent = resourceBundleMeta(artifacts);
    copy.appendChild(title);
    copy.appendChild(meta);
    summary.appendChild(copy);

    var caret = document.createElement("span");
    caret.className = "chat-resource-bundle-caret";
    caret.setAttribute("aria-hidden", "true");
    caret.textContent = "›";
    summary.appendChild(caret);
    details.appendChild(summary);

    var list = document.createElement("div");
    list.className = "chat-artifact-list chat-resource-bundle-list";
    artifacts.forEach(function (artifact) { list.appendChild(artifactCard(artifact)); });
    details.appendChild(list);
    parent.appendChild(details);
  }

  function setChatResourcePopoverOpen(open) {
    var menu = $("chatResourceMenu");
    var button = $("toggleChatResourcesButton");
    var panel = $("chatResourcesPopover");
    if (!menu || !button || !panel) return;
    menu.classList.toggle("is-open", !!open);
    panel.classList.toggle("hidden", !open);
    panel.setAttribute("aria-hidden", open ? "false" : "true");
    button.setAttribute("aria-expanded", open ? "true" : "false");
    if (open) {
      renderChatResourceNavigation();
      var search = $("chatResourcesSearchInput");
      if (search && artifactResourceHeads().length > 6) search.focus();
    }
  }

  function renderChatResourceNavigation() {
    var button = $("toggleChatResourcesButton");
    var count = $("chatResourceCount");
    var list = $("chatResourcesList");
    var search = $("chatResourcesSearchInput");
    if (!button || !count || !list) return;
    var items = artifactResourceHeads();
    if (resourceNavigationChatId !== String(state.activeChatId || "")) {
      resourceNavigationChatId = String(state.activeChatId || "");
      if (search) search.value = "";
    }
    if (search && items.length <= 6) search.value = "";
    var query = String(search && search.value || "").trim().toLowerCase();
    button.disabled = !state.activeChatId || !items.length;
    button.title = items.length ? "Ресурсы чата: " + items.length : "В чате пока нет ресурсов";
    button.setAttribute("aria-label", button.title);
    count.textContent = String(items.length);
    count.classList.toggle("hidden", !items.length);
    if (search) search.parentElement.classList.toggle("hidden", items.length <= 6);
    list.replaceChildren();

    var filtered = items.filter(function (artifact) {
      if (!query) return true;
      return [artifactTitle(artifact), kindLabel(artifactKind(artifact)), artifactMeta(artifact)]
        .join(" ").toLowerCase().indexOf(query) >= 0;
    });
    if (!filtered.length) {
      var empty = document.createElement("div");
      empty.className = "chat-resource-empty";
      empty.textContent = query ? "Ничего не найдено." : "Ресурсов пока нет.";
      list.appendChild(empty);
      return;
    }

    ["created", "files", "system"].forEach(function (category) {
      var groupItems = filtered.filter(function (artifact) { return kindCategory(artifactKind(artifact)) === category; });
      if (!groupItems.length) return;
      var section = document.createElement("section");
      section.className = "chat-resource-group";
      var heading = document.createElement("div");
      heading.className = "chat-resource-group-title";
      heading.textContent = categoryLabel(category);
      section.appendChild(heading);
      groupItems.forEach(function (artifact) {
        var row = document.createElement("button");
        row.type = "button";
        row.className = "chat-resource-row category-" + category;
        row.title = "Открыть во вкладке «Артефакты»";
        var icon = document.createElement("span");
        icon.className = "artifact-type-icon";
        icon.innerHTML = iconSvg(artifactKind(artifact));
        var copy = document.createElement("span");
        copy.className = "chat-resource-row-copy";
        var title = document.createElement("strong");
        title.textContent = artifactTitle(artifact);
        var meta = document.createElement("span");
        meta.textContent = artifactMeta(artifact);
        copy.appendChild(title);
        copy.appendChild(meta);
        var arrow = document.createElement("span");
        arrow.className = "chat-resource-row-arrow";
        arrow.setAttribute("aria-hidden", "true");
        arrow.textContent = "›";
        row.appendChild(icon);
        row.appendChild(copy);
        row.appendChild(arrow);
        row.addEventListener("click", function () { openArtifactResource(artifact); });
        section.appendChild(row);
      });
      list.appendChild(section);
    });
  }

  function bindChatResourceNavigation() {
    var button = $("toggleChatResourcesButton");
    var menu = $("chatResourceMenu");
    var search = $("chatResourcesSearchInput");
    var openAll = $("openArtifactsTabButton");
    if (!button || !menu) return;
    button.addEventListener("click", function () {
      setChatResourcePopoverOpen(!menu.classList.contains("is-open"));
    });
    if (search) {
      search.addEventListener("input", renderChatResourceNavigation);
      search.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
          event.preventDefault();
          setChatResourcePopoverOpen(false);
          button.focus();
        }
      });
    }
    if (openAll) {
      openAll.addEventListener("click", function () {
        setChatResourcePopoverOpen(false);
        switchTab("artifacts");
        if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace();
      });
    }
    document.addEventListener("pointerdown", function (event) {
      if (menu.classList.contains("is-open") && !menu.contains(event.target)) setChatResourcePopoverOpen(false);
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && menu.classList.contains("is-open")) {
        setChatResourcePopoverOpen(false);
        button.focus();
      }
    });
    renderChatResourceNavigation();
  }

  window.RNAssistantArtifactVisuals = {
    category: kindCategory,
    categoryLabel: categoryLabel,
    iconSvg: iconSvg,
    kindLabel: kindLabel,
    meta: artifactMeta
  };
  window.artifactResourceHeads = artifactResourceHeads;
  window.messageResourceRefs = messageResourceRefs;
  window.appendMessageArtifactCards = appendMessageArtifactCards;
  window.appendAgentRunResourceCards = appendAgentRunResourceCards;
  window.bindChatResourceNavigation = bindChatResourceNavigation;
  window.renderChatResourceNavigation = renderChatResourceNavigation;
  window.openArtifactResource = openArtifactResource;
}());
