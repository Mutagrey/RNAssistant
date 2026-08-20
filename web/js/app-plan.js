(function () {
  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
  }

  function parseJson(value) {
    if (!value) return null;
    if (typeof value === "object") return value;
    try {
      return JSON.parse(String(value));
    } catch (error) {
      return null;
    }
  }

  function planFromArtifact(artifact) {
    if (!artifact || String(value(artifact, "Kind", "kind", "")).toLowerCase() !== "plan") return null;
    var plan = parseJson(value(artifact, "InlineText", "inlineText", ""));
    if (!plan || !Array.isArray(plan.steps || plan.Steps)) return null;
    return {
      artifactId: value(artifact, "Id", "id", ""),
      revision: Number(value(artifact, "Revision", "revision", 1) || 1),
      id: plan.id || plan.Id || "",
      goal: plan.goal || plan.Goal || "План задачи",
      steps: plan.steps || plan.Steps || []
    };
  }

  function persistedActivePlan() {
    var activeId = state.activePlanArtifactId || "";
    if (!activeId) return null;
    var artifacts = state.artifacts || [];
    for (var i = artifacts.length - 1; i >= 0; i -= 1) {
      if (value(artifacts[i], "Id", "id", "") === activeId) return planFromArtifact(artifacts[i]);
    }
    return null;
  }

  function planFromToolActivity(activity) {
    var data = parseJson(typeof activityDataJson === "function" ? activityDataJson(activity) : "");
    var plan = data && (data.plan || data.Plan);
    if (!plan || !Array.isArray(plan.steps || plan.Steps)) return null;
    return {
      artifactId: data.artifactId || data.ArtifactId || "",
      revision: Number(data.revision || data.Revision || 1),
      id: plan.id || plan.Id || "",
      goal: plan.goal || plan.Goal || "План задачи",
      steps: plan.steps || plan.Steps || []
    };
  }

  function applyLivePlanActivity(current, activity) {
    if (!activity) return current;
    var toolId = typeof activityToolId === "function" ? activityToolId(activity) : "";
    var status = typeof activityStatus === "function" ? activityStatus(activity) : "";
    if (status === "completed" && (toolId === "common.plan_create" || toolId === "common.plan_update")) {
      current = planFromToolActivity(activity) || current;
    } else if (status === "completed" && toolId === "common.plan_delete") {
      var deleted = parseJson(typeof activityDataJson === "function" ? activityDataJson(activity) : "");
      var deletedId = deleted && (deleted.id || deleted.Id);
      if (!current || !deletedId || current.id === deletedId) current = null;
    }
    var children = typeof activityChildren === "function" ? activityChildren(activity) : [];
    children.forEach(function (child) { current = applyLivePlanActivity(current, child); });
    return current;
  }

  function activePlan() {
    var current = persistedActivePlan();
    (state.liveAgentRun || []).forEach(function (activity) {
      current = applyLivePlanActivity(current, activity);
    });
    return current;
  }

  function stepValue(step, pascal, camel, fallback) {
    return value(step, pascal, camel, fallback);
  }

  function status(step) {
    return String(stepValue(step, "Status", "status", "pending") || "pending").toLowerCase();
  }

  function statusMark(stepStatus) {
    var marks = { completed: "✓", in_progress: "•", blocked: "!", cancelled: "–", pending: "" };
    return marks[stepStatus] || "";
  }

  function planInfo(plan) {
    var steps = plan && plan.steps ? plan.steps : [];
    var completed = steps.filter(function (step) { return status(step) === "completed"; }).length;
    var current = steps.filter(function (step) { return status(step) === "in_progress"; })[0] ||
      steps.filter(function (step) { return status(step) === "blocked"; })[0] ||
      steps.filter(function (step) { return status(step) === "pending"; })[0] || null;
    var planStatus = steps.length && completed === steps.length ? "completed" :
      (steps.some(function (step) { return status(step) === "blocked"; }) ? "blocked" :
        (steps.some(function (step) { return status(step) === "in_progress"; }) ? "running" : "planned"));
    return { completed: completed, total: steps.length, current: current, status: planStatus };
  }

  function renderSteps(plan) {
    var list = document.createElement("ol");
    list.className = "agent-plan-list";
    (plan.steps || []).forEach(function (step) {
      var stepStatus = status(step);
      var row = document.createElement("li");
      row.className = "agent-plan-step status-" + stepStatus;
      var mark = document.createElement("span");
      mark.className = "agent-plan-step-mark";
      mark.textContent = statusMark(stepStatus);
      mark.setAttribute("aria-hidden", "true");
      var text = document.createElement("span");
      text.className = "agent-plan-step-text";
      text.textContent = stepValue(step, "Text", "text", stepValue(step, "Id", "id", "Шаг"));
      row.appendChild(mark);
      row.appendChild(text);
      list.appendChild(row);
    });
    return list;
  }

  function renderAgentPlanDock() {
    var dock = $("agentPlanDock");
    if (!dock) return;
    var plan = activePlan();
    if (!plan || !plan.steps.length) {
      dock.replaceChildren();
      dock.classList.add("hidden");
      return;
    }

    var info = planInfo(plan);
    var expansionKey = plan.artifactId || plan.id;
    var details = document.createElement("details");
    details.className = "agent-plan-card status-" + info.status;
    details.open = state.agentPlanExpanded[expansionKey] !== undefined
      ? !!state.agentPlanExpanded[expansionKey]
      : info.status !== "completed";
    details.addEventListener("toggle", function () { state.agentPlanExpanded[expansionKey] = details.open; });

    var summary = document.createElement("summary");
    summary.className = "agent-plan-summary";
    var count = document.createElement("span");
    count.className = "agent-plan-count";
    count.textContent = info.completed + "/" + info.total;
    var copy = document.createElement("span");
    copy.className = "agent-plan-copy";
    var goal = document.createElement("strong");
    goal.textContent = plan.goal;
    var current = document.createElement("span");
    current.textContent = info.current ? stepValue(info.current, "Text", "text", "") : "План выполнен";
    copy.appendChild(goal);
    copy.appendChild(current);
    var caret = document.createElement("span");
    caret.className = "agent-plan-caret";
    caret.textContent = "›";
    caret.setAttribute("aria-hidden", "true");
    summary.appendChild(count);
    summary.appendChild(copy);
    summary.appendChild(caret);
    details.appendChild(summary);
    details.appendChild(renderSteps(plan));
    dock.replaceChildren(details);
    dock.classList.remove("hidden");
  }

  window.renderAgentPlanDock = renderAgentPlanDock;
}());
