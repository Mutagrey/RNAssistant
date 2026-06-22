function renderSkills() {
  renderResourceList({
    listId: "skillsList",
    searchInputId: "skillSearchInput",
    items: state.skills,
    emptyText: state.bridgeUnavailable ? "Office bridge недоступен. Навыки загрузятся внутри add-in." : "Навыки пока не загружены.",
    getSelectedIndex: function () { return state.selectedSkillIndex; },
    setSelectedIndex: function (index) { state.selectedSkillIndex = index; },
    matches: skillMatchesSearch,
    title: function (skill) { return skill.Id || skill.Name || "навык"; },
    enabled: function (skill) { return skill.Enabled !== false; },
    meta: function (skill) { return (skill.Host || "Common") + " - " + (skill.BuiltIn ? "built-in" : "custom"); },
    description: function (skill) { return skill.Description || ((skill.Tags || []).join(", ") || "Инструкция навыка"); },
    syncEditor: syncSelectedSkillFromEditor,
    renderEditor: renderSkillEditor,
    renderList: renderSkills
  });
}

function skillMatchesSearch(skill, query) {
  var text = [
    skill.Id || "",
    skill.Name || "",
    skill.Host || "",
    skill.Description || "",
    (skill.Tags || []).join(" ")
  ].join(" ").toLowerCase();
  return text.indexOf(query) >= 0;
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  var panel = $("skillEditorPanel");
  var empty = $("skillEditorEmpty");
  if (panel) {
    panel.classList.toggle("is-empty", disabled);
  }
  if (empty) {
    empty.querySelector(".resource-editor-empty-title").textContent = state.bridgeUnavailable ? "Навыки недоступны" : "Навык не выбран";
    empty.querySelector(".resource-editor-empty-text").textContent = state.bridgeUnavailable
      ? "Откройте RNAssistant внутри Office, чтобы загрузить built-in и пользовательские навыки."
      : "Выберите навык слева или создайте новый.";
  }
  $("skillEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("skillIdInput").value = skill ? (skill.Id || "") : "";
  $("skillHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("skillDescriptionInput").value = skill ? (skill.Description || "") : "";
  $("skillTagsInput").value = skill ? ((skill.Tags || []).join(", ")) : "";
  $("skillBodyInput").value = skill ? (skill.BodyMarkdown || "") : "";
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("skillBodyInput", $("skillBodyInput").value);
  }

  [
    "skillEnabledInput",
    "skillIdInput",
    "skillHostInput",
    "skillDescriptionInput",
    "skillTagsInput",
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
  skill.Tags = parseSkillTags($("skillTagsInput").value);
  skill.BodyMarkdown = $("skillBodyInput").value;
  skill.Enabled = $("skillEnabledInput").checked;
  skill.BuiltIn = false;
}

function readSkills() {
  syncSelectedSkillFromEditor();
  return state.skills.map(function (skill) {
    return {
      Id: skill.Id || "",
      Host: skill.Host || "Common",
      Name: skill.Name || skill.Id || "",
      Description: skill.Description || "",
      Tags: skill.Tags || [],
      BodyMarkdown: skill.BodyMarkdown || "",
      Enabled: skill.Enabled !== false,
      BuiltIn: !!skill.BuiltIn
    };
  });
}

function parseSkillTags(text) {
  return (text || "").split(",").map(function (tag) {
    return tag.trim();
  }).filter(function (tag) {
    return !!tag;
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
    "tags: " + ((skill.Tags || []).join(", ")),
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
  $("skillSearchInput").addEventListener("input", renderSkills);

  $("addSkillButton").addEventListener("click", function () {
    syncSelectedSkillFromEditor();
    state.skills.push({
      Id: "common.new_skill",
      Host: "Common",
      Name: "new_skill",
      Description: "",
      Tags: [],
      BodyMarkdown: "# Новый навык\n\nИспользуйте этот навык, когда...\n",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedSkillIndex = state.skills.length - 1;
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
      Tags: (source.Tags || []).slice(),
      BodyMarkdown: source.BodyMarkdown || "",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedSkillIndex = state.skills.length - 1;
    renderSkills();
  });

  $("saveSkillsButton").addEventListener("click", async function () {
    try {
      var response = await send("saveSkills", { skills: readSkills() });
      state.skills = response || [];
      renderSkills();
      log("Навыки сохранены.");
    } catch (error) {
      log(error.message);
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
      setChatInputText("Отредактируй RNAssistant-навык из добавленного контекста. Верни обновленный SKILL.md и при необходимости вызови common.skills_save после подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  });
}
