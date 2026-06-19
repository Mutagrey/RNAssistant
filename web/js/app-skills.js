function renderSkills() {
  var list = $("skillsList");
  if (!list) {
    return;
  }

  list.innerHTML = "";
  if (!state.skills.length) {
    state.selectedSkillIndex = -1;
    renderSkillEditor();
    return;
  }

  if (state.selectedSkillIndex < 0 || state.selectedSkillIndex >= state.skills.length) {
    state.selectedSkillIndex = 0;
  }

  state.skills.forEach(function (skill, index) {
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
  });

  renderSkillEditor();
}

function renderSkillEditor() {
  var skill = state.skills[state.selectedSkillIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  $("skillEditorTitle").textContent = skill ? (skill.Id || "skill") : "No skill selected";
  $("skillEditorMeta").textContent = skill ? (builtIn ? "Built-in skill" : (skill.StoragePath || "Custom skill")) : "";
  $("skillEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("skillIdInput").value = skill ? (skill.Id || "") : "";
  $("skillHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("skillDescriptionInput").value = skill ? (skill.Description || "") : "";
  $("skillTagsInput").value = skill ? ((skill.Tags || []).join(", ")) : "";
  $("skillBodyInput").value = skill ? (skill.BodyMarkdown || "") : "";

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

  $("deleteSkillButton").disabled = disabled || builtIn;
  $("cloneSkillButton").disabled = disabled;
  $("copySkillContextButton").disabled = disabled;
  $("askSkillBuilderButton").disabled = disabled;
}

function syncSelectedSkillFromEditor() {
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
