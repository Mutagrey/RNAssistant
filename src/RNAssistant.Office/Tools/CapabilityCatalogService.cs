using RNAssistant.Core.Tools;
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
    internal sealed partial class CapabilityCatalogService
    {
        internal const int MaximumDescriptorCharacters = 24000;

        private const int MaximumSearchPageSize = 20;
        private const int MaximumQueryCharacters = 200;
        private const int MaximumNameCharacters = 100;
        private const int MaximumPromptSummaryCharacters = 96;
        private const int MaximumSummaryCharacters = 160;

        internal CapabilityToolOutcome Execute(
            string toolId,
            IDictionary<string, object> arguments,
            IReadOnlyList<ToolCatalogEntry> runnableCatalog,
            IReadOnlyList<SkillDefinition> skills,
            ChatSession session,
            bool manualRun)
        {
            arguments = arguments ??
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var tools = NormalizeTools(runnableCatalog);
            var enabledSkills = NormalizeSkills(
                CapabilitySkills(manualRun, skills));
            var collision = FindCollision(tools, enabledSkills);
            if (!string.IsNullOrWhiteSpace(collision)) return CollisionFailure(collision);

            if (string.Equals(toolId, CapabilityToolCatalog.SearchToolId,
                StringComparison.Ordinal))
            {
                return Search(arguments, tools, enabledSkills);
            }
            if (string.Equals(toolId, CapabilityToolCatalog.ReadToolId,
                StringComparison.Ordinal))
            {
                return Read(arguments, tools, enabledSkills, session);
            }
            return CapabilityToolOutcome.Error(
                "Unknown capability tool: " + toolId, null,
                "unknown_tool", false);
        }

        internal static JObject Descriptor(ToolCatalogEntry tool)
        {
            return ConversationPromptComposer.BuildTool(tool);
        }

        internal static string Revision(ToolCatalogEntry tool)
        {
            var descriptor = Descriptor(tool);
            return descriptor == null ? string.Empty : Sha256(descriptor.ToString(Formatting.None));
        }

        internal static string ToolCatalogRevision(IEnumerable<ToolCatalogEntry> tools)
        {
            return Sha256(string.Join("\n", NormalizeTools(tools)
                .Select(tool => tool.Id + ":" + Revision(tool))
                .ToArray()));
        }

        internal static string CatalogRevision(
            IEnumerable<ToolCatalogEntry> tools,
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
            IEnumerable<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills,
            IEnumerable<ToolCatalogEntry> activeTools)
        {
            var toolCatalog = NormalizeTools(tools);
            var skillCatalog = NormalizeSkills(skills);
            ThrowOnCollision(toolCatalog, skillCatalog);
            var activeIds = new HashSet<string>((activeTools ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
            var entries = Records(toolCatalog, skillCatalog)
                .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Kind, StringComparer.Ordinal)
                .ToList();
            var selected = entries
                .Select(record => Metadata(record, activeIds.Contains(record.Id), MaximumPromptSummaryCharacters))
                .ToArray();
            return new JObject
            {
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
            IList<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var toolCatalog = NormalizeTools(tools);
            var skillCatalog = NormalizeSkills(skills);
            ThrowOnCollision(toolCatalog, skillCatalog);
            var reader = (tools ?? new List<ToolCatalogEntry>()).FirstOrDefault(tool => tool != null &&
                string.Equals(tool.Id, CapabilityToolCatalog.ReadToolId,
                    StringComparison.Ordinal));
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
            IEnumerable<ToolCatalogEntry> tools,
            int expectedIds)
        {
            var reader = (tools ?? new ToolCatalogEntry[0]).FirstOrDefault(tool => tool != null &&
                string.Equals(tool.Id, CapabilityToolCatalog.ReadToolId,
                    StringComparison.Ordinal));
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
            IEnumerable<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var collision = FindCollision(NormalizeTools(tools), NormalizeSkills(skills));
            if (!string.IsNullOrWhiteSpace(collision))
            {
                throw new InvalidOperationException(
                    "Capability id is used by both a tool and a skill: " + collision + ". Rename one definition.");
            }
        }

        private CapabilityToolOutcome Read(
            IDictionary<string, object> arguments,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills,
            ChatSession session)
        {
            var id = ToolArgumentReader.String(
                arguments, "id", string.Empty).Trim();
            var tool = tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            var skill = skills.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (tool == null && skill == null)
            {
                var records = Records(tools, skills).ToList();
                var suggestions = Suggest(id, records, 5);
                return CapabilityToolOutcome.Error(
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
                if (HasArgument(arguments, "referencePath") ||
                    HasArgument(arguments, "action"))
                {
                    return CapabilityToolOutcome.Error(
                        "Skill reference arguments cannot be used with tool capability " + id + ".",
                        null,
                        "capability_reference_not_supported",
                        false);
                }
                return ReadTool(tool);
            }
            return ReadSkill(arguments, skill, session);
        }

        private static CapabilityToolOutcome ReadTool(ToolCatalogEntry tool)
        {
            var descriptor = Descriptor(tool);
            if (descriptor == null)
            {
                return CapabilityToolOutcome.Error(
                    "Tool has no valid callable schema: " + tool.Id,
                    null,
                    "invalid_tool_schema",
                    false);
            }
            var compact = descriptor.ToString(Formatting.None);
            if (compact.Length > MaximumDescriptorCharacters)
            {
                return CapabilityToolOutcome.Error(
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
            return CapabilityToolOutcome.Ok(
                "Tool schema returned for callable-state evaluation: " + tool.Id,
                new JObject
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

        private static CapabilityToolOutcome Search(
            IDictionary<string, object> arguments,
            IReadOnlyList<ToolCatalogEntry> tools,
            IReadOnlyList<SkillDefinition> skills)
        {
            var query = ToolArgumentReader.String(
                arguments, "query", string.Empty).Trim();
            if (query.Length == 0)
            {
                return CapabilityToolOutcome.Error(
                    "Capability search query is required.", null,
                    "capability_query_required", true);
            }
            if (query.Length > MaximumQueryCharacters)
            {
                return CapabilityToolOutcome.Error(
                    "Capability search query exceeds " + MaximumQueryCharacters + " characters.",
                    null,
                    "capability_query_too_large",
                    true);
            }
            var kind = ToolArgumentReader.String(
                arguments, "kind", string.Empty).Trim().ToLowerInvariant();
            var matches = Records(tools, skills)
                .Where(record => string.IsNullOrWhiteSpace(kind) || string.Equals(record.Kind, kind, StringComparison.Ordinal))
                .Where(record => record.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(record => SearchRank(record, query))
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var page = matches.Take(MaximumSearchPageSize)
                .Select(record => Metadata(record, null, MaximumSummaryCharacters)).ToArray();
            return CapabilityToolOutcome.Ok(
                matches.Count == 0
                    ? "No capabilities matched the query."
                    : "Capability metadata search completed.", new JObject
            {
                ["kind"] = "capability-search",
                ["catalogRevision"] = CatalogRevision(tools, skills),
                ["query"] = query,
                ["capabilityKind"] = string.IsNullOrWhiteSpace(kind) ? JValue.CreateNull() : (JToken)new JValue(kind),
                ["items"] = new JArray(page),
                ["shown"] = page.Length,
                ["total"] = matches.Count,
                ["complete"] = page.Length == matches.Count,
                ["refineQuery"] = page.Length < matches.Count,
                ["empty"] = matches.Count == 0,
                ["loaded"] = false
            }.ToString(Formatting.None));
        }

        private static IEnumerable<CapabilityRecord> Records(
            IEnumerable<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills)
        {
            foreach (var tool in tools ?? new ToolCatalogEntry[0])
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

        private static JObject Metadata(
            CapabilityRecord record,
            bool? schemaLoaded,
            int maximumSummaryCharacters)
        {
            var result = new JObject
            {
                ["id"] = record.Id,
                ["kind"] = record.Kind
            };
            if (record.Tool != null && schemaLoaded == true)
            {
                // The full active descriptor and safety policy are already present
                // in RUNTIME_CONTEXT.tools. Keep only exact membership here.
                result["schemaLoaded"] = true;
                return result;
            }
            result["name"] = Bound(record.Name, MaximumNameCharacters);
            result["summary"] = Bound(record.Summary, maximumSummaryCharacters);
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

        private static List<ToolCatalogEntry> NormalizeTools(IEnumerable<ToolCatalogEntry> tools)
        {
            return (tools ?? new ToolCatalogEntry[0])
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
            IEnumerable<ToolCatalogEntry> tools,
            IEnumerable<SkillDefinition> skills)
        {
            var toolIds = new HashSet<string>((tools ?? new ToolCatalogEntry[0]).Select(tool => tool.Id),
                StringComparer.OrdinalIgnoreCase);
            return (skills ?? new SkillDefinition[0])
                .Select(skill => skill.Id)
                .FirstOrDefault(id => toolIds.Contains(id));
        }

        private static CapabilityToolOutcome CollisionFailure(string id)
        {
            return CapabilityToolOutcome.Error(
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

        internal static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Literal query over capability ids and compact metadata.\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"tool\",\"skill\"],\"description\":\"Optional exact capability kind.\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        internal static string ReadSchema(
            IEnumerable<string> allowedIds,
            IEnumerable<string> skillIds)
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
                ["action"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "Use read to start or restart this reference; use next only after hasMore=true.",
                    ["enum"] = new JArray("read", "next"),
                    ["default"] = "read"
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

        private static bool HasArgument(
            IDictionary<string, object> arguments, string name)
        {
            return arguments != null && arguments.ContainsKey(name);
        }

        private sealed class CapabilityRecord
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public string Summary { get; set; }
            public string Revision { get; set; }
            public string SearchText { get; set; }
            public ToolCatalogEntry Tool { get; set; }
            public SkillDefinition Skill { get; set; }
        }
    }
}
