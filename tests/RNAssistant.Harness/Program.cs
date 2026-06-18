using System;
using System.Collections.Generic;
using System.IO;
using RNAssistant.Core.Models;
using RNAssistant.Core.Skills;
using RNAssistant.Core.Storage;

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
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles }
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
    }
}
