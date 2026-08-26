using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Core.Storage
{
    public sealed partial class ChatStore
    {
        private static readonly JsonSerializerSettings ProjectionJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new ChatProjectionContractResolver(),
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };
        private static readonly string[] MetadataProperties =
        {
            "FormatVersion", "Id", "ParentSessionId", "ParentSessionRevision", "ForkedThroughMessageId",
            "Host", "DocumentKey", "PreviousDocumentKeys", "DocumentTitle", "DocumentPath",
            "Title", "Model", "Mode", "ReasoningEnabled", "CreatedUtc", "UpdatedUtc"
        };

        private static JObject ToProjectionToken(ChatSession session)
        {
            return JObject.FromObject(session, JsonSerializer.Create(ProjectionJsonSettings));
        }

        private static JObject ReplayProjectionRoot(IEnumerable<SessionEvent> events, JObject seedRoot)
        {
            var root = seedRoot == null ? null : (JObject)seedRoot.DeepClone();
            var replay = root == null ? null : new ProjectionReplayState(root);
            foreach (var sessionEvent in events ?? new List<SessionEvent>())
            {
                if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                    string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
                {
                    if (root != null || sessionEvent.Data == null || sessionEvent.Data.Type != JTokenType.Object) return null;
                    root = (JObject)sessionEvent.Data.DeepClone();
                    if ((int?)root["FormatVersion"] != ChatSession.CurrentFormatVersion) return null;
                    replay = new ProjectionReplayState(root);
                    continue;
                }
                if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal)) continue;
                if (root == null || sessionEvent.Data == null) return null;
                var operations = sessionEvent.Data["Operations"] == null
                    ? new List<SessionOperation>()
                    : sessionEvent.Data["Operations"].ToObject<List<SessionOperation>>();
                ApplyOperations(root, operations, replay);
            }
            if (root == null || replay == null) return null;
            replay.Materialize(root);
            return root;
        }

        private ChatSession Project(
            JObject root,
            long sequence,
            string headHash,
            long tailByteOffset,
            long byteLength,
            long lastWriteUtcTicks,
            bool hydrateActiveArtifacts,
            bool rebuildDerivedProjections)
        {
            if (root == null) return null;
            var session = root.ToObject<ChatSession>();
            session.Revision = sequence;
            session.StorageHeadHash = headHash;
            session.StorageTailByteOffset = tailByteOffset;
            session.StorageByteLength = byteLength;
            session.StorageLastWriteUtcTicks = lastWriteUtcTicks;
            if (rebuildDerivedProjections)
            {
                RebuildHtmlWorkspaceProjection(session);
                RebuildContextCheckpointProjection(session);
                RebuildChartActivityProjection(session);
            }
            if (hydrateActiveArtifacts)
            {
                foreach (var artifact in (session.Artifacts ?? new List<ChatArtifact>()).Where(ShouldHydrateForActiveSession))
                {
                    HydrateArtifact(artifact);
                }
            }
            return session;
        }

        private static List<SessionOperation> BuildOperations(ChatSession beforeSession, ChatSession afterSession)
        {
            var before = ToProjectionToken(beforeSession);
            var after = ToProjectionToken(afterSession);
            var operations = new List<SessionOperation>();

            var metadata = new JObject();
            foreach (var property in MetadataProperties)
            {
                if (!JToken.DeepEquals(before[property], after[property]))
                {
                    metadata[property] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone();
                }
            }
            if (metadata.HasValues) operations.Add(Operation(SessionOperationTypes.SessionMetadataSet, metadata));

            AddSetOperation(operations, before, after, "Context", SessionOperationTypes.ContextSet);
            AddRunOperation(operations, before["LastRun"], after["LastRun"]);
            AddListOperations(operations, before, after, "Messages", "Id",
                SessionOperationTypes.MessageUpdated, SessionOperationTypes.MessageRemove, SessionOperationTypes.MessagesReorder);
            AddListOperations(operations, before, after, "Artifacts", "Id",
                SessionOperationTypes.ArtifactRevisionCreated, SessionOperationTypes.ArtifactRemove, SessionOperationTypes.ArtifactsReorder);

            var active = new JObject();
            foreach (var property in new[] { "ActiveContextCheckpointId", "ActiveHtmlArtifactId", "ActiveTaskListArtifactId", "ActivePlanDocumentArtifactId" })
            {
                if (!JToken.DeepEquals(before[property], after[property]))
                {
                    active[property] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone();
                }
            }
            if (active.HasValues) operations.Add(Operation(SessionOperationTypes.ActiveReferencesSet, active));
            return operations;
        }

        private static void AddSetOperation(
            ICollection<SessionOperation> operations,
            JObject before,
            JObject after,
            string property,
            string operationType)
        {
            if (!JToken.DeepEquals(before[property], after[property]))
            {
                operations.Add(Operation(operationType, new JObject
                {
                    ["Value"] = after[property] == null ? JValue.CreateNull() : after[property].DeepClone()
                }));
            }
        }

        private static void AddRunOperation(ICollection<SessionOperation> operations, JToken before, JToken after)
        {
            if (JToken.DeepEquals(before, after)) return;
            var type = IsNull(before)
                ? SessionOperationTypes.RunStarted
                : IsNull(after)
                    ? SessionOperationTypes.RunEnded
                    : SessionOperationTypes.RunUpdated;
            var data = new JObject
            {
                ["Value"] = after == null ? JValue.CreateNull() : after.DeepClone()
            };
            if (string.Equals(type, SessionOperationTypes.RunEnded, StringComparison.Ordinal))
            {
                data["Previous"] = before == null ? JValue.CreateNull() : before.DeepClone();
            }
            operations.Add(Operation(type, data));
        }

        private static bool IsNull(JToken value)
        {
            return value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined;
        }

        private static void AddListOperations(
            ICollection<SessionOperation> operations,
            JObject before,
            JObject after,
            string property,
            string idProperty,
            string upsertType,
            string removeType,
            string reorderType)
        {
            var beforeItems = (before[property] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var afterItems = (after[property] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var beforeById = beforeItems.Where(item => !string.IsNullOrWhiteSpace((string)item[idProperty]))
                .ToDictionary(item => (string)item[idProperty], item => item, StringComparer.OrdinalIgnoreCase);
            var afterById = afterItems.Where(item => !string.IsNullOrWhiteSpace((string)item[idProperty]))
                .ToDictionary(item => (string)item[idProperty], item => item, StringComparer.OrdinalIgnoreCase);

            foreach (var item in afterItems)
            {
                var id = (string)item[idProperty];
                JObject previous = null;
                var existed = !string.IsNullOrWhiteSpace(id) && beforeById.TryGetValue(id, out previous);
                if (!existed || !JToken.DeepEquals(previous, item))
                {
                    operations.Add(Operation(ResolveUpsertType(property, upsertType, previous, item),
                        new JObject { ["Value"] = item.DeepClone() }));
                }
            }
            foreach (var item in beforeItems)
            {
                var id = (string)item[idProperty];
                if (!string.IsNullOrWhiteSpace(id) && !afterById.ContainsKey(id))
                {
                    operations.Add(Operation(removeType, new JObject { ["Id"] = id }));
                }
            }

            var beforeOrder = beforeItems.Select(item => (string)item[idProperty]).ToList();
            var afterOrder = afterItems.Select(item => (string)item[idProperty]).ToList();
            var replayOrder = beforeOrder
                .Where(id => !string.IsNullOrWhiteSpace(id) && afterById.ContainsKey(id))
                .ToList();
            replayOrder.AddRange(afterOrder.Where(id =>
                !string.IsNullOrWhiteSpace(id) && !beforeById.ContainsKey(id)));
            if (!replayOrder.SequenceEqual(afterOrder, StringComparer.OrdinalIgnoreCase))
            {
                operations.Add(Operation(reorderType, new JObject { ["Ids"] = JArray.FromObject(afterOrder) }));
            }
        }

        private static SessionOperation Operation(string type, JObject data)
        {
            return new SessionOperation { Type = type, Data = data ?? new JObject() };
        }

        private static string ResolveUpsertType(string property, string fallback, JObject previous, JObject item)
        {
            if (string.Equals(property, "Artifacts", StringComparison.Ordinal))
            {
                return SessionOperationTypes.ArtifactRevisionCreated;
            }
            if (!string.Equals(property, "Messages", StringComparison.Ordinal)) return fallback;

            var activity = item["Activity"] as JObject;
            var status = activity == null ? null : (string)activity["Status"];
            var executionStatus = activity == null ? null : (string)activity["ExecutionStatus"];
            var toolCallId = activity == null ? null : (string)activity["ToolCallId"];
            if (!string.IsNullOrWhiteSpace(toolCallId) && string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.ToolExecutionStarted;
            }
            if (!string.IsNullOrWhiteSpace(toolCallId) &&
                (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(status, "waiting_confirmation", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(executionStatus, "waiting_confirmation", StringComparison.OrdinalIgnoreCase)))
            {
                return SessionOperationTypes.ToolExecutionFinished;
            }
            if ((bool?)item["ProtocolMessage"] == true)
            {
                var calls = item["ToolCalls"] as JArray;
                return calls != null && calls.Count > 0
                    ? SessionOperationTypes.ToolCallRecorded
                    : SessionOperationTypes.ToolResultRecorded;
            }
            if (previous == null && string.Equals((string)item["Role"], "user", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.UserMessageAppended;
            }
            if (previous == null && string.Equals((string)item["Role"], "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return SessionOperationTypes.AssistantMessageAppended;
            }
            return fallback;
        }

        private static void ApplyOperations(
            JObject root,
            IEnumerable<SessionOperation> operations,
            ProjectionReplayState replay)
        {
            foreach (var operation in operations ?? new List<SessionOperation>())
            {
                if (operation == null || string.IsNullOrWhiteSpace(operation.Type)) continue;
                var data = operation.Data ?? new JObject();
                switch (operation.Type)
                {
                    case SessionOperationTypes.SessionMetadataSet:
                        if (data.Property("DocumentKey") != null)
                        {
                            RecordPreviousDocumentKey(
                                root,
                                (string)root["DocumentKey"],
                                (string)data["DocumentKey"]);
                        }
                        foreach (var property in data.Properties()) root[property.Name] = property.Value.DeepClone();
                        break;
                    case SessionOperationTypes.ActiveReferencesSet:
                        foreach (var property in data.Properties()) root[property.Name] = property.Value.DeepClone();
                        break;
                    case SessionOperationTypes.ContextSet:
                        root["Context"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.RunStarted:
                    case SessionOperationTypes.RunUpdated:
                    case SessionOperationTypes.RunEnded:
                        root["LastRun"] = CloneValue(data["Value"]);
                        break;
                    case SessionOperationTypes.MessageUpdated:
                    case SessionOperationTypes.UserMessageAppended:
                    case SessionOperationTypes.AssistantMessageAppended:
                    case SessionOperationTypes.ToolCallRecorded:
                    case SessionOperationTypes.ToolResultRecorded:
                    case SessionOperationTypes.ToolExecutionStarted:
                    case SessionOperationTypes.ToolExecutionFinished:
                        replay.Upsert("Messages", data["Value"]);
                        break;
                    case SessionOperationTypes.MessageRemove:
                        replay.Remove("Messages", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.MessagesReorder:
                        replay.Reorder("Messages", data["Ids"] as JArray);
                        break;
                    case SessionOperationTypes.ArtifactRevisionCreated:
                        replay.Upsert("Artifacts", data["Value"]);
                        break;
                    case SessionOperationTypes.ArtifactRemove:
                        replay.Remove("Artifacts", (string)data["Id"]);
                        break;
                    case SessionOperationTypes.ArtifactsReorder:
                        replay.Reorder("Artifacts", data["Ids"] as JArray);
                        break;
                    default:
                        throw new JsonException("Unsupported session operation: " + operation.Type);
                }
            }
        }

        private static void RecordPreviousDocumentKey(
            JObject root,
            string previousDocumentKey,
            string currentDocumentKey)
        {
            var keys = (root["PreviousDocumentKeys"] as JArray ?? new JArray())
                .Values<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            if (!string.IsNullOrWhiteSpace(previousDocumentKey) &&
                !string.Equals(previousDocumentKey, currentDocumentKey, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(previousDocumentKey.Trim());
            }
            root["PreviousDocumentKeys"] = new JArray(keys
                .Where(value => !string.Equals(value, currentDocumentKey, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static JToken CloneValue(JToken value)
        {
            return value == null ? JValue.CreateNull() : value.DeepClone();
        }

        private sealed class ProjectionReplayState
        {
            private readonly ProjectionReplayList _messages;
            private readonly ProjectionReplayList _artifacts;

            public ProjectionReplayState(JObject root)
            {
                _messages = new ProjectionReplayList(root == null ? null : root["Messages"] as JArray);
                _artifacts = new ProjectionReplayList(root == null ? null : root["Artifacts"] as JArray);
            }

            public void Upsert(string property, JToken value)
            {
                List(property).Upsert(value);
            }

            public void Remove(string property, string id)
            {
                List(property).Remove(id);
            }

            public void Reorder(string property, JArray ids)
            {
                List(property).Reorder(ids);
            }

            public void Materialize(JObject root)
            {
                root["Messages"] = _messages.Materialize();
                root["Artifacts"] = _artifacts.Materialize();
            }

            private ProjectionReplayList List(string property)
            {
                if (string.Equals(property, "Messages", StringComparison.Ordinal)) return _messages;
                if (string.Equals(property, "Artifacts", StringComparison.Ordinal)) return _artifacts;
                throw new JsonException("Unsupported projection list: " + property);
            }
        }

        private sealed class ProjectionReplayList
        {
            private List<ProjectionReplayItem> _ordered;
            private readonly Dictionary<string, ProjectionReplayItem> _byId;

            public ProjectionReplayList(JArray source)
            {
                _ordered = new List<ProjectionReplayItem>();
                _byId = new Dictionary<string, ProjectionReplayItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in (source ?? new JArray()).OfType<JObject>())
                {
                    var item = new ProjectionReplayItem
                    {
                        Id = (string)value["Id"],
                        Value = value,
                        Active = true
                    };
                    _ordered.Add(item);
                    if (!string.IsNullOrWhiteSpace(item.Id) && !_byId.ContainsKey(item.Id))
                    {
                        _byId.Add(item.Id, item);
                    }
                }
            }

            public void Upsert(JToken value)
            {
                var objectValue = value as JObject;
                var id = objectValue == null ? null : (string)objectValue["Id"];
                if (objectValue == null || string.IsNullOrWhiteSpace(id))
                {
                    throw new JsonException("Upsert operation requires an object id.");
                }
                ProjectionReplayItem existing;
                if (_byId.TryGetValue(id, out existing))
                {
                    existing.Value = objectValue;
                    return;
                }
                var item = new ProjectionReplayItem
                {
                    Id = id,
                    Value = objectValue,
                    Active = true
                };
                _ordered.Add(item);
                _byId[id] = item;
            }

            public void Remove(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                ProjectionReplayItem existing;
                if (!_byId.TryGetValue(id, out existing)) return;
                existing.Active = false;
                _byId.Remove(id);
                var duplicate = _ordered.FirstOrDefault(item => item.Active &&
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null) _byId[id] = duplicate;
            }

            public void Reorder(JArray ids)
            {
                var remaining = new Dictionary<string, ProjectionReplayItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _ordered.Where(value => value.Active && !string.IsNullOrWhiteSpace(value.Id)))
                {
                    if (remaining.ContainsKey(item.Id))
                    {
                        throw new JsonException("Projection list contains duplicate ids.");
                    }
                    remaining.Add(item.Id, item);
                }

                var reordered = new List<ProjectionReplayItem>();
                foreach (var id in (ids ?? new JArray()).Values<string>())
                {
                    ProjectionReplayItem item;
                    if (!string.IsNullOrWhiteSpace(id) && remaining.TryGetValue(id, out item))
                    {
                        reordered.Add(item);
                        remaining.Remove(id);
                    }
                }
                foreach (var item in _ordered)
                {
                    if (!item.Active || string.IsNullOrWhiteSpace(item.Id) || !remaining.ContainsKey(item.Id)) continue;
                    reordered.Add(item);
                    remaining.Remove(item.Id);
                }

                _ordered = reordered;
                _byId.Clear();
                foreach (var item in _ordered) _byId[item.Id] = item;
            }

            public JArray Materialize()
            {
                var result = new JArray();
                foreach (var item in _ordered.Where(value => value.Active))
                {
                    result.Add(item.Value.DeepClone());
                }
                return result;
            }
        }

        private sealed class ProjectionReplayItem
        {
            public string Id { get; set; }
            public JObject Value { get; set; }
            public bool Active { get; set; }
        }

        private sealed class ChatProjectionContractResolver : DefaultContractResolver
        {
            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);
                if (member.DeclaringType == typeof(ChatArtifact) &&
                    string.Equals(member.Name, "InlineText", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => string.IsNullOrWhiteSpace((value as ChatArtifact)?.ContentSha256);
                }
                if (member.DeclaringType == typeof(ChatSession) &&
                    string.Equals(member.Name, "HtmlWorkspace", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => false;
                }
                if (member.DeclaringType == typeof(ChatSession) &&
                    string.Equals(member.Name, "ContextCheckpoints", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => false;
                }
                if (member.DeclaringType == typeof(ChatMessage) &&
                    string.Equals(member.Name, "Content", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => !IsCompactionMessage(value as ChatMessage);
                }
                if (member.DeclaringType == typeof(ChatActivity) &&
                    string.Equals(member.Name, "ResultMessage", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value => !IsCompactionActivity(value as ChatActivity);
                }
                if (member.DeclaringType == typeof(ChatActivity) &&
                    string.Equals(member.Name, "DataJson", StringComparison.Ordinal))
                {
                    property.ShouldSerialize = value =>
                    {
                        if (IsCompactionActivity(value as ChatActivity)) return false;
                        JObject ignored;
                        return !ChartArtifactPayload.TryParse((value as ChatActivity)?.DataJson, out ignored);
                    };
                }
                return property;
            }

            private static bool IsCompactionMessage(ChatMessage message)
            {
                return message != null && IsCompactionActivity(message.Activity) &&
                    message.ResourceRefs != null && message.ResourceRefs.Count > 0;
            }

            private static bool IsCompactionActivity(ChatActivity activity)
            {
                return activity != null &&
                    string.Equals(activity.Kind, "compaction", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(activity.Status, "completed", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
