var skillReferenceLoadSequence = 0;
var skillReferenceRead = null;
var skillReferenceReadPending = 0;
var skillLibraryContractVersion = 1;
var skillLibraryContractType = "rnassistant.skillLibrary";
var skillLibraryMutationRequestType = "rnassistant.skillLibraryMutationRequest";
var skillLibraryMutationResultType = "rnassistant.skillLibraryMutationResult";
var skillReferenceRequestType = "rnassistant.skillReferenceRequest";
var skillReferenceResultType = "rnassistant.skillReferenceResult";

function skillReferenceFromContract(reference) {
  if (!reference || typeof reference.path !== "string" ||
    typeof reference.revision !== "string" ||
    typeof reference.byteLength !== "number" || reference.byteLength < 0) {
    throw new Error("Некорректный typed reference навыка.");
  }
  return {
    Path: reference.path,
    ByteLength: reference.byteLength,
    Revision: reference.revision
  };
}

function skillFromContract(skill) {
  if (!skill || typeof skill.revision !== "string" || !skill.revision ||
    typeof skill.id !== "string" || !skill.id ||
    typeof skill.host !== "string" || typeof skill.name !== "string" ||
    typeof skill.description !== "string" || typeof skill.version !== "string" ||
    typeof skill.bodyMarkdown !== "string" || typeof skill.enabled !== "boolean" ||
    typeof skill.builtIn !== "boolean" || !Array.isArray(skill.references)) {
    throw new Error("Некорректный typed package навыка.");
  }
  return {
    Id: skill.id,
    Host: skill.host,
    Name: skill.name,
    Description: skill.description,
    Version: skill.version,
    BodyMarkdown: skill.bodyMarkdown,
    Enabled: skill.enabled,
    BuiltIn: skill.builtIn,
    Revision: skill.revision,
    References: skill.references.map(skillReferenceFromContract),
    _baseId: skill.builtIn ? "" : skill.id,
    _baseRevision: skill.builtIn ? "" : skill.revision
  };
}

function skillLibraryItemsFromContract(contract) {
  if (!contract || contract.type !== skillLibraryContractType ||
    contract.contractVersion !== skillLibraryContractVersion ||
    !Array.isArray(contract.skills)) {
    throw new Error("Некорректный typed Skill Library contract.");
  }
  return contract.skills.map(skillFromContract);
}

function requireSkillMutationResult(result) {
  if (!result || result.type !== "rnassistant.skillMutationResult" ||
    result.contractVersion !== skillLibraryContractVersion ||
    ["ok", "error", "unknown"].indexOf(result.status) < 0 ||
    typeof result.message !== "string" || typeof result.dispatch !== "string" ||
    typeof result.effect !== "string") {
    throw new Error("Некорректный typed результат изменения навыка.");
  }
  return result;
}

function skillLibraryMutationFromContract(response) {
  if (!response || response.type !== skillLibraryMutationResultType ||
    response.contractVersion !== skillLibraryContractVersion ||
    !Array.isArray(response.results)) {
    throw new Error("Некорректный typed результат Skill Library.");
  }
  var results = response.results.map(requireSkillMutationResult);
  return {
    skills: skillLibraryItemsFromContract(response.library),
    results: results,
    failure: results.filter(function (result) { return result.status !== "ok"; })[0] || null
  };
}

function skillReferenceFromResponse(response, expectedOperation) {
  if (!response || response.type !== skillReferenceResultType ||
    response.contractVersion !== skillLibraryContractVersion ||
    typeof response.path !== "string") {
    throw new Error("Некорректный typed результат reference навыка.");
  }
  var result = requireSkillMutationResult(response.result);
  var expectedOperations = Array.isArray(expectedOperation)
    ? expectedOperation : [expectedOperation];
  if (expectedOperations.indexOf(result.operation) < 0) {
    throw new Error("Операция typed результата reference не совпадает с запросом.");
  }
  if (result.status !== "ok") {
    var failure = new Error(result.message || "Операция reference не выполнена.");
    failure.detail = result.message;
    failure.code = result.code || "skill_reference_failed";
    throw failure;
  }
  return {
    result: result,
    skill: skillFromContract(response.skill),
    path: response.path,
    content: response.content === null || response.content === undefined
      ? null : String(response.content),
    deleted: response.deleted === true,
    reference: response.reference ? skillReferenceFromContract(response.reference) : null
  };
}

function skillEditorValue() {
  return typeof getCodeEditorValue === "function"
    ? getCodeEditorValue("skillBodyInput")
    : (($("skillBodyInput") && $("skillBodyInput").value) || "");
}

function setSkillEditorValue(value) {
  if (typeof setCodeEditorValue === "function") setCodeEditorValue("skillBodyInput", value || "");
  else if ($("skillBodyInput")) $("skillBodyInput").value = value || "";
}

