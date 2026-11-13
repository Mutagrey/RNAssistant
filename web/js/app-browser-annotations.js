(function () {
  "use strict";
  function create(options) {
    var current = null, bound = 0, sequence = 0, busy = false, disposed = false, annotations;
    var button = document.createElement("button"), status = document.createElement("span");
    button.type = "button"; button.dataset.browserControl = "true"; button.disabled = true;
    status.className = "preview-surface-status"; status.setAttribute("role", "status");
    options.surface.setAnnotation(button); options.root.appendChild(status);
    function address() {
      var owner = current && current.ownerScope;
      return owner && owner.chatId ? owner.kind === "Document" ? { source: "office", sessionId: owner.chatId } :
        { source: "workspace", workspaceId: owner.scopeId, sessionId: owner.chatId } : null;
    }
    function panel() { var target = address(); return target && RNAssistantConversationPanel.find(target); }
    function source() { var value = current && current.annotationContext; return value ? { id: value.sourceId, revision: value.sourceRevision } : null; }
    function draft() { var target = address(); return target ? target.source === "office" ? RNAssistantChatDrafts.readOffice(target.sessionId) : RNAssistantChatDrafts.readWorkspace(target) : null; }
    function write(changes) {
      var target = address(), value = Object.assign({}, draft() || { text: "", attachments: [], nextAnnotationNumber: 1 }, changes);
      if (!target) return;
      if (target.source === "office") RNAssistantChatDrafts.saveOffice(target.sessionId, value.text, value.attachments, true, value.nextAnnotationNumber, value.pendingVisualAnnotation);
      else RNAssistantChatDrafts.updateWorkspace(target, changes);
    }
    function updated() { var view = panel(); if (view) view.controls(); if (annotations) annotations.sync(); options.layout(); }
    function notify(message) { if (!disposed) status.textContent = message; }
    function enabled() { var view = panel(); return !disposed && options.isCurrent() && current && current.status === "Ready" &&
      current.target.capabilities.annotations && !!address() && !(view && view.snapshot().chatReadOnly); }
    var inspector = RNAssistantVisualInspector.createHost({ frame: options.viewport, button: button, status: status,
      enabled: enabled, send: function (message) { return options.command("VisualControl", { visual: message }); },
      onSelectionChanged: function (selected, reason) { if (annotations) annotations.onSelection(selected ? inspector.selectionRecord() : null, reason); options.layout(); },
      onLocations: function (locations) { if (annotations) annotations.onLocations(locations); },
      onReveal: function (result) { if (annotations) annotations.onReveal(result); }
    });
    async function discard(target, id) { return options.send("discardChatResourceDraft", { chatId: target.sessionId, workspaceId: target.workspaceId, id: id }); }
    async function stage(record, comment, number, replacing) {
      var target = address(), origin = source(), binding = current && current.annotationContext, page = current;
      if (busy || !enabled() || !record || record.pageSession !== current.sessionId || record.generation !== current.documentGeneration || record.sourceId !== origin.id || record.sourceRevision !== origin.revision ||
          RNAssistantConversationInput.isPending(target.source, target)) throw new Error("Источник выделения изменился или чат занят. Выберите элемент заново.");
      var view = panel(); if (view) view.captureDraft(true);
      var attachment = null, attached = false; busy = true;
      try {
        var response = binding.pluginInstanceId ? await options.send("stagePluginVisualSelection", { workspaceId: target.workspaceId, sessionId: target.sessionId,
          instanceId: binding.pluginInstanceId, previewGeneration: record.generation, selection: record.selection, comment: String(comment || ""), annotationNumber: number }) :
          await options.send("stageHtmlVisualSelection", { chatId: target.sessionId, artifactId: origin.id, artifactRevision: origin.revision,
            previewGeneration: record.generation, selection: record.selection, comment: String(comment || ""), annotationNumber: number });
        attachment = binding.pluginInstanceId ? response : response && (response.resource || response.Resource);
        if (!attachment || !attachmentId(attachment)) throw new Error("Аннотация не подготовлена.");
        if (!enabled() || current.sessionId !== page.sessionId || current.documentGeneration !== page.documentGeneration || JSON.stringify(address()) !== JSON.stringify(target))
          throw new Error("Страница изменилась. Выберите элемент заново.");
        attachment.visualAnnotation = { selection: record.selection, comment: String(comment || ""), annotationNumber: number };
        attachment.visualSourceArtifactId = origin.id; attachment.visualSourceRevision = origin.revision;
        var value = draft(), items = (value && value.attachments || []).slice(), at = replacing ? items.findIndex(function (item) { return attachmentId(item) === attachmentId(replacing); }) : -1;
        if (replacing && at < 0 || !replacing && items.length >= ATTACHMENT_MAX_FILES) throw new Error("Список вложений изменился или заполнен.");
        if (replacing) items[at] = attachment; else items.push(attachment);
        write({ attachments: items, pendingVisualAnnotation: null }); attached = true;
        await (target.source === "office" ? RNAssistantChatDrafts.flushOffice(target.sessionId) : RNAssistantChatDrafts.flushWorkspace(target));
        if (replacing) await discard(target, attachmentId(replacing)).catch(function () { notify("Новая аннотация сохранена; прежний снимок не удалён."); });
        updated(); return attachment;
      } finally { busy = false; if (attachment && !attached) await discard(target, attachmentId(attachment)).catch(function () {}); }
    }
    annotations = RNAssistantVisualAnnotations.create({ frame: options.viewport, inspector: inspector, stage: stage, context: {
      mount: options.root, browser: options.root, button: button, statusNode: status, source: source,
      get draftBox() { var view = panel(); return view && view.node("attachmentDrafts"); },
      isOpen: function () { return enabled(); }, pageSession: function () { return current && current.sessionId; }, sessionId: function () { var target = address(); return target && target.sessionId; },
      pending: function () { return draft() && draft().pendingVisualAnnotation; }, setPending: function (value) { write({ pendingVisualAnnotation: value }); },
      drafts: function () { return draft() && draft().attachments || []; }, messages: function () { var view = panel(); return view ? view.snapshot().messages || [] : []; },
      updated: updated, status: notify,
      remove: async function (item) { var target = address(); await discard(target, attachmentId(item)); write({ attachments: (draft().attachments || []).filter(function (value) { return attachmentId(value) !== attachmentId(item); }) }); updated(); },
      reserve: function () { var target = address(); return target.source === "office" ? RNAssistantChatDrafts.reserveOfficeAnnotationNumber(target.sessionId) : RNAssistantChatDrafts.reserveWorkspaceAnnotationNumber(target); },
      openSource: function () { options.focus(); return enabled(); }
    } });
    return { annotations: annotations, address: address, busy: function () { return busy; },
      update: function (value) {
        current = value;
        if (!enabled()) { inspector.deactivate(); inspector.refresh(); return; }
        if (bound !== value.documentGeneration) { bound = value.documentGeneration; sequence = 0; inspector.bind(bound); }
        if (value.visual && value.visual.generation === bound && value.visual.sequence > sequence) { sequence = value.visual.sequence; inspector.receive(value.visual); }
        inspector.refresh(); annotations.sync();
      },
      deactivate: function () { inspector.deactivate(); annotations.collapseEditor(); },
      dispose: function () { disposed = true; annotations.dispose(); inspector.dispose(); status.remove(); }
    };
  }
  window.RNAssistantBrowserAnnotations = { create: create };
}());
