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
    copyText(text || "");
  });
  return button;
}

function isScalarJson(value) {
  return value === null || typeof value === "string" || typeof value === "number" || typeof value === "boolean";
}

function appendUnique(items, value) {
  if (value && items.indexOf(value) < 0) {
    items.push(value);
  }
}

function activityDataBadges(activity) {
  var badges = [];
  var toolId = activityToolId(activity).toLowerCase();
  var args = tryParseJson(activityArgumentsJson(activity));
  var data = tryParseJson(activityDataJson(activity));
  var combined = toolId + " " + (activityArgumentsJson(activity) || "").toLowerCase() + " " + (activityDataJson(activity) || "").toLowerCase();

  if (combined.indexOf("vba") >= 0 || combined.indexOf("module") >= 0 || combined.indexOf("macro") >= 0) {
    appendUnique(badges, "vba");
  }
  if (combined.indexOf("slide") >= 0 || combined.indexOf("powerpoint") >= 0) {
    appendUnique(badges, "slide");
  }
  if (combined.indexOf("mail") >= 0 || combined.indexOf("email") >= 0 || combined.indexOf("outlook") >= 0) {
    appendUnique(badges, "mail");
  }
  if (combined.indexOf("range") >= 0 || combined.indexOf("address") >= 0 || combined.indexOf("sheet") >= 0 || combined.indexOf("cell") >= 0) {
    appendUnique(badges, "range");
  }
  if (combined.indexOf("table") >= 0 || combined.indexOf("rows") >= 0 || combined.indexOf("values") >= 0) {
    appendUnique(badges, "table");
  }

  [args.value, data.value].forEach(function (value) {
    if (Array.isArray(value)) {
      appendUnique(badges, "table");
    }
    if (value && typeof value === "object") {
      var keys = objectKeys(value).map(function (key) { return key.toLowerCase(); }).join(" ");
      if (/(rows|values|cells|table)/.test(keys)) {
        appendUnique(badges, "table");
      }
      if (/(range|address|sheet|cell)/.test(keys)) {
        appendUnique(badges, "range");
      }
    }
  });

  if (activityDataJson(activity)) {
    appendUnique(badges, "json");
  }
  return badges;
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
  if (value.length > 10) {
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
  value.forEach(function (item) {
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
  if (value.length > 10) {
    var toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "agent-table-toggle";
    toggle.textContent = "Show all " + value.length + " rows";
    toggle.addEventListener("click", function () {
      var collapsed = wrap.classList.toggle("collapsed");
      toggle.textContent = collapsed ? "Show all " + value.length + " rows" : "Collapse";
    });
    wrap.appendChild(toggle);
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

  var actions = document.createElement("div");
  actions.className = "agent-detail-actions";
  actions.appendChild(createAgentCopyButton("Copy raw JSON", prettyJsonText(text)));
  details.appendChild(actions);

  var pre = document.createElement("pre");
  var code = document.createElement("code");
  code.className = "language-json";
  code.textContent = prettyJsonText(text);
  pre.appendChild(code);
  details.appendChild(pre);
  parent.appendChild(details);
}

function renderArgumentChips(value) {
  var chips = document.createElement("div");
  chips.className = "agent-input-chips";
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    var fallback = document.createElement("span");
    fallback.className = "agent-input-chip";
    fallback.textContent = scalarJsonText(value);
    chips.appendChild(fallback);
    return chips;
  }

  objectKeys(value).forEach(function (key) {
    var chip = document.createElement("span");
    chip.className = "agent-input-chip";
    var label = document.createElement("strong");
    label.textContent = key + ":";
    chip.appendChild(label);
    var chipValue = isScalarJson(value[key]) ? scalarJsonText(value[key]) : JSON.stringify(value[key]);
    if (chipValue.length > 80) {
      chipValue = chipValue.substring(0, 77) + "...";
    }
    chip.appendChild(document.createTextNode(" " + chipValue));
    chips.appendChild(chip);
  });
  return chips;
}

function appendArgumentsData(parent, text) {
  if (!text) {
    return;
  }

  var parsed = tryParseJson(text);
  if (!parsed.ok) {
    appendRawJson(parent, "Arguments", text, true);
    return;
  }

  var details = document.createElement("details");
  details.className = "agent-data agent-arguments";
  var summary = document.createElement("summary");
  summary.textContent = "Arguments";
  details.appendChild(summary);

  var actions = document.createElement("div");
  actions.className = "agent-detail-actions";
  actions.appendChild(createAgentCopyButton("Copy args", prettyJsonText(text)));
  details.appendChild(actions);

  var view = document.createElement("div");
  view.className = "agent-data-view";
  view.appendChild(renderArgumentChips(parsed.value));
  details.appendChild(view);

  appendRawJson(details, "Raw JSON", text, false);
  parent.appendChild(details);
}

function appendActivityData(parent, label, text, copyLabel) {
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

  var actions = document.createElement("div");
  actions.className = "agent-detail-actions";
  actions.appendChild(createAgentCopyButton(copyLabel || "Copy result", prettyJsonText(text)));
  details.appendChild(actions);

  var view = document.createElement("div");
  view.className = "agent-data-view";
  view.appendChild(renderJsonValue(parsed.value, 0));
  details.appendChild(view);

  appendRawJson(details, "Raw JSON", text, false);
  parent.appendChild(details);
}
