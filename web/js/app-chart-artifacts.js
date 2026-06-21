(function () {
  var saveTimers = {};
  var palette = ["#2563eb", "#16a34a", "#f97316", "#dc2626", "#7c3aed", "#0891b2", "#c2410c", "#4b5563"];

  function chartArtifactFromActivity(activity) {
    var parsed = tryParseJson(activityDataJson(activity));
    if (!parsed.ok || !parsed.value || typeof parsed.value !== "object") {
      return null;
    }
    var type = parsed.value.Type || parsed.value.type;
    return type === "rnassistant.chart" ? parsed.value : null;
  }

  function artifactValue(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
  }

  function artifactColumns(artifact) {
    return artifactValue(artifact, "Columns", "columns", []) || [];
  }

  function artifactRows(artifact) {
    return artifactValue(artifact, "Rows", "rows", []) || [];
  }

  function artifactConfig(artifact) {
    var config = artifactValue(artifact, "Config", "config", {}) || {};
    if (!Array.isArray(config.Series || config.series)) {
      config.Series = [];
    }
    if (!Array.isArray(config.Colors || config.colors)) {
      config.Colors = [];
    }
    artifact.Config = config;
    artifact.config = config;
    return config;
  }

  function columnName(column) {
    return artifactValue(column, "Name", "name", "");
  }

  function columnKind(column) {
    return artifactValue(column, "Kind", "kind", "");
  }

  function configValue(config, pascal, camel, fallback) {
    return artifactValue(config, pascal, camel, fallback);
  }

  function setConfigValue(config, pascal, camel, value) {
    config[pascal] = value;
    config[camel] = value;
  }

  function numberValue(value) {
    if (value === null || value === undefined || value === "") {
      return null;
    }
    var number = Number(value);
    return isNaN(number) ? null : number;
  }

  function selectedSeries(config) {
    return configValue(config, "Series", "series", []) || [];
  }

  function selectedColors(config) {
    return configValue(config, "Colors", "colors", []) || [];
  }

  function sourceText(artifact) {
    var source = artifactValue(artifact, "Source", "source", {}) || {};
    var sheet = artifactValue(source, "Sheet", "sheet", "");
    var address = artifactValue(source, "Address", "address", "");
    var workbook = artifactValue(source, "Workbook", "workbook", "");
    return [workbook, sheet && address ? sheet + "!" + address : address].filter(Boolean).join(" · ");
  }

  function createSelect(options, value, onChange) {
    var select = document.createElement("select");
    options.forEach(function (option) {
      var item = document.createElement("option");
      item.value = option.value;
      item.textContent = option.label;
      select.appendChild(item);
    });
    select.value = value || (options[0] ? options[0].value : "");
    select.addEventListener("change", function () {
      onChange(select.value);
    });
    return select;
  }

  function renderChartArtifact(activity, context) {
    var artifact = chartArtifactFromActivity(activity);
    if (!artifact) {
      return null;
    }

    var config = artifactConfig(artifact);
    var node = document.createElement("section");
    node.className = "chart-artifact";

    var header = document.createElement("div");
    header.className = "chart-artifact-header";
    var title = document.createElement("div");
    title.className = "chart-artifact-title";
    title.textContent = artifactValue(artifact, "Title", "title", "Chart");
    var source = document.createElement("div");
    source.className = "chart-artifact-source";
    source.textContent = sourceText(artifact);
    header.appendChild(title);
    header.appendChild(source);
    node.appendChild(header);

    var toolbar = document.createElement("div");
    toolbar.className = "chart-artifact-toolbar";
    appendChartControls(toolbar, artifact, config, context, function () {
      drawChart(node, artifact);
      saveArtifact(context, artifact);
    });
    node.appendChild(toolbar);

    var chart = document.createElement("div");
    chart.className = "chart-artifact-canvas";
    node.appendChild(chart);

    var details = document.createElement("div");
    details.className = "chart-artifact-point";
    details.textContent = "Выберите точку на графике, чтобы увидеть строку данных.";
    node.appendChild(details);

    window.setTimeout(function () {
      drawChart(node, artifact);
    }, 0);
    return node;
  }

  function appendChartControls(toolbar, artifact, config, context, changed) {
    var columns = artifactColumns(artifact);
    var x = configValue(config, "X", "x", "");
    var chartType = configValue(config, "ChartType", "chartType", "column");

    toolbar.appendChild(labeledControl("Тип", createSelect([
      { value: "column", label: "Column" },
      { value: "bar", label: "Bar" },
      { value: "line", label: "Line" },
      { value: "scatter", label: "Scatter" },
      { value: "pie", label: "Pie" }
    ], chartType, function (value) {
      setConfigValue(config, "ChartType", "chartType", value);
      changed();
    })));

    toolbar.appendChild(labeledControl("X", createSelect(columns.map(function (column) {
      var name = columnName(column);
      return { value: name, label: name };
    }), x, function (value) {
      setConfigValue(config, "X", "x", value);
      setConfigValue(config, "Series", "series", selectedSeries(config).filter(function (item) {
        return item !== value;
      }));
      changed();
    })));

    var seriesBox = document.createElement("div");
    seriesBox.className = "chart-series-list";
    columns.forEach(function (column) {
      var name = columnName(column);
      if (!name || name === x) {
        return;
      }
      var label = document.createElement("label");
      label.className = "chart-series-item";
      var input = document.createElement("input");
      input.type = "checkbox";
      input.checked = selectedSeries(config).indexOf(name) >= 0;
      input.addEventListener("change", function () {
        var series = selectedSeries(config).filter(function (item) { return item !== name; });
        if (input.checked) {
          series.push(name);
        }
        setConfigValue(config, "Series", "series", series);
        changed();
      });
      label.appendChild(input);
      label.appendChild(document.createTextNode(name));
      seriesBox.appendChild(label);
    });
    toolbar.appendChild(labeledControl("Series", seriesBox));

    selectedSeries(config).forEach(function (name, index) {
      var color = document.createElement("input");
      color.type = "color";
      color.value = selectedColors(config)[index] || palette[index % palette.length];
      color.addEventListener("input", function () {
        var colors = selectedColors(config).slice();
        colors[index] = color.value;
        setConfigValue(config, "Colors", "colors", colors);
        changed();
      });
      toolbar.appendChild(labeledControl(name, color));
    });

    if (context && context.messageId) {
      var refresh = document.createElement("button");
      refresh.type = "button";
      refresh.className = "chart-refresh-button";
      refresh.textContent = "Обновить";
      refresh.addEventListener("click", function () {
        refreshArtifactFromExcel(artifact, context, refresh);
      });
      toolbar.appendChild(refresh);
    }
  }

  function labeledControl(labelText, control) {
    var label = document.createElement("label");
    label.className = "chart-control";
    var span = document.createElement("span");
    span.textContent = labelText;
    label.appendChild(span);
    label.appendChild(control);
    return label;
  }

  function buildOption(artifact) {
    var rows = artifactRows(artifact);
    var columns = artifactColumns(artifact);
    var config = artifactConfig(artifact);
    var x = configValue(config, "X", "x", "");
    var series = selectedSeries(config);
    var colors = selectedColors(config);
    var chartType = configValue(config, "ChartType", "chartType", "column");
    var xColumn = columns.filter(function (column) { return columnName(column) === x; })[0] || {};
    var xKind = columnKind(xColumn);

    if (chartType === "pie") {
      var pieSeries = series[0] || "";
      return {
        color: colors.length ? colors : palette,
        tooltip: { trigger: "item" },
        legend: { type: "scroll", bottom: 0 },
        series: [{
          type: "pie",
          radius: ["36%", "68%"],
          data: rows.map(function (row) {
            return { name: row[x], value: numberValue(row[pieSeries]) };
          })
        }]
      };
    }

    var categoryData = rows.map(function (row) { return row[x]; });
    var axisIsValue = xKind === "number" && chartType === "scatter";
    var optionSeries = series.map(function (name, index) {
      var type = chartType === "column" || chartType === "bar" ? "bar" : chartType;
      var data = rows.map(function (row) {
        return axisIsValue ? [numberValue(row[x]), numberValue(row[name])] : numberValue(row[name]);
      });
      return {
        name: name,
        type: type,
        smooth: type === "line",
        itemStyle: { color: colors[index] || palette[index % palette.length] },
        data: data
      };
    });

    return {
      color: colors.length ? colors : palette,
      tooltip: { trigger: chartType === "scatter" ? "item" : "axis" },
      legend: { type: "scroll", top: 0 },
      grid: { left: 48, right: 18, top: 46, bottom: rows.length > 20 ? 58 : 34 },
      dataZoom: rows.length > 20 ? [{ type: "inside" }, { type: "slider", height: 18, bottom: 18 }] : [],
      xAxis: chartType === "bar"
        ? { type: "value" }
        : (axisIsValue ? { type: "value", name: x } : { type: "category", data: categoryData, axisLabel: { hideOverlap: true } }),
      yAxis: chartType === "bar"
        ? { type: "category", data: categoryData, axisLabel: { hideOverlap: true } }
        : { type: "value" },
      series: optionSeries
    };
  }

  function drawChart(root, artifact) {
    var canvas = root.querySelector(".chart-artifact-canvas");
    var point = root.querySelector(".chart-artifact-point");
    if (!canvas) {
      return;
    }
    if (!window.echarts) {
      canvas.textContent = "ECharts не загружен.";
      return;
    }
    var chart = window.echarts.getInstanceByDom(canvas) || window.echarts.init(canvas, null, { renderer: "canvas" });
    chart.clear();
    chart.setOption(buildOption(artifact), true);
    chart.off("click");
    chart.on("click", function (params) {
      var rows = artifactRows(artifact);
      var row = rows[params.dataIndex] || {};
      point.textContent = Object.keys(row).map(function (key) {
        return key + ": " + row[key];
      }).join(" · ");
    });
    window.setTimeout(function () {
      chart.resize();
    }, 0);
  }

  function saveArtifact(context, artifact) {
    if (!context || !context.messageId) {
      return;
    }
    var key = context.messageId;
    var dataJson = JSON.stringify(artifact);
    updateLocalMessageArtifact(context.messageId, dataJson);
    if (saveTimers[key]) {
      window.clearTimeout(saveTimers[key]);
    }
    saveTimers[key] = window.setTimeout(function () {
      send("updateMessageActivityData", {
        chatId: state.activeChatId,
        messageId: context.messageId,
        dataJson: dataJson
      }).catch(function (error) {
        log("Chart artifact save failed: " + (error.detail || error.message));
      });
    }, 350);
  }

  function updateLocalMessageArtifact(targetMessageId, dataJson) {
    state.messages.forEach(function (message) {
      if (messageId(message) === targetMessageId && messageActivity(message)) {
        var activity = messageActivity(message);
        activity.DataJson = dataJson;
        activity.dataJson = dataJson;
      }
    });
  }

  async function refreshArtifactFromExcel(artifact, context, button) {
    var source = artifactValue(artifact, "Source", "source", {}) || {};
    var mode = artifactValue(source, "SourceMode", "sourceMode", "selection");
    var args = {
      chartType: configValue(artifactConfig(artifact), "ChartType", "chartType", "auto"),
      title: artifactValue(artifact, "Title", "title", "Excel chart")
    };
    if (mode !== "selection") {
      args.sheet = artifactValue(source, "Sheet", "sheet", "");
      args.address = artifactValue(source, "Address", "address", "");
    }

    button.disabled = true;
    button.textContent = "Обновляю...";
    try {
      var result = await send("runTool", { toolId: "excel.create_chat_chart", arguments: args, dryRun: false });
      if (result.Success === false || result.success === false) {
        throw new Error(result.Message || result.message || "Tool failed.");
      }
      var dataJson = result.DataJson || result.dataJson || "";
      var parsed = tryParseJson(dataJson);
      if (!parsed.ok) {
        throw new Error("Tool returned no chart artifact.");
      }
      updateLocalMessageArtifact(context.messageId, dataJson);
      await send("updateMessageActivityData", { chatId: state.activeChatId, messageId: context.messageId, dataJson: dataJson });
      renderMessages();
    } catch (error) {
      log("Chart refresh failed: " + (error.detail || error.message));
    } finally {
      button.disabled = false;
      button.textContent = "Обновить";
    }
  }

  window.tryRenderChartArtifact = renderChartArtifact;
}());
