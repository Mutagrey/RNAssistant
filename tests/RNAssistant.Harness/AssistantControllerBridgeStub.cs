using System;
using System.Threading.Tasks;

namespace RNAssistant.Office
{
    public sealed class AssistantController
    {
        public string LastToolId { get; private set; }
        public string LastArgumentsJson { get; private set; }
        public bool LastDryRun { get; private set; }
        public string LastChatText { get; private set; }
        public string LastChatId { get; private set; }
        public string LastSettingsJson { get; private set; }
        public string LastApiKey { get; private set; }
        public string LastModuleName { get; private set; }
        public string LastModuleCode { get; private set; }
        public string LastContextKind { get; private set; }
        public string LastContextTitle { get; private set; }
        public string LastContextReference { get; private set; }
        public string LastContextText { get; private set; }

        public string InitializeJson() { return "{\"initialized\":true}"; }
        public string ListChatsJson() { return "{\"chats\":[]}"; }
        public string CreateChatJson(string title) { return "{\"title\":\"" + Escape(title) + "\"}"; }
        public string SelectChatJson(string chatId) { return "{\"chatId\":\"" + Escape(chatId) + "\"}"; }
        public string RenameChatJson(string chatId, string title) { return "{\"chatId\":\"" + Escape(chatId) + "\",\"title\":\"" + Escape(title) + "\"}"; }
        public string SetChatModelJson(string chatId, string model) { return "{\"chatId\":\"" + Escape(chatId) + "\",\"model\":\"" + Escape(model) + "\"}"; }
        public string ClearChatJson(string chatId) { return "{\"chatId\":\"" + Escape(chatId) + "\"}"; }
        public string DeleteChatJson(string chatId) { return "{\"chatId\":\"" + Escape(chatId) + "\"}"; }
        public string DeleteMessageJson(string id, int index, string chatId = null) { return "{\"id\":\"" + Escape(id) + "\",\"index\":" + index + "}"; }
        public string ForkChatJson(string id, int index, string chatId = null) { return "{\"id\":\"" + Escape(id) + "\",\"index\":" + index + "}"; }
        public string GetSettingsJson() { return "{\"settings\":{}}"; }
        public Task<string> GetModelCatalogJsonAsync(string settingsJson, string apiKey) { return Task.FromResult("{\"catalog\":{}}"); }
        public string SaveSettingsJson(string settingsJson, string apiKey)
        {
            LastSettingsJson = settingsJson;
            LastApiKey = apiKey;
            return "{\"settings\":{}}";
        }
        public string ClearRuntimeDataJson() { return "{\"cleared\":true}"; }
        public string GetToolsJson() { return "[]"; }
        public string SaveToolsJson(string toolsJson) { return "[]"; }
        public string GetVbaProjectJson(int maxChars) { return "{\"maxChars\":" + maxChars + "}"; }
        public string SaveVbaModuleJson(string moduleName, string code)
        {
            LastModuleName = moduleName;
            LastModuleCode = code;
            return "{\"moduleName\":\"" + Escape(moduleName) + "\"}";
        }
        public string RestoreVbaBackupJson(string backupId, string moduleName) { return "{\"backupId\":\"" + Escape(backupId) + "\"}"; }
        public string GetContextJson(string chatId = null) { return "{\"chatId\":\"" + Escape(chatId) + "\"}"; }
        public string AddSelectionContextJson(string mode, string chatId = null) { return "{\"mode\":\"" + Escape(mode) + "\"}"; }
        public string AddTextContextJson(string kind, string title, string reference, string text, string detailsJson, string chatId = null)
        {
            LastContextKind = kind;
            LastContextTitle = title;
            LastContextReference = reference;
            LastContextText = text;
            LastChatId = chatId;
            return "{\"kind\":\"" + Escape(kind) + "\"}";
        }
        public string AddVbaContextJson(string chatId = null, int maxChars = 0) { return "{\"maxChars\":" + maxChars + "}"; }
        public string RemoveContextItemJson(string id, string chatId = null) { return "{\"id\":\"" + Escape(id) + "\"}"; }
        public string ClearContextJson(string chatId = null) { return "{\"chatId\":\"" + Escape(chatId) + "\"}"; }
        public Task<string> RunQuickActionAsync(string action) { return Task.FromResult("{\"prompt\":\"" + Escape(action) + "\"}"); }

        public Task<string> SendChatAsync(string text, string chatId = null, Action<string, string> progress = null)
        {
            LastChatText = text;
            LastChatId = chatId;
            if (progress != null)
            {
                progress("thinking", "Testing progress");
            }
            return Task.FromResult("{\"message\":\"ok\"}");
        }

        public string RunToolJson(string toolId, string argumentsJson, bool dryRun, Action<string, string> progress = null)
        {
            LastToolId = toolId;
            LastArgumentsJson = argumentsJson;
            LastDryRun = dryRun;
            if (progress != null)
            {
                progress("executing", "Testing tool");
            }
            return "{\"ran\":true}";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
