using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class TaskListToolHandler : IToolHandler
    {
        private readonly ChatSession _session;
        private readonly TaskListService _service;

        internal TaskListToolHandler(string toolId, ChatSession session)
        {
            if (!TaskListToolCatalog.Owns(toolId))
                throw new ArgumentException(
                    "An exact Task List tool id is required.", nameof(toolId));
            _session = session;
            _service = new TaskListService();
        }

        internal static ToolBinding BindingFor(string toolId)
        {
            if (string.Equals(toolId, TaskListToolCatalog.SetToolId,
                StringComparison.Ordinal))
                return new ToolBinding("conversation.task-list.set.intent.v2");
            return null;
        }

        public Task<ToolHandlerResult> ExecuteAsync(
            ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session == null)
                return Failure("Task-list tools require an active chat session.",
                    "task_list_session_required", false);
            try
            {
                using (DocumentAccessGate.BeginOperation())
                {
                    return Task.FromResult(Project(Execute(context), context));
                }
            }
            catch (JsonException ex) when (!context.MayHaveDispatched)
            {
                return Failure("Task-list JSON is invalid: " + ex.Message,
                    "invalid_task_list", true);
            }
            catch (InvalidOperationException ex) when (!context.MayHaveDispatched)
            {
                return Failure(ex.Message, "invalid_task_list", true);
            }
        }

        private TaskListMutation Execute(ToolHandlerContext context)
        {
            var action = ToolArgumentReader.String(
                context.Arguments, "action", string.Empty);
            if (string.Equals(action, "save", StringComparison.Ordinal))
            {
                return _service.Set(_session,
                    ToolArgumentReader.String(context.Arguments, "goal", string.Empty),
                    ReadSteps(context.Arguments, "steps"),
                    context.MarkDispatchPossible);
            }
            return _service.CloseActive(_session,
                ToolArgumentReader.String(context.Arguments, "outcome", string.Empty),
                context.MarkDispatchPossible);
        }

        private ToolHandlerResult Project(
            TaskListMutation mutation, ToolHandlerContext context)
        {
            if (mutation == null)
                throw new InvalidOperationException(
                    "Task List service returned no outcome.");
            if (!mutation.Success)
            {
                var failed = ErrorData(mutation.ErrorCode, mutation.Retryable);
                return context.MayHaveDispatched
                    ? new ToolHandlerResult(RuntimeResult.Unknown(
                        mutation.Message, failed), ToolEffectEvidence.Unknown)
                    : new ToolHandlerResult(RuntimeResult.Error(
                        mutation.Message, failed), ToolEffectEvidence.None);
            }
            if (!context.MayHaveDispatched)
                return new ToolHandlerResult(RuntimeResult.Error(
                    "Task List mutation returned without a dispatch boundary.",
                    ErrorData("task_list_dispatch_evidence_missing", false)),
                    ToolEffectEvidence.None);
            if (!Verified(mutation))
                return new ToolHandlerResult(RuntimeResult.Unknown(
                    "Task List state could not be verified after mutation.",
                    ErrorData("task_list_verification_failed", false)),
                    ToolEffectEvidence.Unknown);
            return new ToolHandlerResult(RuntimeResult.Ok(
                mutation.Message, Payload(mutation).ToString(Formatting.None)),
                ToolEffectEvidence.VerifiedChange);
        }

        private bool Verified(TaskListMutation mutation)
        {
            var artifact = mutation.Artifact;
            if (artifact == null || !_session.Artifacts.Any(item =>
                object.ReferenceEquals(item, artifact))) return false;
            return mutation.Closed
                ? string.IsNullOrWhiteSpace(_session.ActiveTaskListArtifactId)
                : string.Equals(_session.ActiveTaskListArtifactId,
                    artifact.Id, StringComparison.Ordinal);
        }

        private static JObject Payload(TaskListMutation mutation)
        {
            return new JObject
            {
                ["artifactId"] = mutation.Artifact == null
                    ? null : mutation.Artifact.Id,
                ["revision"] = mutation.Artifact == null
                    ? 0 : mutation.Artifact.Revision,
                ["taskList"] = mutation.TaskList == null
                    ? null : JObject.FromObject(mutation.TaskList)
            };
        }

        private static List<ChatTaskStep> ReadSteps(
            IDictionary<string, object> arguments, string name)
        {
            object raw;
            if (arguments == null || !arguments.TryGetValue(name, out raw) ||
                raw == null) return new List<ChatTaskStep>();
            var token = raw as JToken ?? JToken.FromObject(raw);
            return token.ToObject<List<ChatTaskStep>>() ??
                new List<ChatTaskStep>();
        }

        private static string ErrorData(string code, bool? retryable)
        {
            return JsonConvert.SerializeObject(new { code, retryable });
        }

        private static Task<ToolHandlerResult> Failure(
            string message, string code, bool retryable)
        {
            return Task.FromResult(new ToolHandlerResult(
                RuntimeResult.Error(message, ErrorData(code, retryable)),
                ToolEffectEvidence.None));
        }
    }
}
