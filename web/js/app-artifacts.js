(function () {
  "use strict";

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
  function artifactTitle(artifact) { if (value(artifact, "AvailabilityIssue", "availabilityIssue", "") === "metadata_unavailable") return "Недоступный ресурс"; return value(artifact, "Title", "title", "Артефакт") || "Артефакт"; }
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
    var headArtifacts = [];
    heads.forEach(function (head) {
      var headId = String(value(head, "ArtifactId", "artifactId", "") || "").toLowerCase();
      if (headId) headByArtifactId[headId] = head;
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
      headArtifacts: headArtifacts,
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
    return index.headByArtifactId[id] || null;
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

  function artifactResourceUri(artifact) {
    var direct = value(artifact, "ResourceUri", "resourceUri", "") || "";
    if (direct) return direct;
    return "";
  }

  function isImageArtifact(artifact) {
    var mimeType = String(value(artifact, "MimeType", "mimeType", "") || "").split(";", 1)[0].toLowerCase();
    return artifactKind(artifact) === "image" || /^image\/(?:jpeg|png|gif|webp)$/.test(mimeType);
  }

  function artifactScopeLabel(artifact) {
    return value(artifact, "DocumentScoped", "documentScoped", false)
      ? "Общий для чатов документа" : "Только этот чат";
  }

  function artifactLogicalKey(artifact) {
    var head = libraryHeadForArtifact(artifact);
    var logicalId = value(head, "LogicalId", "logicalId", "");
    return logicalId ? artifactResourceClass(artifact) + ":" + artifactKind(artifact) + ":" + logicalId
      : String(artifactId(artifact) || "").toLowerCase();
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
    var search = document.getElementById("artifactNavSearch");
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
    switchTab("artifacts");
    if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.editor(artifact);
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
      // The thumbnail observer already defers the read. Start decoding now,
      // without a second lazy queue that can outlive the cache-owned URL.
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
    return window.RNAssistantIcons ? window.RNAssistantIcons.render(kind) : "";
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
    if (kind === "plan") return [artifactScopeLabel(artifact), planMeta(artifact), versionLabel].filter(Boolean).join(" · ");
    var mimeType = value(artifact, "MimeType", "mimeType", "") || "";
    var bytes = formatBytes(value(artifact, "ContentByteLength", "contentByteLength", 0));
    var parts = [artifactScopeLabel(artifact), kindLabel(kind)];
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

  function openArtifactResource(artifact, galleryArtifacts, gallerySource) {
    if (!artifact || value(artifact, "AvailabilityIssue", "availabilityIssue", "") === "metadata_unavailable") return false;
    if (state.htmlWorkspaceDirty) {
      window.alert("Сначала сохраните или отмените изменения текущего артефакта.");
      return false;
    }
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
    switchTab("artifacts");
    if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.editor(artifact);
    if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace();
    return true;
  }

  function artifactCard(artifact) {
    var kind = artifactKind(artifact);
    var removed = artifactRemoved(artifact);
    var unavailable = value(artifact, "AvailabilityIssue", "availabilityIssue", "") === "metadata_unavailable";
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
    if (unavailable) {
      card.disabled = true;
      card.title = "Метаданные ресурса недоступны. Ссылку можно убрать через «Ресурсы».";
      card.setAttribute("aria-label", card.title);
    }
    if (!removed && !unavailable) card.addEventListener("click", function () { openArtifactResource(artifact); });
    return card;
  }

  function messageArtifacts(message) {
    return (message.ArtifactIds || []).map(artifactById).filter(Boolean).filter(function (artifact) {
      var kind = artifactKind(artifact);
      return kind !== "attachment" && kind !== "image" && kind !== "audio";
    });
  }

  function messageImageArtifacts(message) {
    return (message.ArtifactIds || []).map(artifactById).filter(Boolean).filter(isImageArtifact);
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

  function releaseMessageMediaThumbnails(root) {
    if (!chatThumbnailObserver || !root || !root.querySelectorAll) return;
    Array.prototype.forEach.call(root.querySelectorAll(".rn-artifact-thumbnail"), function (node) {
      chatThumbnailObserver.unobserve(node);
    });
  }

  function observeChatThumbnail(node, resourceUri) {
    if (typeof window.IntersectionObserver !== "function") {
      loadArtifactThumbnail(resourceUri);
      return;
    }
    if (!chatThumbnailObserver) {
      chatThumbnailObserver = new window.IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (!entry.isIntersecting || !entry.target.isConnected) return;
          // Keep observing: an exact thumbnail may expire or leave the bounded
          // cache while this message stays mounted outside the viewport.
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
    var positions = Object.create(null);
    var artifacts = [];
    var messages = (items || []).map(function (item) { return item && item.message; });
    if (finalMessage && finalMessage.message) messages.push(finalMessage.message);
    messages.filter(Boolean).forEach(function (message) {
      messageArtifacts(message).forEach(function (artifact) {
        var key = artifactLogicalKey(artifact);
        if (!key) return;
        if (positions[key] === undefined) {
          positions[key] = artifacts.length;
          artifacts.push(artifact);
        } else if (artifactRevision(artifact) > artifactRevision(artifacts[positions[key]])) {
          artifacts[positions[key]] = artifact;
        }
      });
    });
    return artifacts;
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
    icon.innerHTML = window.RNAssistantIcons.render("files");
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

  window.RNAssistantArtifactVisuals = {
    category: kindCategory,
    categoryLabel: categoryLabel,
    iconSvg: iconSvg,
    kindLabel: kindLabel,
    meta: artifactMeta,
    libraryHead: libraryHeadForArtifact,
    resourceClass: artifactResourceClass,
    versionLabel: artifactVersionLabel,
    scopeLabel: artifactScopeLabel,
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
  window.messageImageAttachmentIds = messageImageAttachmentIds;
  window.appendMessageMediaGallery = appendMessageMediaGallery;
  window.resetMessageMediaThumbnails = resetMessageMediaThumbnails;
  window.releaseMessageMediaThumbnails = releaseMessageMediaThumbnails;
  window.appendMessageArtifactCards = appendMessageArtifactCards;
  window.appendAgentRunResourceCards = appendAgentRunResourceCards;
  window.openArtifactResource = openArtifactResource;
}());
