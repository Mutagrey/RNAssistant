(function () {
  "use strict";
  var P = window.Prototype, editors = [], documents = new Map(), viewers = [], auxiliaries = [], objectUrls = [], editorSizes = [];

  // The product renderer owns parsing/sanitization; this preview supplies its small host seam.
  window.markdown = function (source) { return window.DOMPurify.sanitize(window.marked.parse(source, { breaks: false })); };
  window.copyTextResult = function (text) { return navigator.clipboard.writeText(text); };

  function mode(key) {
    if (/^(vba:|tool-code)/.test(key)) return "vb";
    if (/schema|args|template|\.json$/.test(key) || key === "material:json") return { name: "javascript", json: true };
    if (/\.js$/.test(key)) return "javascript";
    if (/skill|prompt|report|plan|readme/.test(key)) return "markdown";
    return null; // HTML/CSS modes are not shipped in the current product; no CDN fallback.
  }
  function mountEditor(textarea) {
    if (!window.CodeMirror) return;
    var key = textarea.dataset.draft, source = textarea.value, cached = documents.get(key), cm;
    if (!cached || cached.getValue() !== source) { cached = new CodeMirror.Doc(source, mode(key)); documents.set(key, cached); }
    cm = CodeMirror.fromTextArea(textarea, {
      mode: mode(key), lineNumbers: true, lineWrapping: true, indentUnit: 2, tabSize: 2,
      matchBrackets: true, autoCloseBrackets: true, styleActiveLine: true, viewportMargin: 12,
      extraKeys: { "Ctrl-S": function () { P.dispatchAction("save-editor"); }, "Cmd-S": function () { P.dispatchAction("save-editor"); } }
    });
    cm.swapDoc(cached);
    cm.getWrapperElement().classList.add("product-code-editor");
    cm.getInputField().setAttribute("aria-label", textarea.getAttribute("aria-label"));
    cm.on("change", function () { textarea.value = cm.getValue(); textarea.dispatchEvent(new Event("input", { bubbles: true })); });
    function position() {
      var target = document.querySelector("[data-editor-position]"), cursor = cm.getCursor();
      if (target) target.textContent = "Строка " + (cursor.line + 1) + ", столбец " + (cursor.ch + 1) + " · Строк: " + cm.lineCount();
    }
    cm.on("cursorActivity", position); cm.on("change", position); position();
    var size = new ResizeObserver(function () { if (textarea.isConnected) cm.refresh(); });
    size.observe(cm.getWrapperElement()); editorSizes.push(size);
    editors.push(cm); requestAnimationFrame(function () { if (textarea.isConnected) cm.refresh(); });
  }

  function imageFixture(page, isDocument) {
    var canvas = document.createElement("canvas"); canvas.width = 900; canvas.height = isDocument ? 1180 : 560;
    var c = canvas.getContext("2d"), headings = ["Методика расчёта", "Сопоставимые периоды", "Проверка источников"];
    c.fillStyle = "#fff"; c.fillRect(0, 0, canvas.width, canvas.height);
    c.fillStyle = "#718076"; c.font = "16px system-ui"; c.fillText("RN / ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ", 64, 65);
    c.fillStyle = "#22362b"; c.font = "bold 34px system-ui"; c.fillText(isDocument ? headings[page] : "Динамика продаж", 64, 130);
    c.font = "20px system-ui";
    var lines = isDocument ? [
      ["Сравнивайте выручку за одинаковые периоды.", "Используйте одну валюту и единый состав источников.", "Выручка = сумма продаж за выбранный период."],
      ["Сентябрь: 12,8 млн ₽. Август: 10,8 млн ₽.", "Прирост: 18,4%. Заказы: 1 248 (+13,0%).", "Отдельно проверьте возвраты и неполные записи."],
      ["Москва.xlsx и Казань.xlsx — данные проверены.", "Самара.xlsx — отсутствует поле «Выручка».", "Пропуски исключены из расчёта и отмечены в отчёте."]
    ][page] : ["Выручка по месяцам · млн ₽"];
    lines.forEach(function (line, index) { c.fillText(line, 64, 190 + index * 37); });
    var values = [9.6, 10.2, 10.8, 12.8], names = ["Июнь", "Июль", "Август", "Сентябрь"], base = isDocument ? 650 : 450;
    values.forEach(function (value, index) {
      var x = 80 + index * 185, h = value * 14;
      c.fillStyle = index === 3 ? "#35684b" : "#d8e6dd"; c.fillRect(x, base - h, 120, h);
      c.fillStyle = "#22362b"; c.fillText(String(value).replace(".", ","), x + 34, base - h - 16); c.font = "17px system-ui"; c.fillText(names[index], x + 16, base + 35); c.font = "20px system-ui";
    });
    if (isDocument) { c.fillStyle = "#718076"; c.font = "16px system-ui"; c.fillText("Страница " + (page + 1) + " / 3 · образец для просмотрщика", 64, 1100); }
    var mime = isDocument ? "image/jpeg" : "image/png", bytes = atob(canvas.toDataURL(mime, 0.94).split(",")[1]);
    var blob = new Blob([Uint8Array.from(bytes, function (ch) { return ch.charCodeAt(0); })], { type: mime });
    var url = URL.createObjectURL(blob); objectUrls.push(url);
    return { objectUrl: url, byteLength: blob.size, mimeType: mime };
  }
  function mountViewer(target) {
    var item = P.selectedMaterial(), kind = target.dataset.productViewer, source = P.materialSource(), options;
    function copy(text) { return window.copyTextResult(text); }
    if (kind === "collection") {
      var names = ["Москва", "Казань", "Самара"], rail = RNAssistantSequenceViewer.createRail({
        title: "Регионы", ariaLabel: "Элементы коллекции", count: 3, currentIndex: item.page - 1,
        orientation: "horizontal", className: "collection-rail", showJump: false, showNumbers: false,
        itemExtent: 156, itemLabel: function (i) { return names[i]; },
        onSelect: function (i) { item.page = i + 1; P.refresh(); },
        renderItem: function (i, item, preview) {
          var title = document.createElement("strong"), value = document.createElement("span");
          title.textContent = names[i]; value.textContent = ["7,42 млн ₽", "5,38 млн ₽", "Нет данных"][i];
          preview.append(title, value); return "ready";
        }
      });
      target.appendChild(rail.element); auxiliaries.push(rail);
      var content = document.createElement("div"); content.className = "collection-page"; target.appendChild(content);
      var text = "# " + names[item.page - 1] + "\n\n" + ["Выручка — **7,42 млн ₽**. Основной вклад в рост сентября.", "Выручка — **5,38 млн ₽**. Сопоставимый период подтверждён.", "В исходном файле отсутствует колонка «Выручка». Регион исключён из расчёта."][item.page - 1];
      var childOptions = { text: text, fullText: text, complete: true, sourceComplete: true, onCopy: copy };
      var childController = RNAssistantViewerRegistry.mount("markdown", content, childOptions); viewers.push(content);
      return;
    }
    if (kind === "json") options = { text: source, completeness: "full", expandedPaths: ["$", "$.regions"], onCopy: copy };
    if (kind === "markdown" || kind === "text") options = { text: source, fullText: source, complete: true, sourceComplete: true, draft: !!P.state.dirty[P.materialKey()], onCopy: copy };
    if (kind === "task_list") options = { text: source };
    if (kind === "image") { var image = imageFixture(0, false); options = { title: item.name, data: image, mimeType: image.mimeType, byteLength: image.byteLength }; }
    if (kind === "pdf") {
      var pages = [0, 1, 2].map(function (index) { var img = imageFixture(index, true); return { pageIndex: index, imageMimeType: img.mimeType, imageByteLength: img.byteLength, data: img }; });
      var fullText = "Методика расчёта\n\nСравнивайте выручку за одинаковые периоды, в одной валюте.\n\nСентябрь: 12,8 млн ₽. Август: 10,8 млн ₽. Прирост: 18,4%.\n\nМосква и Казань: источники проверены. Самара: отсутствует поле «Выручка».";
      function change(index) { item.page = Math.max(1, Math.min(3, index + 1)); P.refresh(); }
      options = { title: item.name, pageCount: 3, page: pages[item.page - 1], thumbnails: { 0: pages[0], 1: pages[1], 2: pages[2] },
        initialTab: item.viewerTab || "pages", onTabChange: function (tab) { item.viewerTab = tab; },
        onPrevious: function () { change(item.page - 2); }, onNext: function () { change(item.page); }, onPageSelect: change,
        textPage: { text: fullText }, fullText: fullText, textComplete: true, sourceComplete: true };
    }
    try {
      RNAssistantViewerRegistry.mount(kind, target, options); viewers.push(target);
    }
    catch (error) { target.textContent = "Просмотр недоступен: " + error.message; }
  }
  P.productComponents = {
    mount: function () {
      document.querySelectorAll("textarea[data-draft]").forEach(mountEditor);
      document.querySelectorAll("[data-product-viewer]").forEach(mountViewer);
      P.diffUI.mount(document);
    },
    unmount: function () {
      editorSizes.forEach(function (observer) { observer.disconnect(); }); editorSizes = [];
      editors.forEach(function (cm) { cm.save(); cm.swapDoc(new CodeMirror.Doc("")); cm.toTextArea(); }); editors = [];
      auxiliaries.forEach(function (controller) { controller.destroy(); }); auxiliaries = [];
      viewers.forEach(function (target) { RNAssistantViewerRegistry.unmount(target); }); viewers = [];
      objectUrls.forEach(function (url) { URL.revokeObjectURL(url); }); objectUrls = [];
    }
  };
}());
