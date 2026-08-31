using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Office.Qualification
{
    public enum QualificationRunEventKind
    {
        RunStarted = 1,
        StepStarted = 2,
        StepCompleted = 3,
        RunCompleted = 4
    }

    public sealed class QualificationRunEventData
    {
        public const int CurrentContractVersion = 2;

        public QualificationRunEventData()
        {
            ContractVersion = CurrentContractVersion;
        }

        [JsonProperty("contractVersion")] public int ContractVersion { get; set; }
        [JsonProperty("eventKind")] public string EventKind { get; set; }
        [JsonProperty("runId")] public string RunId { get; set; }
        [JsonProperty("previousRunId")] public string PreviousRunId { get; set; }
        [JsonProperty("packId")] public string PackId { get; set; }
        [JsonProperty("packRevision")] public string PackRevision { get; set; }
        [JsonProperty("packSha256")] public string PackSha256 { get; set; }
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("productVersion")] public string ProductVersion { get; set; }
        [JsonProperty("buildCommit")] public string BuildCommit { get; set; }
        [JsonProperty("channel")] public string Channel { get; set; }
        [JsonProperty("buildEvidenceSha256")] public string BuildEvidenceSha256 { get; set; }
        [JsonProperty("capabilities")] public List<string> Capabilities { get; set; }
        [JsonProperty("runStatus")] public string RunStatus { get; set; }
        [JsonProperty("pendingTerminalStatus")] public string PendingTerminalStatus { get; set; }
        [JsonProperty("stepIndex")] public int? StepIndex { get; set; }
        [JsonProperty("stepId")] public string StepId { get; set; }
        [JsonProperty("stepKind")] public string StepKind { get; set; }
        [JsonProperty("stepOutcome")] public string StepOutcome { get; set; }
        [JsonProperty("attemptId")] public string AttemptId { get; set; }
        [JsonProperty("evidenceStrength")] public string EvidenceStrength { get; set; }
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("expectedJson")] public string ExpectedJson { get; set; }
        [JsonProperty("actualJson")] public string ActualJson { get; set; }
        [JsonProperty("domainEffect")] public string DomainEffect { get; set; }
        [JsonProperty("evidenceStorage")] public string EvidenceStorage { get; set; }
        [JsonProperty("evidencePayloadSha256")] public string EvidencePayloadSha256 { get; set; }
        [JsonProperty("recordedUtc")] public DateTime RecordedUtc { get; set; }
    }

    public sealed class QualificationEventReceipt
    {
        public QualificationEventReceipt(string eventId, long sequence)
        {
            EventId = eventId;
            Sequence = sequence;
        }

        public string EventId { get; private set; }
        public long Sequence { get; private set; }
    }

    public sealed class QualificationJournalRecord
    {
        public QualificationJournalRecord(QualificationRunEventKind kind, QualificationRunEventData data,
            QualificationEventReceipt receipt)
        {
            Kind = kind;
            Data = data;
            Receipt = receipt;
        }

        public QualificationRunEventKind Kind { get; private set; }
        public QualificationRunEventData Data { get; private set; }
        public QualificationEventReceipt Receipt { get; private set; }
    }

    public interface IQualificationRunJournal
    {
        QualificationEventReceipt Append(QualificationRunEventKind kind, QualificationRunEventData data);
        IReadOnlyList<QualificationJournalRecord> Read(string runId);
    }

    public sealed class QualificationEventJournal : IQualificationRunJournal
    {
        private const int InlineEvidenceLimit = 32768;
        private const string EvidenceContentType = "application/vnd.rnassistant.qualification-evidence+json";
        private static readonly string[] EventFields =
        {
            "contractVersion", "eventKind", "runId", "previousRunId", "packId", "packRevision",
            "packSha256", "host", "productVersion", "buildCommit", "channel", "buildEvidenceSha256", "runStatus",
            "pendingTerminalStatus",
            "capabilities",
            "stepIndex", "stepId", "stepKind", "stepOutcome", "attemptId", "evidenceStrength",
            "code", "message", "expectedJson", "actualJson", "domainEffect", "evidenceStorage",
            "evidencePayloadSha256", "recordedUtc"
        };

        private readonly IEventStore _events;
        private readonly ChatSession _session;

        public QualificationEventJournal(IEventStore events, ChatSession session)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public QualificationEventReceipt Append(QualificationRunEventKind kind, QualificationRunEventData data)
        {
            Validate(kind, data);
            var persisted = Clone(data);
            SessionEventPayload payload = null;
            var evidenceLength = (persisted.ExpectedJson == null ? 0 : persisted.ExpectedJson.Length) +
                (persisted.ActualJson == null ? 0 : persisted.ActualJson.Length);
            if (evidenceLength > InlineEvidenceLimit)
            {
                var payloadJson = EvidenceJson(persisted.ExpectedJson, persisted.ActualJson);
                persisted.EvidenceStorage = "payload";
                persisted.EvidencePayloadSha256 = QualificationJson.Sha256(payloadJson);
                persisted.ExpectedJson = null;
                persisted.ActualJson = null;
                payload = SessionEventPayload.FromText(payloadJson, EvidenceContentType);
            }
            else
            {
                persisted.EvidenceStorage = "inline";
                persisted.EvidencePayloadSha256 = null;
            }
            var sessionEvent = _events.Append(_session, new SessionEventWrite(
                Descriptor(kind), persisted, payload,
                new SessionEventCorrelation(data.RunId, data.RunId, data.StepId)));
            return new QualificationEventReceipt(sessionEvent.EventId, sessionEvent.Sequence);
        }

        public IReadOnlyList<QualificationJournalRecord> Read(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run id is required.", nameof(runId));
            var result = new List<QualificationJournalRecord>();
            foreach (var item in _events.Read(_session, SessionEventReadMode.RequireComplete)
                .Where(item => item != null && string.Equals(item.RunId, runId, StringComparison.Ordinal))
                .OrderBy(item => item.Sequence))
            {
                QualificationRunEventKind kind;
                if (!TryKind(item.Type, out kind)) continue;
                var root = item.Data as JObject;
                if (root == null)
                    throw new InvalidOperationException("A durable qualification event has no object data.");
                QualificationJson.EnsureOnly(root, EventFields, "Qualification event");
                QualificationRunEventData data;
                try { data = root.ToObject<QualificationRunEventData>(); }
                catch (JsonException ex) { throw new InvalidOperationException("A durable qualification event is malformed.", ex); }
                if (!string.Equals(item.RunId, data.RunId, StringComparison.Ordinal) ||
                    !string.Equals(item.TurnId, data.RunId, StringComparison.Ordinal) ||
                    !string.Equals(item.StepId, data.StepId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Qualification event correlation does not match its data.");
                if (string.Equals(data.EvidenceStorage, "payload", StringComparison.Ordinal))
                {
                    if (data.ExpectedJson != null || data.ActualJson != null || item.Payload == null ||
                        !string.Equals(item.Payload.ContentType, EvidenceContentType, StringComparison.Ordinal))
                        throw new InvalidOperationException("Qualification evidence payload metadata is invalid.");
                    var payloadJson = _events.ReadPayload(_session, item);
                    if (string.IsNullOrWhiteSpace(payloadJson) ||
                        !string.Equals(QualificationJson.Sha256(payloadJson), data.EvidencePayloadSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("Qualification evidence payload is missing or has the wrong hash.");
                    ReadEvidence(payloadJson, data);
                }
                else if (!string.Equals(data.EvidenceStorage, "inline", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Qualification evidence storage is invalid.");
                }
                else if (data.EvidencePayloadSha256 != null || item.Payload != null)
                {
                    throw new InvalidOperationException("Inline qualification evidence cannot reference a payload.");
                }
                Validate(kind, data);
                result.Add(new QualificationJournalRecord(kind, data,
                    new QualificationEventReceipt(item.EventId, item.Sequence)));
            }
            return Array.AsReadOnly(result.ToArray());
        }

        public string FindLatestRunId()
        {
            var item = _events.Read(_session, SessionEventReadMode.RequireComplete)
                .Where(value => value != null &&
                    string.Equals(value.Type, SessionEventTypes.QualificationRunStarted, StringComparison.Ordinal))
                .OrderByDescending(value => value.Sequence)
                .FirstOrDefault();
            if (item == null) return null;
            var root = item.Data as JObject;
            if (root == null)
                throw new InvalidOperationException("A durable qualification start event has no object data.");
            QualificationJson.EnsureOnly(root, EventFields, "Qualification event");
            QualificationRunEventData data;
            try { data = root.ToObject<QualificationRunEventData>(); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("A durable qualification start event is malformed.", ex);
            }
            if (!string.Equals(item.RunId, data.RunId, StringComparison.Ordinal) ||
                !string.Equals(item.TurnId, data.RunId, StringComparison.Ordinal) ||
                item.StepId != null || data.StepId != null)
                throw new InvalidOperationException("Qualification start event correlation does not match its data.");
            Validate(QualificationRunEventKind.RunStarted, data);
            return data.RunId;
        }

        private static void Validate(QualificationRunEventKind kind, QualificationRunEventData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.ContractVersion != QualificationRunEventData.CurrentContractVersion)
                throw new InvalidOperationException("Unsupported qualification event contract version.");
            if (!string.Equals(data.EventKind, EventKindName(kind), StringComparison.Ordinal))
                throw new InvalidOperationException("Qualification event kind does not match its descriptor.");
            Required(data.RunId, 96, "runId");
            Required(data.PackId, 96, "packId");
            Required(data.PackRevision, 32, "packRevision");
            Required(data.PackSha256, 64, "packSha256");
            if (!Regex.IsMatch(data.PackSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Qualification event packSha256 is invalid.");
            Required(data.Host, 32, "host");
            Required(data.ProductVersion, 64, "productVersion");
            Required(data.BuildCommit, 128, "buildCommit");
            Required(data.Channel, 32, "channel");
            Required(data.BuildEvidenceSha256, 64, "buildEvidenceSha256");
            if (data.BuildEvidenceSha256 != "unavailable" &&
                !Regex.IsMatch(data.BuildEvidenceSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Qualification buildEvidenceSha256 is invalid.");
            if (data.Capabilities == null || data.Capabilities.Count > 256 ||
                data.Capabilities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 96) ||
                data.Capabilities.Any(value => !Regex.IsMatch(value,
                    "^[A-Za-z][A-Za-z0-9]*(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)) ||
                data.Capabilities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Capabilities.Count ||
                !data.Capabilities.SequenceEqual(data.Capabilities.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Qualification event capabilities are invalid.");
            QualificationManifestParser.ParseRunStatus(data.RunStatus);
            if (data.PendingTerminalStatus != null)
            {
                var pendingStatus = QualificationManifestParser.ParseRunStatus(data.PendingTerminalStatus);
                if (pendingStatus != QualificationRunStatus.Failed && pendingStatus != QualificationRunStatus.Blocked &&
                    pendingStatus != QualificationRunStatus.Cancelled)
                    throw new InvalidOperationException("Qualification pending terminal status is invalid.");
            }
            if (kind == QualificationRunEventKind.RunStarted && data.RunStatus != "running")
                throw new InvalidOperationException("Qualification start event must be running.");
            if (kind == QualificationRunEventKind.RunStarted && data.PendingTerminalStatus != null)
                throw new InvalidOperationException("Qualification start event cannot have a pending terminal status.");
            if (kind == QualificationRunEventKind.RunCompleted && data.RunStatus != "passed" &&
                data.RunStatus != "failed" && data.RunStatus != "blocked" && data.RunStatus != "cancelled")
                throw new InvalidOperationException("Qualification terminal event must have a terminal status.");
            if (kind == QualificationRunEventKind.RunCompleted &&
                ((data.RunStatus == "passed" && data.PendingTerminalStatus != null) ||
                 (data.RunStatus != "passed" && data.PendingTerminalStatus != data.RunStatus)))
                throw new InvalidOperationException("Qualification terminal event conflicts with its pending status.");
            if (data.RecordedUtc == default(DateTime))
                throw new InvalidOperationException("Qualification event recordedUtc is required.");
            if (data.PreviousRunId != null) Required(data.PreviousRunId, 96, "previousRunId");
            if (data.Code != null && data.Code.Length > 128)
                throw new InvalidOperationException("Qualification event code is overlong.");
            if (data.Message != null && data.Message.Length > 2000)
                throw new InvalidOperationException("Qualification event message is overlong.");
            var stepEvent = kind == QualificationRunEventKind.StepStarted || kind == QualificationRunEventKind.StepCompleted;
            if (stepEvent)
            {
                if (!data.StepIndex.HasValue || data.StepIndex < 0 || data.StepIndex >= 100)
                    throw new InvalidOperationException("Qualification step index is invalid.");
                Required(data.StepId, 64, "stepId");
                Required(data.StepKind, 32, "stepKind");
                Required(data.StepOutcome, 32, "stepOutcome");
                Required(data.AttemptId, 96, "attemptId");
                var outcome = QualificationManifestParser.ParseOutcome(data.StepOutcome);
                if (kind == QualificationRunEventKind.StepStarted && outcome != QualificationStepOutcome.Running &&
                    outcome != QualificationStepOutcome.AwaitingUser)
                    throw new InvalidOperationException("Qualification step-start outcome is invalid.");
                if (kind == QualificationRunEventKind.StepCompleted &&
                    (outcome == QualificationStepOutcome.NotRun || outcome == QualificationStepOutcome.Running ||
                     outcome == QualificationStepOutcome.AwaitingUser))
                    throw new InvalidOperationException("Qualification step-completion outcome is invalid.");
            }
            else if (data.StepIndex.HasValue || data.StepId != null || data.StepKind != null ||
                data.StepOutcome != null || data.AttemptId != null)
            {
                throw new InvalidOperationException("Run-level qualification events cannot contain step identity.");
            }
            if (kind == QualificationRunEventKind.StepCompleted)
            {
                if (data.EvidenceStrength != "automatic" && data.EvidenceStrength != "manual" &&
                    data.EvidenceStrength != "none")
                    throw new InvalidOperationException("Qualification evidence strength is invalid.");
                QualificationJson.EnsureJsonValue(data.ExpectedJson, "expectedJson");
                QualificationJson.EnsureJsonValue(data.ActualJson, "actualJson");
                if (data.EvidenceStrength == "automatic" && data.StepKind != "assertion")
                    throw new InvalidOperationException("Automatic pass evidence is owned by assertion steps.");
                if (data.EvidenceStrength == "manual" && data.StepKind != "userAction")
                    throw new InvalidOperationException("Manual evidence is owned by user-action steps.");
                if (data.StepKind == "assertion" && data.StepOutcome == "passed" &&
                    (data.EvidenceStrength != "automatic" || data.ExpectedJson == null || data.ActualJson == null))
                    throw new InvalidOperationException("Passing assertion requires automatic expected and actual evidence.");
                if (data.DomainEffect != null && data.DomainEffect != "verified_change" &&
                    data.DomainEffect != "verified_no_change" && data.DomainEffect != "error" &&
                    data.DomainEffect != "unknown")
                    throw new InvalidOperationException("Qualification domain effect is invalid.");
            }
            else if (data.EvidenceStrength != null || data.ExpectedJson != null || data.ActualJson != null ||
                data.DomainEffect != null)
            {
                throw new InvalidOperationException("Only completed steps may contain evidence.");
            }
        }

        private static void Required(string value, int maximum, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
                throw new InvalidOperationException("Qualification event " + field + " is missing or overlong.");
        }

        private static SessionEventDescriptor Descriptor(QualificationRunEventKind kind)
        {
            switch (kind)
            {
                case QualificationRunEventKind.RunStarted:
                    return SessionEventDescriptors.For(SessionEventKind.QualificationRunStarted);
                case QualificationRunEventKind.StepStarted:
                    return SessionEventDescriptors.For(SessionEventKind.QualificationStepStarted);
                case QualificationRunEventKind.StepCompleted:
                    return SessionEventDescriptors.For(SessionEventKind.QualificationStepCompleted);
                case QualificationRunEventKind.RunCompleted:
                    return SessionEventDescriptors.For(SessionEventKind.QualificationRunCompleted);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static bool TryKind(string eventType, out QualificationRunEventKind kind)
        {
            if (eventType == SessionEventTypes.QualificationRunStarted) { kind = QualificationRunEventKind.RunStarted; return true; }
            if (eventType == SessionEventTypes.QualificationStepStarted) { kind = QualificationRunEventKind.StepStarted; return true; }
            if (eventType == SessionEventTypes.QualificationStepCompleted) { kind = QualificationRunEventKind.StepCompleted; return true; }
            if (eventType == SessionEventTypes.QualificationRunCompleted) { kind = QualificationRunEventKind.RunCompleted; return true; }
            kind = default(QualificationRunEventKind);
            return false;
        }

        internal static string EventKindName(QualificationRunEventKind kind)
        {
            switch (kind)
            {
                case QualificationRunEventKind.RunStarted: return "run_started";
                case QualificationRunEventKind.StepStarted: return "step_started";
                case QualificationRunEventKind.StepCompleted: return "step_completed";
                case QualificationRunEventKind.RunCompleted: return "run_completed";
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static QualificationRunEventData Clone(QualificationRunEventData value)
        {
            return JsonConvert.DeserializeObject<QualificationRunEventData>(JsonConvert.SerializeObject(value));
        }

        private static string EvidenceJson(string expectedJson, string actualJson)
        {
            return new JObject
            {
                ["expectedJson"] = expectedJson == null ? JValue.CreateNull() : new JValue(expectedJson),
                ["actualJson"] = actualJson == null ? JValue.CreateNull() : new JValue(actualJson)
            }.ToString(Formatting.None);
        }

        private static void ReadEvidence(string json, QualificationRunEventData data)
        {
            var root = QualificationJson.ReadObject(json, "Qualification evidence payload", 1100000);
            QualificationJson.EnsureOnly(root, new[] { "expectedJson", "actualJson" }, "Qualification evidence payload");
            data.ExpectedJson = ReadNullableString(root["expectedJson"], "expectedJson");
            data.ActualJson = ReadNullableString(root["actualJson"], "actualJson");
        }

        private static string ReadNullableString(JToken token, string field)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String)
                throw new InvalidOperationException("Qualification evidence " + field + " must be a string or null.");
            return (string)token;
        }
    }
}
