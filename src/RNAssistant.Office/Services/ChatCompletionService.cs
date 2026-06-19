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
    public sealed class ChatCompletionResult
    {
        public string AssistantText { get; set; }
        public IReadOnlyList<object> ToolResults { get; set; }
        public object ContextUsage { get; set; }
    }

    public sealed class ChatCompletionService
    {
        private const int MaxAgentIterations = 3;
        public delegate string PendingToolRegistrar(ChatSession session, ToolCommand command, ToolResult result);

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> _completeAsync;
        private readonly PromptComposer _promptComposer;
        private readonly ToolCommandParser _commandParser;

        public ChatCompletionService(
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

        public async Task<ChatCompletionResult> ExecuteAsync(
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
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "context", "Читаю документ...");
            ApplyChatModel(settings, session);
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });

            var vbaSnapshot = string.Empty;
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                vbaSnapshot,
                tools,
                skills,
                null);
            var contextPrompt = _promptComposer.ComposeContextPrompt(documentContext);
            if (!string.IsNullOrWhiteSpace(contextPrompt))
            {
                ReportProgress(progress, "context", "Добавленный контекст включен в запрос: " + documentContext.Notes.Count + " item(s).");
            }

            object contextUsage = null;
            var assistantText = string.Empty;
            var resultLog = new List<object>();
            string followUpPrompt = null;
            var lastResponseWasToolBlock = false;
            for (var iteration = 0; iteration < MaxAgentIterations; iteration++)
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
                    if (iteration == 0 && settings.AgentModeEnabled != false && AgentTranscript.ShouldForceAgentToolUse(text, _adapter.HostName))
                    {
                        followUpPrompt = parseResult.HasProtocolDiagnostics
                            ? "Your previous response contained an RNAssistant tool block, but the local parser could not recover executable JSON. Return only one corrected ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. No prose."
                            : "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps. Copy toolId values exactly from the Available tools list. If a tool is missing, say that plainly instead of inventing one.";
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
                for (var i = 0; i < commands.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var command = commands[i];
                    ReportProgress(
                        progress,
                        settings.AutoRunToolCalls != false ? "executing" : "waiting",
                        (settings.AutoRunToolCalls != false ? "Исполняю tool " : "Auto-run отключен для tool ") + (i + 1) + "/" + commands.Count + ": " + command.ToolId,
                        CreateRunningActivity(command, settings.AutoRunToolCalls != false ? "running" : "waiting", "tool"));
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = settings.AutoRunToolCalls != false
                        ? _toolExecutor.Execute(command, tools, settings, false, false, cancellationToken)
                        : ToolResult.SkippedAutoRun("Auto tool execution is disabled: " + command.ToolId);
                    AttachPendingId(session, command, result, pendingToolRegistrar);
                    resultLog.Add(AgentTranscript.DescribeResult(command, result));
                    AgentTranscript.AddLocalResultMessage(session, command, result);
                    ReportProgress(progress, result.Success ? "completed" : (AgentTranscript.IsWaitingResult(result) ? "waiting" : "failed"), result.Message, AgentTranscript.CreateToolActivity(command, result, "tool"));
                    var commandCompleted = result.Success;
                    if (!result.Success && settings.AutoRunToolCalls != false && settings.AutoRetryToolErrors != false && AgentTranscript.CanRetryToolError(result))
                    {
                        ReportProgress(progress, "repairing", "Tool упал, прошу модель исправить вызов: " + command.ToolId);
                        commandCompleted = await RetryFailedToolAsync(systemPrompt, contextPrompt, session, settings, tools, command, result, resultLog, progress, pendingToolRegistrar, cancellationToken).ConfigureAwait(false);
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

                followUpPrompt = "Local tool results above are available. If the task is complete, answer the user normally. If more Office/VBA actions are needed, return one rnassistant-agent block with only the next commands.";
            }

            if (lastResponseWasToolBlock)
            {
                assistantText = AgentTranscript.CreateRunSummary(resultLog);
            }

            ChatTitleBuilder.ApplyDeferred(settings, session, text, assistantText);

            return new ChatCompletionResult
            {
                AssistantText = assistantText,
                ToolResults = resultLog,
                ContextUsage = contextUsage ?? ContextUsageEstimator.FromSession(session, settings)
            };
        }

        private async Task<bool> RetryFailedToolAsync(
            string systemPrompt,
            string contextPrompt,
            ChatSession session,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            ToolCommand failedCommand,
            ToolResult failedResult,
            ICollection<object> resultLog,
            Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repairPrompt = "A local tool call failed. Return only corrected rnassistant-agent JSON block(s), no prose. " +
                "Use only these exact available tool ids: " + AvailableToolIdsText(tools) + "\n" +
                "Original command: `" + failedCommand.ToolId + "` with arguments:\n```json\n" +
                JsonConvert.SerializeObject(failedCommand.Arguments, Formatting.Indented) +
                "\n```\nError: " + failedResult.Message +
                (string.IsNullOrWhiteSpace(failedResult.DataJson) ? string.Empty : "\nData:\n```json\n" + failedResult.DataJson + "\n```");
            var repairMessages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
            repairMessages.Add(new ChatMessage { Role = "user", Content = repairPrompt });

            var repairCompletion = await _completeAsync(settings, repairMessages, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var repairText = repairCompletion.Content ?? string.Empty;
            var retryCommands = _commandParser.Parse(repairText).ToList();
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(
                retryCommands.Count == 0
                    ? "Agent retry did not return an executable tool call."
                    : "Agent retry returned " + retryCommands.Count + " corrected tool call(s).",
                repairCompletion));
            var anySuccess = false;
            var allSucceeded = retryCommands.Count > 0;
            for (var i = 0; i < retryCommands.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retry = retryCommands[i];
                ReportProgress(progress, "retrying", "Повтор tool " + (i + 1) + "/" + retryCommands.Count + ": " + retry.ToolId, CreateRunningActivity(retry, "running", "retry"));
                var retryResult = _toolExecutor.Execute(retry, tools, settings, false, false, cancellationToken);
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

            return anySuccess && allSucceeded;
        }

        private static string AvailableToolIdsText(IEnumerable<ToolDefinition> tools)
        {
            var ids = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                .Select(tool => tool.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id)
                .ToArray();

            return ids.Length == 0 ? "none" : string.Join(", ", ids);
        }

        private static void AttachPendingId(ChatSession session, ToolCommand command, ToolResult result, PendingToolRegistrar pendingToolRegistrar)
        {
            if (!AgentTranscript.IsWaitingResult(result) || pendingToolRegistrar == null)
            {
                return;
            }

            result.PendingId = pendingToolRegistrar(session, command, result);
        }

        private static void ApplyChatModel(AppSettings settings, ChatSession session)
        {
            if (settings == null || session == null || string.IsNullOrWhiteSpace(session.Model))
            {
                return;
            }

            settings.Model = session.Model.Trim();
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
    }
}
