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

  function taskListFromArtifact(artifact) {
    if (!artifact || String(value(artifact, "Kind", "kind", "")).toLowerCase() !== "task_list") return null;
    var plan = parseJson(value(artifact, "InlineText", "inlineText", ""));
    if (!plan || !Array.isArray(plan.steps || plan.Steps)) return null;
    return {
      artifactId: value(artifact, "Id", "id", ""),
      revision: Number(value(artifact, "Revision", "revision", 1) || 1),
      id: plan.id || plan.Id || "",
      goal: plan.goal || plan.Goal || "Текущая задача",
      status: plan.status || plan.Status || "active",
      steps: plan.steps || plan.Steps || []
    };
  }

  function persistedActiveTaskList() {
    var activeId = state.activeTaskListArtifactId || "";
    if (!activeId) return null;
    var artifacts = state.artifacts || [];
    for (var i = artifacts.length - 1; i >= 0; i -= 1) {
      if (value(artifacts[i], "Id", "id", "") === activeId) return taskListFromArtifact(artifacts[i]);
    }
    return null;
  }

  function taskListFromToolActivity(activity) {
    var data = parseJson(typeof activityDataJson === "function" ? activityDataJson(activity) : "");
    var plan = data && (data.taskList || data.TaskList);
    if (!plan || !Array.isArray(plan.steps || plan.Steps)) return null;
    return {
      artifactId: data.artifactId || data.ArtifactId || "",
      revision: Number(data.revision || data.Revision || 1),
      id: plan.id || plan.Id || "",
      goal: plan.goal || plan.Goal || "Текущая задача",
      status: plan.status || plan.Status || "active",
      steps: plan.steps || plan.Steps || []
    };
  }

  function applyLiveTaskListActivity(current, activity) {
    if (!activity) return current;
    var toolId = typeof activityToolId === "function" ? activityToolId(activity) : "";
    var status = typeof activityStatus === "function" ? activityStatus(activity) : "";
    if (status === "completed" && toolId === "common.task_list_set") {
      var projected = taskListFromToolActivity(activity);
      current = projected && String(value(projected, "Status", "status", "active")).toLowerCase() === "active"
        ? projected : null;
    }
    var children = typeof activityChildren === "function" ? activityChildren(activity) : [];
    children.forEach(function (child) { current = applyLiveTaskListActivity(current, child); });
    return current;
  }

  function activeTaskList() {
    var current = persistedActiveTaskList();
    (state.liveAgentRun || []).forEach(function (activity) {
      current = applyLiveTaskListActivity(current, activity);
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
    var plan = activeTaskList();
    if (!plan || !plan.steps.length) {
      dock.replaceChildren();
      dock.classList.add("hidden");
      return;
    }

    var info = planInfo(plan);
    var expansionKey = plan.artifactId || plan.id;
    var approvalPending = typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity();
    var details = document.createElement("details");
    details.className = "agent-plan-card status-" + info.status;
    details.open = !approvalPending && !!state.agentPlanExpanded[expansionKey];
    details.addEventListener("toggle", function () {
      state.agentPlanExpanded[expansionKey] = details.open;
      if (details.open && typeof window.setChatResourcePopoverOpen === "function") window.setChatResourcePopoverOpen(false);
    });

    var summary = document.createElement("summary");
    summary.className = "agent-plan-summary";
    summary.title = plan.goal;
    summary.setAttribute("aria-label", "Задачи: выполнено " + info.completed + " из " + info.total + ". " + plan.goal);
    var icon = document.createElement("span");
    icon.className = "agent-plan-icon";
    icon.setAttribute("aria-hidden", "true");
    icon.innerHTML = "<svg viewBox=\"0 0 24 24\"><rect x=\"4\" y=\"3\" width=\"16\" height=\"18\" rx=\"2\"/><path d=\"m8 9 1.5 1.5L12 8\"/><path d=\"M14 9h3\"/><path d=\"m8 15 1.5 1.5L12 14\"/><path d=\"M14 15h3\"/></svg>";
    var label = document.createElement("span");
    label.className = "agent-plan-label";
    label.textContent = "Задачи";
    var count = document.createElement("span");
    count.className = "agent-plan-count";
    count.textContent = info.completed + "/" + info.total;
    var caret = document.createElement("span");
    caret.className = "agent-plan-caret";
    caret.setAttribute("aria-hidden", "true");
    summary.appendChild(icon);
    summary.appendChild(label);
    summary.appendChild(count);
    summary.appendChild(caret);
    details.appendChild(summary);

    var popover = document.createElement("div");
    popover.className = "agent-plan-popover";
    var head = document.createElement("div");
    head.className = "agent-plan-popover-head";
    var goal = document.createElement("strong");
    goal.textContent = plan.goal;
    var current = document.createElement("span");
    current.textContent = info.current ? stepValue(info.current, "Text", "text", "") : "Задачи выполнены";
    head.appendChild(goal);
    head.appendChild(current);
    popover.appendChild(head);
    popover.appendChild(renderSteps(plan));
    details.appendChild(popover);
    dock.replaceChildren(details);
    dock.classList.remove("hidden");
  }

  function setAgentPlanDockOpen(open) {
    var dock = $("agentPlanDock");
    var details = dock && dock.querySelector(".agent-plan-card");
    if (details) details.open = !!open;
  }

  document.addEventListener("pointerdown", function (event) {
    var dock = $("agentPlanDock");
    var details = dock && dock.querySelector(".agent-plan-card");
    if (details && details.open && !dock.contains(event.target)) details.open = false;
  });

  document.addEventListener("keydown", function (event) {
    if (event.key !== "Escape") return;
    var dock = $("agentPlanDock");
    var details = dock && dock.querySelector(".agent-plan-card");
    if (!details || !details.open) return;
    details.open = false;
    var summary = details.querySelector("summary");
    if (summary) summary.focus();
  });

  window.renderAgentPlanDock = renderAgentPlanDock;
  window.setAgentPlanDockOpen = setAgentPlanDockOpen;
}());
