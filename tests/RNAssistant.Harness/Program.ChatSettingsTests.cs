using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatSettingsUseSessionModelWithoutMutatingGlobalSettings()
        {
            var settings = new AppSettings { Model = "global-model" };
            settings.CustomHeaders["X-Test"] = "before";
            var session = new ChatSession { Model = "  chat-model  " };

            var effective = ChatSettingsResolver.Resolve(settings, session);

            AssertEqual("chat-model", effective.Model, "effective chat model");
            AssertEqual("global-model", settings.Model, "global model");
            effective.CustomHeaders["X-Test"] = "after";
            AssertEqual("before", settings.CustomHeaders["X-Test"], "settings clone");

            session.Model = " ";
            effective = ChatSettingsResolver.Resolve(settings, session);
            AssertEqual("global-model", effective.Model, "blank chat model fallback");
        }

        private static void PromptSavePreservesGlobalModel()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var global = new AppSettings { Model = "global-model", SystemPrompt = "Old prompt" };
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => global,
                    value => global = value);
                var runtime = global.Clone();
                runtime.Model = "per-chat-model";
                var empty = executor.Execute(
                    new ToolCommand { ToolId = "common.prompts_save" },
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    runtime,
                    false,
                    false);
                AssertTrue(!empty.Success, "empty prompt save fails before confirmation");
                AssertEqual("prompt_update_empty", empty.ErrorCode, "empty prompt save error");

                var command = new ToolCommand { ToolId = "common.prompts_save" };
                command.Arguments["systemPrompt"] = "New prompt";
                command.Arguments["agentToolsPrompt"] = "New tool prompt";
                command.Arguments["agentSkillsPrompt"] = "New skill prompt";
                command.Arguments["attachmentAnalysisPrompt"] = "New attachment prompt";

                var result = executor.Execute(
                    command,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    runtime,
                    false,
                    true);

                AssertTrue(result.Success, "prompt save succeeds");
                AssertEqual("New prompt", global.SystemPrompt, "global prompt updated");
                AssertEqual("New tool prompt", global.AgentToolsPrompt, "tool prompt updated");
                AssertEqual("New skill prompt", global.AgentSkillsPrompt, "skill prompt updated");
                AssertEqual("New attachment prompt", global.AttachmentAnalysisPrompt, "attachment prompt updated");
                AssertEqual("global-model", global.Model, "per-chat model is not copied into global settings");
            });
        }

        private static void SettingsHardCutoverLegacyAgentPrompts()
        {
            var legacy = JsonConvert.DeserializeObject<AppSettings>(
                "{\"SystemPrompt\":\"legacy custom combined\"," +
                "\"AgentToolsPrompt\":\"legacy custom tools\"," +
                "\"AgentSkillsPrompt\":\"legacy custom skills\"," +
                "\"ChatSystemPrompt\":\"legacy plain chat\"}");
            AssertEqual(0, legacy.AgentPromptSchemaVersion, "missing schema marker identifies legacy settings");

            legacy.NormalizeAgentPrompts();
            AssertEqual(AppSettings.CurrentAgentPromptSchemaVersion, legacy.AgentPromptSchemaVersion,
                "legacy settings are marked with the current Agent prompt schema");
            AssertEqual(AgentPromptDefaults.GeneralInstructions, legacy.SystemPrompt,
                "legacy combined prompt is discarded during hard cutover");
            AssertEqual(AgentPromptDefaults.ToolInstructions, legacy.AgentToolsPrompt,
                "legacy tool prompt is replaced with the current default");
            AssertEqual(AgentPromptDefaults.SkillInstructions, legacy.AgentSkillsPrompt,
                "legacy skill prompt is replaced with the current default");
            AssertEqual(AgentPromptDefaults.ChatInstructions, legacy.ChatSystemPrompt,
                "legacy no-tools Chat prompt is replaced with the structured resource default");

            legacy.SystemPrompt = "current custom general";
            legacy.AgentToolsPrompt = "current custom tools";
            legacy.AgentSkillsPrompt = "current custom skills";
            legacy.ChatSystemPrompt = "current custom chat";
            var serialized = JsonConvert.SerializeObject(legacy);
            var current = JsonConvert.DeserializeObject<AppSettings>(serialized);
            current.NormalizeAgentPrompts();
            AssertEqual("current custom general", current.SystemPrompt,
                "current-version general prompt is preserved");
            AssertEqual("current custom tools", current.AgentToolsPrompt,
                "current-version tool prompt is preserved");
            AssertEqual("current custom skills", current.AgentSkillsPrompt,
                "current-version skill prompt is preserved");
            AssertEqual("current custom chat", current.ChatSystemPrompt,
                "current-version Chat prompt is preserved");
            AssertContains(serialized, "AgentPromptSchemaVersion",
                "saved settings persist the Agent prompt schema marker");

            current.AgentPromptSchemaVersion = AppSettings.CurrentAgentPromptSchemaVersion + 1;
            current.NormalizeAgentPrompts();
            AssertEqual(AgentPromptDefaults.GeneralInstructions, current.SystemPrompt,
                "unknown Agent prompt schema is not treated as current");
            AssertEqual(AgentPromptDefaults.ChatInstructions, current.ChatSystemPrompt,
                "unknown schema resets the Chat protocol too");
        }

        private static void SettingsNormalizeInvalidNumericValues()
        {
            var settings = new AppSettings
            {
                Temperature = double.NaN,
                TopP = double.PositiveInfinity,
                UiFontScale = double.NegativeInfinity
            };
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(0.2, settings.Temperature, "non-finite temperature uses the default");
            AssertEqual(1.0, settings.TopP, "non-finite top-p uses the default");
            AssertEqual(1.0, settings.UiFontScale, "non-finite UI scale uses the default");

            settings.Temperature = 10;
            settings.UiFontScale = 10;
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(2.0, settings.Temperature, "temperature is clamped to the supported endpoint range");
            AssertEqual(1.30, settings.UiFontScale, "UI scale is clamped to the rendered range");
        }
    }
}
