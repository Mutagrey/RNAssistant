using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class AssistantController
    {
        public string LastToolId { get; private set; }
        public string LastArgumentsJson { get; private set; }
        public bool LastDryRun { get; private set; }
        public string LastChatText { get; private set; }
        public string LastChatId { get; private set; }
        public string LastChatMode { get; private set; }
        public bool LastChatReasoning { get; private set; }
        public string LastRunId { get; private set; }
        public AppSettings LastSettings { get; private set; }
        public string LastApiKey { get; private set; }
        public string LastModuleName { get; private set; }
        public string LastModuleCode { get; private set; }
        public string LastContextKind { get; private set; }
        public string LastContextTitle { get; private set; }
        public string LastContextReference { get; private set; }
        public string LastContextText { get; private set; }
        public string LastToolsJson { get; private set; }
        public string LastSkillsJson { get; private set; }
        public string LastDocumentHost { get; private set; }
        public string LastHtmlPath { get; private set; }
        public string LastHtmlDataName { get; private set; }

        public InitResponse Initialize() { return new InitResponse { Host = "Excel", Title = "Harness.xlsx" }; }
        public ChatStateResponse ListChats() { return ChatState(); }
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
        public ChatStateResponse SetChatHtmlMode(string chatId, bool enabled) { return ChatState(enabled ? "html" : string.Empty, chatId); }
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
        public SettingsResponse GetSettings() { return new SettingsResponse { Settings = new AppSettings(), HasApiKey = false }; }
        public RuntimeLogResponse GetRuntimeLog() { return new RuntimeLogResponse { Content = "runtime log", Path = "runtime.log" }; }
        public RuntimeLogResponse ClearRuntimeLog() { return new RuntimeLogResponse { Content = string.Empty, Path = "runtime.log" }; }
        public Task<ModelCatalogResponse> GetModelCatalogAsync(AppSettings settings, string apiKey) { return Task.FromResult(new ModelCatalogResponse { Catalog = new JObject() }); }

        public SettingsResponse SaveSettings(AppSettings settings, string apiKey)
        {
            LastSettings = settings;
            LastApiKey = apiKey;
            return GetSettings();
        }

        public Task<ModelCompatibilityResponse> TestModelCompatibilityAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelCompatibilityResponse
            {
                Compatible = true,
                Model = "harness-model",
                Checks = new[]
                {
                    new ModelCompatibilityCheckDto { Id = "user_role", Title = "Роль user", Passed = true, Required = true }
                }
            });
        }

        public InitResponse ClearRuntimeData() { return Initialize(); }
        public IReadOnlyList<ToolDefinition> GetTools() { return new ToolDefinition[0]; }
        public IReadOnlyList<ToolDefinition> SaveTools(IEnumerable<ToolDefinition> tools)
        {
            LastToolsJson = JsonConvert.SerializeObject(tools ?? new ToolDefinition[0]);
            return new ToolDefinition[0];
        }

        public VbaToolPackageResponse InstallVbaTool(string id, bool dryRun)
        {
            LastToolId = id;
            LastDryRun = dryRun;
            return new VbaToolPackageResponse { Result = ToolResult.Ok("installed"), Tools = new ToolDefinition[0] };
        }

        public VbaToolPackageResponse UninstallVbaTool(string id)
        {
            LastToolId = id;
            return new VbaToolPackageResponse { Result = ToolResult.Ok("uninstalled"), Tools = new ToolDefinition[0] };
        }

        public IReadOnlyList<SkillDefinition> GetSkills() { return new SkillDefinition[0]; }

        public IReadOnlyList<SkillDefinition> SaveSkills(IEnumerable<SkillDefinition> skills)
        {
            LastSkillsJson = JsonConvert.SerializeObject(skills ?? new SkillDefinition[0]);
            return new SkillDefinition[0];
        }
        public ChatStateResponse ConfirmAgentTool(string pendingId, string chatId = null) { return ChatState(pendingId, chatId); }
        public Task<ChatStateResponse> ConfirmAgentToolAsync(
            string pendingId,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            CancellationToken cancellationToken = default(CancellationToken),
            string runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatId = chatId;
            LastRunId = runId;
            if (progress != null)
            {
                progress("executing", "Testing confirm", new ChatActivity { Kind = "tool", Title = pendingId, Status = "running" });
            }
            return Task.FromResult(ChatState(pendingId, chatId));
        }
        public ChatStateResponse CancelAgentTool(string pendingId, string chatId = null) { return ChatState(pendingId, chatId); }
        public VbaProjectResponse GetVbaProject(int maxChars) { return new VbaProjectResponse { Result = ToolResult.Ok("ok") }; }

        public ToolResult SaveVbaModule(string moduleName, string code)
        {
            LastModuleName = moduleName;
            LastModuleCode = code;
            return ToolResult.Ok("saved");
        }

        public ToolResult RestoreVbaBackup(string backupId, string moduleName) { return ToolResult.Ok("restored"); }
        public HtmlWorkspaceResponse GetHtmlWorkspace(string chatId = null) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() }; }
        public HtmlWorkspaceResponse SaveHtmlWorkspaceFile(string chatId, string path, string kind, string content, bool setActive) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace { ActiveFileId = path ?? string.Empty } }; }
        public HtmlWorkspaceResponse SaveHtmlWorkspaceData(string chatId, string name, string json) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() }; }
        public HtmlWorkspaceResponse DeleteHtmlWorkspaceFile(string chatId, string path)
        {
            LastChatId = chatId;
            LastHtmlPath = path;
            return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() };
        }
        public HtmlWorkspaceResponse DeleteHtmlWorkspaceData(string chatId, string name)
        {
            LastChatId = chatId;
            LastHtmlDataName = name;
            return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() };
        }
        public HtmlWorkspaceResponse SetActiveHtmlWorkspaceFile(string chatId, string path) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace { ActiveFileId = path ?? string.Empty } }; }
        public HtmlWorkspaceResponse RestoreHtmlWorkspaceSnapshot(string chatId, string snapshotId) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() }; }
        public HtmlWorkspaceResponse RedoHtmlWorkspaceSnapshot(string chatId, string snapshotId) { return new HtmlWorkspaceResponse { ActiveChatId = chatId ?? string.Empty, Workspace = new HtmlWorkspace() }; }
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
            IReadOnlyList<string> attachmentIds = null,
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
                progress("thinking", "Testing progress", new ChatActivity { Kind = "notice", Title = "Testing progress", Status = "running" });
                progress("streaming", "Hel", null);
            }
            if (chatStateChanged != null)
            {
                chatStateChanged(ChatState("Generated title", chatId));
            }
            return Task.FromResult(new SendChatResponse { Message = "ok" });
        }

        public AttachmentResponse ImportAttachment(string fileName, string contentType, string base64)
        {
            return new AttachmentResponse
            {
                Attachment = new ChatAttachment { FileName = fileName, ContentType = contentType, Kind = "image" }
            };
        }

        public object DeleteDraftAttachment(string id) { return new { deleted = true }; }

        public ToolResult RunTool(string toolId, IDictionary<string, object> arguments, bool dryRun, Action<string, string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastToolId = toolId;
            LastArgumentsJson = JsonConvert.SerializeObject(arguments ?? new Dictionary<string, object>());
            LastDryRun = dryRun;
            if (progress != null)
            {
                progress("executing", "Testing tool");
            }
            return ToolResult.Ok("ran", "{\"ran\":true}");
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
    }
}
