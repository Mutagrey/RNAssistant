(function () {
  "use strict";
  // One bounded Myers formatter for editor previews, retained journal pairs and run summaries.
  // Presentation only: the source owner establishes existence/effect/completeness.
  function tokens(text) { return text.match(/[^\n]*\n|[^\n]+$/g) || []; }
  function format(before, after) {
    before = String(before == null ? "" : before); after = String(after == null ? "" : after);
    if (before.length + after.length > 512000) return limited();
    var old = tokens(before.replace(/\r\n?/g, "\n")), next = tokens(after.replace(/\r\n?/g, "\n"));
    var prefix = 0, suffix = 0;
    while (prefix < old.length && prefix < next.length && old[prefix] === next[prefix]) prefix++;
    while (suffix < old.length - prefix && suffix < next.length - prefix &&
        old[old.length - 1 - suffix] === next[next.length - 1 - suffix]) suffix++;
    var a = old.slice(prefix, old.length - suffix), b = next.slice(prefix, next.length - suffix);
    var edits = [], trace = [], v = { 1: 0 }, work = 0, found = false;
    if (old.length + next.length > 40000 || before.length + after.length > 512000) return limited();
    if (!a.length || !b.length) {
      edits = a.map(function (text) { return { type: "remove", text: text }; })
        .concat(b.map(function (text) { return { type: "add", text: text }; }));
      found = true;
    }
    outer: for (var d = 0; !found && d <= a.length + b.length; d++) {
      var layer = {};
      for (var k = -d; k <= d; k += 2) {
        if (++work > 500000) return limited();
        var left = v[k - 1] == null ? -Infinity : v[k - 1];
        var right = v[k + 1] == null ? -Infinity : v[k + 1];
        var x = k === -d || (k !== d && left < right) ? right : left + 1;
        var y = x - k;
        while (x < a.length && y < b.length && a[x] === b[y]) {
          if (++work > 500000) return limited();
          x++; y++;
        }
        layer[k] = x;
        if (x >= a.length && y >= b.length) { trace.push(layer); found = true; break outer; }
      }
      trace.push(layer); v = layer;
    }
    if (!found) return limited();
    var ax = a.length, by = b.length;
    for (var depth = trace.length - 1; depth > 0; depth--) {
      var prev = trace[depth - 1], diagonal = ax - by;
      var pk = diagonal === -depth || (diagonal !== depth &&
        (prev[diagonal - 1] == null ? -Infinity : prev[diagonal - 1]) <
        (prev[diagonal + 1] == null ? -Infinity : prev[diagonal + 1])) ? diagonal + 1 : diagonal - 1;
      var px = prev[pk], py = px - pk;
      while (ax > px && by > py) { edits.push({ type: "context", text: a[--ax] }); by--; }
      if (ax === px) edits.push({ type: "add", text: b[--by] });
      else edits.push({ type: "remove", text: a[--ax] });
    }
    while (ax > 0 && by > 0) { edits.push({ type: "context", text: a[--ax] }); by--; }
    if (trace.length) edits.reverse();
    edits = old.slice(0, prefix).map(context).concat(edits, old.slice(old.length - suffix).map(context));
    var added = 0, removed = 0, oldLine = 0, newLine = 0, indexes = [];
    edits.forEach(function (line, index) {
      line.oldLine = line.type === "add" ? "" : ++oldLine;
      line.newLine = line.type === "remove" ? "" : ++newLine;
      if (line.type === "add") added++;
      if (line.type === "remove") removed++;
      if (line.type !== "context") indexes.push(index);
      line.noNewline = line.text.slice(-1) !== "\n";
      line.text = line.text.replace(/\n$/, "");
    });
    var visible = [], end = -1;
    indexes.forEach(function (index) {
      var start = Math.max(0, index - 3), limit = Math.min(edits.length, index + 4);
      if (start > end + 1) visible.push({ type: "note", text: "⋯" });
      for (var i = Math.max(start, end + 1); i < limit; i++) {
        if (visible.length < 600) visible.push(edits[i]);
      }
      end = Math.max(end, limit - 1);
    });
    var truncated = indexes.length > 0 && visible.length >= 600;
    if (truncated) visible.push({ type: "note", text: "Показаны первые 600 строк diff; счётчики полные." });
    var eolOnly = before !== after && !added && !removed;
    return { added: added, removed: removed, complete: true, truncated: truncated, lines: visible,
      summary: added || removed ? "+" + added + " −" + removed : eolOnly ? "Изменён формат перевода строк." : "Изменений нет." };
  }
  function context(text) { return { type: "context", text: text }; }
  function limited() { return { added: null, removed: null, complete: false, lines: [], summary: "Сравнение превышает лимит. Число строк не рассчитано." }; }

  function render(container, diff) {
    if (!container) return;
    container.textContent = "";
    var summary = document.createElement("div"); summary.className = "vba-diff-summary";
    summary.textContent = diff.summary || ""; container.appendChild(summary);
    (diff.lines || []).forEach(function (line) {
      var row = document.createElement("div"); row.className = "vba-diff-line " + line.type;
      [line.type === "add" ? "+" : line.type === "remove" ? "−" : " ", line.oldLine, line.newLine].forEach(function (value, index) {
        var span = document.createElement("span");
        span.className = index === 0 ? "vba-diff-marker" : "vba-diff-line-number vba-diff-" + (index === 1 ? "old" : "new") + "-line";
        span.textContent = value || ""; row.appendChild(span);
      });
      var code = document.createElement("code"); code.textContent = line.text || "";
      if (line.noNewline && line.type !== "context") {
        var note = document.createElement("span"); note.className = "text-diff-newline";
        note.textContent = " ⏎ нет перевода строки"; code.appendChild(note);
      }
      row.appendChild(code); container.appendChild(row);
    });
  }
  window.RNAssistantTextDiff = { format: format, render: render };
}());
