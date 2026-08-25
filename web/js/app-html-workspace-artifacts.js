(function () {
  "use strict";

  function prop(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function artifactId(artifact) {
    return prop(artifact, "Id", "id", "");
  }

  function artifactKind(artifact) {
    return String(prop(artifact, "Kind", "kind", "file") || "file").toLowerCase();
  }

  function artifactRevision(artifact) {
    return Number(prop(artifact, "Revision", "revision", 1) || 1);
  }

  function artifactInlineText(artifact) {
    return prop(artifact, "InlineText", "inlineText", "") || "";
  }

  function planStableId(artifact) {
    var plan = null;
    try { plan = JSON.parse(artifactInlineText(artifact)); } catch (ignore) {}
    return plan && (plan.id || plan.Id) || artifactId(artifact);
  }

  function storedPlanId(artifact) {
    try {
      var metadata = JSON.parse(prop(artifact, "MetadataJson", "metadataJson", "{}") || "{}");
      if (metadata.planId || metadata.PlanId) return metadata.planId || metadata.PlanId;
    } catch (ignore) {}
    return planStableId(artifact);
  }

  function typeLabel(kind) {
    var labels = { attachment: "Вложение", image: "Изображение", file: "Файл", markdown: "Markdown", chart: "Диаграмма", compaction: "Checkpoint", tool_result: "Результат" };
    return labels[kind] || kind;
  }

  function planSummary(artifact) {
    var plan = null;
    try { plan = JSON.parse(artifactInlineText(artifact)); } catch (ignore) {}
    var steps = plan && (plan.steps || plan.Steps) || [];
    var completed = steps.filter(function (step) {
      return String(step.status || step.Status || "pending") === "completed";
    }).length;
    return completed + "/" + steps.length;
  }

  function planStatusLabel(status) {
    var labels = { pending: "Ожидает", in_progress: "В работе", completed: "Готово", blocked: "Заблокирован", cancelled: "Отменён" };
    return labels[status] || status;
  }

  function renderDetail(root, selected, editorValue) {
    root.replaceChildren();
    if (selected.type === "plan") {
      var plan = null;
      try { plan = JSON.parse(editorValue || ""); }
      catch (error) {
        var invalid = document.createElement("div");
        invalid.className = "artifact-detail-error";
        invalid.textContent = "Некорректный JSON: " + error.message;
        root.appendChild(invalid);
        return;
      }
      var goal = document.createElement("h2");
      goal.textContent = plan.goal || plan.Goal || "План без цели";
      root.appendChild(goal);
      var steps = plan.steps || plan.Steps || [];
      var summary = document.createElement("div");
      summary.className = "artifact-plan-summary";
      summary.textContent = steps.filter(function (step) {
        return String(step.status || step.Status || "pending") === "completed";
      }).length + " из " + steps.length + " шагов выполнено";
      root.appendChild(summary);
      var list = document.createElement("ol");
      list.className = "artifact-plan-steps";
      steps.forEach(function (step) {
        var status = String(step.status || step.Status || "pending");
        var row = document.createElement("li");
        row.className = "status-" + status;
        var mark = document.createElement("span");
        mark.className = "artifact-plan-mark";
        mark.textContent = status === "completed" ? "✓" : (status === "in_progress" ? "•" : (status === "blocked" ? "!" : ""));
        var text = document.createElement("span");
        text.textContent = step.text || step.Text || step.id || step.Id || "Шаг";
        var badge = document.createElement("em");
        badge.textContent = planStatusLabel(status);
        row.appendChild(mark);
        row.appendChild(text);
        row.appendChild(badge);
        list.appendChild(row);
      });
      root.appendChild(list);
      return;
    }

    var metadata = document.createElement("dl");
    metadata.className = "artifact-metadata";
    [
      ["Тип", typeLabel(artifactKind(selected.item))],
      ["Формат", prop(selected.item, "MimeType", "mimeType", "—") || "—"],
      ["Путь", prop(selected.item, "RelativePath", "relativePath", "—") || "—"],
      ["Версия", String(artifactRevision(selected.item))]
    ].forEach(function (pair) {
      var term = document.createElement("dt");
      var value = document.createElement("dd");
      term.textContent = pair[0];
      value.textContent = pair[1];
      metadata.appendChild(term);
      metadata.appendChild(value);
    });
    root.appendChild(metadata);
    var content = artifactInlineText(selected.item) || prop(selected.item, "MetadataJson", "metadataJson", "");
    if (content) {
      var pre = document.createElement("pre");
      try { pre.textContent = JSON.stringify(JSON.parse(content), null, 2); }
      catch (error) { pre.textContent = content; }
      root.appendChild(pre);
    }
  }

  function validatePlanDraft(artifact) {
    var plan;
    try { plan = JSON.parse(artifactInlineText(artifact)); }
    catch (error) { throw new Error("Некорректный JSON плана: " + error.message); }
    if (!plan || Array.isArray(plan) || typeof plan !== "object") throw new Error("План должен быть JSON-объектом.");
    var currentId = storedPlanId(artifact);
    var id = String(plan.id || plan.Id || "").trim();
    if (!id || id !== currentId) throw new Error("ID плана нельзя изменять.");
    var goal = String(plan.goal || plan.Goal || "").trim();
    if (!goal || goal.length > 500) throw new Error("Цель плана должна содержать от 1 до 500 символов.");
    var steps = plan.steps || plan.Steps;
    if (!Array.isArray(steps) || !steps.length || steps.length > 32) throw new Error("План должен содержать от 1 до 32 шагов.");
    var ids = {};
    var statuses = ["pending", "in_progress", "completed", "blocked", "cancelled"];
    steps = steps.map(function (step) {
      if (!step || Array.isArray(step) || typeof step !== "object") throw new Error("Каждый шаг должен быть объектом.");
      var stepId = String(step.id || step.Id || "").trim();
      var text = String(step.text || step.Text || "").trim();
      var status = String(step.status || step.Status || "pending").toLowerCase();
      if (!stepId || /\s/.test(stepId) || stepId.length > 80) throw new Error("У каждого шага нужен ID без пробелов длиной до 80 символов.");
      if (ids[stepId.toLowerCase()]) throw new Error("Повторяется ID шага: " + stepId);
      if (!text || text.length > 500) throw new Error("Описание каждого шага должно содержать от 1 до 500 символов.");
      if (statuses.indexOf(status) < 0) throw new Error("Неизвестный статус шага: " + status);
      ids[stepId.toLowerCase()] = true;
      return { id: stepId, text: text, status: status };
    });
    return { id: id, goal: goal, steps: steps };
  }

  window.RNAssistantHtmlWorkspaceArtifacts = {
    planSummary: planSummary,
    renderDetail: renderDetail,
    typeLabel: typeLabel,
    validatePlanDraft: validatePlanDraft
  };
}());
