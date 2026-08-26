using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    internal sealed class ProgressiveToolWorkingSet
    {
        public const int MaximumDynamicSchemas = 8;
        private const int MinimumDynamicSchemaBudgetTokens = 8192;
        private const int MaximumDynamicSchemaBudgetTokens = 20000;

        private static readonly HashSet<string> AgentBootstrapToolIds = new HashSet<string>(
            new[]
            {
                ResourceToolExecutor.ListToolId,
                ResourceToolExecutor.ResolveToolId,
                ResourceToolExecutor.SearchToolId,
                ResourceToolExecutor.ReadToolId,
                CapabilityDiscoveryExecutor.SearchToolId,
                CapabilityDiscoveryExecutor.ReadToolId
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly string _mode;
        private readonly IReadOnlyList<ToolDefinition> _catalog;
        private readonly IDictionary<string, ToolDefinition> _catalogById;
        private readonly IDictionary<string, string> _toolIdByApiName;
        private readonly HashSet<string> _bootstrapIds;
        private readonly LinkedList<string> _dynamicLru = new LinkedList<string>();
        private readonly IDictionary<string, LinkedListNode<string>> _dynamicNodes =
            new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly AppSettings _settings;
        private readonly int _dynamicSchemaBudgetTokens;

        private ProgressiveToolWorkingSet(
            string mode,
            IReadOnlyList<ToolDefinition> catalog,
            AppSettings settings)
        {
            _mode = ChatModes.Normalize(mode);
            _catalog = (catalog ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _catalogById = _catalog.ToDictionary(tool => tool.Id, StringComparer.OrdinalIgnoreCase);
            _toolIdByApiName = _catalog
                .GroupBy(tool => AgentJsonProtocol.ApiToolName(tool.Id), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Id,
                    StringComparer.OrdinalIgnoreCase);
            _settings = settings ?? new AppSettings();
            _dynamicSchemaBudgetTokens = Math.Max(
                MinimumDynamicSchemaBudgetTokens,
                Math.Min(
                    MaximumDynamicSchemaBudgetTokens,
                    ModelContextBudget.InputBudgetTokens(_settings) / 4));
            _bootstrapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal))
            {
                foreach (var tool in _catalog) _bootstrapIds.Add(tool.Id);
            }
            else
            {
                foreach (var id in AgentBootstrapToolIds)
                {
                    if (_catalogById.ContainsKey(id)) _bootstrapIds.Add(id);
                }
            }
        }

        public static ProgressiveToolWorkingSet Create(
            string mode,
            IReadOnlyList<ToolDefinition> catalog,
            AppSettings settings,
            IEnumerable<ChatMessage> activeMessages = null)
        {
            var result = new ProgressiveToolWorkingSet(mode, catalog, settings);
            result.Restore(activeMessages);
            return result;
        }

        public IReadOnlyList<ToolDefinition> Tools
        {
            get
            {
                if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return _catalog;
                var active = new HashSet<string>(_bootstrapIds, StringComparer.OrdinalIgnoreCase);
                foreach (var id in _dynamicLru) active.Add(id);
                return _catalog.Where(tool => active.Contains(tool.Id)).ToList();
            }
        }

        public IReadOnlyList<ToolDefinition> Catalog
        {
            get { return _catalog; }
        }

        public JObject CapabilityContext(IEnumerable<SkillDefinition> skills)
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return null;
            var result = CapabilityDiscoveryExecutor.BuildPromptCatalog(_catalog, skills, Tools);
            result["policy"] = "progressive";
            result["maxDynamicSchemas"] = MaximumDynamicSchemas;
            result["dynamicSchemaBudgetTokens"] = _dynamicSchemaBudgetTokens;
            return result;
        }

        public bool ObserveReadResult(ChatMessage message, out IReadOnlyList<string> evicted)
        {
            evicted = new string[0];
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return false;
            string id;
            if (!TryReadEvidence(message, out id)) return false;
            evicted = Load(id);
            return true;
        }

        public void Touch(string toolId)
        {
            LinkedListNode<string> node;
            if (string.IsNullOrWhiteSpace(toolId) || !_dynamicNodes.TryGetValue(toolId, out node)) return;
            _dynamicLru.Remove(node);
            _dynamicLru.AddLast(node);
        }

        public ChatMessage BuildStateMessage(IEnumerable<string> evicted)
        {
            var removed = (evicted ?? new string[0])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ChatMessage
            {
                Role = "user",
                ProtocolMessage = true,
                Content = "TOOL_WORKING_SET:\n" + new JObject
                {
                    ["catalogRevision"] = CapabilityDiscoveryExecutor.ToolCatalogRevision(_catalog),
                    ["activeDynamicSchemas"] = new JArray(_dynamicLru.Select(id => new JObject
                    {
                        ["id"] = id,
                        ["revision"] = CapabilityDiscoveryExecutor.Revision(_catalogById[id])
                    })),
                    ["evicted"] = new JArray(removed),
                    ["maxDynamicSchemas"] = MaximumDynamicSchemas,
                    ["dynamicSchemaBudgetTokens"] = _dynamicSchemaBudgetTokens,
                    ["instruction"] =
                        "Only bootstrap tools and activeDynamicSchemas are callable. Read an evicted tool capability again with common.capabilities_read before calling it."
                }.ToString(Formatting.None)
            };
        }

        private void Restore(IEnumerable<ChatMessage> messages)
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return;
            foreach (var message in messages ?? new ChatMessage[0])
            {
                string id;
                if (TryReadEvidence(message, out id))
                {
                    Load(id);
                    continue;
                }
                foreach (var calledToolId in CalledToolIds(message)) Touch(calledToolId);
            }
        }

        private IEnumerable<string> CalledToolIds(ChatMessage message)
        {
            if (message == null || !message.ProtocolMessage ||
                !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }
            if (!string.IsNullOrWhiteSpace(message.ToolName) && _catalogById.ContainsKey(message.ToolName))
            {
                yield return message.ToolName;
            }
            foreach (var call in message.ToolCalls ?? new List<LlmToolCall>())
            {
                if (call == null || string.IsNullOrWhiteSpace(call.Name)) continue;
                string id;
                if (_catalogById.ContainsKey(call.Name))
                {
                    yield return call.Name;
                }
                else if (_toolIdByApiName.TryGetValue(call.Name, out id))
                {
                    yield return id;
                }
            }
            if (string.IsNullOrWhiteSpace(message.Content)) yield break;
            JObject root;
            try
            {
                root = JObject.Parse(message.Content);
            }
            catch (JsonException)
            {
                yield break;
            }
            var calls = root["tool_calls"] as JArray;
            if (calls == null) yield break;
            foreach (var call in calls.OfType<JObject>())
            {
                var id = (string)call["name"];
                if (!string.IsNullOrWhiteSpace(id) && _catalogById.ContainsKey(id)) yield return id;
            }
        }

        private IReadOnlyList<string> Load(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || _bootstrapIds.Contains(id) || !_catalogById.ContainsKey(id))
            {
                return new string[0];
            }
            LinkedListNode<string> existing;
            if (_dynamicNodes.TryGetValue(id, out existing))
            {
                _dynamicLru.Remove(existing);
                _dynamicLru.AddLast(existing);
                return new string[0];
            }

            var node = _dynamicLru.AddLast(id);
            _dynamicNodes[id] = node;
            var evicted = new List<string>();
            while (_dynamicLru.Count > MaximumDynamicSchemas ||
                   _dynamicLru.Count > 1 && DynamicSchemaTokens() > _dynamicSchemaBudgetTokens)
            {
                var first = _dynamicLru.First;
                if (first == null || string.Equals(first.Value, id, StringComparison.OrdinalIgnoreCase) &&
                    _dynamicLru.Count == 1)
                {
                    break;
                }
                _dynamicLru.RemoveFirst();
                _dynamicNodes.Remove(first.Value);
                evicted.Add(first.Value);
            }
            return evicted;
        }

        private int DynamicSchemaTokens()
        {
            return _dynamicLru.Sum(id => ModelContextBudget.EstimateTextTokens(
                CapabilityDiscoveryExecutor.Descriptor(_catalogById[id]).ToString(Formatting.None),
                _settings));
        }

        private bool TryReadEvidence(ChatMessage message, out string id)
        {
            id = null;
            if (message == null || !message.ProtocolMessage || string.IsNullOrWhiteSpace(message.Content)) return false;
            var json = message.Content.Trim();
            if (json.StartsWith("TOOL_RESULT:", StringComparison.Ordinal))
            {
                json = json.Substring("TOOL_RESULT:".Length).Trim();
            }
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return false;
            }
            if ((bool?)root["ok"] != true ||
                !string.Equals((string)root["name"], CapabilityDiscoveryExecutor.ReadToolId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var data = root["data"] as JObject;
            if (data == null ||
                !string.Equals((string)data["kind"], "tool-schema", StringComparison.Ordinal) ||
                (bool?)data["loaded"] != true ||
                (bool?)data["complete"] != true ||
                (bool?)data["truncated"] != false)
            {
                return false;
            }
            id = ((string)data["id"] ?? string.Empty).Trim();
            ToolDefinition tool;
            if (string.IsNullOrWhiteSpace(id) || !_catalogById.TryGetValue(id, out tool)) return false;
            var revision = (string)data["revision"] ?? string.Empty;
            if (!string.Equals(revision, CapabilityDiscoveryExecutor.Revision(tool), StringComparison.OrdinalIgnoreCase)) return false;
            var descriptor = data["descriptor"] as JObject;
            return descriptor != null && JToken.DeepEquals(descriptor, CapabilityDiscoveryExecutor.Descriptor(tool));
        }
    }
}