function skillReferencePath(reference) {
  return (reference && reference.Path) || "";
}

function ensureSkillReferenceState(skill) {
  if (!skill) return;
  if (!skill.References) skill.References = [];
  if (!skill._referenceDrafts) skill._referenceDrafts = {};
  if (!skill._referenceLoaded) skill._referenceLoaded = {};
  if (!skill._referenceLoading) skill._referenceLoading = {};
  if (!skill._referenceLoadTokens) skill._referenceLoadTokens = {};
  if (!skill._referenceDirty) skill._referenceDirty = {};
  if (!skill._referenceConflicts) skill._referenceConflicts = {};
  if (!skill._selectedReferencePath) skill._selectedReferencePath = "";
}

function selectedSkillReferencePath(skill) {
  ensureSkillReferenceState(skill);
  return skill ? (skill._selectedReferencePath || "") : "";
}

function writableSkillLibraryItems(skills) {
  return (skills || []).filter(function (skill) { return skill && !skill.BuiltIn; });
}

function skillLibraryComparable(skill) {
  return {
      Id: skill.Id || "",
      Host: skill.Host || "Common",
      Name: skill.Name || skill.Id || "",
      Description: skill.Description || "",
      Version: skill.Version || "1.0.0",
      BodyMarkdown: skill.BodyMarkdown || "",
      Enabled: skill.Enabled !== false
  };
}

function skillLibraryIdentity(skill) {
  var baseId = String(skill && skill._baseId || "").toLowerCase();
  return "id:" + (baseId || String(skill && skill.Id || "").toLowerCase());
}

function skillLibraryRecords(skills) {
  return writableSkillLibraryItems(skills).map(function (skill) {
    return {
      entity: skill,
      identity: skillLibraryIdentity(skill),
      id: String(skill.Id || "").toLowerCase(),
      baseId: skill._baseId || "",
      revision: skill._baseRevision || "",
      comparable: skillLibraryComparable(skill)
    };
  });
}

function skillLibrarySnapshot(skills) {
  return JSON.stringify(skillLibraryRecords(skills).map(function (item) { return item.comparable; }));
}

function skillRecordIndex(records) {
  var byIdentity = {};
  var byId = {};
  (records || []).forEach(function (item) {
    if (item.identity) byIdentity[item.identity] = item;
    if (item.id) byId[item.id] = item;
  });
  return { byIdentity: byIdentity, byId: byId };
}

function matchingSkillRecord(index, record) {
  return index.byIdentity[record.identity] || index.byId[record.id] || null;
}

function skillHasDirtyReferences(skill) {
  ensureSkillReferenceState(skill);
  return Object.keys(skill._referenceDirty).some(function (path) { return !!skill._referenceDirty[path]; });
}

function requireUnconflictedSkillReferences() {
  (state.skills || []).forEach(function (skill) {
    ensureSkillReferenceState(skill);
    if (Object.keys(skill._referenceConflicts).some(function (path) { return skill._referenceDirty[path] && skill._referenceConflicts[path]; }))
      throw new Error("Reference изменился после чтения. Обновите пакет и разрешите конфликт перед сохранением.");
  });
}

function skillRecordChanged(current, baseline) {
  return !baseline || skillHasDirtyReferences(current.entity) ||
    JSON.stringify(current.comparable) !== JSON.stringify(baseline.comparable);
}

function setSkillLibraryBaseline(skills) {
  state.skillLibraryBaselineItems = skillLibraryRecords(skills);
  state.skillLibraryBaseline = skillLibrarySnapshot(skills);
}

function reconcileSkillLibraryCatalog(serverSkills) {
  var currentRecords = skillLibraryRecords(state.skills);
  var currentIndex = skillRecordIndex(currentRecords);
  var baselineIndex = skillRecordIndex(state.skillLibraryBaselineItems || []);
  var used = [];
  var merged = [];
  (serverSkills || []).forEach(function (serverSkill) {
    if (!serverSkill || serverSkill.BuiltIn) {
      if (serverSkill) merged.push(serverSkill);
      return;
    }
    var serverRecord = skillLibraryRecords([serverSkill])[0];
    var current = matchingSkillRecord(currentIndex, serverRecord);
    var baseline = matchingSkillRecord(baselineIndex, serverRecord);
    if (!current && baseline) return;
    if (current) used.push(current.entity);
    merged.push(current && skillRecordChanged(current, baseline) ? current.entity : serverSkill);
  });
  currentRecords.forEach(function (current) {
    if (used.indexOf(current.entity) >= 0) return;
    var baseline = matchingSkillRecord(baselineIndex, current);
    if (skillRecordChanged(current, baseline)) merged.push(current.entity);
  });
  setSkillLibraryBaseline(serverSkills);
  state.skills = preserveSkillReferenceState(merged);
  updateSkillLibraryDirty();
  return state.skills;
}

