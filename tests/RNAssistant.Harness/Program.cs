using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class HarnessTest
        {
            public string Name { get; set; }
            public Action Run { get; set; }
        }

        private sealed class HostTaskScenario
        {
            public string Host { get; set; }
            public string UserText { get; set; }
            public string Response { get; set; }
            public string[] ExpectedTools { get; set; }
        }

        public static int Main(string[] args)
        {
            var tests = new List<HarnessTest>
            {
                new HarnessTest { Name = "parser: fenced agent steps", Run = ParsesFencedAgentSteps },
                new HarnessTest { Name = "parser: bare json array", Run = ParsesBareJsonArray },
                new HarnessTest { Name = "parser: native tool_calls", Run = ParsesNativeToolCalls },
                new HarnessTest { Name = "parser: noisy embedded json", Run = ParsesNoisyEmbeddedJson },
                new HarnessTest { Name = "parser: bad json skipped", Run = SkipsBadJson },
                new HarnessTest { Name = "storage: chat roundtrip", Run = CreatesAndListsChatsInTempRoot },
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles },
                new HarnessTest { Name = "pipeline: dry-run resolves placeholders", Run = PipelineDryRunResolvesPlaceholders },
                new HarnessTest { Name = "pipeline: executes fake adapter steps", Run = PipelineExecutesFakeAdapterSteps },
                new HarnessTest { Name = "pipeline: resolves step output placeholders", Run = PipelineResolvesStepOutputPlaceholders },
                new HarnessTest { Name = "pipeline: stops after failed step", Run = PipelineStopsAfterFailedStep },
                new HarnessTest { Name = "pipeline: rejects invalid definitions", Run = PipelineRejectsInvalidDefinitions },
                new HarnessTest { Name = "pipeline: enforces nesting limit", Run = PipelineEnforcesNestingLimit },
                new HarnessTest { Name = "pipeline: custom tool needs confirmation", Run = CustomPipelineNeedsConfirmation },
                new HarnessTest { Name = "pipeline: agent mode gates built-in mutation", Run = AgentModeGatesBuiltInMutation },
                new HarnessTest { Name = "tools: catalog merges visible tools", Run = ToolCatalogMergesVisibleTools },
                new HarnessTest { Name = "tools: store saves and updates custom tools", Run = ToolStoreSavesAndUpdatesCustomTools },
                new HarnessTest { Name = "tools: unknown and disabled tools fail", Run = UnknownAndDisabledToolsFail },
                new HarnessTest { Name = "tools: safety metadata gates mutations", Run = ToolSafetyMetadataGatesMutations },
                new HarnessTest { Name = "tools: confirmation matrix covers dry and manual runs", Run = ConfirmationMatrixCoversDryAndManualRuns },
                new HarnessTest { Name = "vba: replace text backs up module", Run = VbaReplaceTextBacksUpModule },
                new HarnessTest { Name = "prompt: trims oldest history", Run = PromptBuilderTrimsOldestHistory },
                new HarnessTest { Name = "prompt: usage estimator counts context", Run = ContextUsageEstimatorCountsPromptAndSession },
                new HarnessTest { Name = "chat: completion service records prose", Run = ChatCompletionServiceRecordsProseResponse },
                new HarnessTest { Name = "chat: executes typical host tasks", Run = ChatExecutesTypicalHostTasks },
                new HarnessTest { Name = "chat: agent activity transcript", Run = AgentTranscriptCreatesActivityTree },
                new HarnessTest { Name = "chat: prose action forces tool follow-up", Run = ChatProseActionForcesToolFollowUp },
                new HarnessTest { Name = "chat: failed tool retries corrected call", Run = ChatFailedToolRetriesCorrectedCall },
                new HarnessTest { Name = "chat: auto-run disabled records failure", Run = ChatAutoRunDisabledRecordsLocalFailure },
                new HarnessTest { Name = "chat: malformed tool response stays prose", Run = ChatMalformedToolResponseStaysProse },
                new HarnessTest { Name = "chat: explicit clone preserves values", Run = ChatCloneServicePreservesValues },
                new HarnessTest { Name = "context: normalize and upsert", Run = ContextServiceNormalizesAndUpserts },
                new HarnessTest { Name = "context: trim helper", Run = ContextServiceTrimsText },
                new HarnessTest { Name = "bridge: typed runTool payload", Run = BridgeUsesTypedRunToolPayload },
                new HarnessTest { Name = "bridge: typed sendChat progress", Run = BridgeUsesTypedSendChatPayloadAndProgress },
                new HarnessTest { Name = "bridge: typed settings payload", Run = BridgeUsesTypedSettingsPayload },
                new HarnessTest { Name = "bridge: typed context payload", Run = BridgeUsesTypedContextPayload },
                new HarnessTest { Name = "bridge: typed vba payload", Run = BridgeUsesTypedVbaPayload }
            };

            var failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Run();
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failed += 1;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine(failed == 0 ? "OK" : "FAILED " + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void ParsesFencedAgentSteps()
        {
            var commands = new SkillCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"steps\":[" +
                "{\"description\":\"Add sheet\",\"skillId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                "{\"toolId\":\"excel.add_chart\",\"args\":{\"title\":\"Sales\"}}" +
                "]}" +
                "\n```");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("excel.add_sheet", commands[0].SkillId, "first skill id");
            AssertEqual("Report", commands[0].Arguments["name"], "first arg");
            AssertEqual("excel.add_chart", commands[1].SkillId, "second skill id");
        }

        private static void ParsesNativeToolCalls()
        {
            var commands = new SkillCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\",\"function\":{\"name\":\"excel.write_table\",\"arguments\":\"{\\\"sheet\\\":\\\"Data\\\",\\\"startAddress\\\":\\\"A1\\\"}\"}}]}" +
                "\n```");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("excel.write_table", commands[0].SkillId, "skill id");
            AssertEqual("Data", commands[0].Arguments["sheet"], "sheet arg");
            AssertEqual("A1", commands[0].Arguments["startAddress"], "address arg");
        }

        private static void ParsesBareJsonArray()
        {
            var commands = new SkillCommandParser().Parse(
                "[" +
                "{\"tool\":\"word.insert_text\",\"parameters\":{\"text\":\"Hello\"}}," +
                "{\"action\":\"excel.autofit\",\"input\":{\"sheet\":\"Data\"}}" +
                "]");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("word.insert_text", commands[0].SkillId, "first skill id");
            AssertEqual("Hello", commands[0].Arguments["text"], "text arg");
            AssertEqual("excel.autofit", commands[1].SkillId, "second skill id");
        }

        private static void ParsesNoisyEmbeddedJson()
        {
            var commands = new SkillCommandParser().Parse(
                "I will handle it. First, here is the plan: " +
                "{\"steps\":[{\"toolId\":\"powerpoint.add_slide\",\"arguments\":{\"title\":\"Q1\",\"body\":\"Revenue grew\"}}]} " +
                "Then I will summarize.");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("powerpoint.add_slide", commands[0].SkillId, "skill id");
            AssertEqual("Q1", commands[0].Arguments["title"], "title arg");
        }

        private static void SkipsBadJson()
        {
            var commands = new SkillCommandParser().Parse("```rnassistant-agent\n{\"steps\":[\n```");
            AssertEqual(0, commands.Count, "command count");
        }

        private static void CreatesAndListsChatsInTempRoot()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var session = store.Create("Word", "doc-key", "Doc", "First");
                session.Messages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "hello",
                    Activity = new ChatActivity
                    {
                        Kind = "notice",
                        Title = "Stored activity",
                        Status = "completed"
                    }
                });
                store.Save(session);

                var loaded = store.Load("Word", "doc-key", ChatStore.GetSessionId(session));
                AssertTrue(loaded != null, "loaded session");
                AssertEqual("First", loaded.Title, "title");
                AssertEqual(1, loaded.Messages.Count, "message count");
                AssertEqual("hello", loaded.Messages[0].Content, "message content");
                AssertEqual("Stored activity", loaded.Messages[0].Activity.Title, "message activity title");

                var sessions = store.List("Word", "doc-key", "Doc");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(ChatStore.GetSessionId(session), ChatStore.GetSessionId(sessions[0]), "session id");
                AssertEqual(ChatStore.GetSessionId(session), store.LoadActiveSessionId("Word", "doc-key"), "active id");
            });
        }

        private static void SkipsBrokenChatFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ChatStore(paths);
                var documentDirectory = Path.Combine(paths.ChatDirectory, AppDataPaths.SafeFileName("Excel|book"));
                Directory.CreateDirectory(documentDirectory);
                File.WriteAllText(Path.Combine(documentDirectory, "broken.json"), "{ broken");

                var session = store.Create("Excel", "book", "Book", "Good");
                var sessions = store.List("Excel", "book", "Book");
                AssertEqual(1, sessions.Count, "document session count");
                AssertEqual(ChatStore.GetSessionId(session), ChatStore.GetSessionId(sessions[0]), "session id");

                var allSessions = store.List();
                AssertEqual(1, allSessions.Count, "global session count");
            });
        }

        private static void PipelineDryRunResolvesPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new SkillCommand { SkillId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings(), true, false);

                AssertTrue(result.Success, "pipeline dry-run result");
                AssertContains(result.Message, "Dry run completed", "dry-run message");
                AssertContains(result.DataJson, "Report", "pipeline data");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void PipelineExecutesFakeAdapterSteps()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new SkillCommand { SkillId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "pipeline result");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].SkillId, "first tool");
                AssertEqual("Report", adapter.Executed[0].Arguments["name"], "first arg");
                AssertEqual("excel.write_table", adapter.Executed[1].SkillId, "second tool");
                AssertEqual("Report", adapter.Executed[1].Arguments["sheet"], "second arg");
            });
        }

        private static void PipelineResolvesStepOutputPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildStepPlaceholderPipelineTools();
                var command = new SkillCommand { SkillId = "excel.chain_report" };

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "pipeline result");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("executed excel.add_sheet", adapter.Executed[1].Arguments["sourceMessage"], "step message placeholder");
                AssertEqual("true", adapter.Executed[1].Arguments["sourceSuccess"], "step success placeholder");
            });
        }

        private static void PipelineStopsAfterFailedStep()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", SkillResult.Fail("No table values provided."));
                var tools = BuildThreeStepPipelineTools();
                var command = new SkillCommand { SkillId = "excel.full_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "Pipeline step failed: table", "failure message");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].SkillId, "first tool");
                AssertEqual("excel.write_table", adapter.Executed[1].SkillId, "failed tool");
            });
        }

        private static void CustomPipelineNeedsConfirmation()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(true);
                var command = new SkillCommand { SkillId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "requires confirmation", "confirmation message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void AgentModeGatesBuiltInMutation()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new SkillCommand { SkillId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = false }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "requires confirmation", "confirmation message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void ToolCatalogMergesVisibleTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var toolStore = new ToolStore(paths);
                toolStore.Save(new[]
                {
                    CustomTool("Common", "common.inspect"),
                    CustomTool("Excel", "excel.custom"),
                    CustomTool("Word", "word.hidden")
                });
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths));
                var catalog = new ToolCatalogService(adapter, executor, toolStore).GetVisibleTools();

                AssertTrue(HasTool(catalog, "excel.add_sheet"), "built-in tool visible");
                AssertTrue(HasTool(catalog, "excel.vba_apply_patch"), "controller VBA tool visible");
                AssertTrue(HasTool(catalog, "common.inspect"), "common custom tool visible");
                AssertTrue(HasTool(catalog, "excel.custom"), "host custom tool visible");
                AssertTrue(!HasTool(catalog, "word.hidden"), "other host custom tool hidden");
            });
        }

        private static void ToolStoreSavesAndUpdatesCustomTools()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var initial = CustomTool("Excel", "excel.custom_report");
                initial.Name = "Initial report";
                initial.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}]}";
                initial.Readme = "Creates a report sheet.";
                var otherHost = CustomTool("Word", "word.review");
                store.Save(new[] { initial, otherHost });

                var loadedInitial = FindTool(store.Load(), "excel.custom_report");
                AssertTrue(loadedInitial != null, "initial custom tool loaded");
                AssertEqual("Initial report", loadedInitial.Name, "initial name");
                AssertContains(loadedInitial.PipelineJson, "excel.add_sheet", "initial pipeline");
                AssertContains(loadedInitial.Readme, "report sheet", "initial readme");
                AssertTrue(!string.IsNullOrWhiteSpace(loadedInitial.StoragePath), "storage path set");

                var edited = CustomTool("Excel", "excel.custom_report");
                edited.Name = "Updated report";
                edited.RequiresConfirmation = true;
                edited.MutatesDocument = true;
                edited.PipelineJson = "{\"steps\":[{\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Report\",\"startAddress\":\"A1\",\"values\":\"[[\\\"A\\\"]]\"}}]}";
                store.Save(new[] { edited }, "Excel");

                var loaded = store.Load();
                var updated = FindTool(loaded, "excel.custom_report");
                AssertTrue(updated != null, "updated custom tool loaded");
                AssertEqual("Updated report", updated.Name, "updated name");
                AssertTrue(updated.RequiresConfirmation, "updated confirmation flag");
                AssertContains(updated.PipelineJson, "excel.write_table", "updated pipeline");
                AssertTrue(HasTool(loaded, "word.review"), "other host preserved");
            });
        }

        private static void ToolSafetyMetadataGatesMutations()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<SkillDefinition>(adapter.GetBuiltInSkills());
                tools.Add(new SkillDefinition
                {
                    Id = "excel.metadata_mutation",
                    Host = "Excel",
                    Name = "metadata mutation",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true
                });
                var command = new SkillCommand { SkillId = "excel.metadata_mutation" };

                var blocked = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "metadata mutation blocked");
                AssertContains(blocked.Message, "requires confirmation", "metadata block message");
                AssertEqual(0, adapter.Executed.Count, "blocked adapter execution count");

                var allowed = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false }, false, false);
                AssertTrue(allowed.Success, "metadata mutation allowed in agent mode");
                AssertEqual(1, adapter.Executed.Count, "allowed adapter execution count");
            });
        }

        private static void VbaReplaceTextBacksUpModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore);
                var command = new SkillCommand { SkillId = executor.VbaToolId("vba_replace_text") };
                command.Arguments["moduleName"] = "Module1";
                command.Arguments["find"] = "\"old\"";
                command.Arguments["replace"] = "\"new\"";

                var blocked = executor.Execute(command, new List<SkillDefinition>(adapter.GetBuiltInSkills()), new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "vba replace blocked");
                AssertEqual(0, adapter.Executed.Count, "blocked vba adapter execution count");

                var result = executor.Execute(command, new List<SkillDefinition>(adapter.GetBuiltInSkills()), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "replace result");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "updated module");
                AssertTrue(adapter.VbaModuleCode.IndexOf("\"old\"", StringComparison.Ordinal) < 0, "old text removed");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module1", backups[0].ModuleName, "backup module");
                AssertContains(backups[0].Code, "\"old\"", "backup code");
            });
        }

        private static void PromptBuilderTrimsOldestHistory()
        {
            var messages = PromptMessageBuilder.Build(
                "system",
                "context",
                new[]
                {
                    new ChatMessage { Role = "user", Content = "old-" + new string('o', 3000) },
                    new ChatMessage { Role = "assistant", Content = "middle-" + new string('m', 1500) },
                    new ChatMessage { Role = "user", Content = "newest-" + new string('n', 1000) }
                },
                4000);

            AssertEqual(4, messages.Count, "prompt message count");
            AssertEqual("system", messages[0].Role, "system role");
            AssertEqual("context", messages[1].Content, "context content");
            AssertContains(messages[2].Content, "middle-", "middle message retained");
            AssertContains(messages[3].Content, "newest-", "newest message retained");
        }

        private static void ContextUsageEstimatorCountsPromptAndSession()
        {
            var settings = new AppSettings { ContextCharLimit = 8000 };
            var promptUsage = JObject.FromObject(ContextUsageEstimator.FromPrompt(new[]
            {
                new ChatMessage { Role = "system", Content = "abc" },
                new ChatMessage { Role = "user", Content = "defg" }
            }, settings));
            AssertEqual(7, promptUsage["usedChars"].Value<int>(), "prompt used chars");
            AssertEqual(8000, promptUsage["limitChars"].Value<int>(), "prompt limit chars");
            AssertEqual(2, promptUsage["messageCount"].Value<int>(), "prompt message count");
            AssertTrue(promptUsage["actual"].Value<bool>(), "prompt actual");

            var session = new ChatSession();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
            session.Context.Notes.Add(new ContextNote { Text = "selection!" });
            var sessionUsage = JObject.FromObject(ContextUsageEstimator.FromSession(session, settings));
            AssertEqual(15, sessionUsage["usedChars"].Value<int>(), "session used chars");
            AssertEqual(1, sessionUsage["messageCount"].Value<int>(), "session message count");
            AssertTrue(!sessionUsage["actual"].Value<bool>(), "session actual");
        }

        private static void ChatCompletionServiceRecordsProseResponse()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var capturedMessages = new List<ChatMessage>();
                var service = new ChatCompletionService(
                    adapter,
                    executor,
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages)
                    {
                        capturedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                        return Task.FromResult(new LlmCompletionResult
                        {
                            Content = "Done.",
                            PromptTokens = 10,
                            CompletionTokens = 2,
                            TotalTokens = 12
                        });
                    });

                var session = new ChatSession
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    Title = "New chat"
                };
                var context = new DocumentContext
                {
                    Host = "Excel",
                    DocumentKey = "doc",
                    Title = "Harness.xlsx"
                };
                context.Notes.Add(new ContextNote
                {
                    Host = "Excel",
                    Kind = "selection",
                    Title = "Selection",
                    Reference = "A1",
                    Text = "Selected cells"
                });

                var result = service.ExecuteAsync(
                    "hello world",
                    session,
                    context,
                    new AppSettings { AgentModeEnabled = false, ContextCharLimit = 8000 },
                    new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual("hello world", session.Messages[0].Content, "user message");
                AssertEqual("Done.", session.Messages[1].Content, "assistant message");
                AssertEqual("hello world", session.Title, "session title");
                AssertTrue(ContainsMessage(capturedMessages, "User-added context attachments"), "context prompt captured");
            });
        }

        private static void ChatExecutesTypicalHostTasks()
        {
            var scenarios = new[]
            {
                new HostTaskScenario
                {
                    Host = "Excel",
                    UserText = "Create a sales report sheet and chart.",
                    Response = AgentBlock(
                        Command("excel.add_sheet", "name", "Report"),
                        Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"],[\"Jan\",10]]"),
                        Command("excel.add_chart", "sheet", "Report", "sourceRange", "A1:B2", "chartType", "column", "title", "Sales")),
                    ExpectedTools = new[] { "excel.add_sheet", "excel.write_table", "excel.add_chart" }
                },
                new HostTaskScenario
                {
                    Host = "Word",
                    UserText = "Insert an executive summary and add a review comment.",
                    Response = AgentBlock(
                        Command("word.insert_text", "text", "Executive summary"),
                        Command("word.add_comment", "text", "Review this paragraph.")),
                    ExpectedTools = new[] { "word.insert_text", "word.add_comment" }
                },
                new HostTaskScenario
                {
                    Host = "PowerPoint",
                    UserText = "Add a quarterly summary slide.",
                    Response = AgentBlock(
                        Command("powerpoint.add_slide", "title", "Q1 Summary", "body", "Revenue grew.")),
                    ExpectedTools = new[] { "powerpoint.add_slide" }
                },
                new HostTaskScenario
                {
                    Host = "Outlook",
                    UserText = "Read the selected email and draft a reply.",
                    Response = AgentBlock(
                        Command("outlook.read_selection", "maxChars", "12000"),
                        Command("outlook.draft_reply", "body", "Thanks, I will follow up.")),
                    ExpectedTools = new[] { "outlook.read_selection", "outlook.draft_reply" }
                }
            };

            for (var i = 0; i < scenarios.Length; i++)
            {
                var scenario = scenarios[i];
                WithTempExecutor(FakeOfficeAdapter.ForHost(scenario.Host), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var calls = new List<IReadOnlyList<ChatMessage>>();
                    var service = ChatServiceWithResponses(adapter, executor, calls, scenario.Response, "Done.");
                    var session = NewSession(adapter);
                    var context = NewContext(adapter);
                    context.Notes.Add(new ContextNote { Host = adapter.HostName, Kind = "selection", Title = "Pinned", Reference = "ref", Text = "Pinned context" });

                    var result = service.ExecuteAsync(
                        scenario.UserText,
                        session,
                        context,
                        new AppSettings { ContextCharLimit = 8000 },
                        new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                        null).GetAwaiter().GetResult();

                    AssertEqual("Done.", result.AssistantText, scenario.Host + " assistant text");
                    AssertEqual(scenario.ExpectedTools.Length, adapter.Executed.Count, scenario.Host + " executed count");
                    for (var toolIndex = 0; toolIndex < scenario.ExpectedTools.Length; toolIndex++)
                    {
                        AssertEqual(scenario.ExpectedTools[toolIndex], adapter.Executed[toolIndex].SkillId, scenario.Host + " tool " + toolIndex);
                    }
                    AssertTrue(ContainsMessage(calls[0], "User-added context attachments"), scenario.Host + " context prompt");
                    AssertTrue(ContainsMessage(session.Messages, "Agent plan"), scenario.Host + " plan recorded");
                    AssertTrue(ContainsMessage(session.Messages, "Agent step"), scenario.Host + " result recorded");
                });
            }
        }

        private static void ChatProseActionForcesToolFollowUp()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    "Sure, I can do that.",
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "prose-only answer is not acceptable"), "forced follow-up prompt");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].SkillId, "executed tool");
            });
        }

        private static void AgentTranscriptCreatesActivityTree()
        {
            var plan = AgentTranscript.CreateAgentPlanActivity(new[] { Command("excel.add_sheet", "name", "Report") });
            AssertEqual("plan", plan.Kind, "plan kind");
            AssertEqual(1, plan.Children.Count, "plan child count");
            AssertEqual("excel.add_sheet", plan.Children[0].ToolId, "plan child tool");
            AssertContains(plan.Children[0].ArgumentsJson, "Report", "plan child args");

            var command = new SkillCommand { SkillId = "excel.make_report" };
            command.Arguments["sheet"] = "Report";
            var result = SkillResult.Ok(
                "Pipeline executed: excel.make_report",
                "{\"toolId\":\"excel.make_report\",\"steps\":[{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"success\":true,\"message\":\"Added sheet\"},{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"success\":false,\"message\":\"No table\"}]}");
            var activity = AgentTranscript.CreateToolActivity(command, result, "tool");

            AssertEqual("completed", activity.Status, "pipeline parent status");
            AssertEqual(2, activity.Children.Count, "pipeline child count");
            AssertEqual("sheet", activity.Children[0].Title, "first child title");
            AssertEqual("completed", activity.Children[0].Status, "first child status");
            AssertEqual("failed", activity.Children[1].Status, "second child status");

            var session = new ChatSession();
            AgentTranscript.AddLocalResultMessage(session, command, result);
            AssertEqual(1, session.Messages.Count, "transcript message count");
            AssertTrue(session.Messages[0].Activity != null, "transcript activity exists");
            AssertContains(session.Messages[0].Content, "Agent step", "fallback content");
            var promptMessages = PromptMessageBuilder.Build("system", string.Empty, session.Messages, 8000);
            AssertContains(promptMessages.Last().Content, "Structured agent activity", "prompt activity marker");
            AssertContains(promptMessages.Last().Content, "Pipeline executed", "prompt activity result");
        }

        private static void ChatFailedToolRetriesCorrectedCall()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", SkillResult.Fail("No table values provided."));
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1")),
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"]]")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Write a report table.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                    null).GetAwaiter().GetResult();

                AssertEqual(2, calls.Count, "llm call count");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.write_table", adapter.Executed[0].SkillId, "first tool");
                AssertTrue(!adapter.Executed[0].Arguments.ContainsKey("values"), "first command missing values");
                AssertEqual("[[\"Month\",\"Sales\"]]", adapter.Executed[1].Arguments["values"], "retry values");
                var resultJson = JsonConvert.SerializeObject(result.SkillResults);
                AssertContains(resultJson, "No table values provided", "failed result logged");
                AssertContains(resultJson, "executed excel.write_table", "retry result logged");
                AssertTrue(ContainsMessage(session.Messages, "Local skill retry result") || ContainsMessage(session.Messages, "Agent step"), "retry transcript recorded");
            });
        }

        private static void ChatAutoRunDisabledRecordsLocalFailure()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(adapter, executor, calls, AgentBlock(Command("word.insert_text", "text", "Hello")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Insert text into the document.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AutoRunToolCalls = false, ContextCharLimit = 8000 },
                    new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertContains(JsonConvert.SerializeObject(result.SkillResults), "Auto tool execution is disabled", "auto-run result");
                AssertTrue(ContainsMessage(session.Messages, "Agent plan"), "plan recorded");
                AssertTrue(ContainsMessage(session.Messages, "failed"), "failure recorded");
            });
        }

        private static void ChatMalformedToolResponseStaysProse()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("PowerPoint"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var malformed = "I tried this but it is broken:\n```rnassistant-agent\n{\"steps\":[\n```\nExtra noisy text.";
                var service = ChatServiceWithResponses(adapter, executor, calls, malformed);
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Summarize the presentation.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = false, ContextCharLimit = 8000 },
                    new List<SkillDefinition>(adapter.GetBuiltInSkills()),
                    null).GetAwaiter().GetResult();

                AssertEqual(malformed, result.AssistantText, "assistant text");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual(malformed, session.Messages[1].Content, "assistant transcript");
            });
        }

        private static void ChatCloneServicePreservesValues()
        {
            var context = new DocumentContext
            {
                Host = "Excel",
                DocumentKey = "doc",
                Title = "Harness.xlsx",
                Summary = "Pinned summary",
                UpdatedUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
            context.Notes.Add(new ContextNote
            {
                Id = "note-1",
                Host = "Excel",
                Kind = "selection",
                Title = "Cells",
                Reference = "A1",
                Source = "Sheet1!A1",
                Text = "Original note",
                Preview = "Original",
                DetailsJson = "{\"range\":\"A1\"}",
                CreatedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            var clonedContext = ChatCloneService.CloneContext(context);
            AssertTrue(!object.ReferenceEquals(context, clonedContext), "context cloned");
            AssertTrue(!object.ReferenceEquals(context.Notes[0], clonedContext.Notes[0]), "context note cloned");
            AssertEqual("Pinned summary", clonedContext.Summary, "context summary");
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context note text");
            context.Notes[0].Text = "Changed";
            AssertEqual("Original note", clonedContext.Notes[0].Text, "context clone independent");

            var sourceMessage = new ChatMessage
            {
                Id = "message-1",
                Role = "assistant",
                Content = "Done",
                PromptTokens = 10,
                CompletionTokens = 2,
                TotalTokens = 12,
                UsageJson = "{\"total\":12}",
                Activity = new ChatActivity
                {
                    Kind = "tool",
                    Title = "Write table",
                    Status = "completed",
                    ToolId = "excel.write_table",
                    Children = new List<ChatActivity>
                    {
                        new ChatActivity { Kind = "tool", Title = "Nested", Status = "completed", ToolId = "excel.add_sheet" }
                    }
                },
                CreatedUtc = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc)
            };
            var clonedMessages = ChatCloneService.CloneMessages(new[] { sourceMessage });
            AssertEqual(1, clonedMessages.Count, "message count");
            AssertTrue(!object.ReferenceEquals(sourceMessage, clonedMessages[0]), "message cloned");
            AssertEqual("message-1", clonedMessages[0].Id, "message id");
            AssertEqual("assistant", clonedMessages[0].Role, "message role");
            AssertEqual(12, clonedMessages[0].TotalTokens, "message tokens");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity, clonedMessages[0].Activity), "activity cloned");
            AssertTrue(!object.ReferenceEquals(sourceMessage.Activity.Children[0], clonedMessages[0].Activity.Children[0]), "activity child cloned");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity title");
            sourceMessage.Content = "Changed";
            sourceMessage.Activity.Title = "Changed activity";
            AssertEqual("Done", clonedMessages[0].Content, "message clone independent");
            AssertEqual("Write table", clonedMessages[0].Activity.Title, "activity clone independent");
        }

        private static void ContextServiceNormalizesAndUpserts()
        {
            var adapter = new FakeOfficeAdapter();
            var service = new ContextService(adapter);
            var session = new ChatSession
            {
                Host = "Excel",
                DocumentKey = "doc",
                DocumentTitle = "Harness.xlsx",
                Title = "Chat title",
                Context = new DocumentContext { Notes = null }
            };

            var context = service.LoadContext(session);
            AssertEqual("Excel", context.Host, "context host");
            AssertEqual("doc", context.DocumentKey, "context document key");
            AssertEqual("Chat title", context.Title, "context title");
            AssertTrue(context.Notes != null, "notes initialized");

            var note = new ContextNote
            {
                Id = "",
                Host = "",
                Kind = "",
                Title = "",
                Reference = "A1",
                Source = "",
                Text = "first",
                Preview = "",
                DetailsJson = "{\"range\":\"A1\"}"
            };
            service.NormalizeContextNote(note, "selection");
            ContextService.UpsertContextNote(context, note);
            AssertEqual(1, context.Notes.Count, "note count after insert");
            AssertEqual("Excel", context.Notes[0].Host, "note host");
            AssertEqual("selection", context.Notes[0].Kind, "note kind");
            AssertEqual("Harness.xlsx", context.Notes[0].Title, "note title");
            AssertEqual("A1", context.Notes[0].Source, "note source");

            var replacement = new ContextNote
            {
                Host = "Excel",
                Kind = "selection",
                Title = "Changed",
                Reference = "A1",
                Source = "A1",
                Text = "second",
                Preview = "second",
                DetailsJson = "{\"range\":\"A1\"}"
            };
            ContextService.UpsertContextNote(context, replacement);
            AssertEqual(1, context.Notes.Count, "note count after update");
            AssertEqual("Changed", context.Notes[0].Title, "updated note title");
            AssertEqual("second", context.Notes[0].Text, "updated note text");
        }

        private static void ContextServiceTrimsText()
        {
            AssertEqual("abc", ContextService.TrimForContext("abc", 10), "short trim");
            AssertEqual("abc\n...[truncated]", ContextService.TrimForContext("abcdef", 3), "long trim");
            AssertEqual(string.Empty, ContextService.TrimForContext(null, 3), "null trim");
        }

        private static void BridgeUsesTypedRunToolPayload()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b1\",\"type\":\"runTool\",\"payload\":{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"},\"dryRun\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("b1", response["id"].Value<string>(), "bridge response id");
            AssertTrue(response["payload"]["Success"].Value<bool>(), "bridge payload success");
            AssertEqual("excel.add_sheet", controller.LastToolId, "tool id");
            AssertContains(controller.LastArgumentsJson, "Report", "tool args");
            AssertTrue(controller.LastDryRun, "dry run");
            AssertEqual(1, progressMessages.Count, "progress count");
            AssertEqual("progress", JObject.Parse(progressMessages[0])["type"].Value<string>(), "progress type");
        }

        private static void BridgeUsesTypedSendChatPayloadAndProgress()
        {
            var controller = new AssistantController();
            var progressMessages = new List<string>();
            var bridge = new AssistantWebBridge(controller, progressMessages.Add);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b2\",\"type\":\"sendChat\",\"payload\":{\"chatId\":\"chat-1\",\"text\":\"hello\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("ok", response["payload"]["message"].Value<string>(), "chat response message");
            AssertEqual("hello", controller.LastChatText, "chat text");
            AssertEqual("chat-1", controller.LastChatId, "chat id");
            var progress = JObject.Parse(progressMessages[0]);
            AssertEqual("b2", progress["id"].Value<string>(), "progress id");
            AssertEqual("thinking", progress["payload"]["phase"].Value<string>(), "progress phase");
            AssertEqual("Testing progress", progress["payload"]["activity"]["Title"].Value<string>(), "progress activity title");
        }

        private static void BridgeUsesTypedSettingsPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b3\",\"type\":\"saveSettings\",\"payload\":{\"settings\":{\"model\":\"gpt-test\"},\"apiKey\":\"secret\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("gpt-test", controller.LastSettings.Model, "settings model");
            AssertEqual("secret", controller.LastApiKey, "api key");
        }

        private static void BridgeUsesTypedContextPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b4\",\"type\":\"addTextContext\",\"payload\":{\"chatId\":\"chat-2\",\"kind\":\"note\",\"title\":\"T\",\"reference\":\"R\",\"text\":\"Body\",\"detailsJson\":\"{}\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("chat-2", controller.LastChatId, "chat id");
            AssertEqual("note", controller.LastContextKind, "context kind");
            AssertEqual("T", controller.LastContextTitle, "context title");
            AssertEqual("R", controller.LastContextReference, "context reference");
            AssertEqual("Body", controller.LastContextText, "context text");
        }

        private static void BridgeUsesTypedVbaPayload()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var responseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b5\",\"type\":\"saveVbaModule\",\"payload\":{\"moduleName\":\"Module1\",\"code\":\"Sub Main()\\nEnd Sub\"}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("Module1", controller.LastModuleName, "module name");
            AssertContains(controller.LastModuleCode, "Sub Main", "module code");
        }

        private static SkillDefinition CustomTool(string host, string id)
        {
            return new SkillDefinition
            {
                Id = id,
                Host = host,
                Name = id,
                Executor = "pipeline",
                Enabled = true,
                BuiltIn = false,
                PipelineJson = "{\"steps\":[]}"
            };
        }

        private static bool HasTool(IEnumerable<SkillDefinition> tools, string id)
        {
            foreach (var tool in tools)
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static SkillDefinition FindTool(IEnumerable<SkillDefinition> tools, string id)
        {
            foreach (var tool in tools ?? new SkillDefinition[0])
            {
                if (tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return tool;
                }
            }

            return null;
        }

        private static bool ContainsMessage(IEnumerable<ChatMessage> messages, string text)
        {
            foreach (var message in messages ?? new ChatMessage[0])
            {
                if (message != null && (message.Content ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<SkillDefinition> BuildPipelineTools(bool requiresConfirmation)
        {
            return new List<SkillDefinition>
            {
                new SkillDefinition
                {
                    Id = "excel.make_report",
                    Host = "Excel",
                    Name = "Make report",
                    Executor = "pipeline",
                    Enabled = true,
                    RequiresConfirmation = requiresConfirmation,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\",\"values\":\"[[\\\"Month\\\",\\\"Sales\\\"]]\"}}" +
                        "]}"
                }
            };
        }

        private static List<SkillDefinition> BuildStepPlaceholderPipelineTools()
        {
            return new List<SkillDefinition>
            {
                new SkillDefinition
                {
                    Id = "excel.chain_report",
                    Host = "Excel",
                    Name = "Chain report",
                    Executor = "pipeline",
                    Enabled = true,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Report\",\"sourceMessage\":\"{{steps.sheet.message}}\",\"sourceSuccess\":\"{{steps.sheet.success}}\"}}" +
                        "]}"
                }
            };
        }

        private static List<SkillDefinition> BuildThreeStepPipelineTools()
        {
            return new List<SkillDefinition>
            {
                new SkillDefinition
                {
                    Id = "excel.full_report",
                    Host = "Excel",
                    Name = "Full report",
                    Executor = "pipeline",
                    Enabled = true,
                    PipelineJson = "{" +
                        "\"steps\":[" +
                        "{\"id\":\"sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"{{args.sheet}}\"}}," +
                        "{\"id\":\"table\",\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"startAddress\":\"A1\"}}," +
                        "{\"id\":\"chart\",\"toolId\":\"excel.add_chart\",\"arguments\":{\"sheet\":\"{{args.sheet}}\",\"sourceRange\":\"A1:B2\",\"chartType\":\"column\",\"title\":\"Report\"}}" +
                        "]}"
                }
            };
        }

        private static SkillCommand Command(string id, params object[] keyValues)
        {
            var command = new SkillCommand { SkillId = id };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static string AgentBlock(params SkillCommand[] commands)
        {
            return "```rnassistant-agent\n" +
                JsonConvert.SerializeObject(new
                {
                    steps = (commands ?? new SkillCommand[0]).Select(command => new
                    {
                        skillId = command.SkillId,
                        arguments = command.Arguments
                    }).ToArray()
                }) +
                "\n```";
        }

        private static ChatCompletionService ChatServiceWithResponses(
            FakeOfficeAdapter adapter,
            OfficeToolExecutor executor,
            ICollection<IReadOnlyList<ChatMessage>> calls,
            params string[] responses)
        {
            var index = 0;
            return new ChatCompletionService(
                adapter,
                executor,
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages)
                {
                    if (calls != null)
                    {
                        calls.Add(new List<ChatMessage>(messages ?? new ChatMessage[0]));
                    }

                    var content = index < (responses == null ? 0 : responses.Length)
                        ? responses[index]
                        : "Done.";
                    index += 1;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = content,
                        PromptTokens = 10,
                        CompletionTokens = 2,
                        TotalTokens = 12
                    });
                });
        }

        private static ChatSession NewSession(FakeOfficeAdapter adapter)
        {
            return new ChatSession
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                DocumentTitle = adapter.DocumentTitle,
                Title = "New chat"
            };
        }

        private static DocumentContext NewContext(FakeOfficeAdapter adapter)
        {
            return new DocumentContext
            {
                Host = adapter.HostName,
                DocumentKey = adapter.DocumentKey,
                Title = adapter.DocumentTitle
            };
        }

        private static void WithTempExecutor(Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempExecutor(new FakeOfficeAdapter(), action);
        }

        private static void WithTempExecutor(FakeOfficeAdapter adapter, Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths));
                action(executor, adapter);
            });
        }

        private static void WithTempPaths(Action<AppDataPaths> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "RNAssistant.Harness." + Guid.NewGuid().ToString("N"));
            try
            {
                action(AppDataPaths.CreateForRoot(root));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string name)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(name + ": expected '" + expected + "', got '" + actual + "'");
            }
        }

        private static void AssertTrue(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name + " was false");
            }
        }

        private static void AssertContains(string value, string expected, string name)
        {
            if ((value ?? string.Empty).IndexOf(expected, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(name + ": expected '" + value + "' to contain '" + expected + "'");
            }
        }

        private sealed class FakeOfficeAdapter : IOfficeApplicationAdapter
        {
            public readonly List<SkillCommand> Executed = new List<SkillCommand>();
            public string VbaModuleCode = string.Empty;
            public string VbaModuleType = "StdModule";
            public bool FailUnknownSkills { get; set; }

            private readonly string _hostName;
            private readonly string _documentTitle;
            private readonly string _documentSnapshot;
            private readonly List<SkillDefinition> _builtInSkills;
            private readonly Dictionary<string, Queue<SkillResult>> _scriptedResults;

            public FakeOfficeAdapter()
                : this("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness document")
            {
            }

            private FakeOfficeAdapter(string hostName, string documentTitle, IEnumerable<SkillDefinition> builtInSkills, string documentSnapshot)
            {
                _hostName = hostName;
                _documentTitle = documentTitle;
                _documentSnapshot = documentSnapshot;
                _builtInSkills = new List<SkillDefinition>((builtInSkills ?? new SkillDefinition[0]).Select(CloneSkill));
                _scriptedResults = new Dictionary<string, Queue<SkillResult>>(StringComparer.OrdinalIgnoreCase);
            }

            public static FakeOfficeAdapter ForHost(string host)
            {
                if (string.Equals(host, "Word", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("Word", "Harness.docx", WordBuiltIns(), "Harness Word document");
                }

                if (string.Equals(host, "PowerPoint", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("PowerPoint", "Harness.pptx", PowerPointBuiltIns(), "Harness slide deck");
                }

                if (string.Equals(host, "Outlook", StringComparison.OrdinalIgnoreCase))
                {
                    return new FakeOfficeAdapter("Outlook", "Selected mail", OutlookBuiltIns(), "Subject: Harness mail");
                }

                return new FakeOfficeAdapter("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness workbook");
            }

            public string HostName { get { return _hostName; } }
            public string DocumentKey { get { return "doc"; } }
            public string LegacyDocumentKey { get { return "legacy-doc"; } }
            public string RuntimeDocumentKey { get { return "runtime-doc"; } }
            public string DocumentTitle { get { return _documentTitle; } }

            public string GetDocumentSnapshot(int maxChars)
            {
                return _documentSnapshot;
            }

            public string GetVbaSnapshot(int maxChars)
            {
                return string.Empty;
            }

            public void PrepareForContextCapture()
            {
            }

            public ContextNote CaptureSelectionContext(string mode, int maxChars)
            {
                return null;
            }

            public IEnumerable<SkillDefinition> GetBuiltInSkills()
            {
                return _builtInSkills.Select(CloneSkill).ToArray();
            }

            public void QueueResult(string skillId, SkillResult result)
            {
                Queue<SkillResult> queue;
                if (!_scriptedResults.TryGetValue(skillId, out queue))
                {
                    queue = new Queue<SkillResult>();
                    _scriptedResults[skillId] = queue;
                }

                queue.Enqueue(result);
            }

            public SkillResult ExecuteSkill(SkillCommand command)
            {
                Executed.Add(Clone(command));
                SkillResult scripted;
                if (TryDequeueResult(command.SkillId, out scripted))
                {
                    return scripted;
                }

                if ((command.SkillId ?? string.Empty).EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase))
                {
                    return SkillResult.Ok("read " + command.SkillId, JsonConvert.SerializeObject(new { code = VbaModuleCode, type = VbaModuleType }));
                }

                if ((command.SkillId ?? string.Empty).EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase))
                {
                    object code;
                    if (command.Arguments.TryGetValue("code", out code))
                    {
                        VbaModuleCode = Convert.ToString(code);
                    }

                    return SkillResult.Ok("replaced " + command.SkillId);
                }

                if (FailUnknownSkills && !IsKnownSkill(command.SkillId))
                {
                    return SkillResult.Fail("Unsupported " + HostName + " skill: " + command.SkillId);
                }

                return SkillResult.Ok("executed " + command.SkillId, JsonConvert.SerializeObject(new { host = HostName, skillId = command.SkillId }));
            }

            private bool TryDequeueResult(string skillId, out SkillResult result)
            {
                result = null;
                Queue<SkillResult> queue;
                if (!_scriptedResults.TryGetValue(skillId ?? string.Empty, out queue) || queue.Count == 0)
                {
                    return false;
                }

                result = queue.Dequeue();
                return true;
            }

            private bool IsKnownSkill(string skillId)
            {
                return _builtInSkills.Any(skill => string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase));
            }

            private static IEnumerable<SkillDefinition> ExcelBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Excel", "excel.workbook_summary", false, false, true),
                    BuiltIn("Excel", "excel.list_sheets", false, false, true),
                    BuiltIn("Excel", "excel.read_range", false, false, true),
                    BuiltIn("Excel", "excel.write_range", false, true, true),
                    BuiltIn("Excel", "excel.write_table", false, true, true),
                    BuiltIn("Excel", "excel.add_chart", false, true, true),
                    BuiltIn("Excel", "excel.add_sheet", false, true, true),
                    BuiltIn("Excel", "excel.vba_read_project", false, false, true),
                    BuiltIn("Excel", "excel.vba_read_module", false, false, true),
                    BuiltIn("Excel", "excel.vba_replace_module", false, true, false),
                    BuiltIn("Excel", "excel.insert_vba_module", false, true, false),
                    BuiltIn("Excel", "excel.run_macro", false, true, false)
                };
            }

            private static IEnumerable<SkillDefinition> WordBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Word", "word.read_document", false, false, true),
                    BuiltIn("Word", "word.read_selection", false, false, true),
                    BuiltIn("Word", "word.insert_text", false, true, true),
                    BuiltIn("Word", "word.replace_selection", false, true, true),
                    BuiltIn("Word", "word.add_comment", false, true, true),
                    BuiltIn("Word", "word.vba_read_project", false, false, true),
                    BuiltIn("Word", "word.vba_read_module", false, false, true),
                    BuiltIn("Word", "word.vba_replace_module", false, true, false),
                    BuiltIn("Word", "word.insert_vba_module", false, true, false),
                    BuiltIn("Word", "word.run_macro", false, true, false)
                };
            }

            private static IEnumerable<SkillDefinition> PowerPointBuiltIns()
            {
                return new[]
                {
                    BuiltIn("PowerPoint", "powerpoint.read_slides", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.add_slide", false, true, true),
                    BuiltIn("PowerPoint", "powerpoint.replace_selection_text", false, true, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_read_project", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_read_module", false, false, true),
                    BuiltIn("PowerPoint", "powerpoint.vba_replace_module", false, true, false),
                    BuiltIn("PowerPoint", "powerpoint.insert_vba_module", false, true, false),
                    BuiltIn("PowerPoint", "powerpoint.run_macro", false, true, false)
                };
            }

            private static IEnumerable<SkillDefinition> OutlookBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Outlook", "outlook.read_selection", false, false, true),
                    BuiltIn("Outlook", "outlook.draft_reply", false, true, true),
                    BuiltIn("Outlook", "outlook.collect_folder_mail", false, false, true),
                    BuiltIn("Outlook", "outlook.collect_monthly_summary_data", false, false, true)
                };
            }

            private static SkillDefinition BuiltIn(string host, string id, bool requiresConfirmation, bool mutatesDocument, bool agentCanRun)
            {
                return new SkillDefinition
                {
                    Id = id,
                    Host = host,
                    Name = id,
                    Enabled = true,
                    BuiltIn = true,
                    RequiresConfirmation = requiresConfirmation,
                    MutatesDocument = mutatesDocument,
                    AgentCanRun = agentCanRun
                };
            }

            private static SkillDefinition CloneSkill(SkillDefinition skill)
            {
                return new SkillDefinition
                {
                    Id = skill.Id,
                    Host = skill.Host,
                    Name = skill.Name,
                    Description = skill.Description,
                    ArgumentSchemaJson = skill.ArgumentSchemaJson,
                    Executor = skill.Executor,
                    RequiresConfirmation = skill.RequiresConfirmation,
                    MutatesDocument = skill.MutatesDocument,
                    AgentCanRun = skill.AgentCanRun,
                    PipelineJson = skill.PipelineJson,
                    Code = skill.Code,
                    Readme = skill.Readme,
                    StoragePath = skill.StoragePath,
                    Enabled = skill.Enabled,
                    BuiltIn = skill.BuiltIn
                };
            }

            private static SkillCommand Clone(SkillCommand command)
            {
                var clone = new SkillCommand { SkillId = command.SkillId, Description = command.Description };
                foreach (var pair in command.Arguments)
                {
                    clone.Arguments[pair.Key] = pair.Value;
                }
                return clone;
            }
        }
    }
}
