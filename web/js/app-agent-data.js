var AGENT_DATA_LIST_ITEM_LIMIT = 20;
var AGENT_DATA_TABLE_ROW_LIMIT = 100;
var AGENT_DATA_OBJECT_KEY_LIMIT = 100;

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

function createAgentCopyButton(label, text) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "agent-copy-button";
  button.textContent = label;
  button.addEventListener("click", function (event) {
    event.preventDefault();
    event.stopPropagation();
    var value = typeof text === "function" ? text() : text;
    copyText(value || "");
  });
  return button;
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
    emptyArray.textContent = "Пустой массив";
    return emptyArray;
  }

  var tableKeys = tableKeysForArray(value);
  var allRowsAreObjects = value.slice(0, AGENT_DATA_TABLE_ROW_LIMIT).every(function (item) {
    return item && typeof item === "object" && !Array.isArray(item);
  });
  if (allRowsAreObjects && tableKeys.length) {
    return renderJsonTable(value, tableKeys);
  }

  var list = document.createElement("ol");
  list.className = "agent-data-list";
  value.slice(0, AGENT_DATA_LIST_ITEM_LIMIT).forEach(function (item) {
    var li = document.createElement("li");
    li.appendChild(renderJsonValue(item, depth + 1));
    list.appendChild(li);
  });
  if (value.length > AGENT_DATA_LIST_ITEM_LIMIT) {
    var more = document.createElement("li");
    more.className = "agent-data-note";
    more.textContent = "Еще " + (value.length - AGENT_DATA_LIST_ITEM_LIMIT);
    list.appendChild(more);
  }
  return list;
}

function renderJsonTable(value, tableKeys) {
  var rows = value.slice(0, AGENT_DATA_TABLE_ROW_LIMIT);
  var wrap = document.createElement("div");
  wrap.className = "agent-data-table-wrap";
  if (rows.length > 10) {
    wrap.className += " collapsed";
  }
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
  rows.forEach(function (item) {
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
  if (rows.length > 10) {
    var toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "agent-table-toggle";
    var expandedLabel = value.length > rows.length
      ? "Развернуть первые " + rows.length + " из " + value.length + " строк"
      : "Показать все " + rows.length + " строк";
    toggle.textContent = expandedLabel;
    toggle.addEventListener("click", function () {
      var collapsed = wrap.classList.toggle("collapsed");
      toggle.textContent = collapsed ? expandedLabel : "Свернуть";
    });
    wrap.appendChild(toggle);
  }
  if (value.length > rows.length) {
    var note = document.createElement("span");
    note.className = "agent-data-note agent-table-note";
    note.textContent = "Показаны первые " + rows.length + " строк. Полный JSON доступен ниже.";
    wrap.appendChild(note);
  }
  return wrap;
}

function renderJsonObject(value, depth) {
  var keys = objectKeys(value);
  if (!keys.length) {
    var emptyObject = document.createElement("span");
    emptyObject.className = "agent-data-empty";
    emptyObject.textContent = "Пустой объект";
    return emptyObject;
  }

  if (depth > 3) {
    var compact = document.createElement("code");
    compact.className = "agent-data-compact";
    compact.textContent = "Объект: " + keys.length + " полей";
    return compact;
  }

  var grid = document.createElement("dl");
  grid.className = "agent-data-grid";
  keys.slice(0, AGENT_DATA_OBJECT_KEY_LIMIT).forEach(function (key) {
    var dt = document.createElement("dt");
    dt.textContent = key;
    var dd = document.createElement("dd");
    dd.appendChild(renderJsonValue(value[key], depth + 1));
    grid.appendChild(dt);
    grid.appendChild(dd);
  });
  if (keys.length > AGENT_DATA_OBJECT_KEY_LIMIT) {
    var moreKey = document.createElement("dt");
    moreKey.textContent = "…";
    var moreValue = document.createElement("dd");
    moreValue.className = "agent-data-note";
    moreValue.textContent = "Еще " + (keys.length - AGENT_DATA_OBJECT_KEY_LIMIT) + " полей";
    grid.appendChild(moreKey);
    grid.appendChild(moreValue);
  }
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
  var loaded = false;
  function load() {
    if (loaded) {
      return;
    }
    loaded = true;
    var actions = document.createElement("div");
    actions.className = "agent-detail-actions";
    actions.appendChild(createAgentCopyButton("Копировать JSON", function () { return prettyJsonText(text); }));
    details.appendChild(actions);

    var pre = document.createElement("pre");
    var code = document.createElement("code");
    code.className = "language-json";
    code.textContent = prettyJsonText(text);
    pre.appendChild(code);
    details.appendChild(pre);
  }
  details.addEventListener("toggle", function () {
    if (details.open) {
      load();
    }
  });
  parent.appendChild(details);
  if (details.open) {
    load();
  }
}

function appendStructuredAgentData(parent, label, text, copyLabel, className) {
  if (!text) {
    return;
  }

  var details = document.createElement("details");
  details.className = className || "agent-data";
  var summary = document.createElement("summary");
  summary.textContent = label;
  details.appendChild(summary);
  var loaded = false;
  details.addEventListener("toggle", function () {
    if (!details.open || loaded) {
      return;
    }
    loaded = true;
    var parsed = tryParseJson(text);
    if (!parsed.ok) {
      appendRawJson(details, "Исходный JSON", text, true);
      return;
    }
    var actions = document.createElement("div");
    actions.className = "agent-detail-actions";
    actions.appendChild(createAgentCopyButton(copyLabel, function () { return prettyJsonText(text); }));
    details.appendChild(actions);

    var view = document.createElement("div");
    view.className = "agent-data-view";
    view.appendChild(renderJsonValue(parsed.value, 0));
    details.appendChild(view);
    appendRawJson(details, "Исходный JSON", text, false);
  });
  parent.appendChild(details);
}

function appendArgumentsData(parent, text) {
  appendStructuredAgentData(parent, "Аргументы", text, "Копировать аргументы", "agent-data agent-arguments");
}

function appendActivityData(parent, label, text, copyLabel) {
  appendStructuredAgentData(parent, label, text, copyLabel || "Копировать результат", "agent-data");
}
