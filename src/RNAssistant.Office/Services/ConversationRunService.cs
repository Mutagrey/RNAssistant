using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Storage;
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

    // Model context remains outside the pure kernel. The same three adapters serve
    // a fresh turn and a confirmed continuation; there is no second execution loop.
    public sealed class ConversationRunService
    {
        public delegate string PendingToolRegistrar(ChatSession session, ToolCommand command, ToolResult result);

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ChatStore _chatStore;
        private readonly Func<IMaterializedModelProtocol> _modelProtocolFactory;
        private readonly ContextCompactionService _contextCompactionService;
        private readonly AttachmentAnalysisService _attachmentAnalysisService;
        private readonly Action<ChatSession> _saved;

        public ConversationRunService(IOfficeApplicationAdapter adapter, OfficeToolExecutor toolExecutor,
            ChatStore chatStore, LlmCompletionDelegate completeAsync)
            : this(adapter, toolExecutor, chatStore, completeAsync, null) { }

        internal ConversationRunService(IOfficeApplicationAdapter adapter, OfficeToolExecutor toolExecutor,
            ChatStore chatStore, LlmCompletionDelegate completeAsync, ContextCompactionService contextCompactionService,
            Func<IMaterializedModelProtocol> modelProtocolFactory = null, Action<ChatSession> saved = null)
        {
            _adapter = adapter;
            _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
            _modelProtocolFactory = modelProtocolFactory ?? (() => new ModelProtocolClient(completeAsync));
            _contextCompactionService = contextCompactionService;
            _attachmentAnalysisService = new AttachmentAnalysisService(completeAsync);
            _saved = saved;
        }

        public Task<ChatTurnResult> ExecuteAsync(string mode, string text, ChatSession session,
            DocumentContext documentContext, AppSettings settings, IReadOnlyList<ToolDefinition> tools,
            Action<string, string, ChatActivity> progress, PendingToolRegistrar pendingToolRegistrar = null,
            IReadOnlyList<SkillDefinition> skills = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteAsync(mode, text, session, documentContext, settings, tools, null, progress,
                pendingToolRegistrar, skills, cancellationToken, true);
        }

        public async Task<ChatTurnResult> ExecuteAsync(string mode, string text, ChatSession session,
            DocumentContext documentContext, AppSettings settings, IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<ChatAttachment> attachments, Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar, IReadOnlyList<SkillDefinition> skills,
            CancellationToken cancellationToken, bool appendUserMessage = true)
        {
            settings = settings ?? new AppSettings();
            settings.EnsureAgentPromptsReviewed();
            ConversationProtocolContext.EnsureCurrentHistory(session);
            mode = ValidateMode(mode, session);
            if (appendUserMessage)
            {
                session.Messages.Add(new ChatMessage
                {
                    Role = "user", Content = text ?? string.Empty,
                    HtmlWorkspaceCheckpoint = ChatResourceUri.ResolveArtifactRevision(session, session.ActiveHtmlArtifactId),
                    Attachments = attachments == null ? new List<ChatAttachment>() : new List<ChatAttachment>(attachments)
                });
            }
            if (session.LastRun == null || session.LastRun.KernelState != null)
                session.LastRun = new ChatRunRecord { RunId = Guid.NewGuid().ToString("N"), StartedUtc = DateTime.UtcNow };
            if (string.IsNullOrWhiteSpace(session.LastRun.RunId)) session.LastRun.RunId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(session.LastRun.TurnId)) session.LastRun.TurnId = session.LastRun.RunId;
            var user = session.Messages.LastOrDefault(message => !message.ProtocolMessage && message.Role == "user");
            if (user != null) user.RunId = session.LastRun.TurnId;
            var input = new ConversationRunInput(settings, documentContext, tools, skills, attachments);
            using (var ports = CreatePorts(mode, text, session, input, progress, pendingToolRegistrar, cancellationToken))
            {
                var kernel = new AgentKernel(ports, ports, ports);
                var result = await kernel.RunAsync(new AgentRunRequest(session.LastRun.RunId, session.LastRun.TurnId,
                    text, new AgentRunLimits(Math.Max(1, settings.MaxAgentIterations), Math.Max(1, settings.MaxAgentToolSteps))),
                    cancellationToken).ConfigureAwait(false);
                return ports.Result(result.Summary);
            }
        }

        public async Task<ChatTurnResult> ConfirmAsync(string pendingId, ToolCommand command, ChatSession session,
            ConversationRunInput input, Action<string, string, ChatActivity> progress,
            PendingToolRegistrar pendingToolRegistrar = null,
            Func<CancellationToken, Task<ConversationRunInput>> refreshModelInput = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (session == null || ChatModes.Normalize(session.Mode) != ChatModes.Agent)
                throw new InvalidOperationException("Only Agent mode can continue a confirmed tool call.");
            input.Settings.EnsureAgentPromptsReviewed();
            var continuation = ConversationProtocolContext.RestoreContinuation(session, command);
            if (continuation.Summary.PendingConfirmation.PendingId != pendingId)
                throw new InvalidOperationException("Pending tool was not found or was already resolved.");
            using (var ports = CreatePorts(ChatModes.Agent, LatestUserRequest(session), session, input,
                progress, pendingToolRegistrar, cancellationToken, command, refreshModelInput, continuation.Revision))
            {
                var result = await new AgentKernel(ports, ports, ports).ResumeAsync(session.LastRun.RunId,
                    pendingId, continuation, cancellationToken).ConfigureAwait(false);
                return ports.Result(result.Summary);
            }
        }

        private ConversationKernelAdapter CreatePorts(string mode, string text, ChatSession session,
            ConversationRunInput input, Action<string, string, ChatActivity> progress,
            PendingToolRegistrar registrar, CancellationToken cancellationToken, ToolCommand confirmedCommand = null,
            Func<CancellationToken, Task<ConversationRunInput>> refresh = null, long revision = 0)
        {
            return new ConversationKernelAdapter(_adapter, _toolExecutor, _chatStore, _modelProtocolFactory(),
                _contextCompactionService, _attachmentAnalysisService, _saved, mode, text, session, input,
                progress, registrar, cancellationToken, confirmedCommand, refresh, revision);
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

        private static bool ValidToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsWhiteSpace);
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

    }
}