function hasDirtySkillReference() {
  return (state.skills || []).some(function (skill) {
    return skillHasDirtyReferences(skill);
  });
}

function updateSkillLibraryDirty() {
  state.skillLibraryDirty = skillLibrarySnapshot(state.skills) !== state.skillLibraryBaseline || hasDirtySkillReference();
  updateSkillSaveButton();
}

function markSkillLibraryDirty() {
  syncSelectedSkillFromEditor();
  updateSkillLibraryDirty();
}

function acceptSkillLibraryState() {
  setSkillLibraryBaseline(state.skills);
  state.skillLibraryDirty = hasDirtySkillReference();
  updateSkillSaveButton();
}

function captureSelectedSkillResource(skill) {
  if (!skill) return;
  ensureSkillReferenceState(skill);
  var value = skillEditorValue();
  var path = selectedSkillReferencePath(skill);
  if (path) {
    if (!skill._referenceLoaded[path]) return;
    if (skill._referenceDrafts[path] !== value) {
      skill._referenceDirty[path] = true;
    }
    skill._referenceDrafts[path] = value;
    skill._referenceLoaded[path] = true;
  } else {
    skill.BodyMarkdown = value;
  }
}

function mergeSkillReferenceMetadata(skill, references) {
  if (!skill) return;
  ensureSkillReferenceState(skill);
  var server = (references || []).slice();
  skill.References.forEach(function (reference) {
    var path = skillReferencePath(reference);
    if (reference && reference.Pending && !server.some(function (item) {
      return skillReferencePath(item).toLowerCase() === path.toLowerCase();
    })) server.push(reference);
  });
  skill.References = server;
}

function preserveSkillReferenceState(skills) {
  cancelSkillReferenceRead();
  var transient = {};
  (state.skills || []).forEach(function (skill) {
    if (!skill || !skill.Id) return;
    ensureSkillReferenceState(skill);
    transient[String(skill.Id).toLowerCase()] = {
      selected: skill._selectedReferencePath || "",
      drafts: skill._referenceDrafts,
      loaded: skill._referenceLoaded,
      dirty: skill._referenceDirty,
      conflicts: skill._referenceConflicts,
      references: skill.References.slice(),
      pending: (skill.References || []).filter(function (item) { return !!item.Pending; })
    };
  });
  (skills || []).forEach(function (skill) {
    var saved = transient[String((skill && skill.Id) || "").toLowerCase()];
    ensureSkillReferenceState(skill);
    if (!saved) return;
    skill._selectedReferencePath = saved.selected;
    skill._referenceDrafts = saved.drafts;
    skill._referenceLoaded = saved.loaded;
    skill._referenceLoading = {};
    skill._referenceLoadTokens = {};
    skill._referenceDirty = saved.dirty;
    skill._referenceConflicts = saved.conflicts;
    Object.keys(saved.loaded).forEach(function (path) {
      var before = saved.references.find(function (item) { return skillReferencePath(item) === path; });
      var after = skill.References.find(function (item) { return skillReferencePath(item) === path; });
      if (before && before.Pending) return;
      if (!before || !after || before.Revision !== after.Revision) {
        if (saved.dirty[path]) skill._referenceConflicts[path] = true;
        else { delete skill._referenceLoaded[path]; delete skill._referenceDrafts[path]; }
      }
    });
    saved.pending.forEach(function (reference) {
      var path = skillReferencePath(reference);
      if (!skill.References.some(function (item) {
        return skillReferencePath(item).toLowerCase() === path.toLowerCase();
      })) skill.References.push(reference);
    });
  });
  return skills || [];
}

function hasPendingSkillReferenceLoad() {
  return (state.skills || []).some(function (skill) {
    ensureSkillReferenceState(skill);
    return Object.keys(skill._referenceLoading).some(function (path) {
      return !!skill._referenceLoading[path];
    });
  });
}

function updateSkillSaveButton() {
  if ($("saveSkillsButton")) {
    $("saveSkillsButton").hidden = !state.skillLibraryDirty;
    $("saveSkillsButton").disabled = !!state.bridgeUnavailable || !state.skillLibraryDirty || hasPendingSkillReferenceLoad();
  }
}

function renderSkills() {
  renderInstructions();
}

function skillMatchesSearch(skill, query) {
  var text = [
    skill.Id || "",
    skill.Name || "",
    skill.Host || "",
    skill.Description || ""
  ].join(" ").toLowerCase();
  return text.indexOf(query) >= 0;
}

