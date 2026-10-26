using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RNAssistant.Office.Contracts
{
    // This closed vocabulary owns dispatch identity, cancellation and UI idle waits.
    // The existing switch keeps each operation's typed request/response boundary.
    public enum BridgeOperationId
    {
        ReportClientTiming,
        [BridgeOperationPolicy(false, 30)] Init,
        GetPlugins, GetPluginIcons, SavePluginIcon, DeletePluginIcon, UpdatePluginAppearance, [BridgeOperationPolicy(true, 360)] DrawPluginIcon,
        CreatePlugin, ConnectPlugin, GetPluginSourceState, CheckPlugin, ApplyPlugin, GetPluginVersions, RestorePluginVersion, OpenPlugin, ClosePlugin, DisablePlugin,
        StagePluginVisualSelection, DiscardPluginVisualSelection,
        GetRemovedPlugins, [BridgeOperationPolicy(true, 300)] RemovePlugin, RestorePlugin,
        GetPluginChat,
        GetPluginVendors, ImportPluginVendor, ResolveHtmlVendor,
        GetProjectChats, ConnectProjectWorkspace, CreateProjectWorkspace, CreateProjectChat, ForkProjectChat, GetProjectChat,
        GetPreviousProjectChatMessages, GetProjectToolResultPresentation, DeleteProjectChatMessage,
        RenameProjectChat, DeleteProjectChat, DeleteProjectHistory,
        [BridgeOperationPolicy(true, 300)] SubmitProjectChat, [BridgeOperationPolicy(true, 300)] ResolveProjectChat, CancelProjectChat,
        BeginProjectChatFileUpload, WriteProjectChatFileChunk, [BridgeOperationPolicy(true, 300)] CompleteProjectChatFileUpload, CancelProjectChatFileUpload,
        GetWorkspaceState,
        OpenWorkspace,
        CreateTabularProject,
        OpenTabularProject,
        SaveTabularProjectSources,
        SaveTabularProjectDraft,
        [BridgeOperationPolicy(true, 300)] ListTabularProjectDrafts,
        [BridgeOperationPolicy(true, 300)] OpenTabularProjectDraft,
        [BridgeOperationPolicy(true, 300)] ListTabularProjectTemplates,
        [BridgeOperationPolicy(true, 300)] OpenTabularProjectTemplate,
        [BridgeOperationPolicy(true, 300)] ReadTabularProjectDictionary,
        ReconcileWorkspaceFile,
        PrepareWorkspaceSources,
        BrowseWorkspaceFiles,
        GetWorkspaceSourceQueue,
        ControlWorkspaceSourceQueue,
        ListChats,
        GetChatDrafts,
        SaveChatDraft,
        GetChatState,
        GetPreviousChatMessages,
        [BridgeOperationPolicy(true, 300)] GetChatTrajectory,
        [BridgeOperationPolicy(true, 300)] ExportChatTrajectory,
        [BridgeOperationPolicy(true, 300)] GetChatEventPayload,
        CreateChat,
        CreateDocumentChat,
        SelectChat,
        OpenDocument,
        ActivateDocument,
        AttachOutlookMailbox,
        CreateOfficeHostChat,
        DeleteDocument,
        RenameChat,
        SetChatModel,
        SetChatMode,
        SetChatMutationPolicy,
        SetChatReasoning,
        AllowHtmlNetworkOrigin,
        [BridgeOperationPolicy(true, 300)] HtmlFetch,
        ClearChat,
        [BridgeOperationPolicy(true, 300)] CompactChatContext,
        DeleteChat,
        StopChatInbox,
        GetChatInbox,
        SubmitChatInput,
        EditChatInput,
        RemoveChatInput,
        SteerChatInput,
        ResumeChatInbox,
        [BridgeOperationPolicy(true, 300)] BeginChatResourceUpload,
        [BridgeOperationPolicy(true, 300)] CompleteChatResourceUpload,
        CancelChatResourceUpload,
        DiscardChatResourceDraft,
        DeleteMessage,
        ForkChat,
        [BridgeOperationPolicy(true, 300)] EditMessage,
        UpdateMessageActivityData,
        GetSettings,
        GetCasHealth,
        CollectCasGarbage,
        [BridgeOperationPolicy(true, 300)] GetModelCatalog,
        [BridgeOperationPolicy(true, 300)] SaveSettings,
        [BridgeOperationPolicy(true, 300)] ReadPromptSource,
        [BridgeOperationPolicy(true, 300)] BeginPromptMutationUpload,
        CancelPromptMutationUpload,
        [BridgeOperationPolicy(true, 300)] TestModelCompatibility,
        [BridgeOperationPolicy(true, 300)] TestModelConnection,
        ClearRuntimeData,
        GetTools,
        [BridgeOperationPolicy(true, 300)] GetToolDocumentation,
        [BridgeOperationPolicy(true, 300)] SaveTools,
        [BridgeOperationPolicy(true, 300)] ReadToolSource,
        [BridgeOperationPolicy(true, 300)] BeginToolMutationUpload,
        CancelToolMutationUpload,
        InstallVbaTool,
        UninstallVbaTool,
        GetSkills,
        [BridgeOperationPolicy(true, 300)] SaveSkills,
        [BridgeOperationPolicy(true, 300)] BeginSkillMutationUpload,
        CancelSkillMutationUpload,
        [BridgeOperationPolicy(true, 300)] ReadSkillSource,
        [BridgeOperationPolicy(true, 300)] SaveSkillReference,
        DeleteSkillReference,
        [BridgeOperationPolicy(true, 300)] RunTool,
        [BridgeOperationPolicy(true, 300)] ConfirmAgentTool,
        CancelAgentTool,
        GetVbaProject,
        [BridgeOperationPolicy(true, 300)] GetVbaModule,
        GetVbaMutations,
        GetToolResultPresentation,
        GetRunChanges,
        GetVbaMutationDetail,
        [BridgeOperationPolicy(true, 300)] SaveVbaModule,
        [BridgeOperationPolicy(true, 300)] CreateVbaModule,
        [BridgeOperationPolicy(true, 300)] BeginVbaModuleUpload,
        CancelVbaModuleUpload,
        DeleteVbaModule,
        RestoreVbaBackup,
        [BridgeOperationPolicy(true, 300)] RunVbaMacro,
        [BridgeOperationPolicy(true, 300)] ResourceDataOpen,
        ResourceDataClose,
        GetHtmlWorkspace,
        [BridgeOperationPolicy(true, 300)] ReadHtmlWorkspaceSource,
        [BridgeOperationPolicy(true, 300)] SaveHtmlWorkspaceFile,
        [BridgeOperationPolicy(true, 300)] SaveHtmlWorkspaceData,
        [BridgeOperationPolicy(true, 300)] BeginHtmlWorkspaceMutationUpload,
        CancelHtmlWorkspaceMutationUpload,
        ListArtifactCatalog,
        GetArtifactLibraryPage,
        GetArtifactLibraryHistory,
        ExportArtifact,
        CopyArtifact,
        BeginArtifactImport,
        ImportArtifact,
        CloseArtifactTransfer,
        ChangeArtifactLink,
        DeleteDocumentArtifact,
        [BridgeOperationPolicy(true, 300)] ReadArtifactViewerPage,
        [BridgeOperationPolicy(true, 300)] ReadArtifactImage,
        [BridgeOperationPolicy(true, 300)] ReadArtifactImageThumbnail,
        ReadArtifactPdfInfo,
        [BridgeOperationPolicy(true, 300)] ReadArtifactPdfPage,
        [BridgeOperationPolicy(true, 300)] ReadArtifactPdfThumbnail,
        ImportUploadedHtmlToWorkspace,
        PrepareHtmlWorkspaceExport,
        DeleteHtmlWorkspaceFile,
        DeleteHtmlWorkspaceData,
        SetActiveHtmlWorkspaceFile,
        RestoreHtmlWorkspaceSnapshot,
        RedoHtmlWorkspaceSnapshot,
        GetContext,
        [BridgeOperationPolicy(true, 300)] GetModelContext,
        [BridgeOperationPolicy(true, 300)] GetModelContextPayload,
        InspectModelContextPreview,
        InspectPromptContext,
        GetAgentArsenal,
        StageSelectionInput,
        StageHtmlVisualSelection,
        AddSelectionContext,
        AddTextContext,
        RemoveContextItem,
        ClearContext,
        QuickAction,
        CancelRequest,
        CancelChatRun,
    }

    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class BridgeOperationPolicyAttribute : Attribute
    {
        internal bool Cancellable { get; private set; }
        internal int IdleTimeoutSeconds { get; private set; }
        public BridgeOperationPolicyAttribute(bool cancellable = false, int idleTimeoutSeconds = 120)
        { Cancellable = cancellable; IdleTimeoutSeconds = idleTimeoutSeconds; }
    }

    public sealed class BridgeOperationDescriptor
    {
        [JsonIgnore] public BridgeOperationId Id { get; private set; }
        [JsonProperty("name")] public string Name { get; private set; }
        [JsonProperty("cancellable")] public bool Cancellable { get; private set; }
        [JsonProperty("idleTimeoutMs")] public int IdleTimeoutMs { get; private set; }

        internal BridgeOperationDescriptor(BridgeOperationId id)
        {
            Id = id;
            var name = id.ToString();
            Name = char.ToLowerInvariant(name[0]) + name.Substring(1);
            var policy = (BridgeOperationPolicyAttribute)Attribute.GetCustomAttribute(
                typeof(BridgeOperationId).GetField(name), typeof(BridgeOperationPolicyAttribute))
                ?? new BridgeOperationPolicyAttribute();
            Cancellable = policy.Cancellable;
            IdleTimeoutMs = policy.IdleTimeoutSeconds * 1000;
        }
    }

    public sealed class BridgeContractDto
    {
        [JsonProperty("version")] public int Version { get { return BridgeOperationCatalog.Version; } }
        [JsonProperty("operations")] public IReadOnlyList<BridgeOperationDescriptor> Operations
        { get { return BridgeOperationCatalog.Operations; } }
    }

    public static class BridgeOperationCatalog
    {
        public const int Version = 2;
        public static readonly IReadOnlyList<BridgeOperationDescriptor> Operations = Array.AsReadOnly(
            Enum.GetValues(typeof(BridgeOperationId)).Cast<BridgeOperationId>()
                .Select(id => new BridgeOperationDescriptor(id)).ToArray());
        private static readonly Dictionary<string, BridgeOperationDescriptor> ByName =
            Operations.ToDictionary(operation => operation.Name, StringComparer.Ordinal);

        public static BridgeOperationDescriptor Resolve(string name)
        {
            BridgeOperationDescriptor operation;
            if (!ByName.TryGetValue(name ?? string.Empty, out operation))
                throw new InvalidOperationException("Unknown bridge message: " + name);
            return operation;
        }
    }
}
