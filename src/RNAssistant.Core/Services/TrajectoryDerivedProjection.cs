using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Services
{
    internal static class TrajectoryDerivedProjection
    {
        public static List<TrajectoryViewRow> Build(IReadOnlyList<SessionEvent> events, string view)
        {
            var source = (events ?? new List<SessionEvent>()).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            var models = BuildModelRows(source);
            var tools = BuildToolRows(source);
            if (string.Equals(view, TrajectoryViews.ModelReplay, StringComparison.OrdinalIgnoreCase)) return models;
            if (string.Equals(view, TrajectoryViews.ToolExecution, StringComparison.OrdinalIgnoreCase)) return tools;
            if (string.Equals(view, TrajectoryViews.ArtifactLineage, StringComparison.OrdinalIgnoreCase)) return BuildArtifactRows(source);
            if (string.Equals(view, TrajectoryViews.ConfirmationPauses, StringComparison.OrdinalIgnoreCase)) return BuildConfirmationRows(tools);
            var turns = BuildTurnRows(source, models, tools);
            if (string.Equals(view, TrajectoryViews.TurnUsage, StringComparison.OrdinalIgnoreCase)) return turns;
            if (string.Equals(view, TrajectoryViews.FailureRetries, StringComparison.OrdinalIgnoreCase)) return BuildFailureRows(models, tools, turns);
            return new List<TrajectoryViewRow>();
        }

        private static List<TrajectoryViewRow> BuildModelRows(IReadOnlyList<SessionEvent> events)
        {
            var groups = new Dictionary<string, List<SessionEvent>>(StringComparer.OrdinalIgnoreCase);
            var lastStepByTurn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in events.Where(IsModelEvent))
            {
                var stepId = Text(item.StepId) ?? Text(Property(item.Data, "RequestId"));
                var turnKey = CorrelationKey(item.RunId, item.TurnId);
                if (string.IsNullOrWhiteSpace(stepId) &&
                    string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase))
                {
                    lastStepByTurn.TryGetValue(turnKey, out stepId);
                }
                var key = string.IsNullOrWhiteSpace(stepId) ? "event:" + item.EventId : "step:" + stepId;
                List<SessionEvent> group;
                if (!groups.TryGetValue(key, out group)) groups[key] = group = new List<SessionEvent>();
                group.Add(item);
                if (!string.IsNullOrWhiteSpace(stepId)) lastStepByTurn[turnKey] = stepId;
            }

            return groups.Select(pair => ModelRow(pair.Key, pair.Value)).ToList();
        }

        private static TrajectoryViewRow ModelRow(string key, IList<SessionEvent> events)
        {
            var ordered = events.OrderBy(item => item.Sequence).ToList();
            var last = ordered[ordered.Count - 1];
            var stepId = ordered.Select(item => Text(item.StepId) ?? Text(Property(item.Data, "RequestId"))).LastOrDefault(value => value != null);
            var status = "running";
            foreach (var item in ordered)
            {
                if (string.Equals(item.Type, SessionEventTypes.StepEnded, StringComparison.OrdinalIgnoreCase)) status = Text(Property(item.Data, "Status")) ?? status;
                else if (string.Equals(item.Type, SessionEventTypes.LlmFailure, StringComparison.OrdinalIgnoreCase)) status = "failed";
                else if (string.Equals(item.Type, SessionEventTypes.LlmResponse, StringComparison.OrdinalIgnoreCase)) status = "completed";
                else if (string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase)) status = "rejected";
            }

            var attempts = ordered.Select(item => IntValue(Property(item.Data, "Attempt"))).Where(value => value.HasValue)
                .Select(value => value.Value).DefaultIfEmpty(0).Max();
            attempts = Math.Max(Math.Max(1, attempts), ordered.Count(item => string.Equals(item.Type, SessionEventTypes.LlmRequest, StringComparison.OrdinalIgnoreCase)));
            var terminal = ordered.LastOrDefault(item =>
                string.Equals(item.Type, SessionEventTypes.StepEnded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.LlmFailure, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.LlmResponse, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase));
            var promptTokens = LastInt(ordered, "PromptTokens");
            var completionTokens = LastInt(ordered, "CompletionTokens");
            var totalTokens = LastInt(ordered, "TotalTokens");
            var estimatedTokens = LastInt(ordered, "EstimatedPromptTokens");
            var cost = ordered.Select(item => CostUsd(item.Data)).LastOrDefault(value => value.HasValue);
            var payloads = new JArray(ordered.Where(item => item.Payload != null).Select(item => new JObject
            {
                ["eventId"] = item.EventId,
                ["eventType"] = item.Type,
                ["sha256"] = item.Payload.Sha256,
                ["byteLength"] = item.Payload.ByteLength,
                ["contentType"] = item.Payload.ContentType
            }));
            var row = new TrajectoryViewRow
            {
                Id = "model:" + (stepId ?? key),
                View = TrajectoryViews.ModelReplay,
                Kind = "model-step",
                Title = ordered.Select(item => Text(Property(item.Data, "Purpose"))).LastOrDefault(value => value != null) ??
                    ordered.Select(item => Text(Property(item.Data, "Model"))).LastOrDefault(value => value != null) ?? "Model request",
                Status = status,
                RunId = LastText(ordered.Select(item => item.RunId)),
                TurnId = LastText(ordered.Select(item => item.TurnId)),
                StepId = stepId,
                AttemptCount = attempts,
                FailureCount = ordered.Count(item =>
                    string.Equals(item.Type, SessionEventTypes.LlmFailure, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase)),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                EstimatedPromptTokens = estimatedTokens,
                CostUsd = cost,
                CompletedUtc = terminal == null ? (DateTime?)null : terminal.CreatedUtc,
                Data = new JObject
                {
                    ["eventTypes"] = new JArray(ordered.Select(item => item.Type)),
                    ["requestEventIds"] = new JArray(ordered.Where(item => string.Equals(item.Type, SessionEventTypes.LlmRequest, StringComparison.OrdinalIgnoreCase)).Select(item => item.EventId)),
                    ["responseEventIds"] = new JArray(ordered.Where(item => string.Equals(item.Type, SessionEventTypes.LlmResponse, StringComparison.OrdinalIgnoreCase)).Select(item => item.EventId)),
                    ["formatRepairCount"] = ordered.Count(item => string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase)),
                    ["chunkBatchCount"] = ordered.Count(item => string.Equals(item.Type, SessionEventTypes.AssistantChunk, StringComparison.OrdinalIgnoreCase)),
                    ["failureKinds"] = new JArray(ordered.Select(item => Text(Property(item.Data, "FailureKind"))).Where(value => value != null).Distinct(StringComparer.OrdinalIgnoreCase)),
                    ["payloads"] = payloads
                }
            };
            ApplySources(row, ordered);
            ApplyDuration(row);
            return row;
        }

        private static List<TrajectoryViewRow> BuildToolRows(IReadOnlyList<SessionEvent> events)
        {
            var tools = new Dictionary<string, ToolAggregate>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in events.Where(value => string.Equals(value.Type, SessionEventTypes.SessionCommit, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var operation in Operations(item))
                {
                    var operationType = Text(Property(operation, "Type"));
                    if (!IsToolOperation(operationType)) continue;
                    var data = Property(operation, "Data") as JObject ?? new JObject();
                    var message = Property(data, "Value") as JObject;
                    if (string.Equals(operationType, SessionOperationTypes.ToolCallRecorded, StringComparison.OrdinalIgnoreCase))
                    {
                        var calls = Property(message, "ToolCalls") as JArray;
                        foreach (var call in calls == null ? new List<JObject>() : calls.OfType<JObject>())
                        {
                            var callId = Text(Property(call, "Id"));
                            if (callId == null) continue;
                            var aggregate = Tool(tools, callId);
                            aggregate.ToolId = Text(Property(call, "Name")) ?? aggregate.ToolId;
                            aggregate.Add(item, operationType, "queued", message, null);
                        }
                        continue;
                    }

                    var activity = Property(message, "Activity") as JObject;
                    var toolCallId = Text(Property(activity, "ToolCallId")) ?? Text(Property(message, "ToolCallId"));
                    if (toolCallId == null) continue;
                    var tool = Tool(tools, toolCallId);
                    tool.ToolId = Text(Property(activity, "ToolId")) ?? Text(Property(message, "ToolName")) ?? tool.ToolId;
                    tool.Title = Text(Property(activity, "Title")) ?? tool.Title;
                    var status = ToolStatus(operationType, activity);
                    tool.Add(item, operationType, status, message, activity);
                }
            }
            return tools.Values.Select(ToolRow).ToList();
        }

        private static TrajectoryViewRow ToolRow(ToolAggregate tool)
        {
            var status = tool.Statuses.LastOrDefault() ?? "queued";
            SessionEvent terminal = null;
            for (var index = 0; index < tool.Events.Count && index < tool.Statuses.Count; index++)
            {
                if (IsTerminal(tool.Statuses[index])) terminal = tool.Events[index];
            }
            var row = new TrajectoryViewRow
            {
                Id = "tool:" + tool.ToolCallId,
                View = TrajectoryViews.ToolExecution,
                Kind = "tool-call",
                Title = tool.Title ?? tool.ToolId ?? tool.ToolCallId,
                Status = status,
                RunId = LastText(tool.Events.Select(item => item.RunId)),
                TurnId = LastText(tool.Events.Select(item => item.TurnId)),
                StepId = tool.StepId,
                ToolCallId = tool.ToolCallId,
                ToolId = tool.ToolId,
                AttemptCount = Math.Max(1, tool.OperationTypes.Count(value => string.Equals(value, SessionOperationTypes.ToolExecutionStarted, StringComparison.OrdinalIgnoreCase))),
                FailureCount = tool.Statuses.Count(value => string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase)),
                CompletedUtc = terminal == null ? (DateTime?)null : terminal.CreatedUtc,
                Data = new JObject
                {
                    ["operationTypes"] = new JArray(tool.OperationTypes),
                    ["statusHistory"] = new JArray(tool.Statuses),
                    ["waitedForConfirmation"] = tool.WaitedForConfirmation,
                    ["confirmationStartedUtc"] = tool.ConfirmationStartedUtc.HasValue ? new JValue(tool.ConfirmationStartedUtc.Value) : JValue.CreateNull(),
                    ["confirmationEndedUtc"] = tool.ConfirmationEndedUtc.HasValue ? new JValue(tool.ConfirmationEndedUtc.Value) : JValue.CreateNull(),
                    ["retryable"] = tool.Retryable.HasValue ? new JValue(tool.Retryable.Value) : JValue.CreateNull(),
                    ["pendingId"] = tool.PendingId == null ? JValue.CreateNull() : new JValue(tool.PendingId),
                    ["errorCode"] = tool.ErrorCode == null ? JValue.CreateNull() : new JValue(tool.ErrorCode),
                    ["resultMessage"] = tool.ResultMessage == null ? JValue.CreateNull() : new JValue(tool.ResultMessage)
                }
            };
            ApplySources(row, tool.Events);
            ApplyDuration(row);
            return row;
        }

        private static List<TrajectoryViewRow> BuildArtifactRows(IReadOnlyList<SessionEvent> events)
        {
            var artifacts = new Dictionary<string, ArtifactAggregate>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in events.Where(value => string.Equals(value.Type, SessionEventTypes.SessionCommit, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var operation in Operations(item))
                {
                    var type = Text(Property(operation, "Type"));
                    if (!IsArtifactOperation(type)) continue;
                    var data = Property(operation, "Data") as JObject ?? new JObject();
                    var value = Property(data, "Value") as JObject;
                    var id = Text(Property(value, "Id")) ?? Text(Property(data, "Id"));
                    if (id == null) continue;
                    ArtifactAggregate aggregate;
                    if (!artifacts.TryGetValue(id, out aggregate)) artifacts[id] = aggregate = new ArtifactAggregate { Id = id };
                    AddDistinct(aggregate.Events, item);
                    aggregate.Removed = string.Equals(type, SessionOperationTypes.ArtifactRemove, StringComparison.OrdinalIgnoreCase);
                    if (value != null) aggregate.Value = value;
                }
            }
            return artifacts.Values.Select(artifact =>
            {
                var value = artifact.Value ?? new JObject();
                var row = new TrajectoryViewRow
                {
                    Id = "artifact:" + artifact.Id,
                    View = TrajectoryViews.ArtifactLineage,
                    Kind = Text(Property(value, "Kind")) ?? "artifact",
                    Title = Text(Property(value, "Title")) ?? artifact.Id,
                    Status = artifact.Removed ? "removed" : "current",
                    RunId = Text(Property(value, "RunId")) ?? LastText(artifact.Events.Select(item => item.RunId)),
                    TurnId = LastText(artifact.Events.Select(item => item.TurnId)),
                    ArtifactId = artifact.Id,
                    ParentArtifactId = Text(Property(value, "ParentArtifactId")),
                    CompletedUtc = artifact.Events.Last().CreatedUtc,
                    Data = new JObject
                    {
                        ["revision"] = Property(value, "Revision") ?? JValue.CreateNull(),
                        ["mimeType"] = Property(value, "MimeType") ?? JValue.CreateNull(),
                        ["sourceMessageId"] = Property(value, "SourceMessageId") ?? JValue.CreateNull(),
                        ["contentSha256"] = Property(value, "ContentSha256") ?? JValue.CreateNull(),
                        ["contentByteLength"] = Property(value, "ContentByteLength") ?? JValue.CreateNull(),
                        ["relatedArtifactIds"] = Property(value, "RelatedArtifactIds") ?? new JArray()
                    }
                };
                ApplySources(row, artifact.Events);
                ApplyDuration(row);
                return row;
            }).ToList();
        }

        private static List<TrajectoryViewRow> BuildConfirmationRows(IEnumerable<TrajectoryViewRow> tools)
        {
            return (tools ?? new List<TrajectoryViewRow>()).Where(item => BoolValue(Property(item.Data, "waitedForConfirmation")) == true).Select(item =>
            {
                var row = Clone(item);
                row.Id = "confirmation:" + item.ToolCallId;
                row.View = TrajectoryViews.ConfirmationPauses;
                row.Kind = "confirmation-pause";
                row.Title = "Confirmation: " + item.Title;
                var started = DateValue(Property(item.Data, "confirmationStartedUtc"));
                var ended = DateValue(Property(item.Data, "confirmationEndedUtc"));
                row.CreatedUtc = started ?? item.CreatedUtc;
                row.CompletedUtc = ended;
                row.Status = !ended.HasValue ? "pending" :
                    string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase) ? "resolved" : item.Status;
                ApplyDuration(row);
                return row;
            }).ToList();
        }

        private static List<TrajectoryViewRow> BuildTurnRows(
            IReadOnlyList<SessionEvent> events,
            IList<TrajectoryViewRow> models,
            IList<TrajectoryViewRow> tools)
        {
            var groups = events.Where(item => !string.IsNullOrWhiteSpace(item.TurnId) || !string.IsNullOrWhiteSpace(item.RunId))
                .GroupBy(item => Text(item.TurnId) ?? Text(item.RunId), StringComparer.OrdinalIgnoreCase);
            var rows = new List<TrajectoryViewRow>();
            foreach (var group in groups)
            {
                var source = group.OrderBy(item => item.Sequence).ToList();
                var turnModels = models.Where(item => Same(item.TurnId ?? item.RunId, group.Key)).ToList();
                var turnTools = tools.Where(item => Same(item.TurnId ?? item.RunId, group.Key)).ToList();
                var promptTokens = Sum(turnModels.Select(item => item.PromptTokens));
                var completionTokens = Sum(turnModels.Select(item => item.CompletionTokens));
                var totalTokens = Sum(turnModels.Select(item => item.TotalTokens));
                var cost = Sum(turnModels.Select(item => item.CostUsd));
                if (!promptTokens.HasValue && !completionTokens.HasValue && !totalTokens.HasValue)
                {
                    var fallback = MessageUsage(source);
                    promptTokens = fallback.PromptTokens;
                    completionTokens = fallback.CompletionTokens;
                    totalTokens = fallback.TotalTokens;
                    if (!cost.HasValue) cost = fallback.CostUsd;
                }
                var ended = source.LastOrDefault(item => string.Equals(item.Type, SessionEventTypes.TurnEnded, StringComparison.OrdinalIgnoreCase));
                var started = source.FirstOrDefault(item => string.Equals(item.Type, SessionEventTypes.TurnStarted, StringComparison.OrdinalIgnoreCase));
                var status = ended == null ? "running" : Text(Property(ended.Data, "Status")) ?? "completed";
                var row = new TrajectoryViewRow
                {
                    Id = "turn:" + group.Key,
                    View = TrajectoryViews.TurnUsage,
                    Kind = "turn",
                    Title = "Turn " + group.Key,
                    Status = status,
                    RunId = LastText(source.Select(item => item.RunId)),
                    TurnId = group.Key,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    EstimatedPromptTokens = Sum(turnModels.Select(item => item.EstimatedPromptTokens)),
                    CostUsd = cost,
                    CompletedUtc = ended == null ? (DateTime?)null : ended.CreatedUtc,
                    FailureCount = turnModels.Sum(item => item.FailureCount) + turnTools.Sum(item => item.FailureCount),
                    AttemptCount = turnModels.Sum(item => item.AttemptCount),
                    Data = new JObject
                    {
                        ["modelStepCount"] = turnModels.Count,
                        ["toolCallCount"] = turnTools.Count,
                        ["confirmationPauseCount"] = turnTools.Count(item => BoolValue(Property(item.Data, "waitedForConfirmation")) == true),
                        ["providerCostAvailable"] = cost.HasValue
                    }
                };
                ApplySources(row, source);
                if (started != null) row.CreatedUtc = started.CreatedUtc;
                ApplyDuration(row);
                rows.Add(row);
            }
            return rows;
        }

        private static List<TrajectoryViewRow> BuildFailureRows(
            IEnumerable<TrajectoryViewRow> models,
            IEnumerable<TrajectoryViewRow> tools,
            IEnumerable<TrajectoryViewRow> turns)
        {
            var result = new List<TrajectoryViewRow>();
            foreach (var source in (models ?? new List<TrajectoryViewRow>()).Where(item => item.FailureCount > 0 || item.AttemptCount > 1 || IsFailure(item.Status)))
            {
                result.Add(FailureRow(source, "model"));
            }
            foreach (var source in (tools ?? new List<TrajectoryViewRow>()).Where(item => item.FailureCount > 0 || IsFailure(item.Status) || BoolValue(Property(item.Data, "retryable")) == true))
            {
                result.Add(FailureRow(source, "tool"));
            }
            foreach (var source in (turns ?? new List<TrajectoryViewRow>()).Where(item => IsFailure(item.Status)))
            {
                result.Add(FailureRow(source, "turn"));
            }
            return result;
        }

        private static TrajectoryViewRow FailureRow(TrajectoryViewRow source, string scope)
        {
            var row = Clone(source);
            row.Id = "failure:" + scope + ":" + source.Id;
            row.View = TrajectoryViews.FailureRetries;
            row.Kind = scope + "-failure";
            row.Title = char.ToUpperInvariant(scope[0]) + scope.Substring(1) + ": " + source.Title;
            row.Data["sourceView"] = source.View;
            row.Data["retryCount"] = Math.Max(0, source.AttemptCount - 1);
            return row;
        }

        private static MessageUsageTotals MessageUsage(IEnumerable<SessionEvent> events)
        {
            var messages = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in events.Where(value => string.Equals(value.Type, SessionEventTypes.SessionCommit, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var operation in Operations(item))
                {
                    var type = Text(Property(operation, "Type"));
                    var data = Property(operation, "Data") as JObject ?? new JObject();
                    if (string.Equals(type, SessionOperationTypes.MessageRemove, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Remove(Text(Property(data, "Id")) ?? string.Empty);
                        continue;
                    }
                    if (!IsMessageOperation(type)) continue;
                    var value = Property(data, "Value") as JObject;
                    var id = Text(Property(value, "Id"));
                    if (id != null) messages[id] = value;
                }
            }
            return new MessageUsageTotals
            {
                PromptTokens = Sum(messages.Values.Select(item => IntValue(Property(item, "PromptTokens")))),
                CompletionTokens = Sum(messages.Values.Select(item => IntValue(Property(item, "CompletionTokens")))),
                TotalTokens = Sum(messages.Values.Select(item => IntValue(Property(item, "TotalTokens")))),
                CostUsd = Sum(messages.Values.Select(CostUsd))
            };
        }

        private static bool IsModelEvent(SessionEvent item)
        {
            return item != null && (string.Equals(item.Type, SessionEventTypes.StepStarted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.StepEnded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.LlmRequest, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.LlmResponse, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.LlmFailure, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.AssistantChunk, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Type, SessionEventTypes.AgentResponseRejected, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsToolOperation(string type)
        {
            return string.Equals(type, SessionOperationTypes.ToolCallRecorded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolResultRecorded, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolExecutionStarted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ToolExecutionFinished, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMessageOperation(string type)
        {
            return string.Equals(type, SessionOperationTypes.MessageUpsert, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.UserMessageAppended, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.AssistantMessageAppended, StringComparison.OrdinalIgnoreCase) || IsToolOperation(type);
        }

        private static bool IsArtifactOperation(string type)
        {
            return string.Equals(type, SessionOperationTypes.ArtifactUpsert, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ArtifactRevisionCreated, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, SessionOperationTypes.ArtifactRemove, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToolStatus(string operationType, JObject activity)
        {
            var execution = Text(Property(activity, "ExecutionStatus"));
            var status = Text(Property(activity, "Status"));
            if (Same(execution, "waiting_confirmation") || Same(status, "waiting") || Same(status, "waiting_confirmation")) return "waiting_confirmation";
            if (!string.IsNullOrWhiteSpace(execution) && !Same(execution, "executing") && !Same(execution, "running")) return execution;
            if (!string.IsNullOrWhiteSpace(status)) return status;
            if (Same(operationType, SessionOperationTypes.ToolExecutionStarted)) return "running";
            if (Same(operationType, SessionOperationTypes.ToolResultRecorded)) return "completed";
            return "queued";
        }

        private static bool IsTerminal(string status)
        {
            return Same(status, "completed") || Same(status, "failed") || Same(status, "cancelled") || Same(status, "partial");
        }

        private static bool IsFailure(string status)
        {
            return Same(status, "failed") || Same(status, "cancelled") || Same(status, "rejected") || Same(status, "interrupted");
        }

        private static IEnumerable<JObject> Operations(SessionEvent item)
        {
            var operations = Property(item == null ? null : item.Data, "Operations") as JArray;
            return operations == null ? new List<JObject>() : operations.OfType<JObject>();
        }

        private static void ApplySources(TrajectoryViewRow row, IEnumerable<SessionEvent> events)
        {
            var source = (events ?? new List<SessionEvent>()).Where(item => item != null).OrderBy(item => item.Sequence).ToList();
            row.SourceEventSeqs = source.Select(item => item.Sequence).Distinct().ToList();
            row.SourceEventIds = source.Select(item => item.EventId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            row.FirstSequence = source.Count == 0 ? 0 : source[0].Sequence;
            row.LastSequence = source.Count == 0 ? 0 : source[source.Count - 1].Sequence;
            if (source.Count > 0 && row.CreatedUtc == default(DateTime)) row.CreatedUtc = source[0].CreatedUtc;
        }

        private static void ApplyDuration(TrajectoryViewRow row)
        {
            row.DurationMs = row.CompletedUtc.HasValue
                ? (long?)Math.Max(0, Math.Round((row.CompletedUtc.Value - row.CreatedUtc).TotalMilliseconds))
                : null;
        }

        private static TrajectoryViewRow Clone(TrajectoryViewRow source)
        {
            return new TrajectoryViewRow
            {
                Id = source.Id, View = source.View, Kind = source.Kind, Title = source.Title, Status = source.Status,
                CreatedUtc = source.CreatedUtc, CompletedUtc = source.CompletedUtc, DurationMs = source.DurationMs,
                FirstSequence = source.FirstSequence, LastSequence = source.LastSequence,
                RunId = source.RunId, TurnId = source.TurnId, StepId = source.StepId,
                ToolCallId = source.ToolCallId, ToolId = source.ToolId,
                ArtifactId = source.ArtifactId, ParentArtifactId = source.ParentArtifactId,
                AttemptCount = source.AttemptCount, FailureCount = source.FailureCount,
                PromptTokens = source.PromptTokens, CompletionTokens = source.CompletionTokens, TotalTokens = source.TotalTokens,
                EstimatedPromptTokens = source.EstimatedPromptTokens, CostUsd = source.CostUsd,
                Data = source.Data == null ? new JObject() : (JObject)source.Data.DeepClone(),
                SourceEventSeqs = new List<long>(source.SourceEventSeqs ?? new List<long>()),
                SourceEventIds = new List<string>(source.SourceEventIds ?? new List<string>())
            };
        }

        private static ToolAggregate Tool(IDictionary<string, ToolAggregate> tools, string id)
        {
            ToolAggregate value;
            if (!tools.TryGetValue(id, out value)) tools[id] = value = new ToolAggregate { ToolCallId = id };
            return value;
        }

        private static void AddDistinct(ICollection<SessionEvent> values, SessionEvent value)
        {
            if (value != null && !values.Any(item => item.Sequence == value.Sequence)) values.Add(value);
        }

        private static string CorrelationKey(string runId, string turnId)
        {
            return (turnId ?? string.Empty) + "\n" + (runId ?? string.Empty);
        }

        private static string LastText(IEnumerable<string> values)
        {
            return (values ?? new string[0]).Select(Text).LastOrDefault(value => value != null);
        }

        private static int? LastInt(IEnumerable<SessionEvent> events, string name)
        {
            return (events ?? new List<SessionEvent>()).Select(item => IntValue(Property(item.Data, name))).LastOrDefault(value => value.HasValue);
        }

        private static int? Sum(IEnumerable<int?> values)
        {
            var present = (values ?? new int?[0]).Where(value => value.HasValue).Select(value => value.Value).ToList();
            return present.Count == 0 ? (int?)null : present.Sum();
        }

        private static decimal? Sum(IEnumerable<decimal?> values)
        {
            var present = (values ?? new decimal?[0]).Where(value => value.HasValue).Select(value => value.Value).ToList();
            return present.Count == 0 ? (decimal?)null : present.Sum();
        }

        private static decimal? CostUsd(JToken source)
        {
            var usage = Property(source, "UsageJson");
            if (usage != null && usage.Type == JTokenType.String)
            {
                try { usage = JObject.Parse((string)usage); }
                catch (JsonException) { usage = null; }
            }
            usage = usage ?? Property(source, "Usage") ?? source;
            var direct = DecimalNamed(usage, "cost_usd", "total_cost_usd", "totalCostUsd", "costUsd");
            if (direct.HasValue) return direct;
            var currency = Text(Property(usage, "currency"));
            return Same(currency, "usd") ? DecimalNamed(usage, "cost", "total_cost", "totalCost") : null;
        }

        private static decimal? DecimalNamed(JToken source, params string[] names)
        {
            if (source == null) return null;
            var wanted = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var property in Properties(source))
            {
                if (!wanted.Contains(property.Name)) continue;
                decimal value;
                if (decimal.TryParse(Convert.ToString((property.Value as JValue)?.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            }
            return null;
        }

        private static IEnumerable<JProperty> Properties(JToken source)
        {
            var obj = source as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties())
                {
                    yield return property;
                    foreach (var nested in Properties(property.Value)) yield return nested;
                }
                yield break;
            }
            var array = source as JArray;
            if (array == null) yield break;
            foreach (var item in array)
            {
                foreach (var nested in Properties(item)) yield return nested;
            }
        }

        private static JToken Property(JToken source, string name)
        {
            var obj = source as JObject;
            return obj == null ? null : obj.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private static string Text(JToken value)
        {
            return value == null || value.Type == JTokenType.Null ? null : Text(Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture));
        }

        private static string Text(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        private static int? IntValue(JToken value)
        {
            int result;
            return value != null && int.TryParse(Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? (int?)result
                : null;
        }

        private static bool? BoolValue(JToken value)
        {
            bool result;
            return value != null && bool.TryParse(Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture), out result)
                ? (bool?)result
                : null;
        }

        private static DateTime? DateValue(JToken value)
        {
            DateTime result;
            return value != null && DateTime.TryParse(Convert.ToString((value as JValue)?.Value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result)
                ? (DateTime?)result
                : null;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ToolAggregate
        {
            public string ToolCallId;
            public string ToolId;
            public string Title;
            public string StepId;
            public bool WaitedForConfirmation;
            public DateTime? ConfirmationStartedUtc;
            public DateTime? ConfirmationEndedUtc;
            public bool? Retryable;
            public string PendingId;
            public string ErrorCode;
            public string ResultMessage;
            public readonly List<SessionEvent> Events = new List<SessionEvent>();
            public readonly List<string> OperationTypes = new List<string>();
            public readonly List<string> Statuses = new List<string>();

            public void Add(SessionEvent item, string operationType, string status, JObject message, JObject activity)
            {
                Events.Add(item);
                OperationTypes.Add(operationType);
                Statuses.Add(status);
                StepId = Text(Property(activity, "StepId")) ?? StepId;
                Retryable = BoolValue(Property(activity, "Retryable")) ?? Retryable;
                PendingId = Text(Property(activity, "PendingId")) ?? PendingId;
                ErrorCode = Text(Property(activity, "ErrorCode")) ?? ErrorCode;
                ResultMessage = Text(Property(activity, "ResultMessage")) ?? Text(Property(message, "Content")) ?? ResultMessage;
                if (Same(status, "waiting_confirmation"))
                {
                    WaitedForConfirmation = true;
                    if (!ConfirmationStartedUtc.HasValue) ConfirmationStartedUtc = item.CreatedUtc;
                }
                else if (WaitedForConfirmation && IsTerminal(status) && !ConfirmationEndedUtc.HasValue)
                {
                    ConfirmationEndedUtc = item.CreatedUtc;
                }
            }
        }

        private sealed class ArtifactAggregate
        {
            public string Id;
            public bool Removed;
            public JObject Value;
            public readonly List<SessionEvent> Events = new List<SessionEvent>();
        }

        private sealed class MessageUsageTotals
        {
            public int? PromptTokens;
            public int? CompletionTokens;
            public int? TotalTokens;
            public decimal? CostUsd;
        }
    }
}
