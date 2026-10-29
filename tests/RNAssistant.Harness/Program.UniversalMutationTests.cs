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
        private static JObject PatchArguments(string target, string body)
        { return new JObject { ["patch"] = "*** Update Target: " + target + "\n" + body }; }

        private static void UniversalHunksUseOriginalSnapshot()
        {
            var source = "α😀 one\r\nrepeat\r\nrepeat\r\nend";
            var patch = "@@\n-α😀 one\n+α😀 expanded\n+line\n@@\n repeat\n-repeat\n+second\n end\n@@\n-end\n+end!\n";
            AssertEqual("α😀 expanded\r\nline\r\nrepeat\r\nsecond\r\nend!", TextEditPlan.Patch(source, patch).Text, "all hunks address the original; CRLF and missing final newline survive");
            var ambiguous = RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch(source, "@@\n-repeat\n+bad\n"));
            AssertEqual("ambiguous_context", ambiguous.ErrorCode, "repetition cannot choose an arbitrary match");
            AssertContains(ambiguous.Message, "2, 3", "ambiguity includes bounded candidate lines");
            AssertEqual("overlap", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("abc", "@@\n-abc\n+x\n@@\n-abc\n+y\n")).ErrorCode, "overlap rejects entire plan");
            var sections = "# A\n## Work\nold\n# B\n## Work\nnoise\nold\n";
            AssertEqual(sections.Substring(0, sections.Length - 4) + "new\n", TextEditPlan.Patch(sections, "@@ # B\n@@ ## Work\n-old\n+new\n").Text, "ordered distant anchors are language independent");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch(sections, "@@ # B\n@@ # A\n-old\n+bad\n")).ErrorCode, "anchor order is strict");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch(sections, "@@ # A\n-old\n+bad\n")).ErrorCode, "anchor does not silently choose the first later occurrence or imply a language scope");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("abc", "@@\n-b\n+x\n")).ErrorCode, "matching requires complete lines");
            AssertEqual("invalid_patch", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("abc", "@@\nabc\n+x\n")).ErrorCode, "unprefixed body fails");
            AssertEqual("invalid_patch", RuntimeThrows<TextPatternException>(() => TextPatch.Parse("@@\n abc\n" )).ErrorCode, "a hunk needs a change");
            AssertEqual("overlap", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("a\n", "@@\n+x\n a\n@@\n+y\n a\n")).ErrorCode, "same-offset insertions are rejected");
            AssertEqual("\uFEFFheader\nbody", TextEditPlan.Patch("\uFEFFbody", "@@\n+header\n body\n").Text, "start insertion preserves BOM");
            AssertEqual("body\nfooter", TextEditPlan.Patch("body", "@@\n+footer\n*** End of File\n").Text, "append separates an unterminated last line");
            AssertEqual("body\r\nfooter\r\n", TextEditPlan.Patch("body\r\n", "@@\n+footer\n*** End of File\n").Text, "append preserves CRLF and trailing newline");
            AssertEqual("a\nb\nc", TextEditPlan.Patch("a\nc", "@@\n a\n+b\n c\n").Text, "context insertion");
            AssertEqual("a\n", TextEditPlan.Patch("a\nb", "@@\n-b\n*** End of File\n").Text, "delete complete final line");
            AssertEqual("\uFEFFnew\n", TextEditPlan.Patch("\uFEFF", "@@\n+new\n").Text, "existing empty BOM source accepts an addition");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("a\nb", "@@\n-a\n+x\n*** End of File\n")).ErrorCode, "EOF marker is enforced");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("a\na\na", "@@\n-a\n-a\n+b\n")).ErrorCode, "overlapping candidate sequences are ambiguous");
            Func<string, string> vba = VbaTextCanonicalizer.NormalizeVbeComparableLine;
            var code = "Sub Run()\r\n  Debug.Print \"HELLO\" 'Keep\r\nEnd Sub";
            AssertEqual(code.Replace("HELLO", "WORLD"), TextEditPlan.Patch(code,
                "@@ sub run()\n-  debug.print \"HELLO\" 'Keep\n+  Debug.Print \"WORLD\" 'Keep\n end sub\n", normalizeLine: vba).Text, "VBE token matching preserves actual unchanged context");
            foreach (var wrong in new[] { "\"hello\" 'Keep", "\"HELLO\" 'keep" })
                AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch(code,
                    "@@\n-  Debug.Print " + wrong + "\n+ignored\n", normalizeLine: vba)).ErrorCode, "strings and comments stay significant");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("Rem Keep", "@@\n-rem keep\n+x\n", normalizeLine: vba)).ErrorCode, "Rem comments stay significant");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("Dim Value\ndim value", "@@\n-Dim Value\n+Dim Other\n", normalizeLine: vba)).ErrorCode, "equivalent VBA matches remain ambiguous even when one is exact");
            AssertEqual("$1 $1", TextEditPlan.Replace("aa aa", "aa", "$1", "literal", true, 2).Text, "literal replacement preserves dollars");
            AssertEqual("x1\r\nx2", TextEditPlan.Replace("a1\r\na2", "a([0-9])", "x$1", "regex", true, 2).Text, "regex groups preserve line endings");
            AssertEqual("replacement_count_conflict", RuntimeThrows<TextPatternException>(() => TextEditPlan.Replace("aa", "a", "b", "literal", true, 1)).ErrorCode, "count checked before writing");
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
                var largeHunks = "@@\n-α😀 first\n+α😀 FIRST\n@@\n-last\n+LAST\n";
                var changed = files.ApplyTextEdits(workspace, "large.txt", exact, largeHunks, () => dispatched++, effectOperation: "core.patch");
                AssertEqual(1, dispatched, "multi-hunk patch dispatches once");
                AssertEqual(TextPatternEngine.Sha256(source.Replace("first", "FIRST").Replace("last", "LAST")), TextPatternEngine.Sha256(new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(workspace.RootPath, "large.txt")))), "UTF-8 content and CRLF remain exact except hunks");
                AssertTrue(changed.AuthorityCommit != null, "streamed read-back publishes authority");
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = "large-patch", Name = "core.patch", Arguments = new Dictionary<string, object> { ["patch"] = "*** Update Target: large.txt\n" + largeHunks } },
                    "Edit.", "Update large file.", null, ToolResultRoles.User, FixtureCallOrigin());
                var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "large-patch", ToolId = "core.patch" },
                    ToolResult.Ok("Verified.", Newtonsoft.Json.JsonConvert.SerializeObject(new PatchBatchResult { Phase = "execution", WriteStarted = true,
                        Targets = new List<PatchTargetResult> { new PatchTargetResult { Target = "large.txt", Status = ResourceTargetStatus.Changed, WriteStarted = true } } })), ToolResultRoles.User);
                accepted.RunId = terminal.RunId = "large-run";
                terminal.ResourceEffect = new ResourceEffect("large-effect", "core.patch", ResourceEffectOutcome.VerifiedChanged,
                    changed.AuthorityCommit.Effect.Impacts, targets: new[] { new ResourceTargetEffect { Target = "large.txt", Status = ResourceTargetStatus.Changed,
                        Source = exact, After = changed.Reference, WriteStarted = true, Effect = changed.AuthorityCommit.Effect } });
                var history = new ChatSession { Messages = new List<ChatMessage> { accepted, terminal } };
                var comparison = new WorkspaceToolChangesProjection(files, new ChatBlobStore(paths)).Read(history, "large-run", "large-patch").Items.Single();
                AssertEqual("hunks", comparison.Availability, "large verified file retains a compact hunk presentation");
                AssertEqual(2, comparison.Hunks.Count, "both accepted fragments remain visible");
                AssertTrue(comparison.Hunks.All(h => h.Offset == -1), "fragment display does not invent absolute offsets");
                RuntimeThrows<WorkspaceFileException>(() => files.ApplyTextEdits(workspace, "large.txt", exact,
                    "@@\n-LAST\n+stale\n", () => dispatched++));
                AssertEqual(1, dispatched, "stale snapshot never dispatches");
                files.ApplyTextEdits(workspace, "large.txt", changed.Reference, "@@\n+header\n α😀 FIRST\n");
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
                var patch = executor.ExecuteManual(Command("core.patch", "patch", "*** Update Target: VBA module: Universal\n" + "@@ sub run()\n-debug.print 1\n+Debug.Print 2\n"), catalog, settings, false, true, session);
                AssertTrue(patch.Success, "universal VBA patch: " + patch.Message);
                AssertContains(adapter.GetVbaModuleCode("Universal"), "Print 2", "owner read-back verifies changed VBA");
                var replacementRuntime = executor.CreateNativeRuntime(session, catalog,
                    new AppSettings { AutoConfirmToolActions = false }, "agent", false,
                    (execution, preparation) => "replace-confirm");
                Func<string, JObject, string, ToolExecutionRecord> replaceStep = (id, args, step) => {
                    var call = new ToolCall("replace-" + step, id, args.ToString(Newtonsoft.Json.Formatting.None));
                    return replacementRuntime.ExecuteAsync(new ToolExecutionContext(call, replacementRuntime.Describe(call),
                        "replace-run", "replace-turn", step, DateTime.UtcNow, false, 5), CancellationToken.None).GetAwaiter().GetResult();
                };
                var searched = replaceStep("core.search", new JObject { ["target"] = "VBA module: Universal",
                    ["query"] = "Debug.Print 2", ["matchCase"] = true }, "search");
                AssertEqual(ToolExecutionOutcome.Ok, searched.Outcome, "VBA replacement searches current source");
                var replacePending = replaceStep("core.replace", new JObject { ["target"] = "VBA module: Universal",
                    ["query"] = "Debug.Print 2", ["replacement"] = "Debug.Print 3", ["matchCase"] = true,
                    ["expectedMatches"] = 1 }, "replace");
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, replacePending.Outcome,
                    "VBA replacement uses its completed search: " + replacePending.Result?.Message);
                var replaced = ConfirmVbaNative(new VbaPendingExecution { Runtime = replacementRuntime, Record = replacePending });
                AssertEqual(ToolExecutionOutcome.Ok, replaced.Outcome, "VBA replacement uses the guarded whole writer");
                AssertContains(adapter.GetVbaModuleCode("Universal"), "Debug.Print 3", "replacement verifies full module read-back");
                var rewrite = executor.ExecuteManual(Command("core.write", "target", "VBA module: Universal", "content", "Sub Run()\nEnd Sub"), catalog, settings, false, true, session);
                AssertTrue(rewrite.Success, "same write updates an existing module: " + rewrite.Message);
                var html = executor.ExecuteManual(Command("core.write", "target", "HTML file: index.html", "content", "<main>before</main>"), catalog, settings, false, true, session);
                AssertTrue(html.Success, "same contract creates HTML: " + html.Message);
                var historicalHtml = executor.ResourceGateway.ResolveIntentTarget(session,
                    executor.ResourceGateway.Find(session, "index.html", "html").Items.First(item => item.Target.StartsWith("HTML file: ")).Target).Descriptor.SnapshotTarget;
                var note = executor.ExecuteManual(Command("core.write", "target", "note: review.md", "content", "# Review"), catalog, settings, false, true, session);
                AssertTrue(note.Success, "same contract creates Markdown: " + note.Message);
                var historicalNote = executor.ResourceGateway.Find(session, "review.md", "document").Items.First(item => item.Target.StartsWith("note: ")).Target;
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
                AssertTrue(executor.ExecuteManual(Command("core.write", "target", "file: report.txt", "content", "before"), catalog, settings, false, true, session).Success,
                    "authored file joins the same prepared package");
                var mixed = Command("core.patch", "patch", "*** Update Target: VBA module: Klass\n@@\n-Public Value As String\n+Public Value As Boolean\n" +
                    "*** Update Target: HTML file: index.html\n@@\n-<main>after</main>\n+<main>verified</main>\n" +
                    "*** Update Target: note: review.md\n@@\n+# Complete\n" +
                    "*** Update Target: file: report.txt\n@@\n-before\n+after\n");
                var mixedPending = PrepareVbaNative(executor, session, mixed);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, mixedPending.Record.Outcome, "mixed owners prepare together: " + mixedPending.Record.Result?.DataJson);
                var mixedResult = ConfirmVbaNative(mixedPending);
                AssertEqual(ToolExecutionOutcome.Ok, mixedResult.Outcome, "mixed owners publish sequentially: " + mixedResult.Result.DataJson);
                AssertEqual(4, mixedResult.ResourceEffect.Targets.Count, "each owner keeps its own effect");
                var acceptedMixed = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = mixedResult.Context.Call.Id, Name = "core.patch",
                    Arguments = mixed.Arguments }, "Edit.", "Related edits.", null, ToolResultRoles.User, FixtureCallOrigin());
                var terminalMixed = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = mixedResult.Context.Call.Id, ToolId = "core.patch" }, mixedResult.Result, ToolResultRoles.User);
                acceptedMixed.RunId = terminalMixed.RunId = mixedResult.Context.RunId; terminalMixed.ResourceEffect = mixedResult.ResourceEffect;
                session.Messages.Add(acceptedMixed); session.Messages.Add(terminalMixed);
                var authorityStore = new ResourceAuthorityStore(FixturePaths.Value);
                var payloads = new ChatBlobStore(FixturePaths.Value);
                var authority = new ResourceAuthorityService(authorityStore, authorityStore, new ResourceMutationJournal(FixturePaths.Value), payloads);
                var presentation = PatchChangesProjection.Read(session, mixedResult.Context.RunId, mixedResult.Context.Call.Id, payloads,
                    (reference, maximum) => PatchChangesProjection.ReadSource(authority, reference, maximum),
                    (reference, after, body) => PatchChangesProjection.ReadFragments(authority, reference, after, body));
                AssertTrue(presentation.Items.Count == 4 && presentation.Items.All(i => i.Availability == "available" && i.After != null), "retained VBA/HTML/Markdown/authored-file read-back is visible per target");
                var runView = new RunChangesService(a => a.InlineText, null, null, authority).Read(session, mixedResult.Context.RunId);
                AssertEqual(4, runView.Items.Count, "run-wide presentation does not repeat patch artifacts");
                AssertContains(presentation.Items[0].After, "Boolean", "VBA display uses actual saved read-back");
                var exactHtml = executor.ResourceGateway.Find(session, "index.html", "html").Items.First(item => item.Target.StartsWith("HTML file: ")).Target;
                var duplicate = PrepareVbaNative(executor, session, Command("core.patch", "patch", "*** Update Target: HTML file: index.html\n@@\n-<main>verified</main>\n+<main>changed</main>\n" +
                    "*** Update Target: " + exactHtml + "\n@@\n-<main>verified</main>\n+<main>other</main>\n")).Record;
                AssertTrue(duplicate.Outcome == ToolExecutionOutcome.Error && !duplicate.MayHaveDispatched, "different semantic aliases cannot change the same resource twice");
                AssertContains(duplicate.Result.DataJson, "Two target sections resolve", "duplicate rejection uses resolved identity");
                foreach (var stale in new[] { historicalHtml + "\n@@\n-<main>before</main>\n+<main>stale</main>\n", historicalNote + "\n@@\n-# Review\n+# Stale\n" })
                {
                    var refused = PrepareVbaNative(executor, session, Command("core.patch", "patch", "*** Update Target: VBA module: Klass\n@@\n-Public Value As Boolean\n+Public Value As Double\n*** Update Target: " + stale)).Record;
                    AssertTrue(refused.Outcome == ToolExecutionOutcome.Error && !refused.MayHaveDispatched, "historical owner target rejects the whole package before VBA writes: " + refused.Result?.DataJson);
                    AssertContains(refused.Result.DataJson, stale.StartsWith("HTML file: ", StringComparison.Ordinal) ? "source_changed" : "resource_target_not_found", "historical source retains the owner conflict/availability diagnostic");
                    AssertEqual("Public Value As Boolean", adapter.GetVbaModuleCode("Klass"), "first target stays untouched when a later owner source is historical");
                }
                var crossPending = PrepareVbaNative(executor, session, Command("core.patch", "patch", "*** Update Target: VBA module: Klass\n@@\n-Public Value As Boolean\n+Public Value As Long\n" +
                    "*** Update Target: VBA module: Universal\n@@\n-Sub Run()\n+Sub RunChanged()\n"));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, crossPending.Record.Outcome, "two VBA sources prepare before writes");
                adapter.BeforeVbaBackendCall = call => {
                    if (call.Operation == FakeVbaOperation.ReplaceModule)
                        adapter.SetVbaModule("Universal", "Sub Run()\n' VBE recased or externally changed\nEnd Sub", "StdModule");
                };
                var cross = ConfirmVbaNative(crossPending);
                AssertEqual(ToolExecutionOutcome.Error, cross.Outcome, "a preceding VBA mutation cannot bypass the next source guard");
                AssertTrue(cross.ResourceEffect.Targets.Select(t => t.Status).SequenceEqual(new[] { ResourceTargetStatus.Changed, ResourceTargetStatus.Rejected }), "cross-module change preserves first result and rejects stale second");
                adapter.BeforeVbaBackendCall = null;


            });
        }

        private static void UniversalHtmlBatchPinsMembers()
        {
            WithTempExecutor((executor, adapter) =>
            {
                var session = NewSession(adapter);
                var catalog = executor.GetControllerTools().ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                Action<string, string> write = (path, content) => {
                    var saved = executor.ExecuteManual(Command("core.write", "target", "HTML file: " + path,
                        "content", content), catalog, settings, false, true, session);
                    AssertTrue(saved.Success, "HTML fixture publication: " + saved.Message);
                };
                Func<string, string> source = path => session.HtmlWorkspace.Files.Single(f => f.Path == path).Content;
                Func<string, string, string, string> section = (path, before, after) =>
                    "*** Update Target: HTML file: " + path + "\n" + LineHunk(before, after);
                write("index.html", "<main>one</main>");
                write("script.js", "let count = 1;");
                var patch = Command("core.patch", "patch", section("index.html", "<main>one</main>", "<main>two</main>") +
                    section("script.js", "let count = 1;", "let count = 2;"));
                var pending = PrepareVbaNative(executor, session, patch);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "two HTML members prepare together");
                AssertEqual("<main>one</main>", source("index.html"), "confirmation does not publish changes");
                var completed = ConfirmVbaNative(pending);
                AssertEqual(ToolExecutionOutcome.Ok, completed.Outcome, "sibling aggregate revision does not stale the second member: " + completed.Result?.DataJson);
                AssertTrue(completed.ResourceEffect.Targets.All(t => t.Status == ResourceTargetStatus.Changed), "both member writes are confirmed");
                AssertEqual("<main>two</main>", source("index.html"), "first member saved");
                AssertEqual("let count = 2;", source("script.js"), "second member saved");
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = completed.Context.Call.Id,
                    Name = "core.patch", Arguments = patch.Arguments }, "Edit.", "Update two HTML members.", null, ToolResultRoles.User, FixtureCallOrigin());
                var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = completed.Context.Call.Id,
                    ToolId = "core.patch" }, completed.Result, ToolResultRoles.User);
                accepted.RunId = terminal.RunId = completed.Context.RunId; terminal.ResourceEffect = completed.ResourceEffect;
                session.Messages.Add(accepted); session.Messages.Add(terminal);
                var view = PatchChangesProjection.Read(session, completed.Context.RunId, completed.Context.Call.Id,
                    new ChatBlobStore(FixturePaths.Value),
                    (reference, maximum) => PatchChangesProjection.ReadSource(executor.ResourceAuthority, reference, maximum),
                    (reference, after, body) => PatchChangesProjection.ReadFragments(executor.ResourceAuthority, reference, after, body));
                AssertEqual(2, view.Items.Count, "member effects retain separate diff rows");
                AssertTrue(view.Items.All(item => item.Availability == "available"), "both retained diffs remain readable after the aggregate advances");
                AssertEqual("<main>one</main>", view.Items[0].Before, "first original snapshot is retained");
                AssertEqual("let count = 2;", view.Items[1].After, "second diff uses verified member read-back");

                var stale = PrepareVbaNative(executor, session, Command("core.patch", "patch",
                    section("index.html", "<main>two</main>", "<main>three</main>") + section("script.js", "let count = 2;", "let count = 3;")));
                write("script.js", "let count = 9;");
                var refused = ConfirmVbaNative(stale);
                AssertEqual(ToolExecutionOutcome.Error, refused.Outcome, "external change before confirmation rejects the whole package");
                AssertTrue(!refused.MayHaveDispatched, "preflight detects drift before the first write");
                AssertEqual("<main>two</main>", source("index.html"), "first member is unchanged after failed preflight");
                write("script.js", "let count = 2;");
                write("style.css", "body{}");
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print 1\nEnd Sub";
                var crossPatch = Command("core.patch", "patch",
                    section("index.html", "<main>two</main>", "<main>three</main>") +
                    "*** Update Target: VBA module: Module1\n" + LineHunk("Debug.Print 1", "Debug.Print 2") +
                    section("script.js", "let count = 2;", "let count = 3;") +
                    section("style.css", "body{}", "body{color:red;}"));
                var injected = false;
                var crossRuntime = executor.CreateNativeRuntime(session, ToolPackSnapshotFactory.Capture("agent", adapter.HostName, catalog),
                    new AppSettings { AutoConfirmToolActions = false }, "agent", false, (execution, preparation) => "cross-confirm",
                    dispatchGuard: execution => {
                        if (!injected && execution.TargetOrdinal == 2)
                        {
                            injected = true;
                            write("script.js", "let count = 99;");
                            write("script.js", "let count = 2;");
                        }
                        return null;
                    });
                var crossCall = new ToolCall("html-cross", "core.patch", Newtonsoft.Json.JsonConvert.SerializeObject(crossPatch.Arguments));
                var cross = new VbaPendingExecution { Runtime = crossRuntime,
                    Record = crossRuntime.ExecuteAsync(new ToolExecutionContext(crossCall, crossRuntime.Describe(crossCall),
                        "html-cross-run", "html-cross-turn", "cross", DateTime.UtcNow, false, 5), CancellationToken.None).GetAwaiter().GetResult() };
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, cross.Record.Outcome, "mixed package pins all members before dispatch");
                var partial = ConfirmVbaNative(cross);
                AssertTrue(injected, "prior write triggers a member change and restoration before the next target");
                AssertEqual(ToolExecutionOutcome.Error, partial.Outcome, "equal text does not hide member republication");
                AssertTrue(partial.ResourceEffect.Targets.Select(t => t.Status).SequenceEqual(new[] {
                    ResourceTargetStatus.Changed, ResourceTargetStatus.Changed, ResourceTargetStatus.Rejected, ResourceTargetStatus.NotStarted }),
                    "member conflict preserves completed effects and stops the remainder: " + partial.Result?.DataJson);
                AssertContains(partial.Result.DataJson, "source_changed", "member version drift has a stable diagnostic");
                AssertEqual("<main>three</main>", source("index.html"), "confirmed first effect is preserved");
                AssertEqual("let count = 2;", source("script.js"), "stale candidate is never written despite identical restored source");
                AssertEqual("body{}", source("style.css"), "later member is not started");
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
                var unseen = prepare(Command("core.patch", "patch", "*** Update Target: VBA module: Module1\n" + "@@\n Debug.Print 1\n-Debug.Print 1\n+Debug.Print 2\n End Sub\n"), evidence).Record;
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated line outside the observed window is not authority");
                var patch = prepare(Command("core.patch", "patch", "*** Update Target: VBA module: Module1\n" + "@@\n-Debug.Print 1\n+Debug.Print 2\n Debug.Print 1\n"), evidence);
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

        private static void UniversalPatchBatchesPreservePerTargetFacts()
        {
            foreach (var scenario in new[] { "success", "invalid", "alias", "stale", "partial", "unknown" })
                WithTempPaths(paths => {
                    var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "batch"));
                    var files = new WorkspaceFileService(paths); var blobs = new ChatBlobStore(paths);
                    var observed = new Dictionary<string, ResourceEvidence>();
                    foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
                    {
                        File.WriteAllText(Path.Combine(workspace.RootPath, name), name + " old\r\n");
                        observed[name] = files.ReadText(workspace, name).Evidence;
                    }
                    observed["./a.txt"] = observed["a.txt"];
                    var entry = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch);
                    var registry = new ToolHandlerRegistry();
                    registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson), entry.Policy, entry.Binding),
                        new ResourcePatchBatchHandler(new WorkspaceConversationService.FileHandler(files, workspace, observed, entry.Id, blobs, new ResourceSearchObservation())));
                    var runtime = new ToolRuntime(registry, "agent", false, true, (ctx, prep) => "approval");
                    var second = scenario == "alias" ? "a.txt" : "b.txt";
                    var body = "*** Update Target: a.txt\n@@\n-a.txt old\n+a.txt new\n*** Update Target: " + second + "\n@@\n-" +
                        (scenario == "alias" ? "a.txt" : "b.txt") + " old\n+" + (scenario == "success" ? "b.txt old" : "b.txt new") + "\n*** Update Target: c.txt\n@@\n-c.txt " +
                        (scenario == "invalid" ? "missing" : "old") + "\n+c.txt new\n";
                    var call = new ToolCall("batch", entry.Id, new JObject { ["patch"] = body }.ToString(Newtonsoft.Json.Formatting.None));
                    var pending = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "step", DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    ToolExecutionRecord completed;
                    if (scenario == "invalid" || scenario == "alias")
                    {
                        AssertEqual(ToolExecutionOutcome.Error, pending.Outcome, scenario + " rejects entire preparation");
                        completed = pending;
                    }
                    else
                    {
                        AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, scenario + " prepares every target");
                        var preview = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(pending.ConfirmationDataJson);
                        AssertTrue(preview.Targets.Count == 3 && preview.Targets.All(t => t.Status == ResourceTargetStatus.Prepared && t.Preview.Count > 0), "preview covers approved package");
                        if (scenario == "stale") File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "external");
                        var dispatched = 0;
                        files.FaultPoint = point => {
                            if (point != "after-dispatch") return;
                            dispatched++;
                            if (scenario == "partial" && dispatched == 1) File.WriteAllText(Path.Combine(workspace.RootPath, "b.txt"), "external");
                            if (scenario == "unknown" && dispatched == 2) throw new IOException("read-back unavailable");
                        };
                        completed = runtime.ExecuteAsync(new ToolExecutionContext(call, pending.Context.Policy, "run", "turn", "step", DateTime.UtcNow, true, 1,
                            pending.PreparedStateJson), CancellationToken.None).GetAwaiter().GetResult();
                    }
                    var result = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(completed.Result.DataJson);
                    AssertEqual(3, result.Targets.Count, scenario + " lists every target");
                    if (scenario == "invalid" || scenario == "alias" || scenario == "stale")
                    {
                        AssertTrue(!completed.MayHaveDispatched && !result.WriteStarted, "all-or-reject preparation has no write");
                        AssertEqual("a.txt old\r\n", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "first prepared target is untouched");
                        AssertEqual(scenario == "invalid" ? "context_not_found" : scenario == "alias" ? "invalid_patch" : "source_changed",
                            result.Targets.Single(t => t.Diagnostic != null).Diagnostic.Code, "typed preparation reason");
                        if (scenario == "invalid") AssertEqual(1, result.Targets[2].Diagnostic.Hunk.Value, "diagnostic hunk is local to its target");
                        return;
                    }
                    var expected = scenario == "success" ? new[] { ResourceTargetStatus.Changed, ResourceTargetStatus.Unchanged, ResourceTargetStatus.Changed } :
                        new[] { ResourceTargetStatus.Changed, scenario == "unknown" ? ResourceTargetStatus.Unknown : ResourceTargetStatus.Rejected, ResourceTargetStatus.NotStarted };
                    AssertTrue(result.Targets.Select(t => t.Status).SequenceEqual(expected), scenario + " preserves per-target truth");
                    AssertEqual(scenario == "success" ? ToolExecutionOutcome.Ok : scenario == "unknown" ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Error, completed.Outcome, "top status follows actual effects");
                    AssertEqual(scenario != "success", result.Partial, "partial flag preserves first verified write");
                    AssertTrue(completed.Recovery == null, "never automatically replay a changed package");
                    AssertEqual("a.txt new\r\n", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "first result survives subsequent failure");
                    if (scenario != "success") AssertEqual("c.txt old\r\n", File.ReadAllText(Path.Combine(workspace.RootPath, "c.txt")), "remainder never starts");
                    var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = call.Id, Name = call.Name,
                        Arguments = new Dictionary<string, object> { ["patch"] = body } }, "Edit.", "Related edits.", null, ToolResultRoles.User, FixtureCallOrigin());
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.Id, ToolId = call.Name }, completed.Result, ToolResultRoles.User);
                    accepted.RunId = terminal.RunId = "run"; terminal.ResourceEffect = completed.ResourceEffect;
                    terminal.ResourceEvidence = completed.ResourceEvidence.ToList();
                    AssertEqual("a.txt", ModelContextCompiler.CurrentSourceLabel(terminal, completed.ResourceEvidence.First()), "verified after-source keeps its semantic target even after a later failure");
                    var session = new ChatSession { Messages = new List<ChatMessage> { accepted, terminal } };
                    session = Newtonsoft.Json.JsonConvert.DeserializeObject<ChatSession>(Newtonsoft.Json.JsonConvert.SerializeObject(session));
                    var modelMessage = ModelToolResultProjection.Project(terminal);
                    RNAssistant.Core.ModelProtocol.ToolResultWireReadResult wire; string readError;
                    AssertTrue(ToolResultHistoryReader.TryRead(modelMessage, out wire, out readError), "model result remains typed");
                    var visible = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(wire.Result.DataJson);
                    AssertTrue(visible.Targets.Select(t => t.Status).SequenceEqual(expected), "model receives all per-target statuses");
                    var receipt = ModelContextCompiler.CompleteOperation(modelMessage, wire,
                        new ResourceObservationNotice { State = EvidenceState.Superseded, BodyIncluded = false }, call: accepted);
                    AssertTrue(receipt.CompletedOperation.Targets.SequenceEqual(new[] { "a.txt", "b.txt", "c.txt" }), "historical receipt keeps every semantic target");
                    AssertTrue(Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(receipt.CompletedOperation.DataJson).Targets.Select(t => t.Status).SequenceEqual(expected), "omitted source does not erase partial patch facts");
                    AssertTrue(!wire.Result.DataJson.Contains("rna://") && !wire.Result.DataJson.Contains("sourceHash"), "runtime identity never becomes a model argument");
                    var view = new WorkspaceToolChangesProjection(files, blobs).Read(session, "run", call.Id);
                    AssertTrue(view.Items.Select(t => t.TargetStatus.Value).SequenceEqual(expected), "history round-trip retains all target statuses");
                    AssertEqual("a.txt new\r\n", view.Items[0].After, "confirmed result stays visible beside failure");
                    if (scenario != "success")
                    {
                        session.LastRun = RunViewSession(new RunSummary("run", "turn", RunLifecycle.Completed,
                            new ToolCounts(writeError: scenario == "partial" ? 1 : 0, writeUnknown: scenario == "unknown" ? 1 : 0),
                            1, 1, "Stopped.", "model_finish", null)).LastRun;
                        session.Messages.Add(EffectMessage("run", call.Id, completed.Evidence.Effect));
                        var runState = RunViewStateProjector.Create(session);
                        AssertEqual(1, runState.VerifiedWrites, "run summary retains confirmed changes in a partial package");
                        AssertEqual(scenario == "unknown" ? RunViewHealth.Unknown : RunViewHealth.Errors, runState.ExecutionHealth, "a known partial failure is not a new unknown effect");
                    }
                    if (scenario == "unknown") AssertTrue(view.Items[1].After == null && view.Items[1].IntendedAfter.Contains("b.txt new"), "unknown only shows intended comparison");
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
                {
                    var handler = new WorkspaceConversationService.FileHandler(files, workspace, observed, entry.Id, blobs, search);
                    registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson), entry.Policy, entry.Binding),
                        entry.Id == ResourceMutationToolCatalog.Patch ? (IToolHandler)new ResourcePatchBatchHandler(handler) : handler);
                }
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
                var unseen = prepare("core.patch", PatchArguments("a.txt", "@@\n repeat\n-repeat\n+unseen\n"));
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated text in an excerpt cannot authorize a different unseen coordinate");
                AssertEqual(ToolRetryPolicy.RefreshRequired, unseen.Recovery.RetryPolicy, "a subsequent relevant read can repair excerpt coverage");
                observed["a.txt"] = whole;
                apply("core.patch", PatchArguments("a.txt", "@@\n repeat\n-repeat\n+second\n"));
                AssertEqual("first\nrepeat\nsecond", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "coordinate patch selects original repeated line");
                apply("resources.copy", new JObject { ["target"] = "a.txt", ["destination"] = "b.txt" });
                apply("resources.move", new JObject { ["target"] = "b.txt", ["destination"] = "c.txt" });
                apply("resources.delete", new JObject { ["target"] = "c.txt" });
                apply("resources.restore", new JObject { ["target"] = "c.txt" });
                AssertEqual(File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), File.ReadAllText(Path.Combine(workspace.RootPath, "c.txt")), "copy/move/delete/restore retain exact bytes");
                apply("core.write", new JObject { ["target"] = "history.txt", ["content"] = "before" });
                var historyBefore = observed["history.txt"];
                var historyPatch = PatchArguments("history.txt", "@@\n-before\n+after\n");
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
                var uncertain = prepare("core.patch", PatchArguments("a.txt", "@@\n-first\n+written\n"));
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
