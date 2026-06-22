using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class AgentRunService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> _completeAsync;
        private readonly PromptComposer _promptComposer;
        private readonly ToolCommandParser _commandParser;

        public AgentRunService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _completeAsync = completeAsync;
            _promptComposer = new PromptComposer();
            _commandParser = new ToolCommandParser();
        }

        public async Task<ChatCompletionResult> RunUserTurnAsync(
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken)
        {
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });
            return await RunLoopAsync(text, null, false, session, documentContext, settings, tools, progress, pendingToolRegistrar, skills, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ChatCompletionResult> ContinueAfterToolAsync(
            ToolCommand confirmedCommand,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken)
        {
            var prompt = PromptText(settings, p => p.ConfirmedToolContinuationPrompt);
            return await RunLoopAsync(prompt, prompt, CommandMutates(confirmedCommand, tools), session, documentContext, settings, tools, progress, pendingToolRegistrar, skills, cancellationToken).ConfigureAwait(false);
        }

        public bool CommandMutates(ToolCommand command, IReadOnlyList<ToolDefinition> tools)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return false;
            }

            var tool = (tools ?? new ToolDefinition[0]).FirstOrDefault(t =>
                t != null && string.Equals(t.Id, command.ToolId, StringComparison.OrdinalIgnoreCase));
            return ToolSafetyPolicy.EffectiveMutatesDocument(tool, tools);
        }

        private async Task<ChatCompletionResult> RunLoopAsync(
            string taskText,
            string initialFollowUpPrompt,
            bool initialVerificationRequired,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "context", "Читаю документ...");
            settings = settings ?? new AppSettings();
            tools = tools ?? new ToolDefinition[0];

            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                CaptureDocumentSnapshot(settings),
                CaptureVbaSnapshot(settings, taskText),
                tools,
                skills,
                null);
            if (session != null && session.HtmlModeEnabled)
            {
                systemPrompt += "\n\nHTML MODE IS ENABLED FOR THIS CHAT.\n" +
                    "Treat this turn as an HTML workspace task unless the user explicitly says otherwise. " +
                    "For an existing HTML workspace, call common.html_workspace_read before editing. " +
                    "Create or update files with common.html_workspace_upsert_file using kind html, css, or script. " +
                    "Create or update dynamic JSON data with common.html_workspace_upsert_data; preview exposes it as window.RNAssistantData[name]. " +
                    "Do not use inline chat HTML artifact tools in HTML mode.";
            }
            var contextPrompt = _promptComposer.ComposeContextPrompt(documentContext);
            if (!string.IsNullOrWhiteSpace(contextPrompt) && documentContext != null)
            {
                ReportProgress(progress, "context", "Добавленный контекст включен в запрос: " + documentContext.Notes.Count + " item(s).");
            }

            object contextUsage = null;
            var assistantText = string.Empty;
            var resultLog = new List<object>();
            var followUpPrompt = initialFollowUpPrompt;
            var lastResponseWasToolBlock = false;
            var totalToolSteps = 0;
            var verificationExpected = initialVerificationRequired && settings.RequireVerificationForMutations != false;
            var verificationPromptSent = initialVerificationRequired;
            var maxIterations = Math.Max(1, settings.MaxAgentIterations);
            var maxToolSteps = Math.Max(1, settings.MaxAgentToolSteps);

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var messages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
                if (!string.IsNullOrWhiteSpace(followUpPrompt))
                {
                    messages.Add(new ChatMessage { Role = "user", Content = followUpPrompt });
                }

                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", iteration == 0 ? "Модель думает..." : "Модель продолжает агентскую задачу...");
                var completion = await _completeAsync(settings, messages, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                assistantText = completion.Content ?? string.Empty;

                ReportProgress(progress, "processing", "Разбираю ответ...");
                var parseResult = _commandParser.ParseWithDiagnostics(assistantText);
                var commands = parseResult.Commands.ToList();
                if (commands.Count == 0)
                {
                    lastResponseWasToolBlock = false;
                    if (iteration == 0 && settings.AgentModeEnabled != false && AgentTranscript.ShouldForceAgentToolUse(taskText, _adapter.HostName))
                    {
                        followUpPrompt = parseResult.HasProtocolDiagnostics
                            ? PromptText(settings, p => p.RepairMalformedToolBlockPrompt)
                            : PromptText(settings, p => p.ForceToolUsePrompt);
                        continue;
                    }

                    if (verificationExpected && settings.RequireVerificationForMutations != false && !verificationPromptSent)
                    {
                        followUpPrompt = VerificationPrompt(settings);
                        verificationPromptSent = true;
                        continue;
                    }

                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
                    break;
                }

                if (settings.AgentModeEnabled == false)
                {
                    assistantText = "Agent mode is disabled; returned tool block was not executed.";
                    var disabledActivity = AgentTranscript.CreateAgentPlanActivity(commands, parseResult.Diagnostics);
                    disabledActivity.Title = "Agent mode disabled";
                    disabledActivity.Status = "skipped";
                    disabledActivity.ExecutionStatus = "agent_disabled";
                    disabledActivity.ResultMessage = assistantText;
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion, disabledActivity));
                    lastResponseWasToolBlock = false;
                    break;
                }

                lastResponseWasToolBlock = true;
                var planActivity = AgentTranscript.CreateAgentPlanActivity(commands, parseResult.Diagnostics);
                ReportProgress(progress, "plan", "Агент подготовил план: " + commands.Count + " step(s).", planActivity);
                session.Messages.Add(AgentTranscript.CreateAssistantMessage(AgentTranscript.CreateAgentPlanMessage(commands), completion, planActivity));
                var shouldContinue = settings.AutoRunToolCalls != false;
                var mutationExecutedThisIteration = false;
                for (var i = 0; i < commands.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalToolSteps >= maxToolSteps)
                    {
                        var limitResult = ToolResult.Fail("Agent tool step limit exceeded: " + maxToolSteps + ".");
                        resultLog.Add(new { toolId = "agent.step_limit", success = false, status = limitResult.Status, message = limitResult.Message });
                        session.Messages.Add(AgentTranscript.CreateAssistantMessage(limitResult.Message, null, new ChatActivity
                        {
                            Kind = "diagnostic",
                            Title = "Agent step limit",
                            Status = "failed",
                            ExecutionStatus = "step_limit",
                            ResultMessage = limitResult.Message
                        }));
                        shouldContinue = false;
                        break;
                    }

                    totalToolSteps += 1;
                    var command = commands[i];
                    ReportProgress(
                        progress,
                        settings.AutoRunToolCalls != false ? "executing" : "waiting",
                        (settings.AutoRunToolCalls != false ? "Исполняю tool " : "Auto-run отключен для tool ") + (i + 1) + "/" + commands.Count + ": " + command.ToolId,
                        CreateRunningActivity(command, settings.AutoRunToolCalls != false ? "running" : "waiting", "tool"));
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = settings.AutoRunToolCalls != false
                        ? _toolExecutor.Execute(command, tools, settings, false, false, session, cancellationToken)
                        : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + command.ToolId);
                    AttachPendingId(session, command, result, pendingToolRegistrar);
                    ReportProgress(progress, result.Success ? "completed" : (AgentTranscript.IsWaitingResult(result) ? "waiting" : "failed"), result.Message, AgentTranscript.CreateToolActivity(command, result, "tool"));

                    var mutating = CommandMutates(command, tools);
                    if (result.Success && mutating)
                    {
                        mutationExecutedThisIteration = true;
                        verificationExpected = settings.RequireVerificationForMutations != false;
                        verificationPromptSent = false;
                    }
                    else if (result.Success && !mutating && verificationExpected && verificationPromptSent)
                    {
                        verificationExpected = false;
                    }

                    var commandCompleted = result.Success;
                    var retrySucceeded = false;
                    var retryAttempted = false;
                    var retryResultIndex = resultLog.Count;
                    var retrySessionIndex = session.Messages.Count;
                    if (!result.Success && settings.AutoRunToolCalls != false && settings.AutoRetryToolErrors != false && AgentTranscript.CanRetryToolError(result))
                    {
                        retryAttempted = true;
                        ReportProgress(progress, "repairing", "Tool упал, прошу модель исправить вызов: " + command.ToolId);
                        var retry = await RetryFailedToolAsync(systemPrompt, contextPrompt, session, settings, tools, command, result, resultLog, progress, pendingToolRegistrar, cancellationToken).ConfigureAwait(false);
                        commandCompleted = retry.Success;
                        retrySucceeded = retry.Success;
                        if (retry.Mutated)
                        {
                            mutationExecutedThisIteration = true;
                            verificationExpected = settings.RequireVerificationForMutations != false;
                            verificationPromptSent = false;
                        }
                    }
                    if (!retrySucceeded)
                    {
                        var resultEntry = AgentTranscript.DescribeResult(command, result);
                        var resultMessage = AgentTranscript.CreateLocalResultMessage(command, result);
                        if (retryAttempted && retryResultIndex >= 0 && retryResultIndex <= resultLog.Count)
                        {
                            resultLog.Insert(retryResultIndex, resultEntry);
                        }
                        else
                        {
                            resultLog.Add(resultEntry);
                        }
                        if (retryAttempted && retrySessionIndex >= 0 && retrySessionIndex <= session.Messages.Count)
                        {
                            session.Messages.Insert(retrySessionIndex, resultMessage);
                        }
                        else
                        {
                            session.Messages.Add(resultMessage);
                        }
                    }
                    if (!commandCompleted)
                    {
                        shouldContinue = false;
                        break;
                    }
                }

                if (!shouldContinue)
                {
                    break;
                }

                followUpPrompt = mutationExecutedThisIteration && settings.RequireVerificationForMutations != false
                    ? VerificationPrompt(settings)
                    : PromptText(settings, p => p.AfterToolResultsPrompt);
                if (mutationExecutedThisIteration && settings.RequireVerificationForMutations != false)
                {
                    verificationPromptSent = true;
                }
            }

            if (lastResponseWasToolBlock)
            {
                assistantText = AgentTranscript.CreateRunSummary(resultLog);
            }

            return new ChatCompletionResult
            {
                AssistantText = assistantText,
                ToolResults = resultLog,
                ContextUsage = contextUsage ?? ContextUsageEstimator.FromSession(session, settings)
            };
        }

        private async Task<RetryResult> RetryFailedToolAsync(
            string systemPrompt,
            string contextPrompt,
            ChatSession session,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            ToolCommand failedCommand,
            ToolResult failedResult,
            ICollection<object> resultLog,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failedDataJson = failedResult == null ? null : failedResult.DataJson;
            var dataJsonBlock = string.IsNullOrWhiteSpace(failedDataJson)
                ? string.Empty
                : "Data:\n```json\n" + failedDataJson + "\n```";
            var repairPrompt = RenderPrompt(PromptText(settings, p => p.RetryFailedToolPrompt), new Dictionary<string, string>
            {
                ["availableToolIds"] = AvailableToolIdsText(tools),
                ["toolId"] = failedCommand == null ? string.Empty : failedCommand.ToolId,
                ["argumentsJson"] = JsonConvert.SerializeObject(failedCommand == null ? null : failedCommand.Arguments, Formatting.Indented),
                ["error"] = failedResult == null ? string.Empty : failedResult.Message,
                ["dataJsonBlock"] = dataJsonBlock
            });
            var repairMessages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
            repairMessages.Add(new ChatMessage { Role = "user", Content = repairPrompt });

            var repairCompletion = await _completeAsync(settings, repairMessages, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var repairText = repairCompletion.Content ?? string.Empty;
            var retryCommands = _commandParser.Parse(repairText).ToList();
            var anySuccess = false;
            var allSucceeded = retryCommands.Count > 0;
            var mutated = false;
            for (var i = 0; i < retryCommands.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retry = retryCommands[i];
                ReportProgress(progress, "retrying", "Повтор tool " + (i + 1) + "/" + retryCommands.Count + ": " + retry.ToolId, CreateRunningActivity(retry, "running", "retry"));
                var retryResult = _toolExecutor.Execute(retry, tools, settings, false, false, session, cancellationToken);
                AttachPendingId(session, retry, retryResult, pendingToolRegistrar);
                if (resultLog != null)
                {
                    resultLog.Add(AgentTranscript.DescribeResult(retry, retryResult));
                }
                AgentTranscript.AddLocalResultMessage(session, retry, retryResult);
                ReportProgress(progress, retryResult.Success ? "completed" : (AgentTranscript.IsWaitingResult(retryResult) ? "waiting" : "failed"), retryResult.Message, AgentTranscript.CreateToolActivity(retry, retryResult, "retry"));
                if (retryResult.Success)
                {
                    anySuccess = true;
                    mutated = mutated || CommandMutates(retry, tools);
                }
                else
                {
                    allSucceeded = false;
                    break;
                }
            }

            if (retryCommands.Count == 0)
            {
                var noCommand = ToolResult.Fail("Auto-retry did not return a corrected tool call.");
                if (resultLog != null)
                {
                    resultLog.Add(new { toolId = "auto-retry", success = false, message = noCommand.Message, dataJson = noCommand.DataJson });
                }
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Local skill retry result: " + noCommand.Message });
            }

            return new RetryResult { Success = anySuccess && allSucceeded, Mutated = mutated };
        }

        private string CaptureDocumentSnapshot(AppSettings settings)
        {
            try
            {
                return _adapter.GetDocumentSnapshot((settings ?? new AppSettings()).ContextCharLimit);
            }
            catch (Exception ex)
            {
                return "Document snapshot could not be read: " + ex.Message;
            }
        }

        private string CaptureVbaSnapshot(AppSettings settings, string taskText)
        {
            settings = settings ?? new AppSettings();
            if (!settings.IncludeVbaContext && !LooksLikeVbaTask(taskText))
            {
                return string.Empty;
            }

            try
            {
                return _adapter.GetVbaSnapshot(Math.Max(1000, settings.VbaContextCharLimit));
            }
            catch (Exception ex)
            {
                return "VBA project snapshot could not be read: " + ex.Message;
            }
        }

        private static bool LooksLikeVbaTask(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant();
            return value.IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макрос", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("макро", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("visual basic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string VerificationPrompt(AppSettings settings)
        {
            return PromptText(settings, p => p.VerifyMutationPrompt);
        }

        private static string PromptText(AppSettings settings, Func<AgentPromptSettings, string> selector)
        {
            var defaults = new AgentPromptSettings();
            var prompts = settings == null || settings.AgentPrompts == null
                ? defaults
                : settings.AgentPrompts;
            var value = selector(prompts);
            return string.IsNullOrWhiteSpace(value) ? selector(defaults) : value;
        }

        private static string RenderPrompt(string template, IDictionary<string, string> values)
        {
            var result = template ?? string.Empty;
            foreach (var item in values ?? new Dictionary<string, string>())
            {
                result = result.Replace("{{" + item.Key + "}}", item.Value ?? string.Empty);
            }

            return result;
        }

        private static string AvailableToolIdsText(IEnumerable<ToolDefinition> tools)
        {
            var ids = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && (tool.AgentCanRun || tool.MutatesDocument || tool.RequiresConfirmation) && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => tool.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id)
                .ToArray();

            return ids.Length == 0 ? "none" : string.Join(", ", ids);
        }

        private static void AttachPendingId(ChatSession session, ToolCommand command, ToolResult result, ChatCompletionService.PendingToolRegistrar pendingToolRegistrar)
        {
            if (!AgentTranscript.IsWaitingResult(result) || pendingToolRegistrar == null)
            {
                return;
            }

            result.PendingId = pendingToolRegistrar(session, command, result);
        }

        private static ChatActivity CreateRunningActivity(ToolCommand command, string status, string kind)
        {
            return new ChatActivity
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "tool" : kind,
                Title = command == null || string.IsNullOrWhiteSpace(command.Description)
                    ? (command == null ? "Tool step" : command.ToolId)
                    : command.Description,
                Subtitle = command == null ? string.Empty : command.ToolId,
                Status = status,
                ExecutionStatus = status,
                ToolId = command == null ? string.Empty : command.ToolId,
                ArgumentsJson = command == null ? null : JsonConvert.SerializeObject(command.Arguments, Formatting.Indented)
            };
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            ReportProgress(progress, phase, message, null);
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message, activity);
            }
        }

        private sealed class RetryResult
        {
            public bool Success { get; set; }
            public bool Mutated { get; set; }
        }
    }
}
