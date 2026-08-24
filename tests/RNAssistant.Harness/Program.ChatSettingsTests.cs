using System;
using System.Linq;
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

                var result = executor.Execute(
                    command,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    runtime,
                    false,
                    true);

                AssertTrue(result.Success, "prompt save succeeds");
                AssertEqual("New prompt", global.SystemPrompt, "global prompt updated");
                AssertEqual("global-model", global.Model, "per-chat model is not copied into global settings");
            });
        }

        private static void SettingsMigrateLegacySkillLoadingPolicy()
        {
            var settings = new AppSettings();
            var legacy = settings.SystemPrompt.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.LegacyInstructions);
            AssertContains(legacy, AgentSkillPromptPolicy.LegacyInstructions,
                "legacy policy fixture is present");

            var upgraded = AgentSkillPromptPolicy.Upgrade(legacy);
            AssertContains(upgraded, AgentSkillPromptPolicy.CurrentInstructions,
                "legacy default policy is upgraded");
            AssertTrue(upgraded.IndexOf(AgentSkillPromptPolicy.LegacyInstructions, StringComparison.Ordinal) < 0,
                "legacy policy is removed after upgrade");

            var revisionPolicy = settings.SystemPrompt.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.RevisionInstructions);
            AssertContains(AgentSkillPromptPolicy.Upgrade(revisionPolicy), AgentSkillPromptPolicy.CurrentInstructions,
                "revision-only policy is upgraded to explicit loaded evidence");

            const string custom = "Custom prompt without the default skill policy.";
            AssertEqual(custom, AgentSkillPromptPolicy.Upgrade(custom),
                "custom prompt without legacy policy is preserved");
        }
    }
}
