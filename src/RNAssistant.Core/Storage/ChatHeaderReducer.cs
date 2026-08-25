using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Storage
{
    /// <summary>
    /// Minimal replay state used by chat-list reads. It deliberately does not retain message
    /// bodies, tool payloads, context, or non-HTML artifact data.
    /// </summary>
    internal sealed class ChatHeaderReducer
    {
        private readonly ChatBlobStore _blobs;
        private HeaderReplayList<HeaderMessage> _messages = new HeaderReplayList<HeaderMessage>();
        private HeaderReplayList<HeaderArtifact> _artifacts = new HeaderReplayList<HeaderArtifact>();
        private Dictionary<string, CasUsageEntry> _casReferences =
            new Dictionary<string, CasUsageEntry>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _conflictingCasReferences =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _seeded;
        private bool _invalid;
        private string _id;
        private string _host;
        private string _documentKey;
        private string _documentTitle;
        private string _documentPath;
        private string _title;
        private string _model;
        private string _mode;
        private bool _reasoningEnabled;
        private DateTime _createdUtc;
        private DateTime _updatedUtc;
        private string _activeHtmlArtifactId;
        private ChatRunRecord _lastRun;
        private long _casLogicalByteLength;
        private long _casStoredByteLength;
        private int _casMissingBlobCount;
        private int _invalidCasReferenceCount;

        public ChatHeaderReducer(ChatBlobStore blobs)
        {
            _blobs = blobs;
        }

        public bool IsValid
        {
            get { return _seeded && !_invalid; }
        }

        public string SessionId
        {
            get { return _id; }
        }

        public long EstimatedCharacters
        {
            get
            {
                long total = StringLength(_id) + StringLength(_host) + StringLength(_documentKey) +
                    StringLength(_documentTitle) + StringLength(_documentPath) + StringLength(_title) +
                    StringLength(_model) + StringLength(_mode) + StringLength(_activeHtmlArtifactId) + 256;
                total += _messages.Items.Sum(item => StringLength(item.Id) + 16L);
                total += _artifacts.Items.Sum(item => StringLength(item.Id) + StringLength(item.Kind) +
                    StringLength(item.ContentSha256) + 48L);
                total += _casReferences.Count * 128L + _conflictingCasReferences.Count * 80L;
                if (_lastRun != null)
                {
                    total += StringLength(_lastRun.RunId) + StringLength(_lastRun.RuntimeId) +
                        StringLength(_lastRun.Status) + StringLength(_lastRun.Phase) + 128;
                }
                return total;
            }
        }

        public void Apply(SessionEvent sessionEvent)
        {
            if (sessionEvent == null) return;
            CaptureCasReferences(sessionEvent);
            if (string.Equals(sessionEvent.Type, SessionEventTypes.SessionCreated, StringComparison.Ordinal) ||
                string.Equals(sessionEvent.Type, SessionEventTypes.SessionForked, StringComparison.Ordinal))
            {
                var root = sessionEvent.Data as JObject;
                if (_seeded || root == null)
                {
                    _invalid = true;
                    return;
                }
                Seed(root);
                return;
            }
            if (!string.Equals(sessionEvent.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal)) return;
            if (!_seeded || sessionEvent.Data == null)
            {
                _invalid = true;
                return;
            }

            var operationsToken = sessionEvent.Data["Operations"];
            var operations = operationsToken == null
                ? new List<SessionOperation>()
                : operationsToken.ToObject<List<SessionOperation>>();
            ApplyOperations(operations);
        }

        public ChatHeaderReducer Clone()
        {
            return new ChatHeaderReducer(_blobs)
            {
                _messages = _messages.Clone(item => item.Clone()),
                _artifacts = _artifacts.Clone(item => item.Clone()),
                _casReferences = _casReferences.ToDictionary(
                    item => item.Key,
                    item => item.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase),
                _conflictingCasReferences = new HashSet<string>(
                    _conflictingCasReferences,
                    StringComparer.OrdinalIgnoreCase),
                _seeded = _seeded,
                _invalid = _invalid,
                _id = _id,
                _host = _host,
                _documentKey = _documentKey,
                _documentTitle = _documentTitle,
                _documentPath = _documentPath,
                _title = _title,
                _model = _model,
                _mode = _mode,
                _reasoningEnabled = _reasoningEnabled,
                _createdUtc = _createdUtc,
                _updatedUtc = _updatedUtc,
                _activeHtmlArtifactId = _activeHtmlArtifactId,
                _lastRun = CloneRun(_lastRun),
                _casLogicalByteLength = _casLogicalByteLength,
                _casStoredByteLength = _casStoredByteLength,
                _casMissingBlobCount = _casMissingBlobCount,
                _invalidCasReferenceCount = _invalidCasReferenceCount
            };
        }

        public ChatSessionHeader CreateHeader(
            ChatBlobStore blobs,
            long revision,
            long jsonlByteLength,
            string fallbackHost,
            string fallbackDocumentKey,
            string fallbackDocumentTitle)
        {
            if (!IsValid) return null;
            var host = string.IsNullOrWhiteSpace(_host) ? fallbackHost ?? _host ?? string.Empty : _host;
            var documentKey = string.IsNullOrWhiteSpace(_documentKey)
                ? fallbackDocumentKey ?? _documentKey ?? string.Empty
                : _documentKey;
            var storedTitle = _title;
            var documentTitle = string.IsNullOrWhiteSpace(_documentTitle)
                ? fallbackDocumentTitle ?? _documentTitle ?? storedTitle ?? string.Empty
                : _documentTitle;
            var title = string.IsNullOrWhiteSpace(storedTitle) ? "New chat" : storedTitle;
            var createdUtc = _createdUtc;
            var updatedUtc = _updatedUtc;
            if (createdUtc == default(DateTime))
            {
                createdUtc = updatedUtc == default(DateTime) ? DateTime.UtcNow : updatedUtc;
            }
            if (updatedUtc == default(DateTime)) updatedUtc = createdUtc;

            int fileCount;
            int dataSourceCount;
            ReadWorkspaceCounts(blobs, out fileCount, out dataSourceCount);
            var run = _lastRun;
            var casReferenceIssueCount = SaturatingAdd(
                _invalidCasReferenceCount,
                _conflictingCasReferences.Count);
            jsonlByteLength = Math.Max(0, jsonlByteLength);
            return new ChatSessionHeader
            {
                Id = string.IsNullOrWhiteSpace(_id) ? Guid.NewGuid().ToString("N") : _id,
                Revision = revision,
                Host = host,
                DocumentKey = documentKey,
                DocumentTitle = documentTitle,
                DocumentPath = _documentPath,
                Title = title,
                Model = _model,
                Mode = ChatModes.Normalize(_mode),
                ReasoningEnabled = _reasoningEnabled,
                HasHtmlWorkspace = fileCount > 0 || dataSourceCount > 0,
                HtmlFileCount = fileCount,
                HtmlDataSourceCount = dataSourceCount,
                CreatedUtc = createdUtc,
                UpdatedUtc = updatedUtc,
                MessageCount = _messages.Items.Count(item => item.Active && !item.ProtocolMessage),
                RunId = run == null ? null : run.RunId,
                RunRuntimeId = run == null ? null : run.RuntimeId,
                RunStatus = run == null ? null : run.Status,
                RunPhase = run == null ? null : run.Phase,
                RunStartedUtc = run == null ? (DateTime?)null : run.StartedUtc,
                JsonlByteLength = jsonlByteLength,
                CasBlobCount = _casReferences.Count,
                CasLogicalByteLength = _casLogicalByteLength,
                CasStoredByteLength = _casStoredByteLength,
                CasMissingBlobCount = _casMissingBlobCount,
                CasReferenceIssueCount = casReferenceIssueCount,
                StorageWarningLevel = ChatStorageUsagePolicy.GetWarningLevel(
                    jsonlByteLength,
                    _casLogicalByteLength,
                    _casStoredByteLength,
                    _casMissingBlobCount,
                    casReferenceIssueCount)
            };
        }

        private void CaptureCasReferences(SessionEvent sessionEvent)
        {
            CaptureCasReference(sessionEvent.Payload);
            CaptureTokenReferences(sessionEvent.Data);
        }

        private void CaptureTokenReferences(JToken token)
        {
            if (token == null) return;
            var value = token as JObject;
            if (value != null)
            {
                CaptureCasPair(value, "Sha256", "ByteLength");
                CaptureCasPair(value, "ContentSha256", "ContentByteLength");
                CaptureCasPair(value, "ExtractedTextSha256", "ExtractedTextByteLength");
            }
            foreach (var child in token.Children()) CaptureTokenReferences(child);
        }

        private void CaptureCasPair(JObject value, string hashProperty, string lengthProperty)
        {
            var hashToken = value[hashProperty];
            if (hashToken == null || hashToken.Type == JTokenType.Null ||
                hashToken.Type == JTokenType.Undefined) return;
            var hash = hashToken.Type == JTokenType.String ? (string)hashToken : null;
            if (string.IsNullOrWhiteSpace(hash)) return;

            var byteLength = -1L;
            var lengthToken = value[lengthProperty];
            if (lengthToken != null && lengthToken.Type == JTokenType.Integer)
            {
                try { byteLength = lengthToken.ToObject<long>(); }
                catch (Exception ex) when (ex is JsonException || ex is FormatException ||
                    ex is OverflowException || ex is InvalidCastException)
                {
                    byteLength = -1;
                }
            }
            CaptureCasReference(new ChatBlobReference { Sha256 = hash, ByteLength = byteLength });
        }

        private void CaptureCasReference(ChatBlobReference reference)
        {
            if (reference == null) return;
            if (!ChatBlobStore.ValidReference(reference))
            {
                _invalidCasReferenceCount = SaturatingIncrement(_invalidCasReferenceCount);
                return;
            }

            var hash = reference.Sha256.ToLowerInvariant();
            CasUsageEntry existing;
            if (_casReferences.TryGetValue(hash, out existing))
            {
                if (existing.LogicalByteLength != reference.ByteLength &&
                    _conflictingCasReferences.Add(hash))
                {
                    return;
                }
                return;
            }

            var storedByteLength = 0L;
            var missing = _blobs == null || !_blobs.TryGetStoredByteLength(hash, out storedByteLength);
            var entry = new CasUsageEntry
            {
                LogicalByteLength = reference.ByteLength,
                StoredByteLength = missing ? 0 : storedByteLength,
                Missing = missing
            };
            _casReferences[hash] = entry;
            _casLogicalByteLength = SaturatingAdd(_casLogicalByteLength, entry.LogicalByteLength);
            _casStoredByteLength = SaturatingAdd(_casStoredByteLength, entry.StoredByteLength);
            if (missing) _casMissingBlobCount = SaturatingIncrement(_casMissingBlobCount);
        }

        private static long SaturatingAdd(long first, long second)
        {
            if (first < 0) first = 0;
            if (second < 0) second = 0;
            return first > long.MaxValue - second ? long.MaxValue : first + second;
        }

        private static int SaturatingAdd(int first, int second)
        {
            return first > int.MaxValue - second ? int.MaxValue : first + second;
        }

        private static int SaturatingIncrement(int value)
        {
            return value == int.MaxValue ? value : value + 1;
        }

        private void Seed(JObject root)
        {
            _seeded = true;
            _id = StringValue(root["Id"]);
            _host = StringValue(root["Host"]);
            _documentKey = StringValue(root["DocumentKey"]);
            _documentTitle = StringValue(root["DocumentTitle"]);
            _documentPath = StringValue(root["DocumentPath"]);
            _title = StringValue(root["Title"]);
            _model = StringValue(root["Model"]);
            _mode = StringValue(root["Mode"]);
            _reasoningEnabled = BooleanValue(root["ReasoningEnabled"]);
            _createdUtc = DateTimeValue(root["CreatedUtc"]);
            _updatedUtc = DateTimeValue(root["UpdatedUtc"]);
            _activeHtmlArtifactId = StringValue(root["ActiveHtmlArtifactId"]);
            _lastRun = RunValue(root["LastRun"]);
            _messages = new HeaderReplayList<HeaderMessage>(
                (root["Messages"] as JArray ?? new JArray()).OfType<JObject>().Select(HeaderMessage.FromToken));
            _artifacts = new HeaderReplayList<HeaderArtifact>(
                (root["Artifacts"] as JArray ?? new JArray()).OfType<JObject>().Select(HeaderArtifact.FromToken));
        }

        private void ApplyOperations(IEnumerable<SessionOperation> operations)
        {
            foreach (var operation in operations ?? new List<SessionOperation>())
            {
                if (operation == null || string.IsNullOrWhiteSpace(operation.Type)) continue;
                var data = operation.Data ?? new JObject();
                switch (operation.Type)
                {
                    case SessionOperationTypes.SessionMetadataSet:
                        ApplyMetadata(data);
                        break;
                    case SessionOperationTypes.ActiveReferencesSet:
                        if (data.Property("ActiveHtmlArtifactId") != null)
                        {
                            _activeHtmlArtifactId = StringValue(data["ActiveHtmlArtifactId"]);
                        }
                        break;
                    case SessionOperationTypes.ContextSet:
                        break;
                    case SessionOperationTypes.RunStarted:
                    case SessionOperationTypes.RunUpdated:
                    case SessionOperationTypes.RunEnded:
                        _lastRun = RunValue(data["Value"]);
                        break;
                    case SessionOperationTypes.MessageUpsert:
                    case SessionOperationTypes.UserMessageAppended:
                    case SessionOperationTypes.AssistantMessageAppended:
                    case SessionOperationTypes.ToolCallRecorded:
                    case SessionOperationTypes.ToolResultRecorded:
                    case SessionOperationTypes.ToolExecutionStarted:
                    case SessionOperationTypes.ToolExecutionFinished:
                        _messages.Upsert(HeaderMessage.FromUpsert(data["Value"]));
                        break;
                    case SessionOperationTypes.MessageRemove:
                        _messages.Remove(StringValue(data["Id"]));
                        break;
                    case SessionOperationTypes.MessagesReorder:
                        _messages.Reorder(data["Ids"] as JArray);
                        break;
                    case SessionOperationTypes.ArtifactUpsert:
                    case SessionOperationTypes.ArtifactRevisionCreated:
                        _artifacts.Upsert(HeaderArtifact.FromUpsert(data["Value"]));
                        break;
                    case SessionOperationTypes.ArtifactRemove:
                        _artifacts.Remove(StringValue(data["Id"]));
                        break;
                    case SessionOperationTypes.ArtifactsReorder:
                        _artifacts.Reorder(data["Ids"] as JArray);
                        break;
                    default:
                        throw new JsonException("Unsupported session operation: " + operation.Type);
                }
            }
        }

        private void ApplyMetadata(JObject data)
        {
            foreach (var property in data.Properties())
            {
                switch (property.Name)
                {
                    case "Id": _id = StringValue(property.Value); break;
                    case "Host": _host = StringValue(property.Value); break;
                    case "DocumentKey": _documentKey = StringValue(property.Value); break;
                    case "DocumentTitle": _documentTitle = StringValue(property.Value); break;
                    case "DocumentPath": _documentPath = StringValue(property.Value); break;
                    case "Title": _title = StringValue(property.Value); break;
                    case "Model": _model = StringValue(property.Value); break;
                    case "Mode": _mode = StringValue(property.Value); break;
                    case "ReasoningEnabled": _reasoningEnabled = BooleanValue(property.Value); break;
                    case "CreatedUtc": _createdUtc = DateTimeValue(property.Value); break;
                    case "UpdatedUtc": _updatedUtc = DateTimeValue(property.Value); break;
                }
            }
        }

        private void ReadWorkspaceCounts(ChatBlobStore blobs, out int fileCount, out int dataSourceCount)
        {
            fileCount = 0;
            dataSourceCount = 0;
            if (string.IsNullOrWhiteSpace(_activeHtmlArtifactId)) return;
            var artifact = _artifacts.Items.FirstOrDefault(item => item.Active &&
                string.Equals(item.Id, _activeHtmlArtifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact == null ||
                !string.Equals(artifact.Kind, ChatArtifactKinds.HtmlWorkspace, StringComparison.OrdinalIgnoreCase)) return;
            if (artifact.MetadataCountsValid)
            {
                fileCount = artifact.FileCount;
                dataSourceCount = artifact.DataSourceCount;
                return;
            }
            if (artifact.InlineCountsValid)
            {
                fileCount = artifact.InlineFileCount;
                dataSourceCount = artifact.InlineDataSourceCount;
                return;
            }
            if (blobs == null || string.IsNullOrWhiteSpace(artifact.ContentSha256) ||
                !artifact.ContentByteLength.HasValue) return;
            var body = blobs.ReadText(new ChatBlobReference
            {
                Sha256 = artifact.ContentSha256,
                ByteLength = artifact.ContentByteLength.Value
            });
            TryReadWorkspaceBodyCounts(body, out fileCount, out dataSourceCount);
        }

        private static bool TryReadWorkspaceBodyCounts(string json, out int fileCount, out int dataSourceCount)
        {
            fileCount = 0;
            dataSourceCount = 0;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var root = JObject.Parse(json);
                var files = root.GetValue("Files", StringComparison.OrdinalIgnoreCase) as JArray;
                var dataSources = root.GetValue("DataSources", StringComparison.OrdinalIgnoreCase) as JArray;
                fileCount = files == null ? 0 : files.Count;
                dataSourceCount = dataSources == null ? 0 : dataSources.Count;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadMetadataCounts(string json, out int fileCount, out int dataSourceCount)
        {
            fileCount = 0;
            dataSourceCount = 0;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var metadata = JObject.Parse(json);
                var files = (int?)metadata["fileCount"];
                var dataSources = (int?)metadata["dataSourceCount"];
                if (!files.HasValue || !dataSources.HasValue || files.Value < 0 || dataSources.Value < 0) return false;
                fileCount = files.Value;
                dataSourceCount = dataSources.Value;
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException ||
                ex is OverflowException || ex is InvalidCastException)
            {
                return false;
            }
        }

        private static ChatRunRecord RunValue(JToken value)
        {
            return value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined
                ? null
                : value.ToObject<ChatRunRecord>();
        }

        private static ChatRunRecord CloneRun(ChatRunRecord value)
        {
            return value == null ? null : new ChatRunRecord
            {
                RunId = value.RunId,
                TurnId = value.TurnId,
                RuntimeId = value.RuntimeId,
                Status = value.Status,
                Phase = value.Phase,
                CurrentAction = value.CurrentAction,
                DocumentRuntimeKey = value.DocumentRuntimeKey,
                IterationsUsed = value.IterationsUsed,
                ToolStepsUsed = value.ToolStepsUsed,
                StartedUtc = value.StartedUtc
            };
        }

        private static string StringValue(JToken value)
        {
            return value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined
                ? null
                : (string)value;
        }

        private static bool BooleanValue(JToken value)
        {
            return value != null && value.Type != JTokenType.Null && value.Type != JTokenType.Undefined &&
                value.ToObject<bool>();
        }

        private static DateTime DateTimeValue(JToken value)
        {
            return value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined
                ? default(DateTime)
                : value.ToObject<DateTime>();
        }

        private static long StringLength(string value)
        {
            return value == null ? 0 : value.Length;
        }

        private sealed class CasUsageEntry
        {
            public long LogicalByteLength { get; set; }
            public long StoredByteLength { get; set; }
            public bool Missing { get; set; }

            public CasUsageEntry Clone()
            {
                return new CasUsageEntry
                {
                    LogicalByteLength = LogicalByteLength,
                    StoredByteLength = StoredByteLength,
                    Missing = Missing
                };
            }
        }

        private abstract class HeaderReplayItem
        {
            public string Id { get; set; }
            public bool Active { get; set; }
        }

        private sealed class HeaderMessage : HeaderReplayItem
        {
            public bool ProtocolMessage { get; set; }

            public static HeaderMessage FromToken(JObject value)
            {
                return new HeaderMessage
                {
                    Id = StringValue(value == null ? null : value["Id"]),
                    ProtocolMessage = value != null && BooleanValue(value["ProtocolMessage"]),
                    Active = true
                };
            }

            public static HeaderMessage FromUpsert(JToken value)
            {
                var objectValue = value as JObject;
                var item = FromToken(objectValue);
                if (objectValue == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    throw new JsonException("Upsert operation requires an object id.");
                }
                return item;
            }

            public HeaderMessage Clone()
            {
                return new HeaderMessage { Id = Id, ProtocolMessage = ProtocolMessage, Active = Active };
            }
        }

        private sealed class HeaderArtifact : HeaderReplayItem
        {
            public string Kind { get; set; }
            public string ContentSha256 { get; set; }
            public long? ContentByteLength { get; set; }
            public bool MetadataCountsValid { get; set; }
            public int FileCount { get; set; }
            public int DataSourceCount { get; set; }
            public bool InlineCountsValid { get; set; }
            public int InlineFileCount { get; set; }
            public int InlineDataSourceCount { get; set; }

            public static HeaderArtifact FromToken(JObject value)
            {
                int fileCount;
                int dataSourceCount;
                int inlineFileCount;
                int inlineDataSourceCount;
                var metadataValid = TryReadMetadataCounts(
                    StringValue(value == null ? null : value["MetadataJson"]), out fileCount, out dataSourceCount);
                var inlineValid = TryReadWorkspaceBodyCounts(
                    StringValue(value == null ? null : value["InlineText"]), out inlineFileCount, out inlineDataSourceCount);
                return new HeaderArtifact
                {
                    Id = StringValue(value == null ? null : value["Id"]),
                    Kind = StringValue(value == null ? null : value["Kind"]),
                    ContentSha256 = StringValue(value == null ? null : value["ContentSha256"]),
                    ContentByteLength = value == null || value["ContentByteLength"] == null ||
                        value["ContentByteLength"].Type == JTokenType.Null
                            ? (long?)null
                            : value["ContentByteLength"].ToObject<long>(),
                    MetadataCountsValid = metadataValid,
                    FileCount = fileCount,
                    DataSourceCount = dataSourceCount,
                    InlineCountsValid = inlineValid,
                    InlineFileCount = inlineFileCount,
                    InlineDataSourceCount = inlineDataSourceCount,
                    Active = true
                };
            }

            public static HeaderArtifact FromUpsert(JToken value)
            {
                var objectValue = value as JObject;
                var item = FromToken(objectValue);
                if (objectValue == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    throw new JsonException("Upsert operation requires an object id.");
                }
                return item;
            }

            public HeaderArtifact Clone()
            {
                return new HeaderArtifact
                {
                    Id = Id,
                    Kind = Kind,
                    ContentSha256 = ContentSha256,
                    ContentByteLength = ContentByteLength,
                    MetadataCountsValid = MetadataCountsValid,
                    FileCount = FileCount,
                    DataSourceCount = DataSourceCount,
                    InlineCountsValid = InlineCountsValid,
                    InlineFileCount = InlineFileCount,
                    InlineDataSourceCount = InlineDataSourceCount,
                    Active = Active
                };
            }
        }

        private sealed class HeaderReplayList<T> where T : HeaderReplayItem
        {
            private List<T> _ordered;
            private readonly Dictionary<string, T> _byId;

            public HeaderReplayList()
                : this(new T[0])
            {
            }

            public HeaderReplayList(IEnumerable<T> source)
            {
                _ordered = (source ?? new T[0]).ToList();
                _byId = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _ordered.Where(value => value != null && value.Active &&
                    !string.IsNullOrWhiteSpace(value.Id)))
                {
                    if (!_byId.ContainsKey(item.Id)) _byId.Add(item.Id, item);
                }
            }

            public IEnumerable<T> Items
            {
                get { return _ordered; }
            }

            public void Upsert(T item)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    throw new JsonException("Upsert operation requires an object id.");
                }
                T existing;
                if (_byId.TryGetValue(item.Id, out existing))
                {
                    var index = _ordered.IndexOf(existing);
                    item.Active = true;
                    _ordered[index] = item;
                    _byId[item.Id] = item;
                    return;
                }
                item.Active = true;
                _ordered.Add(item);
                _byId[item.Id] = item;
            }

            public void Remove(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) return;
                T existing;
                if (!_byId.TryGetValue(id, out existing)) return;
                existing.Active = false;
                _byId.Remove(id);
                var duplicate = _ordered.FirstOrDefault(item => item.Active &&
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null) _byId[id] = duplicate;
            }

            public void Reorder(JArray ids)
            {
                var remaining = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _ordered.Where(value => value.Active && !string.IsNullOrWhiteSpace(value.Id)))
                {
                    if (remaining.ContainsKey(item.Id)) throw new JsonException("Projection list contains duplicate ids.");
                    remaining.Add(item.Id, item);
                }
                var reordered = new List<T>();
                foreach (var id in (ids ?? new JArray()).Values<string>())
                {
                    T item;
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

            public HeaderReplayList<T> Clone(Func<T, T> clone)
            {
                return new HeaderReplayList<T>(_ordered.Select(clone));
            }
        }
    }
}
