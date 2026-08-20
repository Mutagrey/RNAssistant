using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ChatTurnResult
    {
        public string AssistantText { get; set; }
        public IReadOnlyList<object> ToolResults { get; set; }
        public object ContextUsage { get; set; }
    }

    public sealed class AgentRunService
    {
        public delegate string PendingToolRegistrar(ChatSession session, ToolCommand command, ToolResult result);

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly AgentPromptComposer _promptComposer;
        private readonly AgentResponseParser _responseParser;
        private readonly ContextCompactionService _contextCompactionService;

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync)
            : this(adapter, toolExecutor, completeAsync, null)
        {
        }

        internal AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            ContextCompactionService contextCompactionService)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _completeAsync = completeAsync;
            _promptComposer = new AgentPromptComposer();
            _responseParser = new AgentResponseParser();
            _contextCompactionService = contextCompactionService;
        }

        public Task<ChatTurnResult> ExecuteAsync(
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
            return ExecuteAsync(text, session, documentContext, settings, tools, null, progress,
                pendingToolRegistrar, skills, cancellationToken, true);
        }

        public Task<ChatTurnResult> ExecuteAsync(
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
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = text ?? string.Empty,
                    HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId,
                    Attachments = attachments == null
                        ? new List<ChatAttachment>()
                        : new List<ChatAttachment>(attachments)
                });
            }
            return RunLoopAsync(text, session, documentContext, settings, tools, attachments, progress,
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RunLoopAsync(LatestUserRequest(session), session, documentContext, settings, tools, attachments,
                progress, pendingToolRegistrar, skills, confirmedCommand, confirmedResult, cancellationToken);
        }

        private async Task<ChatTurnResult> RunLoopAsync(
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
            CancellationToken cancellationToken)
        {
            settings = settings ?? new AppSettings();
            var availableTools = PrepareTools(tools);
            var enabledSkills = (skills ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList();
            var messages = await BuildMessagesAsync(text, session, documentContext, settings, availableTools,
                enabledSkills, attachments, initialCommand != null && initialResult != null, progress, cancellationToken).ConfigureAwait(false);
            var results = new List<object>();
            var toolSteps = 0;
            object contextUsage = null;
            var runCache = new LlmRunCache();

            if (initialCommand != null && initialResult != null)
            {
                var confirmed = AgentJsonProtocol.CreateToolResultMessage(initialCommand, initialResult);
                session.Messages.Add(confirmed);
                messages.Add(confirmed);
                results.Add(AgentTranscript.DescribeResult(initialCommand, initialResult));
                toolSteps += Math.Max(1, initialResult.ToolStepsConsumed);
            }

            for (var iteration = 0; iteration < Math.Max(1, settings.MaxAgentIterations); iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, "thinking", "Агент выбирает следующий шаг...", null);
                var options = new LlmRequestOptions
                {
                    ResponseFormat = LlmResponseFormats.JsonObject,
                    ReasoningEnabled = session == null ? (bool?)null : session.ReasoningEnabled,
                    RunCache = runCache
                };
                var completion = await CompleteAsync(settings, messages, options, progress, cancellationToken).ConfigureAwait(false);
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings,
                    completion == null ? null : completion.PromptTokens, options);
                var parsed = _responseParser.Parse(completion == null ? null : completion.Content, availableTools);
                if (!parsed.Success)
                {
                    return FinishWithDiagnostic(session, results, contextUsage, completion,
                        "Ответ агента не выполнен: " + parsed.Error);
                }

                var response = parsed.Response;
                if (response.ToolCalls.Count == 0)
                {
                    var finalText = response.Message.Trim();
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(finalText, completion));
                    return Result(finalText, results, contextUsage);
                }

                if (!string.IsNullOrWhiteSpace(response.Message))
                {
                    Report(progress, "acting", response.Message.Trim(), null);
                }
                for (var callIndex = 0; callIndex < response.ToolCalls.Count; callIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var call = response.ToolCalls[callIndex];
                    var command = AgentJsonProtocol.ToCommand(call);
                    var callMessage = AgentJsonProtocol.CreateToolCallMessage(
                        call,
                        callIndex == 0 ? response.Message : string.Empty,
                        callIndex == 0 ? completion : null);
                    session.Messages.Add(callMessage);
                    messages.Add(callMessage);

                    ToolResult toolResult;
                    if (toolSteps >= Math.Max(1, settings.MaxAgentToolSteps))
                    {
                        toolResult = ToolResult.Fail("Agent tool step limit reached.", null, "tool_step_limit_reached", false);
                    }
                    else if (!settings.AutoRunToolCalls)
                    {
                        toolResult = ToolResult.SkippedAutoRun("Automatic tool execution is disabled.");
                    }
                    else
                    {
                        toolResult = _toolExecutor.Execute(
                            command,
                            availableTools,
                            settings,
                            false,
                            false,
                            session,
                            Math.Max(1, settings.MaxAgentToolSteps - toolSteps),
                            enabledSkills,
                            cancellationToken) ?? ToolResult.Fail("Tool returned no result.", null, "missing_result", true);
                    }
                    toolSteps += Math.Max(1, toolResult.ToolStepsConsumed);
                    if (AgentTranscript.IsWaitingResult(toolResult) && pendingToolRegistrar != null)
                    {
                        toolResult.PendingId = pendingToolRegistrar(session, command, toolResult);
                    }

                    if (!AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var resultMessage = AgentJsonProtocol.CreateToolResultMessage(command, toolResult);
                        session.Messages.Add(resultMessage);
                        messages.Add(resultMessage);
                    }
                    var activityMessage = AgentTranscript.CreateLocalResultMessage(command, toolResult);
                    activityMessage.HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId;
                    session.Messages.Add(activityMessage);
                    results.Add(AgentTranscript.DescribeResult(command, toolResult));
                    Report(progress, "tool_result", toolResult.Message, activityMessage.Activity);

                    if (AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var waitingText = string.IsNullOrWhiteSpace(response.Message) ? toolResult.Message : response.Message.Trim();
                        return Result(waitingText, results, contextUsage);
                    }
                    if (string.Equals(toolResult.ErrorCode, "tool_step_limit_reached", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            var limitText = "Агент остановлен: достигнут лимит шагов.";
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(limitText, null));
            return Result(limitText, results, contextUsage);
        }

        private async Task<List<ChatMessage>> BuildMessagesAsync(
            string text,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills,
            IReadOnlyList<ChatAttachment> attachments,
            bool replayCurrentUserInHistory,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                return _promptComposer.BuildMessages(
                    text, _adapter, tools, skills, context, settings, session, attachments, replayCurrentUserInHistory);
            }
            catch (PromptBudgetExceededException ex) when (
                ex.CanCompact && settings.AutoCompressContext && _contextCompactionService != null)
            {
                var checkpoint = await _contextCompactionService.EnsureWithinBudgetAsync(
                    session, settings, string.Empty, true, progress, cancellationToken).ConfigureAwait(false);
                if (checkpoint == null) throw;
                return _promptComposer.BuildMessages(
                    text, _adapter, tools, skills, context, settings, session, attachments, replayCurrentUserInHistory);
            }
        }

        private async Task<LlmCompletionResult> CompleteAsync(
            AppSettings settings,
            IReadOnlyList<ChatMessage> messages,
            LlmRequestOptions options,
            Action<string, string, ChatActivity> progress,
            CancellationToken cancellationToken)
        {
            var reasoning = new StringBuilder();
            var completion = await _completeAsync(settings, messages, options, update =>
            {
                if (update == null) return;
                if (!string.IsNullOrEmpty(update.ReasoningDelta)) reasoning.Append(update.ReasoningDelta);
                if (reasoning.Length == 0) return;
                if (reasoning.Length < 256 && !update.Completed) return;
                Report(progress, "thinking", "Агент анализирует запрос...", new ChatActivity
                {
                    Kind = "reasoning",
                    Title = "Анализ",
                    Status = update.Completed ? "completed" : "running",
                    ResultMessage = reasoning.ToString()
                });
                reasoning.Clear();
            }, cancellationToken).ConfigureAwait(false);
            if (completion == null) throw new InvalidOperationException("Model returned no completion.");
            return completion;
        }

        private static List<ToolDefinition> PrepareTools(IEnumerable<ToolDefinition> tools)
        {
            var source = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
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
                if (!ToolSchemaSupport.TryNormalize(tool, out schema, out schemaError)) continue;
                if (!string.IsNullOrWhiteSpace(tool.CapabilityStatus) &&
                    !string.Equals(tool.CapabilityStatus, "available", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tool.CapabilityStatus, "partial", StringComparison.OrdinalIgnoreCase)) continue;
                tool.MutatesDocument = profile.MutatesDocument;
                tool.MutatesLocalState = profile.MutatesLocalState;
                tool.RequiresConfirmation = profile.RequiresConfirmation;
                tool.RiskLevel = profile.RiskLevel;
                result.Add(tool);
            }
            return result.OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ChatTurnResult FinishWithDiagnostic(
            ChatSession session,
            IReadOnlyList<object> results,
            object contextUsage,
            LlmCompletionResult completion,
            string text)
        {
            var activity = new ChatActivity
            {
                Kind = "diagnostic",
                Title = "Некорректный ответ агента",
                Status = "failed",
                ExecutionStatus = "invalid_agent_response",
                ResultMessage = text
            };
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(text, completion, activity));
            return Result(text, results, contextUsage);
        }

        private static ChatTurnResult Result(string text, IReadOnlyList<object> results, object contextUsage)
        {
            return new ChatTurnResult
            {
                AssistantText = text ?? string.Empty,
                ToolResults = results ?? new object[0],
                ContextUsage = contextUsage
            };
        }

        private static string LatestUserRequest(ChatSession session)
        {
            var message = (session == null ? null : session.Messages ?? new List<ChatMessage>())
                .LastOrDefault(item => item != null && !item.ProtocolMessage &&
                    string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
            return message == null ? string.Empty : message.Content ?? string.Empty;
        }

        private static void Report(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null) progress(phase, message ?? string.Empty, activity);
        }
    }
}
