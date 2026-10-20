using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceOfficeTargetStaysPinned()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), paths: paths);
                var office = new WorkspaceOfficeCapabilityService(adapter, executor, paths);
                {
                    var target = office.CaptureCurrentTarget();
                    AssertTrue(target != null && !string.IsNullOrWhiteSpace(target.DocumentAuthorityId),
                        "workspace run captures exact Office authority");
                    var session = new ChatSession { Id = "workspace-office-test", WorkspaceId = "ws-test",
                        Messages = new List<ChatMessage>() };
                    var initial = office.ForTarget(target, session, new AppSettings(), "run-one");
                    AssertTrue(initial.Available && initial.Tools.Any(item => item.Entry.Id == "excel.inspect") &&
                        initial.Tools.Any(item => item.Entry.Id == "excel.write_range") &&
                        initial.Tools.All(item => item.Entry.Id != "excel.create_chat_chart"),
                        "workspace run receives document tools without chat-owned chart artifacts");
                    var registry = RegisterOfficeContributions(initial);
                    var runtime = new ToolRuntime(registry, "agent", true, true,
                        mutationObserver: initial.MutationObserver);
                    var liveWriteCall = new ToolCall("live-write", "excel.write_range",
                        "{\"kind\":\"value\",\"sheet\":\"Data\",\"address\":\"C1\",\"value\":\"42\"}");
                    var liveWrite = runtime.ExecuteAsync(new ToolExecutionContext(liveWriteCall, runtime.Describe(liveWriteCall),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, liveWrite.Outcome,
                        "workspace document mutation uses the Office handler and authority observer");
                    var liveCall = new ToolCall("live-resource", "common.resources_read",
                        "{\"target\":\"Excel range: Data!A1:B2\",\"representation\":\"text\"}");
                    var liveExecution = new ToolExecutionContext(liveCall,
                        new ToolPolicySnapshot(liveCall.Name, "test", ResourceReadToolHandler.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var live = initial.ResourceRead.ExecuteAsync(new ToolHandlerContext(liveExecution,
                        new Dictionary<string, object> { ["target"] = "Excel range: Data!A1:B2",
                            ["representation"] = "text" }), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Ok, live.Result.Status,
                        "workspace can read the pinned Office document");

                    adapter.DocumentKeyValue = "another-document";
                    adapter.RuntimeDocumentKeyValue = "another-runtime";
                    var changed = office.ForTarget(target, session, new AppSettings(), "run-one");
                    AssertTrue(!changed.Available, "switching documents does not retarget the run");
                    AssertTrue(initial.Tools.Select(item => item.Entry.Id).OrderBy(id => id).SequenceEqual(
                        changed.Tools.Select(item => item.Entry.Id).OrderBy(id => id)),
                        "document switch preserves the accepted Office catalog");
                    var inspect = changed.Tools.Single(item => item.Entry.Id == "excel.inspect");
                    var call = new ToolCall("pinned-read", inspect.Entry.Id, "{\"kind\":\"workbook\"}");
                    var execution = new ToolExecutionContext(call,
                        new ToolPolicySnapshot(call.Name, "test", inspect.Entry.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var read = inspect.Handler.ExecuteAsync(new ToolHandlerContext(execution,
                        new Dictionary<string, object> { ["kind"] = "workbook" }), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Error, read.Result.Status,
                        "Office read refuses the replacement document");
                    AssertTrue(read.Result.DataJson.Contains("active_document_changed"),
                        "Office read reports exact target mismatch");
                    var resourceCall = new ToolCall("pinned-resource", "common.resources_read",
                        "{\"target\":\"Excel range: Data!A1:B2\",\"representation\":\"text\"}");
                    var resourceExecution = new ToolExecutionContext(resourceCall,
                        new ToolPolicySnapshot(resourceCall.Name, "test", ResourceReadToolHandler.Policy),
                        "run-one", "run-one", "step", DateTime.UtcNow, false, 1);
                    var resource = changed.ResourceRead.ExecuteAsync(new ToolHandlerContext(resourceExecution,
                        new Dictionary<string, object> { ["target"] = "Excel range: Data!A1:B2",
                            ["representation"] = "text" }), CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolResultStatus.Error, resource.Result.Status,
                        "semantic Office read refuses the replacement document");

                    var foreignAdapter = FakeOfficeAdapter.ForHost("Word");
                    var foreignExecutor = new OfficeToolExecutor(foreignAdapter, new VbaJournalStore(paths),
                        new SkillStore(paths), new ToolStore(paths), paths: paths);
                    var foreign = new WorkspaceOfficeCapabilityService(foreignAdapter, foreignExecutor, paths);
                    {
                        var switched = foreign.ForTarget(target, session, new AppSettings(), "run-one");
                        AssertTrue(!switched.Available && switched.Tools.Any(item => item.Entry.Id == "excel.inspect"),
                            "host switch preserves the pending run's Office catalog");
                        RegisterOfficeContributions(switched);
                        var refused = switched.Tools.Single(item => item.Entry.Id == "excel.inspect").Handler
                            .ExecuteAsync(new ToolHandlerContext(execution,
                                new Dictionary<string, object> { ["kind"] = "workbook" }), CancellationToken.None)
                            .GetAwaiter().GetResult();
                        AssertEqual(ToolResultStatus.Error, refused.Result.Status,
                            "host switch refuses an Office read before dispatch");
                        var write = switched.Tools.Single(item => item.Entry.Id == "excel.write_range");
                        var writeCall = new ToolCall("pinned-write", write.Entry.Id, "{}");
                        var writeExecution = new ToolExecutionContext(writeCall,
                            new ToolPolicySnapshot(writeCall.Name, "test", write.Entry.Policy),
                            "run-one", "run-one", "step", DateTime.UtcNow, true, 1);
                        var rejected = false;
                        try { switched.MutationObserver.Prepare(writeExecution, new Dictionary<string, object>()); }
                        catch (ToolMutationPreparationException error)
                        { rejected = error.Code == "active_document_changed"; }
                        AssertTrue(rejected, "confirmed write cannot prepare against another host");
                    }
                }
            });
        }

        private static ToolHandlerRegistry RegisterOfficeContributions(RNAssistant.Runtime.Contracts.WorkspaceOfficeCapabilitySet office)
        {
            var registry = new ToolHandlerRegistry();
            foreach (var contribution in office.Tools)
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(contribution.Entry.Id,
                    contribution.Entry.Description, contribution.Entry.ArgumentSchemaJson),
                    contribution.Entry.Policy, contribution.Entry.Binding), contribution.Handler);
            return registry;
        }
    }
}
