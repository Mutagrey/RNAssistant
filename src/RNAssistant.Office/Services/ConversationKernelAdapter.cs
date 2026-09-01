using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    public sealed class ConversationRunInput
    {
        public AppSettings Settings { get; private set; }
        public DocumentContext Context { get; private set; }
        public IReadOnlyList<ToolDefinition> Tools { get; private set; }
        public IReadOnlyList<SkillDefinition> Skills { get; private set; }
        public IReadOnlyList<ChatAttachment> Attachments { get; private set; }

        public ConversationRunInput(AppSettings settings, DocumentContext context, IReadOnlyList<ToolDefinition> tools,
            IReadOnlyList<SkillDefinition> skills = null, IReadOnlyList<ChatAttachment> attachments = null)
        {
            Settings = settings ?? new AppSettings();
            Context = context;
            Tools = tools ?? new ToolDefinition[0];
            Skills = skills ?? new SkillDefinition[0];
            Attachments = attachments ?? new ChatAttachment[0];
        }
    }

    // One invocation-scoped adapter for the existing application services. Its
    // partials separate model materialization, executor mapping and event projection.
    // No loop, effect aggregation, accepted-id index or durable side store lives here.
    internal sealed partial class ConversationKernelAdapter : IModelProtocol, IToolRuntime, IRunStore, IDisposable
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly OfficeToolExecutor _executor;
        private readonly IConversationStore _conversations;
        private readonly IEventStore _eventStore;
        private readonly IMaterializedModelProtocol _protocol;
        private readonly ContextCompactionService _compaction;
        private readonly AttachmentAnalysisService _attachments;
        private readonly Action<ChatSession> _saved;
        private readonly ConversationRunPolicy _policy;
        private readonly string _text;
        private readonly ChatSession _session;
        private readonly Action<string, string, ChatActivity> _progress;
        private readonly ConversationRunService.PendingToolRegistrar _registrar;
        private readonly CancellationToken _runCancellation;
        private readonly ToolCommand _confirmedCommand;
        private readonly Func<CancellationToken, Task<ConversationRunInput>> _refresh;
        private readonly Dictionary<string, ToolCommand> _commands = new Dictionary<string, ToolCommand>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolResultMaterialization> _results = new Dictionary<string, ToolResultMaterialization>(StringComparer.Ordinal);
        // UI/domain compatibility only; never a model writer input.
        private readonly Dictionary<string, ToolResult> _uiResults = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
        private readonly List<object> _projectedResults = new List<object>();
        private ConversationRunInput _input;
        private List<ToolDefinition> _catalog;
        private ToolPackSnapshot _toolPack;
        private IReadOnlyList<SkillDefinition> _skills;
        private ConversationModelSession _modelSession;
        private ModelProtocolResult _lastModel;
        private NativeToolRuntimeAdapter _nativeTools;
        private AgentModelResult _preparationFailure;
        private object _contextUsage;
        private long _cursor;
        private string _stepMessage;

        internal ConversationKernelAdapter(IOfficeApplicationAdapter adapter, OfficeToolExecutor executor,
            IConversationStore conversations, IEventStore eventStore, IMaterializedModelProtocol protocol,
            ContextCompactionService compaction,
            AttachmentAnalysisService attachments, Action<ChatSession> saved, string mode, string text,
            ChatSession session, ConversationRunInput input, Action<string, string, ChatActivity> progress,
            ConversationRunService.PendingToolRegistrar registrar, CancellationToken cancellationToken,
            ToolCommand confirmedCommand, Func<CancellationToken, Task<ConversationRunInput>> refresh, long revision)
        {
            _adapter = adapter;
            _executor = executor;
            _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _protocol = protocol;
            _compaction = compaction;
            _attachments = attachments;
            _saved = saved;
            _policy = ConversationRunPolicy.For(mode);
            _text = text;
            _session = session;
            _progress = progress;
            _registrar = _policy.AllowsConfirmation ? registrar : null;
            _runCancellation = cancellationToken;
            _confirmedCommand = confirmedCommand;
            _refresh = refresh;
            _cursor = revision;
            UseInput(input);
            ConversationModelSession.ReleasePreviousMedia(session);
        }

        private void UseInput(ConversationRunInput input)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _catalog = ConversationRunService.PrepareToolsForRun(input.Tools);
            _skills = _policy.SelectSkills(input.Skills);
            CapabilityDiscoveryExecutor.ThrowOnCollision(_catalog, _skills);
            _catalog = _policy.SelectTools(_executor.AvailableConversationToolsForSession(_catalog, _session));
            CapabilityDiscoveryExecutor.BindReadSchema(_catalog, _skills);
            _toolPack = ToolPackSnapshotFactory.Capture(_policy.Mode, _adapter.HostName, _catalog);
            _nativeTools = _executor.CreateNativeRuntime(
                _session, _toolPack, _input.Settings, _policy.Mode,
                true, RegisterNativePending);
        }

        internal ChatTurnResult Result(RunSummary summary)
        {
            var status = ConversationRunProjection.Status(summary);
            return new ChatTurnResult
            {
                AssistantText = summary.AssistantMessage,
                ToolResults = _projectedResults,
                ContextUsage = _contextUsage,
                WaitingForConfirmation = summary.Lifecycle == RunLifecycle.AwaitingConfirmation,
                ResponseProtocolVersion = AgentResponseProtocol.CurrentVersion,
                ResponseStatus = summary.Reason == "provider_refused" ? AgentResponseStatuses.Refused
                    : summary.Lifecycle == RunLifecycle.Completed ? status : null,
                RunViewState = RunViewStateProjector.Create(_session)
            };
        }

        public void Dispose()
        {
            if (_modelSession != null) _modelSession.Dispose();
        }
    }
}
