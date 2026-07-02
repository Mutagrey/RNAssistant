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

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = false }, false, false);
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

        private static void ContextUsageEstimatorCountsPromptAndSession()
        {
            var settings = new AppSettings { ContextWindowOverrideTokens = 8000 };
            var promptUsage = JObject.FromObject(ContextUsageEstimator.FromPrompt(new[]
            {
                new ChatMessage { Role = "system", Content = "abc" },
                new ChatMessage { Role = "user", Content = "defg" }
            }, settings, 12));
            AssertEqual(7, promptUsage["usedChars"].Value<int>(), "prompt used chars");
            AssertEqual(12, promptUsage["usedTokens"].Value<int>(), "prompt used tokens");
            AssertEqual(4928, promptUsage["limitTokens"].Value<int>(), "prompt input token budget");
            AssertEqual(2, promptUsage["messageCount"].Value<int>(), "prompt message count");
            AssertTrue(promptUsage["actual"].Value<bool>(), "prompt actual");

            var session = new ChatSession();
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "hello",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment { Kind = "image", ExtractedCharCount = 10000 }
                }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "internal activity",
                Activity = new ChatActivity { Kind = "tool" }
            });
            session.Context.Notes.Add(new ContextNote { Text = "selection!" });
            var sessionUsage = JObject.FromObject(ContextUsageEstimator.FromSession(session, settings));
            AssertEqual(10015, sessionUsage["usedChars"].Value<int>(), "session used chars");
            AssertEqual(9106, sessionUsage["usedTokens"].Value<int>(), "session used tokens");
            AssertEqual(1, sessionUsage["messageCount"].Value<int>(), "session message count");
            AssertTrue(!sessionUsage["actual"].Value<bool>(), "session actual");
        }
    }
}
