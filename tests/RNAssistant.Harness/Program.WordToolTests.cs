using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WordToolsUseExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var tools = adapter.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session,
                        tools.Where(tool => WordToolIds.Owns(tool.Id)),
                        new AppSettings(), "agent", false);
                    var ids = new[]
                    {
                        WordToolIds.ReadText,
                        WordToolIds.FindText,
                        WordToolIds.Inspect,
                        WordToolIds.WriteText,
                        WordToolIds.ReplaceText,
                        WordToolIds.FormatText,
                        WordToolIds.AddTable,
                        WordToolIds.InsertPageBreak,
                        WordToolIds.AddComment
                    };
                    foreach (var id in ids)
                    {
                        var call = new ToolCall(
                            "word-policy-" + id, id, WordArguments(id));
                        var policy = runtime.Describe(call);
                        AssertTrue(policy != null,
                            "Word family has one exact native registration: " + id);
                        AssertEqual(
                            WordToolIds.IsMutation(id),
                            policy.MayHaveSideEffects,
                            "Word effect policy is source-owned: " + id);
                        if (WordToolIds.IsMutation(id))
                            AssertEqual(
                                ToolVerification.Tool,
                                policy.Policy.Verification,
                                "Word mutation requires tool verification: " + id);
                    }

                    var readCall = new ToolCall(
                        "word-read", WordToolIds.ReadText,
                        "{\"source\":\"document\",\"maxChars\":24}");
                    var read = ExecuteNative(
                        runtime, readCall, runtime.Describe(readCall));
                    AssertEqual(ToolExecutionOutcome.Ok, read.Outcome,
                        "Word read uses the typed backend");
                    AssertTrue(((string)JObject.Parse(read.Result.DataJson)["text"])
                        .StartsWith("Quarterly revenue", StringComparison.Ordinal),
                        "Word read keeps the existing text result shape");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        WordToolIds.Owns(command.ToolId)),
                        "Word public ids never reach generic host dispatch");

                    var htmlReads = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordReadTextOperation);
                    var bound = executor.Execute(Command(
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        "dataName", "word_text",
                        "sourceTool", WordToolIds.ReadText,
                        "sourceArguments", new JObject
                        {
                            ["source"] = "document",
                            ["maxChars"] = 12000
                        }), tools, new AppSettings(), false, false, session);
                    AssertTrue(bound.Success,
                        "Word HTML binding shares the typed read route");
                    AssertEqual(htmlReads + 1,
                        adapter.WordBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.WordReadTextOperation),
                        "Word HTML binding enters the direct backend once");
                    AssertEqual(0, adapter.Executed.Count(command =>
                        command.ToolId == WordToolIds.ReadText),
                        "Word HTML binding never enters generic host dispatch");

                    var writes = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation);
                    var dryRun = executor.Execute(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", "dry"),
                        tools, new AppSettings(), true, true, session);
                    AssertTrue(dryRun.Success,
                        "Word native mutation supports non-mutating dry-run");
                    AssertEqual(writes, adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation),
                        "Word dry-run never enters its backend mutation");
                    AssertTrue(runtime.Describe(new ToolCall(
                        "word-case", "WORD.WRITE_TEXT", "{}")) == null,
                        "Word native ownership has no case alias");
                });
        }

        private static void WordToolsPreserveFamilySemantics()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = WordRuntime(executor, adapter);
                    var findCall = new ToolCall(
                        "word-find", WordToolIds.FindText,
                        "{\"query\":\"revenue\",\"scope\":\"main\"}");
                    var found = ExecuteNative(
                        runtime, findCall, runtime.Describe(findCall));
                    AssertEqual(1,
                        (int)JObject.Parse(found.Result.DataJson)["matchCount"],
                        "Word find preserves literal scope matching");

                    var replaceCall = new ToolCall(
                        "word-replace", WordToolIds.ReplaceText,
                        "{\"find\":\"revenue\",\"replace\":\"sales\",\"scope\":\"main\"}");
                    var replaced = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolExecutionOutcome.Ok, replaced.Outcome,
                        "Word replacement succeeds through typed service");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        replaced.Evidence.Effect,
                        "Word replacement exposes verified change evidence");
                    AssertTrue(adapter.WordText.IndexOf(
                        "sales", StringComparison.Ordinal) >= 0,
                        "Word replacement updates exact text");

                    var noChange = ExecuteNativeConfirmed(
                        runtime, replaceCall, runtime.Describe(replaceCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        noChange.Evidence.Effect,
                        "a replacement with no remaining match is verified no-change");

                    var formatCall = new ToolCall(
                        "word-format", WordToolIds.FormatText,
                        "{\"kind\":\"font\",\"bold\":true,\"fontSize\":12}");
                    var formatted = ExecuteNativeConfirmed(
                        runtime, formatCall, runtime.Describe(formatCall));
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        formatted.Evidence.Effect,
                        "Word font formatting is verified");
                    var sameFormat = ExecuteNativeConfirmed(
                        runtime, formatCall, runtime.Describe(formatCall));
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        sameFormat.Evidence.Effect,
                        "matching Word formatting skips dispatch");

                    var tableCall = new ToolCall(
                        "word-table", WordToolIds.AddTable,
                        "{\"values\":[[\"A\",\"B\"],[\"1\",\"2\"]],\"location\":\"end\"}");
                    var table = ExecuteNativeConfirmed(
                        runtime, tableCall, runtime.Describe(tableCall));
                    var tableData = JObject.Parse(table.Result.DataJson);
                    AssertTrue(table.Outcome == ToolExecutionOutcome.Ok &&
                        (int)tableData["rows"] == 2 &&
                        (int)tableData["columns"] == 2,
                        "Word table dimensions are inferred from native values");

                    var commentCall = new ToolCall(
                        "word-comment", WordToolIds.AddComment,
                        "{\"text\":\"Review this\"}");
                    var comment = ExecuteNativeConfirmed(
                        runtime, commentCall, runtime.Describe(commentCall));
                    AssertTrue(comment.Outcome == ToolExecutionOutcome.Ok &&
                        adapter.WordCommentCount == 1,
                        "Word comment mutation keeps its public behavior");

                    var breakCall = new ToolCall(
                        "word-break", WordToolIds.InsertPageBreak, "{}");
                    var pageBreak = ExecuteNativeConfirmed(
                        runtime, breakCall, runtime.Describe(breakCall));
                    AssertTrue(pageBreak.Outcome == ToolExecutionOutcome.Ok &&
                        adapter.WordText.EndsWith("\f", StringComparison.Ordinal),
                        "Word page break is a verified direct mutation");

                    var inspectCall = new ToolCall(
                        "word-inspect", WordToolIds.Inspect,
                        "{\"kind\":\"stats\"}");
                    var inspected = ExecuteNative(
                        runtime, inspectCall, runtime.Describe(inspectCall));
                    AssertEqual(1,
                        (int)JObject.Parse(inspected.Result.DataJson)["tables"],
                        "Word inspect reads the same typed backend state");
                });
        }

        private static void WordToolsClassifyDispatchFaults()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var runtime = WordRuntime(executor, adapter);
                    adapter.WordThrowAfterMutation = true;
                    var call = new ToolCall(
                        "word-fault", WordToolIds.WriteText,
                        "{\"mode\":\"insert\",\"text\":\" dispatched\"}");
                    var result = ExecuteNativeConfirmed(
                        runtime, call, runtime.Describe(call));
                    AssertEqual(ToolExecutionOutcome.Unknown, result.Outcome,
                        "Word failure after dispatch is unknown");
                    AssertEqual(ToolEffectEvidence.Unknown,
                        result.Evidence.Effect,
                        "Word post-dispatch fault never fabricates verification");
                    AssertTrue(adapter.WordText.EndsWith(
                        " dispatched", StringComparison.Ordinal),
                        "fault fixture proves the Word mutation happened");

                    var calls = adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordTableOperation);
                    var oversized = new ToolCall(
                        "word-large-table", WordToolIds.AddTable,
                        "{\"rows\":101,\"columns\":100}");
                    var rejected = ExecuteNativeConfirmed(
                        runtime, oversized, runtime.Describe(oversized));
                    AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome,
                        "oversized Word table is rejected before dispatch");
                    AssertEqual(calls, adapter.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordTableOperation),
                        "oversized Word table never reaches COM backend");
                });
        }

        private static void WordToolsUseBoundDocumentScope()
        {
            WithTempPaths(paths =>
            {
                using (var dispatcher = new OfficeStaDispatcher())
                {
                    var document = new BoundTestDocument
                    {
                        StableId = "bound-word-document",
                        IsAlive = true
                    };
                    var sessionPort = new BoundTestOfficeSession(
                        dispatcher, document, "bound-word-runtime",
                        new object(), "Word");
                    var inner = FakeOfficeAdapter.ForHost("Word");
                    var host = new BoundTestOfficeAdapter(sessionPort, inner);
                    var ownerSta = false;
                    host.BeforeRead = operation =>
                    {
                        if (operation == FakeOfficeAdapter.WordWriteOperation)
                            ownerSta = dispatcher.CheckAccess;
                    };
                    var executor = new OfficeToolExecutor(
                        host, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths),
                        paths: paths);
                    var chat = new ChatSession
                    {
                        Host = "Word",
                        DocumentKey = "bound-word-document",
                        DocumentTitle = "Bound.docx"
                    };
                    var tools = host.GetBuiltInTools()
                        .Concat(executor.GetControllerTools()).ToList();
                    var result = executor.Execute(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", " bound"),
                        tools, new AppSettings(), false, true, chat);
                    AssertTrue(result.Success && ownerSta,
                        "Word mutation stays on the bound document owner STA");

                    var dispatched = inner.WordBackendCalls.Count(operation =>
                        operation == FakeOfficeAdapter.WordWriteOperation);
                    dispatcher.Invoke(() => document.IsAlive = false);
                    var closed = executor.Execute(Command(
                        WordToolIds.WriteText,
                        "mode", "insert", "text", " stale"),
                        tools, new AppSettings(), false, true, chat);
                    AssertEqual("active_document_changed", closed.ErrorCode,
                        "closed bound Word document is rejected before mutation");
                    AssertEqual(dispatched,
                        inner.WordBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.WordWriteOperation),
                        "closed Word document never reaches direct backend");
                }
            });
        }

        private static NativeToolRuntimeAdapter WordRuntime(
            OfficeToolExecutor executor, FakeOfficeAdapter adapter)
        {
            return executor.CreateNativeRuntime(
                NewSession(adapter),
                adapter.GetBuiltInTools().Where(tool =>
                    WordToolIds.Owns(tool.Id)),
                new AppSettings(), "agent", false);
        }

        private static string WordArguments(string toolId)
        {
            if (toolId == WordToolIds.FindText)
                return "{\"query\":\"revenue\"}";
            if (toolId == WordToolIds.Inspect)
                return "{\"kind\":\"stats\"}";
            if (toolId == WordToolIds.WriteText)
                return "{\"mode\":\"insert\",\"text\":\"x\"}";
            if (toolId == WordToolIds.ReplaceText)
                return "{\"find\":\"revenue\"}";
            if (toolId == WordToolIds.FormatText)
                return "{\"kind\":\"font\",\"bold\":true}";
            if (toolId == WordToolIds.AddComment)
                return "{\"text\":\"x\"}";
            return "{}";
        }
    }
}
