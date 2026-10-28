using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using ToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void UniversalHunksUseOriginalSnapshot()
        {
            var source = "α😀 one\r\nrepeat\r\nrepeat\r\nend";
            var hunks = new[] {
                new TextEditHunk { OldText = "one", NewText = "expanded\nline" },
                new TextEditHunk { OldText = "repeat", NewText = "second", StartLine = 3, StartColumn = 1 },
                new TextEditHunk { OldText = "", NewText = "!", Boundary = "end" } };
            AssertEqual("α😀 expanded\nline\r\nrepeat\r\nsecond\r\nend!", TextEditPlan.Patch(source, hunks).Text, "all coordinates refer to original source");
            AssertEqual("patch_ambiguous", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch(source,
                new[] { new TextEditHunk { OldText = "repeat", NewText = "bad" } })).ErrorCode, "repetition cannot choose an arbitrary match");
            AssertEqual("patch_overlap", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("abc", new[] {
                new TextEditHunk { OldText = "ab", NewText = "x" }, new TextEditHunk { OldText = "bc", NewText = "y" } })).ErrorCode, "overlap rejects entire plan");
            AssertEqual("$1 $1", TextEditPlan.Replace("aa aa", "aa", "$1", "literal", true, 2).Text, "literal replacement preserves dollars");
            AssertEqual("x1\r\nx2", TextEditPlan.Replace("a1\r\na2", "a([0-9])", "x$1", "regex", true, 2).Text, "regex groups preserve line endings");
            AssertEqual("\uFEFFheader\nbody", TextEditPlan.Patch("\uFEFFbody", new[] {
                new TextEditHunk { OldText = "", NewText = "header\n", Boundary = "start" } }).Text, "start insertion preserves encoding signature");
            AssertEqual("replacement_count_conflict", RuntimeThrows<TextPatternException>(() => TextEditPlan.Replace("aa", "a", "b", "literal", true, 1)).ErrorCode, "count checked before writing");
            var physical = TextLineEdits.Plan("a\nold\nstable\nold2\nz", "a\nnew\nstable\nnew2\nz");
            AssertEqual(2, physical.Count, "unchanged middle lines are preserved");
            AssertEqual(4, physical[0].StartLine, "physical writes start at the bottom");
        }
        private static void UniversalFileHunksStageOnce()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "universal"));
                var files = new WorkspaceFileService(paths);
                var source = "\uFEFFα😀 first\r\n" + new string('x', WorkspaceFileService.MaximumTextBytes + 32) + "\r\nlast";
                File.WriteAllText(Path.Combine(workspace.RootPath, "large.txt"), source, new System.Text.UTF8Encoding(false));
                var exact = files.CaptureFile(workspace, "large.txt", long.MaxValue).Reference;
                var dispatched = 0;
                var largeHunks = new[] {
                    new TextEditHunk { OldText = "first", NewText = "FIRST", StartLine = 1, StartColumn = 6 },
                    new TextEditHunk { OldText = "last", NewText = "LAST" } };
                var changed = files.ApplyTextEdits(workspace, "large.txt", exact, largeHunks, () => dispatched++, effectOperation: "core.patch");
                AssertEqual(1, dispatched, "multi-hunk patch dispatches once");
                AssertEqual(TextPatternEngine.Sha256(source.Replace("first", "FIRST").Replace("last", "LAST")), TextPatternEngine.Sha256(new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(workspace.RootPath, "large.txt")))), "UTF-8 content and CRLF remain exact except hunks");
                AssertTrue(changed.AuthorityCommit != null, "streamed read-back publishes authority");
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = "large-patch", Name = "core.patch", Arguments = new Dictionary<string, object> { ["target"] = "large.txt", ["hunks"] = largeHunks } },
                    "Edit.", "Update large file.", null, ToolResultRoles.User, FixtureCallOrigin());
                var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "large-patch", ToolId = "core.patch" },
                    ToolResult.Ok("Verified."), ToolResultRoles.User);
                accepted.RunId = terminal.RunId = "large-run"; terminal.ResourceEffect = changed.AuthorityCommit.Effect;
                var history = new ChatSession { Messages = new List<ChatMessage> { accepted, terminal } };
                var comparison = new WorkspaceToolChangesProjection(files, new ChatBlobStore(paths)).Read(history, "large-run", "large-patch").Items.Single();
                AssertEqual("hunks", comparison.Availability, "large verified file retains a compact hunk presentation");
                AssertEqual(2, comparison.Hunks.Count, "both accepted fragments remain visible");
                AssertTrue(comparison.Hunks.All(h => h.Offset == -1), "fragment display does not invent absolute offsets");
                RuntimeThrows<WorkspaceFileException>(() => files.ApplyTextEdits(workspace, "large.txt", exact,
                    new[] { new TextEditHunk { OldText = "LAST", NewText = "stale" } }, () => dispatched++));
                AssertEqual(1, dispatched, "stale snapshot never dispatches");
                files.ApplyTextEdits(workspace, "large.txt", changed.Reference, new[] {
                    new TextEditHunk { OldText = "", NewText = "header\r\n", Boundary = "start" } });
                var bytes = File.ReadAllBytes(Path.Combine(workspace.RootPath, "large.txt"));
                AssertTrue(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf && bytes[3] == (byte)'h', "streamed start insertion leaves BOM at byte zero");
                var small = files.CreateText(workspace, "matches.md", "aaaa");
                var request = new ResourceTextSearchRequest { Target = "matches.md", Query = "aa", MatchCase = true };
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                var page = new ResourceTextSearch().Search(request, provider.SearchSources(request, CancellationToken.None), CancellationToken.None);
                AssertEqual(2, page.TotalMatches, "literal search and replacement share non-overlapping matches");
                var receipt = new ResourceSearchObservation(); receipt.Record(request, page);
                files.ReplaceMatches(workspace, "matches.md", receipt.Require("matches.md", "aa", "literal", true, 2), "aa", "b", "literal", true, 2, null, CancellationToken.None);
                AssertEqual("bb", File.ReadAllText(Path.Combine(workspace.RootPath, "matches.md")), "search receipt drives exact replacement");
            });
        }
        private static void UniversalOfficeRoutesToOwners()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var session = NewSession(adapter); var catalog = executor.GetControllerTools().ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var write = executor.ExecuteManual(Command("core.write", "target", "VBA module: Universal", "content", "Sub Run()\nDebug.Print 1\nEnd Sub"), catalog, settings, false, true, session);
                AssertTrue(write.Success, "universal VBA create: " + write.Message);
                var patch = executor.ExecuteManual(Command("core.patch", "target", "VBA module: Universal", "hunks", new JArray(
                    new JObject { ["oldText"] = "Print 1", ["newText"] = "Print 2" })), catalog, settings, false, true, session);
                AssertTrue(patch.Success, "universal VBA patch: " + patch.Message);
                AssertContains(adapter.GetVbaModuleCode("Universal"), "Print 2", "owner read-back verifies changed VBA");
                var rewrite = executor.ExecuteManual(Command("core.write", "target", "VBA module: Universal", "content", "Sub Run()\nEnd Sub"), catalog, settings, false, true, session);
                AssertTrue(rewrite.Success, "same write updates an existing module: " + rewrite.Message);
                var html = executor.ExecuteManual(Command("core.write", "target", "HTML file: index.html", "content", "<main>before</main>"), catalog, settings, false, true, session);
                AssertTrue(html.Success, "same contract creates HTML: " + html.Message);
                var note = executor.ExecuteManual(Command("core.write", "target", "note: review.md", "content", "# Review"), catalog, settings, false, true, session);
                AssertTrue(note.Success, "same contract creates Markdown: " + note.Message);
                var noteUpdate = executor.ExecuteManual(Command("core.write", "target", "note: review.md", "content", ""), catalog, settings, false, true, session);
                AssertTrue(noteUpdate.Success, "upsert resolves a note and permits empty source: " + noteUpdate.Message);
                var htmlUpdate = executor.ExecuteManual(Command("core.write", "target", "HTML file: index.html", "content", "<main>after</main>"), catalog, settings, false, true, session);
                AssertTrue(htmlUpdate.Success, "upsert resolves the current HTML file: " + htmlUpdate.Message);
                var copy = executor.ExecuteManual(Command("resources.copy", "target", "VBA module: Universal", "destination", "VBA module: Duplicate"), catalog, settings, false, true, session);
                AssertTrue(copy.Success, "VBA copy retains the source owner guard: " + copy.Message);
                AssertEqual(adapter.GetVbaModuleCode("Universal"), adapter.GetVbaModuleCode("Duplicate"), "copy is exact source");
                var move = executor.ExecuteManual(Command("resources.move", "target", "VBA module: Duplicate", "destination", "VBA module: Renamed"), catalog, settings, false, true, session);
                AssertTrue(move.Success, "VBA rename: " + move.Message);
                var delete = executor.ExecuteManual(Command("resources.delete", "target", "VBA module: Renamed"), catalog, settings, false, true, session);
                AssertTrue(delete.Success, "VBA delete: " + delete.Message);
                var backupTarget = executor.ResourceGateway.Find(session, "Renamed", "backups").Items.Last().Target;
                var restore = executor.ExecuteManual(Command("resources.restore", "target", backupTarget), catalog, settings, false, true, session);
                AssertTrue(restore.Success, "deleted VBA restores from a discovered backup: " + restore.Message);
                AssertTrue(executor.ExecuteManual(Command("core.write", "target", "VBA module: Klass", "resourceType", "ClassModule", "content", "Public Value As Long"), catalog, settings, false, true, session).Success, "class creation");
                var classUpdate = executor.ExecuteManual(Command("core.write", "target", "VBA module: Klass", "content", "Public Value As String"), catalog, settings, false, true, session);
                AssertTrue(classUpdate.Success, "existing type is resolved by owner: " + classUpdate.Message);
                AssertTrue(!catalog.Any(t => t.Id.StartsWith("vba.module.") || t.Id.StartsWith("html.source.") || t.Id.StartsWith("file.source.")), "source owners are not public tools");
                AssertEqual(10, CallableToolProfile.Core().Select(catalog).Count, "all hosts use the same compact core");
            });
        }

        private static void UniversalOfficeRequiresDeliveredSource()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var session = NewSession(adapter);
                adapter.VbaModuleCode = "Sub Run()\nDebug.Print 1\nDebug.Print 1\nEnd Sub";
                Func<ToolInvocation, IEnumerable<ResourceEvidence>, VbaPendingExecution> prepare = (command, delivered) => {
                    command.ToolCallId = "c" + Guid.NewGuid().ToString("N");
                    var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                        Id = command.ToolCallId, Name = command.ToolId, Arguments = command.Arguments },
                        "Edit.", "Change source.", null, ToolResultRoles.User, FixtureCallOrigin("run-vba-native:1"));
                    accepted.RunId = "run-vba-native";
                    accepted.ResourceEvidence = delivered.ToList(); session.Messages.Add(accepted);
                    return PrepareVbaNative(executor, session, command);
                };
                var blocked = prepare(Command("core.write", "target", "VBA module: Module1", "content", ""), new ResourceEvidence[0]).Record;
                AssertEqual(ToolExecutionOutcome.Error, blocked.Outcome, "accepted model call cannot use manual-editor authority");
                AssertEqual(ToolRetryPolicy.RefreshRequired, blocked.Recovery.RetryPolicy, "fresh source allows recovery after refusal");
                var module = VbaComponent(executor, session, "Module1");
                var excerpt = executor.ResourceGateway.ReadLines(session, new ResourceRef(module.Reference.Uri), "source", 2, 1).Result;
                var evidence = executor.ResourceGateway.Evidence(session, excerpt).ToArray();
                var unseen = prepare(Command("core.patch", "target", "VBA module: Module1", "hunks", new JArray(
                    new JObject { ["oldText"] = "Debug.Print 1", ["newText"] = "Debug.Print 2", ["startLine"] = 3 })), evidence).Record;
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated line outside the observed window is not authority");
                var patch = prepare(Command("core.patch", "target", "VBA module: Module1", "hunks", new JArray(
                    new JObject { ["oldText"] = "Debug.Print 1", ["newText"] = "Debug.Print 2", ["startLine"] = 2 })), evidence);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, patch.Record.Outcome, "current retained excerpt authorizes its exact hunk: " + patch.Record.Result?.Message);
                AssertEqual(ToolExecutionOutcome.Ok, ConfirmVbaNative(patch).Outcome, "exact observed VBA hunk applies");
                var full = ReadVbaSource(executor, session, "Module1");
                var pending = prepare(Command("core.write", "target", "VBA module: Module1", "content", "Sub Run()\nEnd Sub"), executor.ResourceGateway.Evidence(session, full));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "whole source pins overwrite");
                adapter.VbaModuleCode += "\n' external change";
                var stale = ConfirmVbaNative(pending);
                AssertTrue(stale.Outcome == ToolExecutionOutcome.Error && !stale.MayHaveDispatched, "confirmation rechecks source without dispatch");
                AssertEqual(ToolRetryPolicy.RefreshRequired, stale.Recovery.RetryPolicy, "source drift requires a new delivered read");
            });
        }

        private static void UniversalWorkspaceGuardsSurviveConfirmation()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "mutations"));
                var files = new WorkspaceFileService(paths); var blobs = new ChatBlobStore(paths);
                var observed = new Dictionary<string, ResourceEvidence>(); var search = new ResourceSearchObservation();
                var registry = new ToolHandlerRegistry();
                foreach (var entry in ResourceMutationToolCatalog.GetTools())
                    registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson), entry.Policy, entry.Binding),
                        new WorkspaceConversationService.FileHandler(files, workspace, observed, entry.Id, blobs, search));
                var runtime = new ToolRuntime(registry, "agent", false, true, (context, preparation) => "confirmation");
                var sequence = 0;
                Func<string, JObject, ToolExecutionRecord> prepare = (id, args) => {
                    var call = new ToolCall("c" + (++sequence), id, args.ToString(Newtonsoft.Json.Formatting.None));
                    return runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "r", "t", call.Id, DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                };
                Func<ToolExecutionRecord, ToolExecutionRecord> confirm = pending => runtime.ExecuteAsync(new ToolExecutionContext(
                    pending.Context.Call, pending.Context.Policy, "r", "t", pending.Context.StepId, DateTime.UtcNow, true, 1,
                    pending.PreparedStateJson), CancellationToken.None).GetAwaiter().GetResult();
                Func<string, JObject, ToolExecutionRecord> apply = (id, args) => {
                    var pending = prepare(id, args);
                    AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, id + " preparation: " + pending.Result?.Message);
                    var result = confirm(pending); AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, id + ": " + result.Result.Message); return result;
                };
                var first = prepare("core.write", new JObject { ["target"] = "collision.txt", ["content"] = "intent" });
                File.WriteAllText(Path.Combine(workspace.RootPath, "collision.txt"), "external");
                var collision = confirm(first);
                AssertTrue(!collision.MayHaveDispatched && collision.Outcome == ToolExecutionOutcome.Error, "creation never silently turns into replacement while waiting");
                AssertEqual("external", File.ReadAllText(Path.Combine(workspace.RootPath, "collision.txt")), "collision preserves external data");
                var unobserved = prepare("core.write", new JObject { ["target"] = "collision.txt", ["content"] = "overwrite" });
                AssertTrue(!unobserved.MayHaveDispatched && unobserved.Outcome == ToolExecutionOutcome.Error, "whole write requires delivered source");
                observed["collision.txt"] = files.ReadText(workspace, "collision.txt").Evidence;
                var pendingUpdate = prepare("core.write", new JObject { ["target"] = "collision.txt", ["content"] = "prepared" });
                File.WriteAllText(Path.Combine(workspace.RootPath, "collision.txt"), "changed while waiting");
                AssertTrue(!confirm(pendingUpdate).MayHaveDispatched, "source is checked again before overwrite");
                apply("core.write", new JObject { ["target"] = "a.txt", ["content"] = "first\nrepeat\nrepeat" });
                observed["a.txt"] = files.ReadText(workspace, "a.txt").Evidence;
                var whole = observed["a.txt"];
                observed["a.txt"] = files.ReadTextLines(workspace, "a.txt", 2, 1).Evidence;
                var unseen = prepare("core.patch", new JObject { ["target"] = "a.txt", ["hunks"] = new JArray(new JObject { ["oldText"] = "repeat", ["newText"] = "unseen", ["startLine"] = 3 }) });
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated text in an excerpt cannot authorize a different unseen coordinate");
                AssertEqual(ToolRetryPolicy.RefreshRequired, unseen.Recovery.RetryPolicy, "a subsequent relevant read can repair excerpt coverage");
                observed["a.txt"] = whole;
                apply("core.patch", new JObject { ["target"] = "a.txt", ["hunks"] = new JArray(new JObject { ["oldText"] = "repeat", ["newText"] = "second", ["startLine"] = 3 }) });
                AssertEqual("first\nrepeat\nsecond", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "coordinate patch selects original repeated line");
                apply("resources.copy", new JObject { ["target"] = "a.txt", ["destination"] = "b.txt" });
                apply("resources.move", new JObject { ["target"] = "b.txt", ["destination"] = "c.txt" });
                apply("resources.delete", new JObject { ["target"] = "c.txt" });
                apply("resources.restore", new JObject { ["target"] = "c.txt" });
                AssertEqual(File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), File.ReadAllText(Path.Combine(workspace.RootPath, "c.txt")), "copy/move/delete/restore retain exact bytes");
                apply("core.write", new JObject { ["target"] = "history.txt", ["content"] = "before" });
                var historyBefore = observed["history.txt"];
                var historyPatch = new JObject { ["target"] = "history.txt", ["hunks"] = new JArray(new JObject { ["oldText"] = "before", ["newText"] = "after" }) };
                var recorded = apply("core.patch", historyPatch);
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = recorded.Context.Call.Id, Name = "core.patch", Arguments = historyPatch.ToObject<Dictionary<string, object>>() },
                    "Edit.", "Change history.", null, ToolResultRoles.User, FixtureCallOrigin());
                accepted.RunId = "r"; accepted.ResourceEvidence = new List<ResourceEvidence> { historyBefore };
                var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = recorded.Context.Call.Id, ToolId = "core.patch" }, recorded.Result, ToolResultRoles.User);
                terminal.RunId = "r"; terminal.ResourceEffect = recorded.ResourceEffect; terminal.ExecutionProgress = ToolExecutionProgress.Capture(recorded);
                var history = new ChatSession { Messages = new List<ChatMessage> { accepted, terminal } };
                File.WriteAllText(Path.Combine(workspace.RootPath, "history.txt"), "later external source");
                var comparison = new WorkspaceToolChangesProjection(files, blobs).Read(history, "r", recorded.Context.Call.Id).Items.Single();
                AssertEqual("before", comparison.Before, "step diff retains its original source");
                AssertEqual("after", comparison.After, "step diff never reads a later live file");
                var request = new ResourceTextSearchRequest { Target = "a.txt", Query = "repeat", MatchCase = true };
                var provider = new WorkspaceFileResourceProvider(files, workspace);
                var page = new ResourceTextSearch().Search(request, provider.SearchSources(request, CancellationToken.None), CancellationToken.None);
                search.Record(request, page, "same-step");
                RuntimeThrows<ResourceRequestException>(() => search.Require("a.txt", "repeat", "literal", true, 1, "same-step"));
                apply("core.replace", new JObject { ["target"] = "a.txt", ["query"] = "repeat", ["replacement"] = "replaced", ["matchCase"] = true, ["expectedMatches"] = 1 });
                AssertTrue(prepare("core.replace", new JObject { ["target"] = "a.txt", ["query"] = "repeat", ["replacement"] = "bad", ["matchCase"] = true, ["expectedMatches"] = 1 }).Outcome == ToolExecutionOutcome.Error, "old search receipt cannot authorize changed source");
                var uncertain = prepare("core.patch", new JObject { ["target"] = "a.txt", ["hunks"] = new JArray(new JObject { ["oldText"] = "first", ["newText"] = "written" }) });
                files.FaultPoint = point => { if (point == "after-dispatch") throw new IOException("read-back fault"); };
                var unknown = confirm(uncertain);
                AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome, "possible write with failed read-back is unknown");
                AssertTrue(unknown.MayHaveDispatched, "unknown keeps dispatch evidence");
                AssertContains(File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "written", "failure after dispatch must not be reported as no effect");
                var uncertainCall = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = unknown.Context.Call.Id, Name = "core.patch", Arguments = JObject.Parse(unknown.Context.Call.ArgumentsJson).ToObject<Dictionary<string, object>>() },
                    "Edit.", "Change source.", null, ToolResultRoles.User, FixtureCallOrigin());
                uncertainCall.ResourceEvidence = new List<ResourceEvidence> { historyBefore, whole };
                var unknownResult = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = unknown.Context.Call.Id, ToolId = "core.patch" }, unknown.Result, ToolResultRoles.User);
                uncertainCall.RunId = unknownResult.RunId = "unknown-run";
                unknownResult.ExecutionProgress = ToolExecutionProgress.Capture(unknown);
                var unknownView = new WorkspaceToolChangesProjection(files, blobs).Read(new ChatSession {
                    Messages = new List<ChatMessage> { uncertainCall, unknownResult } }, "unknown-run", unknown.Context.Call.Id).Items.Single();
                AssertTrue(unknownView.Availability == "unverified" && unknownView.Before == null && unknownView.IntendedAfter == null,
                    "unknown without exact source binding cannot borrow another file from context for a preview");
            });
        }
    }
}
