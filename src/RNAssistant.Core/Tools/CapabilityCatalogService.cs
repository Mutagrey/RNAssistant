using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Tools
{
    public sealed class CapabilityCatalogService
    {
        public static JObject BuildTool(ToolCatalogEntry tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Id)) return null;
            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError)) return null;
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = tool.Id,
                    ["description"] = BuildDescription(tool),
                    ["parameters"] = ToolSchemaSupport.ForPrompt(schema)
                },
                ["safety"] = new JObject
                {
                    ["mutates_document"] = tool.MutatesDocument,
                    ["mutates_local_state"] = tool.MutatesLocalState,
                    ["requires_confirmation"] = tool.RequiresConfirmation,
                    ["risk_level"] = tool.RiskLevel
                }
            };
        }

        public static string BuildDescription(ToolCatalogEntry tool)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(tool.Description)) parts.Add(tool.Description.Trim());
            if (!string.IsNullOrWhiteSpace(tool.UseWhen)) parts.Add("Use when: " + tool.UseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.DoNotUseWhen)) parts.Add("Do not use when: " + tool.DoNotUseWhen.Trim());
            if (!string.IsNullOrWhiteSpace(tool.Limitations)) parts.Add("Limitations: " + tool.Limitations.Trim());
            return string.Join(" ", parts.ToArray());
        }

        public static bool CompactCallableSchema(JToken token, IEnumerable<ToolCatalogEntry> callableTools)
        {
            var data = token as JObject;
            if (data == null || (string)data["kind"] != "tool-schema" ||
                data["loaded"]?.Type != JTokenType.Boolean || !(bool)data["loaded"] ||
                data["complete"]?.Type != JTokenType.Boolean || !(bool)data["complete"] ||
                data["truncated"]?.Type != JTokenType.Boolean || (bool)data["truncated"])
                return false;
            var tool = callableTools?.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, (string)data["id"], StringComparison.Ordinal));
            if (tool == null || !string.Equals((string)data["revision"],
                    CapabilityCatalogService.Revision(tool), StringComparison.Ordinal) ||
                !JToken.DeepEquals(data["descriptor"], CapabilityCatalogService.Descriptor(tool)))
                return false;
            // Only the detached model view changes. Admission/replay still consume
            // the complete durable descriptor, and this request supplies its exact copy.
            data.Remove("descriptor");
            data["schemaInRequest"] = true;
            return true;
        }

        public static void RemoveRuntimeState(JToken token)
        {
            var root = token as JObject;
            if (root == null) return;
            foreach (var name in new[] { "catalogRevision", "revision", "skillRevision", "snapshotRevision",
                "previousSnapshotRevision", "admission", "progressCharacters", "loaded" }) root.Remove(name);
            var items = root["items"] as JArray;
            if (items != null)
            {
                foreach (var item in items.OfType<JObject>())
                    { item.Remove("revision"); item.Remove("catalogRevision"); }
            }
            var references = root["references"] as JArray;
            if (references != null)
            {
                foreach (var item in references.OfType<JObject>())
                    { item.Remove("revision"); item.Remove("skillRevision"); }
            }
        }

        public const int MaximumDescriptorCharacters = 24000;

        private const int MaximumSearchPageSize = 20;
        private const int MaximumQueryCharacters = 200;
        private const int MaximumNameCharacters = 100;
        private const int MaximumPromptSummaryCharacters = 96;
        private const int MaximumSummaryCharacters = 160;

        private readonly SkillCatalogService _skillCatalog;
        private readonly Func<IDictionary<string, object>, SkillDefinition, ChatSession, CapabilityToolOutcome> _readSkill;

        public CapabilityCatalogService(SkillCatalogService skillCatalog,
            Func<IDictionary<string, object>, SkillDefinition, ChatSession, CapabilityToolOutcome> readSkill)
        {
            _skillCatalog = skillCatalog ?? throw new ArgumentNullException(nameof(skillCatalog));
            _readSkill = readSkill ?? throw new ArgumentNullException(nameof(readSkill));
        }

        public SkillCatalogSnapshot CaptureSkills() { return _skillCatalog.Capture(); }
        public SkillCatalogSnapshot SelectPublishedSkills(SkillCatalogSnapshot published)
        { return _skillCatalog.SelectPublished(published); }

        private IReadOnlyList<SkillDefinition> CapabilitySkills(bool manualRun, IReadOnlyList<SkillDefinition> runtimeSkills)
        {
            if (runtimeSkills != null) return runtimeSkills.Where(item => item != null && item.Enabled).ToList();
            return manualRun ? (IReadOnlyList<SkillDefinition>)_skillCatalog.GetVisibleSkills() : new SkillDefinition[0];
        }

        public CapabilityToolOutcome Execute(
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
                "unknown_tool", false, DefectRecovery());
        }

        public static JObject Descriptor(ToolCatalogEntry tool)
        {
            return BuildTool(tool);
        }

        public static string Revision(ToolCatalogEntry tool)
        {
            var descriptor = Descriptor(tool);
            return descriptor == null ? string.Empty : Sha256(descriptor.ToString(Formatting.None));
        }

        public static string ToolCatalogRevision(IEnumerable<ToolCatalogEntry> tools)
        {
            return Sha256(string.Join("\n", NormalizeTools(tools)
                .Select(tool => tool.Id + ":" + Revision(tool))
                .ToArray()));
        }

        public static string CatalogRevision(
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

        public static JObject BuildPromptCatalog(
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
                .Select(record => {
                    var metadata = Metadata(record, activeIds.Contains(record.Id), MaximumPromptSummaryCharacters);
                    if (record.Tool != null && record.Tool.Exposure == ToolExposure.Skill && !activeIds.Contains(record.Id))
                        metadata["loadSkill"] = new JArray(skillCatalog.Where(skill => skill.Package != null &&
                            (skill.Package.RequiredTools ?? new List<SkillToolDependency>()).Concat(skill.Package.OptionalTools ?? new List<SkillToolDependency>())
                                .Any(dependency => dependency != null && dependency.Id == record.Id))
                            .Select(skill => skill.Id).OrderBy(id => id, StringComparer.Ordinal));
                    return metadata;
                })
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
                    "This is the complete runnable tool and enabled skill index for this run. Use only an exact id shown here; core.tools.search is an optional filter. For a tool with loadSkill, select one listed skill through core.skills.load before calling the tool; an empty list means unavailable. core.tools.read inspects sources or admits direct tool schemas. Never synthesize an id."
            };
        }

        public static void BindReadSchema(
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

        public static void ThrowOnCollision(
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
            var id = ArgumentString(
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
                    "Capability not found: " + id + ". Use an exact id from RUNTIME_CONTEXT.capabilities or core.tools.search; never invent an id.",
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
                return ReadTool(tool, skills);
            }
            return _readSkill(arguments, skill, session);
        }

        private static CapabilityToolOutcome ReadTool(ToolCatalogEntry tool, IReadOnlyList<SkillDefinition> skills)
        {
            if (tool.Exposure == ToolExposure.Skill)
            {
                // Discovery succeeds without granting access or caching a refusal
                // that survives selection. The selected request owns the schema;
                // duplicating it in history wastes the budget needed for the skill.
                return CapabilityToolOutcome.Ok("Tool schema is supplied by its selected skill: " + tool.Id +
                    ". If already present in RUNTIME_CONTEXT.tools, call it directly. Otherwise select one listed loadSkill through core.skills.load. This read grants no tool access.",
                    new JObject
                    {
                        ["kind"] = "skill-tool",
                        ["id"] = tool.Id,
                        ["revision"] = Revision(tool),
                        ["loadSkill"] = new JArray(skills.Where(skill =>
                            (skill.Package?.RequiredTools ?? new List<SkillToolDependency>()).Concat(
                                skill.Package?.OptionalTools ?? new List<SkillToolDependency>())
                                .Any(dependency => dependency != null && dependency.Id == tool.Id))
                            .Select(skill => skill.Id).OrderBy(id => id, StringComparer.Ordinal))
                    }.ToString(Formatting.None));
            }
            var descriptor = Descriptor(tool);
            if (descriptor == null)
            {
                return CapabilityToolOutcome.Error(
                    "Tool has no valid callable schema: " + tool.Id,
                    null,
                    "invalid_tool_schema",
                    false,
                    DefectRecovery());
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
                    false,
                    DefectRecovery());
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
            var query = ArgumentString(
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
            var kind = ArgumentString(
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

        public static ToolResultSummary SummarizeSearch(JObject data)
        {
            var items = data?["items"] as JArray;
            if (items == null || data["kind"]?.Type != JTokenType.String ||
                (string)data["kind"] != "capability-search" ||
                data["complete"]?.Type != JTokenType.Boolean) return null;
            return new ToolResultSummary {
                Kind = "capability-search",
                ReturnedItems = items.Count,
                TotalItems = data["total"]?.Type == JTokenType.Integer ? data.Value<int?>("total") : null,
                Complete = data.Value<bool>("complete")
            };
        }

        public static ToolResultSummary SummarizeRead(JObject data)
        {
            if (data?["kind"]?.Type != JTokenType.String ||
                data["complete"]?.Type != JTokenType.Boolean) return null;
            var kind = data.Value<string>("kind");
            if (kind != "tool-schema" && kind != "skill" && kind != "reference") return null;
            var characters = data["returnedChars"]?.Type == JTokenType.Integer
                ? data.Value<int?>("returnedChars") : null;
            return new ToolResultSummary {
                Kind = "capability-read",
                Representation = kind,
                ReturnedCharacters = characters,
                Complete = data.Value<bool>("complete")
            };
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
            // The prompt index only chooses an exact capability. Policy belongs to
            // the admitted descriptor/runtime; search retains inspection metadata.
            var promptIndex = schemaLoaded.HasValue;
            if (record.Skill != null || !promptIndex)
                result["name"] = Bound(record.Name, MaximumNameCharacters);
            result["summary"] = Bound(record.Summary, maximumSummaryCharacters);
            if (record.Tool != null)
            {
                if (schemaLoaded.HasValue) result["schemaLoaded"] = schemaLoaded.Value;
                if (!promptIndex)
                {
                    result["mutatesDocument"] = record.Tool.MutatesDocument;
                    result["mutatesLocalState"] = record.Tool.MutatesLocalState;
                    result["requiresConfirmation"] = record.Tool.RequiresConfirmation;
                }
            }
            if (record.Skill != null && !promptIndex)
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
                false,
                DefectRecovery());
        }

        private static ToolRecoveryContract DefectRecovery()
        {
            return new ToolRecoveryContract(
                ToolFailureKind.ToolDefect,
                ToolRetryPolicy.None);
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

        public static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Literal query over capability ids and compact metadata.\"}," +
                "\"kind\":{\"type\":\"string\",\"enum\":[\"tool\",\"skill\"],\"description\":\"Optional exact capability kind.\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        public static string ReadSchema(
            IEnumerable<string> allowedIds,
            IEnumerable<string> skillIds)
        {
            var id = new JObject
            {
                ["type"] = "string",
                ["description"] = "Exact capability id from RUNTIME_CONTEXT.capabilities or core.tools.search. Never invent an id.",
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

        private static string ArgumentString(IDictionary<string, object> args, string name, string fallback = "")
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null)
            {
                return fallback;
            }

            var token = value as JToken;
            if (token == null)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return fallback;
            }
            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return token.ToString(Formatting.None);
            }

            var scalar = token as JValue;
            return Convert.ToString(scalar == null ? token.ToString(Formatting.None) : scalar.Value, System.Globalization.CultureInfo.InvariantCulture);
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
