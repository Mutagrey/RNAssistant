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

            AssertEqual(2, found.MatchCount, "regex match count");
            AssertEqual("item-12 item-345 decoder-7", replaced.Text, "regex capture replacement");
            AssertEqual(2, replaced.MatchCount, "regex replacement count");

            var literal = new TextPatternOptions();
            var literalReplacement = "cost $& and $$";
            AssertEqual(literalReplacement,
                TextPatternEngine.Replace("alpha", "alpha", literalReplacement, literal, true, 1).Text,
                "literal replacement preserves dollar sequences");
            AssertEqual(literalReplacement,
                TextPatternEngine.PlanReplacements("alpha", "alpha", literalReplacement, literal, true, 1)[0].Text,
                "planned literal replacement preserves dollar sequences");

            var limited = TextPatternEngine.Find("x x x", "x", new TextPatternOptions(), 1, 0);
            AssertEqual(3, limited.MatchCount, "truncated search keeps exact match count");
            AssertEqual(1, limited.Matches.Count, "truncated search limits returned matches");
            AssertTrue(limited.Truncated, "truncated search flag");

            var largeText = new string('a', TextPatternEngine.MaxPreviewChars - 2) +
                "\uD83D\uDE00" + new string('b', 10000);
            var large = TextPatternEngine.Find(largeText, ".+",
                new TextPatternOptions { Mode = "regex" }, 1, 0);
            var preview = large.Matches[0].Preview;
            AssertEqual(largeText.Length, large.Matches[0].Length, "long match keeps exact coordinates");
            AssertTrue(preview.Length <= TextPatternEngine.MaxPreviewChars &&
                preview.EndsWith("…", System.StringComparison.Ordinal),
                "long match preview is bounded and marked as clipped");
            AssertTrue(!char.IsHighSurrogate(preview[preview.Length - 2]),
                "bounded preview does not split a surrogate pair");

            var endAnchor = TextPatternEngine.Find("x", "$",
                new TextPatternOptions { Mode = "regex" }, 1, 0);
            AssertEqual(string.Empty, endAnchor.Matches[0].Preview,
                "end-anchor match permits an empty preview");

            TextPatternException invalidMode = null;
            try
            {
                TextPatternEngine.Find("aaaa", "a+",
                    new TextPatternOptions { Mode = "regx" }, 1, 0);
            }
            catch (TextPatternException error)
            {
                invalidMode = error;
            }
            AssertEqual("invalid_arguments", invalidMode == null ? null : invalidMode.ErrorCode,
                "unknown search mode fails before interpreting the pattern as literal");
        }

        private static void VbaSearchRegexpPatchAndDeleteAreSafe()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.SetVbaModule("Module1", "Option Explicit\nSub Test()\nDim oldValue As Long\noldValue = 1\nEnd Sub", "StdModule");
                adapter.SetVbaModule("ThisWorkbook", "Private Sub Workbook_Open()\nEnd Sub", "DocumentModule");
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };

                var session = NewSession(adapter);
                var listed = ListVbaComponents(executor, session);
                var read = ReadVbaSource(executor, session, "Module1");
                var searched = SearchVbaSource(executor, session, "oldValue");
                AssertTrue(listed.Items.Count >= 2, "VBA resource list succeeds");
                AssertTrue(listed.Items.All(item => item.Metadata.ContainsKey("name")),
                    "VBA resource list returns metadata without source bodies");
                AssertTrue(!listed.Items.Any(item => item.Metadata.Values.Any(value =>
                    value != null && value.IndexOf("Option Explicit", System.StringComparison.Ordinal) >= 0)),
                    "VBA resource list omits source code");
                AssertTrue(!string.IsNullOrWhiteSpace(read.ContentSha256), "VBA resource read returns code hash");
                AssertTrue(searched.Matches.Count >= 1 && searched.Matches.All(match => match.Title == "Module1"),
                    "VBA literal search returns the matching module");

                var limitedSearch = SearchVbaSource(executor, session, "Sub", 1);
                AssertEqual(1, limitedSearch.Matches.Count, "VBA resource search obeys its result bound");

                var patch = new JArray(new JObject
                {
                    ["find"] = "Dim oldValue As Long\noldValue = 1",
                    ["text"] = "Dim newValue As Long\nnewValue = 1"
                });
                var patched = executor.ExecuteManual(Command("common.vba_apply_patch", "moduleName", "Module1", "patch", patch), tools, settings, false, false);
                AssertTrue(patched.Success, "VBA exact patch succeeds after resource discovery");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "newValue", "VBA exact patch updates discovered source");

                var blockedDelete = executor.ExecuteManual(Command("common.vba_delete_module", "moduleName", "ThisWorkbook"), tools, settings, false, false);
                AssertEqual("vba_component_type_read_only", blockedDelete.ErrorCode, "document module delete blocked");

                var deleted = executor.ExecuteManual(Command("common.vba_delete_module", "moduleName", "Module1"), tools, settings, false, false);
                AssertTrue(deleted.Success, "standard module delete succeeds");
            });
        }
    }
}
