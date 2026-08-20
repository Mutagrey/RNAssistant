using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void TextPatternEngineSupportsRegexpAndGroups()
        {
            var options = new TextPatternOptions { Mode = "regex", MatchCase = false, WholeWord = true };
            var found = TextPatternEngine.Find("Code-12 code-345 decoder-7", "code-(\\d+)", options, 10, 5);
            var replaced = TextPatternEngine.Replace("Code-12 code-345 decoder-7", "code-(\\d+)", "item-$1", options, true, 10);

            AssertEqual(2, found.MatchCount, "regexp match count");
            AssertEqual("item-12 item-345 decoder-7", replaced.Text, "regexp capture replacement");
            AssertEqual(2, replaced.MatchCount, "regexp replacement count");
        }

        private static void PipelineExecutionValidatesArgumentsAndNestedBudget()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildThreeStepPipelineTools()[0];
                var invalid = executor.Execute(Command(tool.Id, "sheet", "Report", "unexpected", true), new[] { tool }, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertEqual("invalid_arguments", invalid.ErrorCode, "unexpected custom argument rejected");

                var limited = executor.Execute(Command(tool.Id, "sheet", "Report"), new[] { tool }, new AppSettings { AutoConfirmToolActions = true, MaxAgentToolSteps = 2 }, false, false);
                AssertTrue(!limited.Success, "nested budget stops pipeline");
                AssertContains(limited.DataJson, "tool_step_limit_exceeded", "nested budget error is recorded");
                AssertEqual(1, adapter.Executed.Count, "only one nested adapter step executes within budget");
            });
        }

        private static void VbaSearchRegexpPatchAndDeleteAreSafe()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Option Explicit\nSub Test()\nDim oldValue As Long\noldValue = 1\nEnd Sub", "StdModule");
                adapter.SetVbaModule("ThisWorkbook", "Private Sub Workbook_Open()\nEnd Sub", "DocumentModule");
                var tools = adapter.GetBuiltInTools().ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };

                var listed = executor.Execute(Command("excel.vba_list_modules"), tools, settings, false, false);
                var read = executor.Execute(Command("excel.vba_read_module", "moduleName", "Module1"), tools, settings, false, false);
                var searched = executor.Execute(Command("excel.vba_search_code", "query", "old(Value)", "mode", "regex", "matchCase", true), tools, settings, false, false);
                AssertTrue(listed.Success, "VBA module list succeeds");
                AssertTrue(!listed.DataJson.Contains("Option Explicit"), "VBA module list omits source code");
                AssertContains(read.DataJson, "codeSha256", "VBA module read returns code hash");
                AssertContains(searched.DataJson, "Module1", "VBA regexp search returns module");

                var patch = "[{\"op\":\"regexReplace\",\"pattern\":\"old(Value)\",\"text\":\"new$1\",\"replaceAll\":true}]";
                var patched = executor.Execute(Command("excel.vba_apply_patch", "moduleName", "Module1", "patch", patch), tools, settings, false, false);
                AssertTrue(patched.Success, "VBA regexp patch succeeds");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "newValue", "VBA regexp patch applies captures");

                var blockedDelete = executor.Execute(Command("excel.vba_delete_module", "moduleName", "ThisWorkbook", "expectedCodeSha256", VbaToolManifestParser.CodeSha256(adapter.GetVbaModuleCode("ThisWorkbook"))), tools, settings, false, false);
                AssertEqual("vba_component_type_read_only", blockedDelete.ErrorCode, "document module delete blocked");

                var currentHash = VbaToolManifestParser.CodeSha256(adapter.GetVbaModuleCode("Module1"));
                var deleted = executor.Execute(Command("excel.vba_delete_module", "moduleName", "Module1", "expectedCodeSha256", currentHash), tools, settings, false, false);
                AssertTrue(deleted.Success, "standard module delete succeeds");
                AssertEqual("vba_module_not_found", deleted.Verification.ExpectedErrorCode, "delete verifies absence");
            });
        }
    }
}
