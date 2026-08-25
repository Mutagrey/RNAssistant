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

        private static void SettingsMigrateLegacySkillLoadingPolicy()
        {
            var settings = new AppSettings();
            var legacy = settings.AgentSkillsPrompt.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.LegacyInstructions);
            AssertContains(legacy, AgentSkillPromptPolicy.LegacyInstructions,
                "legacy policy fixture is present");

            var upgraded = AgentSkillPromptPolicy.Upgrade(legacy);
            AssertContains(upgraded, AgentSkillPromptPolicy.CurrentInstructions,
                "legacy default policy is upgraded");
            AssertTrue(upgraded.IndexOf(AgentSkillPromptPolicy.LegacyInstructions, StringComparison.Ordinal) < 0,
                "legacy policy is removed after upgrade");

            var revisionPolicy = settings.AgentSkillsPrompt.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.RevisionInstructions);
            AssertContains(AgentSkillPromptPolicy.Upgrade(revisionPolicy), AgentSkillPromptPolicy.CurrentInstructions,
                "revision-only policy is upgraded to explicit loaded evidence");

            const string custom = "Custom prompt without the default skill policy.";
            AssertEqual(custom, AgentSkillPromptPolicy.Upgrade(custom),
                "custom prompt without legacy policy is preserved");

            var evidencePolicy = settings.AgentSkillsPrompt.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.LoadedEvidenceInstructions);
            AssertContains(AgentSkillPromptPolicy.Upgrade(evidencePolicy), AgentSkillPromptPolicy.CurrentInstructions,
                "previous loaded-evidence policy is upgraded to metadata-only wording");

            var oldCombined = AgentPromptDefaults.LegacyCombinedInstructions.Replace(
                AgentSkillPromptPolicy.CurrentInstructions,
                AgentSkillPromptPolicy.LoadedEvidenceInstructions);
            AssertContains(AgentPromptDefaults.LegacyCombinedInstructions, "chat.html_workspace_preferred=true",
                "legacy combined fixture retains the previous runtime wording");
            AssertEqual(AgentPromptDefaults.GeneralInstructions,
                AgentPromptDefaults.UpgradeGeneralInstructions(oldCombined),
                "known legacy combined default migrates to the general Agent prompt");

            var customCombined = oldCombined + "\n\n## Custom\n\nKeep this user rule.";
            var customUpgraded = AgentPromptDefaults.UpgradeGeneralInstructions(customCombined);
            AssertTrue(customUpgraded.IndexOf("html_workspace_preferred", StringComparison.Ordinal) < 0,
                "custom legacy prompt drops the removed HTML preference reference");
            AssertTrue(customUpgraded.IndexOf("## Tools", StringComparison.Ordinal) < 0 &&
                customUpgraded.IndexOf("## Skills", StringComparison.Ordinal) < 0,
                "custom legacy prompt does not duplicate split tool and skill policies");
            AssertContains(customUpgraded, "Keep this user rule.",
                "custom legacy prompt content is preserved");
        }
    }
}
