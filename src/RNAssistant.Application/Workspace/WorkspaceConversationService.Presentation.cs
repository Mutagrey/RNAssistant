using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Runtime
{
    public sealed partial class WorkspaceConversationService
    {
        private sealed class FilePresentationTarget
        {
            public string Target { get; set; }
            public string Destination { get; set; }
        }

        private static WorkspaceFileActivity FileActivity(string toolId, string arguments,
            ToolExecutionOutcome? outcome, ResourceEffect effect)
        {
            if (!ResourceMutationToolCatalog.Owns(toolId)) return null;
            var state = effect?.Outcome;
            if (outcome == ToolExecutionOutcome.Unknown) state = ResourceEffectOutcome.UnknownAfterDispatch;
            if (state != ResourceEffectOutcome.VerifiedChanged && state != ResourceEffectOutcome.Restored &&
                state != ResourceEffectOutcome.UnknownAfterDispatch) return null;
            if (toolId == ResourceMutationToolCatalog.Patch && effect?.Targets != null)
                return new WorkspaceFileActivity { ToolId = toolId, Effect = state.Value,
                    Targets = effect.Targets.Take(ResourcePatch.MaximumTargets).Select(t => new PatchTargetResult {
                        Target = t.Target, Status = t.Status, WriteStarted = t.WriteStarted }).ToArray() };
            FilePresentationTarget target = null;
            try { target = JsonConvert.DeserializeObject<FilePresentationTarget>(arguments ?? "{}"); }
            catch (JsonException) { /* Missing target is explicit, never guessed from narrative. */ }
            if (target?.Target?.Contains(": ") == true) return null;
            return new WorkspaceFileActivity { ToolId = toolId, Path = PresentationText(target?.Target, 1024),
                TargetPath = PresentationText(target?.Destination, 1024),
                PathsTruncated = target?.Target?.Length > 1024 || target?.Destination?.Length > 1024,
                Effect = state.Value };
        }

        private static string PresentationText(string value, int maximum)
        { return value != null && value.Length > maximum ? value.Substring(0, maximum) + " [truncated]" : value; }

        private static WorkspaceVerificationSummary VerificationSummary(WorkspaceWebSnapshotStore snapshots,
            WorkspaceDescriptor workspace, string sessionId, string runId, string callId,
            IReadOnlyList<ResourceRef> references)
        {
            if (references == null || references.Count != 1)
                return new WorkspaceVerificationSummary { Unavailable = true };
            try
            {
                var record = snapshots.ReadVerification(workspace, references[0]);
                if (record.Origin?.SessionId != sessionId || record.Origin.RunId != runId ||
                    record.Origin.ToolCallId != callId)
                    return new WorkspaceVerificationSummary { Unavailable = true };
                return new WorkspaceVerificationSummary
                {
                    VerificationId = record.Id, SnapshotId = record.SnapshotId, EntryPath = PresentationText(record.EntryPath, 1024),
                    EntryPathTruncated = record.EntryPath?.Length > 1024,
                    State = record.State, Historical = record.Historical, CheckedFiles = record.CheckedFiles.Count,
                    PassedChecks = record.CheckResults.Count(item => item.Status == WebCheckStatus.Passed),
                    FailedChecks = record.CheckResults.Count(item => item.Status == WebCheckStatus.Failed),
                    NotRunChecks = record.CheckResults.Count(item => item.Status == WebCheckStatus.NotRun)
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidOperationException || ex is ArgumentException || ex is JsonException)
            { return new WorkspaceVerificationSummary { Unavailable = true }; }
        }

        private WorkspaceRunPresentation Presentation(WorkspaceDescriptor workspace, ChatSession session)
        {
            var presentation = new WorkspaceRunPresentation();
            var run = session.LastRun;
            if (run == null) return presentation;
            var runs = new HashSet<string>(run.WorkspaceAcceptance?.PriorQuestionRunIds ?? new List<string>(),
                StringComparer.Ordinal) { run.RunId };
            // Retain at most 32 operations while scanning the existing projection;
            // do not materialize a second unbounded task history for presentation.
            var kept = new List<ChatMessage>(32);
            ChatMessage verification = null;
            for (var i = session.Messages.Count - 1; i >= 0; i--)
            {
                var item = session.Messages[i];
                if (!runs.Contains(item.RunId) || !item.ProtocolMessage || string.IsNullOrWhiteSpace(item.ToolCallId)) continue;
                if (verification == null && item.ToolName == "web.verify") verification = item;
                if (ResourceMutationToolCatalog.Owns(item.ToolName) &&
                    (item.ResourceEffect?.Outcome == ResourceEffectOutcome.VerifiedChanged ||
                     item.ResourceEffect?.Outcome == ResourceEffectOutcome.Restored ||
                     item.ResourceEffect?.Outcome == ResourceEffectOutcome.UnknownAfterDispatch ||
                     item.ExecutionProgress?.Outcome == ToolExecutionOutcome.Unknown))
                {
                    if (kept.Count < 32) kept.Add(item);
                    else presentation.FilesTruncated = true;
                }
                if (presentation.FilesTruncated && verification != null) break;
            }
            kept.Reverse();
            var ids = new HashSet<string>(kept.Select(item => item.ToolCallId), StringComparer.Ordinal);
            var calls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in session.Messages)
            {
                if (calls.Count == ids.Count) break;
                if (!runs.Contains(message.RunId) || message.ToolCalls == null) continue;
                foreach (var call in message.ToolCalls)
                    if (ids.Contains(call.Id) && !calls.ContainsKey(call.Id)) calls.Add(call.Id, call.ArgumentsJson);
            }
            presentation.Files = kept.Select(item => FileActivity(item.ToolName,
                calls.TryGetValue(item.ToolCallId, out var arguments) ? arguments : null,
                item.ExecutionProgress?.Outcome, item.ResourceEffect)).Where(item => item != null).ToArray();
            if (verification != null)
                presentation.LatestVerification = VerificationSummary(_webSnapshots, workspace, session.Id,
                    verification.RunId, verification.ToolCallId, verification.ResourceRefs);
            return presentation;
        }
    }
}