function renderSkillReferenceControls(skill, disabled, builtIn) {
  var select = $("skillResourceSelect");
  if (!select) return;
  ensureSkillReferenceState(skill);
  select.innerHTML = "";
  var core = document.createElement("option");
  core.value = "";
  core.textContent = "SKILL.md";
  select.appendChild(core);
  (skill ? skill.References : []).forEach(function (reference) {
    var path = skillReferencePath(reference);
    if (!path) return;
    var option = document.createElement("option");
    option.value = path;
    option.textContent = path + (reference.Pending ? " · новый" : "");
    select.appendChild(option);
  });
  var selected = selectedSkillReferencePath(skill);
  if (selected && !Array.prototype.some.call(select.options, function (option) { return option.value === selected; })) {
    skill._selectedReferencePath = "";
    selected = "";
  }
  select.value = selected;
  select.disabled = disabled || builtIn;
  if ($("skillResourceLabel")) $("skillResourceLabel").textContent = selected || "SKILL.md";
  if ($("addSkillReferenceButton")) $("addSkillReferenceButton").disabled = disabled || builtIn || !!state.bridgeUnavailable;
  if ($("deleteSkillReferenceButton")) $("deleteSkillReferenceButton").disabled = disabled || builtIn || !selected || !!state.bridgeUnavailable;
}

function closeSkillReferenceRead(operation) {
  if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
  operation.closed = true;
  return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "skill-reference-editor", leaseId: operation.data.leaseId })
    .catch(function () {});
}

function cancelSkillReferenceRead() {
  var operation = skillReferenceRead;
  if (!operation) return;
  operation.abort.abort();
  if (operation.bridgeRequestId) cancelBridgeRequest(operation.bridgeRequestId).catch(function () {});
  closeSkillReferenceRead(operation);
  delete operation.skill._referenceLoading[operation.path];
  delete operation.skill._referenceLoadTokens[operation.path];
  skillReferenceRead = null;
}

function updateSkillReferenceReadOnly() {
  var skill = state.skills[state.selectedSkillIndex], path = selectedSkillReferencePath(skill);
  var readOnly = !!state.bridgeUnavailable || !skill || !!skill.BuiltIn || !!(path && !skill._referenceLoaded[path]);
  if ($("skillBodyInput")) $("skillBodyInput").readOnly = readOnly;
  if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("skillBodyInput", readOnly);
}

function skillReferenceReadFromContract(response, operation) {
  var resource = response && response.resource;
  var parts = resource && typeof resource.uri === "string" ? resource.uri.split("/") : [];
  if (!response || response.type !== "rnassistant.skillReferenceRead" || response.contractVersion !== skillLibraryContractVersion ||
      response.chatId !== operation.chatId || response.skillId !== operation.skillId || response.packageRevision !== operation.packageRevision ||
      !response.reference || response.reference.path !== operation.path || response.reference.revision !== operation.referenceRevision ||
      response.reference.byteLength !== operation.referenceByteLength || !Number.isInteger(response.totalCharacters) ||
      response.totalCharacters < 0 || response.totalCharacters > 500000 || !response.data ||
      !response.data.payload || response.data.payload.contentType !== "text/markdown; charset=utf-8" ||
      parts.length !== 7 || parts[0] !== "rna:" || parts[1] !== "" || parts[2] !== "catalog" || parts[3] !== "skills" ||
      decodeURIComponent(parts[4]) !== operation.skillId || parts[5] !== "reference" ||
      decodeURIComponent(parts[6]) !== operation.path.substring("references/".length) ||
      typeof resource.revision !== "string" || !resource.revision)
    throw new Error("Некорректный снимок reference навыка.");
  return response;
}

