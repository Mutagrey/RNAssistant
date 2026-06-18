using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class ChatCompletionResult
    {
        public string AssistantText { get; set; }
        public IReadOnlyList<object> SkillResults { get; set; }
        public object ContextUsage { get; set; }
    }

    public sealed class ChatCompletionService
    {
        private const int MaxAgentIterations = 3;
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly Func<AppSettings, IEnumerable<ChatMessage>, Task<LlmCompletionResult>> _completeAsync;
        private readonly PromptComposer _promptComposer;
        private readonly SkillCommandParser _commandParser;

        public ChatCompletionService(
            IOfficeApplicationAdapter adapter,
            OfficeToolExecutor toolExecutor,
            Func<AppSettings, IEnumerable<ChatMessage>, Task<LlmCompletionResult>> completeAsync)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor;
            _completeAsync = completeAsync;
            _promptComposer = new PromptComposer();
            _commandParser = new SkillCommandParser();
        }

        public async Task<ChatCompletionResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext documentContext,
            AppSettings settings,
            IReadOnlyList<SkillDefinition> tools,
            Action<string, string> progress)
        {
            ReportProgress(progress, "context", "Читаю документ...");
            ApplyChatModel(settings, session);
            session.Messages.Add(new ChatMessage { Role = "user", Content = text });
            EnsureSessionTitleFromUserText(session, text);

            var vbaSnapshot = string.Empty;
            var systemPrompt = _promptComposer.ComposeSystemPrompt(
                settings,
                _adapter.HostName,
                _adapter.GetDocumentSnapshot(settings.ContextCharLimit),
                vbaSnapshot,
                tools,
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
            for (var iteration = 0; iteration < MaxAgentIterations; iteration++)
            {
                var messages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
                if (!string.IsNullOrWhiteSpace(followUpPrompt))
                {
                    messages.Add(new ChatMessage { Role = "user", Content = followUpPrompt });
                }

                contextUsage = ContextUsageEstimator.FromPrompt(messages, settings);
                ReportProgress(progress, "thinking", iteration == 0 ? "Модель думает..." : "Модель продолжает агентскую задачу...");
                var completion = await _completeAsync(settings, messages).ConfigureAwait(false);
                assistantText = completion.Content ?? string.Empty;

                ReportProgress(progress, "processing", "Разбираю ответ...");
                var commands = _commandParser.Parse(assistantText).ToList();
                if (commands.Count == 0)
                {
                    if (iteration == 0 && settings.AgentModeEnabled != false && AgentTranscript.ShouldForceAgentToolUse(text, _adapter.HostName))
                    {
                        followUpPrompt = "You are in RNAssistant Agent mode. The user asked for an Office action, so a prose-only answer is not acceptable. Return only one ```rnassistant-agent fenced JSON block with executable steps using available tools. If a tool is missing, say that plainly instead of inventing one.";
                        continue;
                    }
                    session.Messages.Add(AgentTranscript.CreateAssistantMessage(assistantText, completion));
                    break;
                }

                session.Messages.Add(AgentTranscript.CreateAssistantMessage(AgentTranscript.CreateAgentPlanMessage(commands), completion));
                var shouldContinue = settings.AutoRunToolCalls != false;
                for (var i = 0; i < commands.Count; i++)
                {
                    var command = commands[i];
                    ReportProgress(
                        progress,
                        settings.AutoRunToolCalls != false ? "executing" : "waiting",
                        (settings.AutoRunToolCalls != false ? "Исполняю tool " : "Auto-run отключен для tool ") + (i + 1) + "/" + commands.Count + ": " + command.SkillId);
                    var result = settings.AutoRunToolCalls != false
                        ? _toolExecutor.Execute(command, tools, settings, false, false)
                        : SkillResult.Fail("Auto tool execution is disabled: " + command.SkillId);
                    resultLog.Add(AgentTranscript.DescribeResult(command, result));
                    AgentTranscript.AddLocalResultMessage(session, command, result);
                    if (!result.Success)
                    {
                        shouldContinue = false;
                    }
                    if (!result.Success && settings.AutoRunToolCalls != false && settings.AutoRetryToolErrors != false && AgentTranscript.CanRetryToolError(result))
                    {
                        ReportProgress(progress, "repairing", "Tool упал, прошу модель исправить вызов: " + command.SkillId);
                        await RetryFailedToolAsync(systemPrompt, contextPrompt, session, settings, tools, command, result, resultLog, progress).ConfigureAwait(false);
                    }
                }

                if (!shouldContinue)
                {
                    break;
                }

                followUpPrompt = "Local tool results above are available. If the task is complete, answer the user normally. If more Office/VBA actions are needed, return one rnassistant-agent block with only the next commands.";
            }

            return new ChatCompletionResult
            {
                AssistantText = assistantText,
                SkillResults = resultLog,
                ContextUsage = contextUsage ?? ContextUsageEstimator.FromSession(session, settings)
            };
        }

        private async Task RetryFailedToolAsync(
            string systemPrompt,
            string contextPrompt,
            ChatSession session,
            AppSettings settings,
            IReadOnlyList<SkillDefinition> tools,
            SkillCommand failedCommand,
            SkillResult failedResult,
            ICollection<object> resultLog,
            Action<string, string> progress)
        {
            var repairPrompt = "A local tool call failed. Return only corrected rnassistant-skill JSON block(s), no prose. " +
                "Original command: `" + failedCommand.SkillId + "` with arguments:\n```json\n" +
                JsonConvert.SerializeObject(failedCommand.Arguments, Formatting.Indented) +
                "\n```\nError: " + failedResult.Message +
                (string.IsNullOrWhiteSpace(failedResult.DataJson) ? string.Empty : "\nData:\n```json\n" + failedResult.DataJson + "\n```");
            var repairMessages = PromptMessageBuilder.Build(systemPrompt, contextPrompt, session.Messages, settings.ContextCharLimit);
            repairMessages.Add(new ChatMessage { Role = "user", Content = repairPrompt });

            var repairCompletion = await _completeAsync(settings, repairMessages).ConfigureAwait(false);
            var repairText = repairCompletion.Content ?? string.Empty;
            session.Messages.Add(AgentTranscript.CreateAssistantMessage(repairText, repairCompletion));
            var retryCommands = _commandParser.Parse(repairText).ToList();
            for (var i = 0; i < retryCommands.Count; i++)
            {
                var retry = retryCommands[i];
                ReportProgress(progress, "retrying", "Повтор tool " + (i + 1) + "/" + retryCommands.Count + ": " + retry.SkillId);
                var retryResult = _toolExecutor.Execute(retry, tools, settings, false, false);
                if (resultLog != null)
                {
                    resultLog.Add(AgentTranscript.DescribeResult(retry, retryResult));
                }
                AgentTranscript.AddLocalResultMessage(session, retry, retryResult);
            }

            if (retryCommands.Count == 0)
            {
                var noCommand = SkillResult.Fail("Auto-retry did not return a corrected tool call.");
                if (resultLog != null)
                {
                    resultLog.Add(new { skillId = "auto-retry", success = false, message = noCommand.Message, dataJson = noCommand.DataJson });
                }
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Local skill retry result: " + noCommand.Message });
            }
        }

        private static void ApplyChatModel(AppSettings settings, ChatSession session)
        {
            if (settings == null || session == null || string.IsNullOrWhiteSpace(session.Model))
            {
                return;
            }

            settings.Model = session.Model.Trim();
        }

        private static void EnsureSessionTitleFromUserText(ChatSession session, string text)
        {
            if (session == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!string.Equals(session.Title, "New chat", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var title = Regex.Replace(text.Trim(), "\\s+", " ");
            session.Title = title.Length <= 64 ? title : title.Substring(0, 61) + "...";
        }

        private static void ReportProgress(Action<string, string> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message);
            }
        }
    }
}
