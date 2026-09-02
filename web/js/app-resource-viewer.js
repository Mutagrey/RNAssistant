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
    if (typeof window.Viewer !== "function") throw new Error("Viewer.js is unavailable.");
    var bytes = bytesFromBase64(options.base64Content);
    if (bytes.byteLength !== Number(options.byteLength || 0)) throw new Error("Image byte length is inconsistent.");
    var objectUrl = URL.createObjectURL(new Blob([bytes], { type: options.mimeType }));
    var root = element("div", "rn-image-viewer");
    if (options.documentPage) root.classList.add("is-document-page");
    var toolbar = element("div", "rn-resource-viewer-toolbar");
    var dimensions = element("span", "rn-resource-viewer-status", formatBytes(options.byteLength));
    var download = element("button", "secondary compact", options.downloadLabel || "Скачать");
    download.type = "button";
    download.title = options.downloadLabel || "Скачать";
    var stageShell = element("div", "rn-image-viewer-stage-shell");
    if (options.documentPage) stageShell.classList.add("is-document-page");
    stageShell.tabIndex = 0;
    stageShell.setAttribute("role", "group");
    stageShell.setAttribute("aria-label", options.documentPage ? "Страница документа" : "Просмотр изображения");
    var image = element("img", "rn-viewerjs-source");
    image.alt = String(options.title || "Image");
    var naturalWidth = 0;
    var naturalHeight = 0;
    var vendorViewer = null;
    var sequenceController = null;
    var resizeObserver = null;
    var resizeFrame = 0;
    var resizeTimer = 0;
    var released = false;

    function updateDimensions(label) {
      dimensions.textContent = (naturalWidth && naturalHeight
        ? naturalWidth + " × " + naturalHeight + " px · "
        : "") + (label ? label + " · " : "") + formatBytes(options.byteLength);
    }

    function localizeViewerControls() {
      var labels = {
        "viewer-zoom-in": "Увеличить",
        "viewer-zoom-out": "Уменьшить",
        "viewer-one-to-one": "Размер 100%",
        "viewer-reset": "Вписать",
        "viewer-rotate-left": "Повернуть влево",
        "viewer-rotate-right": "Повернуть вправо"
      };
      Object.keys(labels).forEach(function (className) {
        var control = stageShell.querySelector("." + className);
        if (!control) return;
        control.title = labels[className];
        control.setAttribute("aria-label", labels[className]);
      });
    }

    function fittedRatio() {
      if (!naturalWidth || !naturalHeight) return 1;
      var width = Number(stageShell.clientWidth || 0);
      var height = Number(stageShell.clientHeight || 0);
      if (width <= 0 || height <= 0) return 1;
      return Math.max(0.05, Math.min(8, Math.min(width / naturalWidth, height / naturalHeight)));
    }

    function fitViewer() {
      if (!vendorViewer) return;
      vendorViewer.reset();
      vendorViewer.zoomTo(fittedRatio(), false);
    }

    function showActualSize() {
      if (!vendorViewer) return;
      vendorViewer.reset();
      vendorViewer.zoomTo(1, true);
    }

    function scheduleViewerResize() {
      if (released || resizeFrame) return;
      var schedule = typeof window.requestAnimationFrame === "function"
        ? window.requestAnimationFrame.bind(window)
        : function (callback) { return window.setTimeout(callback, 0); };
      resizeFrame = schedule(function () {
        resizeFrame = 0;
        if (released || !vendorViewer) return;
        if (typeof vendorViewer.resize === "function") vendorViewer.resize();
        fitViewer();
      });
    }

    function ensureViewer() {
      if (released || vendorViewer) return;
      vendorViewer = new window.Viewer(image, {
        inline: true,
        backdrop: false,
        button: false,
        navbar: false,
        title: false,
        toolbar: {
          zoomIn: "large",
          zoomOut: "large",
          oneToOne: { show: true, size: "large", click: showActualSize },
          reset: { show: true, size: "large", click: fitViewer },
          rotateLeft: true,
          rotateRight: true
        },
        className: "rn-vendor-image-viewer",
        initialCoverage: 1,
        keyboard: true,
        focus: false,
        loop: false,
        minWidth: 100,
        minHeight: 100,
        minZoomRatio: 0.05,
        maxZoomRatio: 8,
        movable: true,
        rotatable: true,
        scalable: false,
        slideOnTouch: false,
        toggleOnDblclick: false,
        tooltip: true,
        transition: true,
        zoomOnTouch: true,
        zoomOnWheel: true,
        zoomRatio: 0.15,
        ready: function () {
          localizeViewerControls();
          if (typeof window.ResizeObserver === "function") {
            resizeObserver = new window.ResizeObserver(scheduleViewerResize);
            resizeObserver.observe(stageShell);
          }
          scheduleViewerResize();
          resizeTimer = window.setTimeout(function () {
            resizeTimer = 0;
            scheduleViewerResize();
          }, 50);
        },
        viewed: function () {
          var data = this.viewer && this.viewer.imageData;
          updateDimensions(data && data.ratio ? Math.round(data.ratio * 100) + "%" : "Вписано");
        },
        zoomed: function (event) {
          var ratio = event && event.detail ? Number(event.detail.ratio || 0) : 0;
          if (ratio > 0) updateDimensions(Math.round(ratio * 100) + "%");
        }
      });
    }

    image.addEventListener("load", function () {
      naturalWidth = image.naturalWidth || 0;
      naturalHeight = image.naturalHeight || 0;
      updateDimensions("Вписано");
      ensureViewer();
    });
    stageShell.addEventListener("dblclick", function (event) {
      if (!vendorViewer) return;
      var ratio = vendorViewer.imageData ? Number(vendorViewer.imageData.ratio || 0) : 0;
      if (Math.abs(ratio - 1) < 0.01) fitViewer();
      else showActualSize();
      if (event && typeof event.preventDefault === "function") event.preventDefault();
      if (event && typeof event.stopPropagation === "function") event.stopPropagation();
    });
    download.addEventListener("click", function () {
      var link = document.createElement("a");
      link.href = objectUrl;
      link.download = fileName(options.title, "image");
      document.body.appendChild(link);
      link.click();
      link.remove();
    });
    toolbar.appendChild(dimensions);
    toolbar.appendChild(download);
    stageShell.appendChild(image);

    var navigation = options.navigation || null;
    function runNavigation(action, label) {
      if (typeof action !== "function") return;
      var previousLabel = label.textContent;
      label.textContent = "Загружаю…";
      Promise.resolve(action()).then(function (changed) {
        if (changed === false) label.textContent = navigation.unavailableLabel || "Элемент недоступен";
        else label.textContent = previousLabel;
      }).catch(function () { label.textContent = navigation.unavailableLabel || "Элемент недоступен"; });
    }
    if (navigation) {
      root.classList.add("has-navigation");
      var previous = element("button", "rn-preview-nav rn-preview-nav-previous", "‹");
      var next = element("button", "rn-preview-nav rn-preview-nav-next", "›");
      var pageLabel = element("span", "rn-preview-page-label", String(navigation.label || ""));
      previous.type = next.type = "button";
      previous.disabled = navigation.hasPrevious !== true;
      next.disabled = navigation.hasNext !== true;
      previous.title = navigation.previousLabel || "Предыдущий элемент";
      next.title = navigation.nextLabel || "Следующий элемент";
      previous.setAttribute("aria-label", previous.title);
      next.setAttribute("aria-label", next.title);
      pageLabel.setAttribute("aria-live", "polite");
      previous.addEventListener("click", function () { runNavigation(navigation.onPrevious, pageLabel); });
      next.addEventListener("click", function () { runNavigation(navigation.onNext, pageLabel); });
      stageShell.appendChild(previous);
      stageShell.appendChild(next);
      stageShell.appendChild(pageLabel);
    }
    stageShell.addEventListener("keydown", function (event) {
      var key = event && event.key;
      if (navigation && key === "ArrowLeft" && !previous.disabled) runNavigation(navigation.onPrevious, pageLabel);
      else if (navigation && key === "ArrowRight" && !next.disabled) runNavigation(navigation.onNext, pageLabel);
      else if (vendorViewer && (key === "+" || key === "=")) vendorViewer.zoom(0.15, true);
      else if (vendorViewer && key === "-") vendorViewer.zoom(-0.15, true);
      else if (vendorViewer && key === "0") showActualSize();
      else if (vendorViewer && (key === "f" || key === "F")) fitViewer();
      else return;
      if (typeof event.preventDefault === "function") event.preventDefault();
      if (typeof event.stopPropagation === "function") event.stopPropagation();
    });
    root.appendChild(toolbar);
    root.appendChild(stageShell);
    var sequence = options.sequence || null;
    if (sequence && Number(sequence.count || 0) > 1) {
      if (!window.RNAssistantSequenceViewer) throw new Error("Sequence viewer is unavailable.");
      root.classList.add("has-sequence");
      sequenceController = window.RNAssistantSequenceViewer.createRail({
        ariaLabel: sequence.ariaLabel || "Изображения",
        title: sequence.title || "Изображения",
        count: sequence.count,
        currentIndex: sequence.currentIndex,
        orientation: "horizontal",
        itemExtent: sequence.itemExtent || 92,
        showJump: false,
        showNumbers: false,
        pending: sequence.pending === true,
        scrollOffset: sequence.scrollOffset,
        getItem: sequence.getItem,
        itemLabel: sequence.itemLabel,
        onScroll: sequence.onScroll,
        onRequest: sequence.onRequest,
        onSelect: sequence.onSelect,
        renderItem: function (index, item, preview) {
          if (index === Number(sequence.currentIndex || 0)) {
            var current = element("img", "rn-sequence-current-image");
            current.alt = "";
            current.src = objectUrl;
            preview.appendChild(current);
            return "ready";
          }
          return typeof sequence.renderItem === "function"
            ? sequence.renderItem(index, item, preview)
            : null;
        }
      });
      root.appendChild(sequenceController.element);
    }
    image.src = objectUrl;

    function release() {
      if (released) return;
      released = true;
      if (resizeObserver) resizeObserver.disconnect();
      resizeObserver = null;
      if (resizeFrame && typeof window.cancelAnimationFrame === "function") window.cancelAnimationFrame(resizeFrame);
      resizeFrame = 0;
      if (resizeTimer) window.clearTimeout(resizeTimer);
      resizeTimer = 0;
      if (sequenceController) sequenceController.destroy();
      sequenceController = null;
      if (vendorViewer && typeof vendorViewer.destroy === "function") vendorViewer.destroy();
      vendorViewer = null;
      URL.revokeObjectURL(objectUrl);
    }
    window.addEventListener("beforeunload", release);
    return {
      element: root,
      sourceUrl: objectUrl,
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

  function createPdfThumbnailRail(options, currentPage, thumbnailUrl, currentPageUrl) {
    if (!window.RNAssistantSequenceViewer) throw new Error("Sequence viewer is unavailable.");
    var pageCount = Math.max(1, Number(options.pageCount || 1));
    var currentIndex = Math.max(0, Math.min(Number(currentPage.pageIndex || 0), pageCount - 1));
    return window.RNAssistantSequenceViewer.createRail({
      className: "rn-pdf-thumbnail-rail",
      ariaLabel: "Страницы PDF",
      title: "Страницы",
      count: pageCount,
      currentIndex: currentIndex,
      currentRole: "page",
      orientation: "vertical",
      itemExtent: 126,
      jumpLabel: "Перейти к странице",
      pending: options.pending === true,
      scrollOffset: options.thumbnailScrollTop,
      itemLabel: function (pageIndex) { return "Страница " + (pageIndex + 1); },
      onScroll: options.onThumbnailScroll,
      onRequest: options.onThumbnailRequest,
      onSelect: options.onPageSelect,
      renderItem: function (pageIndex, item, preview) {
        var thumbnail = pageIndex === currentIndex
          ? currentPage
          : ((options.thumbnails || {})[String(pageIndex)] || null);
        if (thumbnail && (!thumbnail.status || thumbnail.status === "ready")) {
          try {
            var image = element("img", "rn-pdf-thumbnail-image");
            image.alt = "";
            image.src = pageIndex === currentIndex && currentPageUrl
              ? currentPageUrl
              : thumbnailUrl(thumbnail);
            preview.appendChild(image);
            return "ready";
          } catch (error) {
            return { status: "error", message: "Миниатюра недоступна" };
          }
        }
        if (thumbnail && thumbnail.status === "error") {
          return { status: "error", message: thumbnail.message || "Миниатюра недоступна" };
        }
        return thumbnail ? thumbnail.status : "";
      }
    });
  }

  function createPdf(options) {
    options = options || {};
    var root = element("div", "rn-pdf-viewer");
    if (options.extractionWarning) {
      root.appendChild(element("div", "rn-pdf-viewer-warning", options.extractionWarning));
    }
    var tabs = element("div", "rn-pdf-viewer-tabs");
    var pagesButton = element("button", "secondary compact active", "Страницы");
    var textButton = element("button", "secondary compact", "Текст");
    pagesButton.type = textButton.type = "button";
    tabs.appendChild(pagesButton);
    tabs.appendChild(textButton);
    root.appendChild(tabs);
    var body = element("div", "rn-pdf-viewer-body");
    root.appendChild(body);
    var child = null;
    var railChild = null;
    var thumbnailUrls = {};

    function pdfThumbnailUrl(page) {
      var key = String(page.pageIndex) + ":" + String(page.imageContentSha256 || "");
      if (thumbnailUrls[key]) return thumbnailUrls[key];
      var bytes = bytesFromBase64(page.imageBase64Content);
      if (bytes.byteLength !== Number(page.imageByteLength || 0)) {
        throw new Error("PDF thumbnail byte length is inconsistent.");
      }
      thumbnailUrls[key] = URL.createObjectURL(new Blob([bytes], { type: page.imageMimeType }));
      return thumbnailUrls[key];
    }

    function releaseThumbnailUrls() {
      Object.keys(thumbnailUrls).forEach(function (key) { URL.revokeObjectURL(thumbnailUrls[key]); });
      thumbnailUrls = {};
    }

    function clear() {
      if (child && typeof child.destroy === "function") child.destroy();
      child = null;
      if (railChild && typeof railChild.destroy === "function") railChild.destroy();
      railChild = null;
      releaseThumbnailUrls();
      body.replaceChildren();
    }

    function showPages() {
      clear();
      if (typeof options.onTabChange === "function") options.onTabChange("pages");
      pagesButton.classList.add("active");
      textButton.classList.remove("active");
      var page = options.page || {};
      child = createImage({
        title: fileName(options.title, "document.pdf") + ".page-" + (Number(page.pageIndex || 0) + 1) + ".jpg",
        mimeType: page.imageMimeType,
        byteLength: page.imageByteLength,
        base64Content: page.imageBase64Content,
        downloadLabel: "Скачать страницу",
        documentPage: true,
        navigation: {
          label: (Number(page.pageIndex || 0) + 1) + " / " + Number(options.pageCount || 0),
          hasPrevious: !options.pending && Number(page.pageIndex || 0) > 0 && typeof options.onPrevious === "function",
          hasNext: !options.pending && Number(page.pageIndex || 0) + 1 < Number(options.pageCount || 0) &&
            typeof options.onNext === "function",
          onPrevious: options.onPrevious,
          onNext: options.onNext,
          previousLabel: "Предыдущая страница",
          nextLabel: "Следующая страница",
          unavailableLabel: "Страница недоступна"
        }
      });
      var layout = element("div", "rn-pdf-pages-layout");
      var pageHost = element("div", "rn-pdf-page-host");
      pageHost.appendChild(child.element);
      railChild = createPdfThumbnailRail(options, page, pdfThumbnailUrl, child.sourceUrl);
      layout.appendChild(railChild.element);
      layout.appendChild(pageHost);
      body.appendChild(layout);
    }

    function showText() {
      clear();
      if (typeof options.onTabChange === "function") options.onTabChange("text");
      pagesButton.classList.remove("active");
      textButton.classList.add("active");
      if (!window.RNAssistantArtifactTextViewers) {
        body.appendChild(element("div", "artifact-detail-error", "Text viewer is unavailable."));
        return;
      }
      var page = options.textPage || {};
      var text = String(page.text || "");
      child = window.RNAssistantArtifactTextViewers.createText({
        text: text,
        fullText: options.textComplete ? options.fullText : null,
        complete: options.textComplete === true,
        offset: Number(page.offset || 0),
        startLine: Number(page.startLine || 1),
        totalCharacters: Number(page.totalCharacters || options.extractedCharacters || text.length),
        sourceComplete: options.sourceComplete === true,
        viewerLimitReached: options.viewerLimitReached === true,
        fullReadAllowed: options.fullReadAllowed === true,
        hasPrevious: options.hasTextPrevious === true,
        hasNext: options.hasTextNext === true,
        onPrevious: options.onTextPrevious,
        onNext: options.onTextNext,
        onLoadFull: options.onLoadTextFull,
        onCopy: window.copyTextResult
      });
      body.appendChild(child.element);
    }

    pagesButton.addEventListener("click", showPages);
    textButton.addEventListener("click", showText);
    if (options.initialTab === "text") showText();
    else showPages();
    return {
      element: root,
      destroy: function () { clear(); root.replaceChildren(); }
    };
  }

  if (!window.RNAssistantViewerRegistry) throw new Error("Viewer registry is unavailable.");
  window.RNAssistantViewerRegistry.register("image", createImage);
  window.RNAssistantViewerRegistry.register("pdf", createPdf);
  window.RNAssistantViewerRegistry.register("task_list", createTaskList);
  window.RNAssistantArtifactResourceViewers = {
    createImage: createImage,
    createPdf: createPdf,
    createTaskList: createTaskList
  };
}());
