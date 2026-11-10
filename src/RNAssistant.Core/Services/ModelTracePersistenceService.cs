using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;

namespace RNAssistant.Core.Services
{
    public sealed class ModelTracePersistenceService
    {
        private readonly IEventStore _eventStore;
        private readonly ModelDiagnosticBuffer _diagnostics;
        private readonly Action<string> _diagnosticLog;

        public ModelTracePersistenceService(IEventStore eventStore, Action<string> diagnosticLog = null)
            : this(eventStore, ModelDiagnosticBuffer.Shared, diagnosticLog)
        {
        }

        internal ModelTracePersistenceService(IEventStore eventStore, ModelDiagnosticBuffer diagnostics,
            Action<string> diagnosticLog = null)
        {
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _diagnosticLog = diagnosticLog;
        }

        public void Configure(LlmRequestOptions options, bool captureRaw = false)
        {
            if (options == null || options.TraceSession == null || options.TraceSinkConfigured) return;
            var session = options.TraceSession;
            var previousSink = options.TraceSink;
            var run = session.LastRun;
            var runId = run == null ? null : run.RunId;
            var turnId = run == null || string.IsNullOrWhiteSpace(run.TurnId) ? runId : run.TurnId;
            var documentRuntimeId = run == null ? null : run.DocumentRuntimeKey;
            options.TraceSink = record =>
            {
                if (record == null) return;
                if (string.Equals(record.Type, "request", StringComparison.OrdinalIgnoreCase))
                {
                    options.TraceRequestId = record.RequestId;
                }
                Persist(session, options, record, runId, turnId, documentRuntimeId, captureRaw);
                if (previousSink != null)
                    try { previousSink(record); }
                    catch (Exception) { ReportDiagnosticFailure("Optional model observer failed."); }
            };
            options.TraceSinkConfigured = true;
        }

        private void Persist(ChatSession session, LlmRequestOptions options, LlmTraceRecord source,
            string runId, string turnId, string documentRuntimeId, bool captureRaw)
        {
            // UI streaming already owns transient text. SSE frames have no durable
            // consumer and never enter the semantic event stream or a write queue.
            if (string.Equals(source.Type, "chunk", StringComparison.OrdinalIgnoreCase) || source.Type == "stream-body") return;
            var descriptor = Descriptor(source.Type);
            var record = source.CopyMetadata();
            record.Stage = Stage(descriptor.Kind);
            record.SessionId = session.Id; record.RunId = runId; record.TurnId = turnId;
            record.StepId = options.TraceStepId ?? record.RequestId;
            record.ModelAttemptId = options.TraceModelAttemptId ?? record.RequestId;
            record.ContextSnapshotId = options.TraceContextReceipt?.SnapshotId;
            record.DocumentRuntimeId = documentRuntimeId;
            record.Error = Clip(record.Error, 2048);
            record.UsageJson = null; // parsed numeric usage is retained below
            record.DiagnosticCapture = ModelDiagnosticCaptureStatus.Disabled;
            record.PayloadRepresentation = record.Type == "request" ? ModelPayloadRepresentation.RequestJson :
                options.TraceSession != null && record.Type == "response" && record.Streaming == true
                    ? ModelPayloadRepresentation.NormalizedStreamJson : ModelPayloadRepresentation.ResponseJson;
            var request = descriptor.Kind == SessionEventKind.ModelRequestPrepared;
            var bytes = source.PayloadUtf8Bytes;
            if (bytes == null && (request || captureRaw) && source.PayloadJson != null) bytes = Encoding.UTF8.GetBytes(source.PayloadJson);
            if (bytes != null)
            {
                record.WireBytes = bytes.LongLength;
                using (var hash = SHA256.Create()) record.WireSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                if (captureRaw)
                {
                    try
                    {
                        record.DiagnosticCaptureId = _diagnostics.Retain(record, bytes);
                        record.DiagnosticCapture = record.DiagnosticCaptureId == null ? ModelDiagnosticCaptureStatus.SizeLimit : ModelDiagnosticCaptureStatus.Retained;
                        record.DiagnosticDropped = _diagnostics.Dropped; record.DiagnosticEvicted = _diagnostics.Evicted;
                    }
                    catch (Exception) { record.DiagnosticCapture = ModelDiagnosticCaptureStatus.CaptureFailed; ReportDiagnosticFailure("Optional model capture failed."); }
                }
            }
            if (request)
            {
                var receipt = options.TraceContextReceipt;
                record.Manifest = new ModelRequestManifest { ToolGeneration = receipt?.ToolGeneration, SkillGeneration = receipt?.SkillGeneration,
                    CompilerBuild = typeof(ModelContextCompiler).Module.ModuleVersionId.ToString("D"),
                    InstructionSha256 = source.Manifest?.InstructionSha256 ?? new Dictionary<int, string>(),
                    ContextPreparation = options.ContextPreparation,
                    OmittedContext = new Dictionary<string, int>(receipt?.OmittedContext ?? new Dictionary<string, int>(), StringComparer.Ordinal),
                    SchemaGeneration = receipt?.SchemaGeneration, ConversationHighWaterMark = receipt?.ConversationHighWaterMark ?? 0,
                    ResponseSchemaSha256 = options.ResponseSchemaJson == null ? null : TextPatternEngine.Sha256(options.ResponseSchemaJson) };
                var selected = new List<ContextMessagePresentation>();
                var retainedExclusions = 0;
                foreach (var message in record.ContextMessages ?? new List<ContextMessagePresentation>())
                {
                    if (message == null) continue;
                    if (message.Presentation != ContextPresentationKind.Excluded || retainedExclusions++ < ContextReceipt.MaximumExcludedDetails) { selected.Add(message); continue; }
                    var reason = (message.ExclusionKind ?? ContextExclusionKind.Unknown).ToString();
                    int count; record.Manifest.OmittedContext.TryGetValue(reason, out count); record.Manifest.OmittedContext[reason] = count + 1;
                }
                record.ContextMessages = selected;
            }
            var write = new SessionEventWrite(descriptor, record, null, new SessionEventCorrelation(runId, turnId, record.RequestId));
            if (request) _eventStore.Append(session, write); // mandatory manifest precedes HTTP
            else
            {
                try { _eventStore.Append(session, write); }
                catch (Exception) { ReportDiagnosticFailure("Optional model outcome metadata unavailable."); }
            }
        }

        private void ReportDiagnosticFailure(string message)
        { try { _diagnosticLog?.Invoke(message); } catch (Exception) { } }
        private static string Clip(string value, int limit)
        { return value != null && value.Length > limit ? value.Substring(0, limit) : value; }

        private static SessionEventDescriptor Descriptor(string type)
        {
            if (string.Equals(type, "request", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelRequestPrepared);
            if (string.Equals(type, "response", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelResponseReceived);
            if (string.Equals(type, "chunk", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelStreamChunk);
            if (string.Equals(type, "rejected", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelAttemptRejected);
            if (string.Equals(type, "accepted", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelResponseAccepted);
            if (string.Equals(type, "failure", StringComparison.OrdinalIgnoreCase))
                return SessionEventDescriptors.For(SessionEventKind.ModelFailure);
            throw new InvalidOperationException("Unsupported model trace type: " + (type ?? "<null>") + ".");
        }

        private static string Stage(SessionEventKind kind)
        {
            if (kind == SessionEventKind.ModelRequestPrepared) return "model.request.prepared";
            if (kind == SessionEventKind.ModelAttemptRejected) return "model.attempt.rejected";
            if (kind == SessionEventKind.ModelResponseAccepted) return SessionEventTypes.ModelResponseAccepted;
            return null;
        }
    }
}