async function loadSelectedSkillReference(skill, path) {
  if (!skill || !path) return;
  ensureSkillReferenceState(skill);
  if (skill._referenceLoaded[path] || skill._referenceLoading[path]) return;
  var reference = skill.References.filter(function (item) {
    return skillReferencePath(item).toLowerCase() === path.toLowerCase();
  })[0];
  if (reference && reference.Pending) {
    skill._referenceLoaded[path] = true;
    return;
  }
  cancelSkillReferenceRead();
  if (!reference || !state.activeChatId || state.bridgeUnavailable) return;
  if (skillReferenceReadPending >= 2) {
    log("Предыдущее чтение ещё закрывается. Выберите reference повторно после завершения.", "error");
    return;
  }
  var requestId = ++skillReferenceLoadSequence;
  var operation = { skill: skill, skillId: skill.Id, path: path, chatId: state.activeChatId, packageRevision: skill._baseRevision,
    referenceRevision: reference.Revision, referenceByteLength: reference.ByteLength,
    abort: new AbortController(), data: null, bridgeRequestId: null, closed: false };
  skillReferenceRead = operation;
  skillReferenceReadPending++;
  skill._referenceLoading[path] = requestId;
  skill._referenceLoadTokens[path] = requestId;
  updateSkillSaveButton();
  function current() {
    return skillReferenceRead === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
      state.activeChatId === operation.chatId && state.skills[state.selectedSkillIndex] === skill &&
      selectedSkillReferencePath(skill) === path && skill._baseRevision === operation.packageRevision && skill.Id === operation.skillId &&
      skill._referenceLoadTokens[path] === requestId && !skill._referenceDirty[path] &&
      skill.References.some(function (item) { return skillReferencePath(item) === path && item.Revision === operation.referenceRevision; });
  }
  function active() { if (!current()) throw new Error("RESOURCE_READ_CANCELLED"); }
  try {
    updateSkillReferenceReadOnly();
    active();
    var opening = send("readSkillReference", {
      type: skillReferenceRequestType,
      contractVersion: skillLibraryContractVersion,
      chatId: operation.chatId,
      skillId: operation.skillId || "",
      path: path,
      expectedPackageRevision: operation.packageRevision || ""
    });
    operation.bridgeRequestId = opening.requestId;
    var response = await opening;
    operation.bridgeRequestId = null;
    operation.data = response && response.data;
    active();
    var typed = skillReferenceReadFromContract(response, operation);
    var bytes = await window.RNAssistantResourceDownload.read(typed.data, { maxBytes: 2100000, fetch: window.fetch.bind(window),
      signal: operation.abort.signal, isCurrent: current });
    var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    if (text.length !== typed.totalCharacters) throw new Error("Неполный снимок reference навыка.");
    await closeSkillReferenceRead(operation);
    active();
    skill._referenceDrafts[path] = text;
    skill._referenceLoaded[path] = true;
    delete skill._referenceDirty[path];
    delete skill._referenceConflicts[path];
    if (state.skills[state.selectedSkillIndex] === skill && selectedSkillReferencePath(skill) === path) {
      setSkillEditorValue(skill._referenceDrafts[path]);
      renderSkillPreview();
    }
  } catch (error) {
    if (current()) log(error.detail || error.message, "error");
  } finally {
    await closeSkillReferenceRead(operation);
    if (skill._referenceLoading[path] === requestId) delete skill._referenceLoading[path];
    skillReferenceReadPending--;
    if (skillReferenceRead === operation) skillReferenceRead = null;
    updateSkillReferenceReadOnly();
    updateSkillSaveButton();
  }
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  ensureSkillReferenceState(skill);
  var referencePath = selectedSkillReferencePath(skill);
  if (skillReferenceRead && (skillReferenceRead.skill !== skill || skillReferenceRead.path !== referencePath ||
      skillReferenceRead.chatId !== state.activeChatId || skillReferenceRead.packageRevision !== skill._baseRevision)) cancelSkillReferenceRead();
  var references = skill ? (skill.References || []) : [];
  var panel = $("skillEditorPanel");
  if (panel) panel.classList.toggle("hidden", disabled);
  if ($("skillTitle")) $("skillTitle").textContent = skill ? (skill.Id || skill.Name || "Навык") : "Навык";
  if ($("skillMeta")) $("skillMeta").textContent = skill
    ? ((skill.BuiltIn ? "Встроенный навык" : "Пользовательский навык") + " · " + (skill.Host || "Common") + " · references: " + references.length)
    : "";
  $("skillEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("skillIdInput").value = skill ? (skill.Id || "") : "";
  $("skillHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("skillDescriptionInput").value = skill ? (skill.Description || "") : "";
  var body = skill ? (referencePath ? (skill._referenceDrafts[referencePath] || "") : (skill.BodyMarkdown || "")) : "";
  setSkillEditorValue(body);
  renderSkillReferenceControls(skill, disabled, builtIn);
  renderSkillPreview();
  applyInstructionMode();

  ["skillEnabledInput", "skillDescriptionInput", "skillBodyInput"].forEach(function (id) {
    $(id).disabled = disabled || builtIn;
  });
  var identityLocked = references.length > 0;
  $("skillIdInput").disabled = disabled || builtIn || identityLocked;
  $("skillHostInput").disabled = disabled || builtIn || identityLocked;
  updateSkillReferenceReadOnly();

  $("deleteSkillButton").disabled = disabled || builtIn;
  $("cloneSkillButton").disabled = disabled;
  $("copySkillContextButton").disabled = disabled;
  $("askSkillBuilderButton").disabled = disabled;
  $("addSkillButton").disabled = !!state.bridgeUnavailable;
  updateSkillSaveButton();
  if (referencePath && !skill._referenceLoaded[referencePath]) loadSelectedSkillReference(skill, referencePath);
}

function syncSelectedSkillFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["skillBodyInput"]);
  }
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill || skill.BuiltIn) return;

  captureSelectedSkillResource(skill);
  skill.Id = $("skillIdInput").value.trim();
  skill.Host = $("skillHostInput").value;
  skill.Name = skill.Id;
  skill.Description = $("skillDescriptionInput").value;
  skill.Enabled = $("skillEnabledInput").checked;
  skill.BuiltIn = false;
}

