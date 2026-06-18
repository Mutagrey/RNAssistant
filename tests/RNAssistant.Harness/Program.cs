using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    internal static class Program
    {
        private sealed class HarnessTest
        {
            public string Name { get; set; }
            public Action Run { get; set; }
        }

        public static int Main(string[] args)
        {
            var tests = new List<HarnessTest>
            {
                new HarnessTest { Name = "parser: fenced agent steps", Run = ParsesFencedAgentSteps },
                new HarnessTest { Name = "parser: bare json array", Run = ParsesBareJsonArray },
                new HarnessTest { Name = "parser: native tool_calls", Run = ParsesNativeToolCalls },
                new HarnessTest { Name = "parser: bad json skipped", Run = SkipsBadJson },
                new HarnessTest { Name = "storage: chat roundtrip", Run = CreatesAndListsChatsInTempRoot },
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles },
                new HarnessTest { Name = "pipeline: dry-run resolves placeholders", Run = PipelineDryRunResolvesPlaceholders },
                new HarnessTest { Name = "pipeline: executes fake adapter steps", Run = PipelineExecutesFakeAdapterSteps },
                new HarnessTest { Name = "pipeline: custom tool needs confirmation", Run = CustomPipelineNeedsConfirmation },
                new HarnessTest { Name = "pipeline: agent mode gates built-in mutation", Run = AgentModeGatesBuiltInMutation },
                new HarnessTest { Name = "tools: catalog merges visible tools", Run = ToolCatalogMergesVisibleTools },
                new HarnessTest { Name = "prompt: trims oldest history", Run = PromptBuilderTrimsOldestHistory },
                new HarnessTest { Name = "prompt: usage estimator counts context", Run = ContextUsageEstimatorCountsPromptAndSession },
                new HarnessTest { Name = "chat: completion service records prose", Run = ChatCompletionServiceRecordsProseResponse },
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
                session.Messages.Add(new ChatMessage { Role = "user", Content = "hello" });
                store.Save(session);

                var loaded = store.Load("Word", "doc-key", ChatStore.GetSessionId(session));
                AssertTrue(loaded != null, "loaded session");
                AssertEqual("First", loaded.Title, "title");
                AssertEqual(1, loaded.Messages.Count, "message count");
                AssertEqual("hello", loaded.Messages[0].Content, "message content");

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
            AssertTrue(response["payload"]["ran"].Value<bool>(), "bridge payload ran");
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
            AssertEqual("b2", JObject.Parse(progressMessages[0])["id"].Value<string>(), "progress id");
            AssertEqual("thinking", JObject.Parse(progressMessages[0])["payload"]["phase"].Value<string>(), "progress phase");
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
            AssertContains(controller.LastSettingsJson, "gpt-test", "settings json");
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

        private static void WithTempExecutor(Action<OfficeToolExecutor, FakeOfficeAdapter> action)
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
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

            public string HostName { get { return "Excel"; } }
            public string DocumentKey { get { return "doc"; } }
            public string LegacyDocumentKey { get { return "legacy-doc"; } }
            public string RuntimeDocumentKey { get { return "runtime-doc"; } }
            public string DocumentTitle { get { return "Harness.xlsx"; } }

            public string GetDocumentSnapshot(int maxChars)
            {
                return "Harness document";
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
                return new[]
                {
                    BuiltIn("excel.add_sheet", false),
                    BuiltIn("excel.write_table", true)
                };
            }

            public SkillResult ExecuteSkill(SkillCommand command)
            {
                Executed.Add(Clone(command));
                return SkillResult.Ok("executed " + command.SkillId);
            }

            private static SkillDefinition BuiltIn(string id, bool requiresConfirmation)
            {
                return new SkillDefinition
                {
                    Id = id,
                    Host = "Excel",
                    Name = id,
                    Enabled = true,
                    BuiltIn = true,
                    RequiresConfirmation = requiresConfirmation
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
