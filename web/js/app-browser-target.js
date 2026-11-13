(function () {
  "use strict";
  // Runtime projection validation only; locators and labels never grant authority.
  function accept(value, expected, kind) {
    expected = expected || {};
    var caps = value && value.capabilities;
    var kinds = ["HtmlWorkspace", "HtmlArtifact", "HtmlResource", "PluginSource", "PluginRelease", "WorkspaceHtmlProject", "Home"];
    function refuse() { throw new Error("Цель Browser не подтверждена для этого источника. Обновите представление."); }
    if (!value || value.kind !== kind || kinds.indexOf(kind) < 0 || !caps ||
        ["Ready", "Empty", "Unavailable", "Removed"].indexOf(value.state) < 0) refuse();
    ["chatId", "documentId", "workspaceId", "pluginId", "releaseId", "savedVersionId", "artifactId", "resourceUri"].forEach(function (key) {
      if (expected[key] !== undefined && expected[key] !== null && expected[key] !== "" && value[key] !== expected[key]) refuse();
    });
    ["preview", "source", "edit", "versions", "restore", "exportProject", "createProject", "annotations"].forEach(function (key) {
      if (typeof caps[key] !== "boolean") refuse();
    });
    ["saveVersion", "activateVersion", "interact", "media"].forEach(function (key) {
      if (caps[key] !== undefined && typeof caps[key] !== "boolean") refuse();
    });
    if (kind === "Home") {
      if (value.state !== "Empty" || value.displayLocator !== "/" || caps.preview || caps.edit || caps.interact) refuse();
    } else if (kind === "HtmlWorkspace" || kind === "HtmlArtifact" || kind === "HtmlResource") {
      if (typeof value.chatId !== "string" || typeof value.documentId !== "string" || typeof value.resourceUri !== "string" ||
          typeof value.artifactId !== "string" || !Number.isSafeInteger(value.revision) || value.revision < 0 ||
          value.state === "Ready" && (!value.artifactId || value.revision < 1 || !value.resourceUri.startsWith("rna://"))) refuse();
      if (kind !== "HtmlWorkspace" && (caps.edit || caps.restore)) refuse();
      if (kind === "HtmlResource" && (caps.versions || caps.saveVersion || caps.exportProject)) refuse();
    } else {
      if (!value.ownerScope || value.ownerScope.kind !== "Workspace" || !value.workspaceId ||
          value.ownerScope.scopeId !== value.workspaceId || !value.displayLocator || !value.entryPath) refuse();
      if ((kind === "PluginSource" || kind === "PluginRelease") && !value.pluginId) refuse();
      if (kind === "PluginRelease" && (!value.releaseId || !value.savedVersionId || !value.immutable || caps.edit)) refuse();
    }
    return value;
  }
  window.RNAssistantBrowserTarget = { accept: accept };
}());