function renderSkillPreview() {
  var preview = $("skillPreview");
  if (!preview) return;
  var value = typeof getCodeEditorValue === "function" ? getCodeEditorValue("skillBodyInput") : $("skillBodyInput").value;
  preview.innerHTML = markdown(value || "_Навык пуст._");
  if (typeof enhanceMarkdown === "function") enhanceMarkdown(preview);
}

function applyInstructionMode() {
  var mode = state.promptEditorMode === "preview" ? "preview" : "edit";
  Array.prototype.slice.call(document.querySelectorAll(".instruction-mode-button")).forEach(function (button) {
    button.classList.toggle("active", button.getAttribute("data-instruction-mode") === mode);
  });
  Array.prototype.slice.call(document.querySelectorAll(".instruction-edit-view")).forEach(function (node) { node.classList.toggle("hidden", mode !== "edit"); });
  Array.prototype.slice.call(document.querySelectorAll(".instruction-preview-view")).forEach(function (node) { node.classList.toggle("hidden", mode !== "preview"); });
  if (typeof setCodeEditorVisible === "function") setCodeEditorVisible("skillBodyInput", mode === "edit");
  if (mode === "preview") renderSkillPreview();
}

function skillLibraryMutations() {
  syncSelectedSkillFromEditor();
  var current = skillLibraryRecords(state.skills);
  var baseline = state.skillLibraryBaselineItems || [];
  var currentIndex = skillRecordIndex(current);
  var baselineIndex = skillRecordIndex(baseline);
  var mutations = [];
  current.forEach(function (record) {
    var previous = matchingSkillRecord(baselineIndex, record);
    var changed = !previous || JSON.stringify(record.comparable) !== JSON.stringify(previous.comparable);
    if (!changed) return;
    var skill = record.entity;
    mutations.push({
      kind: "upsert",
      baseId: previous ? previous.baseId : "",
      expectedRevision: previous ? previous.revision : "",
      id: skill.Id || "",
      host: skill.Host || "Common",
      name: skill.Name || skill.Id || "",
      description: skill.Description || "",
      version: skill.Version || "1.0.0",
      bodyMarkdown: skill.BodyMarkdown || "",
      enabled: skill.Enabled !== false
    });
  });
  baseline.forEach(function (record) {
    if (matchingSkillRecord(currentIndex, record)) return;
    mutations.push({
      kind: "delete",
      baseId: record.baseId,
      expectedRevision: record.revision
    });
  });
  return mutations;
}

function selectedSkillContext() {
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill) {
    return "";
  }

  var sections = [
    "# Skill",
    "id: " + (skill.Id || ""),
    "host: " + (skill.Host || "Common"),
    "",
    "## Description",
    skill.Description || "",
    "",
    "## SKILL.md",
    "```markdown",
    skill.BodyMarkdown || "",
    "```"
  ];
  var references = skill.References || [];
  if (references.length) {
    sections.push("", "## References", references.map(function (reference) {
      return "- " + skillReferencePath(reference);
    }).join("\n"));
  }
  var selectedReference = selectedSkillReferencePath(skill);
  if (selectedReference && skill._referenceLoaded[selectedReference]) {
    sections.push("", "## Selected reference: " + selectedReference, "```markdown",
      skill._referenceDrafts[selectedReference] || "", "```");
  }
  return sections.join("\n");
}

async function addSelectedSkillContextToContext() {
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  var context = selectedSkillContext();
  if (!skill || !context) {
    return false;
  }

  await addTextContext(
    "SuppliedData",
    "skill_definition",
    "Skill: " + (skill.Id || "skill"),
    "skill:" + (skill.Id || "skill"),
    context,
    {
      type: "skill_definition",
      id: skill.Id || ""
    });
  log("Контекст навыка добавлен в чат.");
  return true;
}

