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

        private static void VbaBackupFailureBlocksReplacement()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Original()\nEnd Sub";
                adapter.QueueResult("excel.vba_read_module", ToolResult.Ok("malformed read", "{}"));
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
                var command = Command("excel.vba_replace_module", "moduleName", "Module1", "code", "Sub Changed()\nEnd Sub", "createIfMissing", false);

                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "replacement blocked");
                AssertEqual("vba_backup_failed", result.ErrorCode, "backup failure code");
                AssertEqual(false, result.Retryable, "backup failure retryable");
                AssertEqual("Sub Original()\nEnd Sub", adapter.VbaModuleCode, "module unchanged");
                AssertEqual(1, adapter.Executed.Count, "only backup read executed");

                adapter.Executed.Clear();
                var create = Command("excel.vba_replace_module", "moduleName", "NewModule", "code", "Sub NewMacro()\nEnd Sub", "createIfMissing", true);
                var created = executor.Execute(create, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(created.Success, "missing module can be created");
                AssertContains(adapter.GetVbaModuleCode("NewModule"), "NewMacro", "new module code");
            });
        }

        private static void VbaPatchRejectsLineOverrun()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var command = Command(
                    executor.VbaToolId("vba_apply_patch"),
                    "moduleName", "Module1",
                    "patch", "[{\"op\":\"replaceLines\",\"startLine\":2,\"deleteCount\":5,\"text\":\"End Sub\"}]");

                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "line overrun rejected");
                AssertContains(result.Message, "past the end", "line overrun message");
                AssertEqual("Sub Main()\nEnd Sub", adapter.VbaModuleCode, "line overrun leaves module unchanged");
            });
        }

        private static void VbaCustomMacroFailureCleansSession()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var code =
                    "Option Explicit\n" +
                    "' <RNAssistantTool>\n" +
                    "' {\"protocolVersion\":1,\"id\":\"excel.custom_vba\",\"name\":\"Custom VBA\",\"description\":\"Test tool\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"Main\",\"components\":[\"RNA_CustomVba\"],\"argumentOrder\":[\"value\"],\"parameters\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                    "' </RNAssistantTool>\n" +
                    "Public Function Main(ByVal value As String) As String\n" +
                    "    Main = value\n" +
                    "End Function";
                var tool = new ToolDefinition
                {
                    Id = "excel.custom_vba",
                    Host = "Excel",
                    Name = "Custom VBA",
                    Executor = "vba",
                    Code = code,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}",
                    Enabled = true,
                    BuiltIn = false,
                    MutatesDocument = true,
                    RequiresConfirmation = true,
                    RiskLevel = 3
                };
                adapter.QueueResult("excel.run_macro", ToolResult.Fail("macro failed", null, "macro_failed", true));
                var command = Command(tool.Id, "value", "test");
                var tools = adapter.GetBuiltInTools().Concat(new[] { tool }).ToList();

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "custom macro result");
                AssertEqual("failed", result.Status, "custom macro failure status");
                AssertEqual(true, result.Retryable, "custom macro retryable");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_CustomVba"), "temporary module cleaned after failure");
                AssertContains(result.DataJson, "sessionInstalled", "session lifecycle recorded");
            });
        }

        private static void VbaFailedModuleWriteRestoresCode()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub Original()\nEnd Sub");
            component.CodeModule.FailNextAdd = true;

            try
            {
                VbaProjectSupport.ReplaceModule(document, "Module1", "Sub Changed()\nEnd Sub", false);
                throw new InvalidOperationException("failed VBA replacement was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "original code was restored", "atomic replacement diagnostic");
            }

            AssertEqual("Sub Original()\nEnd Sub", component.CodeModule.Code, "original code restored");

            var newDocument = new FakeVbaDocumentObject();
            newDocument.VBProject.VBComponents.FailNextAddedModuleWrite = true;
            try
            {
                VbaProjectSupport.ReplaceModule(newDocument, "NewModule", "Sub Main()\nEnd Sub", true);
                throw new InvalidOperationException("failed new VBA module was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "incomplete module was removed", "new module cleanup diagnostic");
            }
            AssertEqual(0, newDocument.VBProject.VBComponents.Count, "incomplete module removed");
        }

        private static void VbaRestoreAppliesBackup()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Current()\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
                var backup = backupStore.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Restored()\nEnd Sub");
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = Command(executor.VbaToolId("vba_restore_backup"), "backupId", backup.BackupId, "moduleName", "Module1");
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "restore result");
                AssertContains(adapter.VbaModuleCode, "Restored", "restored module code");
                AssertEqual(2, backupStore.List("Excel", "doc").Count, "restore preserves current version as backup");
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

            var estimatedJson = JObject.FromObject(ContextUsageEstimator.FromPrompt(
                new[] { new ChatMessage { Role = "user", Content = "hello" } },
                settings,
                null,
                new LlmRequestOptions
                {
                    ResponseFormat = LlmResponseFormats.JsonObject
                }));
            AssertTrue(estimatedJson["usedTokens"].Value<int>() > 0, "json response mode counts toward estimated request usage");

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
            AssertEqual(5012, sessionUsage["usedTokens"].Value<int>(), "session used tokens");
            AssertEqual(1, sessionUsage["messageCount"].Value<int>(), "session message count");
            AssertTrue(!sessionUsage["actual"].Value<bool>(), "session actual");
        }
    }
}
