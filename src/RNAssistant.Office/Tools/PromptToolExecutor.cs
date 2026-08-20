using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Tools
{
    internal sealed class PromptToolExecutor
    {
        private readonly Func<AppSettings> _loadSettings;
        private readonly Action<AppSettings> _saveSettings;

        public PromptToolExecutor(Func<AppSettings> loadSettings, Action<AppSettings> saveSettings)
        {
            _loadSettings = loadSettings;
            _saveSettings = saveSettings;
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            if (_loadSettings == null)
            {
                yield break;
            }

            yield return ControllerToolDefinition.Create("common.prompts_read", "Common", "Read-only: Read RNAssistant editable chat and agent prompt templates from Settings.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", name: "prompts_read");
            yield return ControllerToolDefinition.Create("common.prompts_read_defaults", "Common", "Read-only: Read current RNAssistant prompts and built-in default prompt templates.", "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}", name: "prompts_read_defaults");
            yield return ControllerToolDefinition.Create(
                "common.prompts_save",
                "Common",
                "Mutates settings: Update RNAssistant Agent, Chat, compaction, or title prompts after the user asks to edit them.",
                "{\"type\":\"object\",\"properties\":{" +
                    "\"systemPrompt\":{\"type\":\"string\"}," +
                    "\"chatSystemPrompt\":{\"type\":\"string\"}," +
                    "\"systemPromptRole\":{\"type\":\"string\",\"enum\":[\"developer\",\"system\",\"user\"]}," +
                    "\"contextCompactionPrompt\":{\"type\":\"string\"}," +
                    "\"chatTitlePrompt\":{\"type\":\"string\"}}," +
                    "\"required\":[],\"additionalProperties\":false}",
                mutatesLocalState: true,
                requiresConfirmation: true,
                riskLevel: 1,
                name: "prompts_save");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, AppSettings runtimeSettings, bool dryRun)
        {
            if (_loadSettings == null)
            {
                return ToolResult.Fail("Prompt settings store is not available.");
            }

            if (string.Equals(command.ToolId, "common.prompts_read", StringComparison.OrdinalIgnoreCase))
            {
                var current = _loadSettings();
                return ToolResult.Ok("RNAssistant prompt templates read.", JsonConvert.SerializeObject(ToPayload(current)));
            }

            if (string.Equals(command.ToolId, "common.prompts_read_defaults", StringComparison.OrdinalIgnoreCase))
            {
                var current = _loadSettings();
                return ToolResult.Ok("RNAssistant prompt templates and defaults read.", JsonConvert.SerializeObject(new
                {
                    current = ToPayload(current),
                    defaults = ToPayload(new AppSettings())
                }));
            }

            if (string.Equals(command.ToolId, "common.prompts_save", StringComparison.OrdinalIgnoreCase))
            {
                return SavePrompts(command, runtimeSettings, dryRun);
            }

            return ToolResult.Fail("Unknown prompt controller tool: " + command.ToolId);
        }

        private ToolResult SavePrompts(ToolCommand command, AppSettings runtimeSettings, bool dryRun)
        {
            if (_saveSettings == null)
            {
                return ToolResult.Fail("Prompt settings store is read-only.");
            }

            var source = runtimeSettings ?? _loadSettings() ?? new AppSettings();
            var settings = dryRun
                ? source.Clone()
                : source;
            ApplyIfPresent(command, "systemPrompt", value => settings.SystemPrompt = value);
            ApplyIfPresent(command, "chatSystemPrompt", value => settings.ChatSystemPrompt = value);
            ApplyIfPresent(command, "systemPromptRole", value => settings.SystemPromptRole = NormalizePromptRole(value));
            ApplyIfPresent(command, "contextCompactionPrompt", value => settings.ContextCompactionPrompt = value);
            ApplyIfPresent(command, "chatTitlePrompt", value => settings.ChatTitlePrompt = value);

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would save RNAssistant prompt templates.", JsonConvert.SerializeObject(ToPayload(settings)));
            }

            _saveSettings(settings);
            var saved = _loadSettings();
            return ToolResult.Ok("RNAssistant prompt templates saved.", JsonConvert.SerializeObject(ToPayload(saved)));
        }

        private static void ApplyIfPresent(ToolCommand command, string name, Action<string> apply)
        {
            if (command == null || command.Arguments == null || !command.Arguments.ContainsKey(name))
            {
                return;
            }

            apply(ToolArgumentReader.String(command.Arguments, name, string.Empty));
        }

        private static object ToPayload(AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            return new
            {
                systemPrompt = settings.SystemPrompt,
                chatSystemPrompt = settings.ChatSystemPrompt,
                systemPromptRole = settings.SystemPromptRole,
                contextCompactionPrompt = settings.ContextCompactionPrompt,
                chatTitlePrompt = settings.ChatTitlePrompt
            };
        }

        private static string NormalizePromptRole(string value)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }

    }
}
