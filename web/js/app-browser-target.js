(function () {
  "use strict";
  // Validate the addressed runtime projection; never derive permission from route labels/MIME.
  function accept(value, expected, kind) {
    var capabilities = value && value.capabilities;
    if (!value || value.kind !== kind || ["Ready", "Empty", "Unavailable"].indexOf(value.state) < 0 ||
        value.chatId !== expected.chatId || typeof value.documentId !== "string" ||
        expected.documentId && value.documentId !== expected.documentId ||
        typeof value.resourceUri !== "string" || expected.resourceUri && value.resourceUri !== expected.resourceUri ||
        typeof value.artifactId !== "string" || expected.artifactId !== undefined && value.artifactId !== expected.artifactId ||
        !Number.isSafeInteger(value.revision) || value.revision < 0 || !capabilities ||
        value.state === "Ready" && (!value.artifactId || value.revision < 1 || !value.resourceUri.startsWith("rna://")) ||
        ["preview", "source", "edit", "versions", "restore", "exportProject", "createProject", "annotations"].some(function (name) { return typeof capabilities[name] !== "boolean"; }) ||
        kind !== "HtmlWorkspace" && (value.state !== "Ready" || capabilities.edit || capabilities.restore || capabilities.annotations) ||
        kind === "HtmlResource" && (capabilities.preview || capabilities.versions || capabilities.createProject || capabilities.exportProject))
      throw new Error("Цель Browser не подтверждена для этого источника. Обновите представление.");
    return value;
  }
  window.RNAssistantBrowserTarget = { accept: accept };
}());
