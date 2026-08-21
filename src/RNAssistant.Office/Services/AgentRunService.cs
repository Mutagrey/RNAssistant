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
        private const int ToolResultEnvelopeReserveTokens = 1200;

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
                var confirmed = CreateBoundedToolResultMessage(initialCommand, initialResult, messages, settings);
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
                string budgetError;
                if (!TryValidatePromptBudget(messages, settings, options, out budgetError))
                {
                    return FinishWithDiagnostic(session, results, contextUsage, budgetError,
                        "Контекст переполнен", "prompt_budget_exceeded");
                }
                var completion = await CompleteAsync(settings, messages, options, progress, cancellationToken).ConfigureAwait(false);
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings,
                    completion == null ? null : completion.PromptTokens, options);
                var parsed = _responseParser.Parse(completion == null ? null : completion.Content, availableTools);
                var configuredFormatRetries = settings.MaxAgentFormatRetries > 0
                    ? settings.MaxAgentFormatRetries
                    : new AppSettings().MaxAgentFormatRetries;
                var maxFormatRetries = Math.Max(1, Math.Min(5, configuredFormatRetries));
                for (var retry = 1; !parsed.Success && retry <= maxFormatRetries; retry++)
                {
                    Report(progress, "thinking", "Модель исправляет формат ответа... (" + retry + "/" + maxFormatRetries + ")", null);
                    var repairMessages = new List<ChatMessage>(messages)
                    {
                        AgentJsonProtocol.CreateFormatRepairMessage(parsed.Error, retry, maxFormatRetries)
                    };
                    if (!TryValidatePromptBudget(repairMessages, settings, options, out budgetError))
                    {
                        return FinishWithDiagnostic(session, results, contextUsage, budgetError,
                            "Контекст переполнен", "prompt_budget_exceeded");
                    }
                    completion = await CompleteAsync(settings, repairMessages, options, progress, cancellationToken).ConfigureAwait(false);
                    contextUsage = ContextUsageEstimator.FromPrompt(repairMessages, settings,
                        completion == null ? null : completion.PromptTokens, options);
                    parsed = _responseParser.Parse(completion == null ? null : completion.Content, availableTools);
                }
                if (!parsed.Success)
                {
                    return FinishWithDiagnostic(session, results, contextUsage,
                        "Ответ агента не выполнен после " + maxFormatRetries + " попыток исправить формат: " + parsed.Error);
                }

                var response = parsed.Response;
                if (response.ToolCalls.Count == 0)
                {
                    var finalText = response.Message.Trim();
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(finalText, completion));
                    return Result(finalText, results, contextUsage);
                }

                var stepId = Guid.NewGuid().ToString("N");
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
                    var callMessage = AgentJsonProtocol.CreateToolCallMessage(
                        call,
                        callIndex == 0 ? response.Message : string.Empty,
                        callIndex == 0 ? completion : null);
                    session.Messages.Add(callMessage);
                    messages.Add(callMessage);

                    Report(progress, "tool_running", "Выполняю действие", AgentTranscript.CreateRunningToolActivity(
                        command, stepId, stepMessage));

                    ToolResult toolResult;
                    if (toolSteps >= Math.Max(1, settings.MaxAgentToolSteps))
                    {
                        toolResult = ToolResult.Fail("Agent tool step limit reached.", null, "tool_step_limit_reached", false);
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
                        var resultMessage = CreateBoundedToolResultMessage(command, toolResult, messages, settings);
                        session.Messages.Add(resultMessage);
                        messages.Add(resultMessage);
                    }
                    var activityMessage = AgentTranscript.CreateLocalResultMessage(command, toolResult, stepId, stepMessage);
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
                if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError)) continue;
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
            string text,
            string title = "Некорректный ответ агента",
            string executionStatus = "invalid_agent_response")
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

        private static ChatMessage CreateBoundedToolResultMessage(
            ToolCommand command,
            ToolResult result,
            IReadOnlyList<ChatMessage> messages,
            AppSettings settings)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var used = ModelContextBudget.EstimateMessagesTokens(messages);
            var availableForData = Math.Max(0, inputBudget - used - ToolResultEnvelopeReserveTokens);
            var maxDataTokens = Math.Min(AgentJsonProtocol.DefaultMaxToolResultDataTokens, availableForData);
            return AgentJsonProtocol.CreateToolResultMessage(command, result, maxDataTokens);
        }

        private static bool TryValidatePromptBudget(
            IReadOnlyList<ChatMessage> messages,
            AppSettings settings,
            LlmRequestOptions options,
            out string error)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var estimated = ModelContextBudget.EstimateMessagesTokens(messages) +
                ModelContextBudget.EstimateRequestOptionsTokens(options);
            if (estimated <= inputBudget)
            {
                error = null;
                return true;
            }

            error = "Агент остановлен до следующего запроса модели: контекст занимает примерно " + estimated +
                " токенов при доступном лимите " + inputBudget +
                ". Сузьте диапазон/объём результата или начните новый чат.";
            return false;
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
