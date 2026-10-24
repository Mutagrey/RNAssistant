(function () {
  "use strict";

  function annotation(item) { return item && (item.visualAnnotation || item.VisualAnnotation); }
  function value(item, camel, pascal) { return item && (item[camel] !== undefined ? item[camel] : item[pascal]); }
  function targetLabel(selection) {
    return selection.tag + (selection.stableUiId ? " · " + selection.stableUiId : "") +
      (selection.textExcerpt ? " · " + selection.textExcerpt.slice(0, 55) : "");
  }
  function messageCard(item) {
    var data = annotation(item), selection = data && (data.selection || data.Selection);
    if (!selection) return null;
    var card = document.createElement("div"); card.className = "visual-annotation-message";
    var badge = document.createElement("span"); badge.className = "visual-annotation-number";
    badge.textContent = String(data.annotationNumber || data.AnnotationNumber);
    var copy = document.createElement("div"); copy.className = "visual-annotation-message-copy";
    var target = document.createElement("strong"); target.textContent = targetLabel(selection);
    var comment = document.createElement("span"); comment.textContent = data.comment || data.Comment || "Только элемент";
    var source = document.createElement("small");
    var sourceId = value(item, "visualSourceArtifactId", "VisualSourceArtifactId");
    var plugin = window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() === "plugin";
    source.textContent = plugin ? "Отправлено · релиз " + String(sourceId || "").slice(0, 16) :
      "Отправлено · версия " + value(item, "visualSourceRevision", "VisualSourceRevision");
    if (plugin) source.title = String(sourceId || "");
    copy.append(target, comment, source);
    var show = document.createElement("button"); show.type = "button"; show.textContent = "На странице";
    show.addEventListener("click", function () {
      var controller = window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() === "plugin"
        ? window.RNAssistantPluginAnnotations : window.RNAssistantHtmlWorkspaceAnnotations;
      if (controller) controller.showSent(item);
    });
    card.append(badge, copy, show);
    return card;
  }
  function create(options) {
    var frame = options.frame, inspector = options.inspector;
    var context = options.context || {
      mount: document.getElementById("chatBrowserFrameMount"),
      browser: document.getElementById("chatBrowser"),
      button: document.getElementById("inspectHtmlWorkspaceButton"),
      statusNode: document.getElementById("htmlWorkspaceInspectStatus"),
      isOpen: function () { return this.browser.contains(frame) && this.browser.getAttribute("aria-hidden") === "false"; },
      source: function () {
        var matches = (state.artifacts || []).filter(function (artifact) {
          return (artifact.Id || artifact.id) === state.activeHtmlArtifactId &&
            (artifact.Kind || artifact.kind) === "html_workspace";
        });
        return matches.length === 1 ? { id: state.activeHtmlArtifactId,
          revision: Number(matches[0].Revision || matches[0].revision || 0) } : null;
      },
      pending: function () { var draft = readOfficeDraft(state.activeChatId); return draft && draft.pendingVisualAnnotation || null; },
      setPending: function (value) { window.RNAssistantChatDrafts.setPendingOfficeAnnotation(state.activeChatId, value); },
      drafts: function () { return state.draftAttachments || []; },
      messages: function () { return state.chatDetailPending || state.chatDetailError ? [] : state.messages || []; },
      updated: function () { if (typeof renderAttachmentDrafts === "function") renderAttachmentDrafts(); },
      remove: function (item) { removeDraftAttachment(item); },
      reserve: function () { return window.RNAssistantChatDrafts.reserveOfficeAnnotationNumber(state.activeChatId); },
      openSource: function () { return window.RNAssistantChatBrowser.openCurrent(); },
      status: function (message) { setAttachmentStatus(state.activeChatId, message, "warning"); }
    };
    var mount = context.mount, browser = context.browser;
    var layer = document.createElement("div"), editor = null, sentPopup = null;
    var locations = Object.create(null), pinnedSent = null;
    layer.className = "visual-annotation-layer";
    mount.appendChild(layer);

    function browserOpen() { return context.isOpen(); }
    function currentSource(item) {
      var sourceId = value(item, "visualSourceArtifactId", "VisualSourceArtifactId");
      var revision = Number(value(item, "visualSourceRevision", "VisualSourceRevision"));
      var source = context.source();
      return !!source && sourceId === source.id && revision === source.revision;
    }
    function pending() { return context.pending(); }
    function pendingItem() {
      var value = pending();
      return value ? { Id: "pending-visual-" + value.Number, pendingVisual: true,
        visualAnnotation: { selection: value.Selection, comment: value.Comment,
          annotationNumber: value.Number }, visualSourceArtifactId: value.ArtifactId,
        visualSourceRevision: value.ArtifactRevision, replacingDraftId: value.ReplacingDraftId } : null;
    }
    function stagedDrafts() {
      return context.drafts().filter(function (item) { return !!annotation(item); });
    }
    function drafts() {
      var pendingDraft = pendingItem(), staged = stagedDrafts();
      if (!pendingDraft) return staged;
      return staged.filter(function (item) { return attachmentId(item) !== pendingDraft.replacingDraftId; })
        .concat(pendingDraft);
    }
    function sent() {
      return context.messages().filter(function (message) {
        return messageRole(message) === "user" && !message.Local && !message.Pending && !message.Failed;
      }).reduce(function (items, message) {
        return items.concat(messageAttachments(message).filter(function (item) { return !!annotation(item); }));
      }, []);
    }
    function visibleSent() {
      var items = sent().filter(currentSource);
      var limit = Math.max(0, 100 - drafts().filter(currentSource).length);
      var recent = limit ? items.slice(-limit) : [];
      if (pinnedSent && limit && items.some(function (item) { return attachmentId(item) === attachmentId(pinnedSent); }) &&
          !recent.some(function (item) { return attachmentId(item) === attachmentId(pinnedSent); })) {
        recent = recent.slice(1).concat(pinnedSent);
      }
      return recent;
    }
    function point(rect) {
      if (!rect) return null;
      var host = mount.getBoundingClientRect(), target = frame.getBoundingClientRect();
      return { x: target.left - host.left + rect.x, y: target.top - host.top + rect.y,
        width: rect.width, height: rect.height };
    }
    function placeEditor() {
      if (!editor) return;
      var rect = editor.item ? (locations[attachmentId(editor.item)] || {}).rect || editor.record.selection.rect
        : editor.record.selection.rect;
      var at = point(rect), width = Math.min(290, Math.max(220, mount.clientWidth - 24));
      var x = at ? at.x + at.width + 12 : 12;
      if (x + width > mount.clientWidth - 12) x = at ? at.x - width - 12 : 12;
      if (x < 12) x = 12;
      var y = at ? at.y : 68;
      y = Math.max(60, Math.min(y, mount.clientHeight - 195));
      editor.root.style.left = x + "px";
      editor.root.style.top = y + "px";
      editor.root.style.width = width + "px";
    }
    function closeSent() {
      if (!sentPopup) return;
      sentPopup.root.remove(); sentPopup = null;
    }
    function placeSent() {
      if (!sentPopup) return;
      var location = locations[attachmentId(sentPopup.item)];
      var selection = annotation(sentPopup.item).selection || annotation(sentPopup.item).Selection;
      var at = point(location && location.rect || selection.rect);
      var width = Math.min(290, Math.max(220, mount.clientWidth - 24));
      var x = at ? at.x + at.width + 12 : 12;
      if (x + width > mount.clientWidth - 12) x = at ? at.x - width - 12 : 12;
      sentPopup.root.style.left = Math.max(12, x) + "px";
      sentPopup.root.style.top = Math.max(60, Math.min(at ? at.y : 68, mount.clientHeight - 145)) + "px";
      sentPopup.root.style.width = width + "px";
    }
    function openSent(item) {
      closeSent(); closeEditor(false);
      var data = annotation(item), selection = data.selection || data.Selection;
      var root = document.createElement("div"); root.className = "visual-annotation-editor visual-annotation-sent";
      var head = document.createElement("div"); head.className = "visual-annotation-editor-head";
      var badge = document.createElement("span"); badge.className = "visual-annotation-number";
      badge.textContent = String(data.annotationNumber || data.AnnotationNumber);
      var target = document.createElement("span"); target.className = "visual-annotation-target";
      target.textContent = targetLabel(selection);
      head.append(badge, target);
      var comment = document.createElement("p"); comment.className = "visual-annotation-sent-comment";
      comment.textContent = data.comment || data.Comment || "Только элемент";
      var actions = document.createElement("div"); actions.className = "visual-annotation-editor-actions";
      var close = document.createElement("button"); close.type = "button"; close.textContent = "Закрыть";
      close.addEventListener("click", closeSent); actions.appendChild(close);
      root.append(head, comment, actions); layer.appendChild(root);
      sentPopup = { root: root, item: item }; placeSent(); close.focus();
    }
    function closeEditor(cancel) {
      if (!editor) return;
      var number = editor.number;
      editor.root.remove(); editor = null; frame.classList.remove("annotation-editing");
      if (cancel && pending() && pending().Number === number) {
        context.setPending(null);
        context.updated();
      }
    }
    async function finish() {
      if (!editor || editor.busy) return;
      var editing = editor;
      editing.busy = true;
      editing.done.disabled = true;
      editing.cancel.disabled = true;
      try {
        var replacing = editing.item && editing.item.pendingVisual
          ? context.drafts().find(function (item) {
              return attachmentId(item) === editing.item.replacingDraftId;
            }) : editing.item;
        var attachment = await options.stage(editing.record, editing.textarea.value,
          editing.number, replacing || null, true);
        if (!attachment) throw new Error("Аннотация не была добавлена.");
        if (editor === editing) closeEditor();
        context.updated();
      } catch (error) {
        var latest = stagedDrafts().find(function (item) {
          var data = annotation(item);
          return data && Number(data.annotationNumber || data.AnnotationNumber) === editing.number;
        });
        if (latest) {
          editing.item = latest;
          var stillPending = pending();
          if (stillPending && stillPending.Number === editing.number)
            context.setPending(
              Object.assign({}, stillPending, { ReplacingDraftId: attachmentId(latest) }));
        }
        editing.busy = false;
        editing.done.disabled = false;
        editing.cancel.disabled = false;
        context.statusNode.textContent = error.detail || error.message || "Не удалось сохранить аннотацию.";
      }
    }
    function openEditor(record, number, item) {
      if (!browserOpen() || !record) return;
      closeEditor(false);
      var root = document.createElement("div");
      root.className = "visual-annotation-editor";
      root.setAttribute("role", "dialog");
      root.setAttribute("aria-label", "Аннотация " + number);
      var head = document.createElement("div"); head.className = "visual-annotation-editor-head";
      var badge = document.createElement("span"); badge.className = "visual-annotation-number";
      badge.textContent = String(number); head.appendChild(badge);
      var label = document.createElement("span"); label.className = "visual-annotation-target";
      label.textContent = targetLabel(record.selection); head.appendChild(label);
      var textarea = document.createElement("textarea");
      textarea.rows = 3; textarea.maxLength = 2048; textarea.placeholder = "Что изменить или проверить?";
      textarea.setAttribute("aria-label", "Комментарий к элементу " + number);
      textarea.value = item ? annotation(item).comment || "" : "";
      var actions = document.createElement("div"); actions.className = "visual-annotation-editor-actions";
      var cancel = document.createElement("button"); cancel.type = "button";
      cancel.textContent = "Отмена";
      var done = document.createElement("button"); done.type = "button";
      done.className = "primary"; done.textContent = "Готово";
      actions.append(cancel, done); root.append(head, textarea, actions); layer.appendChild(root);
      editor = { root: root, textarea: textarea, record: record, number: number,
        item: item || null, cancel: cancel, done: done, busy: false };
      frame.classList.add("annotation-editing");
      cancel.addEventListener("click", function () { closeEditor(true); });
      done.addEventListener("click", finish);
      textarea.addEventListener("input", function () {
        var saved = pending();
        if (!saved || saved.Number !== number) return;
        context.setPending(
          Object.assign({}, saved, { Comment: textarea.value }));
        document.querySelectorAll(".visual-annotation-list-row[data-pending-visual='true'] .visual-annotation-comment")
          .forEach(function (node) { node.textContent = "Не готово · " + (textarea.value || "Только элемент"); });
      });
      textarea.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
          event.preventDefault(); event.stopPropagation(); closeEditor(false);
          context.button.focus({ preventScroll: true });
        }
        if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); finish(); }
      });
      placeEditor(); textarea.focus();
    }
    function edit(item) {
      closeSent();
      if (!currentSource(item)) {
        context.status("Источник аннотации изменился. Удалите её и выберите элемент заново.");
        return;
      }
      var data = annotation(item), number = Number(data.annotationNumber || data.AnnotationNumber);
      var saved = pending();
      if (saved && saved.Number !== number) {
        context.status("Сначала завершите или удалите незавершённую аннотацию.");
        return;
      }
      if (!saved) {
        context.setPending({
          ArtifactId: value(item, "visualSourceArtifactId", "VisualSourceArtifactId"),
          ArtifactRevision: Number(value(item, "visualSourceRevision", "VisualSourceRevision")),
          Number: number, Comment: data.comment || data.Comment || "",
          Selection: data.selection || data.Selection, ReplacingDraftId: attachmentId(item)
        });
        context.updated();
      }
      context.openSource();
      var visible = pendingItem(), selection = data.selection || data.Selection;
      openEditor({ generation: inspector.generation(), selection: selection,
        sourceId: value(item, "visualSourceArtifactId", "VisualSourceArtifactId"),
        sourceRevision: Number(value(item, "visualSourceRevision", "VisualSourceRevision")),
        sessionId: context.sessionId ? context.sessionId() : null },
        number, visible);
      inspector.reveal(attachmentId(visible));
    }
    function show(item) {
      if (!currentSource(item)) {
        context.status("Источник аннотации изменился. Удалите её и выберите элемент заново.");
        return;
      }
      context.openSource();
      inspector.reveal(attachmentId(item));
    }
    function showSent(item) {
      if (!currentSource(item)) {
        context.status("Для этой аннотации нужна точная версия источника. Текущий просмотр отличается.");
        return;
      }
      if (!context.openSource()) {
        context.status("Не удалось открыть источник этой аннотации.");
        return;
      }
      pinnedSent = item;
      sync(); inspector.reveal(attachmentId(item)); openSent(item);
    }
    function renderMarkers() {
      layer.querySelectorAll(".visual-annotation-marker").forEach(function (node) { node.remove(); });
      if (!browserOpen()) return;
      var overlaps = Object.create(null);
      var entries = drafts().filter(currentSource).map(function (item) { return { item: item, sent: false }; })
        .concat(visibleSent().map(function (item) { return { item: item, sent: true }; }));
      entries.forEach(function (entry) {
        var item = entry.item;
        var position = point((locations[attachmentId(item)] || {}).rect);
        if (!position) return;
        var data = annotation(item), number = Number(data.annotationNumber || data.AnnotationNumber);
        var marker = document.createElement("button"); marker.type = "button";
        marker.className = "visual-annotation-marker"; marker.textContent = String(number);
        marker.classList.toggle("is-sent", entry.sent);
        marker.setAttribute("aria-label", (entry.sent ? "Просмотреть отправленную аннотацию " : "Изменить аннотацию ") + number);
        var key = Math.round(position.x / 16) + ":" + Math.round(position.y / 16);
        var offset = overlaps[key] || 0; overlaps[key] = offset + 1;
        marker.style.left = Math.max(8, Math.min(mount.clientWidth - 30, position.x + position.width - 13 + offset * 25)) + "px";
        marker.style.top = Math.max(55, Math.min(mount.clientHeight - 30, position.y - 11)) + "px";
        marker.addEventListener("click", function () { if (entry.sent) showSent(item); else edit(item); });
        layer.appendChild(marker);
      });
      placeEditor();
      placeSent();
    }
    function updateTargetStates() {
      (context.draftBox || document).querySelectorAll(".visual-annotation-target-state").forEach(function (node) {
        var location = locations[node.dataset.annotationId];
        node.textContent = location && location.status === "missing" ? "Цель не найдена" :
          location && location.status === "outside" ? "Вне экрана" : "";
        node.hidden = !node.textContent;
      });
    }
    function sync() {
      var watched = drafts().filter(currentSource).concat(visibleSent()).map(function (item) {
        var selection = annotation(item).selection || annotation(item).Selection;
        return { id: attachmentId(item), selector: selection.selector, tag: selection.tag,
          stableUiId: selection.stableUiId || "" };
      });
      inspector.watch(watched);
      renderMarkers();
    }
    function renderDrafts(box, items) {
      if (!items.length) return;
      var details = document.createElement("details"); details.className = "visual-annotation-list";
      var summary = document.createElement("summary"); summary.textContent = "Аннотации · " + items.length;
      details.appendChild(summary);
      details.addEventListener("mouseenter", function () { details.open = true; });
      details.addEventListener("mouseleave", function () { if (details.dataset.pinned !== "true") details.open = false; });
      summary.addEventListener("click", function (event) {
        event.preventDefault(); details.dataset.pinned = details.dataset.pinned === "true" ? "false" : "true";
        details.open = details.dataset.pinned === "true";
      });
      var list = document.createElement("div"); list.className = "visual-annotation-list-body";
      items.forEach(function (item) {
        var data = annotation(item), selection = data.selection || data.Selection;
        var row = document.createElement("div"); row.className = "visual-annotation-list-row";
        row.dataset.pendingVisual = item.pendingVisual ? "true" : "false";
        var badge = document.createElement("span"); badge.className = "visual-annotation-number";
        badge.textContent = String(data.annotationNumber || data.AnnotationNumber);
        var copy = document.createElement("div"); copy.className = "visual-annotation-list-copy";
        var name = document.createElement("strong"); name.textContent = targetLabel(selection);
        var comment = document.createElement("span"); comment.className = "visual-annotation-comment";
        comment.textContent = (item.pendingVisual ? "Не готово · " : "") +
          (data.comment || data.Comment || "Только элемент");
        var targetState = document.createElement("span"); targetState.className = "visual-annotation-target-state";
        targetState.dataset.annotationId = attachmentId(item);
        copy.append(name, comment, targetState); row.append(badge, copy);
        [ ["Показать", function () { show(item); }],
          ["Изменить", function () { edit(item); }],
          ["Удалить", function () {
            if (item.pendingVisual) {
              if (editor && editor.number === Number(data.annotationNumber || data.AnnotationNumber)) closeEditor(false);
              context.setPending(null);
              context.updated();
            } else context.remove(item);
          }] ].forEach(function (action) {
          var button = document.createElement("button"); button.type = "button";
          button.textContent = action[0]; button.addEventListener("click", action[1]); row.appendChild(button);
        });
        list.appendChild(row);
      });
      details.appendChild(list); box.appendChild(details); updateTargetStates();
    }
    function onFrameLoad() { locations = Object.create(null); closeSent(); renderMarkers(); }
    frame.addEventListener("load", onFrameLoad);
    window.addEventListener("resize", renderMarkers);
    function onKeyDown(event) {
      if (event.key === "Escape" && sentPopup) { event.preventDefault(); closeSent(); return; }
      if (event.key !== "Escape" || editor || !browserOpen() ||
          !browser.contains(document.activeElement) ||
          context.button.getAttribute("aria-pressed") !== "true") return;
      event.preventDefault(); inspector.deactivate();
    }
    document.addEventListener("keydown", onKeyDown);
    return {
      onSelection: function (record, reason) {
        if (!browserOpen()) return;
        if (!record) {
          if (editor && (reason === "reset" || reason === "load")) editor.record.generation = inspector.generation();
          if (editor && !editor.busy && (reason === "deactivate" || reason === "stopped")) closeEditor(false);
          return;
        }
        if (editor) return;
        if (pending()) { edit(pendingItem()); return; }
        if (context.drafts().length >= ATTACHMENT_MAX_FILES) {
          context.status("В одном сообщении можно выбрать не более 10 элементов и файлов.");
          return;
        }
        var source = context.source();
        var revision = Number(source && source.revision || 0);
        if (!Number.isSafeInteger(revision) || revision < 1) {
          context.status("Источник выделения недоступен. Обновите просмотр.");
          return;
        }
        var number = context.reserve();
        record.sourceId = source.id;
        record.sourceRevision = revision;
        record.sessionId = context.sessionId ? context.sessionId() : null;
        context.setPending({
          ArtifactId: record.sourceId, ArtifactRevision: record.sourceRevision,
          Number: number, Comment: "", Selection: record.selection, ReplacingDraftId: null
        });
        context.updated();
        openEditor(record, number, pendingItem());
      },
      onLocations: function (next) {
        locations = Object.create(null);
        next.forEach(function (entry) { if (entry && typeof entry.id === "string") locations[entry.id] = entry; });
        renderMarkers(); updateTargetStates();
      },
      onReveal: function (result) {
        if (result && !result.found)
          context.status("Цель аннотации исчезла или определяется неоднозначно.");
      },
      renderDrafts: renderDrafts, visibleDrafts: drafts, sync: sync, showSent: showSent,
      collapseEditor: function () { closeEditor(false); closeSent(); },
      canShow: currentSource,
      dispose: function () { closeEditor(false); closeSent(); inspector.watch([]); layer.remove();
        frame.removeEventListener("load", onFrameLoad);
        window.removeEventListener("resize", renderMarkers);
        document.removeEventListener("keydown", onKeyDown); }
    };
  }
  window.RNAssistantVisualAnnotations = { create: create, annotation: annotation, messageCard: messageCard };
}());
