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

        public InitResponse Initialize() { return new InitResponse { Host = "Excel", Title = "Harness.xlsx" }; }
        public ChatStateResponse ListChats() { return ChatState(); }
        public ChatStateResponse CreateChat(string title) { return ChatState(title); }
        public ChatStateResponse SelectChat(string chatId) { return ChatState(null, chatId); }
        public ChatStateResponse RenameChat(string chatId, string title) { return ChatState(title, chatId); }
        public ChatStateResponse SetChatModel(string chatId, string model) { return ChatState(model, chatId); }
        public ChatStateResponse ClearChat(string chatId) { return ChatState(null, chatId); }
        public ChatStateResponse DeleteChat(string chatId) { return ChatState(null, chatId); }
        public ChatStateResponse DeleteMessage(string id, int index, string chatId = null) { return ChatState(id, chatId); }
        public ChatStateResponse ForkChat(string id, int index, string chatId = null) { return ChatState(id, chatId); }
        public ChatStateResponse UpdateMessageActivityData(string messageId, string dataJson, string chatId = null) { return ChatState(messageId, chatId); }
        public SettingsResponse GetSettings() { return new SettingsResponse { Settings = new AppSettings(), HasApiKey = false }; }
        public Task<ModelCatalogResponse> GetModelCatalogAsync(AppSettings settings, string apiKey) { return Task.FromResult(new ModelCatalogResponse { Catalog = new JObject() }); }

        public SettingsResponse SaveSettings(AppSettings settings, string apiKey)
        {
            LastSettings = settings;
            LastApiKey = apiKey;
            return GetSettings();
        }

        public InitResponse ClearRuntimeData() { return Initialize(); }
        public IReadOnlyList<ToolDefinition> GetTools() { return new ToolDefinition[0]; }
        public IReadOnlyList<ToolDefinition> SaveTools(IEnumerable<ToolDefinition> tools)
        {
            LastToolsJson = JsonConvert.SerializeObject(tools ?? new ToolDefinition[0]);
            return new ToolDefinition[0];
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
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public DocumentContext AddVbaContext(string chatId = null, int maxChars = 0) { return new DocumentContext { Title = maxChars.ToString() }; }
        public DocumentContext RemoveContextItem(string id, string chatId = null) { return new DocumentContext { Title = id ?? string.Empty }; }
        public DocumentContext ClearContext(string chatId = null) { return new DocumentContext { DocumentKey = chatId ?? string.Empty }; }
        public Task<QuickActionResponse> RunQuickActionAsync(string action) { return Task.FromResult(new QuickActionResponse { Prompt = action }); }

        public Task<SendChatResponse> SendChatAsync(
            string text,
            string chatId = null,
            Action<string, string, ChatActivity> progress = null,
            Action<ChatStateResponse> chatStateChanged = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastChatText = text;
            LastChatId = chatId;
            if (progress != null)
            {
                progress("thinking", "Testing progress", new ChatActivity { Kind = "notice", Title = "Testing progress", Status = "running" });
            }
            if (chatStateChanged != null)
            {
                chatStateChanged(ChatState("Generated title", chatId));
            }
            return Task.FromResult(new SendChatResponse { Message = "ok" });
        }

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
                Chats = new ChatSessionSummary[0],
                Context = new DocumentContext(),
                Messages = new ChatMessage[0]
            };
        }
    }
}
