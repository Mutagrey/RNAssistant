function renderSkills() {
  var list = $("skillsList");
  var query = (($("skillSearchInput") && $("skillSearchInput").value) || "").trim().toLowerCase();
  if (!list) {
    return;
  }

  list.innerHTML = "";
  if (!state.skills.length) {
    state.selectedSkillIndex = -1;
    list.appendChild(createSkillEmptyState("Skills пока не загружены."));
    renderSkillEditor();
    return;
  }

  if (state.selectedSkillIndex < 0 || state.selectedSkillIndex >= state.skills.length) {
    state.selectedSkillIndex = 0;
  }

  var rendered = 0;
  state.skills.forEach(function (skill, index) {
    if (query && !skillMatchesSearch(skill, query)) {
      return;
    }

    var item = document.createElement("button");
    item.type = "button";
    item.className = "tool-list-item" + (index === state.selectedSkillIndex ? " active" : "");
    item.innerHTML = "<div class=\"tool-list-title\"></div><div class=\"tool-list-meta\"></div>";
    item.querySelector(".tool-list-title").textContent = skill.Id || skill.Name || "skill";
    item.querySelector(".tool-list-meta").textContent = (skill.Host || "Common") + " - " + (skill.BuiltIn ? "built-in" : "custom");
    item.addEventListener("click", function () {
      syncSelectedSkillFromEditor();
      state.selectedSkillIndex = index;
      renderSkills();
    });
    list.appendChild(item);
    rendered += 1;
  });

  if (!rendered) {
    list.appendChild(createSkillEmptyState("Ничего не найдено."));
  }

  renderSkillEditor();
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

function createSkillEmptyState(text) {
  var node = document.createElement("div");
  node.className = "tool-list-empty";
  node.textContent = text;
  return node;
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  $("skillEditorTitle").textContent = skill ? (skill.Id || "skill") : "Skill не выбран";
  $("skillEditorMeta").textContent = skill ? (builtIn ? "Встроенный skill" : (skill.StoragePath || "Custom skill")) : "Выберите skill из списка или создайте новый.";
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
  log("Skill context added to chat context.");
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
      BodyMarkdown: "# New skill\n\nUse this skill when...\n",
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
      log("Skills saved.");
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
    log("Skill context copied.");
  });

  $("askSkillBuilderButton").addEventListener("click", function () {
    addSelectedSkillContextToContext().then(function (added) {
      if (!added) {
        return;
      }

      $("chatInput").value = "Отредактируй RNAssistant skill из добавленного контекста. Верни обновленный SKILL.md и при необходимости вызови common.skills_save после подтверждения.";
      switchTab("chat");
      $("chatInput").focus();
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  });
}
