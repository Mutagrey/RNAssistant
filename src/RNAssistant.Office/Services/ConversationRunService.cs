using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ChatTurnResult
    {
        public string AssistantText { get; set; }
        public IReadOnlyList<object> ToolResults { get; set; }
        public object ContextUsage { get; set; }
        public bool WaitingForConfirmation { get; set; }
        public int ResponseProtocolVersion { get; set; }
        public string ResponseStatus { get; set; }
        public string RunStatus { get; set; }
        public RunExecutionSummary ExecutionSummary { get; set; }
    }

    public sealed class ConversationRunService
    {
        public delegate string PendingToolRegistrar(ChatSession session, ToolCommand command, ToolResult result);

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly Func<IModelProtocol> _modelProtocolFactory;
        private readonly ContextCompactionService _contextCompactionService;
        private readonly AttachmentAnalysisService _attachmentAnalysisService;

        public ConversationRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync)
            : this(adapter, toolExecutor, completeAsync, null)
        {
        }

        internal ConversationRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            ContextCompactionService contextCompactionService,
            Func<IModelProtocol> modelProtocolFactory = null)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _modelProtocolFactory = modelProtocolFactory ?? (() => new ModelProtocolClient(completeAsync));
            _contextCompactionService = contextCompactionService;
            _attachmentAnalysisService = new AttachmentAnalysisService(completeAsync);
        }

        public Task<ChatTurnResult> ExecuteAsync(
            string mode,
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteAsync(mode, text, session, documentContext, settings, tools, null, progress,
                pendingToolRegistrar, skills, cancellationToken, true);
        }

        public Task<ChatTurnResult> ExecuteAsync(
            string mode,
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken,
            bool appendUserMessage = true)
        {
            settings = settings ?? new AppSettings();
            settings.EnsureAgentPromptsReviewed();
            ConversationProtocolContext.EnsureCurrentHistory(session);
            mode = ValidateMode(mode, session);
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text ?? string.Empty,
                    HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId),
                    Attachments = attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(attachments)
                });
            }
            return RunLoopAsync(mode, text, session, documentContext, settings, tools, attachments, progress,
                pendingToolRegistrar, skills, null, null, cancellationToken);
        }

        public Task<ChatTurnResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ToolResult confirmedResult,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null,
            CancellationToken cancellationToken = default(CancellationToken),
            int initialIterationsUsed = 0,
            int initialToolStepsUsed = 0,
            RunSummaryBuilder summaryBuilder = null)
        {
            if (!string.Equals(ChatModes.Normalize(session == null ? null : session.Mode), ChatModes.Agent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only Agent mode can continue a confirmed tool call.");
            }
            settings = settings ?? new AppSettings();
            settings.EnsureAgentPromptsReviewed();
            ConversationProtocolContext.EnsureCanContinue(session, confirmedCommand);
            return RunLoopAsync(ChatModes.Agent, LatestUserRequest(session), session, documentContext, settings, tools, attachments,
                progress, pendingToolRegistrar, skills, confirmedCommand, confirmedResult, cancellationToken,
                initialIterationsUsed, initialToolStepsUsed, summaryBuilder);
        }

        private async Task<ChatTurnResult> RunLoopAsync(
            string mode,
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            ToolCommand initialCommand,
            ToolResult initialResult,
            CancellationToken cancellationToken,
            int initialIterationsUsed = 0,
            int initialToolStepsUsed = 0,
            RunSummaryBuilder summaryBuilder = null)
        {
            var policy = ConversationRunPolicy.For(mode);
            ConversationModelSession.ReleasePreviousMedia(session);
            var runnableCatalog = PrepareToolsForRun(tools);
            var enabledSkills = policy.SelectSkills(skills);
            CapabilityDiscoveryExecutor.ThrowOnCollision(runnableCatalog, enabledSkills);
            runnableCatalog = _toolExecutor.AvailableConversationToolsForSession(runnableCatalog, session);
            runnableCatalog = policy.SelectTools(runnableCatalog);
            CapabilityDiscoveryExecutor.BindReadSchema(runnableCatalog, enabledSkills);
            var protocolContext = ConversationProtocolContext.Begin(session, runnableCatalog, initialCommand);
            protocolContext.EnsureComplete();
            if (!policy.AllowsConfirmation) pendingToolRegistrar = null;
            summaryBuilder = summaryBuilder ?? new RunSummaryBuilder(runnableCatalog,
                initialCommand == null ? null : RunSummaryBuilder.ContinuationSeed(session));
            if (initialCommand != null) summaryBuilder.Observe(initialCommand, initialResult);
            summaryBuilder.UseCatalog(runnableCatalog);
            summaryBuilder.Publish(session);
            var modelSession = await ConversationModelSession.CreateAsync(
                _adapter,
                _contextCompactionService,
                _attachmentAnalysisService,
                policy.Mode,
                text,
                session,
                documentContext,
                settings,
                runnableCatalog,
                enabledSkills,
                attachments,
                initialCommand != null && initialResult != null,
                progress,
                cancellationToken).ConfigureAwait(false);
            var results = new List<object>();
            var toolSteps = Math.Max(0, initialToolStepsUsed);
            var iterationsUsed = Math.Max(0, initialIterationsUsed);
            object contextUsage = null;
            var modelProtocol = _modelProtocolFactory();
            var protocolProgress = ConversationStreamProgressProjector.ForProtocol(progress);

            try
            {
            if (initialCommand != null && initialResult != null)
            {
                modelSession.AppendConfirmedResult(initialCommand, initialResult);
                results.Add(AgentTranscript.DescribeResult(initialCommand, initialResult));
                var confirmedCost = Math.Max(1, initialResult.ToolStepsConsumed);
                toolSteps += initialToolStepsUsed > 0 ? Math.Max(0, confirmedCost - 1) : confirmedCost;
                UpdateRunCursor(session, iterationsUsed, toolSteps, "running", "executing");
            }

            for (; iterationsUsed < Math.Max(1, settings.MaxAgentIterations);)
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterationsUsed += 1;
                UpdateRunCursor(session, iterationsUsed, toolSteps, "running", "thinking");
                Report(progress, "thinking", "Модель выбирает следующий шаг...", null);
                var stepId = Guid.NewGuid().ToString("N");
                ModelProtocolResult protocolResult;
                try
                {
                    protocolResult = await modelProtocol.GetResponseAsync(
                        modelSession.CreateRequest(stepId, protocolContext.Snapshot()),
                        protocolProgress, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // Every internal attempt sees the same materialized prompt. Release
                    // ephemeral media only after the protocol step accepts or terminates.
                    modelSession.ReleaseRequestMedia();
                }
                contextUsage = protocolResult.ContextUsage ?? contextUsage;
                if (protocolResult.Failure != null)
                {
                    var failure = protocolResult.Failure;
                    // Phase 2A adapter to the existing controller failure/cancellation path.
                    if (failure.Cause != null) ExceptionDispatchInfo.Capture(failure.Cause).Throw();
                    var budgetFailure = failure.Kind == ModelProtocolFailureKind.PromptBudgetExceeded;
                    return FinishWithDiagnostic(session, summaryBuilder, results, contextUsage, failure.Message,
                        budgetFailure ? "Контекст переполнен" : "Некорректный ответ модели",
                        budgetFailure ? "prompt_budget_exceeded" : "invalid_model_response");
                }
                if (protocolResult.ProviderRefusal != null)
                {
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(protocolResult.ProviderRefusal,
                        protocolResult.Completion, null, AgentResponseStatuses.Refused));
                    return Result(session, summaryBuilder, protocolResult.ProviderRefusal, results, contextUsage,
                        false, AgentResponseStatuses.Refused, AgentResponseStatuses.Refused);
                }
                var response = protocolResult.Response;
                protocolContext.ObserveAccepted(response.ToolCalls);
                var completion = protocolResult.Completion;
                if (response.ToolCalls.Count == 0)
                {
                    var finalText = response.Message;
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                        finalText, completion, null, AgentResponseStatuses.Completed));
                    // Empty calls end the model loop; the existing independent execution
                    // summary remains the authority for effects, errors and unknowns.
                    return Result(session, summaryBuilder, finalText, results, contextUsage, false,
                        AgentResponseStatuses.Completed, AgentResponseStatuses.Completed);
                }

                var stepMessage = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : response.Message.Trim();
                if (!string.IsNullOrWhiteSpace(stepMessage))
                {
                    Report(progress, "acting", stepMessage, new ChatActivity
                    {
                        StepId = stepId,
                        StepMessage = stepMessage,
                        Kind = "step",
                        Title = stepMessage,
                        Status = "running"
                    });
                }
                for (var callIndex = 0; callIndex < response.ToolCalls.Count; callIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var call = response.ToolCalls[callIndex];
                    var command = AgentJsonProtocol.ToCommand(call);
                    command.RuntimeStepId = stepId;
                    modelSession.AppendToolCall(call,
                        callIndex == 0 ? response.Message : string.Empty,
                        callIndex == 0 ? completion : null);
                    var activityMessage = AgentTranscript.CreateRunningToolMessage(session, command, stepId, stepMessage);
                    session.Messages.Add(activityMessage);
                    Report(progress, "tool_running",
                        string.IsNullOrWhiteSpace(stepMessage) ? "Выполняю действие" : stepMessage,
                        activityMessage.Activity);

                    ToolResult toolResult;
                    if (toolSteps >= Math.Max(1, settings.MaxAgentToolSteps))
                    {
                        toolResult = ToolResult.Fail("Conversation tool step limit reached.", null, "tool_step_limit_reached", false);
                    }
                    else
                    {
                        try
                        {
                            toolResult = _toolExecutor.Execute(
                                command,
                                runnableCatalog,
                                settings,
                                false,
                                false,
                                session,
                                Math.Max(1, settings.MaxAgentToolSteps - toolSteps),
                                enabledSkills,
                                cancellationToken) ?? ToolResult.Fail("Tool returned no result.", null, "missing_result", true);
                        }
                        catch
                        {
                            // No result after entering the executor cannot certify a write's effect.
                            summaryBuilder.Observe(command, null);
                            summaryBuilder.Publish(session, activityMessage);
                            throw;
                        }
                    }
                    summaryBuilder.Observe(command, toolResult);
                    summaryBuilder.Publish(session, activityMessage);
                    if (!policy.AllowsConfirmation && AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var consumedSteps = Math.Max(1, toolResult.ToolStepsConsumed);
                        toolResult = ToolResult.Fail(
                            "This conversation mode cannot execute a tool that requires confirmation.",
                            null,
                            "conversation_policy_denied",
                            false);
                        toolResult.ToolStepsConsumed = consumedSteps;
                    }
                    toolSteps += Math.Max(1, toolResult.ToolStepsConsumed);
                    UpdateRunCursor(session, iterationsUsed, toolSteps, "running", "tool_result");
                    if (AgentTranscript.IsWaitingResult(toolResult) && pendingToolRegistrar != null)
                    {
                        toolResult.ConfirmationCatalogSha256 = ToolExecutionFingerprint(
                            runnableCatalog,
                            command.ToolId);
                        toolResult.PendingId = pendingToolRegistrar(session, command, toolResult);
                    }

                    if (!AgentTranscript.IsWaitingResult(toolResult) && !AgentTranscript.IsAwaitingUserResult(toolResult))
                    {
                        var prepared = await modelSession.PrepareToolResultAsync(toolResult, cancellationToken).ConfigureAwait(false);
                        toolResult = prepared.Result;
                        summaryBuilder.Observe(command, toolResult);
                        summaryBuilder.Publish(session, activityMessage);
                        modelSession.AppendToolResult(command, prepared);
                    }
                    AgentTranscript.CompleteToolActivityMessage(session, activityMessage, command, toolResult, stepId, stepMessage);
                    summaryBuilder.Observe(command, toolResult);
                    summaryBuilder.Publish(session, activityMessage);
                    results.Add(AgentTranscript.DescribeResult(command, toolResult));

                    if (AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var waitingText = string.IsNullOrWhiteSpace(response.Message) ? toolResult.Message : response.Message.Trim();
                        UpdateRunCursor(session, iterationsUsed, toolSteps,
                            "waiting_confirmation", "waiting_confirmation");
                        Report(progress, "tool_result", toolResult.Message, activityMessage.Activity);
                        return Result(session, summaryBuilder, waitingText, results, contextUsage, true, null, "waiting_confirmation");
                    }
                    if (AgentTranscript.IsAwaitingUserResult(toolResult))
                    {
                        var waitingText = string.IsNullOrWhiteSpace(toolResult.Message) ? response.Message : toolResult.Message;
                        UpdateRunCursor(session, iterationsUsed, toolSteps, "awaiting_user", "awaiting_user");
                        Report(progress, "tool_result", waitingText, activityMessage.Activity);
                        return Result(session, summaryBuilder, waitingText, results, contextUsage, false,
                            AgentResponseStatuses.AwaitingUser, AgentResponseStatuses.AwaitingUser);
                    }
                    Report(progress, "tool_result", toolResult.Message, activityMessage.Activity);
                    if (string.Equals(toolResult.ErrorCode, "tool_step_limit_reached", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
                modelSession.EndResponse();
            }

            var limitText = "Выполнение остановлено: достигнут лимит шагов.";
            return FinishWithDiagnostic(session, summaryBuilder, results, contextUsage, limitText,
                "Лимит выполнения", "step_limit_reached");
            }
            finally
            {
                modelSession.Dispose();
            }
        }

        internal static List<ToolDefinition> PrepareToolsForRun(IEnumerable<ToolDefinition> tools)
        {
            var source = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && ValidToolId(tool.Id))
                .OrderByDescending(tool => tool.BuiltIn)
                .ThenBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().Clone())
                .ToList();
            var safety = ToolSafetyPolicy.ResolveAll(source);
            var result = new List<ToolDefinition>();
            foreach (var tool in source)
            {
                ToolSafetyProfile profile;
                if (!safety.TryGetValue(tool.Id, out profile) || !profile.Valid || !profile.AgentCanRun) continue;
                JObject schema;
                string schemaError;
                if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError)) continue;
                if (!string.IsNullOrWhiteSpace(tool.CapabilityStatus) &&
                    !string.Equals(tool.CapabilityStatus, "available", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tool.CapabilityStatus, "partial", StringComparison.OrdinalIgnoreCase)) continue;
                var descriptor = ConversationPromptComposer.BuildTool(tool);
                if (descriptor == null || descriptor.ToString(Formatting.None).Length >
                    CapabilityDiscoveryExecutor.MaximumDescriptorCharacters) continue;
                tool.MutatesDocument = profile.MutatesDocument;
                tool.MutatesLocalState = profile.MutatesLocalState;
                tool.RequiresConfirmation = profile.RequiresConfirmation;
                tool.RiskLevel = profile.RiskLevel;
                result.Add(tool);
            }
            return result.OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static List<ToolDefinition> PrepareToolsForMode(
            string mode,
            IEnumerable<ToolDefinition> tools)
        {
            return ConversationRunPolicy.For(mode).SelectTools(PrepareToolsForRun(tools));
        }

        internal static string ToolExecutionFingerprint(IEnumerable<ToolDefinition> tools, string rootToolId)
        {
            var catalog = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ToolDefinition root;
            if (!catalog.TryGetValue(rootToolId ?? string.Empty, out root) ||
                string.Equals(root.Executor, "pipeline", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            var selected = new[] { root };

            var canonical = selected
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(tool => new
                {
                    tool.Id,
                    tool.BuiltIn,
                    tool.Scope,
                    tool.ArgumentSchemaJson,
                    tool.Executor,
                    codeSha256 = Sha256Text(tool.Code),
                    tool.EntryPoint,
                    argumentOrder = tool.ArgumentOrder ?? new List<string>(),
                    components = (tool.Components ?? new List<VbaToolComponent>())
                        .Where(component => component != null)
                        .Select(component => new
                        {
                            component.Name,
                            component.Type,
                            codeSha256 = Sha256Text(component.Code)
                        }),
                    tool.Enabled,
                    tool.AgentCanRun,
                    tool.MutatesDocument,
                    tool.MutatesLocalState,
                    tool.RequiresConfirmation,
                    tool.RiskLevel,
                    tool.CapabilityStatus
                })
                .ToList();
            var json = JsonConvert.SerializeObject(canonical, Formatting.None);
            return Sha256Text(json);
        }

        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static bool ValidToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsWhiteSpace);
        }

        private static ChatTurnResult FinishWithDiagnostic(
            ChatSession session,
            RunSummaryBuilder summaryBuilder,
            IReadOnlyList<object> results,
            object contextUsage,
            string text,
            string title = "Некорректный ответ модели",
            string executionStatus = "invalid_model_response")
        {
            var activity = new ChatActivity
            {
                Kind = "diagnostic",
                Title = title,
                Status = "failed",
                ExecutionStatus = executionStatus,
                ResultMessage = text
            };
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(text, null, activity));
            return Result(session, summaryBuilder, text, results, contextUsage, false, null, "failed");
        }

        private static ChatTurnResult Result(
            ChatSession session,
            RunSummaryBuilder summaryBuilder,
            string text,
            IReadOnlyList<object> results,
            object contextUsage,
            bool waitingForConfirmation,
            string responseStatus = null,
            string runStatus = null)
        {
            return new ChatTurnResult
            {
                AssistantText = text ?? string.Empty,
                ExecutionSummary = summaryBuilder.Publish(session, session.Messages.LastOrDefault(message =>
                    message != null && !message.ProtocolMessage && string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))),
                ToolResults = results ?? new object[0],
                ContextUsage = contextUsage,
                WaitingForConfirmation = waitingForConfirmation,
                ResponseProtocolVersion = AgentResponseStatuses.IsKnown(responseStatus)
                    ? AgentResponseProtocol.CurrentVersion
                    : 0,
                ResponseStatus = AgentResponseStatuses.IsKnown(responseStatus) ? responseStatus : null,
                RunStatus = string.IsNullOrWhiteSpace(runStatus)
                    ? (waitingForConfirmation ? "waiting_confirmation" : "failed")
                    : runStatus
            };
        }

        private static void UpdateRunCursor(
            ChatSession session,
            int iterationsUsed,
            int toolStepsUsed,
            string status,
            string phase)
        {
            if (session == null || session.LastRun == null) return;
            session.LastRun.IterationsUsed = Math.Max(0, iterationsUsed);
            session.LastRun.ToolStepsUsed = Math.Max(0, toolStepsUsed);
            if (!string.IsNullOrWhiteSpace(status)) session.LastRun.Status = status;
            if (!string.IsNullOrWhiteSpace(phase)) session.LastRun.Phase = phase;
        }

        private static string LatestUserRequest(ChatSession session)
        {
            var message = (session == null ? null : session.Messages ?? new List<ChatMessage>())
                .LastOrDefault(item => item != null && !item.ProtocolMessage &&
                    string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
            return message == null ? string.Empty : message.Content ?? string.Empty;
        }

        private static string ValidateMode(string mode, ChatSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var requested = ChatModes.Normalize(mode);
            var persisted = ChatModes.Normalize(session.Mode);
            if (!string.Equals(requested, persisted, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Conversation mode does not match the active chat session.");
            }
            return requested;
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null) progress(phase, message ?? string.Empty, activity);
        }

    }
}
