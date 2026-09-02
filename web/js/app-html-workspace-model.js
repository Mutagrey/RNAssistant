(function () {
  "use strict";

  function create(state) {
    function prop(source, pascal, camel, fallback) {
      source = source || {};
      return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
    }

    function workspace() {
      var current = state.htmlWorkspace || {};
      current.files = prop(current, "Files", "files", []) || [];
      current.dataSources = prop(current, "DataSources", "dataSources", []) || [];
      current.history = prop(current, "History", "history", []) || [];
      current.redoHistory = prop(current, "RedoHistory", "redoHistory", []) || [];
      current.redoBranches = prop(current, "RedoBranches", "redoBranches", current.redoHistory) || [];
      current.activeFileId = prop(current, "ActiveFileId", "activeFileId", "") || "";
      var recoveryState = prop(current, "Recovery", "recovery", {}) || {};
      recoveryState.status = String(prop(recoveryState, "Status", "status", "empty") || "empty").toLowerCase();
      recoveryState.issue = prop(recoveryState, "Issue", "issue", "") || "";
      recoveryState.message = prop(recoveryState, "Message", "message", "") || "";
      recoveryState.activeArtifactId = prop(recoveryState, "ActiveArtifactId", "activeArtifactId", "") || "";
      recoveryState.problemArtifactId = prop(recoveryState, "ProblemArtifactId", "problemArtifactId", "") || "";
      recoveryState.canMutate = prop(recoveryState, "CanMutate", "canMutate", true) !== false;
      recoveryState.candidates = prop(recoveryState, "Candidates", "candidates", []) || [];
      current.recovery = recoveryState;
      state.htmlWorkspace = current;
      return current;
    }

    function files() {
      return workspace().files;
    }

    function dataSources() {
      return workspace().dataSources;
    }

    function artifactId(artifact) {
      return prop(artifact, "Id", "id", "");
    }

    function artifactKind(artifact) {
      var kind = String(prop(artifact, "DisplayKind", "displayKind", prop(artifact, "Kind", "kind", "file")) || "file").toLowerCase();
      return kind === "plan_document" ? "plan" : kind;
    }

    function artifactTitle(artifact) {
      return prop(artifact, "Title", "title", "Артефакт") || "Артефакт";
    }

    function artifactRevision(artifact) {
      return Number(prop(artifact, "Revision", "revision", 1) || 1);
    }

    function artifactInlineText(artifact) {
      return prop(artifact, "InlineText", "inlineText", "") || "";
    }

    function artifactInlineTruncated(artifact) {
      return prop(artifact, "InlineTruncated", "inlineTruncated", false) === true;
    }

    function setArtifactInlineText(artifact, value) {
      if (!artifact) return;
      if (artifact.inlineText !== undefined || artifact.InlineText === undefined) artifact.inlineText = value || "";
      else artifact.InlineText = value || "";
    }

    function setArtifactInlineProjection(artifact, value) {
      setArtifactInlineText(artifact, value);
      if (!artifact) return;
      if (artifact.inlineTruncated !== undefined || artifact.InlineTruncated === undefined) artifact.inlineTruncated = false;
      else artifact.InlineTruncated = false;
    }

    function artifactById(id) {
      return (state.artifacts || []).filter(function (artifact) { return artifactId(artifact) === id; })[0] || null;
    }

    function planStableId(artifact) {
      try {
        var metadata = JSON.parse(prop(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
        return metadata.planId || metadata.PlanId || artifactId(artifact);
      } catch (error) { return artifactId(artifact); }
    }

    function latestPlanArtifacts() {
      if (typeof artifactResourceHeads === "function") {
        return artifactResourceHeads().filter(function (artifact) { return artifactKind(artifact) === "plan"; });
      }
      var latest = {};
      (state.artifacts || []).forEach(function (artifact) {
        if (artifactKind(artifact) !== "plan") return;
        var id = planStableId(artifact);
        if (!latest[id] || artifactRevision(artifact) > artifactRevision(latest[id])) latest[id] = artifact;
      });
      return Object.keys(latest).map(function (id) { return latest[id]; });
    }

    function historyItems() {
      return workspace().history || [];
    }

    function redoBranches() {
      return workspace().redoBranches || [];
    }

    function recovery() {
      return workspace().recovery;
    }

    function recoveryBlocked() {
      var current = recovery();
      return current.status === "degraded" && !current.canMutate;
    }

    function fileId(file) {
      return prop(file, "Id", "id", prop(file, "Path", "path", ""));
    }

    function filePath(file) {
      return prop(file, "Path", "path", fileId(file));
    }

    function fileKind(file) {
      return (prop(file, "Kind", "kind", "") || "").toLowerCase();
    }

    function fileContent(file) {
      return prop(file, "Content", "content", "") || "";
    }

    function setFileContent(file, value) {
      if (!file) {
        return;
      }
      if (file.content !== undefined || file.Content === undefined) {
        file.content = value || "";
      } else {
        file.Content = value || "";
      }
    }

    function dataId(data) {
      return prop(data, "Id", "id", prop(data, "Name", "name", ""));
    }

    function dataName(data) {
      return prop(data, "Name", "name", dataId(data));
    }

    function dataJson(data) {
      return prop(data, "Json", "json", "{}") || "{}";
    }

    function dataBinding(data) {
      return prop(data, "Binding", "binding", null);
    }

    function bindingValue(binding, pascal, camel, fallback) {
      return prop(binding || {}, pascal, camel, fallback);
    }

    function boundDataSources(refreshPolicy) {
      return dataSources().filter(function (data) {
        var binding = dataBinding(data);
        if (!binding) return false;
        return !refreshPolicy || String(bindingValue(binding, "RefreshPolicy", "refreshPolicy", "manual")).toLowerCase() === String(refreshPolicy).toLowerCase();
      });
    }

    function setDataJson(data, value) {
      if (!data) {
        return;
      }
      if (data.json !== undefined || data.Json === undefined) {
        data.json = value || "{}";
      } else {
        data.Json = value || "{}";
      }
    }

    function selectedItem() {
      var selection = state.htmlWorkspaceSelection || {};
      var id = selection.id || "";
      var result = null;
      if (selection.type === "collection") {
        return id ? { type: "collection", item: { id: id } } : null;
      }
      if (selection.type === "plan" || selection.type === "artifact") {
        var artifact = artifactById(id);
        return artifact ? { type: selection.type, item: artifact } : null;
      }
      if (selection.type === "data") {
        dataSources().forEach(function (item) {
          if (dataId(item) === id) {
            result = { type: "data", item: item };
          }
        });
        return result;
      }

      files().forEach(function (item) {
        if (fileId(item) === id) {
          result = { type: "file", item: item };
        }
      });
      return result;
    }

    function activeHtmlFile() {
      var activeId = workspace().activeFileId || "";
      var active = null;
      files().forEach(function (file) {
        if (!active && fileId(file) === activeId && fileKind(file) === "html") {
          active = file;
        }
      });
      if (active) {
        return active;
      }
      files().forEach(function (file) {
        if (!active && fileKind(file) === "html") {
          active = file;
        }
      });
      return active;
    }

    function refreshLibraryHeadSelection() {
      if (state.htmlWorkspaceDirty) return false;
      var selected = selectedItem();
      if (!selected || (selected.type !== "plan" && selected.type !== "artifact")) return false;
      var visuals = window.RNAssistantArtifactVisuals || null;
      var head = visuals && typeof visuals.libraryHead === "function"
        ? visuals.libraryHead(selected.item)
        : null;
      var headId = prop(head, "ArtifactId", "artifactId", "") || "";
      if (!headId || String(headId).toLowerCase() === String(artifactId(selected.item)).toLowerCase()) return false;
      var headArtifact = artifactById(headId);
      if (!headArtifact) return false;
      state.htmlWorkspaceSelection = {
        type: artifactKind(headArtifact) === "plan" ? "plan" : "artifact",
        id: artifactId(headArtifact)
      };
      return true;
    }

    function ensureSelection() {
      if (selectedItem()) {
        return;
      }

      if (state.activePlanDocumentArtifactId && artifactById(state.activePlanDocumentArtifactId)) {
        state.htmlWorkspaceSelection = { type: "plan", id: state.activePlanDocumentArtifactId };
        return;
      }
      var active = activeHtmlFile();
      if (active) {
        state.htmlWorkspaceSelection = { type: "file", id: fileId(active) };
        return;
      }
      if (files().length) {
        state.htmlWorkspaceSelection = { type: "file", id: fileId(files()[0]) };
        return;
      }
      if (dataSources().length) {
        state.htmlWorkspaceSelection = { type: "data", id: dataId(dataSources()[0]) };
        return;
      }
      if ((state.artifacts || []).length) {
        state.htmlWorkspaceSelection = { type: "artifact", id: artifactId(state.artifacts[0]) };
        return;
      }
      state.htmlWorkspaceSelection = { type: "file", id: "" };
    }

    function snapshotLabel(snapshot) {
      return prop(snapshot || {}, "Label", "label", "HTML workspace snapshot");
    }

    return {
      artifactId: artifactId,
      artifactInlineText: artifactInlineText,
      artifactInlineTruncated: artifactInlineTruncated,
      artifactKind: artifactKind,
      artifactRevision: artifactRevision,
      artifactTitle: artifactTitle,
      bindingValue: bindingValue,
      boundDataSources: boundDataSources,
      dataBinding: dataBinding,
      dataId: dataId,
      dataJson: dataJson,
      dataName: dataName,
      dataSources: dataSources,
      ensureSelection: ensureSelection,
      fileContent: fileContent,
      fileId: fileId,
      fileKind: fileKind,
      filePath: filePath,
      files: files,
      historyItems: historyItems,
      latestPlanArtifacts: latestPlanArtifacts,
      planStableId: planStableId,
      prop: prop,
      recovery: recovery,
      recoveryBlocked: recoveryBlocked,
      refreshLibraryHeadSelection: refreshLibraryHeadSelection,
      redoBranches: redoBranches,
      selectedItem: selectedItem,
      setArtifactInlineText: setArtifactInlineText,
      setArtifactInlineProjection: setArtifactInlineProjection,
      setDataJson: setDataJson,
      setFileContent: setFileContent,
      snapshotLabel: snapshotLabel,
      workspace: workspace
    };
  }

  window.RNAssistantHtmlWorkspaceModel = { create: create };
}());
