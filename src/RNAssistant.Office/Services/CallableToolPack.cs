using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    // Model-visible callable membership for one run. Execution authority remains
    // the immutable ToolPackSnapshot captured by ConversationKernelAdapter.
    internal sealed class CallableToolPack
    {
        private static readonly HashSet<string> BootstrapToolIds = ExactIds(
            ResourceToolExecutor.ListToolId,
            ResourceToolExecutor.ResolveToolId,
            ResourceToolExecutor.SearchToolId,
            ResourceToolExecutor.ReadToolId,
            CapabilityDiscoveryExecutor.SearchToolId,
            CapabilityDiscoveryExecutor.ReadToolId);

        private static readonly HashSet<string> ExcelCoreToolIds = ExactIds(
            "excel.inspect",
            "excel.read_range",
            "excel.find_cells",
            "excel.create_chat_chart",
            "excel.replace_cells",
            "excel.write_range",
            "excel.add_table",
            "excel.upsert_chart",
            "excel.delete_chart",
            "excel.format_range",
            "excel.add_sheet",
            "excel.rename_sheet",
            "excel.clear_range",
            "excel.sort_range",
            "excel.filter_range");

        private static readonly HashSet<string> VbaCoreToolIds = ExactIds(
            "common.vba_restore_backup",
            "common.vba_write_module",
            "common.vba_apply_patch",
            "common.vba_delete_module",
            "common.office_run_macro");

        private readonly string _mode;
        private readonly string _host;
        private readonly string _runId;
        private readonly IReadOnlyList<ToolDefinition> _catalog;
        private readonly IDictionary<string, ToolDefinition> _catalogById;
        private readonly HashSet<string> _coreIds;
        private readonly List<string> _optionalIds = new List<string>();
        private readonly HashSet<string> _optionalSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _pendingIds = new List<string>();
        private readonly HashSet<string> _pendingSet = new HashSet<string>(StringComparer.Ordinal);

        private CallableToolPack(
            string mode,
            string host,
            string runId,
            IReadOnlyList<ToolDefinition> catalog)
        {
            _mode = ChatModes.Normalize(mode);
            _host = host ?? string.Empty;
            _runId = runId ?? string.Empty;
            _catalog = (catalog ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.Ordinal)
                .Select(group => group.First().Clone())
                .OrderBy(tool => tool.Id, StringComparer.Ordinal)
                .ToArray();
            _catalogById = _catalog.ToDictionary(tool => tool.Id, StringComparer.Ordinal);
            _coreIds = SelectCoreIds(_mode, _host, _catalogById.Keys);
        }

        public static CallableToolPack Create(
            string mode,
            string host,
            string runId,
            IReadOnlyList<ToolDefinition> catalog)
        {
            return new CallableToolPack(mode, host, runId, catalog);
        }

        public IReadOnlyList<ToolDefinition> Tools
        {
            get { return ToolsFor(_optionalIds); }
        }

        public IReadOnlyList<ToolDefinition> Catalog
        {
            get { return _catalog; }
        }

        public int OptionalSchemaCount
        {
            get { return _optionalIds.Count; }
        }

        public string Revision
        {
            get { return SnapshotRevision(Tools); }
        }

        public JObject CapabilityContext(IEnumerable<SkillDefinition> skills)
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return null;
            var result = CapabilityDiscoveryExecutor.BuildPromptCatalog(_catalog, skills, Tools);
            result["policy"] = "tool-pack";
            result["profile"] = ProfileId();
            result["snapshotRevision"] = Revision;
            result["coreSchemas"] = SchemaRefs(_coreIds);
            result["optionalSchemas"] = SchemaRefs(_optionalIds);
            result["extensionBoundary"] = "next_model_step";
            result["admissionPolicy"] = "atomic_full_request_budget";
            result["evictionPolicy"] = "none_until_run_end";
            return result;
        }

        public bool StageReadResult(ChatMessage message)
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return false;
            // Raw read evidence proves only that a descriptor was returned. It is
            // not replay authority for a prior admission decision. Until Phase 8C
            // persists that decision, accept evidence only from this live run.
            if (message == null || string.IsNullOrWhiteSpace(_runId) ||
                !string.Equals(message.RunId, _runId, StringComparison.Ordinal)) return false;
            string id;
            if (!TryReadEvidence(message, out id) || _coreIds.Contains(id) || _optionalSet.Contains(id) ||
                !_pendingSet.Add(id)) return false;
            _pendingIds.Add(id);
            return true;
        }

        public ToolPackAdmission CommitPending(
            Func<IReadOnlyList<ToolDefinition>, ChatMessage, bool> canPublish)
        {
            if (_pendingIds.Count == 0) return null;

            var requested = _pendingIds.ToArray();
            var previousRevision = Revision;
            var candidateIds = _optionalIds.Concat(requested).ToArray();
            var candidateTools = ToolsFor(candidateIds);
            var candidateRevision = SnapshotRevision(candidateTools);
            var acceptedMessage = BuildStateMessage(
                requested, true, null, previousRevision, candidateRevision, candidateIds);
            var admitted = canPublish != null && canPublish(candidateTools, acceptedMessage);

            _pendingIds.Clear();
            _pendingSet.Clear();
            if (admitted)
            {
                foreach (var id in requested)
                {
                    if (_optionalSet.Add(id)) _optionalIds.Add(id);
                }
                return new ToolPackAdmission(true, requested, previousRevision, candidateRevision, acceptedMessage);
            }

            var rejectedMessage = BuildStateMessage(
                requested,
                false,
                "tool_pack_budget_exceeded",
                previousRevision,
                previousRevision,
                _optionalIds);
            return new ToolPackAdmission(false, requested, previousRevision, previousRevision, rejectedMessage);
        }

        private IReadOnlyList<ToolDefinition> ToolsFor(IEnumerable<string> optionalIds)
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return _catalog;
            var active = new HashSet<string>(_coreIds, StringComparer.Ordinal);
            foreach (var id in optionalIds ?? new string[0]) active.Add(id);
            return _catalog.Where(tool => active.Contains(tool.Id)).ToArray();
        }

        private ChatMessage BuildStateMessage(
            IEnumerable<string> requested,
            bool admitted,
            string code,
            string previousRevision,
            string revision,
            IEnumerable<string> optionalIds)
        {
            var requestedIds = (requested ?? new string[0]).ToArray();
            return new ChatMessage
            {
                Role = "user",
                ProtocolMessage = true,
                Content = "TOOL_PACK_STATE:\n" + new JObject
                {
                    ["profile"] = ProfileId(),
                    ["catalogRevision"] = CapabilityDiscoveryExecutor.ToolCatalogRevision(_catalog),
                    ["previousSnapshotRevision"] = previousRevision,
                    ["snapshotRevision"] = revision,
                    ["requestedSchemas"] = admitted
                        ? (JToken)new JArray(requestedIds)
                        : JValue.CreateNull(),
                    ["requestedSchemaCount"] = requestedIds.Length,
                    ["admitted"] = admitted,
                    ["code"] = code == null ? JValue.CreateNull() : (JToken)new JValue(code),
                    ["activeOptionalSchemas"] = admitted
                        ? (JToken)SchemaRefs(optionalIds)
                        : JValue.CreateNull(),
                    ["instruction"] = admitted
                        ? "The requested exact schemas are callable from this model step and remain callable in this live model session. No schema was evicted. After confirmation continuation or context reconstruction, read and admit an optional schema again until durable admission events are available."
                        : "The requested schemas were not added because the complete next request would exceed its input budget. Existing schemas remain callable and no schema was evicted. Do not call a rejected tool unless a later exact read is admitted."
                }.ToString(Formatting.None)
            };
        }

        private JArray SchemaRefs(IEnumerable<string> ids)
        {
            return new JArray((ids ?? new string[0])
                .Where(id => _catalogById.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => new JObject
                {
                    ["id"] = id,
                    ["revision"] = CapabilityDiscoveryExecutor.Revision(_catalogById[id])
                }));
        }

        private string SnapshotRevision(IReadOnlyList<ToolDefinition> tools)
        {
            return ToolPackSnapshotFactory.Capture(_mode, _host, tools).Revision;
        }

        private string ProfileId()
        {
            if (string.Equals(_mode, ChatModes.Chat, StringComparison.Ordinal)) return "chat-resource-core";
            if (string.Equals(_mode, ChatModes.Agent, StringComparison.Ordinal) &&
                string.Equals(_host, "Excel", StringComparison.OrdinalIgnoreCase)) return "excel-vba-core";
            if (string.Equals(_mode, ChatModes.Agent, StringComparison.Ordinal) &&
                (string.Equals(_host, "Word", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(_host, "PowerPoint", StringComparison.OrdinalIgnoreCase))) return "vba-core";
            return "bootstrap-core";
        }

        private static HashSet<string> SelectCoreIds(
            string mode,
            string host,
            IEnumerable<string> catalogIds)
        {
            var catalog = new HashSet<string>(catalogIds ?? new string[0], StringComparer.Ordinal);
            if (string.Equals(mode, ChatModes.Chat, StringComparison.Ordinal)) return catalog;
            var selected = new HashSet<string>(BootstrapToolIds, StringComparer.Ordinal);
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase)) selected.UnionWith(ExcelCoreToolIds);
            if (string.Equals(mode, ChatModes.Agent, StringComparison.Ordinal) &&
                (string.Equals(host, "Excel", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))) selected.UnionWith(VbaCoreToolIds);
            selected.IntersectWith(catalog);
            return selected;
        }

        private bool TryReadEvidence(ChatMessage message, out string id)
        {
            id = null;
            RNAssistant.Core.ModelProtocol.ToolResultWireReadResult result;
            string error;
            if (!ToolResultHistoryReader.TryRead(message, out result, out error) ||
                result.Result.Status != RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok ||
                !string.Equals(result.Name, CapabilityDiscoveryExecutor.ReadToolId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(result.Result.DataJson)) return false;
            JObject data;
            try
            {
                data = JsonConvert.DeserializeObject<JToken>(result.Result.DataJson,
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) as JObject;
            }
            catch (JsonException)
            {
                return false;
            }
            if (data == null ||
                data["kind"]?.Type != JTokenType.String || (string)data["kind"] != "tool-schema" ||
                data["loaded"]?.Type != JTokenType.Boolean || !(bool)data["loaded"] ||
                data["complete"]?.Type != JTokenType.Boolean || !(bool)data["complete"] ||
                data["truncated"]?.Type != JTokenType.Boolean || (bool)data["truncated"] ||
                data["id"]?.Type != JTokenType.String || data["revision"]?.Type != JTokenType.String)
            {
                return false;
            }
            id = (string)data["id"];
            ToolDefinition tool;
            if (string.IsNullOrWhiteSpace(id) || !_catalogById.TryGetValue(id, out tool) ||
                !string.Equals(id, tool.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals((string)data["revision"], CapabilityDiscoveryExecutor.Revision(tool), StringComparison.Ordinal)) return false;
            var descriptor = data["descriptor"] as JObject;
            return descriptor != null && JToken.DeepEquals(descriptor, CapabilityDiscoveryExecutor.Descriptor(tool));
        }

        private static HashSet<string> ExactIds(params string[] ids)
        {
            return new HashSet<string>(ids ?? new string[0], StringComparer.Ordinal);
        }
    }

    internal sealed class ToolPackAdmission
    {
        public bool Admitted { get; private set; }
        public IReadOnlyList<string> RequestedIds { get; private set; }
        public string PreviousRevision { get; private set; }
        public string Revision { get; private set; }
        public ChatMessage StateMessage { get; private set; }

        public ToolPackAdmission(bool admitted, IReadOnlyList<string> requestedIds,
            string previousRevision, string revision, ChatMessage stateMessage)
        {
            Admitted = admitted;
            RequestedIds = requestedIds ?? new string[0];
            PreviousRevision = previousRevision ?? string.Empty;
            Revision = revision ?? string.Empty;
            StateMessage = stateMessage;
        }
    }
}
