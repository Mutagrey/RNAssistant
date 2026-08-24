using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
        public bool WaitingForConfirmation { get; set; }
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
            CancellationToken cancellationToken = default(CancellationToken),
            int initialIterationsUsed = 0,
            int initialToolStepsUsed = 0)
        {
            return RunLoopAsync(LatestUserRequest(session), session, documentContext, settings, tools, attachments,
                progress, pendingToolRegistrar, skills, confirmedCommand, confirmedResult, cancellationToken,
                initialIterationsUsed, initialToolStepsUsed);
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
            CancellationToken cancellationToken,
            int initialIterationsUsed = 0,
            int initialToolStepsUsed = 0)
        {
            settings = settings ?? new AppSettings();
            var availableTools = PrepareToolsForRun(tools);
            var enabledSkills = (skills ?? new SkillDefinition[0]).Where(skill => skill != null && skill.Enabled).ToList();
            var messages = await BuildMessagesAsync(text, session, documentContext, settings, availableTools,
                enabledSkills, attachments, initialCommand != null && initialResult != null, progress, cancellationToken).ConfigureAwait(false);
            var results = new List<object>();
            var toolSteps = Math.Max(0, initialToolStepsUsed);
            var iterationsUsed = Math.Max(0, initialIterationsUsed);
            object contextUsage = null;
            var runCache = new LlmRunCache();
            var responseMode = AgentResponseModes.Normalize(settings.AgentResponseMode);

            if (initialCommand != null && initialResult != null)
            {
                var confirmed = CreateBoundedToolResultMessage(initialCommand, initialResult, messages, settings);
                session.Messages.Add(confirmed);
                messages.Add(confirmed);
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
                Report(progress, "thinking", "Агент выбирает следующий шаг...", null);
                var options = BuildRequestOptions(responseMode, availableTools, session, runCache);
                string budgetError;
                if (!TryValidatePromptBudget(messages, settings, options, out budgetError))
                {
                    return FinishWithDiagnostic(session, results, contextUsage, budgetError,
                        "Контекст переполнен", "prompt_budget_exceeded");
                }
                LlmCompletionResult completion;
                try
                {
                    completion = await CompleteAsync(settings, messages, options, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (LlmRequestException ex) when (
                    ex.Kind == LlmFailureKind.ResponseFormatUnsupported &&
                    string.Equals(responseMode, AgentResponseModes.JsonSchema, StringComparison.Ordinal) &&
                    settings.FallbackToJsonObject)
                {
                    responseMode = AgentResponseModes.JsonObject;
                    options = BuildRequestOptions(responseMode, availableTools, session, runCache);
                    Report(progress, "thinking", "Endpoint не поддерживает json_schema; продолжаю с json_object.", null);
                    if (!TryValidatePromptBudget(messages, settings, options, out budgetError))
                    {
                        return FinishWithDiagnostic(session, results, contextUsage, budgetError,
                            "Контекст переполнен", "prompt_budget_exceeded");
                    }
                    completion = await CompleteAsync(settings, messages, options, progress, cancellationToken).ConfigureAwait(false);
                }
                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings,
                    completion == null ? null : completion.PromptTokens, options);
                string refusal;
                if (TryGetRefusal(completion, out refusal))
                {
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(refusal, completion));
                    return Result(refusal, results, contextUsage, false);
                }
                var parsed = _responseParser.Parse(
                    completion == null ? null : completion.Content,
                    availableTools);
                var configuredFormatRetries = settings.MaxAgentFormatRetries > 0
                    ? settings.MaxAgentFormatRetries
                    : new AppSettings().MaxAgentFormatRetries;
                var maxFormatRetries = Math.Max(
                    1,
                    Math.Min(AppSettings.MaximumAgentFormatRetries, configuredFormatRetries));
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
                    if (TryGetRefusal(completion, out refusal))
                    {
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(refusal, completion));
                        return Result(refusal, results, contextUsage, false);
                    }
                    parsed = _responseParser.Parse(
                        completion == null ? null : completion.Content,
                        availableTools);
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
                    return Result(finalText, results, contextUsage, false);
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
                        callIndex == 0 ? completion : null,
                        settings.ToolResultRole);
                    session.Messages.Add(callMessage);
                    messages.Add(callMessage);

                    var activityMessage = new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        ExcludeFromModelContext = true,
                        HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId,
                        Activity = AgentTranscript.CreateRunningToolActivity(command, stepId, stepMessage)
                    };
                    session.Messages.Add(activityMessage);
                    Report(progress, "tool_running",
                        string.IsNullOrWhiteSpace(stepMessage) ? "Выполняю действие" : stepMessage,
                        activityMessage.Activity);

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
                    UpdateRunCursor(session, iterationsUsed, toolSteps, "running", "tool_result");
                    if (AgentTranscript.IsWaitingResult(toolResult) && pendingToolRegistrar != null)
                    {
                        toolResult.ConfirmationCatalogSha256 = ToolExecutionFingerprint(
                            availableTools,
                            command.ToolId);
                        toolResult.PendingId = pendingToolRegistrar(session, command, toolResult);
                    }

                    if (!AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var resultMessage = CreateBoundedToolResultMessage(command, toolResult, messages, settings);
                        session.Messages.Add(resultMessage);
                        messages.Add(resultMessage);
                    }
                    var completedActivityMessage = AgentTranscript.CreateLocalResultMessage(command, toolResult, stepId, stepMessage);
                    activityMessage.Content = completedActivityMessage.Content;
                    activityMessage.Activity = completedActivityMessage.Activity;
                    activityMessage.HtmlWorkspaceCheckpointId = session.ActiveHtmlArtifactId;
                    results.Add(AgentTranscript.DescribeResult(command, toolResult));

                    if (AgentTranscript.IsWaitingResult(toolResult))
                    {
                        var waitingText = string.IsNullOrWhiteSpace(response.Message) ? toolResult.Message : response.Message.Trim();
                        UpdateRunCursor(session, iterationsUsed, toolSteps,
                            "waiting_confirmation", "waiting_confirmation");
                        Report(progress, "tool_result", toolResult.Message, activityMessage.Activity);
                        return Result(waitingText, results, contextUsage, true);
                    }
                    Report(progress, "tool_result", toolResult.Message, activityMessage.Activity);
                    if (string.Equals(toolResult.ErrorCode, "tool_step_limit_reached", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            var limitText = "Агент остановлен: достигнут лимит шагов.";
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(limitText, null));
            return Result(limitText, results, contextUsage, false);
        }

        internal static LlmRequestOptions BuildRequestOptions(
            string responseMode,
            IReadOnlyList<ToolDefinition> tools,
            ChatSession session,
            LlmRunCache runCache)
        {
            var jsonSchema = string.Equals(
                AgentResponseModes.Normalize(responseMode),
                AgentResponseModes.JsonSchema,
                StringComparison.Ordinal);
            return new LlmRequestOptions
            {
                ResponseFormat = jsonSchema ? LlmResponseFormats.JsonSchema : LlmResponseFormats.JsonObject,
                ResponseSchemaName = jsonSchema ? AgentResponseSchemaBuilder.SchemaName : null,
                ResponseSchemaJson = jsonSchema ? AgentResponseSchemaBuilder.Build(tools) : null,
                ReasoningEnabled = session == null ? (bool?)null : session.ReasoningEnabled,
                RunCache = runCache
            };
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
                tool.MutatesDocument = profile.MutatesDocument;
                tool.MutatesLocalState = profile.MutatesLocalState;
                tool.RequiresConfirmation = profile.RequiresConfirmation;
                tool.RiskLevel = profile.RiskLevel;
                result.Add(tool);
            }
            RemovePipelinesWithOmittedDependencies(result);
            return result.OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string ToolExecutionFingerprint(IEnumerable<ToolDefinition> tools, string rootToolId)
        {
            var catalog = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var selected = new List<ToolDefinition>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(rootToolId ?? string.Empty);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                ToolDefinition tool;
                if (!visited.Add(id)) continue;
                if (!catalog.TryGetValue(id, out tool)) return string.Empty;
                selected.Add(tool);
                if (!string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase)) continue;

                PipelineDefinition pipeline;
                string error;
                if (!PipelineDefinitionParser.TryParse(tool.Id, tool.PipelineJson, out pipeline, out error))
                {
                    return string.Empty;
                }
                foreach (var step in pipeline.Steps) pending.Push(step.ToolId);
            }

            var canonical = selected
                .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
                .Select(tool => new
                {
                    tool.Id,
                    tool.BuiltIn,
                    tool.Scope,
                    tool.ArgumentSchemaJson,
                    tool.Executor,
                    tool.PipelineJson,
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

        private static void RemovePipelinesWithOmittedDependencies(List<ToolDefinition> tools)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                var ids = new HashSet<string>(tools.Select(tool => tool.Id), StringComparer.OrdinalIgnoreCase);
                for (var index = tools.Count - 1; index >= 0; index--)
                {
                    var tool = tools[index];
                    if (!string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase)) continue;
                    PipelineDefinition pipeline;
                    string error;
                    if (!PipelineDefinitionParser.TryParse(tool.Id, tool.PipelineJson, out pipeline, out error) ||
                        pipeline.Steps.Any(step => !ids.Contains(step.ToolId)))
                    {
                        tools.RemoveAt(index);
                        changed = true;
                    }
                }
            }
        }

        private static bool ValidToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsWhiteSpace);
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
            return Result(text, results, contextUsage, false);
        }

        private static ChatTurnResult Result(
            string text,
            IReadOnlyList<object> results,
            object contextUsage,
            bool waitingForConfirmation)
        {
            return new ChatTurnResult
            {
                AssistantText = text ?? string.Empty,
                ToolResults = results ?? new object[0],
                ContextUsage = contextUsage,
                WaitingForConfirmation = waitingForConfirmation
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

        private static bool TryGetRefusal(LlmCompletionResult completion, out string refusal)
        {
            refusal = completion == null ? string.Empty : completion.RefusalContent ?? string.Empty;
            return string.IsNullOrWhiteSpace(completion == null ? null : completion.Content) &&
                !string.IsNullOrWhiteSpace(refusal);
        }

        private static ChatMessage CreateBoundedToolResultMessage(
            ToolCommand command,
            ToolResult result,
            IReadOnlyList<ChatMessage> messages,
            AppSettings settings)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var used = ModelContextBudget.EstimateMessagesTokens(messages, settings);
            var availableForData = Math.Max(0, inputBudget - used - ToolResultEnvelopeReserveTokens);
            var toolId = command == null ? null : command.ToolId;
            var maxDataTokens = string.Equals(toolId, "common.skills_read", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolId, HtmlArtifactToolExecutor.ReadWorkspaceToolId, StringComparison.OrdinalIgnoreCase)
                    ? availableForData
                    : Math.Min(AgentJsonProtocol.DefaultMaxToolResultDataTokens, availableForData);
            return AgentJsonProtocol.CreateToolResultMessage(command, result, maxDataTokens, settings.ToolResultRole, settings);
        }

        private static bool TryValidatePromptBudget(
            IReadOnlyList<ChatMessage> messages,
            AppSettings settings,
            LlmRequestOptions options,
            out string error)
        {
            var inputBudget = ModelContextBudget.InputBudgetTokens(settings);
            var estimated = ModelContextBudget.EstimateMessagesTokens(messages, settings) +
                ModelContextBudget.EstimateRequestOptionsTokens(options, settings);
            if (estimated <= inputBudget)
            {
                error = null;
                return true;
            }

            error = "Агент остановлен до следующего запроса модели: контекст занимает ≈" + estimated +
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
