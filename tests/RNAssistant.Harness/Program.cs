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
using RNAssistant.OfficeHosts;

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
                new HarnessTest { Name = "parser: normalizes primitive and complex args", Run = ParserNormalizesPrimitiveAndComplexArgs },
                new HarnessTest { Name = "parser: noisy embedded json", Run = ParsesNoisyEmbeddedJson },
                new HarnessTest { Name = "parser: bad json skipped", Run = SkipsBadJson },
                new HarnessTest { Name = "parser: recovers malformed agent json", Run = RecoversMalformedAgentJson },
                new HarnessTest { Name = "desktop target: parses json descriptor", Run = ParsesOfficeTargetJsonDescriptor },
                new HarnessTest { Name = "desktop target: parses base64 descriptor", Run = ParsesOfficeTargetBase64Descriptor },
                new HarnessTest { Name = "desktop target: ignores utf8 bom", Run = OfficeTargetIgnoresUtf8Bom },
                new HarnessTest { Name = "storage: chat roundtrip", Run = CreatesAndListsChatsInTempRoot },
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles },
                new HarnessTest { Name = "chat sessions: document key migration", Run = ChatSessionServiceMigratesDocumentKey },
                new HarnessTest { Name = "chat sessions: legacy document key migration", Run = ChatSessionServiceMigratesLegacyDocumentKey },
                new HarnessTest { Name = "chat sessions: stale requested id fallback", Run = ChatSessionServiceFallsBackForStaleRequestedId },
                new HarnessTest { Name = "pipeline: dry-run resolves placeholders", Run = PipelineDryRunResolvesPlaceholders },
                new HarnessTest { Name = "pipeline: executes fake adapter steps", Run = PipelineExecutesFakeAdapterSteps },
                new HarnessTest { Name = "pipeline: resolves step output placeholders", Run = PipelineResolvesStepOutputPlaceholders },
                new HarnessTest { Name = "pipeline: stops after failed step", Run = PipelineStopsAfterFailedStep },
                new HarnessTest { Name = "pipeline: rejects missing step tool id", Run = PipelineRejectsMissingStepToolId },
                new HarnessTest { Name = "pipeline: rejects invalid definitions", Run = PipelineRejectsInvalidDefinitions },
                new HarnessTest { Name = "pipeline: enforces nesting limit", Run = PipelineEnforcesNestingLimit },
                new HarnessTest { Name = "pipeline: custom tool needs confirmation", Run = CustomPipelineNeedsConfirmation },
                new HarnessTest { Name = "pipeline: agent mode gates built-in mutation", Run = AgentModeGatesBuiltInMutation },
                new HarnessTest { Name = "tools: catalog merges visible tools", Run = ToolCatalogMergesVisibleTools },
                new HarnessTest { Name = "tools: store saves and updates custom tools", Run = ToolStoreSavesAndUpdatesCustomTools },
                new HarnessTest { Name = "tools: store skips broken custom tool files", Run = ToolStoreSkipsBrokenCustomToolFiles },
                new HarnessTest { Name = "tools: unknown and disabled tools fail", Run = UnknownAndDisabledToolsFail },
                new HarnessTest { Name = "tools: safety metadata gates mutations", Run = ToolSafetyMetadataGatesMutations },
                new HarnessTest { Name = "tools: confirmation matrix covers dry and manual runs", Run = ConfirmationMatrixCoversDryAndManualRuns },
                new HarnessTest { Name = "skills: store saves markdown skills", Run = SkillStoreSavesMarkdownSkills },
                new HarnessTest { Name = "skills: store skips broken markdown skills", Run = SkillStoreSkipsBrokenMarkdownSkills },
                new HarnessTest { Name = "skills: catalog selects relevant skills", Run = SkillCatalogSelectsRelevantSkills },
                new HarnessTest { Name = "skills: prompt separates skills from tools", Run = PromptSeparatesSkillsFromTools },
                new HarnessTest { Name = "skills: prompt limits skill bodies", Run = PromptLimitsSkillBodies },
                new HarnessTest { Name = "skills: agent can save skills with confirmation", Run = AgentCanSaveSkillsWithConfirmation },
                new HarnessTest { Name = "vba: replace text backs up module", Run = VbaReplaceTextBacksUpModule },
                new HarnessTest { Name = "vba: apply patch targets named module", Run = VbaApplyPatchTargetsNamedModule },
                new HarnessTest { Name = "vba: backup store skips broken files", Run = VbaBackupStoreSkipsBrokenFiles },
                new HarnessTest { Name = "prompt: trims oldest history", Run = PromptBuilderTrimsOldestHistory },
                new HarnessTest { Name = "prompt: usage estimator counts context", Run = ContextUsageEstimatorCountsPromptAndSession },
                new HarnessTest { Name = "chat: completion service records prose", Run = ChatCompletionServiceRecordsProseResponse },
                new HarnessTest { Name = "chat: deferred smart title setting", Run = ChatCompletionServiceUsesDeferredSmartTitleSetting },
                new HarnessTest { Name = "chat: executes typical host tasks", Run = ChatExecutesTypicalHostTasks },
                new HarnessTest { Name = "chat: agent activity transcript", Run = AgentTranscriptCreatesActivityTree },
                new HarnessTest { Name = "chat: prose action forces tool follow-up", Run = ChatProseActionForcesToolFollowUp },
                new HarnessTest { Name = "chat: malformed action response forces repair", Run = ChatMalformedActionResponseForcesRepair },
                new HarnessTest { Name = "chat: failed tool retries corrected call", Run = ChatFailedToolRetriesCorrectedCall },
                new HarnessTest { Name = "chat: unknown tool retries exact available id", Run = ChatUnknownToolRetriesExactAvailableId },
                new HarnessTest { Name = "chat: retry success continues", Run = ChatRetrySuccessContinuesToFinalAnswer },
                new HarnessTest { Name = "chat: agent disabled skips tool block", Run = ChatAgentDisabledSkipsToolBlock },
                new HarnessTest { Name = "chat: waiting tool gets pending id", Run = ChatWaitingToolGetsPendingId },
                new HarnessTest { Name = "chat: waiting tool stops batch", Run = ChatWaitingToolStopsBatch },
                new HarnessTest { Name = "chat: max iterations returns summary", Run = ChatMaxIterationsReturnsRuntimeSummary },
                new HarnessTest { Name = "chat: auto-run disabled records failure", Run = ChatAutoRunDisabledRecordsLocalFailure },
                new HarnessTest { Name = "chat: malformed tool response stays prose", Run = ChatMalformedToolResponseStaysProse },
                new HarnessTest { Name = "chat: explicit clone preserves values", Run = ChatCloneServicePreservesValues },
                new HarnessTest { Name = "context: core normalizer", Run = ContextNormalizerUsesCoreModelsOnly },
                new HarnessTest { Name = "context: normalize and upsert", Run = ContextServiceNormalizesAndUpserts },
                new HarnessTest { Name = "context: trim helper", Run = ContextServiceTrimsText },
                new HarnessTest { Name = "bridge: typed runTool payload", Run = BridgeUsesTypedRunToolPayload },
                new HarnessTest { Name = "bridge: typed sendChat progress", Run = BridgeUsesTypedSendChatPayloadAndProgress },
                new HarnessTest { Name = "bridge: typed settings payload", Run = BridgeUsesTypedSettingsPayload },
                new HarnessTest { Name = "bridge: typed tool and skill payloads", Run = BridgeUsesTypedToolAndSkillPayloads },
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
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"steps\":[" +
                "{\"description\":\"Add sheet\",\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"}}," +
                "{\"toolId\":\"excel.add_chart\",\"args\":{\"title\":\"Sales\"}}" +
                "]}" +
                "\n```");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("excel.add_sheet", commands[0].ToolId, "first tool id");
            AssertEqual("Report", commands[0].Arguments["name"], "first arg");
            AssertEqual("excel.add_chart", commands[1].ToolId, "second tool id");
        }

        private static void ParsesNativeToolCalls()
        {
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\",\"function\":{\"name\":\"excel.write_table\",\"arguments\":\"{\\\"sheet\\\":\\\"Data\\\",\\\"startAddress\\\":\\\"A1\\\"}\"}}]}" +
                "\n```");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("excel.write_table", commands[0].ToolId, "tool id");
            AssertEqual("Data", commands[0].Arguments["sheet"], "sheet arg");
            AssertEqual("A1", commands[0].Arguments["startAddress"], "address arg");
        }

        private static void ParserNormalizesPrimitiveAndComplexArgs()
        {
            var commands = new ToolCommandParser().Parse(
                "```rnassistant-agent\n" +
                "{\"toolId\":\"excel.write_table\",\"arguments\":{\"sheet\":\"Data\",\"count\":2,\"enabled\":true,\"values\":[[\"Month\",\"Sales\"]],\"meta\":{\"source\":\"test\"}}}" +
                "\n```");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("Data", commands[0].Arguments["sheet"], "string arg");
            AssertEqual(2L, commands[0].Arguments["count"], "integer arg");
            AssertEqual(true, commands[0].Arguments["enabled"], "bool arg");
            AssertEqual("[[\"Month\",\"Sales\"]]", commands[0].Arguments["values"], "array arg");
            AssertEqual("{\"source\":\"test\"}", commands[0].Arguments["meta"], "object arg");
        }

        private static void ParsesBareJsonArray()
        {
            var commands = new ToolCommandParser().Parse(
                "[" +
                "{\"tool\":\"word.insert_text\",\"parameters\":{\"text\":\"Hello\"}}," +
                "{\"action\":\"excel.autofit\",\"input\":{\"sheet\":\"Data\"}}" +
                "]");

            AssertEqual(2, commands.Count, "command count");
            AssertEqual("word.insert_text", commands[0].ToolId, "first tool id");
            AssertEqual("Hello", commands[0].Arguments["text"], "text arg");
            AssertEqual("excel.autofit", commands[1].ToolId, "second tool id");
        }

        private static void ParsesNoisyEmbeddedJson()
        {
            var commands = new ToolCommandParser().Parse(
                "I will handle it. First, here is the plan: " +
                "{\"steps\":[{\"toolId\":\"powerpoint.add_slide\",\"arguments\":{\"title\":\"Q1\",\"body\":\"Revenue grew\"}}]} " +
                "Then I will summarize.");

            AssertEqual(1, commands.Count, "command count");
            AssertEqual("powerpoint.add_slide", commands[0].ToolId, "tool id");
            AssertEqual("Q1", commands[0].Arguments["title"], "title arg");
        }

        private static void SkipsBadJson()
        {
            var commands = new ToolCommandParser().Parse("```rnassistant-agent\n{\"steps\":[\n```");
            AssertEqual(0, commands.Count, "command count");
        }

        private static void RecoversMalformedAgentJson()
        {
            var result = new ToolCommandParser().ParseWithDiagnostics(
                "```rnassistant-agent\n" +
                "{\"steps\":[{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\"},}\n```");

            AssertEqual(1, result.Commands.Count, "command count");
            AssertEqual("excel.add_sheet", result.Commands[0].ToolId, "tool id");
            AssertEqual("Report", result.Commands[0].Arguments["name"], "sheet name");
            AssertTrue(result.HasRecoveredCommands, "recovery diagnostic");
        }

        private static void ParsesOfficeTargetJsonDescriptor()
        {
            var target = OfficeTargetDescriptor.FromJson("{\"Host\":\"Excel\",\"FullName\":\"C:\\\\Docs\\\\Book.xlsx\",\"Name\":\"Book.xlsx\",\"Selection\":\"Sheet1!A1:B2\"}");
            AssertEqual("Excel", target.Host, "host");
            AssertEqual("C:\\Docs\\Book.xlsx", target.FullName, "full name");
            AssertEqual("Book.xlsx", target.Name, "name");
            AssertEqual("Sheet1!A1:B2", target.Selection, "selection");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void ParsesOfficeTargetBase64Descriptor()
        {
            var json = "{\"Host\":\"Outlook\",\"EntryId\":\"abc123\",\"Name\":\"Mail\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Outlook", target.Host, "host");
            AssertEqual("abc123", target.EntryId, "entry id");
            AssertEqual("Mail", target.Name, "name");
            AssertTrue(target.HasDocumentIdentity, "has identity");
        }

        private static void OfficeTargetIgnoresUtf8Bom()
        {
            var json = "\uFEFF{\"Host\":\"Word\",\"FullName\":\"C:\\\\Docs\\\\Doc.docx\"}";
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var target = OfficeTargetDescriptor.FromBase64Json(base64);
            AssertEqual("Word", target.Host, "host");
            AssertEqual("C:\\Docs\\Doc.docx", target.FullName, "full name");
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

        private static void ChatSessionServiceMigratesDocumentKey()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var session = service.LoadSession(null);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "before save" });
                store.Save(session);

                adapter.DocumentKeyValue = "saved-doc";
                var migrated = service.LoadSession(null);

                AssertEqual(ChatStore.GetSessionId(session), ChatStore.GetSessionId(migrated), "migrated session id");
                AssertEqual("saved-doc", migrated.DocumentKey, "migrated document key");
                AssertEqual(1, migrated.Messages.Count, "migrated message count");
                AssertEqual(0, store.List("Excel", "doc", "Harness.xlsx").Count, "old document sessions");
                AssertEqual(1, store.List("Excel", "saved-doc", "Harness.xlsx").Count, "new document sessions");
            });
        }

        private static void ChatSessionServiceMigratesLegacyDocumentKey()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var legacy = store.Create(adapter.HostName, adapter.LegacyDocumentKey, adapter.DocumentTitle, "Legacy");
                legacy.Messages.Add(new ChatMessage { Role = "user", Content = "legacy chat" });
                store.Save(legacy);

                var service = new ChatSessionService(adapter, store);
                var loaded = service.LoadSession(null);

                AssertEqual(ChatStore.GetSessionId(legacy), ChatStore.GetSessionId(loaded), "legacy session id");
                AssertEqual(adapter.DocumentKey, loaded.DocumentKey, "legacy migrated document key");
                AssertEqual(1, loaded.Messages.Count, "legacy message count");
                AssertEqual(0, store.List(adapter.HostName, adapter.LegacyDocumentKey, adapter.DocumentTitle).Count, "legacy sessions moved");
                AssertEqual(1, store.List(adapter.HostName, adapter.DocumentKey, adapter.DocumentTitle).Count, "current sessions");
            });
        }

        private static void ChatSessionServiceFallsBackForStaleRequestedId()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var service = new ChatSessionService(adapter, store);
                var oldSession = service.LoadSession(null);
                var oldId = ChatStore.GetSessionId(oldSession);
                oldSession.Messages.Add(new ChatMessage { Role = "user", Content = "old doc" });
                store.Save(oldSession);

                adapter.DocumentKeyValue = "other-doc";
                adapter.RuntimeDocumentKeyValue = "other-runtime-doc";

                var current = service.LoadSession(oldId, true);

                AssertTrue(!string.Equals(oldId, ChatStore.GetSessionId(current), StringComparison.OrdinalIgnoreCase), "fallback created current session");
                AssertEqual("other-doc", current.DocumentKey, "fallback document key");
                AssertEqual(0, current.Messages.Count, "fallback message count");
                AssertEqual(1, store.List("Excel", "doc", "Harness.xlsx").Count, "old document preserved");
                AssertEqual(1, store.List("Excel", "other-doc", "Harness.xlsx").Count, "new document session");
            });
        }

        private static void PipelineDryRunResolvesPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(false);
                var command = new ToolCommand { ToolId = "excel.make_report" };
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
                var command = new ToolCommand { ToolId = "excel.make_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "pipeline result");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "first tool");
                AssertEqual("Report", adapter.Executed[0].Arguments["name"], "first arg");
                AssertEqual("excel.write_table", adapter.Executed[1].ToolId, "second tool");
                AssertEqual("Report", adapter.Executed[1].Arguments["sheet"], "second arg");
            });
        }

        private static void PipelineResolvesStepOutputPlaceholders()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildStepPlaceholderPipelineTools();
                var command = new ToolCommand { ToolId = "excel.chain_report" };

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
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
                var tools = BuildThreeStepPipelineTools();
                var command = new ToolCommand { ToolId = "excel.full_report" };
                command.Arguments["sheet"] = "Report";

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "Pipeline step failed: table", "failure message");
                AssertContains(result.DataJson, "\"id\":\"table\"", "failure data keeps failed step");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "first tool");
                AssertEqual("excel.write_table", adapter.Executed[1].ToolId, "failed tool");
            });
        }

        private static void PipelineRejectsMissingStepToolId()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<ToolDefinition>
                {
                    new ToolDefinition
                    {
                        Id = "excel.bad_step",
                        Host = "Excel",
                        Name = "Bad step",
                        Executor = "pipeline",
                        Enabled = true,
                        PipelineJson = "{\"steps\":[{\"id\":\"prepare\",\"arguments\":{\"name\":\"Report\"}}]}"
                    }
                };

                var result = executor.Execute(new ToolCommand { ToolId = "excel.bad_step" }, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "pipeline should fail");
                AssertContains(result.Message, "Pipeline step has no toolId", "missing tool id message");
                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
            });
        }

        private static void CustomPipelineNeedsConfirmation()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = BuildPipelineTools(true);
                var command = new ToolCommand { ToolId = "excel.make_report" };
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
                var command = new ToolCommand { ToolId = "excel.make_report" };
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
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
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

        private static void ToolStoreSkipsBrokenCustomToolFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var validDirectory = Path.Combine(paths.ToolsDirectory, "excel", "valid");
                var brokenDirectory = Path.Combine(paths.ToolsDirectory, "excel", "broken");
                Directory.CreateDirectory(validDirectory);
                Directory.CreateDirectory(brokenDirectory);
                File.WriteAllText(Path.Combine(validDirectory, "tool.json"), JsonConvert.SerializeObject(CustomTool("Excel", "excel.valid")));
                File.WriteAllText(Path.Combine(validDirectory, "pipeline.json"), "{\"steps\":[]}");
                File.WriteAllText(Path.Combine(brokenDirectory, "tool.json"), "{ broken");

                var loaded = new ToolStore(paths).Load();

                AssertEqual(1, loaded.Count, "loaded tool count");
                AssertEqual("excel.valid", loaded[0].Id, "loaded tool id");
                AssertContains(loaded[0].PipelineJson, "steps", "sidecar loaded");
            });
        }

        private static void ToolSafetyMetadataGatesMutations()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = new List<ToolDefinition>(adapter.GetBuiltInTools());
                tools.Add(new ToolDefinition
                {
                    Id = "excel.metadata_mutation",
                    Host = "Excel",
                    Name = "metadata mutation",
                    BuiltIn = true,
                    Enabled = true,
                    MutatesDocument = true,
                    AgentCanRun = true
                });
                var command = new ToolCommand { ToolId = "excel.metadata_mutation" };

                var blocked = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "metadata mutation blocked");
                AssertContains(blocked.Message, "requires confirmation", "metadata block message");
                AssertEqual(0, adapter.Executed.Count, "blocked adapter execution count");

                var allowed = executor.Execute(command, tools, new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false }, false, false);
                AssertTrue(allowed.Success, "metadata mutation allowed in agent mode");
                AssertEqual(1, adapter.Executed.Count, "allowed adapter execution count");
            });
        }

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
            var prompt = new PromptComposer().ComposeSystemPrompt(
                new AppSettings { AgentModeEnabled = true },
                "Excel",
                string.Empty,
                string.Empty,
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
                },
                null);

            AssertContains(prompt, "Relevant markdown skills", "skills section");
            AssertContains(prompt, "Available tools", "tools section");
            AssertContains(prompt, "\"toolId\":\"tool.id\"", "tool id protocol");
            AssertContains(prompt, "Skills are guidance documents only", "skill guidance boundary");
        }

        private static void PromptLimitsSkillBodies()
        {
            var longBody = "# Long skill\n" + new string('a', 2500) + "TAIL_MARKER";
            var prompt = new PromptComposer().ComposeSystemPrompt(
                new AppSettings { AgentModeEnabled = true, ContextCharLimit = 4000 },
                "Excel",
                string.Empty,
                string.Empty,
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
                },
                null);

            AssertContains(prompt, "common.long_skill", "skill id");
            AssertContains(prompt, "[truncated]", "skill body truncated");
            AssertTrue(prompt.IndexOf("TAIL_MARKER", StringComparison.OrdinalIgnoreCase) < 0, "skill tail omitted");
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

        private static void VbaReplaceTextBacksUpModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = new ToolCommand { ToolId = executor.VbaToolId("vba_replace_text") };
                command.Arguments["moduleName"] = "Module1";
                command.Arguments["find"] = "\"old\"";
                command.Arguments["replace"] = "\"new\"";

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false }, false, false);
                AssertTrue(!blocked.Success, "vba replace blocked");
                AssertEqual(0, adapter.Executed.Count, "blocked vba adapter execution count");

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "replace result");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "updated module");
                AssertTrue(adapter.VbaModuleCode.IndexOf("\"old\"", StringComparison.Ordinal) < 0, "old text removed");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module1", backups[0].ModuleName, "backup module");
                AssertContains(backups[0].Code, "\"old\"", "backup code");
            });
        }

        private static void VbaApplyPatchTargetsNamedModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.SetVbaModule("Module1", "Sub Main()\nDebug.Print \"untouched\"\nEnd Sub", "StdModule");
                adapter.SetVbaModule("Module2", "Sub Run()\nDebug.Print \"old\"\nEnd Sub", "StdModule");
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = new ToolCommand { ToolId = executor.VbaToolId("vba_apply_patch") };
                command.Arguments["moduleName"] = "Module2";
                command.Arguments["patch"] = "[{\"op\":\"replaceFirst\",\"find\":\"\\\"old\\\"\",\"text\":\"\\\"new\\\"\"}]";

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "patch result");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "\"new\"", "module2 updated");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "\"untouched\"", "module1 untouched");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module2", backups[0].ModuleName, "backup module");
                AssertContains(backups[0].Code, "\"old\"", "backup code");
            });
        }

        private static void VbaBackupStoreSkipsBrokenFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new VbaBackupStore(paths);
                var backup = store.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Main()\nEnd Sub");
                var directory = Path.Combine(paths.VbaBackupDirectory, AppDataPaths.SafeFileName("Excel|doc"));
                File.WriteAllText(Path.Combine(directory, "broken.json"), "{ broken");

                var backups = store.List("Excel", "doc");

                AssertEqual(1, backups.Count, "backup count");
                AssertEqual(backup.BackupId, backups[0].BackupId, "backup id");
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
                    delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, session.Messages.Count, "session message count");
                AssertEqual("hello world", session.Messages[0].Content, "user message");
                AssertEqual("Done.", session.Messages[1].Content, "assistant message");
                AssertEqual("New chat", session.Title, "session title");
                AssertTrue(ContainsMessage(capturedMessages, "User-added context attachments"), "context prompt captured");
            });
        }

        private static void ChatCompletionServiceUsesDeferredSmartTitleSetting()
        {
            var requestedMessages = new List<ChatMessage>();
            var title = ChatTitleBuilder.GenerateLlmTitleAsync(
                new AppSettings { ContextCharLimit = 8000 },
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.",
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    requestedMessages = new List<ChatMessage>(messages ?? new ChatMessage[0]);
                    AssertEqual(32, settings.MaxTokens, "title max tokens");
                    return Task.FromResult(new LlmCompletionResult { Content = "Продажи по месяцам." });
                },
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual("Продажи по месяцам", title, "llm title");
            AssertTrue(ContainsMessage(requestedMessages, "Запрос пользователя"), "title prompt contains user label");

            var fallbackSession = new ChatSession { Title = "New chat" };
            ChatTitleBuilder.ApplyFallback(
                fallbackSession,
                "Нужно сделать отчет по продажам.",
                "Отчет по продажам создан и сохранен.");
            AssertEqual("Отчет по продажам создан и сохранен", fallbackSession.Title, "fallback title");
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
                        new List<ToolDefinition>(adapter.GetBuiltInTools()),
                        null).GetAwaiter().GetResult();

                    AssertEqual("Done.", result.AssistantText, scenario.Host + " assistant text");
                    AssertEqual(scenario.ExpectedTools.Length, adapter.Executed.Count, scenario.Host + " executed count");
                    for (var toolIndex = 0; toolIndex < scenario.ExpectedTools.Length; toolIndex++)
                    {
                        AssertEqual(scenario.ExpectedTools[toolIndex], adapter.Executed[toolIndex].ToolId, scenario.Host + " tool " + toolIndex);
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
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "prose-only answer is not acceptable"), "forced follow-up prompt");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void ChatMalformedActionResponseForcesRepair()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    "```rnassistant-agent\n{\"steps\":[\n```",
                    AgentBlock(Command("excel.add_sheet", "name", "Report")),
                    "Done.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Create a new sheet named Report.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(3, calls.Count, "llm call count");
                AssertTrue(ContainsMessage(calls[1], "could not recover executable JSON"), "repair prompt");
                AssertEqual(1, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.add_sheet", adapter.Executed[0].ToolId, "executed tool");
            });
        }

        private static void AgentTranscriptCreatesActivityTree()
        {
            var plan = AgentTranscript.CreateAgentPlanActivity(new[] { Command("excel.add_sheet", "name", "Report") });
            AssertEqual("plan", plan.Kind, "plan kind");
            AssertEqual(1, plan.Children.Count, "plan child count");
            AssertEqual("excel.add_sheet", plan.Children[0].ToolId, "plan child tool");
            AssertContains(plan.Children[0].ArgumentsJson, "Report", "plan child args");

            var command = new ToolCommand { ToolId = "excel.make_report" };
            command.Arguments["sheet"] = "Report";
            var result = ToolResult.Ok(
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
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
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
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Done.", result.AssistantText, "assistant text");
                AssertEqual(2, adapter.Executed.Count, "adapter execution count");
                AssertEqual("excel.write_table", adapter.Executed[0].ToolId, "first tool");
                AssertTrue(!adapter.Executed[0].Arguments.ContainsKey("values"), "first command missing values");
                AssertEqual("[[\"Month\",\"Sales\"]]", adapter.Executed[1].Arguments["values"], "retry values");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "No table values provided", "failed result logged");
                AssertContains(resultJson, "executed excel.write_table", "retry result logged");
                AssertTrue(ContainsMessage(session.Messages, "Local skill retry result") || ContainsMessage(session.Messages, "Agent step"), "retry transcript recorded");
            });
        }

        private static void ChatRetrySuccessContinuesToFinalAnswer()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.QueueResult("excel.write_table", ToolResult.Fail("No table values provided."));
                var calls = new List<IReadOnlyList<ChatMessage>>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    calls,
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1")),
                    AgentBlock(Command("excel.write_table", "sheet", "Report", "startAddress", "A1", "values", "[[\"Month\",\"Sales\"]]")),
                    "Finished.");
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Write a report table.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(3, calls.Count, "llm call count");
                AssertEqual("Finished.", result.AssistantText, "assistant text");
                AssertTrue(ContainsMessage(session.Messages, "Finished."), "final assistant message");
            });
        }

        private static void ChatAgentDisabledSkipsToolBlock()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(adapter, executor, null, AgentBlock(Command("word.insert_text", "text", "Hello")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Insert text into the document.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = false, AutoConfirmToolActions = true, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(0, result.ToolResults.Count, "tool result count");
                AssertContains(result.AssistantText, "Agent mode is disabled", "assistant text");
                AssertTrue(ContainsMessage(session.Messages, "Agent mode is disabled"), "disabled message recorded");
            });
        }

        private static void ChatWaitingToolGetsPendingId()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(adapter, executor, null, AgentBlock(Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace a VBA module.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    delegate(ChatSession pendingSession, ToolCommand pendingCommand, ToolResult pendingResult)
                    {
                        AssertEqual(ChatStore.GetSessionId(session), ChatStore.GetSessionId(pendingSession), "pending session id");
                        AssertEqual("word.vba_replace_module", pendingCommand.ToolId, "pending tool id");
                        pendingIds.Add("pending-1");
                        return "pending-1";
                    }).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(1, pendingIds.Count, "pending count");
                var resultJson = JsonConvert.SerializeObject(result.ToolResults);
                AssertContains(resultJson, "waiting_confirmation", "waiting status");
                AssertContains(resultJson, "pending-1", "pending id");
                AssertTrue(session.Messages.Any(m => m != null && m.Activity != null && string.Equals(m.Activity.PendingId, "pending-1", StringComparison.OrdinalIgnoreCase)), "pending activity");
            });
        }

        private static void ChatWaitingToolStopsBatch()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var pendingIds = new List<string>();
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(
                        Command("word.vba_replace_module", "moduleName", "Module1", "code", "Sub Test()\nEnd Sub"),
                        Command("word.insert_text", "text", "Should not run")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "Replace VBA and then insert text.",
                    session,
                    NewContext(adapter),
                    new AppSettings { AgentModeEnabled = true, AutoConfirmToolActions = false, ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null,
                    delegate(ChatSession pendingSession, ToolCommand pendingCommand, ToolResult pendingResult)
                    {
                        pendingIds.Add("pending-" + (pendingIds.Count + 1));
                        return pendingIds[pendingIds.Count - 1];
                    }).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertEqual(1, pendingIds.Count, "pending count");
                AssertEqual(1, result.ToolResults.Count, "tool result count");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "word.vba_replace_module", "first tool logged");
                AssertTrue(JsonConvert.SerializeObject(result.ToolResults).IndexOf("word.insert_text", StringComparison.OrdinalIgnoreCase) < 0, "second tool skipped");
            });
        }

        private static void ChatMaxIterationsReturnsRuntimeSummary()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var service = ChatServiceWithResponses(
                    adapter,
                    executor,
                    null,
                    AgentBlock(Command("excel.list_sheets")),
                    AgentBlock(Command("excel.list_sheets")),
                    AgentBlock(Command("excel.list_sheets")));
                var session = NewSession(adapter);

                var result = service.ExecuteAsync(
                    "List sheets repeatedly.",
                    session,
                    NewContext(adapter),
                    new AppSettings { ContextCharLimit = 8000 },
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertContains(result.AssistantText, "Agent executed", "summary text");
                AssertTrue(result.AssistantText.IndexOf("rnassistant-agent", StringComparison.OrdinalIgnoreCase) < 0, "no raw agent block");
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
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
                    null).GetAwaiter().GetResult();

                AssertEqual(0, adapter.Executed.Count, "adapter execution count");
                AssertContains(JsonConvert.SerializeObject(result.ToolResults), "Auto tool execution is disabled", "auto-run result");
                AssertTrue(ContainsMessage(session.Messages, "Agent plan"), "plan recorded");
                AssertTrue(ContainsMessage(session.Messages, "waiting"), "waiting recorded");
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
                    new List<ToolDefinition>(adapter.GetBuiltInTools()),
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

        private static void ContextNormalizerUsesCoreModelsOnly()
        {
            var normalizer = new ContextNormalizer("Excel", "doc", "Harness.xlsx");
            var session = new ChatSession
            {
                Host = "",
                DocumentKey = "",
                DocumentTitle = "Harness.xlsx",
                Title = "Chat title",
                Context = new DocumentContext { Notes = null }
            };

            var context = normalizer.LoadContext(session);
            AssertEqual("Excel", context.Host, "context host fallback");
            AssertEqual("doc", context.DocumentKey, "context document key fallback");
            AssertEqual("Chat title", context.Title, "context title fallback");
            AssertTrue(context.Notes != null, "notes initialized");

            var note = new ContextNote { Reference = "A1", Text = "abcdef" };
            normalizer.NormalizeContextNote(note, "selection");
            AssertEqual("Excel", note.Host, "note host fallback");
            AssertEqual("selection", note.Kind, "note kind fallback");
            AssertEqual("Harness.xlsx", note.Title, "note title fallback");
            AssertEqual("abcdef", ContextNormalizer.TrimForContext("abcdef", 10), "core trim short");
            AssertEqual("abc\n...[truncated]", ContextNormalizer.TrimForContext("abcdef", 3), "core trim long");
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
                "{\"id\":\"b1\",\"type\":\"runTool\",\"payload\":{\"toolId\":\"excel.add_sheet\",\"arguments\":{\"name\":\"Report\",\"count\":2,\"enabled\":true,\"values\":[[\"A\"]]},\"dryRun\":true}}")
                .GetAwaiter()
                .GetResult();

            var response = JObject.Parse(responseJson);
            AssertTrue(response["ok"].Value<bool>(), "bridge response ok");
            AssertEqual("b1", response["id"].Value<string>(), "bridge response id");
            AssertTrue(response["payload"]["Success"].Value<bool>(), "bridge payload success");
            AssertEqual("excel.add_sheet", controller.LastToolId, "tool id");
            AssertContains(controller.LastArgumentsJson, "Report", "tool args");
            AssertEqual(2, JObject.Parse(controller.LastArgumentsJson)["count"].Value<int>(), "integer tool arg");
            AssertEqual(true, JObject.Parse(controller.LastArgumentsJson)["enabled"].Value<bool>(), "bool tool arg");
            AssertEqual("[[\"A\"]]", JObject.Parse(controller.LastArgumentsJson)["values"].Value<string>(), "nested tool arg");
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
            AssertEqual(2, progressMessages.Count, "send chat event count");
            var chatState = JObject.Parse(progressMessages[1]);
            AssertEqual("chatState", chatState["type"].Value<string>(), "chat state event type");
            AssertEqual("chat-1", chatState["payload"]["activeChatId"].Value<string>(), "chat state active id");
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

        private static void BridgeUsesTypedToolAndSkillPayloads()
        {
            var controller = new AssistantController();
            var bridge = new AssistantWebBridge(controller, null);
            var toolsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b6\",\"type\":\"saveTools\",\"payload\":{\"tools\":[{\"Id\":\"excel.custom\",\"Host\":\"Excel\",\"Executor\":\"pipeline\",\"Enabled\":true}]}}")
                .GetAwaiter()
                .GetResult();
            var skillsResponseJson = bridge.HandleMessageAsync(
                "{\"id\":\"b7\",\"type\":\"saveSkills\",\"payload\":{\"skills\":[{\"Id\":\"common.review\",\"Host\":\"Common\",\"BodyMarkdown\":\"# Review\",\"Enabled\":true}]}}")
                .GetAwaiter()
                .GetResult();

            AssertTrue(JObject.Parse(toolsResponseJson)["ok"].Value<bool>(), "tools bridge response ok");
            AssertTrue(JObject.Parse(skillsResponseJson)["ok"].Value<bool>(), "skills bridge response ok");
            AssertEqual("excel.custom", JArray.Parse(controller.LastToolsJson)[0]["Id"].Value<string>(), "tool id");
            AssertEqual("common.review", JArray.Parse(controller.LastSkillsJson)[0]["Id"].Value<string>(), "skill id");
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

        private static ToolDefinition CustomTool(string host, string id)
        {
            return new ToolDefinition
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

        private static bool HasTool(IEnumerable<ToolDefinition> tools, string id)
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

        private static bool HasSkill(IEnumerable<SkillDefinition> skills, string id)
        {
            foreach (var skill in skills)
            {
                if (skill != null && string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static ToolDefinition FindTool(IEnumerable<ToolDefinition> tools, string id)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
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

        private static List<ToolDefinition> BuildPipelineTools(bool requiresConfirmation)
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
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

        private static List<ToolDefinition> BuildStepPlaceholderPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
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

        private static List<ToolDefinition> BuildThreeStepPipelineTools()
        {
            return new List<ToolDefinition>
            {
                new ToolDefinition
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

        private static ToolCommand Command(string id, params object[] keyValues)
        {
            var command = new ToolCommand { ToolId = id };
            for (var i = 0; i + 1 < (keyValues == null ? 0 : keyValues.Length); i += 2)
            {
                command.Arguments[Convert.ToString(keyValues[i])] = keyValues[i + 1];
            }

            return command;
        }

        private static string AgentBlock(params ToolCommand[] commands)
        {
            return "```rnassistant-agent\n" +
                JsonConvert.SerializeObject(new
                {
                    steps = (commands ?? new ToolCommand[0]).Select(command => new
                    {
                        toolId = command.ToolId,
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
                delegate(AppSettings settings, IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
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
            public readonly List<ToolCommand> Executed = new List<ToolCommand>();
            public string VbaModuleType = "StdModule";
            public readonly List<string> RanMacros = new List<string>();
            public bool FailUnknownSkills { get; set; }
            public string DocumentKeyValue { get; set; }
            public string RuntimeDocumentKeyValue { get; set; }

            private readonly string _hostName;
            private readonly string _documentTitle;
            private readonly string _documentSnapshot;
            private readonly List<ToolDefinition> _builtInTools;
            private readonly Dictionary<string, Queue<ToolResult>> _scriptedResults;
            private readonly Dictionary<string, FakeVbaModule> _vbaModules;

            public string VbaModuleCode
            {
                get { return GetVbaModuleCode("Module1"); }
                set { SetVbaModule("Module1", value, VbaModuleType); }
            }

            public FakeOfficeAdapter()
                : this("Excel", "Harness.xlsx", ExcelBuiltIns(), "Harness document")
            {
            }

            private FakeOfficeAdapter(string hostName, string documentTitle, IEnumerable<ToolDefinition> builtInSkills, string documentSnapshot)
            {
                _hostName = hostName;
                _documentTitle = documentTitle;
                _documentSnapshot = documentSnapshot;
                _builtInTools = new List<ToolDefinition>((builtInSkills ?? new ToolDefinition[0]).Select(CloneTool));
                _scriptedResults = new Dictionary<string, Queue<ToolResult>>(StringComparer.OrdinalIgnoreCase);
                _vbaModules = new Dictionary<string, FakeVbaModule>(StringComparer.OrdinalIgnoreCase);
                DocumentKeyValue = "doc";
                RuntimeDocumentKeyValue = "runtime-doc";
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
            public string DocumentKey { get { return DocumentKeyValue; } }
            public string LegacyDocumentKey { get { return "legacy-doc"; } }
            public string RuntimeDocumentKey { get { return RuntimeDocumentKeyValue; } }
            public string DocumentTitle { get { return _documentTitle; } }

            public string GetDocumentSnapshot(int maxChars)
            {
                return _documentSnapshot;
            }

            public string GetVbaSnapshot(int maxChars)
            {
                return string.Join("\n", _vbaModules.Values.Select(module => module.Name + " (" + module.Type + "): " + module.Code.Length + " chars").ToArray());
            }

            public void PrepareForContextCapture()
            {
            }

            public ContextNote CaptureSelectionContext(string mode, int maxChars)
            {
                return null;
            }

            public IEnumerable<ToolDefinition> GetBuiltInTools()
            {
                return _builtInTools.Select(CloneTool).ToArray();
            }

            public void QueueResult(string toolId, ToolResult result)
            {
                Queue<ToolResult> queue;
                if (!_scriptedResults.TryGetValue(toolId, out queue))
                {
                    queue = new Queue<ToolResult>();
                    _scriptedResults[toolId] = queue;
                }

                queue.Enqueue(result);
            }

            public void SetVbaModule(string moduleName, string code, string type)
            {
                var name = string.IsNullOrWhiteSpace(moduleName) ? "Module1" : moduleName;
                _vbaModules[name] = new FakeVbaModule
                {
                    Name = name,
                    Code = code ?? string.Empty,
                    Type = string.IsNullOrWhiteSpace(type) ? "StdModule" : type
                };
            }

            public string GetVbaModuleCode(string moduleName)
            {
                FakeVbaModule module;
                return _vbaModules.TryGetValue(string.IsNullOrWhiteSpace(moduleName) ? "Module1" : moduleName, out module)
                    ? module.Code
                    : string.Empty;
            }

            public ToolResult ExecuteTool(ToolCommand command)
            {
                Executed.Add(Clone(command));
                ToolResult scripted;
                if (TryDequeueResult(command.ToolId, out scripted))
                {
                    return scripted;
                }

                if ((command.ToolId ?? string.Empty).EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase))
                {
                    var moduleName = Argument(command, "moduleName", "Module1");
                    FakeVbaModule module;
                    if (!_vbaModules.TryGetValue(moduleName, out module))
                    {
                        return ToolResult.Fail("VBA module not found: " + moduleName);
                    }

                    return ToolResult.Ok("read " + command.ToolId, JsonConvert.SerializeObject(new { name = module.Name, code = module.Code, type = module.Type }));
                }

                if ((command.ToolId ?? string.Empty).EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase))
                {
                    SetVbaModule(Argument(command, "moduleName", "Module1"), Argument(command, "code", string.Empty), VbaModuleType);
                    return ToolResult.Ok("replaced " + command.ToolId);
                }

                if ((command.ToolId ?? string.Empty).EndsWith(".insert_vba_module", StringComparison.OrdinalIgnoreCase))
                {
                    SetVbaModule(Argument(command, "moduleName", "Module1"), Argument(command, "code", string.Empty), VbaModuleType);
                    return ToolResult.Ok("inserted " + command.ToolId);
                }

                if ((command.ToolId ?? string.Empty).EndsWith(".run_macro", StringComparison.OrdinalIgnoreCase))
                {
                    RanMacros.Add(Argument(command, "macroName", string.Empty));
                    return ToolResult.Ok("ran " + command.ToolId);
                }

                if (FailUnknownSkills && !IsKnownTool(command.ToolId))
                {
                    return ToolResult.Fail("Unsupported " + HostName + " tool: " + command.ToolId);
                }

                return ToolResult.Ok("executed " + command.ToolId, JsonConvert.SerializeObject(new { host = HostName, toolId = command.ToolId }));
            }

            private static string Argument(ToolCommand command, string name, string fallback)
            {
                object value;
                return command != null && command.Arguments != null && command.Arguments.TryGetValue(name, out value) && value != null
                    ? Convert.ToString(value)
                    : fallback;
            }

            private bool TryDequeueResult(string toolId, out ToolResult result)
            {
                result = null;
                Queue<ToolResult> queue;
                if (!_scriptedResults.TryGetValue(toolId ?? string.Empty, out queue) || queue.Count == 0)
                {
                    return false;
                }

                result = queue.Dequeue();
                return true;
            }

            private bool IsKnownTool(string toolId)
            {
                return _builtInTools.Any(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
            }

            private static IEnumerable<ToolDefinition> ExcelBuiltIns()
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

            private static IEnumerable<ToolDefinition> WordBuiltIns()
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

            private static IEnumerable<ToolDefinition> PowerPointBuiltIns()
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

            private static IEnumerable<ToolDefinition> OutlookBuiltIns()
            {
                return new[]
                {
                    BuiltIn("Outlook", "outlook.read_selection", false, false, true),
                    BuiltIn("Outlook", "outlook.draft_reply", false, true, true),
                    BuiltIn("Outlook", "outlook.collect_folder_mail", false, false, true),
                    BuiltIn("Outlook", "outlook.collect_monthly_summary_data", false, false, true)
                };
            }

            private static ToolDefinition BuiltIn(string host, string id, bool requiresConfirmation, bool mutatesDocument, bool agentCanRun)
            {
                return new ToolDefinition
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

            private static ToolDefinition CloneTool(ToolDefinition skill)
            {
                return new ToolDefinition
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

            private static ToolCommand Clone(ToolCommand command)
            {
                var clone = new ToolCommand { ToolId = command.ToolId, Description = command.Description };
                foreach (var pair in command.Arguments)
                {
                    clone.Arguments[pair.Key] = pair.Value;
                }
                return clone;
            }

            private sealed class FakeVbaModule
            {
                public string Name { get; set; }
                public string Code { get; set; }
                public string Type { get; set; }
            }
        }
    }
}
