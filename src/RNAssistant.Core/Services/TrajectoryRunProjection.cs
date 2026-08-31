using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    /// <summary>
    /// Rebuilds one chronological diagnostic projection from canonical chat events.
    /// It never writes state or infers execution success from model text.
    /// </summary>
    internal static class TrajectoryRunProjection
    {
        public static List<TrajectoryViewRow> Build(IReadOnlyList<SessionEvent> events)
        {
            var source = (events ?? new List<SessionEvent>())
                .Where(item => item != null)
                .OrderBy(item => item.Sequence)
                .ToList();
            var rows = new List<TrajectoryViewRow>();
            foreach (var item in source)
            {
                if (string.Equals(item.Type, SessionEventTypes.SessionCommit, StringComparison.OrdinalIgnoreCase))
                {
                    AddCommitRows(rows, item);
                }
                else if (!string.Equals(item.Type, SessionEventTypes.SessionCreated, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Type, SessionEventTypes.SessionForked, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(EventRow(item));
                }
            }
            AddMissingEvidence(rows, source);
            return rows;
        }

        private static TrajectoryViewRow EventRow(SessionEvent item)
        {
            var data = item.Data as JObject ?? new JObject();
            var stage = Text(Property(data, "Stage")) ?? item.Type ?? "event";
            var logicalStepId = Text(Property(data, "StepId")) ?? item.StepId;
            var status = EventStatus(item.Type, stage, data);
            var rowData = BaseData(stage, item.Type, Layer(stage), "recorded");
            Copy(rowData, data, "RequestId", "requestId");
            Copy(rowData, data, "ResponseStatus", "responseStatus");
            Copy(rowData, data, "StatusCode", "statusCode");
            Copy(rowData, data, "FailureKind", "failureKind");
            Copy(rowData, data, "Error", "error");
            Copy(rowData, data, "Attempt", "attempt");
            Copy(rowData, data, "Code", "code");
            Copy(rowData, data, "Boundary", "boundary");
            Copy(rowData, data, "DocumentRuntimeId", "documentRuntimeId");
            if (!string.Equals(logicalStepId, item.StepId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.StepId))
            {
                rowData["transportStepId"] = item.StepId;
            }
            if (item.Payload != null)
            {
                rowData["payload"] = new JObject
                {
                    ["eventId"] = item.EventId,
                    ["sha256"] = item.Payload.Sha256,
                    ["byteLength"] = item.Payload.ByteLength,
                    ["contentType"] = item.Payload.ContentType,
                    ["encryption"] = item.Payload.Encryption == null
                        ? JValue.CreateNull()
                        : new JValue(item.Payload.Encryption)
                };
            }

            var row = new TrajectoryViewRow
            {
                Id = RowId(item.Sequence, "event", item.EventId),
                View = TrajectoryViews.RunCausal,
                Kind = stage,
                Title = EventTitle(stage),
                Status = status,
                CreatedUtc = item.CreatedUtc,
                FirstSequence = item.Sequence,
                LastSequence = item.Sequence,
                RunId = Text(Property(data, "RunId")) ?? item.RunId,
                TurnId = Text(Property(data, "TurnId")) ?? item.TurnId,
                StepId = logicalStepId,
                ModelAttemptId = Text(Property(data, "ModelAttemptId")),
                ToolCallId = Text(Property(data, "ToolCallId")),
                ToolId = Text(Property(data, "ToolId")),
                MutationId = Text(Property(data, "MutationId")),
                JournalRunId = Text(Property(data, "JournalRunId")),
                ResourceRefs = EventStreamTrajectoryQuery.ExtractResourceRefs(item.Data),
                FailureCount = IsFailure(status) ? 1 : 0,
                Data = rowData
            };
            ApplySource(row, new[] { item });
            return row;
        }

        private static void AddCommitRows(ICollection<TrajectoryViewRow> rows, SessionEvent item)
        {
            var operations = EventStreamTrajectoryQuery.Operations(item).ToList();
            for (var operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                var operation = operations[operationIndex];
                var operationType = Text(Property(operation, "Type"));
                if (!IsCausalOperation(operationType)) continue;
                if (IsAcceptedCallOperation(operationType))
                {
                    AddAcceptedCallRows(rows, item, operation, operationIndex);
                }
                else
                {
                    rows.Add(OperationRow(item, operation, operationIndex));
                }
            }
        }

        private static void AddAcceptedCallRows(
            ICollection<TrajectoryViewRow> rows,
            SessionEvent item,
            JObject operation,
            int operationIndex)
        {
            var operationData = Property(operation, "Data") as JObject ?? new JObject();
            var message = Property(operationData, "Value") as JObject ?? new JObject();
            var origin = Property(message, "AcceptedCallOrigin") as JObject;
            var calls = (Property(message, "ToolCalls") as JArray)?.OfType<JObject>().ToList()
                ?? new List<JObject>();
            if (calls.Count == 0)
            {
                calls.Add(new JObject
                {
                    ["Id"] = Property(message, "ToolCallId") ?? JValue.CreateNull(),
                    ["Name"] = Property(message, "ToolName") ?? JValue.CreateNull()
                });
            }

            for (var callPosition = 0; callPosition < calls.Count; callPosition++)
            {
                var call = calls[callPosition];
                var callId = Text(Property(message, "ToolCallId")) ?? Text(Property(call, "Id"));
                var toolId = Text(Property(message, "ToolName")) ?? Text(Property(call, "Name"));
                var originStepId = Text(Property(origin, "StepId"));
                var modelAttemptId = Text(Property(origin, "ModelAttemptId"));
                var originCallIndex = IntValue(Property(origin, "CallIndex"));
                var data = BaseData(SessionOperationTypes.ToolCallRecorded,
                    SessionEventTypes.SessionCommit, "tool", "recorded");
                data["operationIndex"] = operationIndex;
                data["operationPath"] = "Operations[" + operationIndex.ToString(CultureInfo.InvariantCulture) + "]";
                data["acceptedCallOrigin"] = origin == null ? JValue.CreateNull() : origin.DeepClone();
                data["sourceCallPosition"] = callPosition;
                data["originComplete"] = !string.IsNullOrWhiteSpace(originStepId) &&
                    !string.IsNullOrWhiteSpace(modelAttemptId) && originCallIndex.HasValue;
                data["argumentsAvailable"] = Property(call, "Arguments") != null ||
                    Property(call, "ArgumentsJson") != null || Property(message, "Content") != null;

                var row = new TrajectoryViewRow
                {
                    Id = RowId(item.Sequence, "operation",
                        operationIndex.ToString("D4", CultureInfo.InvariantCulture) + ":call:" +
                        callPosition.ToString("D4", CultureInfo.InvariantCulture)),
                    View = TrajectoryViews.RunCausal,
                    Kind = SessionOperationTypes.ToolCallRecorded,
                    Title = "Accepted call: " + (toolId ?? callId ?? "unknown"),
                    Status = "accepted",
                    CreatedUtc = item.CreatedUtc,
                    FirstSequence = item.Sequence,
                    LastSequence = item.Sequence,
                    RunId = Text(Property(message, "RunId")) ?? item.RunId,
                    TurnId = item.TurnId,
                    StepId = originStepId ?? item.StepId,
                    ModelAttemptId = modelAttemptId,
                    ToolCallId = callId,
                    ToolId = toolId,
                    ResourceRefs = EventStreamTrajectoryQuery.ExtractResourceRefs(operation),
                    Data = data
                };
                ApplySource(row, new[] { item });
                rows.Add(row);
            }
        }

        private static TrajectoryViewRow OperationRow(SessionEvent item, JObject operation, int operationIndex)
        {
            var operationType = Text(Property(operation, "Type")) ?? "session.operation";
            var operationData = Property(operation, "Data") as JObject ?? new JObject();
            var value = Property(operationData, "Value") as JObject ?? new JObject();
            var activity = Property(value, "Activity") as JObject ?? new JObject();
            var origin = Property(value, "AcceptedCallOrigin") as JObject;
            var incompatible = IsIncompatibleToolOperation(operationType, value);
            var status = incompatible
                ? "incompatible"
                : OperationStatus(operationType, operationData, value, activity);
            var toolCallId = Text(Property(activity, "ToolCallId")) ?? Text(Property(value, "ToolCallId"));
            var toolId = Text(Property(activity, "ToolId")) ?? Text(Property(value, "ToolName"));
            var artifactId = IsArtifactOperation(operationType) ? Text(Property(value, "Id")) : null;
            var data = BaseData(operationType, SessionEventTypes.SessionCommit, Layer(operationType), "recorded");
            data["operationIndex"] = operationIndex;
            data["operationPath"] = "Operations[" + operationIndex.ToString(CultureInfo.InvariantCulture) + "]";
            Copy(data, value, "Id", "messageOrArtifactId");
            Copy(data, value, "Role", "role");
            Copy(data, value, "ResponseStatus", "responseStatus");
            Copy(data, value, "ResponseProtocolVersion", "responseProtocolVersion");
            Copy(data, value, "ToolResultProtocolVersion", "toolResultProtocolVersion");
            Copy(data, activity, "ExecutionStatus", "executionStatus");
            Copy(data, activity, "ErrorCode", "errorCode");
            Copy(data, activity, "PendingId", "pendingId");
            Copy(data, activity, "Retryable", "retryable");
            if (origin != null) data["acceptedCallOrigin"] = origin.DeepClone();
            if (incompatible)
            {
                data["requiresReset"] = true;
                data["incompatibleReason"] = "accepted_origin_on_tool_result";
            }

            var row = new TrajectoryViewRow
            {
                Id = RowId(item.Sequence, "operation", operationIndex.ToString("D4", CultureInfo.InvariantCulture)),
                View = TrajectoryViews.RunCausal,
                Kind = operationType,
                Title = OperationTitle(operationType, toolId, artifactId),
                Status = status,
                CreatedUtc = item.CreatedUtc,
                FirstSequence = item.Sequence,
                LastSequence = item.Sequence,
                RunId = Text(Property(activity, "RunId")) ?? Text(Property(value, "RunId")) ?? item.RunId,
                TurnId = item.TurnId,
                StepId = Text(Property(activity, "StepId")) ?? Text(Property(origin, "StepId")) ?? item.StepId,
                ModelAttemptId = Text(Property(origin, "ModelAttemptId")),
                ToolCallId = toolCallId,
                ToolId = toolId,
                MutationId = FirstValue(operation, "MutationId"),
                JournalRunId = FirstValue(operation, "JournalRunId"),
                ArtifactId = artifactId,
                ParentArtifactId = Text(Property(value, "ParentArtifactId")),
                ResourceRefs = EventStreamTrajectoryQuery.ExtractResourceRefs(operation),
                FailureCount = IsFailure(status) ? 1 : 0,
                Data = data
            };
            ApplySource(row, new[] { item });
            return row;
        }

        private static void AddMissingEvidence(
            ICollection<TrajectoryViewRow> rows,
            IReadOnlyList<SessionEvent> events)
        {
            var recorded = rows.ToList();
            foreach (var request in recorded.Where(item => Same(item.Kind, "model.request.prepared") ||
                Same(item.Kind, SessionEventTypes.LlmRequest)).ToList())
            {
                var terminal = TerminalTurn(events, request);
                if (terminal == null || string.IsNullOrWhiteSpace(request.ModelAttemptId)) continue;
                var attemptRows = recorded.Where(item => Same(item.ModelAttemptId, request.ModelAttemptId)).ToList();
                var hasResponse = attemptRows.Any(item => Same(item.Kind, SessionEventTypes.LlmResponse));
                var hasFailure = attemptRows.Any(item => Same(item.Kind, SessionEventTypes.LlmFailure));
                if (!hasResponse && !hasFailure)
                {
                    rows.Add(MissingRow(request, terminal, "model.response", "Model response evidence is missing"));
                    continue;
                }
                var hasVerdict = attemptRows.Any(item => Same(item.Kind, "model.attempt.rejected") ||
                    Same(item.Kind, SessionEventTypes.AgentResponseRejected) ||
                    Same(item.Kind, "model.response.accepted") ||
                    Same(item.Kind, SessionOperationTypes.ToolCallRecorded));
                if (hasResponse && !hasVerdict)
                {
                    rows.Add(MissingRow(request, terminal, "model.verdict", "Model verdict evidence is missing"));
                }
            }

            foreach (var call in recorded.Where(item => Same(item.Kind, SessionOperationTypes.ToolCallRecorded) &&
                !string.IsNullOrWhiteSpace(item.ToolCallId)).ToList())
            {
                var terminal = TerminalTurn(events, call);
                if (terminal == null) continue;
                var toolRows = recorded.Where(item => Same(item.ToolCallId, call.ToolCallId)).ToList();
                var hasStart = toolRows.Any(item => Same(item.Kind, "tool.execution.started"));
                var hasTerminal = toolRows.Any(item => Same(item.Kind, "tool.execution.completed") ||
                    Same(item.Kind, SessionOperationTypes.ToolExecutionFinished));
                if (!hasStart && !hasTerminal)
                {
                    rows.Add(MissingRow(call, terminal, "tool.execution.start", "Tool dispatch evidence is missing"));
                }
                else if (!hasTerminal)
                {
                    rows.Add(MissingRow(call, terminal, "tool.execution.terminal", "Tool terminal evidence is missing"));
                }
            }
        }

        private static TrajectoryViewRow MissingRow(
            TrajectoryViewRow source,
            SessionEvent terminal,
            string expectedStage,
            string title)
        {
            var data = BaseData("diagnostic.evidence.missing", terminal.Type, Layer(expectedStage), "missing");
            data["expectedStage"] = expectedStage;
            data["notProofOfSuccessOrFailure"] = true;
            data["afterRowId"] = source.Id;
            var row = new TrajectoryViewRow
            {
                Id = RowId(terminal.Sequence, "gap", expectedStage + ":" +
                    (source.ToolCallId ?? source.ModelAttemptId ?? source.Id)),
                View = TrajectoryViews.RunCausal,
                Kind = "diagnostic.evidence.missing",
                Title = title,
                Status = "missing",
                CreatedUtc = terminal.CreatedUtc,
                FirstSequence = terminal.Sequence,
                LastSequence = terminal.Sequence,
                RunId = source.RunId,
                TurnId = source.TurnId,
                StepId = source.StepId,
                ModelAttemptId = source.ModelAttemptId,
                ToolCallId = source.ToolCallId,
                ToolId = source.ToolId,
                MutationId = source.MutationId,
                JournalRunId = source.JournalRunId,
                Data = data
            };
            ApplySource(row, new[] { terminal });
            AddSource(row, source);
            return row;
        }

        private static SessionEvent TerminalTurn(IEnumerable<SessionEvent> events, TrajectoryViewRow row)
        {
            return (events ?? new List<SessionEvent>())
                .Where(item => item != null && item.Sequence >= row.LastSequence &&
                    Same(item.Type, SessionEventTypes.TurnEnded) &&
                    (string.IsNullOrWhiteSpace(row.TurnId) || Same(item.TurnId, row.TurnId)) &&
                    (string.IsNullOrWhiteSpace(row.RunId) || Same(item.RunId, row.RunId) ||
                     !string.IsNullOrWhiteSpace(row.TurnId) && Same(item.TurnId, row.TurnId)) &&
                    IsFinalTurnStatus(Text(Property(item.Data, "Status"))))
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();
        }

        private static bool IsFinalTurnStatus(string status)
        {
            return !Same(status, "running") && !Same(status, "waiting") &&
                !Same(status, "waiting_confirmation") && !Same(status, "awaiting_confirmation") &&
                !Same(status, "awaiting_user");
        }

        private static bool IsCausalOperation(string type)
        {
            return Same(type, SessionOperationTypes.RunStarted) ||
                Same(type, SessionOperationTypes.RunUpdated) ||
                Same(type, SessionOperationTypes.RunEnded) ||
                Same(type, SessionOperationTypes.UserMessageAppended) ||
                Same(type, SessionOperationTypes.AssistantMessageAppended) ||
                Same(type, SessionOperationTypes.ToolCallRecorded) ||
                Same(type, SessionOperationTypes.ToolResultRecorded) ||
                Same(type, SessionOperationTypes.ToolExecutionStarted) ||
                Same(type, SessionOperationTypes.ToolExecutionFinished) ||
                IsArtifactOperation(type);
        }

        private static bool IsAcceptedCallOperation(string type)
        {
            return Same(type, SessionOperationTypes.ToolCallRecorded);
        }

        private static bool IsIncompatibleToolOperation(string type, JObject value)
        {
            return Same(type, SessionOperationTypes.ToolResultRecorded) &&
                Property(value, "AcceptedCallOrigin") is JObject;
        }

        private static bool IsArtifactOperation(string type)
        {
            return Same(type, SessionOperationTypes.ArtifactRevisionCreated) ||
                Same(type, SessionOperationTypes.ArtifactRemove);
        }

        private static string EventStatus(string eventType, string stage, JObject data)
        {
            var exact = Text(Property(data, "Status")) ?? Text(Property(data, "ResponseStatus"));
            if (Same(eventType, SessionEventTypes.TurnStarted) || Same(eventType, SessionEventTypes.StepStarted) ||
                Same(stage, "run.started")) return exact ?? "running";
            if (Same(eventType, SessionEventTypes.LlmRequest) || Same(stage, "model.request.prepared")) return "prepared";
            if (Same(eventType, SessionEventTypes.AssistantChunk)) return "streaming";
            if (Same(eventType, SessionEventTypes.LlmResponse)) return "received";
            if (Same(eventType, SessionEventTypes.LlmFailure)) return "failed";
            if (Same(eventType, SessionEventTypes.AgentResponseRejected) || Same(stage, "model.attempt.rejected")) return "rejected";
            if (Same(stage, "model.response.accepted")) return exact ?? "accepted";
            if (Same(stage, "ui.projected")) return "projected";
            if (Same(stage, "domain.effect.prepared")) return exact ?? "prepared";
            if (Same(stage, "domain.effect.dispatched")) return exact ?? "dispatched";
            return exact ?? "recorded";
        }

        private static string OperationStatus(string type, JObject data, JObject value, JObject activity)
        {
            if (Same(type, SessionOperationTypes.ToolCallRecorded)) return "accepted";
            if (Same(type, SessionOperationTypes.ToolExecutionStarted)) return "running";
            if (Same(type, SessionOperationTypes.ToolExecutionFinished))
            {
                var execution = Text(Property(activity, "ExecutionStatus"));
                if (Same(execution, "waiting_confirmation")) return "waiting_confirmation";
                return Text(Property(activity, "Status")) ?? execution ?? "recorded";
            }
            if (Same(type, SessionOperationTypes.UserMessageAppended)) return "persisted";
            if (Same(type, SessionOperationTypes.AssistantMessageAppended))
                return Text(Property(value, "ResponseStatus")) ?? "persisted";
            if (Same(type, SessionOperationTypes.ToolResultRecorded)) return "recorded";
            if (Same(type, SessionOperationTypes.ArtifactRevisionCreated)) return "persisted";
            if (Same(type, SessionOperationTypes.ArtifactRemove)) return "removed";
            return Text(Property(value, "Status")) ?? Text(Property(data, "Status")) ?? "recorded";
        }

        private static string EventTitle(string stage)
        {
            if (Same(stage, "run.started")) return "Run started";
            if (Same(stage, SessionEventTypes.TurnStarted)) return "Turn started";
            if (Same(stage, SessionEventTypes.TurnEnded)) return "Turn ended";
            if (Same(stage, SessionEventTypes.StepStarted)) return "Model step started";
            if (Same(stage, SessionEventTypes.StepEnded)) return "Model step ended";
            if (Same(stage, "model.request.prepared") || Same(stage, SessionEventTypes.LlmRequest)) return "Model request prepared";
            if (Same(stage, SessionEventTypes.LlmResponse)) return "Raw model response received";
            if (Same(stage, SessionEventTypes.AssistantChunk)) return "Model stream chunk batch";
            if (Same(stage, SessionEventTypes.LlmFailure)) return "Model request failed";
            if (Same(stage, "model.attempt.rejected") || Same(stage, SessionEventTypes.AgentResponseRejected)) return "Model response rejected";
            if (Same(stage, "model.response.accepted")) return "Model response accepted";
            if (Same(stage, "tool.execution.started")) return "Tool execution entered";
            if (Same(stage, "tool.execution.completed")) return "Tool execution returned";
            if (Same(stage, "domain.effect.prepared")) return "Effect prepared";
            if (Same(stage, "domain.effect.dispatched")) return "Effect dispatched";
            if (Same(stage, "domain.effect.verified")) return "Effect verified";
            if (Same(stage, "run.summary.created")) return "Run summary created";
            if (Same(stage, "ui.projected")) return "UI response projected";
            return stage;
        }

        private static string OperationTitle(string type, string toolId, string artifactId)
        {
            if (Same(type, SessionOperationTypes.RunStarted)) return "Run state started";
            if (Same(type, SessionOperationTypes.RunUpdated)) return "Run state updated";
            if (Same(type, SessionOperationTypes.RunEnded)) return "Run state ended";
            if (Same(type, SessionOperationTypes.UserMessageAppended)) return "User request persisted";
            if (Same(type, SessionOperationTypes.AssistantMessageAppended)) return "Assistant response persisted";
            if (Same(type, SessionOperationTypes.ToolResultRecorded)) return "Tool result recorded: " + (toolId ?? "unknown");
            if (Same(type, SessionOperationTypes.ToolExecutionStarted)) return "Tool activity started: " + (toolId ?? "unknown");
            if (Same(type, SessionOperationTypes.ToolExecutionFinished)) return "Tool activity finished: " + (toolId ?? "unknown");
            if (Same(type, SessionOperationTypes.ArtifactRevisionCreated)) return "Artifact revision persisted: " + (artifactId ?? "unknown");
            if (Same(type, SessionOperationTypes.ArtifactRemove)) return "Artifact removed: " + (artifactId ?? "unknown");
            return type;
        }

        private static string Layer(string stage)
        {
            stage = stage ?? string.Empty;
            if (stage.StartsWith("model.", StringComparison.OrdinalIgnoreCase) ||
                stage.StartsWith("llm.", StringComparison.OrdinalIgnoreCase) ||
                stage.StartsWith("agent.", StringComparison.OrdinalIgnoreCase) ||
                stage.StartsWith("step.", StringComparison.OrdinalIgnoreCase)) return "model";
            if (stage.StartsWith("tool.", StringComparison.OrdinalIgnoreCase)) return "tool";
            if (stage.StartsWith("domain.effect.", StringComparison.OrdinalIgnoreCase)) return "effect";
            if (stage.StartsWith("artifact.", StringComparison.OrdinalIgnoreCase)) return "artifact";
            if (stage.StartsWith("ui.", StringComparison.OrdinalIgnoreCase)) return "ui";
            if (stage.StartsWith("user.", StringComparison.OrdinalIgnoreCase)) return "user";
            if (stage.StartsWith("assistant.", StringComparison.OrdinalIgnoreCase)) return "model";
            return "run";
        }

        private static JObject BaseData(string stage, string eventType, string layer, string evidence)
        {
            return new JObject
            {
                ["stage"] = stage,
                ["eventType"] = eventType,
                ["layer"] = layer,
                ["evidence"] = evidence
            };
        }

        private static void Copy(JObject target, JObject source, string sourceName, string targetName)
        {
            var value = Property(source, sourceName);
            if (value != null && value.Type != JTokenType.Null) target[targetName] = value.DeepClone();
        }

        private static void ApplySource(TrajectoryViewRow row, IEnumerable<SessionEvent> events)
        {
            var source = (events ?? new SessionEvent[0]).Where(item => item != null)
                .OrderBy(item => item.Sequence).ToList();
            row.SourceEventSeqs = source.Select(item => item.Sequence).Distinct().ToList();
            row.SourceEventIds = source.Select(item => item.EventId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddSource(TrajectoryViewRow target, TrajectoryViewRow source)
        {
            foreach (var sequence in source.SourceEventSeqs ?? new List<long>())
                if (!target.SourceEventSeqs.Contains(sequence)) target.SourceEventSeqs.Add(sequence);
            foreach (var id in source.SourceEventIds ?? new List<string>())
                if (!target.SourceEventIds.Any(value => Same(value, id))) target.SourceEventIds.Add(id);
            target.SourceEventSeqs = target.SourceEventSeqs.OrderBy(value => value).ToList();
        }

        private static string FirstValue(JToken token, params string[] names)
        {
            return EventStreamTrajectoryQuery.ExtractValues(token, names).FirstOrDefault();
        }

        private static JToken Property(JToken token, string name)
        {
            return EventStreamTrajectoryQuery.Property(token, name);
        }

        private static string Text(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            var text = Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static int? IntValue(JToken value)
        {
            int result;
            return value != null && int.TryParse(Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? (int?)result : null;
        }

        private static string RowId(long sequence, string kind, string suffix)
        {
            return "run:" + sequence.ToString("D20", CultureInfo.InvariantCulture) + ":" + kind + ":" +
                (string.IsNullOrWhiteSpace(suffix) ? "unknown" : suffix);
        }

        private static bool IsFailure(string status)
        {
            return Same(status, "failed") || Same(status, "error") || Same(status, "unknown") ||
                Same(status, "rejected") || Same(status, "cancelled") || Same(status, "missing");
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
