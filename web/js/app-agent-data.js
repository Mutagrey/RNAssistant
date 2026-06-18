function prettyJsonText(text) {
  if (!text) {
    return "";
  }

  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch (error) {
    return String(text);
  }
}

function tryParseJson(text) {
  if (!text) {
    return { ok: false, value: null };
  }

  try {
    return { ok: true, value: JSON.parse(text) };
  } catch (error) {
    return { ok: false, value: null };
  }
}

function isScalarJson(value) {
  return value === null || typeof value === "string" || typeof value === "number" || typeof value === "boolean";
}

function scalarJsonText(value) {
  if (value === null) {
    return "null";
  }
  if (typeof value === "boolean") {
    return value ? "true" : "false";
  }
  return String(value);
}

function objectKeys(value) {
  return value && typeof value === "object" && !Array.isArray(value) ? Object.keys(value) : [];
}

function tableKeysForArray(items) {
  var keys = [];
  (items || []).slice(0, 8).forEach(function (item) {
    objectKeys(item).forEach(function (key) {
      if (keys.indexOf(key) < 0 && isScalarJson(item[key])) {
        keys.push(key);
      }
    });
  });
  return keys.slice(0, 8);
}

function renderJsonValue(value, depth) {
  if (isScalarJson(value)) {
    var scalar = document.createElement("span");
    scalar.className = "agent-data-scalar";
    scalar.textContent = scalarJsonText(value);
    return scalar;
  }

  if (Array.isArray(value)) {
    return renderJsonArray(value, depth);
  }

  return renderJsonObject(value, depth);
}

function renderJsonArray(value, depth) {
  if (!value.length) {
    var emptyArray = document.createElement("span");
    emptyArray.className = "agent-data-empty";
    emptyArray.textContent = "Empty array";
    return emptyArray;
  }

  var tableKeys = tableKeysForArray(value);
  var allRowsAreObjects = value.every(function (item) {
    return item && typeof item === "object" && !Array.isArray(item);
  });
  if (allRowsAreObjects && tableKeys.length) {
    return renderJsonTable(value, tableKeys);
  }

  var list = document.createElement("ol");
  list.className = "agent-data-list";
  value.slice(0, 20).forEach(function (item) {
    var li = document.createElement("li");
    li.appendChild(renderJsonValue(item, depth + 1));
    list.appendChild(li);
  });
  if (value.length > 20) {
    var more = document.createElement("li");
    more.className = "agent-data-note";
    more.textContent = "..." + (value.length - 20) + " more";
    list.appendChild(more);
  }
  return list;
}

function renderJsonTable(value, tableKeys) {
  var wrap = document.createElement("div");
  wrap.className = "agent-data-table-wrap";
  var table = document.createElement("table");
  table.className = "agent-data-table";
  var thead = document.createElement("thead");
  var headerRow = document.createElement("tr");
  tableKeys.forEach(function (key) {
    var th = document.createElement("th");
    th.textContent = key;
    headerRow.appendChild(th);
  });
  thead.appendChild(headerRow);
  table.appendChild(thead);

  var tbody = document.createElement("tbody");
  value.slice(0, 20).forEach(function (item) {
    var row = document.createElement("tr");
    tableKeys.forEach(function (key) {
      var cell = document.createElement("td");
      cell.textContent = scalarJsonText(item[key]);
      row.appendChild(cell);
    });
    tbody.appendChild(row);
  });
  table.appendChild(tbody);
  wrap.appendChild(table);
  if (value.length > 20) {
    var note = document.createElement("div");
    note.className = "agent-data-note";
    note.textContent = "Showing 20 of " + value.length + " rows";
    wrap.appendChild(note);
  }
  return wrap;
}

function renderJsonObject(value, depth) {
  var keys = objectKeys(value);
  if (!keys.length) {
    var emptyObject = document.createElement("span");
    emptyObject.className = "agent-data-empty";
    emptyObject.textContent = "Empty object";
    return emptyObject;
  }

  if (depth > 3) {
    var compact = document.createElement("code");
    compact.className = "agent-data-compact";
    compact.textContent = JSON.stringify(value);
    return compact;
  }

  var grid = document.createElement("dl");
  grid.className = "agent-data-grid";
  keys.forEach(function (key) {
    var dt = document.createElement("dt");
    dt.textContent = key;
    var dd = document.createElement("dd");
    dd.appendChild(renderJsonValue(value[key], depth + 1));
    grid.appendChild(dt);
    grid.appendChild(dd);
  });
  return grid;
}

function appendRawJson(parent, label, text, open) {
  if (!text) {
    return;
  }

  var details = document.createElement("details");
  details.className = "agent-json";
  details.open = !!open;
  var summary = document.createElement("summary");
  summary.textContent = label;
  details.appendChild(summary);

  var pre = document.createElement("pre");
  var code = document.createElement("code");
  code.className = "language-json";
  code.textContent = prettyJsonText(text);
  pre.appendChild(code);
  details.appendChild(pre);
  parent.appendChild(details);
}

function appendActivityData(parent, label, text) {
  if (!text) {
    return;
  }

  var parsed = tryParseJson(text);
  if (!parsed.ok) {
    appendRawJson(parent, label, text, true);
    return;
  }

  var details = document.createElement("details");
  details.className = "agent-data";
  var summary = document.createElement("summary");
  summary.textContent = label;
  details.appendChild(summary);

  var view = document.createElement("div");
  view.className = "agent-data-view";
  view.appendChild(renderJsonValue(parsed.value, 0));
  details.appendChild(view);

  appendRawJson(details, "Raw JSON", text, false);
  parent.appendChild(details);
}
