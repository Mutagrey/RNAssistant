var skillReferenceLoadSequence = 0;

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
  return (reference && (reference.Path || reference.path)) || "";
}

function ensureSkillReferenceState(skill) {
  if (!skill) return;
  if (!skill.References) skill.References = [];
  if (!skill._referenceDrafts) skill._referenceDrafts = {};
  if (!skill._referenceLoaded) skill._referenceLoaded = {};
  if (!skill._referenceLoading) skill._referenceLoading = {};
  if (!skill._referenceLoadTokens) skill._referenceLoadTokens = {};
  if (!skill._referenceDirty) skill._referenceDirty = {};
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
  var storagePath = String(skill && (skill.StoragePath || skill.storagePath) || "").toLowerCase();
  return storagePath ? "path:" + storagePath : "id:" + String(skill && (skill.Id || skill.id) || "").toLowerCase();
}

function skillLibraryRecords(skills) {
  return writableSkillLibraryItems(skills).map(function (skill) {
    return {
      entity: skill,
      identity: skillLibraryIdentity(skill),
      id: String(skill.Id || "").toLowerCase(),
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
    if (skill._referenceLoading[path] && !skill._referenceLoaded[path]) return;
    if (!skill._referenceLoaded[path] || skill._referenceDrafts[path] !== value) {
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
  var transient = {};
  (state.skills || []).forEach(function (skill) {
    if (!skill || !skill.Id) return;
    ensureSkillReferenceState(skill);
    transient[String(skill.Id).toLowerCase()] = {
      selected: skill._selectedReferencePath || "",
      drafts: skill._referenceDrafts,
      loaded: skill._referenceLoaded,
      loading: skill._referenceLoading,
      loadTokens: skill._referenceLoadTokens,
      dirty: skill._referenceDirty,
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
    skill._referenceLoading = saved.loading;
    skill._referenceLoadTokens = saved.loadTokens;
    skill._referenceDirty = saved.dirty;
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
  var requestId = ++skillReferenceLoadSequence;
  skill._referenceLoading[path] = requestId;
  skill._referenceLoadTokens[path] = requestId;
  updateSkillSaveButton();
  if (selectedSkillReferencePath(skill) === path && typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("skillBodyInput", true);
  }
  try {
    var response = await send("readSkillReference", { skillId: skill.Id || "", path: path });
    if (skill._referenceLoadTokens[path] !== requestId || skill._referenceLoading[path] !== requestId ||
      !(skill.References || []).some(function (item) {
        return skillReferencePath(item).toLowerCase() === path.toLowerCase();
      })) return;
    if (skill._referenceDirty[path]) return;
    skill._referenceDrafts[path] = (response && response.content) || "";
    skill._referenceLoaded[path] = true;
    delete skill._referenceDirty[path];
    mergeSkillReferenceMetadata(skill, response && response.references);
    if (state.skills[state.selectedSkillIndex] === skill && selectedSkillReferencePath(skill) === path) {
      setSkillEditorValue(skill._referenceDrafts[path]);
      renderSkillPreview();
    }
  } catch (error) {
    if (skill._referenceLoadTokens[path] === requestId) log(error.detail || error.message, "error");
  } finally {
    if (skill._referenceLoading[path] === requestId) delete skill._referenceLoading[path];
    if (state.skills[state.selectedSkillIndex] === skill && typeof setCodeEditorReadOnly === "function") {
      var selectedPath = selectedSkillReferencePath(skill);
      setCodeEditorReadOnly("skillBodyInput", !!skill.BuiltIn || !!(selectedPath && skill._referenceLoading[selectedPath]));
    }
    updateSkillSaveButton();
  }
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  ensureSkillReferenceState(skill);
  var referencePath = selectedSkillReferencePath(skill);
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
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("skillBodyInput", disabled || builtIn || !!(referencePath && skill._referenceLoading[referencePath]));
  }

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

function readSkills() {
  syncSelectedSkillFromEditor();
  return state.skills.map(function (skill) {
    return {
      Id: skill.Id || "",
      Host: skill.Host || "Common",
      Name: skill.Name || skill.Id || "",
      Description: skill.Description || "",
      Version: skill.Version || "1.0.0",
      BodyMarkdown: skill.BodyMarkdown || "",
      Enabled: skill.Enabled !== false,
      BuiltIn: !!skill.BuiltIn
    };
  });
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
  var selectedId = skill.Id || "";
  var response = await send("saveSkills", { skills: readSkills() });
  state.skills = preserveSkillReferenceState(response || []);
  state.selectedSkillIndex = state.skills.findIndex(function (item) {
    return String((item && item.Id) || "").toLowerCase() === String(selectedId).toLowerCase();
  });
  if (state.selectedSkillIndex < 0 && state.skills.length) state.selectedSkillIndex = 0;
  var savedReferences = 0;
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
        skillId: saved.Id || "",
        path: referencePath,
        content: referenceContent
      });
      mergeSkillReferenceMetadata(saved, referenceResponse && referenceResponse.references);
      saved._referenceDrafts[referencePath] = (referenceResponse && referenceResponse.content) || referenceContent;
      saved._referenceLoaded[referencePath] = true;
      delete saved._referenceDirty[referencePath];
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
    var response = await send("deleteSkillReference", { skillId: skill.Id || "", path: path });
    mergeSkillReferenceMetadata(skill, response && response.references);
  }
  delete skill._referenceDrafts[path];
  delete skill._referenceLoaded[path];
  delete skill._referenceLoading[path];
  delete skill._referenceLoadTokens[path];
  delete skill._referenceDirty[path];
  skill._selectedReferencePath = "";
  updateSkillLibraryDirty();
  renderSkillEditor();
  log("Reference удалён: " + path);
}

function bindSkillActions() {
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
      BuiltIn: false
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
      BuiltIn: false
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
