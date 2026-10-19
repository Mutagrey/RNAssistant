(function () {
  "use strict";
  // Preview-only File System Access fixture. The application still owns all validation and writes.
  function missing() { return new DOMException("Файл отсутствует", "NotFoundError"); }
  function MemoryFile(name, body) { this.kind = "file"; this.name = name; this.body = new File([body], name); }
  MemoryFile.prototype.getFile = async function () { return this.body; };
  MemoryFile.prototype.createWritable = async function () {
    var handle = this, parts = [];
    return { write: async function (part) { parts.push(part); }, close: async function () { handle.body = new File(parts, handle.name); }, abort: async function () {} };
  };
  function MemoryDirectory(name) { this.kind = "directory"; this.name = name; this.children = new Map(); }
  MemoryDirectory.prototype.getFileHandle = async function (name, options) {
    var item = this.children.get(name);
    if (!item && options && options.create) { item = new MemoryFile(name, ""); this.children.set(name, item); }
    if (!item || item.kind !== "file") throw missing();
    return item;
  };
  MemoryDirectory.prototype.getDirectoryHandle = async function (name, options) {
    var item = this.children.get(name);
    if (!item && options && options.create) { item = new MemoryDirectory(name); this.children.set(name, item); }
    if (!item || item.kind !== "directory") throw missing();
    return item;
  };
  MemoryDirectory.prototype.removeEntry = async function (name) { if (!this.children.delete(name)) throw missing(); };
  MemoryDirectory.prototype.entries = async function* () { yield* this.children.entries(); };
  MemoryDirectory.prototype.put = function (name, body) { this.children.set(name, new MemoryFile(name, body)); };

  var folder = new MemoryDirectory("Управленческая отчётность");
  var projectId = "tp_" + "a".repeat(32);
  folder.put("project.json", JSON.stringify({ kind: "data-templates", formatVersion: 2,
    projectId: projectId, name: "Управленческая отчётность",
    sources: [{ sourceId: "src_" + "b".repeat(32), path: "matrix-source.xlsx" }] }, null, 2));
  window.showDirectoryPicker = async function () { return folder; };
  window.RNPreviewDataProject = folder;

  async function waitFor(selector, timeout) {
    var started = Date.now();
    while (Date.now() - started < timeout) {
      var found = document.querySelector(selector);
      if (found && !found.disabled) return found;
      await new Promise(function (resolve) { setTimeout(resolve, 50); });
    }
    throw new Error("Экран примера Data Templates не загрузился: " + selector);
  }
  async function seedAndOpen() {
    var source = await (await fetch("core/examples/matrix-xlsx-extraction.json")).text();
    var parsed = JSON.parse(source), timestamp = "1730000000000";
    var dictionary = await folder.getDirectoryHandle("dictionaries", { create: true });
    dictionary.put("dictionary-" + timestamp + "-" + "c".repeat(32) + ".json", JSON.stringify(parsed.dictionaries, null, 2) + "\n");
    var templates = await folder.getDirectoryHandle("templates", { create: true });
    templates.put("template-" + parsed.template.id + "-" + timestamp + "-" + "d".repeat(32) + ".json",
      JSON.stringify({ kind: "data-templates-project-template", formatVersion: 1, projectId: projectId,
        template: parsed.template, source: parsed.source, dataset: parsed.dataset }, null, 2) + "\n");
    var xlsx = await (await fetch("core/examples/matrix-source.xlsx")).arrayBuffer();
    folder.put("matrix-source.xlsx", xlsx);
    (await waitFor("#dtProjectOpen", 5000)).click();
    var input = await waitFor("#dtFiles", 5000), transfer = new DataTransfer();
    transfer.items.add(new File([xlsx], "matrix-source.xlsx", { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }));
    input.files = transfer.files; input.dispatchEvent(new Event("change", { bubbles: true }));
    (await waitFor('#dtLibraryList button[data-action="open"]', 5000)).click();
    await waitFor('#dtTemplateInspection:not([hidden])', 5000);
    await waitFor('#dtTabTemplates[aria-selected="true"]', 5000);
    document.documentElement.dataset.previewReady = "true";
    window.dispatchEvent(new Event("rna.preview.dataTemplatesReady"));
  }
  seedAndOpen().catch(function (error) {
    var status = document.querySelector("#dtIntakeStatus");
    if (status) status.textContent = "Не удалось загрузить проект примера: " + error.message;
  });
}());
