(function () {
  "use strict";

  function format(before, after) {
    if (before === after) {
      return { summary: "Изменений нет.", lines: [] };
    }

    var oldLines = String(before || "").replace(/\r\n/g, "\n").split("\n");
    var newLines = String(after || "").replace(/\r\n/g, "\n").split("\n");
    var start = 0;
    while (start < oldLines.length && start < newLines.length && oldLines[start] === newLines[start]) {
      start += 1;
    }

    var oldEnd = oldLines.length - 1;
    var newEnd = newLines.length - 1;
    while (oldEnd >= start && newEnd >= start && oldLines[oldEnd] === newLines[newEnd]) {
      oldEnd -= 1;
      newEnd -= 1;
    }

    var oldCount = Math.max(0, oldEnd - start + 1);
    var newCount = Math.max(0, newEnd - start + 1);
    var output = [];
    var i;
    for (i = Math.max(0, start - 3); i < start; i += 1) {
      output.push({ type: "context", oldLine: i + 1, newLine: i + 1, text: oldLines[i] });
    }
    for (i = start; i <= oldEnd && i < start + 200; i += 1) {
      output.push({ type: "remove", oldLine: i + 1, newLine: "", text: oldLines[i] });
    }
    for (i = start; i <= newEnd && i < start + 200; i += 1) {
      output.push({ type: "add", oldLine: "", newLine: i + 1, text: newLines[i] });
    }
    if (oldCount > 200 || newCount > 200) {
      output.push({ type: "note", oldLine: "", newLine: "", text: "...сравнение обрезано..." });
    }
    for (i = oldEnd + 1; i < Math.min(oldLines.length, oldEnd + 4); i += 1) {
      output.push({ type: "context", oldLine: i + 1, newLine: newEnd + i - oldEnd + 1, text: oldLines[i] });
    }
    return {
      summary: "Измененные строки: -" + oldCount + " +" + newCount,
      lines: output
    };
  }

  function render(container, diff) {
    if (!container) {
      return;
    }

    container.innerHTML = "";
    var summary = document.createElement("div");
    summary.className = "vba-diff-summary";
    summary.textContent = diff.summary || "";
    container.appendChild(summary);

    if (!diff.lines || !diff.lines.length) {
      var empty = document.createElement("div");
      empty.className = "vba-diff-empty";
      empty.textContent = diff.summary === "Изменений нет."
        ? "Текст редактора совпадает с загруженным модулем."
        : "Diff пока не построен.";
      container.appendChild(empty);
      return;
    }

    diff.lines.forEach(function (line) {
      var row = document.createElement("div");
      row.className = "vba-diff-line " + line.type;

      var marker = document.createElement("span");
      marker.className = "vba-diff-marker";
      marker.textContent = line.type === "add" ? "+" : (line.type === "remove" ? "-" : " ");

      var oldLine = document.createElement("span");
      oldLine.className = "vba-diff-line-number vba-diff-old-line";
      oldLine.textContent = line.oldLine || "";

      var newLine = document.createElement("span");
      newLine.className = "vba-diff-line-number vba-diff-new-line";
      newLine.textContent = line.newLine || "";

      var text = document.createElement("code");
      text.textContent = line.text || "";

      row.appendChild(marker);
      row.appendChild(oldLine);
      row.appendChild(newLine);
      row.appendChild(text);
      container.appendChild(row);
    });
  }

  window.RNAssistantVbaDiff = {
    format: format,
    render: render
  };
}());
