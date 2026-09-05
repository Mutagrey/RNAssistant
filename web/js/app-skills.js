var skillSourceLoadSequence = 0;
var skillSourceRead = null;
var skillSourceReadPending = 0;
var skillWriteOperation = null;
var skillMutationMaximumBytes = 16 * 1024 * 1024;
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
    !skill.body || !/^[a-f0-9]{64}$/.test(skill.body.sha256) ||
    !Number.isInteger(skill.body.byteLength) || skill.body.byteLength < 0 || skill.body.byteLength > 2100000 ||
    !Number.isInteger(skill.body.characters) || skill.body.characters < 0 || skill.body.characters > 500000 ||
    typeof skill.enabled !== "boolean" ||
    typeof skill.builtIn !== "boolean" || !Array.isArray(skill.references)) {
    throw new Error("Некорректный typed package навыка.");
  }
  return {
    Id: skill.id,
    Host: skill.host,
    Name: skill.name,
    Description: skill.description,
    Version: skill.version,
    Body: skill.body,
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

function ensureSkillSourceState(skill) {
  if (!skill) return;
  if (!skill.References) skill.References = [];
  if (!skill._sourceDrafts) skill._sourceDrafts = {};
  if (!skill._sourceLoaded) skill._sourceLoaded = {};
  if (!skill._sourceLoading) skill._sourceLoading = {};
  if (!skill._sourceLoadTokens) skill._sourceLoadTokens = {};
  if (!skill._sourceDirty) skill._sourceDirty = {};
  if (!skill._sourceConflicts) skill._sourceConflicts = {};
  if (!skill._selectedReferencePath) skill._selectedReferencePath = "";
}

function selectedSkillReferencePath(skill) {
  ensureSkillSourceState(skill);
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
      BodySha256: skill.Body ? skill.Body.sha256 : "",
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

function skillHasDirtySources(skill) {
  ensureSkillSourceState(skill);
  return Object.keys(skill._sourceDirty).some(function (path) { return !!skill._sourceDirty[path]; });
}

function requireUnconflictedSkillSources() {
  (state.skills || []).forEach(function (skill) {
    ensureSkillSourceState(skill);
    if (Object.keys(skill._sourceConflicts).some(function (path) { return skill._sourceDirty[path] && skill._sourceConflicts[path]; }))
      throw new Error("Источник навыка изменился после чтения. Обновите пакет и разрешите конфликт перед сохранением.");
  });
}

function skillRecordChanged(current, baseline) {
  return !baseline || skillHasDirtySources(current.entity) ||
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
  state.skills = preserveSkillSourceState(merged);
  updateSkillLibraryDirty();
  return state.skills;
}

function hasDirtySkillSource() {
  return (state.skills || []).some(function (skill) {
    return skillHasDirtySources(skill);
  });
}

function updateSkillLibraryDirty() {
  state.skillLibraryDirty = skillLibrarySnapshot(state.skills) !== state.skillLibraryBaseline || hasDirtySkillSource();
  updateSkillSaveButton();
}

function markSkillLibraryDirty() {
  syncSelectedSkillFromEditor();
  updateSkillLibraryDirty();
}

function acceptSkillLibraryState() {
  setSkillLibraryBaseline(state.skills);
  state.skillLibraryDirty = hasDirtySkillSource();
  updateSkillSaveButton();
}

function captureSelectedSkillResource(skill) {
  if (!skill) return;
  ensureSkillSourceState(skill);
  var value = skillEditorValue();
  var path = selectedSkillReferencePath(skill);
  if (!skill._sourceLoaded[path]) return;
  if (skill._sourceDrafts[path] !== value) skill._sourceDirty[path] = true;
  skill._sourceDrafts[path] = value;
}

function mergeSkillReferenceMetadata(skill, references) {
  if (!skill) return;
  ensureSkillSourceState(skill);
  var server = (references || []).slice();
  skill.References.forEach(function (reference) {
    var path = skillReferencePath(reference);
    if (reference && reference.Pending && !server.some(function (item) {
      return skillReferencePath(item).toLowerCase() === path.toLowerCase();
    })) server.push(reference);
  });
  skill.References = server;
}

function preserveSkillSourceState(skills) {
  cancelSkillSourceRead();
  var transient = {};
  (state.skills || []).forEach(function (skill) {
    if (!skill || !skill.Id) return;
    ensureSkillSourceState(skill);
    transient[String(skill.Id).toLowerCase()] = {
      selected: skill._selectedReferencePath || "",
      drafts: skill._sourceDrafts,
      loaded: skill._sourceLoaded,
      dirty: skill._sourceDirty,
      conflicts: skill._sourceConflicts,
      body: skill.Body,
      references: skill.References.slice(),
      pending: (skill.References || []).filter(function (item) { return !!item.Pending; })
    };
  });
  (skills || []).forEach(function (skill) {
    var saved = transient[String((skill && skill.Id) || "").toLowerCase()];
    ensureSkillSourceState(skill);
    if (!saved) return;
    skill._selectedReferencePath = saved.selected;
    skill._sourceDrafts = saved.drafts;
    skill._sourceLoaded = saved.loaded;
    skill._sourceLoading = {};
    skill._sourceLoadTokens = {};
    skill._sourceDirty = saved.dirty;
    skill._sourceConflicts = saved.conflicts;
    Object.keys(saved.loaded).forEach(function (path) {
      if (!path) {
        if (!saved.body || !skill.Body || saved.body.sha256 !== skill.Body.sha256) {
          if (saved.dirty[path]) skill._sourceConflicts[path] = true;
          else { delete skill._sourceLoaded[path]; delete skill._sourceDrafts[path]; }
        }
        return;
      }
      var before = saved.references.find(function (item) { return skillReferencePath(item) === path; });
      var after = skill.References.find(function (item) { return skillReferencePath(item) === path; });
      if (before && before.Pending) return;
      if (!before || !after || before.Revision !== after.Revision) {
        if (saved.dirty[path]) skill._sourceConflicts[path] = true;
        else { delete skill._sourceLoaded[path]; delete skill._sourceDrafts[path]; }
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

function hasPendingSkillSourceLoad() {
  return (state.skills || []).some(function (skill) {
    ensureSkillSourceState(skill);
    return Object.keys(skill._sourceLoading).some(function (path) {
      return !!skill._sourceLoading[path];
    });
  });
}

function updateSkillSaveButton() {
  if ($("saveSkillsButton")) {
    $("saveSkillsButton").hidden = !state.skillLibraryDirty;
    $("saveSkillsButton").disabled = !!state.bridgeUnavailable || !!skillWriteOperation || !state.skillLibraryDirty || hasPendingSkillSourceLoad();
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
  ensureSkillSourceState(skill);
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

function closeSkillSourceRead(operation) {
  if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
  operation.closed = true;
  return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "skill-editor", leaseId: operation.data.leaseId })
    .catch(function () {});
}

function cancelSkillSourceRead() {
  var operation = skillSourceRead;
  if (!operation) return;
  operation.abort.abort();
  if (operation.bridgeRequestId) cancelBridgeRequest(operation.bridgeRequestId).catch(function () {});
  closeSkillSourceRead(operation);
  delete operation.skill._sourceLoading[operation.path];
  delete operation.skill._sourceLoadTokens[operation.path];
  skillSourceRead = null;
}

function updateSkillSourceReadOnly() {
  var skill = state.skills[state.selectedSkillIndex], path = selectedSkillReferencePath(skill);
  var readOnly = !!state.bridgeUnavailable || !skill || !!skill.BuiltIn || !skill._sourceLoaded[path];
  if ($("skillBodyInput")) $("skillBodyInput").readOnly = readOnly;
  if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("skillBodyInput", readOnly);
  ["cloneSkillButton", "copySkillContextButton", "askSkillBuilderButton"].forEach(function (id) {
    if ($(id)) $(id).disabled = !!state.bridgeUnavailable || !!skillWriteOperation || !skill || !skill._sourceLoaded[""];
  });
}

function skillSourceReadFromContract(response, operation) {
  var resource = response && response.resource;
  var parts = resource && typeof resource.uri === "string" ? resource.uri.split("/") : [];
  var isReference = operation.path !== "";
  if (!response || response.type !== "rnassistant.skillSourceRead" || response.contractVersion !== skillLibraryContractVersion ||
      response.chatId !== operation.chatId || response.skillId !== operation.skillId || response.packageRevision !== operation.packageRevision ||
      response.path !== operation.path || !Number.isInteger(response.totalCharacters) ||
      response.totalCharacters < 0 || response.totalCharacters > 500000 || !response.data ||
      !response.data.payload || response.data.payload.contentType !== "text/markdown; charset=utf-8" ||
      parts.length !== (isReference ? 7 : 6) || parts[0] !== "rna:" || parts[1] !== "" || parts[2] !== "catalog" ||
      (operation.builtIn ? !/^builtin-skills-[a-z]+$/.test(parts[3]) : parts[3] !== "skills") ||
      decodeURIComponent(parts[4]) !== operation.skillId || parts[5] !== (isReference ? "reference" : "body") ||
      typeof resource.revision !== "string" || !resource.revision)
    throw new Error("Некорректный снимок источника навыка.");
  if (isReference ? (!response.reference || response.reference.path !== operation.path ||
      response.reference.revision !== operation.sourceRevision || response.reference.byteLength !== operation.sourceByteLength ||
      decodeURIComponent(parts[6]) !== operation.path.substring("references/".length)) :
      (response.reference != null || response.data.payload.sha256 !== operation.sourceRevision ||
       response.data.payload.byteLength !== operation.sourceByteLength || response.totalCharacters !== operation.sourceCharacters))
    throw new Error("Метаданные источника навыка не совпадают со снимком.");
  return response;
}

async function loadSelectedSkillSource(skill, path) {
  if (!skill || typeof path !== "string") return;
  ensureSkillSourceState(skill);
  if (skill._sourceLoaded[path] || skill._sourceLoading[path]) return;
  var reference = skill.References.filter(function (item) {
    return skillReferencePath(item).toLowerCase() === path.toLowerCase();
  })[0];
  if (reference && reference.Pending) {
    skill._sourceLoaded[path] = true;
    return;
  }
  cancelSkillSourceRead();
  if ((path ? !reference : !skill.Body) || !state.activeChatId || state.bridgeUnavailable) return;
  if (skillSourceReadPending >= 2) {
    log("Предыдущее чтение ещё закрывается. Выберите источник повторно после завершения.", "error");
    return;
  }
  var requestId = ++skillSourceLoadSequence;
  var operation = { skill: skill, skillId: skill._baseId || skill.Id, path: path, chatId: state.activeChatId, packageRevision: skill.Revision,
    builtIn: !!skill.BuiltIn, sourceRevision: path ? reference.Revision : skill.Body.sha256,
    sourceByteLength: path ? reference.ByteLength : skill.Body.byteLength, sourceCharacters: path ? null : skill.Body.characters,
    abort: new AbortController(), data: null, bridgeRequestId: null, closed: false };
  skillSourceRead = operation;
  skillSourceReadPending++;
  skill._sourceLoading[path] = requestId;
  skill._sourceLoadTokens[path] = requestId;
  updateSkillSaveButton();
  function current() {
    return skillSourceRead === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
      state.selectedInstructionKind === "skill" &&
      state.activeChatId === operation.chatId && state.skills[state.selectedSkillIndex] === skill &&
      selectedSkillReferencePath(skill) === path && skill.Revision === operation.packageRevision && (skill._baseId || skill.Id) === operation.skillId &&
      skill._sourceLoadTokens[path] === requestId && !skill._sourceDirty[path] &&
      (path ? skill.References.some(function (item) { return skillReferencePath(item) === path && item.Revision === operation.sourceRevision; }) :
        skill.Body && skill.Body.sha256 === operation.sourceRevision && skill.Body.byteLength === operation.sourceByteLength &&
        skill.Body.characters === operation.sourceCharacters);
  }
  function active() { if (!current()) throw new Error("RESOURCE_READ_CANCELLED"); }
  try {
    updateSkillSourceReadOnly();
    active();
    var opening = send("readSkillSource", {
      type: "rnassistant.skillSourceRequest",
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
    var typed = skillSourceReadFromContract(response, operation);
    var bytes = await window.RNAssistantResourceDownload.read(typed.data, { maxBytes: 2100000, fetch: window.fetch.bind(window),
      signal: operation.abort.signal, isCurrent: current });
    var text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    if (text.length !== typed.totalCharacters) throw new Error("Неполный снимок источника навыка.");
    await closeSkillSourceRead(operation);
    active();
    skill._sourceDrafts[path] = text;
    skill._sourceLoaded[path] = true;
    delete skill._sourceDirty[path];
    delete skill._sourceConflicts[path];
    if (state.skills[state.selectedSkillIndex] === skill && selectedSkillReferencePath(skill) === path) {
      setSkillEditorValue(skill._sourceDrafts[path]);
      renderSkillPreview();
    }
  } catch (error) {
    if (current()) log(error.detail || error.message, "error");
  } finally {
    await closeSkillSourceRead(operation);
    if (skill._sourceLoading[path] === requestId) delete skill._sourceLoading[path];
    skillSourceReadPending--;
    if (skillSourceRead === operation) skillSourceRead = null;
    updateSkillSourceReadOnly();
    updateSkillSaveButton();
  }
}

function trimSkillSourceCache(selected, path) {
  (state.skills || []).forEach(function (skill) {
    ensureSkillSourceState(skill);
    Object.keys(skill._sourceLoaded).forEach(function (key) {
      if (!skill._sourceDirty[key] && (skill !== selected || key !== path && key !== "")) {
        delete skill._sourceLoaded[key]; delete skill._sourceDrafts[key];
      }
    });
  });
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  ensureSkillSourceState(skill);
  var referencePath = selectedSkillReferencePath(skill);
  if (skillSourceRead && (skillSourceRead.skill !== skill || skillSourceRead.path !== referencePath ||
      skillSourceRead.chatId !== state.activeChatId || skillSourceRead.packageRevision !== skill.Revision)) cancelSkillSourceRead();
  trimSkillSourceCache(skill, referencePath);
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
  var body = skill ? (skill._sourceDrafts[referencePath] || "") : "";
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
  updateSkillSourceReadOnly();

  $("deleteSkillButton").disabled = disabled || builtIn;
  $("addSkillButton").disabled = !!state.bridgeUnavailable;
  updateSkillWriteControls();
  updateSkillSaveButton();
  if (skill && !skill._sourceLoaded[referencePath]) loadSelectedSkillSource(skill, referencePath);
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
    var skill = record.entity;
    ensureSkillSourceState(skill);
    var changed = !previous || !!skill._sourceDirty[""] || JSON.stringify(record.comparable) !== JSON.stringify(previous.comparable);
    if (!changed) return;
    var replaceBody = !previous || !!skill._sourceDirty[""];
    if (replaceBody && !skill._sourceLoaded[""]) throw new Error("Сначала загрузите полный SKILL.md перед сохранением его текста.");
    mutations.push({
      kind: "upsert",
      baseId: previous ? previous.baseId : "",
      expectedRevision: previous ? previous.revision : "",
      id: skill.Id || "",
      host: skill.Host || "Common",
      name: skill.Name || skill.Id || "",
      description: skill.Description || "",
      version: skill.Version || "1.0.0",
      preserveBody: !replaceBody,
      bodyMarkdown: replaceBody ? skill._sourceDrafts[""] : undefined,
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
  if (!skill || !skill._sourceLoaded[""]) {
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
    skill._sourceDrafts[""] || "",
    "```"
  ];
  var references = skill.References || [];
  if (references.length) {
    sections.push("", "## References", references.map(function (reference) {
      return "- " + skillReferencePath(reference);
    }).join("\n"));
  }
  var selectedReference = selectedSkillReferencePath(skill);
  if (selectedReference && skill._sourceLoaded[selectedReference]) {
    sections.push("", "## Selected reference: " + selectedReference, "```markdown",
      skill._sourceDrafts[selectedReference] || "", "```");
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

function closeSkillMutationUpload(operation) {
  if (!operation || operation.closed || !operation.lease || !/^[a-f0-9]{64}$/.test(operation.lease.leaseId)) return Promise.resolve();
  operation.closed = true;
  return send("cancelSkillMutationUpload", { chatId: operation.chatId, leaseId: operation.lease.leaseId }).catch(function () {});
}

function cancelSkillSourceWrite() {
  var operation = skillWriteOperation;
  if (!operation) return;
  operation.abort.abort();
  if (operation.requestId) cancelBridgeRequest(operation.requestId).catch(function () {});
  closeSkillMutationUpload(operation);
}

function updateSkillWriteControls() {
  var skill = state.skills[state.selectedSkillIndex], unavailable = !!state.bridgeUnavailable || !!skillWriteOperation;
  var disabled = unavailable || !skill || !!skill.BuiltIn;
  ["skillEnabledInput", "skillDescriptionInput", "deleteSkillButton", "addSkillReferenceButton"].forEach(function (id) { if ($(id)) $(id).disabled = disabled; });
  ["skillIdInput", "skillHostInput"].forEach(function (id) { if ($(id)) $(id).disabled = disabled || !!(skill && skill.References && skill.References.length); });
  if ($("addSkillButton")) $("addSkillButton").disabled = unavailable;
  if ($("deleteSkillReferenceButton")) $("deleteSkillReferenceButton").disabled = disabled || !selectedSkillReferencePath(skill);
  ["cloneSkillButton", "copySkillContextButton", "askSkillBuilderButton"].forEach(function (id) {
    if ($(id)) $(id).disabled = unavailable || !skill || !skill._sourceLoaded[""];
  });
}

async function uploadSkillMutation(operation, action, body) {
  operation.active();
  function validateText(text) {
    if (typeof text !== "string" || text.length > 500000) throw new Error("RESOURCE_BATCH_TOO_LARGE");
    if (new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(new TextEncoder().encode(text)) !== text)
      throw new Error("RESOURCE_UPLOAD_INVALID: некорректный Unicode в тексте навыка.");
  }
  // Bound each serialized member before constructing the whole batch. The resulting
  // typed JSON travels only as upload bytes, never as a nested control-message body.
  if (body.mutations) {
    if (body.mutations.length > 256) throw new Error("RESOURCE_BATCH_TOO_LARGE");
    var length = 256;
    body.mutations.forEach(function (mutation) {
      if (mutation.bodyMarkdown != null) validateText(mutation.bodyMarkdown);
      length += JSON.stringify(mutation).length + 1;
      if (length > skillMutationMaximumBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
    });
  } else validateText(body.content);
  var json = JSON.stringify(body);
  if (json.length > skillMutationMaximumBytes) throw new Error("RESOURCE_BATCH_TOO_LARGE");
  var bytes = new TextEncoder().encode(json);
  if (bytes.length > skillMutationMaximumBytes || new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes) !== json)
    throw new Error("RESOURCE_UPLOAD_INVALID");
  var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)))
    .map(function (part) { return part.toString(16).padStart(2, "0"); }).join("");
  operation.active();
  operation.lease = null; operation.closed = false; operation.possibleEffect = false;
  try {
    var opening = send("beginSkillMutationUpload", { chatId: operation.chatId, byteLength: bytes.length });
    operation.requestId = opening.requestId;
    operation.lease = await opening; operation.requestId = null;
    operation.active();
    await window.RNAssistantResourceUpload.write(operation.lease, new Blob([bytes]), {
      maxBytes: skillMutationMaximumBytes, signal: operation.abort.signal, isCurrent: operation.current
    });
    operation.active();
    operation.possibleEffect = action !== "saveSkills" || body.mutations.length > 0;
    var saving = send(action, { chatId: operation.chatId, uploadLeaseId: operation.lease.leaseId, sha256: hash });
    operation.requestId = saving.requestId;
    var response = await saving; operation.requestId = null;
    operation.active();
    return response;
  } finally { await closeSkillMutationUpload(operation); }
}

async function saveSelectedSkillResource() {
  if (skillWriteOperation) throw new Error("Дождитесь завершения сохранения навыков.");
  var operation = { chatId: state.activeChatId, library: state.skills, abort: new AbortController(),
    requestId: null, lease: null, closed: false, possibleEffect: false };
  operation.current = function () { return skillWriteOperation === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
    !!operation.chatId && state.activeChatId === operation.chatId && state.skills === operation.library; };
  operation.active = function () { if (!operation.current()) throw new Error("Сохранение навыков остановлено: контекст Library изменился."); };
  skillWriteOperation = operation;
  updateSkillSaveButton(); updateSkillWriteControls();
  try { operation.active(); await saveSkillResources(operation); }
  catch (error) {
    if (operation.possibleEffect) error.detail = (error.detail || error.message) + " Обновите Library перед повтором: результат записи не подтверждён в редакторе.";
    throw error;
  } finally {
    await closeSkillMutationUpload(operation);
    if (skillWriteOperation === operation) skillWriteOperation = null;
    updateSkillSaveButton(); updateSkillWriteControls();
  }
}

async function saveSkillResources(operation) {
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill) return;
  requireUnconflictedSkillSources();
  var mutations = skillLibraryMutations();
  var submittedSkills = state.skills.slice();
  var references = [];
  submittedSkills.forEach(function (item) {
    ensureSkillSourceState(item);
    Object.keys(item._sourceDirty).forEach(function (path) {
      if (path && item._sourceDirty[path]) {
        if (!item._sourceLoaded[path]) throw new Error("Сначала загрузите полный reference.");
        references.push({ id: item.Id, path: path, text: item._sourceDrafts[path] });
      }
    });
  });
  var response = await uploadSkillMutation(operation, "saveSkills", {
    type: skillLibraryMutationRequestType,
    contractVersion: skillLibraryContractVersion,
    mutations: mutations
  });
  var coreResult = skillLibraryMutationFromContract(response);
  operation.possibleEffect = coreResult.results.some(function (result) { return result.status === "unknown" || result.effect === "unknown"; });
  var selectedId = (state.skills[state.selectedSkillIndex] || {}).Id || "";
  acknowledgeSkillBodySaves(submittedSkills, mutations, coreResult);
  state.skills = coreResult.failure
    ? reconcileSkillLibraryCatalog(coreResult.skills)
    : preserveSkillSourceState(coreResult.skills);
  operation.library = state.skills;
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
  requireUnconflictedSkillSources();
  for (var referenceIndex = 0; referenceIndex < references.length; referenceIndex++) {
      operation.active(); requireUnconflictedSkillSources();
      var planned = references[referenceIndex];
      var saved = state.skills.find(function (item) { return item.Id === planned.id; });
      if (!saved || saved.BuiltIn || !saved._sourceDirty[planned.path] || saved._sourceDrafts[planned.path] !== planned.text) continue;
      var referencePath = planned.path, referenceContent = planned.text, expectedRevision = saved._baseRevision;
      var referenceResponse = await uploadSkillMutation(operation, "saveSkillReference", {
        type: skillReferenceRequestType,
        contractVersion: skillLibraryContractVersion,
        skillId: saved.Id || "",
        path: referencePath,
        content: referenceContent,
        expectedPackageRevision: expectedRevision || ""
      });
      var typedReference = skillReferenceFromResponse(referenceResponse,
        ["create_reference", "update_reference"]);
      if (typedReference.path !== referencePath || typedReference.skill.Id !== saved.Id || typedReference.result.id !== saved.Id ||
          typedReference.result.previousRevision !== expectedRevision || typedReference.result.revision !== typedReference.skill.Revision)
        throw new Error("Результат сохранения reference не совпадает с запросом.");
      operation.possibleEffect = false;
      mergeSkillReferenceMetadata(saved, typedReference.skill.References);
      saved.Revision = typedReference.skill.Revision;
      saved._baseRevision = typedReference.skill._baseRevision;
      if (saved._sourceDrafts[referencePath] === referenceContent) {
        delete saved._sourceDirty[referencePath]; delete saved._sourceConflicts[referencePath];
        delete saved._sourceLoaded[referencePath]; delete saved._sourceDrafts[referencePath];
      } else saved._sourceConflicts[referencePath] = true;
      savedReferences += 1;
  }
  log(savedReferences ? ("Навыки и references сохранены: " + savedReferences + ".") : "Навыки сохранены.");
  acceptSkillLibraryState();
  renderSkills();
}

function acknowledgeSkillBodySaves(submittedSkills, mutations, result) {
  mutations.forEach(function (mutation) {
    if (mutation.kind !== "upsert" || mutation.preserveBody) return;
    var outcome = result.results.find(function (item) { return item.id === mutation.id && item.status === "ok"; });
    var published = outcome && result.skills.find(function (item) { return item.Id === mutation.id && item.Revision === outcome.revision; });
    var draft = submittedSkills.find(function (item) { return item.Id === mutation.id; });
    if (!published || !draft || state.skills.indexOf(draft) < 0 || draft._sourceDrafts[""] !== mutation.bodyMarkdown) return;
    delete draft._sourceDirty[""]; delete draft._sourceConflicts[""];
    delete draft._sourceLoaded[""]; delete draft._sourceDrafts[""];
    draft.Body = published.Body;
    draft.Revision = published.Revision; draft._baseRevision = published._baseRevision; draft._baseId = published._baseId;
  });
}

function skillWithBodyDraft(skill, body) {
  ensureSkillSourceState(skill);
  skill._sourceDrafts[""] = body; skill._sourceLoaded[""] = true; skill._sourceDirty[""] = true;
  return skill;
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
  ensureSkillSourceState(skill);
  if (skill.References.some(function (reference) {
    return skillReferencePath(reference).toLowerCase() === path.toLowerCase();
  })) {
    log("Reference уже существует: " + path, "error");
    return;
  }
  skill.References.push({ Path: path, ByteLength: 0, Revision: "", Pending: true });
  skill._sourceDrafts[path] = "# " + name.replace(/\.md$/i, "").replace(/[-_]+/g, " ") + "\n\n";
  skill._sourceLoaded[path] = true;
  skill._sourceDirty[path] = true;
  skill._selectedReferencePath = path;
  updateSkillLibraryDirty();
  renderSkillEditor();
}

async function deleteSelectedSkillReference() {
  if (skillWriteOperation) throw new Error("Дождитесь завершения сохранения навыков.");
  cancelSkillSourceRead();
  syncSelectedSkillFromEditor();
  var skill = state.skills[state.selectedSkillIndex];
  var path = selectedSkillReferencePath(skill);
  if (!skill || skill.BuiltIn || !path) return;
  if (!window.confirm("Удалить reference " + path + "?")) return;
  var reference = (skill.References || []).filter(function (item) {
    return skillReferencePath(item).toLowerCase() === path.toLowerCase();
  })[0];
  delete skill._sourceLoading[path];
  delete skill._sourceLoadTokens[path];
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
  delete skill._sourceDrafts[path];
  delete skill._sourceLoaded[path];
  delete skill._sourceLoading[path];
  delete skill._sourceLoadTokens[path];
  delete skill._sourceDirty[path];
  delete skill._sourceConflicts[path];
  skill._selectedReferencePath = "";
  updateSkillLibraryDirty();
  renderSkillEditor();
  log("Reference удалён: " + path);
}

function bindSkillActions() {
  window.addEventListener("pagehide", cancelSkillSourceRead);
  window.addEventListener("pagehide", cancelSkillSourceWrite);
  Array.prototype.slice.call(document.querySelectorAll(".instruction-mode-button")).forEach(function (button) {
    button.addEventListener("click", function () { syncSelectedSkillFromEditor(); state.promptEditorMode = button.getAttribute("data-instruction-mode"); applyInstructionMode(); });
  });

  $("addSkillButton").addEventListener("click", function () {
    if (typeof syncSelectedLibraryItem === "function") syncSelectedLibraryItem();
    else if (state.selectedInstructionKind === "skill") syncSelectedSkillFromEditor();
    state.skills.push(skillWithBodyDraft({
      Id: "common.new_skill",
      Host: "Common",
      Name: "new_skill",
      Description: "",
      Version: "1.0.0",
      References: [],
      Enabled: true,
      BuiltIn: false,
      Revision: "",
      _baseId: "",
      _baseRevision: ""
    }, "# Новый навык\n\nИспользуйте этот навык, когда...\n"));
    state.selectedSkillIndex = state.skills.length - 1;
    state.selectedInstructionKind = "skill";
    updateSkillLibraryDirty();
    renderSkills();
  });

  $("cloneSkillButton").addEventListener("click", function () {
    syncSelectedSkillFromEditor();
    var source = state.skills[state.selectedSkillIndex];
    if (!source || !source._sourceLoaded[""]) {
      return;
    }

    var id = (source.Id || "skill") + ".copy";
    state.skills.push(skillWithBodyDraft({
      Id: id,
      Host: source.Host || "Common",
      Name: id,
      Description: source.Description || "",
      Version: source.Version || "1.0.0",
      References: [],
      Enabled: true,
      BuiltIn: false,
      Revision: "",
      _baseId: "",
      _baseRevision: ""
    }, source._sourceDrafts[""]));
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
    if (skillWriteOperation) return;
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
