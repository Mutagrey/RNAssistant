(function () {
  "use strict";

  function annotation(item) { return item && (item.visualAnnotation || item.VisualAnnotation); }
  function value(item, camel, pascal) { return item && (item[camel] !== undefined ? item[camel] : item[pascal]); }
  function targetLabel(selection) {
    return selection.tag + (selection.stableUiId ? " · " + selection.stableUiId : "") +
      (selection.textExcerpt ? " · " + selection.textExcerpt.slice(0, 55) : "");
  }
  function create(options) {
    var frame = options.frame, inspector = options.inspector;
    var mount = document.getElementById("chatBrowserFrameMount");
    var browser = document.getElementById("chatBrowser");
    var layer = document.createElement("div"), editor = null, locations = Object.create(null);
    layer.className = "visual-annotation-layer";
    mount.appendChild(layer);

    function browserOpen() { return browser.contains(frame) && browser.getAttribute("aria-hidden") === "false"; }
    function currentSource(item) {
      var sourceId = value(item, "visualSourceArtifactId", "VisualSourceArtifactId");
      var revision = Number(value(item, "visualSourceRevision", "VisualSourceRevision"));
      var matches = (state.artifacts || []).filter(function (artifact) {
        return (artifact.Id || artifact.id) === state.activeHtmlArtifactId &&
          (artifact.Kind || artifact.kind) === "html_workspace";
      });
      return matches.length === 1 && sourceId === state.activeHtmlArtifactId &&
        revision === Number(matches[0].Revision || matches[0].revision || 0);
    }
    function drafts() { return (state.draftAttachments || []).filter(function (item) { return !!annotation(item); }); }
    function point(rect) {
      if (!rect) return null;
      var host = mount.getBoundingClientRect(), target = frame.getBoundingClientRect();
      return { x: target.left - host.left + rect.x, y: target.top - host.top + rect.y,
        width: rect.width, height: rect.height };
    }
    function placeEditor() {
      if (!editor) return;
      var rect = editor.item ? (locations[attachmentId(editor.item)] || {}).rect : editor.record.selection.rect;
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
    function closeEditor() {
      if (!editor) return;
      editor.root.remove(); editor = null; frame.classList.remove("annotation-editing");
    }
    async function finish() {
      if (!editor || editor.busy) return;
      var editing = editor;
      editing.busy = true;
      editing.done.disabled = true;
      editing.cancel.disabled = true;
      try {
        var attachment = await options.stage(editing.record, editing.textarea.value,
          editing.number, editing.item, true);
        if (!attachment) throw new Error("Аннотация не была добавлена.");
        if (editor === editing) closeEditor();
        if (typeof renderAttachmentDrafts === "function") renderAttachmentDrafts();
      } catch (error) {
        var latest = drafts().find(function (item) {
          var data = annotation(item);
          return data && Number(data.annotationNumber || data.AnnotationNumber) === editing.number;
        });
        if (latest) editing.item = latest;
        editing.busy = false;
        editing.done.disabled = false;
        editing.cancel.disabled = false;
        document.getElementById("htmlWorkspaceInspectStatus").textContent =
          error.detail || error.message || "Не удалось сохранить аннотацию.";
      }
    }
    function openEditor(record, number, item) {
      if (!browserOpen() || !record) return;
      closeEditor();
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
      cancel.addEventListener("click", closeEditor);
      done.addEventListener("click", finish);
      textarea.addEventListener("keydown", function (event) {
        if (event.key === "Escape") { event.preventDefault(); finish(); }
        if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); finish(); }
      });
      placeEditor(); textarea.focus();
    }
    function edit(item) {
      if (!currentSource(item)) {
        setAttachmentStatus(state.activeChatId, "Источник аннотации изменился. Удалите её и выберите элемент заново.", "warning");
        return;
      }
      window.RNAssistantChatBrowser.openCurrent();
      var data = annotation(item), selection = data.selection || data.Selection;
      openEditor({ generation: inspector.generation(), selection: selection,
        sourceId: value(item, "visualSourceArtifactId", "VisualSourceArtifactId"),
        sourceRevision: Number(value(item, "visualSourceRevision", "VisualSourceRevision")) },
        Number(data.annotationNumber || data.AnnotationNumber), item);
      inspector.reveal(attachmentId(item));
    }
    function show(item) {
      if (!currentSource(item)) {
        setAttachmentStatus(state.activeChatId, "Источник аннотации изменился. Удалите её и выберите элемент заново.", "warning");
        return;
      }
      window.RNAssistantChatBrowser.openCurrent();
      inspector.reveal(attachmentId(item));
    }
    function renderMarkers() {
      layer.querySelectorAll(".visual-annotation-marker").forEach(function (node) { node.remove(); });
      if (!browserOpen()) return;
      var overlaps = Object.create(null);
      drafts().filter(currentSource).forEach(function (item) {
        var position = point((locations[attachmentId(item)] || {}).rect);
        if (!position) return;
        var data = annotation(item), number = Number(data.annotationNumber || data.AnnotationNumber);
        var marker = document.createElement("button"); marker.type = "button";
        marker.className = "visual-annotation-marker"; marker.textContent = String(number);
        marker.setAttribute("aria-label", "Изменить аннотацию " + number);
        var key = Math.round(position.x / 16) + ":" + Math.round(position.y / 16);
        var offset = overlaps[key] || 0; overlaps[key] = offset + 1;
        marker.style.left = Math.max(8, Math.min(mount.clientWidth - 30, position.x + position.width - 13 + offset * 25)) + "px";
        marker.style.top = Math.max(55, Math.min(mount.clientHeight - 30, position.y - 11)) + "px";
        marker.addEventListener("click", function () { edit(item); });
        layer.appendChild(marker);
      });
      placeEditor();
    }
    function updateTargetStates() {
      document.querySelectorAll(".visual-annotation-target-state").forEach(function (node) {
        var location = locations[node.dataset.annotationId];
        node.textContent = location && location.status === "missing" ? "Цель не найдена" :
          location && location.status === "outside" ? "Вне экрана" : "";
        node.hidden = !node.textContent;
      });
    }
    function sync() {
      var watched = drafts().filter(currentSource).map(function (item) {
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
        var badge = document.createElement("span"); badge.className = "visual-annotation-number";
        badge.textContent = String(data.annotationNumber || data.AnnotationNumber);
        var copy = document.createElement("div"); copy.className = "visual-annotation-list-copy";
        var name = document.createElement("strong"); name.textContent = targetLabel(selection);
        var comment = document.createElement("span"); comment.textContent = data.comment || data.Comment || "Только элемент";
        var targetState = document.createElement("span"); targetState.className = "visual-annotation-target-state";
        targetState.dataset.annotationId = attachmentId(item);
        copy.append(name, comment, targetState); row.append(badge, copy);
        [ ["Показать", function () { show(item); }],
          ["Изменить", function () { edit(item); }],
          ["Удалить", function () { removeDraftAttachment(item); }] ].forEach(function (action) {
          var button = document.createElement("button"); button.type = "button";
          button.textContent = action[0]; button.addEventListener("click", action[1]); row.appendChild(button);
        });
        list.appendChild(row);
      });
      details.appendChild(list); box.appendChild(details); updateTargetStates();
    }
    frame.addEventListener("load", function () { locations = Object.create(null); renderMarkers(); });
    window.addEventListener("resize", renderMarkers);
    return {
      onSelection: function (record, reason) {
        if (!browserOpen()) return;
        if (!record) {
          if (editor && (reason === "reset" || reason === "load")) editor.record.generation = inspector.generation();
          if (editor && !editor.busy && (reason === "deactivate" || reason === "stopped")) finish();
          return;
        }
        if (editor && editor.textarea.value) return;
        var number = window.RNAssistantChatDrafts.reserveOfficeAnnotationNumber(state.activeChatId);
        var source = (state.artifacts || []).find(function (item) {
          return (item.Id || item.id) === state.activeHtmlArtifactId;
        });
        record.sourceId = state.activeHtmlArtifactId;
        record.sourceRevision = Number(source && (source.Revision || source.revision) || 0);
        openEditor(record, number, null);
      },
      onLocations: function (next) {
        locations = Object.create(null);
        next.forEach(function (entry) { if (entry && typeof entry.id === "string") locations[entry.id] = entry; });
        renderMarkers(); updateTargetStates();
      },
      onReveal: function (result) {
        if (result && !result.found)
          setAttachmentStatus(state.activeChatId, "Цель аннотации исчезла или определяется неоднозначно.", "warning");
      },
      renderDrafts: renderDrafts, sync: sync,
      closeEditor: closeEditor
    };
  }
  window.RNAssistantVisualAnnotations = { create: create, annotation: annotation };
}());
