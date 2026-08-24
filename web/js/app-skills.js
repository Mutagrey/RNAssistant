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

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  var panel = $("skillEditorPanel");
  if (panel) panel.classList.toggle("hidden", disabled);
  if ($("skillTitle")) $("skillTitle").textContent = skill ? (skill.Id || skill.Name || "Навык") : "Навык";
  if ($("skillMeta")) $("skillMeta").textContent = skill ? ((skill.BuiltIn ? "Встроенный навык" : "Пользовательский навык") + " · " + (skill.Host || "Common")) : "";
  $("skillEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("skillIdInput").value = skill ? (skill.Id || "") : "";
  $("skillHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("skillDescriptionInput").value = skill ? (skill.Description || "") : "";
  $("skillBodyInput").value = skill ? (skill.BodyMarkdown || "") : "";
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("skillBodyInput", $("skillBodyInput").value);
  }
  renderSkillPreview();
  applyInstructionMode();

  [
    "skillEnabledInput",
    "skillIdInput",
    "skillHostInput",
    "skillDescriptionInput",
    "skillBodyInput"
  ].forEach(function (id) {
    $(id).disabled = disabled || builtIn;
  });
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("skillBodyInput", disabled || builtIn);
  }

  $("deleteSkillButton").disabled = disabled || builtIn;
  $("cloneSkillButton").disabled = disabled;
  $("copySkillContextButton").disabled = disabled;
  $("askSkillBuilderButton").disabled = disabled;
  $("addSkillButton").disabled = !!state.bridgeUnavailable;
  $("saveSkillsButton").disabled = !!state.bridgeUnavailable;
}

function syncSelectedSkillFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["skillBodyInput"]);
  }
  var skill = state.skills[state.selectedSkillIndex];
  if (!skill || skill.BuiltIn) {
    return;
  }

  skill.Id = $("skillIdInput").value.trim();
  skill.Host = $("skillHostInput").value;
  skill.Name = skill.Id;
  skill.Description = $("skillDescriptionInput").value;
  skill.BodyMarkdown = $("skillBodyInput").value;
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

  return [
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
  ].join("\n");
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
      BodyMarkdown: "# Новый навык\n\nИспользуйте этот навык, когда...\n",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedSkillIndex = state.skills.length - 1;
    state.selectedInstructionKind = "skill";
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
      BodyMarkdown: source.BodyMarkdown || "",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedSkillIndex = state.skills.length - 1;
    state.selectedInstructionKind = "skill";
    renderSkills();
  });

  $("saveSkillsButton").addEventListener("click", async function () {
    setControlBusy("saveSkillsButton", true);
    try {
      var response = await send("saveSkills", { skills: readSkills() });
      state.skills = response || [];
      renderSkills();
      log("Навыки сохранены.");
    } catch (error) {
      log(error.message, "error");
    } finally {
      setControlBusy("saveSkillsButton", false);
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
      setChatInputText("Отредактируй RNAssistant-навык из добавленного контекста. Сначала прочитай текущий skill, затем при необходимости вызови common.skills_update только с изменёнными полями после подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message, "error");
    });
  });
}
