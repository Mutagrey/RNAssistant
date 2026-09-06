(function () {
  "use strict";

  var resourceNavigationChatId = "";
  var artifactProjectionIndex = null;

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function artifactId(artifact) { return value(artifact, "Id", "id", ""); }
  function artifactKind(artifact) {
    var kind = String(value(artifact, "DisplayKind", "displayKind", value(artifact, "Kind", "kind", "file")) || "file").toLowerCase();
    return kind === "plan_document" ? "plan" : kind;
  }
  function artifactTitle(artifact) { return value(artifact, "Title", "title", "Артефакт") || "Артефакт"; }
  function artifactRevision(artifact) { return Number(value(artifact, "Revision", "revision", 1) || 1); }

  function artifactRemoved(artifact) {
    var uri = String(value(artifact, "ResourceUri", "resourceUri", "") || "").toLowerCase();
    if (!uri) return false;
    return !!artifactProjection().removedResourceUris[uri];
  }

  function artifactLibraryHeads() {
    var projection = state.artifactLibrary || {};
    return value(projection, "Heads", "heads", []) || [];
  }

  function libraryHistory(head) {
    return value(head, "History", "history", []) || [];
  }

  function artifactProjectionKey() {
    var projection = state.artifactLibrary || {};
    var heads = artifactLibraryHeads();
    var artifacts = state.artifacts || [];
    var removed = value(projection, "RemovedResourceUris", "removedResourceUris", []) || [];
    return [
      state.activeChatId || "",
      value(projection, "SessionRevision", "sessionRevision", 0) || 0,
      heads.length,
      artifacts.length,
      removed.length
    ].join("|");
  }

  function artifactProjection() {
    var key = artifactProjectionKey();
    var heads = artifactLibraryHeads();
    var artifacts = state.artifacts || [];
    var projection = state.artifactLibrary || {};
    var removed = value(projection, "RemovedResourceUris", "removedResourceUris", []) || [];
    if (artifactProjectionIndex && artifactProjectionIndex.key === key &&
        artifactProjectionIndex.headsRef === heads &&
        artifactProjectionIndex.artifactsRef === artifacts &&
        artifactProjectionIndex.removedRef === removed) return artifactProjectionIndex;
    var artifactByIdMap = {};
    artifacts.forEach(function (artifact) {
      var id = String(artifactId(artifact) || "").toLowerCase();
      if (id) artifactByIdMap[id] = artifact;
    });
    var headByArtifactId = {};
    var headByHistoryArtifactId = {};
    var revisionResourceUri = {};
    var headArtifacts = [];
    heads.forEach(function (head) {
      var headId = String(value(head, "ArtifactId", "artifactId", "") || "").toLowerCase();
      if (headId) headByArtifactId[headId] = head;
      libraryHistory(head).forEach(function (revision) {
        var id = String(value(revision, "ArtifactId", "artifactId", "") || "").toLowerCase();
        if (!id) return;
        if (!headByHistoryArtifactId[id]) headByHistoryArtifactId[id] = head;
        revisionResourceUri[id + "|" + Number(value(revision, "Revision", "revision", 1) || 1)] =
          value(revision, "ResourceUri", "resourceUri", "") || "";
      });
      var artifact = headId ? artifactByIdMap[headId] : null;
      if (artifact) headArtifacts.push(libraryHeadArtifactFromMap(head, artifact));
    });
    var removedResourceUris = {};
    removed.forEach(function (uri) {
      uri = String(uri || "").toLowerCase();
      if (uri) removedResourceUris[uri] = true;
    });
    artifactProjectionIndex = {
      key: key,
      headsRef: heads,
      artifactsRef: artifacts,
      removedRef: removed,
      artifactById: artifactByIdMap,
      headByArtifactId: headByArtifactId,
      headByHistoryArtifactId: headByHistoryArtifactId,
      headArtifacts: headArtifacts,
      revisionResourceUri: revisionResourceUri,
      removedResourceUris: removedResourceUris
    };
    return artifactProjectionIndex;
  }

  function libraryHeadForArtifact(artifact) {
    var embedded = value(artifact, "LibraryHead", "libraryHead", null);
    if (embedded) return embedded;
    var id = String(artifactId(artifact) || "").toLowerCase();
    if (!id) return null;
    var index = artifactProjection();
    return index.headByArtifactId[id] || index.headByHistoryArtifactId[id] || null;
  }

  function artifactResourceClass(artifact) {
    var head = libraryHeadForArtifact(artifact);
    return String(value(head, "ResourceClass", "resourceClass", "") || "").toLowerCase();
  }

  function artifactLibraryGroup(artifact) {
    var head = libraryHeadForArtifact(artifact);
    return String(value(head, "Group", "group", "") || "").toLowerCase();
  }

  function artifactVersionLabel(artifact) {
    var resourceClass = artifactResourceClass(artifact);
    if (resourceClass === "immutable_original") return "Оригинал";
    if (resourceClass === "derived_resource") return "Производный";
    if (resourceClass === "versioned_document" || resourceClass === "versioned_aggregate") {
      return "v" + artifactRevision(artifact);
    }
    return "";
  }

  function libraryHeadArtifact(head) {
    var artifact = artifactById(value(head, "ArtifactId", "artifactId", ""));
    if (!artifact) return null;
    return libraryHeadArtifactFromMap(head, artifact);
  }

  function libraryHeadArtifactFromMap(head, artifact) {
    var result = {};
    Object.keys(artifact).forEach(function (key) { result[key] = artifact[key]; });
    result.displayKind = value(head, "DisplayKind", "displayKind", artifactKind(artifact));
    result.libraryHead = head;
    return result;
  }

  function artifactById(id) {
    var normalized = String(id || "").toLowerCase();
    return normalized ? artifactProjection().artifactById[normalized] || null : null;
  }

  function messageResourceRefs(message) {
    return value(message, "ResourceRefs", "resourceRefs", []) || [];
  }

  function resourceRefUri(reference) {
    return value(reference, "Uri", "uri", "") || "";
  }

  function artifactResourceUri(artifact) {
    var direct = value(artifact, "ResourceUri", "resourceUri", "") || "";
    if (direct) return direct;
    var id = String(artifactId(artifact) || "").toLowerCase();
    var revision = artifactRevision(artifact);
    return artifactProjection().revisionResourceUri[id + "|" + revision] || "";
  }

  function isImageArtifact(artifact) {
    var mimeType = String(value(artifact, "MimeType", "mimeType", "") || "").split(";", 1)[0].toLowerCase();
    return artifactKind(artifact) === "image" || /^image\/(?:jpeg|png|gif|webp)$/.test(mimeType);
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

  function artifactCollectionLabel(collectionId) {
    return {
      "artifact-plans": "Планы",
      "artifact-authored": "Документы",
      "artifact-files": "Файлы и медиа",
      "artifact-generated": "Созданные снимки",
      "artifact-system": "Служебные данные"
    }[String(collectionId || "")] || "Ресурсы";
  }

  function collectionIdForArtifact(artifact) {
    if (artifactKind(artifact) === "plan") return "artifact-plans";
    return "artifact-" + kindCategory(artifact);
  }

  function artifactCollectionItems(collectionId) {
    var items = artifactResourceHeads().filter(function (artifact) {
      if (artifactKind(artifact) === "html_workspace") return false;
      if (collectionId === "artifact-plans") return artifactKind(artifact) === "plan";
      if (artifactKind(artifact) === "plan") return false;
      return collectionIdForArtifact(artifact) === collectionId;
    });
    var search = document.getElementById("htmlWorkspaceSearchInput");
    var query = String(search && search.value || "").trim().toLowerCase();
    if (query) {
      items = items.filter(function (artifact) {
        return [
          artifactTitle(artifact), artifactKind(artifact),
          value(artifact, "MimeType", "mimeType", ""),
          value(artifact, "RelativePath", "relativePath", ""),
          artifactMeta(artifact)
        ].join(" ").toLowerCase().indexOf(query) >= 0;
      });
    }
    return items.sort(function (left, right) {
      return String(artifactTitle(left)).localeCompare(String(artifactTitle(right)));
    });
  }

  function createImageGalleryContext(source, artifacts, selectedArtifact) {
    var seen = {};
    var items = (artifacts || []).filter(isImageArtifact).map(function (artifact) {
      var uri = artifactResourceUri(artifact);
      if (!uri || seen[uri]) return null;
      seen[uri] = true;
      return {
        artifact: artifact,
        artifactId: artifactId(artifact),
        resourceUri: uri,
        title: artifactTitle(artifact),
        contentSha256: value(artifact, "ContentSha256", "contentSha256", "") || ""
      };
    }).filter(Boolean).slice(0, 10000);
    var selectedUri = artifactResourceUri(selectedArtifact);
    var currentIndex = items.findIndex(function (item) { return item.resourceUri === selectedUri; });
    if (currentIndex < 0 && selectedArtifact && selectedUri) {
      items.unshift({
        artifact: selectedArtifact,
        artifactId: artifactId(selectedArtifact),
        resourceUri: selectedUri,
        title: artifactTitle(selectedArtifact),
        contentSha256: value(selectedArtifact, "ContentSha256", "contentSha256", "") || ""
      });
      items = items.slice(0, 10000);
      currentIndex = 0;
    }
    var context = {
      chatId: state.activeChatId,
      source: source || "library",
      items: items,
      currentIndex: Math.max(0, currentIndex),
      scrollOffset: 0
    };
    state.artifactImageGalleryContext = context;
    return context;
  }

  function artifactImageGalleryContext(artifact) {
    if (!artifact || !isImageArtifact(artifact)) return null;
    var uri = artifactResourceUri(artifact);
    var current = state.artifactImageGalleryContext;
    if (current && current.chatId === state.activeChatId && Array.isArray(current.items)) {
      var currentIndex = current.items.findIndex(function (item) { return item.resourceUri === uri; });
      if (currentIndex >= 0) {
        current.currentIndex = currentIndex;
        return current;
      }
    }
    var collectionId = collectionIdForArtifact(artifact);
    return createImageGalleryContext(
      "library:" + collectionId,
      artifactCollectionItems(collectionId),
      artifact);
  }

  function selectArtifactImageGalleryItem(index) {
    var context = state.artifactImageGalleryContext;
    index = Number(index);
    if (!context || context.chatId !== state.activeChatId || !Array.isArray(context.items) ||
        !Number.isInteger(index) || index < 0 || index >= context.items.length) return false;
    var item = context.items[index];
    var artifact = item && item.artifact;
    if (!artifact || artifactRemoved(artifact) || artifactResourceUri(artifact) !== item.resourceUri) return false;
    context.currentIndex = index;
    state.htmlWorkspaceSelection = { type: "artifact", id: artifactId(artifact) };
    setChatResourcePopoverOpen(false);
    switchTab("artifacts");
    if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace();
    return true;
  }

  function thumbnailRuntime() {
    return window.RNAssistantArtifactThumbnailRuntime || null;
  }

  function artifactThumbnailState(resourceUri) {
    var runtime = thumbnailRuntime();
    return runtime && typeof runtime.state === "function" ? runtime.state(resourceUri) : null;
  }

  function loadArtifactThumbnail(resourceUri) {
    var runtime = thumbnailRuntime();
    return runtime && typeof runtime.load === "function"
      ? runtime.load({ resourceUri: resourceUri })
      : Promise.resolve(false);
  }

  function renderArtifactThumbnailNode(node, resourceUri, thumbnail, title) {
    if (!node) return;
    node.classList.add("rn-artifact-thumbnail");
    node.dataset.resourceUri = String(resourceUri || "");
    node.replaceChildren();
    node.title = "";
    node.classList.remove("is-ready", "is-error", "is-loading");
    thumbnail = thumbnail || artifactThumbnailState(resourceUri);
    if (thumbnail && thumbnail.status === "ready" && thumbnail.resourceUri === resourceUri) {
      var image = document.createElement("img");
      image.alt = title || "";
      image.loading = "lazy";
      image.src = thumbnail.data.objectUrl;
      node.appendChild(image);
      node.classList.add("is-ready");
    } else if (thumbnail && thumbnail.status === "error") {
      var unavailable = document.createElement("span");
      unavailable.className = "rn-artifact-thumbnail-unavailable";
      unavailable.textContent = "×";
      node.appendChild(unavailable);
      node.classList.add("is-error");
      node.title = thumbnail.message || "Миниатюра недоступна";
    } else {
      node.classList.add("is-loading");
    }
  }

  function updateArtifactThumbnailViews(resourceUri, thumbnail) {
    Array.prototype.slice.call(document.querySelectorAll(".rn-artifact-thumbnail")).forEach(function (node) {
      if (node.dataset && node.dataset.resourceUri === resourceUri) {
        renderArtifactThumbnailNode(node, resourceUri, thumbnail, node.dataset.title || "");
      }
    });
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
      task_list: "Task list",
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
    var artifact = kind && typeof kind === "object" ? kind : null;
    var group = artifact ? artifactLibraryGroup(artifact) : "";
    if (group === "authored_documents") return "authored";
    if (group === "files_media") return "files";
    if (group === "generated_snapshots") return "generated";
    if (group === "system_evidence") return "system";
    kind = artifact ? artifactKind(artifact) : String(kind || "").toLowerCase();
    if (["attachment", "image", "audio", "file"].indexOf(kind) >= 0) return "files";
    if (["tool_result", "compaction", "task_list"].indexOf(kind) >= 0) return "system";
    if (["chart"].indexOf(kind) >= 0) return "generated";
    return "authored";
  }

  function categoryLabel(category) {
    return {
      authored: "Документы",
      files: "Файлы и медиа",
      generated: "Созданные снимки",
      system: "Служебные данные"
    }[category] || "Ресурсы";
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

  function planMeta(artifact) {
    var head = libraryHeadForArtifact(artifact);
    var status = String(value(head, "Status", "status", "") || "").toLowerCase();
    var labels = { draft: "Черновик", ready: "Готов", completed: "Завершён", blocked: "Заблокирован" };
    return labels[status] || "План";
  }

  function formatBytes(bytes) {
    bytes = Number(bytes || 0);
    if (!bytes) return "";
    if (bytes < 1024) return bytes + " Б";
    if (bytes < 1024 * 1024) return Math.round(bytes / 102.4) / 10 + " КБ";
    return Math.round(bytes / 1024 / 102.4) / 10 + " МБ";
  }

  function artifactMeta(artifact) {
    if (artifactRemoved(artifact)) return "Ресурс удалён";
    var kind = artifactKind(artifact);
    var versionLabel = artifactVersionLabel(artifact);
    if (kind === "plan") return [planMeta(artifact), versionLabel].filter(Boolean).join(" · ");
    var mimeType = value(artifact, "MimeType", "mimeType", "") || "";
    var bytes = formatBytes(value(artifact, "ContentByteLength", "contentByteLength", 0));
    var parts = [kindLabel(kind)];
    if (versionLabel) {
      parts.push(versionLabel);
    } else if (bytes) {
      parts.push(bytes);
    } else if (mimeType && mimeType.indexOf("/") >= 0) {
      parts.push(mimeType.split("/").pop());
    }
    return parts.join(" · ");
  }

  function artifactResourceHeads(sourceArtifacts) {
    if (!Array.isArray(sourceArtifacts)) {
      return artifactProjection().headArtifacts.slice();
    }
    var seen = {};
    return sourceArtifacts.filter(function (artifact) {
      var id = String(artifactId(artifact) || "").toLowerCase();
      if (!id || seen[id]) return false;
      seen[id] = true;
      return true;
    });
  }

  function chatDockResourceHeads() {
    var activePlanId = String(state.activePlanDocumentArtifactId || "").toLowerCase();
    return artifactResourceHeads().filter(function (artifact) {
      var isActivePlan = activePlanId && artifactKind(artifact) === "plan" &&
        String(artifactId(artifact)).toLowerCase() === activePlanId;
      return !isActivePlan;
    });
  }

  function openArtifactResource(artifact, galleryArtifacts, gallerySource) {
    if (!artifact) return false;
    var kind = artifactKind(artifact);
    if (isImageArtifact(artifact)) {
      createImageGalleryContext(
        gallerySource || (Array.isArray(galleryArtifacts) ? "chat" : "library:" + collectionIdForArtifact(artifact)),
        Array.isArray(galleryArtifacts) ? galleryArtifacts : artifactCollectionItems(collectionIdForArtifact(artifact)),
        artifact);
    } else {
      state.artifactImageGalleryContext = null;
    }
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
    return true;
  }

  function artifactCard(artifact) {
    var kind = artifactKind(artifact);
    var removed = artifactRemoved(artifact);
    var card = document.createElement("button");
    card.type = "button";
    card.className = "chat-artifact-card kind-" + kind + " category-" + kindCategory(artifact);
    card.dataset.artifactId = artifactId(artifact);
    card.title = removed ? "Ресурс удалён" : "Открыть во вкладке «Артефакты»";
    card.setAttribute("aria-label", removed
      ? "Ресурс удалён: " + artifactTitle(artifact)
      : "Открыть " + kindLabel(kind) + " «" + artifactTitle(artifact) + "»");
    if (removed) {
      card.className += " is-removed";
      card.disabled = true;
    }

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
    arrow.textContent = removed ? "×" : "›";
    card.appendChild(arrow);
    if (!removed) card.addEventListener("click", function () { openArtifactResource(artifact); });
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

  function messageImageArtifacts(message) {
    var seen = {};
    return messageResourceRefs(message).map(artifactByReference).filter(Boolean).filter(function (artifact) {
      if (!isImageArtifact(artifact)) return false;
      var uri = artifactResourceUri(artifact);
      if (!uri || seen[uri]) return false;
      seen[uri] = true;
      return true;
    });
  }

  function imageAttachmentId(artifact) {
    try {
      var metadata = JSON.parse(value(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
      return String(metadata.attachmentId || metadata.AttachmentId || "").toLowerCase();
    } catch (ignore) {
      return "";
    }
  }

  function messageImageAttachmentIds(message) {
    var result = {};
    messageImageArtifacts(message).forEach(function (artifact) {
      var id = imageAttachmentId(artifact);
      if (id) result[id] = true;
    });
    return result;
  }

  var chatThumbnailObserver = null;

  function resetMessageMediaThumbnails() {
    if (chatThumbnailObserver) chatThumbnailObserver.disconnect();
    chatThumbnailObserver = null;
  }

  function observeChatThumbnail(node, resourceUri) {
    if (typeof window.IntersectionObserver !== "function") {
      loadArtifactThumbnail(resourceUri);
      return;
    }
    if (!chatThumbnailObserver) {
      chatThumbnailObserver = new window.IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting) return;
          chatThumbnailObserver.unobserve(entry.target);
          loadArtifactThumbnail(entry.target.dataset.resourceUri);
        });
      }, { rootMargin: "160px" });
    }
    chatThumbnailObserver.observe(node);
  }

  function appendArtifactMediaGallery(parent, artifacts, className) {
    artifacts = (artifacts || []).filter(isImageArtifact);
    if (!parent || !artifacts.length) return;
    var wrap = document.createElement("div");
    wrap.className = "chat-media-gallery count-" + Math.min(4, artifacts.length) + (className ? " " + className : "");
    artifacts.slice(0, 4).forEach(function (artifact, index) {
      var uri = artifactResourceUri(artifact);
      if (!uri) return;
      var button = document.createElement("button");
      button.type = "button";
      button.className = "chat-media-item";
      button.title = "Открыть изображение «" + artifactTitle(artifact) + "»";
      button.setAttribute("aria-label", button.title);
      var thumbnail = document.createElement("span");
      thumbnail.dataset.title = artifactTitle(artifact);
      renderArtifactThumbnailNode(thumbnail, uri, artifactThumbnailState(uri), artifactTitle(artifact));
      button.appendChild(thumbnail);
      if (index === 3 && artifacts.length > 4) {
        var more = document.createElement("span");
        more.className = "chat-media-more";
        more.textContent = "+" + (artifacts.length - 4);
        button.appendChild(more);
      }
      button.addEventListener("click", function () { openArtifactResource(artifact, artifacts, "chat"); });
      wrap.appendChild(button);
      observeChatThumbnail(thumbnail, uri);
    });
    parent.appendChild(wrap);
  }

  function appendMessageMediaGallery(parent, message) {
    appendArtifactMediaGallery(parent, messageImageArtifacts(message), "message-media-gallery");
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

  function visibleRunArtifacts(artifacts) {
    artifacts = artifacts || [];
    var hasChart = artifacts.some(function (artifact) {
      return artifactKind(artifact) === "chart";
    });
    if (!hasChart) return artifacts;
    return artifacts.filter(function (artifact) {
      return artifactKind(artifact) !== "html_workspace";
    });
  }

  function collectRunImageArtifacts(items, finalMessage) {
    var messages = (items || []).map(function (item) { return item && item.message; });
    if (finalMessage && finalMessage.message) messages.push(finalMessage.message);
    var seen = {};
    var artifacts = [];
    messages.filter(Boolean).forEach(function (message) {
      messageImageArtifacts(message).forEach(function (artifact) {
        var uri = artifactResourceUri(artifact);
        if (!uri || seen[uri]) return;
        seen[uri] = true;
        artifacts.push(artifact);
      });
    });
    return artifacts;
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
    appendArtifactMediaGallery(parent, collectRunImageArtifacts(items, finalMessage), "agent-run-media-gallery");
    var artifacts = visibleRunArtifacts(collectRunArtifacts(items, finalMessage));
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
      if (typeof window.setAgentPlanDockOpen === "function") window.setAgentPlanDockOpen(false);
      var itemCount = renderChatResourceNavigation();
      var search = $("chatResourcesSearchInput");
      if (search && itemCount > 6) search.focus();
    }
  }

  function renderChatResourceNavigation() {
    var dock = $("chatResourceDock");
    var button = $("toggleChatResourcesButton");
    var count = $("chatResourceCount");
    var list = $("chatResourcesList");
    var search = $("chatResourcesSearchInput");
    if (!button || !count || !list) return 0;
    var items = chatDockResourceHeads();
    var hasResources = !!state.activeChatId && !!items.length;
    if (dock) dock.classList.toggle("hidden", !hasResources);
    if (!hasResources) setChatResourcePopoverOpen(false);
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
      return items.length;
    }

    var groups = { authored: [], files: [], generated: [], system: [] };
    filtered.forEach(function (artifact) {
      var category = kindCategory(artifact);
      if (!groups[category]) groups[category] = [];
      groups[category].push(artifact);
    });
    ["authored", "files", "generated", "system"].forEach(function (category) {
      var groupItems = groups[category] || [];
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
    return items.length;
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
    meta: artifactMeta,
    libraryHead: libraryHeadForArtifact,
    resourceClass: artifactResourceClass,
    versionLabel: artifactVersionLabel,
    removed: artifactRemoved
  };
  window.artifactResourceHeads = artifactResourceHeads;
  window.artifactCollectionItems = artifactCollectionItems;
  window.artifactCollectionLabel = artifactCollectionLabel;
  window.artifactImageGalleryContext = artifactImageGalleryContext;
  window.selectArtifactImageGalleryItem = selectArtifactImageGalleryItem;
  window.renderArtifactThumbnailNode = renderArtifactThumbnailNode;
  window.updateArtifactThumbnailViews = updateArtifactThumbnailViews;
  window.loadArtifactThumbnail = loadArtifactThumbnail;
  window.messageResourceRefs = messageResourceRefs;
  window.messageImageAttachmentIds = messageImageAttachmentIds;
  window.appendMessageMediaGallery = appendMessageMediaGallery;
  window.resetMessageMediaThumbnails = resetMessageMediaThumbnails;
  window.appendMessageArtifactCards = appendMessageArtifactCards;
  window.appendAgentRunResourceCards = appendAgentRunResourceCards;
  window.bindChatResourceNavigation = bindChatResourceNavigation;
  window.renderChatResourceNavigation = renderChatResourceNavigation;
  window.setChatResourcePopoverOpen = setChatResourcePopoverOpen;
  window.openArtifactResource = openArtifactResource;
}());