async function saveSelectedSkillResource() {
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill) return;
  requireUnconflictedSkillReferences();
  var selectedId = skill.Id || "";
  var response = await send("saveSkills", {
    type: skillLibraryMutationRequestType,
    contractVersion: skillLibraryContractVersion,
    mutations: skillLibraryMutations()
  });
  var coreResult = skillLibraryMutationFromContract(response);
  state.skills = coreResult.failure
    ? reconcileSkillLibraryCatalog(coreResult.skills)
    : preserveSkillReferenceState(coreResult.skills);
  state.selectedSkillIndex = state.skills.findIndex(function (item) {
    return String((item && item.Id) || "").toLowerCase() === String(selectedId).toLowerCase();
  });
  if (state.selectedSkillIndex < 0 && state.skills.length) state.selectedSkillIndex = 0;
  if (coreResult.failure) {
    var coreError = new Error(coreResult.failure.message || "Навыки не сохранены.");
    coreError.detail = coreResult.failure.message;
    coreError.code = coreResult.failure.code || "skill_library_mutation_failed";
    updateSkillLibraryDirty();
    renderSkills();
    throw coreError;
  }
  var savedReferences = 0;
  requireUnconflictedSkillReferences();
  for (var skillIndex = 0; skillIndex < state.skills.length; skillIndex += 1) {
    var saved = state.skills[skillIndex];
    if (!saved || saved.BuiltIn) continue;
    ensureSkillReferenceState(saved);
    var dirtyPaths = Object.keys(saved._referenceDirty).filter(function (path) {
      return !!saved._referenceDirty[path];
    });
    for (var pathIndex = 0; pathIndex < dirtyPaths.length; pathIndex += 1) {
      var referencePath = dirtyPaths[pathIndex];
      var referenceContent = saved._referenceDrafts[referencePath] || "";
      var referenceResponse = await send("saveSkillReference", {
        type: skillReferenceRequestType,
        contractVersion: skillLibraryContractVersion,
        skillId: saved.Id || "",
        path: referencePath,
        content: referenceContent,
        expectedPackageRevision: saved._baseRevision || ""
      });
      var typedReference = skillReferenceFromResponse(referenceResponse,
        ["create_reference", "update_reference"]);
      mergeSkillReferenceMetadata(saved, typedReference.skill.References);
      saved.Revision = typedReference.skill.Revision;
      saved._baseRevision = typedReference.skill._baseRevision;
      saved._referenceDrafts[referencePath] = typedReference.content || referenceContent;
      saved._referenceLoaded[referencePath] = true;
      delete saved._referenceDirty[referencePath];
      delete saved._referenceConflicts[referencePath];
      savedReferences += 1;
    }
  }
  log(savedReferences ? ("Навыки и references сохранены: " + savedReferences + ".") : "Навыки сохранены.");
  acceptSkillLibraryState();
  renderSkills();
}

function addSkillReference() {
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill || skill.BuiltIn) return;
  var name = window.prompt("Имя reference Markdown", "details.md");
  if (name === null) return;
  name = String(name || "").trim();
  if (name.toLowerCase().indexOf("references/") === 0) name = name.substring("references/".length);
  if (!name || !/^[^\\\/:*?"<>|]+\.md$/i.test(name)) {
    log("Используйте одно имя Markdown-файла, например details.md.", "error");
    return;
  }
  var path = "references/" + name;
  ensureSkillReferenceState(skill);
  if (skill.References.some(function (reference) {
    return skillReferencePath(reference).toLowerCase() === path.toLowerCase();
  })) {
    log("Reference уже существует: " + path, "error");
    return;
  }
  skill.References.push({ Path: path, ByteLength: 0, Revision: "", Pending: true });
  skill._referenceDrafts[path] = "# " + name.replace(/\.md$/i, "").replace(/[-_]+/g, " ") + "\n\n";
  skill._referenceLoaded[path] = true;
  skill._referenceDirty[path] = true;
  skill._selectedReferencePath = path;
  updateSkillLibraryDirty();
  renderSkillEditor();
}

async function deleteSelectedSkillReference() {
  cancelSkillReferenceRead();
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  var path = selectedSkillReferencePath(skill);
  if (!skill || skill.BuiltIn || !path) return;
  if (!window.confirm("Удалить reference " + path + "?")) return;
  var reference = (skill.References || []).filter(function (item) {
    return skillReferencePath(item).toLowerCase() === path.toLowerCase();
  })[0];
  delete skill._referenceLoading[path];
  delete skill._referenceLoadTokens[path];
  updateSkillSaveButton();
  if (reference && reference.Pending) {
    skill.References = skill.References.filter(function (item) { return skillReferencePath(item) !== path; });
  } else {
    var response = await send("deleteSkillReference", {
      type: skillReferenceRequestType,
      contractVersion: skillLibraryContractVersion,
      skillId: skill.Id || "",
      path: path,
      expectedPackageRevision: skill._baseRevision || ""
    });
    var typed = skillReferenceFromResponse(response, "delete_reference");
    if (!typed.deleted) throw new Error("Удаление reference не подтверждено read-back.");
    mergeSkillReferenceMetadata(skill, typed.skill.References);
    skill.Revision = typed.skill.Revision;
    skill._baseRevision = typed.skill._baseRevision;
  }
  delete skill._referenceDrafts[path];
  delete skill._referenceLoaded[path];
  delete skill._referenceLoading[path];
  delete skill._referenceLoadTokens[path];
  delete skill._referenceDirty[path];
  delete skill._referenceConflicts[path];
  skill._selectedReferencePath = "";
  updateSkillLibraryDirty();
  renderSkillEditor();
  log("Reference удалён: " + path);
}

