using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Tools
{
    internal sealed class SkillToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly SkillStore _skillStore;

        public SkillToolExecutor(IOfficeApplicationAdapter adapter, SkillStore skillStore)
        {
            _adapter = adapter;
            _skillStore = skillStore;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerTool("common.skills_list", "Read-only: List markdown skills visible to the current Office host.", "{}", false);
            yield return ControllerTool("common.skills_read", "Read-only: Read one markdown skill by id.", "{\"id\":\"common.skill_authoring\"}", false);
            yield return ControllerTool("common.skills_save", "Mutates settings: Create or update a markdown skill SKILL.md file.", "{\"id\":\"common.my_skill\",\"host\":\"Common\",\"name\":\"My skill\",\"description\":\"When to use it\",\"tags\":\"tag1, tag2\",\"bodyMarkdown\":\"# My skill\\n...\",\"enabled\":true}", true);
            yield return ControllerTool("common.skills_delete", "Mutates settings: Delete a custom markdown skill by id.", "{\"id\":\"common.my_skill\"}", true);
        }

        public bool IsControllerTool(string toolId)
        {
            return GetControllerTool(toolId) != null;
        }

        public ToolDefinition GetControllerTool(string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return null;
            }

            return GetControllerTools().FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (string.Equals(command.ToolId, "common.skills_list", StringComparison.OrdinalIgnoreCase))
            {
                return ListSkills();
            }

            if (string.Equals(command.ToolId, "common.skills_read", StringComparison.OrdinalIgnoreCase))
            {
                return ReadSkill(command);
            }

            if (string.Equals(command.ToolId, "common.skills_save", StringComparison.OrdinalIgnoreCase))
            {
                return SaveSkill(command, settings, dryRun, manualRun);
            }

            if (string.Equals(command.ToolId, "common.skills_delete", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteSkill(command, settings, dryRun, manualRun);
            }

            return ToolResult.Fail("Unknown skill controller tool: " + command.ToolId);
        }

        private ToolResult ListSkills()
        {
            var skills = VisibleSkills().Select(s => new
            {
                id = s.Id,
                host = s.Host,
                name = s.Name,
                description = s.Description,
                tags = s.Tags,
                builtIn = s.BuiltIn,
                enabled = s.Enabled
            }).ToArray();
            return ToolResult.Ok("Skills listed.", JsonConvert.SerializeObject(skills));
        }

        private ToolResult ReadSkill(ToolCommand command)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var skill = VisibleSkills().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (skill == null)
            {
                return ToolResult.Fail("Skill not found: " + id);
            }

            return ToolResult.Ok("Skill read: " + skill.Id, JsonConvert.SerializeObject(skill));
        }

        private ToolResult SaveSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill save requires confirmation: " + ToolArgumentReader.String(command.Arguments, "id", string.Empty));
            }

            var skill = ReadSkillDefinition(command);
            if (string.IsNullOrWhiteSpace(skill.Id))
            {
                return ToolResult.Fail("Skill id is required.");
            }

            if (string.IsNullOrWhiteSpace(skill.BodyMarkdown))
            {
                return ToolResult.Fail("Skill bodyMarkdown is required.");
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would save skill " + skill.Id, JsonConvert.SerializeObject(skill));
            }

            var saved = _skillStore.SaveOne(skill);
            return ToolResult.Ok("Skill saved: " + skill.Id, JsonConvert.SerializeObject(saved ?? skill));
        }

        private ToolResult DeleteSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Skill id is required.");
            }

            if (VisibleSkills().Any(s => s.BuiltIn && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Built-in skills cannot be deleted: " + id);
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
                Tags = ReadTags(command),
                BodyMarkdown = ToolArgumentReader.String(command.Arguments, "bodyMarkdown", string.Empty),
                Enabled = ReadBool(command, "enabled", true),
                BuiltIn = false
            };
        }

        private static List<string> ReadTags(ToolCommand command)
        {
            var raw = ToolArgumentReader.String(command.Arguments, "tags", string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            try
            {
                var token = JToken.Parse(raw);
                var array = token as JArray;
                if (array != null)
                {
                    return array.Select(t => Convert.ToString(t)).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                }
            }
            catch (JsonException)
            {
            }

            return raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
        }

        private static bool ReadBool(ToolCommand command, string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

        private IEnumerable<SkillDefinition> VisibleSkills()
        {
            var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var skill in BuiltInSkillProvider.GetSkills(_adapter).Where(IsVisible))
            {
                result[skill.Id] = skill;
            }

            foreach (var skill in _skillStore.Load().Where(IsVisible))
            {
                result[skill.Id] = skill;
            }

            return result.Values.OrderBy(s => s.Host).ThenBy(s => s.Id);
        }

        private bool IsVisible(SkillDefinition skill)
        {
            return skill != null &&
                (string.Equals(skill.Host, _adapter.HostName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(skill.Host, "Common", StringComparison.OrdinalIgnoreCase));
        }

        private static ToolDefinition ControllerTool(string id, string description, string schema, bool requiresConfirmation)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = "Common",
                Name = id,
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true,
                RequiresConfirmation = requiresConfirmation,
                MutatesDocument = false,
                MutatesLocalState = requiresConfirmation,
                AgentCanRun = true,
                RiskLevel = requiresConfirmation ? 1 : 0
            };
        }
    }
}
