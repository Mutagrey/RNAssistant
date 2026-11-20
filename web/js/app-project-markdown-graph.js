(function () {
  "use strict";

  function open(workspaceId, name) {
    var content = document.createElement("div");
    content.className = "rna-md-project-content";
    var form = document.createElement("form");
    form.className = "rna-md-project-controls";
    var label = document.createElement("label");
    label.className = "rna-dialog-field";
    label.textContent = "Папка внутри проекта";
    var input = document.createElement("input");
    input.className = "rna-form-control compact";
    input.placeholder = "Корень проекта";
    input.maxLength = 1000;
    input.autocomplete = "off";
    label.appendChild(input);
    var scan = document.createElement("button");
    scan.type = "submit";
    scan.className = "button compact primary";
    scan.textContent = "Сканировать";
    var host = document.createElement("div");
    host.className = "rna-md-project-graph";
    form.append(label, scan);
    content.append(form, host);
    var graph = null, presentation = { graphScope: "all" };

    function scanFolder() {
      var folder = input.value.trim().replace(/\\/g, "/");
      if (folder && (folder.includes(":") || folder.split("/").some(function (part) { return !part || part === "." || part === ".."; }))) {
        input.setAttribute("aria-invalid", "true");
        dialog.setStatus("Введите путь к папке внутри проекта.", true);
        return;
      }
      input.removeAttribute("aria-invalid");
      if (graph) graph.destroy();
      graph = window.RNAssistantMarkdownGraph.mount(host, {
        presentation: presentation,
        scopeLabel: folder || "Корень проекта",
        onStatus: dialog.setStatus,
        onGraphData: function (fresh, signal) {
          var request = send("readProjectMarkdownGraph", { workspaceId: workspaceId, directory: folder });
          function cancel() {
            if (request.requestId) cancelBridgeRequest(request.requestId).catch(function () {});
          }
          signal.addEventListener("abort", cancel, { once: true });
          return request.then(function (value) {
            if (!value || value.scope !== "workspace") throw new Error("Область проекта не подтверждена.");
            return value;
          }).finally(function () { signal.removeEventListener("abort", cancel); });
        }
      });
    }
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      scanFolder();
    });
    var dialog = RNAssistantDialog.open({ title: "Граф Markdown · " + name, className: "rna-md-project-dialog", content: content,
      onClose: function () { if (graph) graph.destroy(); } });
    scanFolder();
  }

  window.RNAssistantProjectMarkdownGraph = Object.freeze({ open: open });
}());
