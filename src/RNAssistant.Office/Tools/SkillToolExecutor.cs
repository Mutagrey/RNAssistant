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
            yield return ControllerToolDefinition.Create("common.skills_read", "Common", "Read-only: Load one complete Markdown skill or a bounded chunk of one listed references/*.md file. Only a non-truncated skill result with data.loaded=true loads the skill; reference chunks never replace that evidence. Omit id only to list metadata.", SkillReadSchema());
            yield return ControllerToolDefinition.Create("common.skills_upsert", "Common", "Mutates settings: Create/update either one custom skill core or one direct references/*.md file per call. Never mix core fields with referencePath/referenceMarkdown. Omitted core fields are preserved; use strict mode only when existence itself matters.", SkillUpsertSchema(), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
            yield return ControllerToolDefinition.Create("common.skills_delete", "Common", "Mutates settings: Delete a custom skill, or delete one direct Markdown reference when referencePath is supplied.", SkillDeleteSchema(), mutatesLocalState: true, requiresConfirmation: true, riskLevel: 1);
        }

        public ToolResult ExecuteControllerTool(
            ToolCommand command,
            AppSettings settings,
            bool dryRun,
            bool manualRun,
            IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (string.Equals(command.ToolId, "common.skills_read", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(ToolArgumentReader.String(command.Arguments, "id", string.Empty)))
                {
                    return HasArgument(command, "referencePath") || HasArgument(command, "offset") || HasArgument(command, "maxChars")
                        ? ToolResult.Fail("Skill id is required when reading a reference.", null, "skill_id_required", false)
                        : ListSkills(manualRun, runtimeSkills);
                }
                return ReadSkill(command, manualRun, runtimeSkills);
            }

            if (string.Equals(command.ToolId, "common.skills_upsert", StringComparison.OrdinalIgnoreCase))
            {
                return UpsertSkill(command, settings, dryRun, manualRun);
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
                revision = SkillRevision.Compute(s),
                bodyChars = (s.BodyMarkdown ?? string.Empty).Length,
                referenceCount = (s.References ?? new List<SkillReferenceMetadata>()).Count,
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

            if (HasArgument(command, "referencePath"))
            {
                return ReadSkillReference(command, skill);
            }
            if (HasArgument(command, "offset") || HasArgument(command, "maxChars"))
            {
                return ToolResult.Fail("offset and maxChars require referencePath.", null, "skill_reference_path_required", false);
            }

            return ToolResult.Ok("Skill loaded: " + skill.Id, JsonConvert.SerializeObject(new
            {
                kind = "skill",
                loaded = true,
                complete = true,
                truncated = false,
                id = skill.Id,
                host = skill.Host,
                name = skill.Name,
                description = skill.Description,
                version = string.IsNullOrWhiteSpace(skill.Version) ? "1.0.0" : skill.Version,
                revision = SkillRevision.Compute(skill),
                bodyChars = (skill.BodyMarkdown ?? string.Empty).Length,
                enabled = skill.Enabled,
                format = "markdown",
                references = (skill.References ?? new List<SkillReferenceMetadata>()).Select(item => new
                {
                    path = item.Path,
                    byteLength = item.ByteLength,
                    revision = item.Revision
                }).ToArray(),
                bodyMarkdown = skill.BodyMarkdown ?? string.Empty
            }));
        }

        private ToolResult ReadSkillReference(ToolCommand command, SkillDefinition skill)
        {
            var referencePath = ToolArgumentReader.String(command.Arguments, "referencePath", string.Empty);
            string content;
            string error;
            SkillReferenceMetadata metadata;
            if (!_skillStore.TryReadReference(skill, referencePath, out content, out metadata, out error))
            {
                var changed = (error ?? string.Empty).IndexOf("changed after", StringComparison.OrdinalIgnoreCase) >= 0;
                return ToolResult.Fail(error, null, changed ? "skill_reference_changed" : "skill_reference_unavailable", false);
            }

            var offset = ToolArgumentReader.Int32(command.Arguments, "offset", 0);
            if (offset < 0 || offset > (content ?? string.Empty).Length)
            {
                return ToolResult.Fail("Skill reference offset is outside the file.", null, "skill_reference_offset_invalid", false);
            }
            var maxChars = Math.Max(1, Math.Min(50000, ToolArgumentReader.Int32(command.Arguments, "maxChars", 24000)));
            var end = Math.Min(content.Length, offset + maxChars);
            if (end > offset && end < content.Length && char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end]))
            {
                end += 1;
            }
            var complete = end >= content.Length;
            return ToolResult.Ok("Skill reference read: " + metadata.Path, JsonConvert.SerializeObject(new
            {
                kind = "reference",
                id = skill.Id,
                skillRevision = SkillRevision.Compute(skill),
                path = metadata.Path,
                revision = metadata.Revision,
                format = "markdown",
                offset = offset,
                returnedChars = end - offset,
                totalChars = content.Length,
                complete = complete,
                nextOffset = complete ? (int?)null : end,
                content = content.Substring(offset, end - offset)
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

        private ToolResult UpsertSkill(ToolCommand command, AppSettings settings, bool dryRun, bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            if (_skillCatalog.GetVisibleSkills().Any(item => item.BuiltIn && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Fail("Built-in skill id is reserved: " + id, null, "reserved_skill_id", false);
            }

            var existing = _skillStore.Load().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (HasArgument(command, "referencePath") || HasArgument(command, "referenceMarkdown"))
            {
                return UpsertSkillReference(command, existing, settings, dryRun, manualRun);
            }
            if (existing != null && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Skill already exists: " + id + ". Use mode=upsert or updateOnly.", null, "skill_already_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Custom skill not found: " + id + ". Use mode=upsert or createOnly.", null, "skill_not_found", false);
            }
            if (existing != null && !HasMutableArguments(command))
            {
                return ToolResult.Fail("Skill update requires at least one supplied field besides id/mode.", null, "skill_update_empty", true);
            }

            if (existing == null)
            {
                return PersistSkill(ReadSkillDefinition(command), settings, dryRun, manualRun, "create");
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

        private ToolResult UpsertSkillReference(
            ToolCommand command,
            SkillDefinition skill,
            AppSettings settings,
            bool dryRun,
            bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            if (!HasArgument(command, "referencePath") || !HasArgument(command, "referenceMarkdown"))
            {
                return ToolResult.Fail("referencePath and referenceMarkdown must be supplied together.", null, "invalid_skill_reference", false);
            }
            if (new[] { "host", "name", "description", "version", "bodyMarkdown", "enabled" }.Any(name => HasArgument(command, name)))
            {
                return ToolResult.Fail("Update a skill core and a reference in separate calls.", null, "mixed_skill_reference_update", false);
            }
            if (skill == null)
            {
                return ToolResult.Fail("Custom skill not found: " + id, null, "skill_not_found", false);
            }

            string normalizedPath;
            if (!SkillStore.TryNormalizeReferencePath(
                ToolArgumentReader.String(command.Arguments, "referencePath", string.Empty), out normalizedPath))
            {
                return ToolResult.Fail("Reference path must be one Markdown file directly under references/.", null, "invalid_skill_reference", false);
            }
            var existing = (skill.References ?? new List<SkillReferenceMetadata>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            var mode = ToolArgumentReader.String(command.Arguments, "mode", "upsert");
            if (existing != null && string.Equals(mode, "createOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Skill reference already exists: " + normalizedPath, null, "skill_reference_exists", false);
            }
            if (existing == null && string.Equals(mode, "updateOnly", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Skill reference not found: " + normalizedPath, null, "skill_reference_not_found", false);
            }
            var operation = existing == null ? "create" : "update";
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill reference " + operation + " requires confirmation: " + normalizedPath);
            }
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would " + operation + " skill reference " + normalizedPath);
            }

            string error;
            SkillReferenceMetadata savedReference;
            if (!_skillStore.TrySaveReference(
                skill,
                normalizedPath,
                ToolArgumentReader.String(command.Arguments, "referenceMarkdown", string.Empty),
                out savedReference,
                out error))
            {
                return ToolResult.Fail(error, null, "invalid_skill_reference", false);
            }
            var savedSkill = _skillStore.Load().First(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return ToolResult.Ok("Skill reference " + (operation == "create" ? "created: " : "updated: ") + normalizedPath,
                SkillReferenceMutationJson(savedSkill, savedReference, false));
        }

        private ToolResult PersistSkill(SkillDefinition skill, AppSettings settings, bool dryRun, bool manualRun, string operation)
        {
            var validationError = SkillStore.ValidateDefinition(skill);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                return ToolResult.Fail(validationError, null, "invalid_skill_definition", false);
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
            var customSkill = _skillStore.Load().FirstOrDefault(skill => skill != null &&
                string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase));
            if (customSkill == null)
            {
                return ToolResult.Fail("Custom skill not found: " + id, null, "skill_not_found", false);
            }
            if (HasArgument(command, "referencePath"))
            {
                return DeleteSkillReference(command, customSkill, settings, dryRun, manualRun);
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

        private ToolResult DeleteSkillReference(
            ToolCommand command,
            SkillDefinition skill,
            AppSettings settings,
            bool dryRun,
            bool manualRun)
        {
            string normalizedPath;
            if (!SkillStore.TryNormalizeReferencePath(
                ToolArgumentReader.String(command.Arguments, "referencePath", string.Empty), out normalizedPath))
            {
                return ToolResult.Fail("Reference path must be one Markdown file directly under references/.", null, "invalid_skill_reference", false);
            }
            var existing = (skill.References ?? new List<SkillReferenceMetadata>()).FirstOrDefault(item => item != null &&
                string.Equals(item.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return ToolResult.Fail("Skill reference not found: " + normalizedPath, null, "skill_reference_not_found", false);
            }
            if (!dryRun && !manualRun && !(settings ?? new AppSettings()).AutoConfirmToolActions)
            {
                return ToolResult.WaitingConfirmation("Skill reference delete requires confirmation: " + normalizedPath);
            }
            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would delete skill reference " + normalizedPath);
            }

            string error;
            if (!_skillStore.TryDeleteReference(skill, normalizedPath, out error))
            {
                return ToolResult.Fail(error, null, "skill_reference_delete_failed", false);
            }
            var savedSkill = _skillStore.Load().First(item => string.Equals(item.Id, skill.Id, StringComparison.OrdinalIgnoreCase));
            return ToolResult.Ok("Skill reference deleted: " + normalizedPath,
                SkillReferenceMutationJson(savedSkill, existing, true));
        }

        private static string SkillReferenceMutationJson(
            SkillDefinition skill,
            SkillReferenceMetadata reference,
            bool deleted)
        {
            return JsonConvert.SerializeObject(new
            {
                id = skill == null ? string.Empty : skill.Id,
                revision = SkillRevision.Compute(skill),
                deleted = deleted,
                reference = reference,
                references = skill == null ? new SkillReferenceMetadata[0] :
                    (skill.References ?? new List<SkillReferenceMetadata>()).ToArray()
            });
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

        private static string SkillUpsertSchema()
        {
            var commonProperties = new JObject
            {
                ["id"] = new JObject { ["type"] = "string", ["description"] = "Exact stable custom skill id.", ["minLength"] = 1, ["maxLength"] = 128 },
                ["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Existence policy for the selected core or reference resource; upsert is normally sufficient.",
                    ["enum"] = new JArray("upsert", "createOnly", "updateOnly"),
                    ["default"] = "upsert"
                }
            };
            var coreProperties = (JObject)commonProperties.DeepClone();
            coreProperties["host"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Office host where the skill is visible.",
                ["enum"] = new JArray("Common", "Excel", "Word", "PowerPoint", "Outlook")
            };
            coreProperties["name"] = new JObject { ["type"] = "string", ["description"] = "Human-readable skill name.", ["maxLength"] = 200 };
            coreProperties["description"] = new JObject { ["type"] = "string", ["description"] = "Concise catalog description used by the model to decide whether to load this skill.", ["maxLength"] = 4000 };
            coreProperties["version"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Semantic version such as 1.0.0."
            };
            coreProperties["bodyMarkdown"] = new JObject { ["type"] = "string", ["description"] = "Complete Markdown instructions for the skill core; references are written in separate calls.", ["maxLength"] = 500000 };
            coreProperties["enabled"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether the skill is enabled and appears in Agent context."
            };

            var referenceProperties = (JObject)commonProperties.DeepClone();
            referenceProperties["referencePath"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact path references/<name>.md directly under references/; this call must contain no skill-core fields.",
                ["minLength"] = 1,
                ["maxLength"] = 260
            };
            referenceProperties["referenceMarkdown"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Complete UTF-8 Markdown content for referencePath.",
                ["maxLength"] = SkillStore.MaximumSkillReferenceCharacters
            };

            var allProperties = (JObject)coreProperties.DeepClone();
            allProperties["referencePath"] = referenceProperties["referencePath"].DeepClone();
            allProperties["referenceMarkdown"] = referenceProperties["referenceMarkdown"].DeepClone();
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = allProperties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = coreProperties,
                        ["required"] = new JArray("id"),
                        ["additionalProperties"] = false
                    },
                    new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = referenceProperties,
                        ["required"] = new JArray("id", "referencePath", "referenceMarkdown"),
                        ["additionalProperties"] = false
                    }
                }
            }.ToString(Formatting.None);
        }

        private static string SkillDeleteSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["id"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact stable custom skill id.",
                        ["minLength"] = 1,
                        ["maxLength"] = 128
                    },
                    ["referencePath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Exact direct references/*.md path to delete; omit to delete the entire custom skill.",
                        ["maxLength"] = 260
                    }
                },
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false
            }.ToString(Formatting.None);
        }

        private static string SkillReadSchema()
        {
            var properties = new JObject
            {
                ["id"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact skill id from RUNTIME_CONTEXT.skills; omit all arguments only to list metadata.",
                    ["maxLength"] = 128
                },
                ["referencePath"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact references/*.md path listed by a previously loaded skill.",
                    ["maxLength"] = 260
                },
                ["offset"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Zero-based character offset for a reference chunk; valid only with referencePath.",
                    ["minimum"] = 0
                },
                ["maxChars"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum reference characters returned; valid only with referencePath.",
                    ["minimum"] = 1,
                    ["maximum"] = 50000
                }
            };
            Func<IEnumerable<string>, IEnumerable<string>, JObject> variant = (allowed, required) =>
            {
                var selected = new JObject();
                foreach (var name in allowed) selected[name] = properties[name].DeepClone();
                return new JObject
                {
                    ["type"] = "object",
                    ["properties"] = selected,
                    ["required"] = new JArray(required),
                    ["additionalProperties"] = false
                };
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(),
                ["additionalProperties"] = false,
                ["anyOf"] = new JArray
                {
                    variant(new string[0], new string[0]),
                    variant(new[] { "id" }, new[] { "id" }),
                    variant(new[] { "id", "referencePath", "offset", "maxChars" }, new[] { "id", "referencePath" })
                }
            }.ToString(Formatting.None);
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private static bool HasMutableArguments(ToolCommand command)
        {
            return command != null && command.Arguments != null && command.Arguments.Keys.Any(name =>
                !string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "mode", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetString(ToolCommand command, string name, Action<string> apply)
        {
            if (HasArgument(command, name) && apply != null) apply(ToolArgumentReader.String(command.Arguments, name, string.Empty));
        }

    }
}
