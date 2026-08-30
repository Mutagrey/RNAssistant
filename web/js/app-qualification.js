(function () {
  "use strict";

  var MAX_REPORT_CHARS = 786432;
  var center = {
    catalog: null,
    run: null,
    selectedPackId: "",
    activeChatId: "",
    activeChatRun: null,
    activeProbeVersion: 0,
    busy: false,
    error: "",
    previousFocus: null
  };

  function value(source, name, fallback) {
    source = source || {};
    if (source[name] !== undefined) return source[name];
    var pascal = name.charAt(0).toUpperCase() + name.slice(1);
    return source[pascal] !== undefined ? source[pascal] : fallback;
  }

  function arrayValue(source, name) {
    var result = value(source, name, []);
    return Array.isArray(result) ? result : [];
  }

  function selectedPack() {
    var packs = center.catalog ? arrayValue(center.catalog, "packs") : [];
    return packs.filter(function (pack) {
      return String(value(pack, "id", "")) === String(center.selectedPackId || "");
    })[0] || null;
  }

  function runPack() {
    if (!center.run) return null;
    var id = value(center.run, "packId", "");
    var packs = center.catalog ? arrayValue(center.catalog, "packs") : [];
    return packs.filter(function (pack) { return String(value(pack, "id", "")) === String(id); })[0] || null;
  }

  function isTerminal(status) {
    return ["passed", "failed", "blocked", "cancelled"].indexOf(String(status || "")) >= 0;
  }

  function statusLabel(status) {
    var labels = {
      ready: "Готов",
      running: "Выполняется",
      awaiting_user: "Ожидает вас",
      verifying: "Проверка evidence",
      passed: "Пройден",
      failed: "Не пройден",
      blocked: "Заблокирован",
      cancelled: "Отменён"
    };
    return labels[String(status || "ready")] || String(status || "Неизвестно");
  }

  function outcomeLabel(outcome) {
    var labels = {
      not_run: "Не запускался",
      running: "Выполняется",
      awaiting_user: "Ожидает пользователя",
      passed: "Пройден",
      failed: "Не пройден",
      blocked: "Заблокирован",
      cancelled: "Отменён",
      unknown: "Эффект неизвестен"
    };
    return labels[String(outcome || "not_run")] || String(outcome || "Неизвестно");
  }

  function setHidden(id, hidden) {
    var node = $(id);
    if (node) node.classList.toggle("hidden", !!hidden);
  }

  function setStatus(message, error) {
    var node = $("qualificationCenterStatus");
    if (!node) return;
    node.textContent = String(message || "");
    node.classList.toggle("is-error", !!error);
  }

  function clearEvidenceViewers() {
    if (!window.RNAssistantViewerRegistry) return;
    Array.prototype.slice.call(document.querySelectorAll(".qualification-evidence-json")).forEach(function (host) {
      window.RNAssistantViewerRegistry.unmount(host);
    });
  }

  function renderPackList() {
    var root = $("qualificationPackList");
    if (!root) return;
    root.replaceChildren();
    var packs = center.catalog ? arrayValue(center.catalog, "packs") : [];
    packs.forEach(function (pack) {
      var id = String(value(pack, "id", ""));
      var available = !!value(pack, "available", false);
      var button = document.createElement("button");
      button.type = "button";
      button.className = "qualification-pack";
      button.classList.toggle("is-selected", id === center.selectedPackId);
      button.disabled = !available || center.busy;
      var title = document.createElement("strong");
      title.textContent = String(value(pack, "title", id));
      var meta = document.createElement("span");
      meta.textContent = String(value(pack, "suite", "")) + " · rev " +
        String(value(pack, "revision", "")) + (available ? "" : " · недоступен");
      button.appendChild(title);
      button.appendChild(meta);
      button.addEventListener("click", function () {
        center.selectedPackId = id;
        renderQualificationCenter();
      });
      root.appendChild(button);
    });
    if (!packs.length) {
      var empty = document.createElement("div");
      empty.className = "qualification-report-status";
      empty.textContent = "Для этого host и suite нет встроенных пакетов.";
      root.appendChild(empty);
    }
  }

  function appendProvenance(root, label, data) {
    if (data === null || data === undefined || data === "") return;
    var item = document.createElement("span");
    item.textContent = label + ": " + String(data);
    root.appendChild(item);
  }

  function packStep(pack, stepId) {
    return arrayValue(pack, "steps").filter(function (step) {
      return String(value(step, "id", "")) === String(stepId || "");
    })[0] || null;
  }

  function mountEvidence(host, text, truncated) {
    if (text === null || text === undefined) return;
    if (window.RNAssistantViewerRegistry && window.RNAssistantViewerRegistry.has("json")) {
      window.RNAssistantViewerRegistry.mount("json", host, {
        text: String(text),
        completeness: truncated ? "preview" : "full",
        onCopy: window.copyTextResult
      });
      return;
    }
    var fallback = document.createElement("pre");
    fallback.textContent = String(text);
    host.appendChild(fallback);
  }

  function appendEvidence(body, label, text, truncated) {
    if (text === null || text === undefined) return;
    var heading = document.createElement("div");
    heading.className = "qualification-evidence-label";
    heading.textContent = label + (truncated ? " · preview" : "");
    var host = document.createElement("div");
    host.className = "qualification-evidence-json";
    body.appendChild(heading);
    body.appendChild(host);
    mountEvidence(host, text, truncated);
  }

  function renderSteps(pack) {
    var root = $("qualificationSteps");
    if (!root) return;
    clearEvidenceViewers();
    root.replaceChildren();
    var manifestSteps = pack ? arrayValue(pack, "steps") : [];
    var resultSteps = center.run ? arrayValue(center.run, "steps") : [];
    var results = Object.create(null);
    resultSteps.forEach(function (step) { results[String(value(step, "stepId", ""))] = step; });
    manifestSteps.forEach(function (step, index) {
      var id = String(value(step, "id", ""));
      var result = results[id] || {};
      var outcome = String(value(result, "outcome", "not_run"));
      var details = document.createElement("details");
      details.className = "qualification-step";
      if (outcome === "failed" || outcome === "blocked" || outcome === "unknown" ||
          id === value(center.run, "currentStepId", "")) details.open = true;
      var summary = document.createElement("summary");
      var number = document.createElement("span");
      number.className = "qualification-step-index";
      number.textContent = String(index + 1);
      var title = document.createElement("span");
      title.className = "qualification-step-title";
      title.textContent = String(value(step, "title", id));
      var stateNode = document.createElement("span");
      stateNode.className = "qualification-step-outcome";
      stateNode.textContent = outcomeLabel(outcome);
      summary.appendChild(number);
      summary.appendChild(title);
      summary.appendChild(stateNode);
      details.appendChild(summary);
      var body = document.createElement("div");
      body.className = "qualification-step-body";
      var message = value(result, "message", "");
      var code = value(result, "code", "");
      var evidence = value(result, "evidenceStrength", "none");
      var text = [code ? "code: " + code : "", evidence !== "none" ? "evidence: " + evidence : "", message]
        .filter(Boolean).join(" · ");
      if (text) {
        var description = document.createElement("p");
        description.textContent = text;
        body.appendChild(description);
      }
      appendEvidence(body, "Ожидалось", value(result, "expectedJson", null),
        !!value(result, "expectedTruncated", false));
      appendEvidence(body, "Получено", value(result, "actualJson", null),
        !!value(result, "actualTruncated", false));
      details.appendChild(body);
      root.appendChild(details);
    });
  }

  function renderInstruction(pack, runStatus) {
    var node = $("qualificationUserInstruction");
    if (!node) return;
    var current = center.run ? packStep(pack, value(center.run, "currentStepId", "")) : null;
    var key = value(current, "instructionKey", "");
    var visible = runStatus === "awaiting_user" && !!current;
    node.classList.toggle("hidden", !visible);
    if (!visible) {
      node.textContent = "";
      return;
    }
    node.textContent = key === "qualification.shell.acknowledge"
      ? "Проверьте, что список шагов виден и этот блок раскрывает явное действие. Затем нажмите «Подтвердить и продолжить»."
      : "Выполните указанный ручной шаг и подтвердите продолжение.";
  }

  function renderActions(pack, runStatus) {
    var available = !!(pack && value(pack, "available", false));
    var hasRun = !!center.run;
    var resumable = hasRun && !!value(center.run, "canResume", false);
    var terminal = hasRun && isTerminal(runStatus);
    setHidden("startQualificationButton", hasRun);
    setHidden("continueQualificationButton", !(resumable && runStatus === "awaiting_user"));
    setHidden("cancelQualificationButton", !(resumable && runStatus === "awaiting_user"));
    setHidden("repeatQualificationButton", !terminal);
    setHidden("openQualificationJournalButton", !hasRun);
    setHidden("copyQualificationReportButton", !hasRun);
    $("startQualificationButton").disabled = center.busy || !available;
    $("continueQualificationButton").disabled = center.busy;
    $("cancelQualificationButton").disabled = center.busy;
    $("repeatQualificationButton").disabled = center.busy || !available;
    $("openQualificationJournalButton").disabled = center.busy;
    $("copyQualificationReportButton").disabled = center.busy;
  }

  function renderQualificationCenter() {
    var pack = runPack() || selectedPack();
    if (pack) center.selectedPackId = String(value(pack, "id", ""));
    renderPackList();
    var runStatus = center.run ? String(value(center.run, "status", "ready")) : "ready";
    $("qualificationRunTitle").textContent = pack ? String(value(pack, "title", "")) : "Выберите пакет";
    $("qualificationRunDescription").textContent = pack ? String(value(pack, "description", "")) : "";
    var statusNode = $("qualificationRunStatus");
    statusNode.className = "qualification-run-status is-" + runStatus;
    statusNode.textContent = statusLabel(runStatus);

    var provenance = $("qualificationProvenance");
    provenance.replaceChildren();
    if (pack) {
      appendProvenance(provenance, "pack", value(pack, "id", ""));
      appendProvenance(provenance, "revision", value(pack, "revision", ""));
      appendProvenance(provenance, "sha256", String(value(pack, "sha256", "")).slice(0, 16));
      appendProvenance(provenance, "policy", value(pack, "workspacePolicy", ""));
    }
    if (center.run) {
      appendProvenance(provenance, "host", value(center.run, "host", ""));
      appendProvenance(provenance, "product", value(center.run, "productVersion", ""));
      appendProvenance(provenance, "commit", value(center.run, "buildCommit", ""));
      appendProvenance(provenance, "run", value(center.run, "runId", ""));
    }
    renderInstruction(pack, runStatus);
    renderSteps(pack);
    renderActions(pack, runStatus);
    if (center.error) setStatus(center.error, true);
    else if (center.busy) setStatus("Выполняется записываемый шаг…", false);
    else if (center.run && value(center.run, "reportTruncated", false))
      setStatus("Report preview ограничен; полные evidence остаются в event stream/CAS.", false);
    else if (center.run) setStatus("Показано состояние из durable qualification events.", false);
    else setStatus("Выберите доступный пакет. Запуск создаст отдельный qualification-чат.", false);
  }

  function applyQualificationResponse(response) {
    response = response || {};
    var chat = value(response, "chat", null);
    center.run = value(response, "run", null);
    center.activeChatRun = center.run;
    center.activeChatId = chat ? String(value(chat, "activeChatId", state.activeChatId || "")) :
      String(state.activeChatId || "");
    if (chat && typeof applyChatState === "function") applyChatState(chat);
    if (center.run) center.selectedPackId = String(value(center.run, "packId", center.selectedPackId));
  }

  function activeQualificationRun() {
    return center.activeChatId && center.activeChatId === String(state.activeChatId || "")
      ? center.activeChatRun : null;
  }

  function refreshActiveQualificationState() {
    if (state.bridgeUnavailable || !state.activeChatId) {
      center.activeChatId = String(state.activeChatId || "");
      center.activeChatRun = null;
      return Promise.resolve(null);
    }
    var chatId = String(state.activeChatId);
    var version = ++center.activeProbeVersion;
    return send("getQualificationRun", { chatId: chatId, runId: null }).then(function (response) {
      if (version !== center.activeProbeVersion || chatId !== String(state.activeChatId || "")) return null;
      center.activeChatId = chatId;
      center.activeChatRun = value(response, "run", null);
      if (typeof renderMessages === "function") renderMessages();
      if (typeof renderSendControls === "function") renderSendControls();
      return center.activeChatRun;
    }).catch(function () {
      if (version === center.activeProbeVersion && chatId === String(state.activeChatId || "")) {
        center.activeChatId = chatId;
        center.activeChatRun = null;
        if (typeof renderSendControls === "function") renderSendControls();
      }
      return null;
    });
  }

  function runRequest(promise) {
    center.busy = true;
    center.error = "";
    $("qualificationReportStatus").textContent = "";
    renderQualificationCenter();
    return promise.then(function (response) {
      applyQualificationResponse(response);
      return response;
    }).catch(function (error) {
      center.error = error && error.message ? error.message : "Qualification request failed.";
      throw error;
    }).finally(function () {
      center.busy = false;
      renderQualificationCenter();
    });
  }

  function loadQualificationCenter() {
    center.busy = true;
    center.error = "";
    renderQualificationCenter();
    return send("getQualificationCatalog", { chatId: state.activeChatId || null, suite: "quick" })
      .then(function (catalog) {
        center.catalog = catalog || null;
        var packs = center.catalog ? arrayValue(center.catalog, "packs") : [];
        if (!center.selectedPackId && packs.length) center.selectedPackId = String(value(packs[0], "id", ""));
        return send("getQualificationRun", { chatId: state.activeChatId || null, runId: null });
      })
      .then(function (response) { applyQualificationResponse(response); })
      .catch(function (error) {
        center.error = error && error.message ? error.message : "Qualification Center is unavailable.";
      })
      .finally(function () {
        center.busy = false;
        renderQualificationCenter();
      });
  }

  function openQualificationCenter() {
    var overlay = $("qualificationCenterOverlay");
    if (!overlay) return Promise.resolve();
    center.previousFocus = document.activeElement;
    overlay.classList.remove("hidden");
    overlay.setAttribute("aria-hidden", "false");
    $("closeQualificationCenterButton").focus();
    return loadQualificationCenter();
  }

  function closeQualificationCenter() {
    var overlay = $("qualificationCenterOverlay");
    if (!overlay) return;
    clearEvidenceViewers();
    overlay.classList.add("hidden");
    overlay.setAttribute("aria-hidden", "true");
    if (center.previousFocus && typeof center.previousFocus.focus === "function") center.previousFocus.focus();
    center.previousFocus = null;
  }

  function startQualification() {
    var pack = selectedPack();
    if (!pack || !value(pack, "available", false) || center.busy) return;
    var previousRunId = center.run ? value(center.run, "runId", null) : null;
    runRequest(send("startQualification", {
      chatId: state.activeChatId || null,
      packId: value(pack, "id", ""),
      previousRunId: previousRunId
    })).catch(function () {});
  }

  function advanceQualification(cancel) {
    if (!center.run || center.busy) return;
    var stepId = value(center.run, "currentStepId", null);
    runRequest(send("advanceQualification", {
      chatId: state.activeChatId || null,
      runId: value(center.run, "runId", ""),
      stepId: cancel ? null : stepId,
      acknowledged: !cancel,
      cancel: !!cancel,
      note: null
    })).catch(function () {});
  }

  function openJournal() {
    if (!center.run || typeof window.openRunJournal !== "function") return;
    var runId = value(center.run, "runId", "");
    var chatId = state.activeChatId || "";
    closeQualificationCenter();
    window.openRunJournal({ chatId: chatId, runId: runId });
  }

  function reportJson() {
    if (!center.run) return "";
    var report = JSON.stringify({
      schemaVersion: 1,
      pack: selectedPack(),
      run: center.run
    }, null, 2);
    if (report.length > MAX_REPORT_CHARS) throw new Error("Bounded qualification report exceeds the UI limit.");
    return report;
  }

  function copyReport() {
    var status = $("qualificationReportStatus");
    try {
      window.copyTextResult(reportJson()).then(function () {
        status.textContent = "Отчёт скопирован.";
      }).catch(function (error) {
        status.textContent = "Не удалось скопировать отчёт: " + error.message;
      });
    } catch (error) {
      status.textContent = error.message;
    }
  }

  function bindQualificationActions() {
    var open = $("openQualificationCenterButton");
    var close = $("closeQualificationCenterButton");
    var overlay = $("qualificationCenterOverlay");
    if (open) open.addEventListener("click", function () { openQualificationCenter(); });
    if (close) close.addEventListener("click", closeQualificationCenter);
    if (overlay) overlay.addEventListener("click", function (event) {
      if (event.target === overlay) closeQualificationCenter();
    });
    $("startQualificationButton").addEventListener("click", startQualification);
    $("continueQualificationButton").addEventListener("click", function () { advanceQualification(false); });
    $("cancelQualificationButton").addEventListener("click", function () { advanceQualification(true); });
    $("repeatQualificationButton").addEventListener("click", startQualification);
    $("openQualificationJournalButton").addEventListener("click", openJournal);
    $("copyQualificationReportButton").addEventListener("click", copyReport);
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && overlay && !overlay.classList.contains("hidden")) closeQualificationCenter();
    });
  }

  window.openQualificationCenter = openQualificationCenter;
  window.bindQualificationActions = bindQualificationActions;
  window.activeQualificationRun = activeQualificationRun;
  window.refreshActiveQualificationState = refreshActiveQualificationState;
  window.RNAssistantQualificationCenter = {
    render: renderQualificationCenter,
    statusLabel: statusLabel,
    outcomeLabel: outcomeLabel,
    reportJson: reportJson,
    state: center
  };
}());
