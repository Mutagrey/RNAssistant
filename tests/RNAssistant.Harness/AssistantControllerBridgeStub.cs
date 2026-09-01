using RNAssistant.Core.Tools;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class AssistantController
    {
        internal event Action<LlmRequestDiagnosticUpdate> ModelRequestDiagnostics;

        public string LastToolId { get; private set; }
        public string LastArgumentsJson { get; private set; }
        public bool LastDryRun { get; private set; }
        public string LastChatText { get; private set; }
        public string LastChatId { get; private set; }
        public IReadOnlyList<string> LastResourceDraftIds { get; private set; }
        public string LastResourceDraftId { get; private set; }
        public string LastResourceFileName { get; private set; }
        public string LastChatMode { get; private set; }
        public bool LastChatReasoning { get; private set; }
        public string LastRunId { get; private set; }
        public AppSettings LastSettings { get; private set; }
        public string LastApiKey { get; private set; }
        public string LastHistorySecret { get; private set; }
        public string LastModuleName { get; private set; }
        public string LastModuleCode { get; private set; }
        public string LastModuleHash { get; private set; }
        public string LastModuleType { get; private set; }
        public string LastVbaMutationId { get; private set; }
        public string LastVbaMutationCursor { get; private set; }
        public string LastContextKind { get; private set; }
        public string LastContextTitle { get; private set; }
        public string LastContextReference { get; private set; }
        public string LastContextText { get; private set; }
        public string LastToolsJson { get; private set; }
        public string LastSkillsJson { get; private set; }
        public string LastSkillReferenceId { get; private set; }
        public string LastSkillReferencePath { get; private set; }
        public string LastSkillReferenceContent { get; private set; }
        public string LastDocumentHost { get; private set; }
        public string LastHtmlPath { get; private set; }
        public string LastHtmlDataName { get; private set; }
        public string LastHtmlSourceResourceUri { get; private set; }
        public string LastExpectedHtmlArtifactId { get; private set; }
        public string LastArtifactViewerResourceUri { get; private set; }
        public string LastArtifactViewerCursor { get; private set; }
        public string LastTrajectoryCursor { get; private set; }
        public string LastTrajectoryView { get; private set; }
        public string LastTrajectorySearch { get; private set; }
        public string LastTrajectoryVisibility { get; private set; }
        public IReadOnlyList<string> LastTrajectoryEventTypes { get; private set; }
        public string LastTrajectoryExportRedaction { get; private set; }
        public bool LastTrajectoryExportCas { get; private set; }
        public string LastQualificationPackId { get; private set; }
        public string LastQualificationRunId { get; private set; }
        public string LastQualificationStepId { get; private set; }
        public string LastQualificationSuite { get; private set; }
        public bool LastQualificationAcknowledged { get; private set; }
        public bool LastQualificationCancel { get; private set; }

        public InitResponse Initialize()
        {
            return new InitResponse
            {
                Host = "Excel",
                Title = "Harness.xlsx",
                Tools = EmptyToolLibrary(),
                Skills = EmptySkillLibrary()
            };
        }
        public ChatStateResponse ListChats() { return ChatState(); }
        public ChatTrajectoryResponse GetChatTrajectory(ChatTrajectoryRequest request)
        {
            LastChatId = request == null ? null : request.ChatId;
            LastTrajectoryView = request == null ? null : request.View;
            LastTrajectoryCursor = request == null ? null : request.Cursor;
            LastTrajectorySearch = request == null ? null : request.Search;
            LastTrajectoryVisibility = request == null ? null : request.Visibility;
            LastTrajectoryEventTypes = request == null
                ? (IReadOnlyList<string>)new string[0]
                : request.EventTypes ?? new List<string>();
            return new ChatTrajectoryResponse { ChatId = LastChatId, Revision = 1, Events = new SessionEventDto[0], Rows = new TrajectoryViewRowDto[0] };
        }
        public ChatTrajectoryExportResponse ExportChatTrajectory(ChatTrajectoryExportRequest request)
        {
            LastChatId = request == null ? null : request.ChatId;
            LastTrajectoryView = request == null ? null : request.View;
            LastTrajectorySearch = request == null ? null : request.Search;
            LastTrajectoryVisibility = request == null ? null : request.Visibility;
            LastTrajectoryEventTypes = request == null
                ? (IReadOnlyList<string>)new string[0]
                : request.EventTypes ?? new List<string>();
            LastTrajectoryExportRedaction = request == null ? null : request.RedactionMode;
            LastTrajectoryExportCas = request != null && request.IncludeCasPayloads == true;
            return new ChatTrajectoryExportResponse
            {
                ChatId = LastChatId,
                FileName = "trajectory.zip",
                ContentType = "application/zip",
                Base64 = string.Empty,
                RedactionMode = LastTrajectoryExportRedaction,
                CasPayloadsIncluded = LastTrajectoryExportCas
            };
        }
        public ChatEventPayloadResponse GetChatEventPayload(string chatId, string eventId)
        {
            return new ChatEventPayloadResponse { ChatId = chatId, EventId = eventId, Text = "{}", ContentType = "application/json" };
        }
        public QualificationCatalogResponse GetQualificationCatalog(string chatId, string suite)
        {
            LastChatId = chatId;
            LastQualificationSuite = suite;
            return new QualificationCatalogResponse
            {
                SchemaVersion = 1,
                Host = "Excel",
                Suite = suite,
                Packs = new QualificationPackDto[0],
                MissingCoverage = new string[0]
            };
        }
        public QualificationSessionResponse GetQualificationRun(string chatId, string runId)
        {
            LastChatId = chatId;
            LastQualificationRunId = runId;
            return QualificationState(chatId, runId);
        }
        public Task<QualificationSessionResponse> StartQualificationAsync(
            string chatId, string packId, string previousRunId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatId = chatId;
            LastQualificationPackId = packId;
            LastQualificationRunId = previousRunId;
            return Task.FromResult(QualificationState("qualification-chat", "qualification-run"));
        }
        public Task<QualificationSessionResponse> AdvanceQualificationAsync(
            string chatId, string runId, string stepId, bool acknowledged, bool cancel, string note,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatId = chatId;
            LastQualificationRunId = runId;
            LastQualificationStepId = stepId;
            LastQualificationAcknowledged = acknowledged;
            LastQualificationCancel = cancel;
            return Task.FromResult(QualificationState(chatId, runId));
        }
        public ChatStateResponse CreateChat(string title) { return ChatState(title); }
        public ChatStateResponse CreateDocumentChat(string title, string host, string documentKey, string documentTitle, string documentPath)
        {
            LastDocumentHost = host;
            return ChatState(title, documentKey);
        }
        public ChatStateResponse SelectChat(string chatId) { return ChatState(null, chatId); }
        public OpenDocumentResponse OpenDocument(string chatId) { return new OpenDocumentResponse { Path = string.Empty, Launched = false }; }
        public ChatStateResponse ActivateDocument(string documentKey) { return ChatState(null, documentKey); }
        public ChatStateResponse DeleteDocument(string host, string documentKey)
        {
            LastDocumentHost = host;
            return ChatState(host, documentKey);
        }
        public ChatStateResponse RenameChat(string chatId, string title) { return ChatState(title, chatId); }
        public ChatStateResponse SetChatModel(string chatId, string model) { return ChatState(model, chatId); }
        public ChatStateResponse SetChatMode(string chatId, string mode)
        {
            LastChatId = chatId;
            LastChatMode = mode;
            var state = ChatState(null, chatId);
            state.ActiveChatMode = mode;
            return state;
        }
        public ChatStateResponse SetChatReasoning(string chatId, bool enabled)
        {
            LastChatId = chatId;
            LastChatReasoning = enabled;
            var state = ChatState(null, chatId);
            state.ActiveChatReasoning = enabled;
            return state;
        }
        public ChatStateResponse ClearChat(string chatId) { return ChatState(null, chatId); }
        public Task<ChatStateResponse> CompactChatContextAsync(string chatId = null, Action<string, string, ChatActivity> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatId = chatId;
            if (progress != null) progress("compacted", "Context compacted", new ChatActivity { Kind = "compaction", Title = "Context compacted", Status = "completed" });
            return Task.FromResult(ChatState(null, chatId));
        }
        public ChatStateResponse DeleteChat(string chatId) { return ChatState(null, chatId); }
        public bool CancelChatRun(string chatId, string runId) { LastChatId = chatId; return !string.IsNullOrWhiteSpace(runId); }
        public ChatStateResponse DeleteMessage(string id, int index, string chatId = null) { return ChatState(id, chatId); }
        public ChatStateResponse ForkChat(string id, int index, string chatId = null) { return ChatState(id, chatId); }
        public Task<ChatStateResponse> EditMessageAsync(
            string text,
            string id,
            int index,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatText = text;
            LastChatId = chatId;
            if (progress != null)
            {
                progress("thinking", "Testing edit progress", new ChatActivity { Kind = "notice", Title = "Testing edit progress", Status = "running" });
            }
            if (chatStateChanged != null)
            {
                chatStateChanged(ChatState("Edited title", chatId));
            }
            return Task.FromResult(ChatState(id, chatId));
        }
        public ChatStateResponse UpdateMessageActivityData(string messageId, string dataJson, string chatId = null) { return ChatState(messageId, chatId); }
        public SettingsResponse GetSettings() { return new SettingsResponse { Settings = new AppSettings(), HasApiKey = false, HasHistorySecret = false }; }
        public RuntimeLogResponse GetRuntimeLog() { return new RuntimeLogResponse { Content = "runtime log", Path = "runtime.log" }; }
        public RuntimeLogResponse ClearRuntimeLog() { return new RuntimeLogResponse { Content = string.Empty, Path = "runtime.log" }; }
        public CasHealthResponse GetCasHealth() { return new CasHealthResponse { Healthy = true, ReachabilityComplete = true, CanGarbageCollect = true }; }
        public CasGarbageCollectionResponse CollectCasGarbage() { return new CasGarbageCollectionResponse { Completed = true, Health = GetCasHealth() }; }
        public Task<ModelCatalogResponse> GetModelCatalogAsync(AppSettings settings, string apiKey) { return Task.FromResult(new ModelCatalogResponse { Catalog = new JObject() }); }

        public bool LastReviewAgentPrompts { get; private set; }

        public SettingsResponse SaveSettings(AppSettings settings, string apiKey, string historySecret, bool reviewAgentPrompts = false)
        {
            LastSettings = settings;
            LastApiKey = apiKey;
            LastHistorySecret = historySecret;
            LastReviewAgentPrompts = reviewAgentPrompts;
            return GetSettings();
        }

        public Task<ModelCompatibilityResponse> TestModelCompatibilityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelCompatibilityResponse
            {
                Compatible = true,
                Model = "harness-model",
                InstructionRole = "developer",
                ResponseMode = AgentResponseModes.JsonObject,
                ToolResultRole = ToolResultRoles.User,
                Checks = new[]
                {
                    new ModelCompatibilityCheckDto { Id = "user_role", Title = "Роль user", Passed = true, Required = true }
                }
            });
        }

        public Task<ModelConnectionTestResponse> TestModelConnectionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = new LlmRequestDiagnosticUpdate
            {
                RequestId = "probe-1",
                Phase = LlmRequestDiagnosticPhases.Completed,
                Model = "harness-model",
                StreamRequested = true,
                ElapsedMs = 25,
                PreparationMs = 2,
                ResponseHeadersMs = 15,
                FirstChunkMs = 20,
                TotalMs = 25,
                StatusCode = 200
            };
            var handler = ModelRequestDiagnostics;
            if (handler != null) handler(update);
            return Task.FromResult(new ModelConnectionTestResponse
            {
                Success = true,
                Summary = "Модель ответила.",
                Model = "harness-model",
                StreamRequested = true,
                DurationMs = 25,
                Diagnostics = ModelRequestDiagnosticsDto.From(update)
            });
        }

        public InitResponse ClearRuntimeData() { return Initialize(); }
        public ToolLibraryResponse GetTools() { return EmptyToolLibrary(); }
        public ToolLibraryMutationResponse SaveTools(SaveToolsPayload payload)
        {
            if (payload == null || payload.Type != SaveToolsPayload.ContractType ||
                payload.ContractVersion != ToolLibraryResponse.CurrentContractVersion)
                throw new InvalidOperationException(
                    "Unsupported Tool Library mutation contract.");
            LastToolsJson = JsonConvert.SerializeObject(
                payload.Mutations ?? new List<ToolCoreMutationPayload>());
            return new ToolLibraryMutationResponse
            {
                Type = ToolLibraryMutationResponse.ContractType,
                ContractVersion = ToolLibraryResponse.CurrentContractVersion,
                Results = new List<ToolMutationResultDto>(),
                Library = EmptyToolLibrary()
            };
        }

        public VbaToolPackageResponse InstallVbaTool(string id, bool dryRun)
        {
            LastToolId = id;
            LastDryRun = dryRun;
            return new VbaToolPackageResponse
            {
                Result = new VbaPackageResultDto
                {
                    ContractVersion = 1,
                    Status = "ok",
                    Success = true,
                    Message = "installed",
                    Effect = "verified_change"
                },
                Tools = EmptyToolLibrary()
            };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            LastToolId = id;
            return new VbaToolPackageResponse
            {
                Result = new VbaPackageResultDto
                {
                    ContractVersion = 1,
                    Status = "ok",
                    Success = true,
                    Message = "uninstalled",
                    Effect = "verified_change"
                },
                Tools = EmptyToolLibrary()
            };
        }

        public SkillLibraryResponse GetSkills()
        {
            return EmptySkillLibrary();
        }

        public SkillLibraryMutationResponse SaveSkills(
            SaveSkillsPayload payload)
        {
            LastSkillsJson = JsonConvert.SerializeObject(
                payload == null ? null : payload.Mutations);
            return new SkillLibraryMutationResponse
            {
                Type = SkillLibraryMutationResponse.ContractType,
                ContractVersion = SkillLibraryResponse.CurrentContractVersion,
                Results = new List<SkillMutationResultDto>(),
                Library = EmptySkillLibrary()
            };
        }
        public SkillReferenceResponse ReadSkillReference(
            SkillReferencePayload payload)
        {
            LastSkillReferenceId = payload == null ? null : payload.SkillId;
            LastSkillReferencePath = payload == null ? null : payload.Path;
            return SkillReferenceResult(
                LastSkillReferenceId, LastSkillReferencePath,
                "reference", false, "read_reference");
        }
        public SkillReferenceResponse SaveSkillReference(
            SaveSkillReferencePayload payload)
        {
            LastSkillReferenceId = payload == null ? null : payload.SkillId;
            LastSkillReferencePath = payload == null ? null : payload.Path;
            LastSkillReferenceContent = payload == null ? null : payload.Content;
            return SkillReferenceResult(
                LastSkillReferenceId, LastSkillReferencePath,
                LastSkillReferenceContent, false, "update_reference");
        }
        public SkillReferenceResponse DeleteSkillReference(
            SkillReferencePayload payload)
        {
            LastSkillReferenceId = payload == null ? null : payload.SkillId;
            LastSkillReferencePath = payload == null ? null : payload.Path;
            return SkillReferenceResult(
                LastSkillReferenceId, LastSkillReferencePath,
                null, true, "delete_reference");
        }
        private static SkillReferenceResponse SkillReferenceResult(
            string skillId, string path, string content,
            bool deleted, string operation)
        {
            var reference = deleted ? null : new SkillReferenceDto
            {
                Path = path,
                Revision = "ref",
                ByteLength = content == null ? 0 : content.Length
            };
            return new SkillReferenceResponse
            {
                Type = SkillReferenceResponse.ContractType,
                ContractVersion = SkillLibraryResponse.CurrentContractVersion,
                Result = new SkillMutationResultDto
                {
                    Type = "rnassistant.skillMutationResult",
                    ContractVersion = SkillLibraryResponse.CurrentContractVersion,
                    Status = "ok",
                    Message = "ok",
                    Dispatch = deleted ? "may_have_dispatched" : "not_dispatched",
                    Effect = deleted ? "verified_change" : "none",
                    Id = skillId,
                    Operation = operation,
                    ReferencePath = path,
                    Revision = "package",
                    PreviousRevision = "package",
                    Changed = deleted
                },
                Skill = new SkillPackageDto
                {
                    Revision = "package",
                    Id = skillId,
                    Host = "Common",
                    Name = skillId,
                    Description = "stub",
                    Version = "1.0.0",
                    BodyMarkdown = "# Stub",
                    Enabled = true,
                    BuiltIn = false,
                    References = deleted
                        ? new List<SkillReferenceDto>()
                        : new List<SkillReferenceDto> { reference }
                },
                Path = path,
                Content = content,
                Deleted = deleted,
                Reference = reference
            };
        }

        private static SkillLibraryResponse EmptySkillLibrary()
        {
            return new SkillLibraryResponse
            {
                Type = SkillLibraryResponse.ContractType,
                ContractVersion = SkillLibraryResponse.CurrentContractVersion,
                Skills = new List<SkillPackageDto>()
            };
        }

        private static ToolLibraryResponse EmptyToolLibrary()
        {
            return new ToolLibraryResponse
            {
                Type = ToolLibraryResponse.ContractType,
                ContractVersion = ToolLibraryResponse.CurrentContractVersion,
                Tools = new List<ToolLibraryItemDto>()
            };
        }
        public ChatStateResponse ConfirmAgentTool(string pendingId, string chatId = null) { return ChatState(pendingId, chatId); }
        public Task<ChatStateResponse> ConfirmAgentToolAsync(
            string pendingId,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null,
            Action<ChatStateResponse> chatStateChanged = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatId = chatId;
            LastRunId = runId;
            if (progress != null)
            {
                progress("executing", "Testing confirm", new ChatActivity { Kind = "tool", Title = pendingId, Status = "running" });
            }
            if (chatStateChanged != null)
            {
                chatStateChanged(ChatState("Confirmed artifact", chatId));
            }
            return Task.FromResult(ChatState(pendingId, chatId));
        }
        public ChatStateResponse CancelAgentTool(string pendingId, string chatId = null) { return ChatState(pendingId, chatId); }
        public PromptContextInspectorResponse InspectPromptContext(string chatId, string text, IReadOnlyList<string> resourceDraftIds, bool includeRaw)
        {
            return new PromptContextInspectorResponse
            {
                ChatId = chatId,
                Mode = "agent",
                Model = "test-model",
                UsedTokens = 10,
                InputLimitTokens = 100,
                ContextWindowTokens = 128,
                ReservedOutputTokens = 20,
                SafetyTokens = 8,
                RemainingInputTokens = 90,
                Percent = 10,
                Estimated = true,
                Sections = new PromptContextSectionDto[0],
                RawRequestJson = includeRaw ? "{}" : null
            };
        }
        public VbaProjectResponse GetVbaProject() { return new VbaProjectResponse { Result = ToolRunResult.Ok("ok") }; }
        public ToolRunResult GetVbaModule(string moduleName) { LastModuleName = moduleName; return ToolRunResult.Ok("read"); }
        public VbaMutationQueryResponse GetVbaMutations(VbaMutationQueryPayload request)
        {
            LastVbaMutationCursor = request == null ? null : request.Cursor;
            return new VbaMutationQueryResponse { View = "vba-mutations", Rows = new VbaMutationRowDto[0] };
        }
        public VbaMutationDetailResponse GetVbaMutationDetail(string mutationId)
        {
            LastVbaMutationId = mutationId;
            return new VbaMutationDetailResponse { MutationId = mutationId, Components = new VbaMutationComponentDto[0] };
        }

        public ToolRunResult SaveVbaModule(string moduleName, string code, string expectedCodeSha256 = null)
        {
            LastModuleName = moduleName;
            LastModuleCode = code;
            LastModuleHash = expectedCodeSha256;
            return ToolRunResult.Ok("saved");
        }

        public ToolRunResult CreateVbaModule(string moduleName, string componentType, string code)
        {
            LastModuleName = moduleName;
            LastModuleType = componentType;
            LastModuleCode = code;
            return ToolRunResult.Ok("created");
        }

        public ToolRunResult DeleteVbaModule(string moduleName)
        {
            LastModuleName = moduleName;
            return ToolRunResult.Ok("deleted");
        }

        public ToolRunResult RestoreVbaBackup(string backupId, string moduleName) { return ToolRunResult.Ok("restored"); }
        public ToolRunResult RunVbaMacro(string macroName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastModuleName = macroName;
            return ToolRunResult.Ok("ran macro");
        }
        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) }; }
        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(new HtmlWorkspace { ActiveFileId = path ?? string.Empty }) }; }
        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) }; }
        public UploadedHtmlSourcePreviewDto GetUploadedHtmlSourcePreview(string chatId, string sourceResourceUri)
        {
            LastChatId = chatId;
            LastHtmlSourceResourceUri = sourceResourceUri;
            return new UploadedHtmlSourcePreviewDto
            {
                SourceResourceUri = sourceResourceUri,
                Text = "<main>preview</main>",
                Complete = true
            };
        }
        public ArtifactViewerPageDto ReadArtifactViewerPage(string chatId, string resourceUri, string cursor)
        {
            LastChatId = chatId;
            LastArtifactViewerResourceUri = resourceUri;
            LastArtifactViewerCursor = cursor;
            return new ArtifactViewerPageDto
            {
                ResourceUri = resourceUri,
                ViewerKind = "markdown",
                Title = "Plan.md",
                MimeType = "text/markdown",
                ContentSha256 = new string('b', 64),
                Text = "# Exact",
                Offset = 32000,
                ReturnedCharacters = 7,
                TotalCharacters = 32007,
                Complete = true,
                SourceComplete = true,
                FullReadAllowed = true,
                MaximumDocumentCharacters = 512000
            };
        }
        public HtmlWorkspaceResponse ImportUploadedHtmlToWorkspace(
            string chatId,
            string sourceResourceUri,
            string expectedActiveHtmlArtifactId,
            string targetPath)
        {
            LastChatId = chatId;
            LastHtmlSourceResourceUri = sourceResourceUri;
            LastExpectedHtmlArtifactId = expectedActiveHtmlArtifactId;
            LastHtmlPath = targetPath;
            return new HtmlWorkspaceResponse
            {
                ActiveChatId = chatId ?? string.Empty,
                ImportedPath = targetPath,
                ImportedFromResourceUri = sourceResourceUri,
                Workspace = HtmlWorkspaceDto.From(null)
            };
        }
        public HtmlWorkspaceResponse PrepareHtmlWorkspaceExport(string chatId, string expectedActiveHtmlArtifactId)
        {
            LastChatId = chatId;
            LastExpectedHtmlArtifactId = expectedActiveHtmlArtifactId;
            return new HtmlWorkspaceResponse
            {
                ActiveChatId = chatId ?? string.Empty,
                ActiveHtmlArtifactId = expectedActiveHtmlArtifactId,
                ExportRevisionArtifactId = expectedActiveHtmlArtifactId,
                ExportResourceUri = "rna://chat/" + chatId + "/artifact/" + expectedActiveHtmlArtifactId + "/revision/3",
                ExportContentSha256 = new string('a', 64),
                Workspace = HtmlWorkspaceDto.From(null)
            };
        }
        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(string chatId, string path)
        {
            LastChatId = chatId;
            LastHtmlPath = path;
            return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) };
        }
        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(string chatId, string name)
        {
            LastChatId = chatId;
            LastHtmlDataName = name;
            return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) };
        }
        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(new HtmlWorkspace { ActiveFileId = path ?? string.Empty }) }; }
        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(string chatId, string snapshotId) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) }; }
        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(string chatId, string snapshotId) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = HtmlWorkspaceDto.From(null) }; }
        public object AllowHtmlNetworkOrigin(string origin) { return new { origin = origin, allowed = true }; }
        public Task<HtmlFetchResponse> HtmlFetchAsync(HtmlFetchRequest request, CancellationToken cancellationToken) { return Task.FromResult(new HtmlFetchResponse { Url = request == null ? "" : request.Url, Status = 200, Body = "ok", Headers = new Dictionary<string, string>() }); }
        public DocumentContext GetContext(string chatId = null) { return new DocumentContext { DocumentKey = chatId ?? string.Empty }; }
        public DocumentContext AddSelectionContextFromBridge(string mode, string chatId = null) { return new DocumentContext { Title = mode ?? string.Empty }; }

        public DocumentContext AddTextContext(string kind, string title, string reference, string text, string detailsJson, string chatId = null)
        {
            LastContextKind = kind;
            LastContextTitle = title;
            LastContextReference = reference;
            LastContextText = text;
            LastChatId = chatId;
            return new DocumentContext { Title = kind ?? string.Empty };
        }

        public DocumentContext RemoveContextItem(string id, string chatId = null) { return new DocumentContext { Title = id ?? string.Empty }; }
        public DocumentContext ClearContext(string chatId = null) { return new DocumentContext { DocumentKey = chatId ?? string.Empty }; }
        public Task<QuickActionResponse> RunQuickActionAsync(string action) { return Task.FromResult(new QuickActionResponse { Prompt = action }); }

        public Task<SendChatResponse> SendChatAsync(
            string text,
            string chatId = null,
            IReadOnlyList<string> resourceDraftIds = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatText = text;
            LastChatId = chatId;
            LastResourceDraftIds = resourceDraftIds ?? new string[0];
            if (progress != null)
            {
                progress("thinking", "Testing progress", new ChatActivity { Kind = "notice", Title = "Testing progress", Status = "running" });
                progress("streaming", string.Empty, null);
                progress("streaming", "Hel", null);
            }
            if (chatStateChanged != null)
            {
                chatStateChanged(ChatState("Generated title", chatId));
            }
            return Task.FromResult(new SendChatResponse
            {
                SessionRevision = 8,
                RunViewState = new RunViewState("run-bridge", "turn-bridge", "ok",
                    RunViewLifecycles.Completed, RunViewHealth.Clean, 0, 0, 0, 0, 0, 0,
                    null, null, "ok", DateTime.UtcNow),
                Message = "ok",
                Tools = new ToolLibraryResponse
                {
                    Type = ToolLibraryResponse.ContractType,
                    ContractVersion = ToolLibraryResponse.CurrentContractVersion,
                    Tools = new List<ToolLibraryItemDto>
                    {
                        new ToolLibraryItemDto
                        {
                            Revision = "generated-revision",
                            Id = "common.generated_tool",
                            Host = "Common",
                            Name = "common.generated_tool",
                            Description = string.Empty,
                            ArgumentSchemaJson = "{}",
                            Executor = "builtin",
                            Enabled = true,
                            BuiltIn = true,
                            AgentCanRun = true,
                            Components = new List<ToolPackageComponentDto>(),
                            ArgumentOrder = new List<string>()
                        }
                    }
                },
                Skills = new SkillLibraryResponse
                {
                    Type = SkillLibraryResponse.ContractType,
                    ContractVersion = SkillLibraryResponse.CurrentContractVersion,
                    Skills = new List<SkillPackageDto>
                    {
                        new SkillPackageDto
                        {
                            Revision = new string('a', 64),
                            Id = "common.generated_skill",
                            Host = "Common",
                            Name = "Generated skill",
                            Description = "Generated skill.",
                            Version = "1.0.0",
                            BodyMarkdown = "# Generated",
                            Enabled = true,
                            BuiltIn = false,
                            References = new List<SkillReferenceDto>()
                        }
                    }
                }
            });
        }

        public ChatResourceDraftResponse StageChatResource(
            string chatId,
            string fileName,
            string contentType,
            string base64)
        {
            LastChatId = chatId;
            LastResourceFileName = fileName;
            return new ChatResourceDraftResponse
            {
                Resource = new ChatAttachment { Id = "resource-draft", FileName = fileName, ContentType = contentType, Kind = "image" }
            };
        }

        public object DiscardChatResourceDraft(string chatId, string id)
        {
            LastChatId = chatId;
            LastResourceDraftId = id;
            return new { deleted = true };
        }

        public ToolRunResult RunTool(string toolId, IDictionary<string, object> arguments, bool dryRun, Action<string, string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastToolId = toolId;
            LastArgumentsJson = JsonConvert.SerializeObject(arguments ?? new Dictionary<string, object>());
            LastDryRun = dryRun;
            if (progress != null)
            {
                progress("executing", "Testing tool");
            }
            return ToolRunResult.Ok("ran", "{\"ran\":true}");
        }

        private static ChatStateResponse ChatState(string title = null, string chatId = null)
        {
            return new ChatStateResponse
            {
                ActiveChatId = chatId ?? string.Empty,
                ActiveChatModel = title ?? string.Empty,
                ActiveChatMode = "chat",
                Chats = new ChatSessionSummary[0],
                Context = new DocumentContext(),
                Messages = new ChatMessage[0]
            };
        }

        private static QualificationSessionResponse QualificationState(string chatId, string runId)
        {
            return new QualificationSessionResponse
            {
                SchemaVersion = 1,
                Chat = ChatState(null, chatId),
                Run = string.IsNullOrWhiteSpace(runId) ? null : new QualificationRunDto
                {
                    RunId = runId,
                    PackId = "common.ui-shell",
                    Status = "awaiting_user",
                    CurrentStepId = "acknowledge",
                    CanResume = true,
                    Steps = new QualificationStepResultDto[0]
                }
            };
        }
    }
}
