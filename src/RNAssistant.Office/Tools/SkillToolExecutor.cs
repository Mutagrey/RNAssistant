using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class SkillToolExecutor
    {
        private readonly SkillStore _skillStore;
        private readonly SkillCatalogService _skillCatalog;

        public SkillToolExecutor(IOfficeApplicationAdapter adapter, SkillStore skillStore)
        {
            _skillStore = skillStore;
            _skillCatalog = new SkillCatalogService(adapter, skillStore);
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            var load = ControllerToolDefinition.Create(
                "common.skills_load",
                "Common",
                "Control: Activate the smallest set of relevant skill instructions before planning or using task tools. Use exact ids from SKILL_INDEX. This changes only the current chat's active guidance.",
                "{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1},\"mode\":{\"type\":\"string\",\"enum\":[\"replace\",\"add\"]}},\"required\":[\"ids\"],\"additionalProperties\":false}");
            load.UseWhen = "A SKILL_INDEX entry clearly applies and its full instructions are not listed in ACTIVE_SKILLS.";
            load.DoNotUseWhen = "The required skill is already active, or no indexed skill is relevant.";
            yield return load;
            yield return ControllerToolDefinition.Create("common.skills_list", "Common", "Read-only: List markdown skills visible to the current Office host.", "{}");
            yield return ControllerToolDefinition.Create("common.skills_read", "Common", "Read-only: Read one markdown skill by id.", "{\"id\":\"common.skill_authoring\"}");
            yield return ControllerToolDefinition.Create("common.skills_save", "Common", "Mutates settings: Create or update a versioned markdown skill with discovery, dependency, conflict, and tool-capability metadata.", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"host\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},\"version\":{\"type\":\"string\"},\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"appliesTo\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"requires\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"conflicts\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"toolCapabilities\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"resources\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"bodyMarkdown\":{\"type\":\"string\"},\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"id\",\"description\",\"bodyMarkdown\"],\"additionalProperties\":false}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.skills_delete", "Common", "Mutates settings: Delete a custom markdown skill by id.", "{\"id\":\"common.my_skill\"}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
        }

        public ToolResult ExecuteControllerTool(
            ToolCommand command,
            AppSettings settings,
            bool dryRun,
            bool manualRun,
            ChatSession session,
            IReadOnlyList<SkillDefinition> runtimeSkills = null)
        {
            if (string.Equals(command.ToolId, "common.skills_list", StringComparison.OrdinalIgnoreCase))
            {
                return ListSkills(runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_load", StringComparison.OrdinalIgnoreCase))
            {
                var ids = SkillResolver.ReadIds(command.Arguments);
                if (ids.Count == 0) return ToolResult.Fail("At least one skill id is required.", null, "missing_skill_ids", false);
                var mode = ToolArgumentReader.String(command.Arguments, "mode", "replace");
                var visibleSkills = VisibleSkills(runtimeSkills);
                var resolution = dryRun
                    ? SkillResolver.Resolve(visibleSkills, ids)
                    : SkillResolver.Activate(session, visibleSkills, ids, mode);
                return resolution.Success
                    ? ToolResult.Ok("Skills resolved: " + string.Join(", ", resolution.Skills.Select(skill => skill.Id).ToArray()), JsonConvert.SerializeObject(new
                    {
                        ids = resolution.Skills.Select(skill => skill.Id).ToArray(),
                        mode = string.Equals(mode, "add", StringComparison.OrdinalIgnoreCase) ? "add" : "replace"
                    }))
                    : ToolResult.Fail(resolution.Error, null, "invalid_skill_selection", false);
            }

            if (string.Equals(command.ToolId, "common.skills_read", StringComparison.OrdinalIgnoreCase))
            {
                return ReadSkill(command, runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_save", StringComparison.OrdinalIgnoreCase))
            {
                return SaveSkill(command, settings, dryRun, manualRun, runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_delete", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteSkill(command, settings, dryRun, manualRun, runtimeSkills);
            }

            return ToolResult.Fail("Unknown skill controller tool: " + command.ToolId);
        }

        private ToolResult ListSkills(IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var skills = VisibleSkills(runtimeSkills).Select(s => new
            {
                id = s.Id,
                host = s.Host,
                name = s.Name,
                description = s.Description,
                version = s.Version,
                tags = s.Tags,
                appliesTo = s.AppliesTo,
                requires = s.Requires,
                conflicts = s.Conflicts,
                toolCapabilities = s.ToolCapabilities,
                resources = s.Resources,
                trustLevel = s.BuiltIn ? "built_in" : "custom",
                builtIn = s.BuiltIn,
                enabled = s.Enabled
            }).ToArray();
            return ToolResult.Ok("Skills listed.", JsonConvert.SerializeObject(skills));
        }

        private ToolResult ReadSkill(ToolCommand command, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var skill = VisibleSkills(runtimeSkills).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (skill == null)
            {
                return ToolResult.Fail("Skill not found: " + id);
            }

            return ToolResult.Ok("Skill read: " + skill.Id, JsonConvert.SerializeObject(skill));
        }

        private ToolResult SaveSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var skill = ReadSkillDefinition(command);
            var visibleSkills = VisibleSkills(runtimeSkills);
            if (string.IsNullOrWhiteSpace(skill.Id))
            {
                return ToolResult.Fail("Skill id is required.");
            }

            if (string.IsNullOrWhiteSpace(skill.BodyMarkdown))
            {
                return ToolResult.Fail("Skill bodyMarkdown is required.");
            }
            if (visibleSkills.Any(item => item.BuiltIn &&
                string.Equals(item.Id, skill.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Built-in skill id is reserved: " + skill.Id, null, "reserved_skill_id", false);
            }
            string validationError;
            if (!SkillResolver.ValidateDefinition(visibleSkills, skill, out validationError))
            {
                return ToolResult.Fail(validationError, null, "invalid_skill_definition", false);
            }
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill save requires confirmation: " + skill.Id);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would save skill " + skill.Id, JsonConvert.SerializeObject(skill));
            }

            var saved = _skillStore.SaveOne(skill);
            return ToolResult.Ok("Skill saved: " + skill.Id, JsonConvert.SerializeObject(saved ?? skill));
        }

        private ToolResult DeleteSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var visibleSkills = VisibleSkills(runtimeSkills);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Skill id is required.");
            }

            if (visibleSkills.Any(s => s.BuiltIn && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Built-in skills cannot be deleted: " + id);
            }
            var dependent = visibleSkills.FirstOrDefault(skill => skill != null &&
                !string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase) &&
                (skill.Requires ?? new List<string>()).Any(required => string.Equals(required, id, StringComparison.OrdinalIgnoreCase)));
            if (dependent != null)
            {
                return ToolResult.Fail(
                    "Skill cannot be deleted because it is required by " + dependent.Id + ".",
                    null,
                    "skill_in_use",
                    false);
            }

            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill delete requires confirmation: " + id);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would delete skill " + id);
            }

            return _skillStore.Delete(id)
                ? ToolResult.Ok("Skill deleted: " + id)
                : ToolResult.Fail("Skill not found: " + id);
        }

        private SkillDefinition ReadSkillDefinition(ToolCommand command)
        {
            return new SkillDefinition
            {
                Id = ToolArgumentReader.String(command.Arguments, "id", string.Empty),
                Host = ToolArgumentReader.String(command.Arguments, "host", "Common"),
                Name = ToolArgumentReader.String(command.Arguments, "name", ToolArgumentReader.String(command.Arguments, "id", string.Empty)),
                Description = ToolArgumentReader.String(command.Arguments, "description", string.Empty),
                Version = ToolArgumentReader.String(command.Arguments, "version", "1.0.0"),
                Tags = ReadStringList(command, "tags"),
                AppliesTo = ReadStringList(command, "appliesTo"),
                Requires = ReadStringList(command, "requires"),
                Conflicts = ReadStringList(command, "conflicts"),
                ToolCapabilities = ReadStringList(command, "toolCapabilities"),
                Resources = ReadStringList(command, "resources"),
                TrustLevel = "custom",
                BodyMarkdown = ToolArgumentReader.String(command.Arguments, "bodyMarkdown", string.Empty),
                Enabled = ReadBool(command, "enabled", true),
                BuiltIn = false
            };
        }

        private static List<string> ReadStringList(ToolCommand command, string name)
        {
            object value;
            if (command != null && command.Arguments != null && command.Arguments.TryGetValue(name, out value))
            {
                return SkillResolver.ReadIds(new Dictionary<string, object> { { "ids", value } });
            }
            return new List<string>();
        }

        private static bool ReadBool(ToolCommand command, string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

        private IReadOnlyList<SkillDefinition> VisibleSkills(IReadOnlyList<SkillDefinition> runtimeSkills = null)
        {
            return runtimeSkills ?? _skillCatalog.GetVisibleSkills();
        }
    }
}
