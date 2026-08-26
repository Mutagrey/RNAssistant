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

            yield return ControllerToolDefinition.Create("common.prompts_read", "Common", "Read-only: Read current RNAssistant Markdown prompts, optionally including built-in defaults in the same result.", "{\"type\":\"object\",\"properties\":{\"includeDefaults\":{\"type\":\"boolean\",\"description\":\"Whether to include built-in defaults beside current prompts.\",\"default\":false}},\"required\":[],\"additionalProperties\":false}", name: "prompts_read");
            yield return ControllerToolDefinition.Create(
                "common.prompts_save",
                "Common",
                "Mutates settings: Update any editable RNAssistant model prompt after the user asks to edit it. Agent general, tool-use, and skill-loading policies are separate fields but are composed into one instruction message at runtime. Compatibility probes remain fixed so their diagnostics stay trustworthy.",
                "{\"type\":\"object\",\"properties\":{" +
                    "\"systemPrompt\":{\"type\":\"string\",\"description\":\"General Agent-mode Markdown: role, runtime context, response contract, and completion rules.\",\"maxLength\":100000}," +
                    "\"agentToolsPrompt\":{\"type\":\"string\",\"description\":\"Agent-wide tool selection and execution policy; tool-specific input details remain in each tool schema.\",\"maxLength\":100000}," +
                    "\"agentSkillsPrompt\":{\"type\":\"string\",\"description\":\"Agent skill discovery, mandatory loading evidence, reference reading, and precedence policy.\",\"maxLength\":100000}," +
                    "\"chatSystemPrompt\":{\"type\":\"string\",\"description\":\"Complete tool-free Chat-mode Markdown prompt.\",\"maxLength\":100000}," +
                    "\"planSystemPrompt\":{\"type\":\"string\",\"description\":\"Complete read-only Plan-mode Markdown prompt.\",\"maxLength\":100000}," +
                    "\"systemPromptRole\":{\"type\":\"string\",\"description\":\"Message role used for prompt instructions.\",\"enum\":[\"developer\",\"system\",\"user\"]}," +
                    "\"contextCompactionPrompt\":{\"type\":\"string\",\"description\":\"Markdown prompt used to compact completed history.\",\"maxLength\":100000}," +
                    "\"chatTitlePrompt\":{\"type\":\"string\",\"description\":\"Markdown prompt used to generate chat titles.\",\"maxLength\":100000}," +
                    "\"attachmentAnalysisPrompt\":{\"type\":\"string\",\"description\":\"Markdown prompt used by the auxiliary image/audio attachment analysis worker.\",\"maxLength\":100000}}," +
                    "\"required\":[],\"additionalProperties\":false}",
                mutatesLocalState: true,
                requiresConfirmation: true,
                riskLevel: 1,
                name: "prompts_save");
        }

        public ToolResult ExecuteControllerTool(ToolCommand command, bool dryRun)
        {
            if (_loadSettings == null)
            {
                return ToolResult.Fail("Prompt settings store is not available.");
            }

            if (string.Equals(command.ToolId, "common.prompts_read", StringComparison.OrdinalIgnoreCase))
            {
                var current = _loadSettings();
                if (ToolArgumentReader.Boolean(command.Arguments, "includeDefaults", false))
                {
                    return ToolResult.Ok("RNAssistant prompt templates and defaults read.", JsonConvert.SerializeObject(new
                    {
                        current = ToPayload(current),
                        defaults = ToPayload(new AppSettings())
                    }));
                }
                return ToolResult.Ok("RNAssistant prompt templates read.", JsonConvert.SerializeObject(ToPayload(current)));
            }

            if (string.Equals(command.ToolId, "common.prompts_save", StringComparison.OrdinalIgnoreCase))
            {
                return SavePrompts(command, dryRun);
            }

            return ToolResult.Fail("Unknown prompt controller tool: " + command.ToolId);
        }

        private ToolResult SavePrompts(ToolCommand command, bool dryRun)
        {
            if (command == null || command.Arguments == null || command.Arguments.Count == 0)
            {
                return ToolResult.Fail("Prompt save requires at least one supplied prompt field.", null, "prompt_update_empty", true);
            }
            if (_saveSettings == null)
            {
                return ToolResult.Fail("Prompt settings store is read-only.");
            }

            var source = _loadSettings() ?? new AppSettings();
            var settings = source.Clone();
            ApplyIfPresent(command, "systemPrompt", value => settings.SystemPrompt = value);
            ApplyIfPresent(command, "agentToolsPrompt", value => settings.AgentToolsPrompt = value);
            ApplyIfPresent(command, "agentSkillsPrompt", value => settings.AgentSkillsPrompt = value);
            ApplyIfPresent(command, "chatSystemPrompt", value => settings.ChatSystemPrompt = value);
            ApplyIfPresent(command, "planSystemPrompt", value => settings.PlanSystemPrompt = value);
            ApplyIfPresent(command, "systemPromptRole", value => settings.SystemPromptRole = NormalizePromptRole(value));
            ApplyIfPresent(command, "contextCompactionPrompt", value => settings.ContextCompactionPrompt = value);
            ApplyIfPresent(command, "chatTitlePrompt", value => settings.ChatTitlePrompt = value);
            ApplyIfPresent(command, "attachmentAnalysisPrompt", value => settings.AttachmentAnalysisPrompt = value);
            if (PromptTooLarge(settings.SystemPrompt) || PromptTooLarge(settings.AgentToolsPrompt) ||
                PromptTooLarge(settings.AgentSkillsPrompt) || PromptTooLarge(settings.ChatSystemPrompt) || PromptTooLarge(settings.PlanSystemPrompt) ||
                PromptTooLarge(settings.ContextCompactionPrompt) || PromptTooLarge(settings.ChatTitlePrompt) ||
                PromptTooLarge(settings.AttachmentAnalysisPrompt))
            {
                return ToolResult.Fail("Prompt template exceeds the 100000 character limit.", null, "prompt_too_large", false);
            }

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
                format = "markdown",
                systemPrompt = settings.SystemPrompt,
                agentToolsPrompt = settings.AgentToolsPrompt,
                agentSkillsPrompt = settings.AgentSkillsPrompt,
                chatSystemPrompt = settings.ChatSystemPrompt,
                planSystemPrompt = settings.PlanSystemPrompt,
                systemPromptRole = settings.SystemPromptRole,
                contextCompactionPrompt = settings.ContextCompactionPrompt,
                chatTitlePrompt = settings.ChatTitlePrompt,
                attachmentAnalysisPrompt = settings.AttachmentAnalysisPrompt
            };
        }

        private static string NormalizePromptRole(string value)
        {
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return "system";
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return "user";
            return "developer";
        }

        private static bool PromptTooLarge(string value)
        {
            return (value ?? string.Empty).Length > 100000;
        }

    }
}
