using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class CapabilityDiscoveryExecutor
    {
        public const string SearchToolId = "common.capabilities_search";
        public const string ReadToolId = "common.capabilities_read";
        public const int MaximumDescriptorCharacters = 24000;

        private const int MaximumSearchPageSize = 20;
        private const int MaximumQueryCharacters = 200;
        private const int MaximumNameCharacters = 100;
        private const int MaximumSummaryCharacters = 160;

        private readonly SkillToolExecutor _skillExecutor;

        public CapabilityDiscoveryExecutor(SkillToolExecutor skillExecutor)
        {
            _skillExecutor = skillExecutor;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(
                SearchToolId,
                "Common",
                "Read-only: Filter the complete compact RUNTIME_CONTEXT.capabilities catalog by id or metadata. Results identify tools and skills but load neither; use the exact id with common.capabilities_read.",
                SearchSchema(),
                name: "capabilities_search",
                scope: "session",
                independentLocalRead: true);
            yield return ControllerToolDefinition.Create(
                ReadToolId,
                "Common",
                "Read-only: Read one exact capability id from RUNTIME_CONTEXT.capabilities or capabilities_search. A tool result loads its exact callable schema; a skill result loads its complete Markdown body. Never invent or derive an id.",
                ReadSchema(null, null),
                name: "capabilities_read",
                scope: "session",
                independentLocalRead: true);
        }

        public ToolResult ExecuteControllerTool(
            ToolCommand command,
            IReadOnlyList<ToolDefinition> runnableCatalog,
            IReadOnlyList<SkillDefinition> skills,
            bool manualRun)
        {
            if (command == null) return ToolResult.Fail("Capability command is empty.");
            var tools = NormalizeTools(runnableCatalog);
            var enabledSkills = NormalizeSkills(_skillExecutor == null
                ? skills
                : _skillExecutor.CapabilityCatalog(manualRun, skills));
            var collision = FindCollision(tools, enabledSkills);
            if (!string.IsNullOrWhiteSpace(collision)) return CollisionFailure(collision);

            if (string.Equals(command.ToolId, SearchToolId, StringComparison.OrdinalIgnoreCase))
            {
                return Search(command, tools, enabledSkills);
            }
            if (string.Equals(command.ToolId, ReadToolId, StringComparison.OrdinalIgnoreCase))
            {
                return Read(command, tools, enabledSkills, manualRun);
            }
            return ToolResult.Fail("Unknown capability discovery command: " + command.ToolId);
        }

        internal static JObject Descriptor(ToolDefinition tool)
        {
            return ConversationPromptComposer.BuildTool(tool);
        }

        internal static string Revision(ToolDefinition tool)
        {
            var descriptor = Descriptor(tool);
            return descriptor == null ? string.Empty : Sha256(descriptor.ToString(Formatting.None));
        }

        internal static string ToolCatalogRevision(IEnumerable<ToolDefinition> tools)
        {
            return Sha256(string.Join("\n", NormalizeTools(tools)
                .Select(tool => tool.Id + ":" + Revision(tool))
                .ToArray()));
        }

        internal static string CatalogRevision(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var entries = Records(NormalizeTools(tools), NormalizeSkills(skills))
                .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Kind, StringComparer.Ordinal)
                .Select(record => CatalogRevisionEntry(record).ToString(Formatting.None))
                .ToArray();
            return Sha256(string.Join("\n", entries));
        }

        internal static JObject BuildPromptCatalog(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills,
            IEnumerable<ToolDefinition> activeTools)
        {
            var toolCatalog = NormalizeTools(tools);
            var skillCatalog = NormalizeSkills(skills);
            ThrowOnCollision(toolCatalog, skillCatalog);
            var activeIds = new HashSet<string>((activeTools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
            var entries = Records(toolCatalog, skillCatalog)
                .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Kind, StringComparer.Ordinal)
                .ToList();
            var selected = entries
                .Select(record => Metadata(record, activeIds.Contains(record.Id)))
                .ToArray();
            return new JObject
            {
                ["revision"] = CatalogRevision(toolCatalog, skillCatalog),
                ["items"] = new JArray(selected),
                ["shown"] = selected.Length,
                ["total"] = entries.Count,
                ["complete"] = true,
                ["truncated"] = false,
                ["idEnumEnforced"] = HasBoundIdEnum(toolCatalog, entries.Count),
                ["instruction"] =
                    "This is the complete runnable tool and enabled skill index for this run. Use only an exact id shown here; common.capabilities_search is an optional filter. Load the selected tool schema or skill body with common.capabilities_read. Never synthesize an id."
            };
        }

        internal static void BindReadSchema(
            IList<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var toolCatalog = NormalizeTools(tools);
            var skillCatalog = NormalizeSkills(skills);
            ThrowOnCollision(toolCatalog, skillCatalog);
            var reader = (tools ?? new List<ToolDefinition>()).FirstOrDefault(tool => tool != null &&
                string.Equals(tool.Id, ReadToolId, StringComparison.OrdinalIgnoreCase));
            if (reader == null) return;
            var allIds = toolCatalog.Select(tool => tool.Id)
                .Concat(skillCatalog.Select(skill => skill.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var skillIds = skillCatalog.Select(skill => skill.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var genericSchema = ReadSchema(null, null);
            reader.ArgumentSchemaJson = ReadSchema(allIds, skillIds);
            var descriptor = Descriptor(reader);
            if (descriptor == null || descriptor.ToString(Formatting.None).Length > MaximumDescriptorCharacters)
            {
                reader.ArgumentSchemaJson = genericSchema;
            }
        }

        private static bool HasBoundIdEnum(
            IEnumerable<ToolDefinition> tools,
            int expectedIds)
        {
            var reader = (tools ?? new ToolDefinition[0]).FirstOrDefault(tool => tool != null &&
                string.Equals(tool.Id, ReadToolId, StringComparison.OrdinalIgnoreCase));
            if (reader == null || string.IsNullOrWhiteSpace(reader.ArgumentSchemaJson)) return false;
            try
            {
                var values = JObject.Parse(reader.ArgumentSchemaJson).SelectToken("properties.id.enum") as JArray;
                return values != null && values.Count == expectedIds;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static void ThrowOnCollision(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var collision = FindCollision(NormalizeTools(tools), NormalizeSkills(skills));
            if (!string.IsNullOrWhiteSpace(collision))
            {
                throw new InvalidOperationException(
                    "Capability id is used by both a tool and a skill: " + collision + ". Rename one definition.");
            }
        }

        private ToolResult Read(
            ToolCommand command,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            bool manualRun)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty).Trim();
            var tool = tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            var skill = skills.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tool == null && skill == null)
            {
                var records = Records(tools, skills).ToList();
                var suggestions = Suggest(id, records, 5);
                return ToolResult.Fail(
                    "Capability not found: " + id + ". Use an exact id from RUNTIME_CONTEXT.capabilities or common.capabilities_search; never invent an id.",
                    new JObject
                    {
                        ["id"] = id,
                        ["suggestions"] = new JArray(suggestions),
                        ["catalogRevision"] = CatalogRevision(tools, skills)
                    }.ToString(Formatting.None),
                    "capability_not_found",
                    true);
            }
            if (tool != null)
            {
                if (HasArgument(command, "referencePath") || HasArgument(command, "offset") || HasArgument(command, "maxChars"))
                {
                    return ToolResult.Fail(
                        "Skill reference arguments cannot be used with tool capability " + id + ".",
                        null,
                        "capability_reference_not_supported",
                        false);
                }
                return ReadTool(tool);
            }
            return _skillExecutor == null
                ? ToolResult.Fail("Skill reader is unavailable.", null, "capability_reader_unavailable", false)
                : _skillExecutor.ReadCapability(command, manualRun, skills);
        }

        private static ToolResult ReadTool(ToolDefinition tool)
        {
            var descriptor = Descriptor(tool);
            if (descriptor == null)
            {
                return ToolResult.Fail(
                    "Tool has no valid callable schema: " + tool.Id,
                    null,
                    "invalid_tool_schema",
                    false);
            }
            var compact = descriptor.ToString(Formatting.None);
            if (compact.Length > MaximumDescriptorCharacters)
            {
                return ToolResult.Fail(
                    "Tool descriptor exceeds the callable-pack descriptor limit: " + tool.Id,
                    JsonConvert.SerializeObject(new
                    {
                        id = tool.Id,
                        descriptorChars = compact.Length,
                        maxDescriptorChars = MaximumDescriptorCharacters
                    }),
                    "tool_schema_too_large",
                    false);
            }
            return ToolResult.Ok("Tool schema returned for callable-state evaluation: " + tool.Id, new JObject
            {
                ["kind"] = "tool-schema",
                ["id"] = tool.Id,
                ["revision"] = Sha256(compact),
                ["loaded"] = true,
                ["complete"] = true,
                ["truncated"] = false,
                ["admission"] = "already_callable_or_next_model_step",
                ["descriptor"] = descriptor
            }.ToString(Formatting.None));
        }

        private static ToolResult Search(
            ToolCommand command,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty).Trim();
            if (query.Length == 0)
            {
                return ToolResult.Fail("Capability search query is required.", null, "capability_query_required", true);
            }
            if (query.Length > MaximumQueryCharacters)
            {
                return ToolResult.Fail(
                    "Capability search query exceeds " + MaximumQueryCharacters + " characters.",
                    null,
                    "capability_query_too_large",
                    true);
            }
            var kind = ToolArgumentReader.String(command.Arguments, "kind", string.Empty).Trim().ToLowerInvariant();
            var cursor = Math.Max(0, ToolArgumentReader.Int32(command.Arguments, "cursor", 0));
            var limit = Math.Max(1, Math.Min(MaximumSearchPageSize,
                ToolArgumentReader.Int32(command.Arguments, "limit", 10)));
            var matches = Records(tools, skills)
                .Where(record => string.IsNullOrWhiteSpace(kind) || string.Equals(record.Kind, kind, StringComparison.Ordinal))
                .Where(record => record.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(record => SearchRank(record, query))
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cursor = Math.Min(cursor, matches.Count);
            var page = matches.Skip(cursor).Take(limit).Select(record => Metadata(record, null)).ToArray();
            var next = cursor + page.Length;
            return ToolResult.Ok("Capability metadata search completed.", new JObject
            {
                ["kind"] = "capability-search-page",
                ["catalogRevision"] = CatalogRevision(tools, skills),
                ["query"] = query,
                ["capabilityKind"] = string.IsNullOrWhiteSpace(kind) ? JValue.CreateNull() : (JToken)new JValue(kind),
                ["cursor"] = cursor,
                ["items"] = new JArray(page),
                ["nextCursor"] = next < matches.Count ? (JToken)new JValue(next) : JValue.CreateNull(),
                ["total"] = matches.Count,
                ["loaded"] = false
            }.ToString(Formatting.None));
        }

        private static IEnumerable<CapabilityRecord> Records(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                yield return new CapabilityRecord
                {
                    Id = tool.Id,
                    Kind = "tool",
                    Name = tool.Name,
                    Summary = FirstText(tool.Description, tool.UseWhen, tool.Limitations),
                    Revision = Revision(tool),
                    SearchText = string.Join("\n", new[]
                    {
                        tool.Id,
                        tool.Name,
                        tool.Description,
                        tool.UseWhen,
                        tool.DoNotUseWhen,
                        tool.Limitations,
                        tool.Host
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                    Tool = tool
                };
            }
            foreach (var skill in skills ?? new SkillDefinition[0])
            {
                yield return new CapabilityRecord
                {
                    Id = skill.Id,
                    Kind = "skill",
                    Name = skill.Name,
                    Summary = skill.Description,
                    Revision = SkillRevision.Compute(skill),
                    SearchText = string.Join("\n", new[]
                    {
                        skill.Id,
                        skill.Name,
                        skill.Description,
                        skill.Host
                    }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()),
                    Skill = skill
                };
            }
        }

        private static JObject Metadata(CapabilityRecord record, bool? schemaLoaded)
        {
            var result = new JObject
            {
                ["id"] = record.Id,
                ["kind"] = record.Kind,
                ["name"] = Bound(record.Name, MaximumNameCharacters),
                ["summary"] = Bound(record.Summary, MaximumSummaryCharacters),
                ["revision"] = record.Revision
            };
            if (record.Tool != null)
            {
                if (schemaLoaded.HasValue) result["schemaLoaded"] = schemaLoaded.Value;
                result["mutatesDocument"] = record.Tool.MutatesDocument;
                result["mutatesLocalState"] = record.Tool.MutatesLocalState;
                result["requiresConfirmation"] = record.Tool.RequiresConfirmation;
            }
            if (record.Skill != null)
            {
                result["bodyChars"] = (record.Skill.BodyMarkdown ?? string.Empty).Length;
                result["referenceCount"] = (record.Skill.References ?? new List<SkillReferenceMetadata>()).Count;
            }
            return result;
        }

        private static JObject CatalogRevisionEntry(CapabilityRecord record)
        {
            var result = new JObject
            {
                ["id"] = record.Id,
                ["kind"] = record.Kind,
                ["name"] = record.Name ?? string.Empty,
                ["summary"] = record.Summary ?? string.Empty,
                ["searchText"] = record.SearchText ?? string.Empty,
                ["revision"] = record.Revision
            };
            if (record.Tool != null)
            {
                result["mutatesDocument"] = record.Tool.MutatesDocument;
                result["mutatesLocalState"] = record.Tool.MutatesLocalState;
                result["requiresConfirmation"] = record.Tool.RequiresConfirmation;
            }
            if (record.Skill != null)
            {
                result["bodyChars"] = (record.Skill.BodyMarkdown ?? string.Empty).Length;
                result["referenceCount"] = (record.Skill.References ?? new List<SkillReferenceMetadata>()).Count;
            }
            return result;
        }

        private static int SearchRank(CapabilityRecord record, string query)
        {
            if (string.Equals(record.Id, query, StringComparison.OrdinalIgnoreCase)) return 0;
            if ((record.Id ?? string.Empty).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
            if ((record.Id ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if ((record.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            return 4;
        }

        private static IReadOnlyList<string> Suggest(string requestedId, IEnumerable<CapabilityRecord> records, int limit)
        {
            var requestedTokens = Tokens(requestedId);
            return (records ?? new CapabilityRecord[0])
                .Select(record => new
                {
                    record.Id,
                    Score = Tokens(record.SearchText).Count(token => requestedTokens.Contains(token))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Id.Length)
                .Take(Math.Max(1, limit))
                .Select(item => item.Id)
                .ToArray();
        }

        private static ISet<string> Tokens(string value)
        {
            return new HashSet<string>((value ?? string.Empty)
                .Split(new[] { '.', '_', '-', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim().ToLowerInvariant())
                .Where(token => token.Length > 1), StringComparer.OrdinalIgnoreCase);
        }

        private static List<ToolDefinition> NormalizeTools(IEnumerable<ToolDefinition> tools)
        {
            return (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && tool.AgentCanRun &&
                    !string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(tool.Id) && Descriptor(tool) != null)
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<SkillDefinition> NormalizeSkills(IEnumerable<SkillDefinition> skills)
        {
            return (skills ?? new SkillDefinition[0])
                .Where(skill => skill != null && skill.Enabled && !string.IsNullOrWhiteSpace(skill.Id))
                .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FindCollision(
            IEnumerable<ToolDefinition> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var toolIds = new HashSet<string>((tools ?? new ToolDefinition[0]).Select(tool => tool.Id),
                StringComparer.OrdinalIgnoreCase);
            return (skills ?? new SkillDefinition[0])
                .Select(skill => skill.Id)
                .FirstOrDefault(id => toolIds.Contains(id));
        }

        private static ToolResult CollisionFailure(string id)
        {
            return ToolResult.Fail(
                "Capability id is used by both a tool and a skill: " + id + ". Rename one definition.",
                JsonConvert.SerializeObject(new { id }),
                "capability_id_collision",
                false);
        }

        private static string Bound(string value, int maxCharacters)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length <= maxCharacters ? text : text.Substring(0, maxCharacters) + "...[truncated]";
        }

        private static string FirstText(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Literal query over capability ids and compact metadata.\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"tool\",\"skill\"],\"description\":\"Optional exact capability kind.\"}," +
                "\"cursor\":{\"type\":\"integer\",\"minimum\":0,\"default\":0,\"description\":\"Zero-based result offset.\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20,\"default\":10,\"description\":\"Maximum matches.\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        private static string ReadSchema(IEnumerable<string> allowedIds, IEnumerable<string> skillIds)
        {
            var id = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact capability id from RUNTIME_CONTEXT.capabilities or common.capabilities_search. Never invent an id.",
                ["minLength"] = 1,
                ["maxLength"] = 200
            };
            var all = (allowedIds ?? new string[0]).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (all.Length > 0) id["enum"] = new JArray(all);
            var referenceId = (JObject)id.DeepClone();
            var skillOnly = (skillIds ?? new string[0]).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (skillOnly.Length > 0) referenceId["enum"] = new JArray(skillOnly);

            var coreProperties = new JObject { ["id"] = id };
            var referenceProperties = new JObject
            {
                ["id"] = referenceId,
                ["referencePath"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Exact references/*.md path listed by a loaded skill.",
                    ["maxLength"] = 260
                },
                ["offset"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Zero-based character offset for a skill reference chunk.",
                    ["minimum"] = 0
                },
                ["maxChars"] = new JObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum skill reference characters returned.",
                    ["minimum"] = 1,
                    ["maximum"] = 50000
                }
            };
            var properties = (JObject)referenceProperties.DeepClone();
            properties["id"] = id.DeepClone();
            var variants = new JArray
            {
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = coreProperties,
                    ["required"] = new JArray("id"),
                    ["additionalProperties"] = false
                }
            };
            if (skillOnly.Length > 0 || all.Length == 0)
            {
                variants.Add(new JObject
                {
                    ["type"] = "object",
                    ["properties"] = referenceProperties,
                    ["required"] = new JArray("id", "referencePath"),
                    ["additionalProperties"] = false
                });
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("id"),
                ["additionalProperties"] = false,
                ["anyOf"] = variants
            }.ToString(Formatting.None);
        }

        private static bool HasArgument(ToolCommand command, string name)
        {
            return command != null && command.Arguments != null && command.Arguments.ContainsKey(name);
        }

        private sealed class CapabilityRecord
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string Summary { get; set; }
            public string Revision { get; set; }
            public string SearchText { get; set; }
            public ToolDefinition Tool { get; set; }
            public SkillDefinition Skill { get; set; }
        }
    }
}