function bindSkillActions() {
  window.addEventListener("pagehide", cancelSkillReferenceRead);
  Array.prototype.slice.call(document.querySelectorAll(".instruction-mode-button")).forEach(function (button) {
    button.addEventListener("click", function () { syncSelectedSkillFromEditor(); state.promptEditorMode = button.getAttribute("data-instruction-mode"); applyInstructionMode(); });
  });

  $("addSkillButton").addEventListener("click", function () {
    if (typeof syncSelectedLibraryItem === "function") syncSelectedLibraryItem();
    else if (state.selectedInstructionKind === "skill") syncSelectedSkillFromEditor();
    state.skills.push({
      Id: "common.new_skill",
      Host: "Common",
      Name: "new_skill",
      Description: "",
      Version: "1.0.0",
      BodyMarkdown: "# Новый навык\n\nИспользуйте этот навык, когда...\n",
      References: [],
      Enabled: true,
      BuiltIn: false,
      Revision: "",
      _baseId: "",
      _baseRevision: ""
    });
    state.selectedSkillIndex = state.skills.length - 1;
    state.selectedInstructionKind = "skill";
    updateSkillLibraryDirty();
    renderSkills();
  });

  $("cloneSkillButton").addEventListener("click", function () {
    syncSelectedSkillFromEditor();
    var source = state.skills[state.selectedSkillIndex];
    if (!source) {
      return;
    }

    var id = (source.Id || "skill") + ".copy";
    state.skills.push({
      Id: id,
      Host: source.Host || "Common",
      Name: id,
      Description: source.Description || "",
      Version: source.Version || "1.0.0",
      BodyMarkdown: source.BodyMarkdown || "",
      References: [],
      Enabled: true,
      BuiltIn: false,
      Revision: "",
      _baseId: "",
      _baseRevision: ""
    });
    state.selectedSkillIndex = state.skills.length - 1;
    state.selectedInstructionKind = "skill";
    updateSkillLibraryDirty();
    renderSkills();
  });

  $("saveSkillsButton").addEventListener("click", async function () {
    setControlBusy("saveSkillsButton", true);
    try {
      await saveSelectedSkillResource();
    } catch (error) {
      log(error.detail || error.message, "error");
    } finally {
      setControlBusy("saveSkillsButton", false);
    }
  });

  $("skillResourceSelect").addEventListener("change", function () {
    var skill = state.skills[state.selectedSkillIndex];
    if (!skill) return;
    captureSelectedSkillResource(skill);
    skill._selectedReferencePath = $("skillResourceSelect").value || "";
    renderSkillEditor();
  });

  $("addSkillReferenceButton").addEventListener("click", addSkillReference);

  $("deleteSkillReferenceButton").addEventListener("click", async function () {
    setControlBusy("deleteSkillReferenceButton", true);
    try {
      await deleteSelectedSkillReference();
    } catch (error) {
      log(error.detail || error.message, "error");
    } finally {
      setControlBusy("deleteSkillReferenceButton", false);
    }
  });

  $("deleteSkillButton").addEventListener("click", function () {
    var skill = state.skills[state.selectedSkillIndex];
    if (!skill || skill.BuiltIn) {
      return;
    }

    state.skills.splice(state.selectedSkillIndex, 1);
    if (state.selectedSkillIndex >= state.skills.length) {
      state.selectedSkillIndex = state.skills.length - 1;
    }
    updateSkillLibraryDirty();
    renderSkills();
  });

  $("copySkillContextButton").addEventListener("click", function () {
    copyText(selectedSkillContext());
    log("Контекст навыка скопирован.");
  });

  $("askSkillBuilderButton").addEventListener("click", function () {
    addSelectedSkillContextToContext().then(function (added) {
      if (!added) {
        return;
      }

      switchTab("chat");
      setChatInputText("Отредактируй RNAssistant-навык из добавленного контекста. При необходимости вызови common.skills_upsert только с изменёнными полями после подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message, "error");
    });
  });

  ["skillEnabledInput", "skillIdInput", "skillHostInput", "skillDescriptionInput"].forEach(function (id) {
    var control = $(id);
    if (!control) return;
    control.addEventListener(control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input", markSkillLibraryDirty);
  });
}
