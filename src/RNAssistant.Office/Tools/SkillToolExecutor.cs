using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            yield return ControllerToolDefinition.Create("common.skills_list", "Common", "Read-only: List Markdown skills available to the current execution.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create("common.skills_read", "Common", "Read-only: Load complete metadata and Markdown instructions for one exact skill id.", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact skill id from RUNTIME_CONTEXT.skills or common.skills_list.\"}},\"required\":[\"id\"],\"additionalProperties\":false}");
            yield return ControllerToolDefinition.Create("common.skills_create", "Common", "Mutates settings: Create a new Markdown skill; fails if the id already exists.", SkillPayloadSchema(false), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.skills_update", "Common", "Mutates settings: Update only supplied fields of an existing custom Markdown skill; omitted fields are preserved.", SkillPayloadSchema(true), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.skills_delete", "Common", "Mutates settings: Delete a custom markdown skill by id.", "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"description\":\"Exact stable identifier.\"}},\"required\":[\"id\"],\"additionalProperties\":false}", mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
        }

        public ToolResult ExecuteControllerTool(
            ToolCommand command,
            AppSettings settings,
            bool dryRun,
            bool manualRun,
            IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (string.Equals(command.ToolId, "common.skills_list", StringComparison.OrdinalIgnoreCase))
            {
                return ListSkills(manualRun, runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_read", StringComparison.OrdinalIgnoreCase))
            {
                return ReadSkill(command, manualRun, runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_create", StringComparison.OrdinalIgnoreCase))
            {
                return CreateSkill(command, settings, dryRun, manualRun);
            }

            if (string.Equals(command.ToolId, "common.skills_update", StringComparison.OrdinalIgnoreCase))
            {
                return UpdateSkill(command, settings, dryRun, manualRun);
            }

            if (string.Equals(command.ToolId, "common.skills_delete", StringComparison.OrdinalIgnoreCase))
            {
                return DeleteSkill(command, settings, dryRun, manualRun);
            }

            return ToolResult.Fail("Unknown skill controller tool: " + command.ToolId);
        }

        private ToolResult ListSkills(bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var source = RuntimeSkillSource(manualRun, runtimeSkills);
            var skills = source.Select(s => new
            {
                id = s.Id,
                host = s.Host,
                name = s.Name,
                description = s.Description,
                version = s.Version,
                builtIn = s.BuiltIn,
                enabled = s.Enabled
            }).ToArray();
            return ToolResult.Ok("Skills listed.", JsonConvert.SerializeObject(skills));
        }

        private ToolResult ReadSkill(ToolCommand command, bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var source = RuntimeSkillSource(manualRun, runtimeSkills);
            var skill = source.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (skill == null)
            {
                return ToolResult.Fail("Skill not found: " + id);
            }

            return ToolResult.Ok("Skill loaded: " + skill.Id, JsonConvert.SerializeObject(new
            {
                id = skill.Id,
                host = skill.Host,
                name = skill.Name,
                description = skill.Description,
                version = string.IsNullOrWhiteSpace(skill.Version) ? "1.0.0" : skill.Version,
                enabled = skill.Enabled,
                format = "markdown",
                bodyMarkdown = skill.BodyMarkdown ?? string.Empty
            }));
        }

        private IReadOnlyList<SkillDefinition> RuntimeSkillSource(
            bool manualRun,
            IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (runtimeSkills != null)
            {
                return runtimeSkills.Where(item => item != null && item.Enabled).ToList();
            }
            return manualRun
                ? (IReadOnlyList<SkillDefinition>)_skillCatalog.GetVisibleSkills()
                : new SkillDefinition[0];
        }

        private ToolResult CreateSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var skill = ReadSkillDefinition(command);
            if (_skillStore.Load().Any(item => string.Equals(item.Id, skill.Id, StringComparison.OrdinalIgnoreCase)) ||
                _skillCatalog.GetVisibleSkills().Any(item => item.BuiltIn && string.Equals(item.Id, skill.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Skill already exists: " + skill.Id + ". Use common.skills_update.", null, "skill_already_exists", false);
            }
            return PersistSkill(skill, settings, dryRun, manualRun, "create");
        }

        private ToolResult UpdateSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (_skillCatalog.GetVisibleSkills().Any(item => item.BuiltIn && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Built-in skill id is reserved: " + id, null, "reserved_skill_id", false);
            }
            var existing = _skillStore.Load().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return ToolResult.Fail("Custom skill not found: " + id + ". Use common.skills_create.", null, "skill_not_found", false);
            }
            var skill = existing;
            SetString(command, "host", value => skill.Host = value);
            SetString(command, "name", value => skill.Name = value);
            SetString(command, "description", value => skill.Description = value);
            SetString(command, "version", value => skill.Version = value);
            SetString(command, "bodyMarkdown", value => skill.BodyMarkdown = value);
            if (HasArgument(command, "enabled")) skill.Enabled = ReadBool(command, "enabled", skill.Enabled);
            return PersistSkill(skill, settings, dryRun, manualRun, "update");
        }

        private ToolResult PersistSkill(SkillDefinition skill, AppSettings settings, bool dryRun, bool manualRun, string operation)
        {
            var validationError = SkillStore.ValidateDefinition(skill);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return ToolResult.Fail(validationError, null, "invalid_skill_definition", false);
            }
            if (string.IsNullOrWhiteSpace(skill.BodyMarkdown)) return ToolResult.Fail("Skill bodyMarkdown is required.");
            if (string.IsNullOrWhiteSpace(skill.Description))
            {
                return ToolResult.Fail("Skill description is required.", null, "invalid_skill_definition", false);
            }
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill " + operation + " requires confirmation: " + skill.Id);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would " + operation + " skill " + skill.Id, JsonConvert.SerializeObject(skill));
            }

            var saved = _skillStore.SaveOne(skill);
            return ToolResult.Ok("Skill " + (operation == "create" ? "created: " : "updated: ") + skill.Id, JsonConvert.SerializeObject(saved ?? skill));
        }

        private ToolResult DeleteSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var visibleSkills = _skillCatalog.GetVisibleSkills();
            if (string.IsNullOrWhiteSpace(id))
            {
                return ToolResult.Fail("Skill id is required.");
            }

            if (visibleSkills.Any(s => s.BuiltIn && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
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
                Version = ToolArgumentReader.String(command.Arguments, "version", "1.0.0"),
                BodyMarkdown = ToolArgumentReader.String(command.Arguments, "bodyMarkdown", string.Empty),
                Enabled = ReadBool(command, "enabled", true),
                BuiltIn = false
            };
        }

        private static bool ReadBool(ToolCommand command, string name, bool fallback)
        {
            var raw = ToolArgumentReader.String(command.Arguments, name, fallback ? "true" : "false");
            bool value;
            return bool.TryParse(raw, out value) ? value : fallback;
        }

        private static string SkillPayloadSchema(bool update)
        {
            var host = new JObject
            {
                ["type"] = "string",
                ["description"] = "Office host where the skill is visible.",
                ["enum"] = new JArray("Common", "Excel", "Word", "PowerPoint", "Outlook")
            };
            var version = new JObject
            {
                ["type"] = "string",
                ["description"] = "Semantic version such as 1.0.0."
            };
            var enabled = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether the skill is enabled and appears in Agent context."
            };
            if (!update)
            {
                host["default"] = "Common";
                version["default"] = "1.0.0";
                enabled["default"] = true;
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject { ["type"] = "string", ["description"] = "Exact stable custom skill id.", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["host"] = host,
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "Human-readable skill name.", ["maxLength"] = 200 },
                    ["description"] = new JObject { ["type"] = "string", ["description"] = "Concise catalog description used by the model to decide whether to load this skill.", ["maxLength"] = 4000 },
                    ["version"] = version,
                    ["bodyMarkdown"] = new JObject { ["type"] = "string", ["description"] = "Complete Markdown instructions for the skill.", ["maxLength"] = 500000 },
                    ["enabled"] = enabled
                },
                ["required"] = update
                    ? new JArray("id")
                    : new JArray("id", "description", "bodyMarkdown"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static void SetString(ToolCommand command, string name, Action<string> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ToolArgumentReader.String(command.Arguments, name, string.Empty));
        }

    }
}
