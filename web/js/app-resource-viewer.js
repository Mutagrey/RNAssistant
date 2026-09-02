(function () {
  "use strict";

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function bytesFromBase64(content) {
    var binary = window.atob(String(content || ""));
    var bytes = new Uint8Array(binary.length);
    for (var index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    return bytes;
  }

  function fileName(title, fallback) {
    var name = String(title || fallback).split(/[\\/]/).pop().replace(/[<>:"|?*\u0000-\u001f]/g, "_");
    return name || fallback;
  }

  function formatBytes(value) {
    value = Number(value || 0);
    if (value < 1024) return value + " Б";
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " КБ";
    return (value / (1024 * 1024)).toFixed(1) + " МБ";
  }

  function createImage(options) {
    options = options || {};
    var bytes = bytesFromBase64(options.base64Content);
    if (bytes.byteLength !== Number(options.byteLength || 0)) throw new Error("Image byte length is inconsistent.");
    var objectUrl = URL.createObjectURL(new Blob([bytes], { type: options.mimeType }));
    var root = element("div", "rn-image-viewer");
    var toolbar = element("div", "rn-resource-viewer-toolbar");
    var dimensions = element("span", "rn-resource-viewer-status", formatBytes(options.byteLength));
    var fit = element("button", "secondary compact active", "Вписать");
    var actual = element("button", "secondary compact", "100%");
    var zoomOut = element("button", "secondary compact", "−");
    var zoomIn = element("button", "secondary compact", "+");
    var download = element("button", "secondary compact", "Скачать");
    [fit, actual, zoomOut, zoomIn, download].forEach(function (button) { button.type = "button"; });
    var stage = element("div", "rn-image-viewer-stage is-fit");
    var image = element("img", "rn-image-viewer-image");
    image.alt = String(options.title || "Image");
    image.src = objectUrl;
    var naturalWidth = 0;
    var naturalHeight = 0;
    var scale = 1;

    function applyScale(nextScale) {
      if (!naturalWidth) return;
      scale = Math.max(0.1, Math.min(8, Number(nextScale || 1)));
      stage.classList.remove("is-fit");
      fit.classList.remove("active");
      actual.classList.toggle("active", scale === 1);
      image.style.width = Math.round(naturalWidth * scale) + "px";
      dimensions.textContent = naturalWidth + " × " + naturalHeight + " px · " + Math.round(scale * 100) + "% · " + formatBytes(options.byteLength);
    }

    image.addEventListener("load", function () {
      naturalWidth = image.naturalWidth || 0;
      naturalHeight = image.naturalHeight || 0;
      dimensions.textContent = (naturalWidth && naturalHeight ? naturalWidth + " × " + naturalHeight + " px · " : "") + formatBytes(options.byteLength);
    });
    fit.addEventListener("click", function () {
      stage.classList.add("is-fit");
      fit.classList.add("active");
      actual.classList.remove("active");
      image.style.width = "";
      dimensions.textContent = (naturalWidth && naturalHeight ? naturalWidth + " × " + naturalHeight + " px · " : "") + formatBytes(options.byteLength);
    });
    actual.addEventListener("click", function () { applyScale(1); });
    zoomOut.addEventListener("click", function () { applyScale(scale / 1.25); });
    zoomIn.addEventListener("click", function () { applyScale(scale * 1.25); });
    download.addEventListener("click", function () {
      var link = document.createElement("a");
      link.href = objectUrl;
      link.download = fileName(options.title, "image");
      document.body.appendChild(link);
      link.click();
      link.remove();
    });
    toolbar.appendChild(dimensions);
    toolbar.appendChild(fit);
    toolbar.appendChild(actual);
    toolbar.appendChild(zoomOut);
    toolbar.appendChild(zoomIn);
    toolbar.appendChild(download);
    stage.appendChild(image);
    root.appendChild(toolbar);
    root.appendChild(stage);

    var released = false;
    function release() {
      if (released) return;
      released = true;
      URL.revokeObjectURL(objectUrl);
    }
    window.addEventListener("beforeunload", release);
    return {
      element: root,
      destroy: function () {
        window.removeEventListener("beforeunload", release);
        release();
        root.replaceChildren();
      }
    };
  }

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function createTaskList(options) {
    options = options || {};
    var root = element("section", "rn-task-list-viewer");
    var taskList;
    try { taskList = JSON.parse(String(options.text || "")); } catch (error) { taskList = null; }
    var steps = value(taskList, "Steps", "steps", null);
    if (!taskList || !Array.isArray(steps) || steps.length > 32) {
      root.appendChild(element("div", "artifact-detail-error", "Task list preview недоступен: payload некорректен."));
      return { element: root, destroy: function () { root.replaceChildren(); } };
    }
    var goal = element("h2", "rn-task-list-goal", value(taskList, "Goal", "goal", "Task list") || "Task list");
    var status = String(value(taskList, "Status", "status", "active") || "active").toLowerCase();
    var statusLabels = { active: "В работе", completed: "Завершён", cancelled: "Отменён", blocked: "Заблокирован" };
    var completed = steps.filter(function (step) {
      return String(value(step, "Status", "status", "pending")).toLowerCase() === "completed";
    }).length;
    var summary = element("div", "rn-task-list-summary");
    summary.appendChild(element("span", "rn-task-list-status status-" + status, statusLabels[status] || status));
    summary.appendChild(element("span", "rn-resource-viewer-status", completed + " из " + steps.length));
    var progress = element("progress", "rn-task-list-progress");
    progress.max = Math.max(1, steps.length);
    progress.value = completed;
    var list = element("ol", "rn-task-list-steps");
    var marks = { completed: "✓", in_progress: "•", blocked: "!", cancelled: "–", pending: "" };
    steps.forEach(function (step) {
      var stepStatus = String(value(step, "Status", "status", "pending") || "pending").toLowerCase();
      var row = element("li", "rn-task-list-step status-" + stepStatus);
      var mark = element("span", "rn-task-list-step-mark", marks[stepStatus] || "");
      mark.setAttribute("aria-hidden", "true");
      row.appendChild(mark);
      row.appendChild(element("span", "rn-task-list-step-text", value(step, "Text", "text", value(step, "Id", "id", "Шаг"))));
      list.appendChild(row);
    });
    root.appendChild(goal);
    root.appendChild(summary);
    root.appendChild(progress);
    root.appendChild(list);
    return { element: root, destroy: function () { root.replaceChildren(); } };
  }

  if (!window.RNAssistantViewerRegistry) throw new Error("Viewer registry is unavailable.");
  window.RNAssistantViewerRegistry.register("image", createImage);
  window.RNAssistantViewerRegistry.register("task_list", createTaskList);
  window.RNAssistantArtifactResourceViewers = { createImage: createImage, createTaskList: createTaskList };
}());
