using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Diagnostics;
using RNAssistant.Office.Qualification;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController : IDisposable
    {
        private readonly string _runtimeId = Guid.NewGuid().ToString("N");
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly AppDataPaths _paths;
        private readonly SettingsService _settingsService;
        private readonly ChatStore _chatStore;
        private readonly IConversationStore _conversationStore;
        private readonly IEventStore _eventStore;
        private readonly ModelTracePersistenceService _modelTracePersistence;
        private readonly AttachmentStore _attachmentStore;
        private readonly ChatResourceIngestionService _chatResourceIngestion;
        private readonly UploadedHtmlResourceService _uploadedHtmlResources;
        private readonly ArtifactViewerService _artifactViewer;
        private readonly ToolStore _toolStore;
        private readonly SkillStore _skillStore;
        private readonly VbaJournalStore _vbaJournalStore;
        private readonly CasMaintenanceService _casMaintenanceService;
        private readonly ITrajectoryQuery _trajectoryQuery;
        private readonly TrajectoryExportService _trajectoryExportService;
        private readonly OfficeToolExecutor _toolExecutor;
        private readonly ToolCatalogService _toolCatalog;
        private readonly SkillCatalogService _skillCatalog;
        private readonly ChatSessionService _chatSessions;
        private readonly QualificationApplicationService _qualification;
        private readonly ChatHistoryEditService _chatHistoryEditService;
        private readonly ConversationRunService _conversationRunService;
        private readonly ContextCompactionService _contextCompactionService;
        private readonly AttachmentAnalysisService _attachmentAnalysisService;
        private readonly ContextService _contextService;
        private readonly OfficeContextCaptureService _officeContextCapture;
        private readonly LlmClient _llmClient;
        private readonly LlmCompletionDelegate _llmCompletion;
        private readonly object _syncRoot;
        private readonly Dictionary<string, PendingAgentTool> _pendingAgentTools;
        private readonly ChatRunRegistry _chatRuns;
        private readonly HtmlNetworkService _htmlNetwork;
        private readonly CancellationTokenSource _lifetimeCancellation;
        private int _disposed;
        private string _queuedQuickAction;

        public AssistantController(IOfficeApplicationAdapter adapter)
            : this(adapter, null, null)
        {
        }

        internal AssistantController(
            IOfficeApplicationAdapter adapter,
            AppDataPaths paths,
            Func<AppSettings, IEnumerable<ChatMessage>, CancellationToken, Task<LlmCompletionResult>> completeAsync)
        {
            _adapter = adapter;
            _paths = paths ?? AppDataPaths.CreateDefault();
            RuntimeLog.Configure(_paths.Root);
            _settingsService = new SettingsService(_paths);
            _chatStore = new ChatStore(_paths, () => _settingsService.LoadStorageProtector());
            _conversationStore = new ChatConversationStoreAdapter(_chatStore);
            _eventStore = new ChatEventStoreAdapter(_chatStore);
            _modelTracePersistence = new ModelTracePersistenceService(_eventStore);
            _attachmentStore = new AttachmentStore(_paths, () => _settingsService.LoadStorageProtector());
            _chatResourceIngestion = new ChatResourceIngestionService(_attachmentStore);
            _toolStore = new ToolStore(_paths);
            _skillStore = new SkillStore(_paths);
            _vbaJournalStore = new VbaJournalStore(_paths, () => _settingsService.LoadStorageProtector());
            _toolExecutor = new OfficeToolExecutor(
                _adapter,
                _vbaJournalStore,
                _skillStore,
                _toolStore,
                () => _settingsService.Load(),
                settings => _settingsService.Save(settings),
                _paths,
                _chatStore.LoadArtifactBody,
                (attachment, maxChars) => _attachmentStore.ReadExtractedText(attachment, maxChars));
            _uploadedHtmlResources = new UploadedHtmlResourceService(
                _toolExecutor.ResourceGateway,
                (attachment, maxChars) => _attachmentStore.ReadExtractedText(attachment, maxChars));
            _artifactViewer = new ArtifactViewerService(_toolExecutor.ResourceGateway);
            _toolCatalog = new ToolCatalogService(_adapter, _toolExecutor, _toolStore);
            _officeContextCapture = new OfficeContextCaptureService(_adapter, _toolExecutor.DocumentRuntime);
            _skillCatalog = new SkillCatalogService(_adapter, _skillStore);
            _chatRuns = new ChatRunRegistry(_paths);
            _casMaintenanceService = new CasMaintenanceService(
                _paths,
                _chatStore,
                _vbaJournalStore,
                () => _settingsService.LoadStorageProtector(),
                _chatRuns.ReserveMaintenance,
                EnsureNoActiveRuns);
            _trajectoryQuery = new EventStreamTrajectoryQuery();
            _trajectoryExportService = new TrajectoryExportService(
                _paths,
                () => _settingsService.LoadStorageProtector(),
                _trajectoryQuery);
            _chatSessions = new ChatSessionService(_adapter, _conversationStore, _vbaJournalStore);
            _qualification = new QualificationApplicationService(
                _eventStore, _adapter as IQualificationHostPort);
            _lifetimeCancellation = new CancellationTokenSource();
            _chatSessions.RunStateProvider = _chatRuns.Get;
            _chatSessions.RunSessionsProvider = _chatRuns.Sessions;
            _chatSessions.RunOwnershipProvider = _chatRuns.IsExternallyRunning;
            _chatSessions.RunRecoveryLeaseProvider = session => _chatRuns.Start(
                session.Id,
                "recover_" + Guid.NewGuid().ToString("N"),
                session);
            _chatSessions.MaintenanceLeaseProvider = _chatRuns.ReserveMaintenance;
            _chatSessions.ReconcileInterruptedRuns(_runtimeId);
            _chatHistoryEditService = new ChatHistoryEditService(
                RemovePendingAgentToolsForSession,
                CancelPendingActivities,
                _chatStore.LoadArtifactBody);
            _htmlNetwork = new HtmlNetworkService(() => _settingsService.Load(), value => _settingsService.Save(value));
            _llmClient = new LlmClient(
                () => _settingsService.LoadApiKey(),
                attachment => AttachmentImageService.ReadForModel(_attachmentStore, attachment),
                (attachment, maxChars) => _attachmentStore.ReadExtractedText(attachment, maxChars),
                (settings, attachment, maxImages, cancellationToken) =>
                    ModelAttachmentService.ReadForModel(_attachmentStore, settings, attachment, maxImages, cancellationToken),
                RuntimeLog.Debug,
                ReportModelRequestDiagnostics);
            LlmCompletionDelegate rawCompletion;
            if (completeAsync == null)
            {
                rawCompletion = (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                    _llmClient.CompleteAsync(settings, messages, requestOptions, streamProgress, cancellationToken);
            }
            else
            {
                rawCompletion = (settings, messages, requestOptions, streamProgress, cancellationToken) =>
                    completeAsync(settings, messages, cancellationToken);
            }
            LlmCompletionDelegate completion = async (settings, messages, requestOptions, streamProgress, cancellationToken) =>
            {
                ConfigureModelTrace(requestOptions);
                var result = await rawCompletion(
                    settings,
                    messages,
                    requestOptions,
                    streamProgress,
                    cancellationToken).ConfigureAwait(false);
                if (result != null && result.TokenEstimateCalibrationEligible &&
                    result.BaseEstimatedPromptTokens.GetValueOrDefault() > 0 &&
                    result.EstimatedPromptTokens.GetValueOrDefault() > 0 &&
                    result.PromptTokens.GetValueOrDefault() > 0)
                {
                    TokenEstimateCalibration.Observe(
                        settings,
                        settings == null ? null : settings.Model,
                        result.BaseEstimatedPromptTokens.Value,
                        result.EstimatedPromptTokens.Value,
                        result.PromptTokens.Value);
                }
                return result;
            };
            _llmCompletion = completion;
            _attachmentAnalysisService = new AttachmentAnalysisService(completion);
            _contextCompactionService = new ContextCompactionService(completion);
            _conversationRunService = new ConversationRunService(
                _adapter,
                _toolExecutor,
                _conversationStore,
                _eventStore,
                completion,
                _contextCompactionService,
                saved: _chatSessions.NotifySaved);
            _contextService = new ContextService(_adapter);
            _syncRoot = new object();
            _pendingAgentTools = new Dictionary<string, PendingAgentTool>(StringComparer.OrdinalIgnoreCase);
        }

        private void ConfigureModelTrace(LlmRequestOptions options)
        {
            _modelTracePersistence.Configure(options);
        }

        public string HostName { get { return _adapter.HostName; } }

        public InitResponse Initialize()
        {
            var session = LoadSession(null);
            var activeId = session.Id;
            var context = LoadContext(session);
            var settings = _settingsService.Load();
            var chatSettings = ResolveChatSettings(session, settings);
            return new InitResponse
            {
                SessionRevision = session == null ? 0 : session.Revision,
                RunViewState = RunViewStateProjector.Create(session),
                AppVersion = ApplicationVersionService.Current,
                Host = _adapter.HostName,
                DocumentKey = _adapter.DocumentKey,
                Title = _adapter.DocumentTitle,
                OfficeContext = _officeContextCapture.CaptureOfficeContext(),
                ActiveChatId = activeId,
                ActiveChatModel = session == null ? string.Empty : session.Model,
                ActiveChatMode = ChatModes.Normalize(session == null ? null : session.Mode),
                ActiveChatReasoning = session != null && session.ReasoningEnabled,
                Chats = _chatSessions.GetChatSummaries(activeId),
                Documents = ListOpenDocuments(),
                Settings = settings,
                HasApiKey = !string.IsNullOrWhiteSpace(_settingsService.LoadApiKey()),
                HasHistorySecret = !string.IsNullOrWhiteSpace(_settingsService.LoadHistorySecret()),
                Tools = _toolCatalog.GetVisibleTools(),
                ToolsPath = _paths.ToolsDirectory,
                Skills = _skillCatalog.GetVisibleSkills(),
                SkillsPath = _paths.SkillsDirectory,
                Context = ChatCloneService.CloneContext(context),
                Messages = ChatCloneService.CloneMessages(session.Messages),
                Artifacts = ChatArtifactDto.From(session),
                ArtifactLibrary = ArtifactLibraryProjectionService.Project(session),
                ActiveContextCheckpointId = session.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = session.ActiveHtmlArtifactId,
                ActiveTaskListArtifactId = session.ActiveTaskListArtifactId,
                ActivePlanDocumentArtifactId = session.ActivePlanDocumentArtifactId,
                ContextUsage = ContextUsageEstimator.FromSession(session, chatSettings),
                HtmlWorkspace = HtmlWorkspaceDto.From(
                    session == null ? null : HtmlWorkspaceToolService.NormalizeWorkspace(session.HtmlWorkspace),
                    session == null ? null : session.HtmlWorkspaceRecovery),
                QuickAction = DequeueQuickAction()
            };
        }

        internal IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            var catalog = _adapter as IOfficeDocumentCatalog;
            if (catalog == null)
            {
                return new OpenOfficeDocumentDto[0];
            }

            try
            {
                return catalog.ListOpenDocuments() ?? new OpenOfficeDocumentDto[0];
            }
            catch
            {
                return new OpenOfficeDocumentDto[0];
            }
        }

        public ChatStateResponse ActivateDocument(string documentKey)
        {
            var catalog = _adapter as IOfficeDocumentCatalog;
            if (catalog == null || !catalog.ActivateDocument(documentKey))
            {
                throw new InvalidOperationException("Не удалось активировать документ.");
            }

            return ChatState(LoadSession(null));
        }

        private ChatStateResponse CreateStoredChatState(string host, string documentKey, string documentTitle)
        {
            var activeId = _conversationStore.LoadActiveSessionId(host, documentKey);
            var active = string.IsNullOrWhiteSpace(activeId)
                ? null
                : _conversationStore.Load(host, documentKey, activeId);
            var chats = _chatSessions.GetChatSummaries(activeId)
                .ToList();

            return new ChatStateResponse
            {
                SessionRevision = active == null ? 0 : active.Revision,
                RunViewState = RunViewStateProjector.Create(active),
                ActiveChatId = activeId,
                ActiveChatModel = active == null ? string.Empty : active.Model,
                ActiveChatMode = ChatModes.Normalize(active == null ? null : active.Mode),
                ActiveChatReasoning = active != null && active.ReasoningEnabled,
                Chats = chats,
                Documents = ListOpenDocuments(),
                Artifacts = ChatArtifactDto.From(active),
                ArtifactLibrary = ArtifactLibraryProjectionService.Project(active),
                ActiveContextCheckpointId = active == null ? string.Empty : active.ActiveContextCheckpointId,
                ActiveHtmlArtifactId = active == null ? string.Empty : active.ActiveHtmlArtifactId,
                ActiveTaskListArtifactId = active == null ? string.Empty : active.ActiveTaskListArtifactId,
                ActivePlanDocumentArtifactId = active == null ? string.Empty : active.ActivePlanDocumentArtifactId
            };
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message)
        {
            if (progress != null)
            {
                progress(phase, message, null);
            }
        }

        private static void ReportProgress(Action<string, string, ChatActivity> progress, string phase, string message, ChatActivity activity)
        {
            if (progress != null)
            {
                progress(phase, message, activity);
            }
        }

        private static void ReportExternalProgress(
            Action<string, string, ChatActivity> progress,
            string phase,
            string message,
            ChatActivity activity)
        {
            if (progress == null) return;
            try
            {
                progress(phase, message, activity);
            }
            catch
            {
                // WebView notifications cannot abort already persisted work.
            }
        }

        private void ReportExternalChatState(
            Action<ChatStateResponse> chatStateChanged,
            ChatSession session)
        {
            if (chatStateChanged == null) return;
            try
            {
                chatStateChanged(ChatState(session));
            }
            catch
            {
                // WebView notifications cannot abort already persisted work.
            }
        }

    }
}
