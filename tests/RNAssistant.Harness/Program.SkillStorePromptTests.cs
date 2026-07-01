using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SkillStoreSavesMarkdownSkills()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new SkillStore(paths);
                store.Save(new[]
                {
                    new SkillDefinition
                    {
                        Id = "common.review_note",
                        Host = "Common",
                        Name = "Review note",
                        Description = "Review short notes.",
                        Tags = new List<string> { "review", "writing" },
                        BodyMarkdown = "# Review note\n\nUse this skill for concise review.",
                        Enabled = true
                    }
                });

                var loaded = store.Load();

                AssertEqual(1, loaded.Count, "loaded skill count");
                AssertEqual("common.review_note", loaded[0].Id, "skill id");
                AssertContains(loaded[0].BodyMarkdown, "# Review note", "skill markdown");
                AssertTrue(File.Exists(Path.Combine(loaded[0].StoragePath, "SKILL.md")), "skill md file");
            });
        }
        private static void SkillStoreSkipsBrokenMarkdownSkills()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var validDirectory = Path.Combine(paths.SkillsDirectory, "common", "valid");
                var brokenDirectory = Path.Combine(paths.SkillsDirectory, "common", "broken");
                Directory.CreateDirectory(validDirectory);
                Directory.CreateDirectory(brokenDirectory);
                File.WriteAllText(
                    Path.Combine(validDirectory, "SKILL.md"),
                    "---\n" +
                    "id: common.valid\n" +
                    "host: Common\n" +
                    "name: Valid\n" +
                    "enabled: true\n" +
                    "---\n" +
                    "\n" +
                    "# Valid skill");
                File.WriteAllText(Path.Combine(brokenDirectory, "SKILL.md"), "# Missing id");

                var loaded = new SkillStore(paths).Load();

                AssertEqual(1, loaded.Count, "loaded skill count");
                AssertEqual("common.valid", loaded[0].Id, "loaded skill id");
            });
        }

        private static void SkillCatalogSelectsRelevantSkills()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new SkillStore(paths);
                store.Save(new[]
                {
                    new SkillDefinition
                    {
                        Id = "word.hidden_review",
                        Host = "Word",
                        Name = "Hidden review",
                        Description = "Word-only review.",
                        Tags = new List<string> { "review" },
                        BodyMarkdown = "# Hidden",
                        Enabled = true
                    }
                });
                var catalog = new SkillCatalogService(adapter, store);

                var visible = catalog.GetVisibleSkills();
                var selected = catalog.SelectRelevantSkills("Create an Excel chart report.", NewContext(adapter), 5);

                AssertTrue(HasSkill(visible, "common.task_planning"), "common built-in visible");
                AssertTrue(HasSkill(visible, "excel.analysis_reporting"), "excel built-in visible");
                AssertTrue(!HasSkill(visible, "word.hidden_review"), "other host custom skill hidden");
                AssertTrue(HasSkill(selected, "excel.analysis_reporting"), "excel analysis selected");
            });
        }

        private static void PromptSeparatesSkillsFromTools()
        {
            var prompt = FlattenMessages(BuildPlannerMessages(
                new AppSettings(),
                new[]
                {
                    new ToolDefinition
                    {
                        Id = "excel.add_sheet",
                        Host = "Excel",
                        Description = "Add a worksheet.",
                        ArgumentSchemaJson = "{\"name\":\"Report\"}",
                        BuiltIn = true,
                        Enabled = true
                    }
                },
                new[]
                {
                    new SkillDefinition
                    {
                        Id = "common.test_skill",
                        Host = "Common",
                        Description = "Test guidance.",
                        BodyMarkdown = "# Test skill\n\nUse guidance only.",
                        Enabled = true
                    }
                }));

            AssertContains(prompt, "RELEVANT_SKILLS", "skills section");
            AssertContains(prompt, "AVAILABLE_TOOLS", "tools section");
            AssertContains(prompt, "\"toolId\":\"exact tool id from AVAILABLE_TOOLS\"", "tool id protocol");
            AssertContains(prompt, "Skills are guidance documents only", "skill guidance boundary");
        }

        private static void PromptLimitsSkillBodies()
        {
            var longBody = "# Long skill\n" + new string('a', 5000) + "TAIL_MARKER";
            var prompt = FlattenMessages(BuildPlannerMessages(
                new AppSettings { ContextWindowOverrideTokens = 4096 },
                new ToolDefinition[0],
                new[]
                {
                    new SkillDefinition
                    {
                        Id = "common.long_skill",
                        Host = "Common",
                        Description = "Long guidance.",
                        BodyMarkdown = longBody,
                        Enabled = true
                    }
                }));

            AssertContains(prompt, "common.long_skill", "skill id");
            AssertContains(prompt, "[truncated]", "skill body truncated");
            AssertTrue(prompt.IndexOf("TAIL_MARKER", StringComparison.OrdinalIgnoreCase) < 0, "skill tail omitted");
        }

        private static void PromptUsesEditableAgentPromptBlocks()
        {
            var settings = new AppSettings();
            settings.AgentPrompts.ToolProtocolPrompt = "CUSTOM_TOOL_PROTOCOL";
            settings.AgentPrompts.ToolRoutingPrompt = "CUSTOM_TOOL_ROUTING";

            var messages = BuildPlannerMessages(settings, new ToolDefinition[0], new SkillDefinition[0]);
            var prompt = FlattenMessages(messages);

            AssertContains(prompt, "CUSTOM_TOOL_PROTOCOL", "custom tool protocol prompt");
            AssertContains(prompt, "CUSTOM_TOOL_ROUTING", "custom tool routing prompt");
            AssertTrue(prompt.IndexOf("Required tool response format", StringComparison.OrdinalIgnoreCase) < 0, "default protocol replaced");
            AssertEqual("user", messages[0].Role, "default instruction role");

            settings.SystemPromptRole = "system";
            AssertEqual("system", BuildPlannerMessages(settings, new ToolDefinition[0], new SkillDefinition[0])[0].Role, "system instruction role");
        }

        private static void PromptSettingsApplyOnNextRequest()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new JsonFileStore();
                var settings = new AppSettings
                {
                    SystemPrompt = "BASE_V1",
                    SystemPromptRole = "user"
                };
                settings.AgentPrompts.ToolProtocolPrompt = "PROTOCOL_V1";
                settings.AgentPrompts.ToolRoutingPrompt = "ROUTING_V1";
                store.Save(paths.SettingsFile, settings);

                var first = BuildPlannerMessages(store.Load(paths.SettingsFile, new AppSettings()), new ToolDefinition[0], new SkillDefinition[0]);
                AssertEqual("user", first[0].Role, "first prompt role");
                AssertContains(first[0].Content, "BASE_V1", "first base prompt");
                AssertContains(first[0].Content, "PROTOCOL_V1", "first protocol prompt");
                AssertContains(first[0].Content, "ROUTING_V1", "first routing prompt");

                settings.SystemPrompt = "BASE_V2";
                settings.SystemPromptRole = "system";
                settings.AgentPrompts.ToolProtocolPrompt = "PROTOCOL_V2";
                settings.AgentPrompts.ToolRoutingPrompt = "ROUTING_V2";
                store.Save(paths.SettingsFile, settings);

                var second = BuildPlannerMessages(store.Load(paths.SettingsFile, new AppSettings()), new ToolDefinition[0], new SkillDefinition[0]);
                AssertEqual("system", second[0].Role, "updated prompt role");
                AssertContains(second[0].Content, "BASE_V2", "updated base prompt");
                AssertContains(second[0].Content, "PROTOCOL_V2", "updated protocol prompt");
                AssertContains(second[0].Content, "ROUTING_V2", "updated routing prompt");
                AssertTrue(second[0].Content.IndexOf("BASE_V1", StringComparison.Ordinal) < 0, "old prompt removed");
            });
        }

        private static void AgentCanSaveSkillsWithConfirmation()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var command = new ToolCommand { ToolId = "common.skills_save" };
                command.Arguments["id"] = "common.generated_skill";
                command.Arguments["host"] = "Common";
                command.Arguments["name"] = "Generated skill";
                command.Arguments["description"] = "Generated by agent.";
                command.Arguments["tags"] = "generated, test";
                command.Arguments["bodyMarkdown"] = "# Generated skill\n\nUse this skill in tests.";

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = false }, false, false);
                var saved = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                var read = executor.Execute(new ToolCommand { ToolId = "common.skills_read", Arguments = { ["id"] = "common.generated_skill" } }, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings(), false, false);

                AssertTrue(!blocked.Success, "skill save waits for confirmation");
                AssertContains(blocked.Status, "waiting_confirmation", "blocked status");
                AssertTrue(saved.Success, "skill save succeeds after confirmation");
                AssertTrue(read.Success, "saved skill readable");
                AssertContains(read.DataJson, "Generated skill", "saved skill data");
            });
        }
    }
}
