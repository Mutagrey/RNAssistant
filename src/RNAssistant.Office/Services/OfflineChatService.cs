using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Services
{
    public sealed class OfflineChatService
    {
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly LlmCompletionDelegate _completeAsync;
        private readonly ContextCompactionService _contextCompactionService;

        public OfflineChatService(
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync)
            : this(toolExecutor, completeAsync, null)
        {
        }

        internal OfflineChatService(
            OfficeToolExecutor toolExecutor,
            LlmCompletionDelegate completeAsync,
            ContextCompactionService contextCompactionService)
        {
            _toolExecutor = toolExecutor;
            _completeAsync = completeAsync;
            _contextCompactionService = contextCompactionService;
        }

        public Task<ChatCompletionResult> ExecuteAsync(
            string text,
            ChatSession session,
            DocumentContext context,
            AppSettings settings,
            IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string, string, ChatActivity> progress,
            ChatCompletionService.PendingToolRegistrar pendingToolRegistrar,
            IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken,
            bool appendUserMessage = true)
        {
            var adapter = new ClosedDocumentAdapter(session);
            var runner = new AgentRunService(adapter, _toolExecutor, _completeAsync, false, _contextCompactionService);
            var service = new ChatCompletionService(runner);
            return service.ExecuteAsync(text, session, context, settings, tools, attachments, progress, pendingToolRegistrar, skills, cancellationToken, appendUserMessage);
        }

        private sealed class ClosedDocumentAdapter : IOfficeApplicationAdapter
        {
            private readonly ChatSession _session;

            public ClosedDocumentAdapter(ChatSession session)
            {
                _session = session ?? new ChatSession();
            }

            public string HostName { get { return _session.Host ?? "Office"; } }
            public string DocumentKey { get { return _session.DocumentKey ?? string.Empty; } }
            public string RuntimeDocumentKey { get { return "closed:" + DocumentKey; } }
            public string DocumentTitle { get { return _session.DocumentTitle ?? _session.Title ?? "Closed document"; } }
            public string GetDocumentSnapshot(int maxChars) { return "Document is closed. Only saved chat context is available; Office actions require opening the file."; }
            public string GetVbaSnapshot(int maxChars) { return string.Empty; }
            public void PrepareForContextCapture() { }
            public ContextNote CaptureSelectionContext(string mode, int maxChars) { return null; }
            public IEnumerable<ToolDefinition> GetBuiltInTools() { return new ToolDefinition[0]; }
            public ToolResult ExecuteTool(ToolCommand command) { return ToolResult.Fail("Document is closed."); }
        }
    }
}
