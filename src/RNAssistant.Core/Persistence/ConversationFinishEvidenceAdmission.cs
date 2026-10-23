using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Core.Persistence
{
    // Stateless reader over the validated append-only stream. The event store owns
    // session integrity; this reader owns current-run call/result correspondence.
    // Neither a materialized prompt nor a SQLite/chat projection grants evidence.
    public static class ConversationFinishEvidenceAdmission
    {
        public static FinishAdmission Evaluate(IReadOnlyList<SessionEvent> events, string sessionId,
            string runId, string turnId, IReadOnlyList<string> acceptedCallIds,
            IReadOnlyList<ConversationResultClaim> results, ChatBlobStore payloads)
        {
            if (events == null || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(turnId) || acceptedCallIds == null || results == null)
                throw new ArgumentException("Complete run/session and durable events are required.");
            if (results.Any(item => item == null) || acceptedCallIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Invalid finish proposal or accepted call identity.");
            var expected = new HashSet<string>(acceptedCallIds, StringComparer.Ordinal);
            if (expected.Count != acceptedCallIds.Count)
                throw new InvalidOperationException("Current-run accepted call IDs are ambiguous.");
            if (expected.Count == 0 && results.Count != 0)
                return FinishAdmission.Reject("unknown_evidence", "This run accepted no tool calls; finish with results=[].");
            if (expected.Count != 0 && results.Count == 0)
                return FinishAdmission.Reject("missing_finish_results", "Report the outcomes of the accepted calls before finish.");
            var references = results.SelectMany(item => item.Evidence).Distinct(StringComparer.Ordinal).ToArray();
            if (expected.Count != 0 && references.Length == 0)
                return FinishAdmission.Reject("missing_finish_evidence", "Reference at least one actual terminal tool_call_id from this run.");

            var calls = new Dictionary<string, Accepted>(StringComparer.Ordinal);
            var origins = new HashSet<string>(StringComparer.Ordinal);
            var started = new HashSet<string>(StringComparer.Ordinal);
            var finished = new Dictionary<string, Terminal>(StringComparer.Ordinal);
            var lastSequence = 0L;
            foreach (var item in events)
            {
                if (item == null || !string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) ||
                    item.Sequence <= lastSequence)
                    throw new InvalidOperationException("Finish evidence stream has wrong session or order.");
                lastSequence = item.Sequence;
                if (item.Type != SessionEventTypes.SessionCommit || item.RunId != runId) continue;
                if (item.TurnId != turnId)
                    throw new InvalidOperationException("A run commit has a different turn identity.");
                var operations = item.Data == null ? null : item.Data["Operations"] as JArray;
                if (operations == null) throw new InvalidOperationException("Run commit has no typed operations.");
                foreach (var operationToken in operations)
                {
                    var operation = operationToken.ToObject<SessionOperation>();
                    if (operation == null || operation.Data == null) throw new InvalidOperationException("Invalid run operation.");
                    if (operation.Type != SessionOperationTypes.ToolCallRecorded &&
                        operation.Type != SessionOperationTypes.ToolExecutionStarted &&
                        operation.Type != SessionOperationTypes.ToolExecutionFinished) continue;
                    var body = operation.Data["Value"];
                    if (body == null || body.Type != JTokenType.Object)
                        throw new InvalidOperationException("Tool fact lacks a message body.");
                    var message = body.ToObject<ChatMessage>();
                    if (message == null || message.RunId != runId)
                        throw new InvalidOperationException("Tool fact belongs to another run.");
                    if (operation.Type == SessionOperationTypes.ToolCallRecorded)
                    {
                        var origin = message.AcceptedCallOrigin;
                        if (!message.ProtocolMessage || message.ResponseProtocolVersion != ConversationResponse.ProtocolVersion ||
                            string.IsNullOrWhiteSpace(message.ToolCallId) || string.IsNullOrWhiteSpace(message.ToolName) ||
                            origin == null || string.IsNullOrWhiteSpace(origin.StepId) ||
                            string.IsNullOrWhiteSpace(origin.ModelAttemptId) || origin.CallIndex < 0 ||
                            !origins.Add(origin.StepId + "\n" + origin.ModelAttemptId + "\n" + origin.CallIndex) ||
                            !expected.Contains(message.ToolCallId) || calls.ContainsKey(message.ToolCallId))
                            throw new InvalidOperationException("Accepted call identity/origin is missing, foreign or duplicated.");
                        var full = AcceptedCallPayloadService.Hydrate(message, payloads);
                        var parsed = ConversationResponseHistoryReader.Read(full);
                        if (!parsed.Success || parsed.Response.ToolCalls.Count != 1 ||
                            parsed.Response.ToolCalls[0].Id != message.ToolCallId ||
                            parsed.Response.ToolCalls[0].Name != message.ToolName)
                            throw new InvalidOperationException("Retained accepted call body disagrees with its metadata.");
                        calls.Add(message.ToolCallId, new Accepted(message.ToolName, origin.StepId));
                        continue;
                    }
                    if (operation.Type == SessionOperationTypes.ToolExecutionStarted)
                    {
                        if (message.Activity == null || message.Activity.RunId != runId ||
                            string.IsNullOrWhiteSpace(message.Activity.ToolCallId) ||
                            !calls.ContainsKey(message.Activity.ToolCallId) ||
                            message.Activity.ToolId != calls[message.Activity.ToolCallId].Name ||
                            message.Activity.StepId != calls[message.Activity.ToolCallId].StepId)
                            throw new InvalidOperationException("Tool start has no run/call identity.");
                        started.Add(message.Activity.ToolCallId);
                        continue;
                    }
                    if (operation.Type == SessionOperationTypes.ToolExecutionFinished)
                    {
                        var activity = message.Activity;
                        if (activity == null || activity.RunId != runId ||
                            string.IsNullOrWhiteSpace(activity.ToolCallId) || string.IsNullOrWhiteSpace(activity.ToolId) ||
                            string.IsNullOrWhiteSpace(activity.StepId) || message.ExecutionProgress == null ||
                            !calls.ContainsKey(activity.ToolCallId) ||
                            activity.ToolId != calls[activity.ToolCallId].Name ||
                            activity.StepId != calls[activity.ToolCallId].StepId)
                            throw new InvalidOperationException("Tool terminal execution is missing or ambiguous.");
                        if (!Enum.IsDefined(typeof(ToolExecutionOutcome), message.ExecutionProgress.Outcome))
                            throw new InvalidOperationException("Tool terminal outcome is unknown.");
                        if (message.ExecutionProgress.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
                            continue;
                        if (message.ExecutionProgress.Outcome != ToolExecutionOutcome.NotDispatched &&
                            !started.Contains(activity.ToolCallId) || finished.ContainsKey(activity.ToolCallId))
                            throw new InvalidOperationException("Tool terminal execution is missing or ambiguous.");
                        finished.Add(activity.ToolCallId, new Terminal(activity.ToolId, activity.StepId,
                            message.ExecutionProgress.Outcome));
                        continue;
                    }
                }
            }
            if (calls.Count != expected.Count || expected.Any(id => !calls.ContainsKey(id)))
                throw new InvalidOperationException("Durable current-run accepted calls differ from kernel state.");
            foreach (var reference in references)
            {
                if (!expected.Contains(reference))
                    return FinishAdmission.Reject("unknown_evidence", "Evidence " + reference + " is not an accepted call in this run.");
                Terminal terminal;
                if (!finished.TryGetValue(reference, out terminal))
                    return FinishAdmission.Reject("nonterminal_evidence", "Evidence " + reference + " has no canonical terminal execution.");
                var call = calls[reference];
                if (terminal.Name != call.Name || terminal.StepId != call.StepId)
                    return FinishAdmission.Reject("unknown_evidence", "Evidence " + reference + " disagrees with its accepted name or origin.");
            }
            foreach (var claim in results)
                if (claim.Assessment == ConversationAssessment.Satisfied && claim.Evidence.Count > 0 &&
                    claim.Evidence.All(id => finished[id].Outcome != ToolExecutionOutcome.Ok))
                    return FinishAdmission.Reject("contradictory_assessment",
                        "A satisfied claim cites only failed, unknown or undispatched terminal outcomes.");
            return FinishAdmission.Allow();
        }

        private sealed class Accepted
        {
            internal readonly string Name, StepId;
            internal Accepted(string name, string stepId) { Name = name; StepId = stepId; }
        }

        private sealed class Terminal
        {
            internal readonly string Name, StepId;
            internal readonly ToolExecutionOutcome Outcome;
            internal Terminal(string name, string stepId, ToolExecutionOutcome outcome)
            { Name = name; StepId = stepId; Outcome = outcome; }
        }
    }
}
