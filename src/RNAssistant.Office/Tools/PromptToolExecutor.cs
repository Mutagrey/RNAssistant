using System;
using System.Collections.Generic;
using System.Linq;
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

            yield return ControllerTool("common.prompts_read", "Read-only: Read RNAssistant editable agent prompt templates from Settings.", "{}", false);
            yield return ControllerTool("common.prompts_read_defaults", "Read-only: Read current RNAssistant prompts and built-in default prompt templates.", "{}", false);
            yield return ControllerTool(
                "common.prompts_save",
                "Mutates settings: Update RNAssistant agent prompt templates after the user asks to edit or improve RNAssistant prompts.",
                "{\"systemPrompt\":\"\",\"agentPrompt\":\"\",\"toolProtocolPrompt\":\"\",\"toolRoutingPrompt\":\"\",\"forceToolUsePrompt\":\"\",\"repairMalformedToolBlockPrompt\":\"\",\"afterToolResultsPrompt\":\"\",\"verifyMutationPrompt\":\"\",\"confirmedToolContinuationPrompt\":\"\",\"retryFailedToolPrompt\":\"\"}",
                true);
        }

        public bool IsControllerTool(string toolId)
        {
            return GetControllerTools().Any(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
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
                return ToolResult.Ok("Agent prompt templates read.", JsonConvert.SerializeObject(ToPayload(current)));
            }

            if (string.Equals(command.ToolId, "common.prompts_read_defaults", StringComparison.OrdinalIgnoreCase))
            {
                var current = _loadSettings();
                return ToolResult.Ok("Agent prompt templates and defaults read.", JsonConvert.SerializeObject(new
                {
                    current = ToPayload(current),
                    defaults = ToPayload(new AppSettings())
                }));
            }

            if (string.Equals(command.ToolId, "common.prompts_save", StringComparison.OrdinalIgnoreCase))
            {
                return SavePrompts(command, dryRun);
            }

            return ToolResult.Fail("Unknown prompt controller tool: " + command.ToolId);
        }

        private ToolResult SavePrompts(ToolCommand command, bool dryRun)
        {
            if (_saveSettings == null)
            {
                return ToolResult.Fail("Prompt settings store is read-only.");
            }

            var settings = _loadSettings();
            settings.AgentPrompts = settings.AgentPrompts ?? new AgentPromptSettings();

            ApplyIfPresent(command, "systemPrompt", value => settings.SystemPrompt = value);
            ApplyIfPresent(command, "agentPrompt", value => settings.AgentPrompt = value);
            ApplyIfPresent(command, "toolProtocolPrompt", value => settings.AgentPrompts.ToolProtocolPrompt = value);
            ApplyIfPresent(command, "toolRoutingPrompt", value => settings.AgentPrompts.ToolRoutingPrompt = value);
            ApplyIfPresent(command, "forceToolUsePrompt", value => settings.AgentPrompts.ForceToolUsePrompt = value);
            ApplyIfPresent(command, "repairMalformedToolBlockPrompt", value => settings.AgentPrompts.RepairMalformedToolBlockPrompt = value);
            ApplyIfPresent(command, "afterToolResultsPrompt", value => settings.AgentPrompts.AfterToolResultsPrompt = value);
            ApplyIfPresent(command, "verifyMutationPrompt", value => settings.AgentPrompts.VerifyMutationPrompt = value);
            ApplyIfPresent(command, "confirmedToolContinuationPrompt", value => settings.AgentPrompts.ConfirmedToolContinuationPrompt = value);
            ApplyIfPresent(command, "retryFailedToolPrompt", value => settings.AgentPrompts.RetryFailedToolPrompt = value);

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would save agent prompt templates.", JsonConvert.SerializeObject(ToPayload(settings)));
            }

            _saveSettings(settings);
            var saved = _loadSettings();
            return ToolResult.Ok("Agent prompt templates saved.", JsonConvert.SerializeObject(ToPayload(saved)));
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
                agentPrompt = settings.AgentPrompt,
                agentPrompts = settings.AgentPrompts ?? new AgentPromptSettings()
            };
        }

        private static ToolDefinition ControllerTool(string id, string description, string schema, bool requiresConfirmation)
        {
            return new ToolDefinition
            {
                Id = id,
                Host = "Common",
                Name = id.Substring(id.IndexOf('.') + 1),
                Description = description,
                ArgumentSchemaJson = schema,
                BuiltIn = true,
                Enabled = true,
                RequiresConfirmation = requiresConfirmation,
                MutatesDocument = false,
                AgentCanRun = true
            };
        }
    }
}
