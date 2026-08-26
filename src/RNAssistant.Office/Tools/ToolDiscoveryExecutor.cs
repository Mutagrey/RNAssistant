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
    internal sealed class ToolDiscoveryExecutor
    {
        public const string ListToolId = "common.tools_list";
        public const string SearchToolId = "common.tools_search";
        public const string ReadToolId = "common.tools_read";
        public const int MaximumDescriptorCharacters = 24000;

        private const int MaximumPageSize = 50;
        private const int MaximumSearchPageSize = 20;
        private const int MaximumQueryCharacters = 200;
        private const int MaximumMetadataTextCharacters = 600;

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            yield return ControllerToolDefinition.Create(
                ListToolId,
                "Common",
                "Read-only: List compact tool namespaces, or page schema-free metadata inside one exact namespace. Use common.tools_read before calling a non-bootstrap tool.",
                ListSchema(),
                name: "tools_list",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                SearchToolId,
                "Common",
                "Read-only: Search runnable tool metadata by a bounded literal query. Results do not load schemas; call common.tools_read with one exact id before use.",
                SearchSchema(),
                name: "tools_search",
                scope: "session");
            yield return ControllerToolDefinition.Create(
                ReadToolId,
                "Common",
                "Read-only: Load the exact current schema and safety descriptor for one runnable tool. A complete revision-matched result makes that tool callable while it remains in the bounded active working set.",
                ReadSchema(),
                name: "tools_read",
                scope: "session");
        }

        public ToolResult ExecuteControllerTool(
            ToolCommand command,
            IReadOnlyList<ToolDefinition> runnableCatalog)
        {
            if (command == null) return ToolResult.Fail("Tool command is empty.");
            var catalog = NormalizeCatalog(runnableCatalog);
            if (string.Equals(command.ToolId, ListToolId, StringComparison.OrdinalIgnoreCase))
            {
                return List(command, catalog);
            }
            if (string.Equals(command.ToolId, SearchToolId, StringComparison.OrdinalIgnoreCase))
            {
                return Search(command, catalog);
            }
            if (string.Equals(command.ToolId, ReadToolId, StringComparison.OrdinalIgnoreCase))
            {
                return Read(command, catalog);
            }
            return ToolResult.Fail("Unknown tool discovery command: " + command.ToolId);
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

        internal static string CatalogRevision(IEnumerable<ToolDefinition> tools)
        {
            var entries = NormalizeCatalog(tools)
                .Select(tool => tool.Id + ":" + Revision(tool))
                .ToArray();
            return Sha256(string.Join("\n", entries));
        }

        internal static string NamespaceOf(string toolId)
        {
            var id = (toolId ?? string.Empty).Trim();
            var dot = id.IndexOf('.');
            if (dot <= 0) return id;
            var root = id.Substring(0, dot);
            if (!string.Equals(root, "common", StringComparison.OrdinalIgnoreCase)) return root;
            var suffix = id.Substring(dot + 1);
            var separator = suffix.IndexOf('_');
            return separator <= 0 ? id : root + "." + suffix.Substring(0, separator);
        }

        internal static JArray BuildNamespaces(IEnumerable<ToolDefinition> tools)
        {
            return new JArray(NormalizeCatalog(tools)
                .GroupBy(tool => NamespaceOf(tool.Id), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new JObject
                {
                    ["id"] = group.Key,
                    ["count"] = group.Count()
                }));
        }

        private static ToolResult List(ToolCommand command, IReadOnlyList<ToolDefinition> catalog)
        {
            var requestedNamespace = ToolArgumentReader.String(command.Arguments, "namespace", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedNamespace))
            {
                return ToolResult.Ok("Tool namespaces listed.", new JObject
                {
                    ["kind"] = "tool-namespaces",
                    ["catalogRevision"] = CatalogRevision(catalog),
                    ["namespaces"] = BuildNamespaces(catalog)
                }.ToString(Formatting.None));
            }

            var cursor = Math.Max(0, ToolArgumentReader.Int32(command.Arguments, "cursor", 0));
            var limit = Math.Max(1, Math.Min(MaximumPageSize,
                ToolArgumentReader.Int32(command.Arguments, "limit", 20)));
            var matches = catalog
                .Where(tool => string.Equals(NamespaceOf(tool.Id), requestedNamespace, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return ToolResult.Fail(
                    "Tool namespace not found: " + requestedNamespace,
                    new JObject { ["namespaces"] = BuildNamespaces(catalog) }.ToString(Formatting.None),
                    "tool_namespace_not_found",
                    true);
            }
            cursor = Math.Min(cursor, matches.Count);
            var page = matches.Skip(cursor).Take(limit).Select(Metadata).ToArray();
            var next = cursor + page.Length;
            return ToolResult.Ok("Tool metadata listed for namespace " + requestedNamespace + ".", new JObject
            {
                ["kind"] = "tool-metadata-page",
                ["catalogRevision"] = CatalogRevision(catalog),
                ["namespace"] = requestedNamespace,
                ["cursor"] = cursor,
                ["items"] = new JArray(page),
                ["nextCursor"] = next < matches.Count ? (JToken)new JValue(next) : JValue.CreateNull(),
                ["total"] = matches.Count,
                ["schemasLoaded"] = false
            }.ToString(Formatting.None));
        }

        private static ToolResult Search(ToolCommand command, IReadOnlyList<ToolDefinition> catalog)
        {
            var query = ToolArgumentReader.String(command.Arguments, "query", string.Empty).Trim();
            if (query.Length == 0)
            {
                return ToolResult.Fail("Tool search query is required.", null, "tool_query_required", true);
            }
            if (query.Length > MaximumQueryCharacters)
            {
                return ToolResult.Fail(
                    "Tool search query exceeds " + MaximumQueryCharacters + " characters.",
                    null,
                    "tool_query_too_large",
                    true);
            }
            var requestedNamespace = ToolArgumentReader.String(command.Arguments, "namespace", string.Empty).Trim();
            var cursor = Math.Max(0, ToolArgumentReader.Int32(command.Arguments, "cursor", 0));
            var limit = Math.Max(1, Math.Min(MaximumSearchPageSize,
                ToolArgumentReader.Int32(command.Arguments, "limit", 10)));
            var matches = catalog
                .Where(tool => string.IsNullOrWhiteSpace(requestedNamespace) ||
                    string.Equals(NamespaceOf(tool.Id), requestedNamespace, StringComparison.OrdinalIgnoreCase))
                .Where(tool => SearchText(tool).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(tool => SearchRank(tool, query))
                .ThenBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cursor = Math.Min(cursor, matches.Count);
            var page = matches.Skip(cursor).Take(limit).Select(Metadata).ToArray();
            var next = cursor + page.Length;
            return ToolResult.Ok("Tool metadata search completed.", new JObject
            {
                ["kind"] = "tool-search-page",
                ["catalogRevision"] = CatalogRevision(catalog),
                ["query"] = query,
                ["namespace"] = string.IsNullOrWhiteSpace(requestedNamespace)
                    ? JValue.CreateNull()
                    : (JToken)new JValue(requestedNamespace),
                ["cursor"] = cursor,
                ["items"] = new JArray(page),
                ["nextCursor"] = next < matches.Count ? (JToken)new JValue(next) : JValue.CreateNull(),
                ["total"] = matches.Count,
                ["schemasLoaded"] = false
            }.ToString(Formatting.None));
        }

        private static ToolResult Read(ToolCommand command, IReadOnlyList<ToolDefinition> catalog)
        {
            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty).Trim();
            var tool = catalog.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
            {
                var suggestions = ToolIdSuggester.Suggest(id, catalog, 5);
                return ToolResult.Fail(
                    "Runnable tool not found: " + id,
                    JsonConvert.SerializeObject(new { id, suggestions }),
                    "tool_not_found",
                    true);
            }
            var descriptor = Descriptor(tool);
            if (descriptor == null)
            {
                return ToolResult.Fail(
                    "Tool has no valid callable schema: " + id,
                    null,
                    "invalid_tool_schema",
                    false);
            }
            var compact = descriptor.ToString(Formatting.None);
            if (compact.Length > MaximumDescriptorCharacters)
            {
                return ToolResult.Fail(
                    "Tool descriptor exceeds the progressive working-set limit: " + id,
                    JsonConvert.SerializeObject(new
                    {
                        id,
                        descriptorChars = compact.Length,
                        maxDescriptorChars = MaximumDescriptorCharacters
                    }),
                    "tool_schema_too_large",
                    false);
            }
            return ToolResult.Ok("Tool schema loaded: " + tool.Id, new JObject
            {
                ["kind"] = "tool-schema",
                ["id"] = tool.Id,
                ["revision"] = Sha256(compact),
                ["loaded"] = true,
                ["complete"] = true,
                ["truncated"] = false,
                ["descriptor"] = descriptor
            }.ToString(Formatting.None));
        }

        private static JObject Metadata(ToolDefinition tool)
        {
            return new JObject
            {
                ["id"] = tool.Id,
                ["namespace"] = NamespaceOf(tool.Id),
                ["name"] = Bound(tool.Name, MaximumMetadataTextCharacters),
                ["description"] = Bound(tool.Description, MaximumMetadataTextCharacters),
                ["useWhen"] = Bound(tool.UseWhen, MaximumMetadataTextCharacters),
                ["revision"] = Revision(tool),
                ["mutatesDocument"] = tool.MutatesDocument,
                ["mutatesLocalState"] = tool.MutatesLocalState,
                ["requiresConfirmation"] = tool.RequiresConfirmation,
                ["riskLevel"] = tool.RiskLevel,
                ["schemaLoaded"] = false
            };
        }

        private static string SearchText(ToolDefinition tool)
        {
            return string.Join("\n", new[]
            {
                tool.Id,
                tool.Name,
                tool.Description,
                tool.UseWhen,
                tool.DoNotUseWhen,
                tool.Limitations,
                tool.Host
            }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        private static int SearchRank(ToolDefinition tool, string query)
        {
            if (string.Equals(tool.Id, query, StringComparison.OrdinalIgnoreCase)) return 0;
            if ((tool.Id ?? string.Empty).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
            if ((tool.Id ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if ((tool.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            return 4;
        }

        private static List<ToolDefinition> NormalizeCatalog(IEnumerable<ToolDefinition> tools)
        {
            return (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && tool.AgentCanRun &&
                    !string.IsNullOrWhiteSpace(tool.Id) && Descriptor(tool) != null)
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Bound(string value, int maxCharacters)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length <= maxCharacters ? text : text.Substring(0, maxCharacters) + "...[truncated]";
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

        private static string ListSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"namespace\":{\"type\":\"string\",\"description\":\"Exact namespace from RUNTIME_CONTEXT.tool_discovery.namespaces. Omit to list namespaces only.\"}," +
                "\"cursor\":{\"type\":\"integer\",\"minimum\":0,\"default\":0,\"description\":\"Zero-based metadata offset.\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"default\":20,\"description\":\"Maximum metadata items.\"}}," +
                "\"required\":[],\"additionalProperties\":false}";
        }

        private static string SearchSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200,\"description\":\"Literal query over tool ids and compact metadata.\"}," +
                "\"namespace\":{\"type\":\"string\",\"description\":\"Optional exact namespace filter.\"}," +
                "\"cursor\":{\"type\":\"integer\",\"minimum\":0,\"default\":0,\"description\":\"Zero-based result offset.\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20,\"default\":10,\"description\":\"Maximum matches.\"}}," +
                "\"required\":[\"query\"],\"additionalProperties\":false}";
        }

        private static string ReadSchema()
        {
            return "{\"type\":\"object\",\"properties\":{" +
                "\"id\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Exact runnable tool id returned by list/search or named by a loaded skill.\"}}," +
                "\"required\":[\"id\"],\"additionalProperties\":false}";
        }
    }
}
