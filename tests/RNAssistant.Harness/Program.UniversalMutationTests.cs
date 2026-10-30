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
        private static TextEditIntent Edit(string old, string replacement, string[] preceding = null, string[] following = null)
        { return new TextEditIntent { Old = old, New = replacement, AnchorsBefore = preceding, AnchorsAfter = following }; }
        private static TextPatch MergeEdits(params TextPatch[] patches) { return new TextPatch(patches.SelectMany(p => p.Intents)); }
        private static JArray MergeTargets(params JArray[] targets) { return new JArray(targets.SelectMany(t => t)); }
        private static TextPatch EditSet(params TextEditIntent[] edits) { return new TextPatch(edits); }
        private static ResourcePatchSection PatchSection(string target, TextPatch body)
        { return new ResourcePatchSection { Target = target, Edits = body.Intents.ToList() }; }
        private static JArray PatchTargets(params ResourcePatchSection[] sections) { return JArray.FromObject(sections); }
        private static JObject PatchArguments(string target, TextPatch body)
        { return new JObject { ["targets"] = PatchTargets(PatchSection(target, body)) }; }

        private static void UniversalPatchSchemaExamplesApply()
        {
            var entry = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch);
            var schema = JObject.Parse(entry.ArgumentSchemaJson);
            string error;
            var arguments = PatchArguments("VBA module: Module1", EditSet(Edit("old", "new"), Edit("End Sub", "ready\nEnd Sub")));
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, schema, false, out error), error);
            AssertTrue(ToolSchemaSupport.ValidateArguments(arguments, ToolSchemaSupport.ForPrompt(schema), false, out error), error);
            AssertEqual(2, ResourcePatch.Parse(arguments.ToString())[0].Edits.Count, "typed public edit shapes");
            AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject { ["patch"] = "@@\n-old\n+new" }, schema, false, out error), "retired manual diff rejected");
            foreach (var bad in new[] {
                new JObject { ["old"] = "", ["new"] = "x" },
                new JObject { ["op"] = "insert", ["text"] = "x", ["position"] = "before" },
                new JObject { ["old"] = "a", ["new"] = "b", ["offset"] = 1 },
                new JObject { ["before"] = "a", ["after"] = "b" },
                new JObject { ["old"] = "a", ["new"] = "b", ["context"] = new JObject() },
                new JObject { ["old"] = "a", ["new"] = "b", ["op"] = "replace" },
                new JObject { ["old"] = 1, ["new"] = "b" },
                new JObject { ["Old"] = "a", ["new"] = "b" },
                new JObject { ["old"] = "a", ["new"] = JValue.CreateNull() },
                new JObject { ["old"] = "a", ["new"] = "b", ["anchors_before"] = JValue.CreateNull() },
                new JObject { ["old"] = "a", ["new"] = "b", ["anchors_after"] = new JArray("line\nother") },
                new JObject { ["old"] = "a", ["new"] = "b", ["anchors_after"] = JArray.FromObject(Enumerable.Repeat("x", 101)) } })
            {
                var invalid = new JObject { ["targets"] = new JArray(new JObject { ["target"] = "a.txt", ["edits"] = new JArray(bad) }) };
                AssertTrue(!ToolSchemaSupport.ValidateArguments(invalid, schema, false, out error), "closed schema rejects invalid edit");
                AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => ResourcePatch.Parse(invalid.ToString())).ErrorCode, "parser rejects without JSON coercion");
            }
            AssertEqual("old,new,anchors_before,anchors_after", string.Join(",", ((JObject)schema["properties"]["targets"]["items"]["properties"]["edits"]["items"]["properties"]).Properties().Select(p => p.Name)), "one normative edit shape");
            foreach (var scope in new[] { arguments, (JObject)arguments["targets"][0] })
            {
                scope["unknown"] = true;
                AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => ResourcePatch.Parse(arguments.ToString())).ErrorCode, "all objects are closed");
                scope.Remove("unknown");
            }
            AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => ResourcePatch.ReadPreparation(arguments.ToString(), "{\"Version\":2}")).ErrorCode, "previous structured approvals cannot execute");
            AssertContains(entry.Description, "TOOL_RESULT", "receipt does not prove effect");
            AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => ResourcePatch.ReadPreparation(arguments.ToString(), "{\"Version\":1}")).ErrorCode, "old approvals cannot execute");
        }

        private static void UniversalPatchDiagnosticResults()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "diagnostics"));
                var files = new WorkspaceFileService(paths);
                File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "a\nx\nx\nend");
                var observed = new Dictionary<string, ResourceEvidence> { ["a.txt"] = files.ReadText(workspace, "a.txt").Evidence };
                var entry = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch);
                var registry = new ToolHandlerRegistry();
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson), entry.Policy, entry.Binding),
                    new ResourcePatchBatchHandler(new WorkspaceConversationService.FileHandler(files, workspace, observed, entry.Id, new ChatBlobStore(paths), new ResourceSearchObservation())));
                var runtime = new ToolRuntime(registry, "agent", false, true);
                var invalid = new[] { "{", "{\"unexpected\":1}", "{\"targets\":[{\"target\":\"a.txt\",\"edits\":[{\"old\":\"\",\"new\":\"x\"}]}]}" };
                foreach (var json in invalid)
                {
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), entry.Id, json);
                    var record = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", call.Id, DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    var data = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(record.Result.DataJson);
                    AssertEqual(ToolExecutionOutcome.Error, record.Outcome, "invalid arguments are a known refusal");
                    AssertTrue(!record.MayHaveDispatched && !data.WriteStarted && !data.Partial && data.Targets.Count == 0, "malformed package invents no targets or effects");
                    AssertEqual("preparation", data.Phase, "invalid arguments use the existing preparation phase");
                    AssertEqual("invalid_edit", data.Diagnostic.Code, "runtime schema refusal uses patch diagnostic");
                    AssertEqual("fix_patch", data.Diagnostic.NextAction, "schema refusal tells the model how to recover");
                }
                var calls = new[] { EditSet(Edit("x", "new")), EditSet(Edit("absent", "new")), EditSet(Edit("a\nx", "new"), Edit("a", "other")) };
                var codes = new[] { "ambiguous_context", "context_not_found", "overlap" };
                var actions = new[] { "refine_context", "read_target", "fix_patch" };
                for (var i = 0; i < calls.Length; i++)
                {
                    var call = new ToolCall("diagnostic-" + i, entry.Id, PatchArguments("a.txt", calls[i]).ToString());
                    var record = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", call.Id, DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    var data = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(record.Result.DataJson);
                    var diagnostic = data.Targets.Single().Diagnostic;
                    AssertTrue(!record.MayHaveDispatched && !data.Partial, "resolver refusal never writes");
                    AssertEqual(codes[i], diagnostic.Code, "public diagnostic code");
                    AssertEqual(actions[i], diagnostic.NextAction, "public recovery hint");
                    AssertTrue(diagnostic.Edit >= 1, "edit index belongs to the original edits array");
                    if (i == 0) AssertTrue(diagnostic.Candidates.Select(c => c.Line).SequenceEqual(new long[] { 2, 3 }), "actual candidate lines are bounded and ordered");
                }
                AssertEqual("a\nx\nx\nend", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "all rejected calls preserve source");
            });
            var reason = "Owner refused rna://workspace/private/path@secret " + new string('a', 64) + "\nSTACK_AND_SOURCE";
            var bounded = PatchDiagnostic.From(new TextPatternException("VBA_CODE_INVALID", reason, 2,
                Enumerable.Range(1, 9).Select(i => new PatchCandidate { Line = i, Context = "<script>\"😀" + new string('x', 500) }).ToArray()));
            AssertEqual("VBA_CODE_INVALID", bounded.OwnerCode, "the real owner code keeps its original spelling");
            AssertEqual("fix_patch", bounded.NextAction, "known VBA validation refusal can be corrected");
            AssertTrue(!bounded.Message.Contains("rna://") && !bounded.Message.Contains(new string('a', 64)) && !bounded.Message.Contains("STACK_AND_SOURCE"), "diagnostic contains no runtime values or stack/source tail");
            AssertTrue(bounded.Candidates.Count == 5 && bounded.Candidates.All(c => c.Context.Length <= 161), "candidate text and count are bounded at serialization");
            var unknown = PatchDiagnostic.Unknown(ToolResult.Unknown(reason, "{\"code\":\"vba_write_unverified\"}"));
            AssertEqual("vba_write_unverified", unknown.OwnerCode, "unknown preserves a real owner code when supplied");
            AssertEqual("verify_effect", unknown.NextAction, "unknown never authorizes retry");
        }

        private static void UniversalHunksUseOriginalSnapshot()
        {
            string Apply(string source, params TextEditIntent[] edits) => TextEditPlan.Patch(source, EditSet(edits)).Text;
            AssertEqual("α😀 expanded\r\nline\r\nrepeat\r\nsecond\r\nend!",
                Apply("α😀 one\r\nrepeat\r\nrepeat\r\nend", Edit("one", "expanded\r\nline"),
                    Edit("repeat", "second", new[] { "repeat" }, new[] { "end" }), Edit("end", "end!")), "original snapshot, scalar boundaries and EOL");
            AssertEqual("axc", Apply("abc", Edit("b", "x")), "within-line replacement");
            AssertEqual("function f(){let x=1;return x*3}", Apply("function f(){let x=1;return x*2}", Edit("return x*2", "return x*3")), "minified line substring");
            AssertEqual("a\nX\rc", Apply("a\nb\rc", Edit("b", "X")), "mixed EOL unchanged outside old");
            AssertEqual("a\r\nX\nY\r\nz", Apply("a\r\nb\r\nz", Edit("b", "X\nY")), "new EOL remains literal");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => Apply("a\r\nb", Edit("a\nb", "x"))).ErrorCode, "old EOL remains exact");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => Apply("a\r\nb", Edit("\n", "x"))).ErrorCode, "cannot split CRLF");
            AssertEqual("a\nb", Apply("a\r\nb", Edit("\r\n", "\n")), "explicit EOL change");
            AssertEqual("a\r\nb\rc\n", Apply("a\r\nb\rc\n", Edit("a\r\nb\rc\n", "a\r\nb\rc\n")), "mixed EOL no-op and final newline");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => Apply("a", Edit("a", "b"), Edit("b", "c"))).ErrorCode, "edits never see intermediate new text");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => Apply("Sub A()\nEnd Sub\nSub B()\nEnd Sub", Edit("End Sub", "x", new[] { "Sub A()" }))).ErrorCode, "anchors are not procedure scopes");
            AssertEqual("H\nH\nx\nH\nH\nnew\nT\nT", Apply("H\nH\nx\nH\nH\nx\nT\nT", Edit("x", "new", new[] { "H", "H", "H" }, new[] { "T", "T" })), "repeated distant anchors on both sides");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => Apply("H\nH\nx\nx\nT\nT", Edit("x", "new", new[] { "H", "H" }, new[] { "T", "T" }))).ErrorCode, "two distinct ranges despite multiple anchor chains");
            AssertEqual("\n\nnew\n\n", Apply("\n\nx\n\n", Edit("x", "new", new[] { "", "" }, new[] { "" })), "repeated empty anchors");
            AssertEqual("H\nnew\nT", TextEditPlan.Patch("H\nx\nT", EditSet(Edit("x", "new", new[] { "H" }, new[] { "T" })).WithCoverage(new[] { Tuple.Create(0L, 1L), Tuple.Create(2L, 3L), Tuple.Create(4L, 5L) })).Text, "anchors need full line text but not EOL coverage");
            AssertEqual("observation_required", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("hidden\nx\nT", EditSet(Edit("x", "new", new[] { "hidden" })).WithCoverage(new[] { Tuple.Create(3L, 10L) }))).ErrorCode, "truncated line is not a delivered anchor");

            AssertEqual("a\r\nX\nZ\rc", Apply("a\r\nb\nc\rc", Edit("b\nc", "X\nZ")), "logical mixed EOL only changes selected fragment");
            AssertEqual("H\nH\n\nnew\nT", Apply("H\nH\n\nold\nT", Edit("old", "new", new[] { "H", "" }, new[] { "T" })), "multiple chains to one location, gaps and empty anchors");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => Apply("A\nB\nold", Edit("old", "x", new[] { "B", "A" }))).ErrorCode, "strict anchor order");
            AssertEqual("ambiguous_context", RuntimeThrows<TextPatternException>(() => Apply("x x", Edit("x", "y"))).ErrorCode, "never choose first occurrence");
            AssertEqual("overlap", RuntimeThrows<TextPatternException>(() => Apply("abc", Edit("ab", "x"), Edit("bc", "y"))).ErrorCode, "replacement overlap");
            AssertEqual("overlap", RuntimeThrows<TextPatternException>(() => Apply("a", Edit("a", "xa"), Edit("a", "ya"))).ErrorCode, "insertion collision");
            AssertEqual("\uFEFFheader\nbody", Apply("\uFEFFbody", Edit("body", "header\nbody")), "BOM outside start insertion");
            AssertEqual("body\nfooter", Apply("body", Edit("body", "body\nfooter")), "explicit EOF separator");
            AssertEqual("bodytail", Apply("body", Edit("body", "bodytail")), "no implicit separator");
            AssertEqual("a\r\nx\r\nb", Apply("a\r\nb", Edit("a\r\n", "a\r\nx\r\n")), "after includes anchor EOL");
            AssertEqual("a\nb", Apply("a\nx\nb", Edit("x\n", "")), "explicit line deletion");
            AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => Apply("\uFEFF", Edit("", "new"))).ErrorCode, "empty source needs core.write");
            AssertEqual("invalid_edit", RuntimeThrows<TextPatternException>(() => EditSet(Edit("x", "\uD800"))).ErrorCode, "invalid Unicode rejected");
            var range = new[] { Tuple.Create(2L, 3L) };
            AssertEqual("x y", TextEditPlan.Patch("x x", EditSet(Edit("x", "y")).WithCoverage(range)).Text, "unique within delivered coverage");
            AssertEqual("observation_required", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("H\nx", EditSet(Edit("x", "y", new[] { "H" })).WithCoverage(range))).ErrorCode, "undelivered anchor cannot authorize edit");
            AssertEqual("observation_required", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("x x", EditSet(Edit("x x", "x xy")).WithCoverage(range))).ErrorCode, "old cannot exceed delivered coverage");
            Func<string, string> vba = VbaTextCanonicalizer.NormalizeVbeComparableLine;
            AssertEqual("Sub Run()\r\n    Value = 2\r\nEnd Sub", TextEditPlan.Patch("Sub Run()\r\n    Value = 1\r\nEnd Sub",
                EditSet(Edit("value=1", "    Value = 2", new[] { "sub run()" })), normalizeLine: vba).Text, "canonical whole-line fallback");
            AssertEqual("Dim Other\ndim value", TextEditPlan.Patch("Dim Value\ndim value", EditSet(Edit("Dim Value", "Dim Other")), normalizeLine: vba).Text, "exact preferred");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("Rem Keep", EditSet(Edit("rem keep", "x")), normalizeLine: vba)).ErrorCode, "comments retain distinctions");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("Value = \"Keep\"", EditSet(Edit("value=\"keep\"", "x")), normalizeLine: vba)).ErrorCode, "string literals retain distinctions");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("Value = 1\r\nOther = 2", EditSet(Edit("value=1\nother=2", "x")), normalizeLine: vba)).ErrorCode, "canonical fallback never normalizes EOL");
            AssertEqual("context_not_found", RuntimeThrows<TextPatternException>(() => TextEditPlan.Patch("  Value = 1", EditSet(Edit("value", "x")), normalizeLine: vba)).ErrorCode, "substring has no canonical fallback");
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
                var largeHunks = EditSet(Edit("α😀 first", "α😀 FIRST"), Edit("last", "LAST"));
                var changed = files.ApplyTextEdits(workspace, "large.txt", exact, largeHunks, () => dispatched++, effectOperation: "core.patch");
                AssertEqual(1, dispatched, "multi-hunk patch dispatches once");
                AssertEqual(TextPatternEngine.Sha256(source.Replace("first", "FIRST").Replace("last", "LAST")), TextPatternEngine.Sha256(new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(workspace.RootPath, "large.txt")))), "UTF-8 content and CRLF remain exact except hunks");
                AssertTrue(changed.AuthorityCommit != null, "streamed read-back publishes authority");
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = "large-patch", Name = "core.patch", Arguments = new Dictionary<string, object> { ["targets"] = PatchTargets(PatchSection("large.txt", largeHunks))} },
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
                    EditSet(Edit("LAST", "stale")), () => dispatched++));
                AssertEqual(1, dispatched, "stale snapshot never dispatches");
                files.ApplyTextEdits(workspace, "large.txt", changed.Reference, EditSet(Edit("α😀 FIRST", "header\nα😀 FIRST")));
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
                var patch = executor.ExecuteManual(Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Universal", EditSet(Edit("debug.print 1", "Debug.Print 2", new[] { "sub run()" }, null))))), catalog, settings, false, true, session);
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
                AssertTrue(executor.ExecuteManual(Command("core.write", "target", "note: review.md", "content", "# Review"), catalog, settings, false, true, session).Success, "seed nonempty note for patch");
                var mixed = Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Klass", EditSet(Edit("Public Value As String", "Public Value As Boolean"))), PatchSection("HTML file: index.html", EditSet(Edit("<main>after</main>", "<main>verified</main>"))), PatchSection("note: review.md", EditSet(Edit("# Review", "# Review\n# Complete\n"))), PatchSection("file: report.txt", EditSet(Edit("before", "after")))));
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
                var duplicate = PrepareVbaNative(executor, session, Command("core.patch", "targets", PatchTargets(PatchSection("HTML file: index.html", EditSet(Edit("<main>verified</main>", "<main>changed</main>"))), PatchSection(exactHtml, EditSet(Edit("<main>verified</main>", "<main>other</main>")))))).Record;
                AssertTrue(duplicate.Outcome == ToolExecutionOutcome.Error && !duplicate.MayHaveDispatched, "different semantic aliases cannot change the same resource twice");
                AssertContains(duplicate.Result.DataJson, "Two target sections resolve", "duplicate rejection uses resolved identity");
                foreach (var stale in new[] { PatchSection(historicalHtml, LineHunk("<main>before</main>", "<main>stale</main>")), PatchSection(historicalNote, LineHunk("# Review", "# Stale")) })
                {
                    var refused = PrepareVbaNative(executor, session, Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Klass", LineHunk("Public Value As Boolean", "Public Value As Double")), stale))).Record;
                    AssertTrue(refused.Outcome == ToolExecutionOutcome.Error && !refused.MayHaveDispatched, "historical target rejects before writes");
                }

                var crossPending = PrepareVbaNative(executor, session, Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Klass", EditSet(Edit("Public Value As Boolean", "Public Value As Long"))), PatchSection("VBA module: Universal", EditSet(Edit("Sub Run()", "Sub RunChanged()"))))));
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
                Func<string, string, string, JArray> section = (path, before, after) =>
                    PatchTargets(PatchSection("HTML file: " + path, LineHunk(before, after)));
                write("index.html", "<main>one</main>");
                write("script.js", "let count = 1;");
                var patch = Command("core.patch", "targets", MergeTargets(section("index.html", "<main>one</main>", "<main>two</main>"), section("script.js", "let count = 1;", "let count = 2;")));
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

                var stale = PrepareVbaNative(executor, session, Command("core.patch", "targets",
                    MergeTargets(section("index.html", "<main>two</main>", "<main>three</main>"), section("script.js", "let count = 2;", "let count = 3;"))));
                write("script.js", "let count = 9;");
                var refused = ConfirmVbaNative(stale);
                AssertEqual(ToolExecutionOutcome.Error, refused.Outcome, "external change before confirmation rejects the whole package");
                AssertTrue(!refused.MayHaveDispatched, "preflight detects drift before the first write");
                AssertEqual("<main>two</main>", source("index.html"), "first member is unchanged after failed preflight");
                write("script.js", "let count = 2;");
                write("style.css", "body{}");
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print 1\nEnd Sub";
                var crossPatch = Command("core.patch", "targets",
                    MergeTargets(section("index.html", "<main>two</main>", "<main>three</main>"), PatchTargets(PatchSection("VBA module: Module1", LineHunk("Debug.Print 1", "Debug.Print 2"))), section("script.js", "let count = 2;", "let count = 3;"), section("style.css", "body{}", "body{color:red;}")));
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
                var unseen = prepare(Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Module1", EditSet(Edit("Debug.Print 1", "Debug.Print 2", new[] { "Debug.Print 1" }, new[] { "End Sub" }))))), evidence).Record;
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated line outside the observed window is not authority");
                var patch = prepare(Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Module1", EditSet(Edit("Debug.Print 1", "Debug.Print 2"))))), evidence);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, patch.Record.Outcome, "current retained excerpt authorizes its exact hunk: " + patch.Record.Result?.Message);
                AssertEqual(ToolExecutionOutcome.Ok, ConfirmVbaNative(patch).Outcome, "exact observed VBA hunk applies");
                ReadVbaSource(executor, session, "Module1");
                var deliveredExcerpt = executor.ResourceGateway.ReadLines(session, new ResourceRef(module.Reference.Uri), "source", 2, 1).Result;
                var staleExcerpt = executor.ResourceGateway.Evidence(session, deliveredExcerpt).ToArray();
                adapter.VbaModuleCode += "\n' outside the excerpt changed";
                var staleExcerptPatch = prepare(Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Module1", EditSet(Edit("Debug.Print 2", "Debug.Print 3"))))), staleExcerpt).Record;
                AssertTrue(staleExcerptPatch.Outcome == ToolExecutionOutcome.Error && !staleExcerptPatch.MayHaveDispatched, "equal excerpt bytes cannot bypass changed module authority");
                AssertContains(staleExcerptPatch.Result.DataJson, "source_changed", "stale excerpt has an authority conflict");
                var full = ReadVbaSource(executor, session, "Module1");
                var pending = prepare(Command("core.write", "target", "VBA module: Module1", "content", "Sub Run()\nEnd Sub"), executor.ResourceGateway.Evidence(session, full));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "whole source pins overwrite");
                adapter.VbaModuleCode += "\n' external change";
                var stale = ConfirmVbaNative(pending);
                AssertTrue(stale.Outcome == ToolExecutionOutcome.Error && !stale.MayHaveDispatched, "confirmation rechecks source without dispatch");
                AssertEqual(ToolRetryPolicy.RefreshRequired, stale.Recovery.RetryPolicy, "source drift requires a new delivered read");
            });
        }

        private static void UniversalVbaSequentialPatchKeepsCurrentSource()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) => {
                var session = NewSession(adapter);
                var original = new[] {
                    "r.m32 = a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32 + a.m34 * b.m42",
                    "r.m42 = a.m41 * b.m12 + a.m42 * b.m22 + a.m43 * b.m32 + a.m44 * b.m42",
                    "r.m43 = a.m41 * b.m13 + a.m42 * b.m23 + a.m43 * b.m33 + a.m44 * b.m43" };
                var edited = new[] { original[0].Replace("a.m31", "a.m32"),
                    original[1].Replace("a.m41", "a.m42"), original[2].Replace("a.m41", "a.m43") };
                var unchanged = string.Join("\n", Enumerable.Range(0, 300).Select(i => "' unchanged module comment " + i)) + "\n";
                Func<IEnumerable<string>, string> module = lines => unchanged + "Function Mat4Multiply(a, b)\n" +
                    string.Join("\n", lines) + "\nMat4Multiply = r\nEnd Function";
                adapter.SetVbaModule("Scene3D", module(original), "StdModule");
                var read = ReadVbaSource(executor, session, "Scene3D");
                var initialEvidence = executor.ResourceGateway.Evidence(session, read).ToList();
                Func<TextPatch, IEnumerable<ResourceEvidence>, VbaPendingExecution> prepare = (body, observed) => {
                    var command = Command("core.patch", "targets", PatchTargets(PatchSection("VBA module: Scene3D", body)));
                    command.ToolCallId = Guid.NewGuid().ToString("N");
                    var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                        Id = command.ToolCallId, Name = command.ToolId, Arguments = command.Arguments },
                        "Edit.", "Change these full lines.", null, ToolResultRoles.User, FixtureCallOrigin("run-vba-native:1"));
                    accepted.RunId = "run-vba-native"; accepted.ResourceEvidence = observed.ToList();
                    session.Messages.Add(accepted);
                    return PrepareVbaNative(executor, session, command);
                };
                Func<ToolExecutionRecord, ChatMessage> publish = record => {
                    var command = new ToolInvocation { ToolCallId = record.Context.Call.Id, ToolId = "core.patch" };
                    var result = new ToolResultMaterialization(record.Result, resourceEvidence: record.ResourceEvidence,
                        resourceEffect: record.ResourceEffect);
                    var message = AgentJsonProtocol.CreateToolResultMessage(command, result, int.MaxValue, ToolResultRoles.User);
                    message.RunId = record.Context.RunId;
                    ConversationModelSession.AddVerifiedSourceEvidence(session, executor.ResourceAuthority, executor.Payloads,
                        message, command, result);
                    session.Messages.Add(message);
                    return message;
                };
                Func<int, ModelContextSnapshot> request = budget => {
                    var scope = executor.ResourceAuthority.ScopeFor(session, initialEvidence[0].Resource, true);
                    var authority = new ModelAuthoritySnapshot(executor.ResourceAuthority.CaptureMany(new[] { scope }),
                        "tools", new SkillCatalogSnapshot(null), null, 1);
                    return new ModelContextCompiler(executor.Payloads, projection: ModelToolResultProjection.Instance)
                        .Compile(authority, new ChatMessage[0], session.Messages, null,
                            new ToolCatalogEntry[0], new AppSettings(), budget);
                };
                Func<ChatMessage> currentSource = () => request(12000).Messages.Single(m => m.SyntheticResourceObservation);
                var first = ConfirmVbaNative(prepare(new TextPatch(Enumerable.Range(0, 3)
                    .Select(i => Edit(original[i], edited[i]))), initialEvidence));
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "first patch is verified");
                publish(first);
                var afterFirst = currentSource();
                AssertContains(afterFirst.Content, module(edited), "next model request receives the full first result");
                var bounded = request(1500);
                AssertEqual(0, bounded.DeliveredObservations.Count, "omitted after-source is not delivered observation authority");
                var notice = string.Join("\n", bounded.Messages.Select(m => m.Content));
                AssertContains(notice, "VBA module: Scene3D", "budget omission keeps the exact semantic target");
                AssertContains(notice, "bodyIncluded\":false", "budget omission is explicit to the model");
                AssertContains(notice, "Read a narrower", "model is directed to read an affordable view before editing again");
                var unobserved = prepare(LineHunk(edited[0], original[0]), bounded.DeliveredObservations).Record;
                AssertTrue(unobserved.Outcome == ToolExecutionOutcome.Error && !unobserved.MayHaveDispatched,
                    "a model that did not receive the updated body must read before its next patch");
                publish(unobserved);
                foreach (var unknown in new[] { false, true })
                {
                    var command = new ToolInvocation { ToolCallId = first.Context.Call.Id, ToolId = "core.patch" };
                    var targets = new[] { first.ResourceEffect.Targets[0], new ResourceTargetEffect {
                        Target = "VBA module: Later", Status = unknown ? ResourceTargetStatus.Unknown : ResourceTargetStatus.Rejected } };
                    var effect = new ResourceEffect("partial", "core.patch", unknown ? ResourceEffectOutcome.UnknownAfterDispatch : ResourceEffectOutcome.VerifiedChanged,
                        first.ResourceEffect.Impacts, targets: targets);
                    var data = Newtonsoft.Json.JsonConvert.SerializeObject(new PatchBatchResult { Phase = "execution", Partial = true, WriteStarted = true,
                        Targets = targets.Select(t => new PatchTargetResult { Target = t.Target, Status = t.Status }).ToList() });
                    var result = new ToolResultMaterialization(unknown ? ToolResult.Unknown("Stopped", data) : ToolResult.Error("Stopped", data), resourceEffect: effect);
                    var message = AgentJsonProtocol.CreateToolResultMessage(command, result, int.MaxValue, ToolResultRoles.User);
                    message.RunId = first.Context.RunId;
                    ConversationModelSession.AddVerifiedSourceEvidence(session, executor.ResourceAuthority, executor.Payloads, message, command, result);
                    AssertEqual(1, message.ResourceEvidence.Count, "later refusal/unknown retains only the verified source");
                    var scope = executor.ResourceAuthority.ScopeFor(session, initialEvidence[0].Resource, true);
                    var authority = new ModelAuthoritySnapshot(executor.ResourceAuthority.CaptureMany(new[] { scope }),
                        "tools", new SkillCatalogSnapshot(null), null, 1);
                    var call = session.Messages.First(m => m.ToolCallId == command.ToolCallId);
                    var archive = new ChatSession { Messages = new List<ChatMessage> { call, message } };
                    SetContinuityCheckpoint(archive, message);
                    var active = new[] { new ChatMessage { Role = "user", Content = "Continue after checking the failed target." } };
                    var compiled = new ModelContextCompiler(executor.Payloads, projection: ModelToolResultProjection.Instance)
                        .Compile(authority, new ChatMessage[0], active, null, new ToolCatalogEntry[0], new AppSettings(), 12000,
                            workingSet: ContextWorkingSet.Restore(archive, active, 6000));
                    var retained = compiled.Messages.SingleOrDefault(m => m.SyntheticResourceObservation);
                    AssertTrue(retained != null, "compaction retains a verified source from a partial " + (unknown ? "unknown" : "error") + " result");
                    AssertContains(retained.Content, module(edited),
                        "compaction cannot hide a confirmed source behind another target's failure");
                }
                var second = ConfirmVbaNative(prepare(MergeEdits(LineHunk(edited[0], original[0]), LineHunk(edited[2], original[2])),
                    afterFirst.ResourceEvidence));
                AssertEqual(ToolExecutionOutcome.Ok, second.Outcome, "explicit reverse hunks apply to current source");
                publish(second);
                var expected = module(new[] { original[0], edited[1], original[2] });
                AssertEqual(expected, adapter.GetVbaModuleCode("Scene3D"), "second patch preserves the unmentioned first edit");
                var afterSecond = currentSource();
                AssertContains(afterSecond.Content, expected, "current source supersedes the previous source in model context");
                var beforeSecond = PatchChangesProjection.ReadSource(executor.ResourceAuthority, second.ResourceEffect.Targets[0].Source, 10000);
                AssertEqual(module(edited), beforeSecond, "second diff before is the actual first read-back");
                var stale = prepare(LineHunk(original[1], edited[1]), initialEvidence).Record;
                AssertTrue(stale.Outcome == ToolExecutionOutcome.Error && !stale.MayHaveDispatched,
                    "old observed snapshot cannot overwrite newer code");
                AssertContains(stale.Result.DataJson, "source_changed", "old observation requires a fresh read");
                var repeated = prepare(LineHunk(edited[0], original[0]), afterSecond.ResourceEvidence).Record;
                AssertTrue(repeated.Outcome == ToolExecutionOutcome.Error && !repeated.MayHaveDispatched,
                    "already consumed minus lines cannot silently match old content");
                AssertContains(repeated.Result.DataJson, "context_not_found", "repeat is a context conflict");
                AssertEqual(expected, adapter.GetVbaModuleCode("Scene3D"), "both rejections leave current code intact");
            });
        }

        private static void UniversalPatchBatchesPreservePerTargetFacts()
        {
            foreach (var scenario in new[] { "success", "invalid", "alias", "stale", "partial", "unknown", "guard_throw" })
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
                    var runtime = new ToolRuntime(registry, "agent", false, true, (ctx, prep) => "approval",
                        dispatchGuard: ctx => { if (scenario == "guard_throw" && ctx.TargetOrdinal == 1) throw new InvalidOperationException("Guard failed before dispatch."); return null; });
                    var second = scenario == "alias" ? "a.txt" : "b.txt";
                    var body = PatchTargets(PatchSection("a.txt", EditSet(Edit("a.txt old", "a.txt new"))), PatchSection(second, EditSet(Edit((scenario == "alias" ? "a.txt" : "b.txt") + (scenario == "invalid" ? " missing" : " old"), (scenario == "success" ? "b.txt old" : "b.txt new")))), PatchSection("c.txt", EditSet(Edit("c.txt old", "c.txt new"))));
                    var call = new ToolCall("batch", entry.Id, new JObject { ["targets"] = body }.ToString(Newtonsoft.Json.Formatting.None));
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
                    var audit = Environment.GetEnvironmentVariable("RNA_PATCH_RESULT_AUDIT");
                    if (!string.IsNullOrEmpty(audit)) { Directory.CreateDirectory(audit); File.WriteAllText(Path.Combine(audit, scenario + ".json"), completed.Result.DataJson); }
                    var result = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(completed.Result.DataJson);
                    AssertEqual(3, result.Targets.Count, scenario + " lists every target");
                    if (scenario == "invalid" || scenario == "alias" || scenario == "stale")
                    {
                        AssertTrue(!completed.MayHaveDispatched && !result.WriteStarted, "all-or-reject preparation has no write");
                        AssertContains(completed.Result.Message, "No target in this call changed",
                            "preparation refusal explicitly denies an applied edit");
                        AssertEqual("a.txt old\r\n", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "first prepared target is untouched");
                        AssertEqual(scenario == "invalid" ? "context_not_found" : scenario == "alias" ? "invalid_edit" : "source_changed",
                            result.Targets.Single(t => t.Diagnostic != null).Diagnostic.Code, "typed preparation reason");
                        if (scenario == "invalid")
                        {
                            AssertEqual(1, result.Targets[1].Diagnostic.Edit.Value, "diagnostic hunk is local to its target");
                            AssertEqual("read_target", result.Targets[1].Diagnostic.NextAction,
                                "missing context directs the model to refresh the rejected target");
                            var acceptedFailure = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                                Id = call.Id, Name = call.Name, Arguments = new Dictionary<string, object> { ["targets"] = body } },
                                "", "Attempt the requested edit.", null, ToolResultRoles.User, FixtureCallOrigin());
                            var terminalFailure = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation {
                                ToolCallId = call.Id, ToolId = call.Name }, completed.Result, ToolResultRoles.User);
                            acceptedFailure.RunId = terminalFailure.RunId = "run";
                            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                                "pack", new SkillCatalogSnapshot(null), null, 1);
                            var nextRequest = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(
                                authority, new ChatMessage[0], new[] { acceptedFailure, terminalFailure }, null,
                                new ToolCatalogEntry[0], new AppSettings(), 10000);
                            var pair = nextRequest.Messages.Where(message => message.ToolCallId == call.Id).ToList();
                            AssertEqual(2, pair.Count, "next model request keeps accepted call and terminal result together");
                            AssertEqual("assistant", pair[0].Role, "accepted call precedes result");
                            RNAssistant.Core.ModelProtocol.ToolResultWireReadResult failedWire; string failedError;
                            AssertTrue(ToolResultHistoryReader.TryRead(pair[1], out failedWire, out failedError),
                                "next request carries a readable result for the failed patch");
                            AssertEqual(call.Id, failedWire.ToolCallId, "failed result is linked to the exact call");
                            AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, failedWire.Result.Status,
                                "preparation failure remains error in model context");
                            var visibleFailure = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(failedWire.Result.DataJson);
                            AssertEqual("preparation", visibleFailure.Phase, "model sees rejection before any write");
                            AssertTrue(visibleFailure.Targets.Select(target => target.Status).SequenceEqual(new[] {
                                ResourceTargetStatus.NotStarted, ResourceTargetStatus.Rejected, ResourceTargetStatus.NotStarted }),
                                "model sees every unapplied target and the rejected hunk");
                            var missingMarker = new ToolCall("missing-marker", entry.Id, new JObject {
                                ["targets"] = "*** Update Target: a.txt\n@@\n    a.txt new\n"
                            }.ToString(Newtonsoft.Json.Formatting.None));
                            var syntaxRefusal = runtime.ExecuteAsync(new ToolExecutionContext(missingMarker,
                                runtime.Describe(missingMarker), "run", "turn", "syntax", DateTime.UtcNow, false, 1),
                                CancellationToken.None).GetAwaiter().GetResult();
                            AssertEqual(ToolExecutionOutcome.Error, syntaxRefusal.Outcome,
                                "missing + marker rejects before dispatch");
                            AssertTrue(!syntaxRefusal.MayHaveDispatched, "invalid structured arguments never dispatch");
                            AssertContains(syntaxRefusal.Result.Message, "targets", "schema error identifies malformed field");
                        }
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
                        Arguments = new Dictionary<string, object> { ["targets"] = body } }, "Edit.", "Related edits.", null, ToolResultRoles.User, FixtureCallOrigin());
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.Id, ToolId = call.Name }, completed.Result, ToolResultRoles.User);
                    accepted.RunId = terminal.RunId = "run"; terminal.ResourceEffect = completed.ResourceEffect;
                    terminal.ResourceEvidence = completed.ResourceEvidence.ToList();
                    AssertEqual("a.txt", ModelContextCompiler.CurrentSourceLabel(terminal, completed.ResourceEvidence.First()), "verified after-source keeps its semantic target even after a later failure");
                    var session = new ChatSession { Messages = new List<ChatMessage> { accepted, terminal } };
                    session = Newtonsoft.Json.JsonConvert.DeserializeObject<ChatSession>(Newtonsoft.Json.JsonConvert.SerializeObject(session));
                    var modelMessage = ModelToolResultProjection.Project(terminal);
                    RNAssistant.Core.ModelProtocol.ToolResultWireReadResult wire; string readError;
                    AssertTrue(ToolResultHistoryReader.TryRead(modelMessage, out wire, out readError), "model result remains typed");
                    AssertEqual(call.Id, wire.ToolCallId, "partial result remains linked to the exact call");
                    var visible = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(wire.Result.DataJson);
                    AssertTrue(visible.Targets.Select(t => t.Status).SequenceEqual(expected), "model receives all per-target statuses");
                    AssertTrue(JToken.DeepEquals(JArray.FromObject(result.Targets.Select(t => t.Diagnostic)), JArray.FromObject(visible.Targets.Select(t => t.Diagnostic))), "serialized and model diagnostics agree");
                    if (scenario == "unknown")
                    {
                        AssertEqual("verify_effect", visible.Targets[1].Diagnostic.NextAction, "unknown requires reconciliation, never replay");
                        AssertEqual("owner_error", visible.Targets[1].Diagnostic.Code, "unknown reuses the owner error category");
                    }
                    var compiled = new ModelContextCompiler(blobs, projection: ModelToolResultProjection.Instance).Compile(
                        new ModelAuthoritySnapshot(files.CaptureAuthority(completed.ResourceEvidence), "pack", new SkillCatalogSnapshot(null), null, 1),
                        new ChatMessage[0], new[] { accepted, terminal }, null, new ToolCatalogEntry[0], new AppSettings(), 16000);
                    var nextResult = compiled.Messages.Single(m => m.ToolCallId == call.Id && m.Role != "assistant");
                    RNAssistant.Core.ModelProtocol.ToolResultWireReadResult sent; string sentError;
                    AssertTrue(ToolResultHistoryReader.TryRead(nextResult, out sent, out sentError), "actual next request has a terminal result");
                    AssertEqual(completed.Result.Status, sent.Result.Status, "actual next request preserves the top-level effect status");
                    if (scenario == "guard_throw") AssertTrue(!result.Targets[1].WriteStarted && result.Targets[1].Status == ResourceTargetStatus.Rejected, "earlier dispatch does not turn a later guard exception into unknown");
                    if (scenario == "partial") AssertContains(wire.Result.Message, "Rejected/NotStarted were not applied",
                        "partial result distinguishes successful and unapplied targets in model context");
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
                            new ToolCounts(writeError: scenario == "partial" || scenario == "guard_throw" ? 1 : 0, writeUnknown: scenario == "unknown" ? 1 : 0),
                            1, 1, "Stopped.", "model_finish", null)).LastRun;
                        session.Messages.Add(EffectMessage("run", call.Id, completed.Evidence.Effect));
                        var runState = RunViewStateProjector.Create(session);
                        AssertEqual(1, runState.VerifiedWrites, "run summary retains confirmed changes in a partial package");
                        AssertEqual(scenario == "unknown" ? RunViewHealth.Unknown : RunViewHealth.Errors, runState.ExecutionHealth, "a known partial failure is not a new unknown effect");
                    }
                    if (scenario == "unknown") AssertTrue(view.Items[1].After == null && view.Items[1].IntendedAfter.Contains("b.txt new"), "unknown only shows intended comparison");
                });
        }

        private static void UniversalPatchRequiresExactAfterState()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "missing-after"));
                File.WriteAllText(Path.Combine(workspace.RootPath, "a.txt"), "old\n");
                var source = new WorkspaceFileService(paths).ReadText(workspace, "a.txt").Evidence.Resource;
                var entry = ResourceMutationToolCatalog.GetTools().Single(t => t.Id == ResourceMutationToolCatalog.Patch);
                var owner = new RuntimePreparableHandler {
                    Prepare = context => new ToolPreparationResult(ToolResult.Ok("Prepared"), "{\"guard\":1}",
                        patchTarget: new PatchTargetPlan { Source = source, SourceHash = "pinned",
                            EffectIdentity = source.Identity, Preview = new List<PatchPreview> {
                                new PatchPreview { Before = "old", After = "new" } } })
                };
                var registry = new ToolHandlerRegistry();
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description,
                    entry.ArgumentSchemaJson), entry.Policy, entry.Binding), new ResourcePatchBatchHandler(owner));
                var runtime = new ToolRuntime(registry, "agent", false, true, (ctx, prep) => "approval");
                var call = new ToolCall("missing-after", entry.Id, new JObject {
                    ["targets"] = PatchTargets(PatchSection("a.txt", EditSet(Edit("old", "new"))))
                }.ToString(Newtonsoft.Json.Formatting.None));
                var pending = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call),
                    "run", "turn", "step", DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, "package prepares before dispatch");
                var completed = runtime.ExecuteAsync(new ToolExecutionContext(call, pending.Context.Policy,
                    "run", "turn", "step", DateTime.UtcNow, true, 1, pending.PreparedStateJson),
                    CancellationToken.None).GetAwaiter().GetResult();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<PatchBatchResult>(completed.Result.DataJson);
                AssertEqual(ToolExecutionOutcome.Unknown, completed.Outcome,
                    "a changed owner result without the target after-state cannot become patch success");
                AssertEqual(ResourceTargetStatus.Unknown, result.Targets.Single().Status,
                    "target status records unresolved publication");
                AssertEqual<ResourceRef>(null, completed.ResourceEffect.Targets.Single().After,
                    "no unrelated result resource substitutes for the missing after-state");
                AssertEqual(ResourceEffectOutcome.UnknownAfterDispatch, completed.ResourceEffect.Outcome,
                    "the aggregate effect stays uncertain");
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
                var unseen = prepare("core.patch", PatchArguments("a.txt", EditSet(Edit("repeat", "unseen", new[] { "repeat" }, null))));
                AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome, "repeated text in an excerpt cannot authorize a different unseen coordinate");
                AssertEqual(ToolRetryPolicy.RefreshRequired, unseen.Recovery.RetryPolicy, "a subsequent relevant read can repair excerpt coverage");
                observed["a.txt"] = whole;
                apply("core.patch", PatchArguments("a.txt", EditSet(Edit("repeat", "second", new[] { "repeat" }, null))));
                AssertEqual("first\nrepeat\nsecond", File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), "coordinate patch selects original repeated line");
                apply("resources.copy", new JObject { ["target"] = "a.txt", ["destination"] = "b.txt" });
                apply("resources.move", new JObject { ["target"] = "b.txt", ["destination"] = "c.txt" });
                apply("resources.delete", new JObject { ["target"] = "c.txt" });
                apply("resources.restore", new JObject { ["target"] = "c.txt" });
                AssertEqual(File.ReadAllText(Path.Combine(workspace.RootPath, "a.txt")), File.ReadAllText(Path.Combine(workspace.RootPath, "c.txt")), "copy/move/delete/restore retain exact bytes");
                apply("core.write", new JObject { ["target"] = "history.txt", ["content"] = "before" });
                var historyBefore = observed["history.txt"];
                var historyPatch = PatchArguments("history.txt", EditSet(Edit("before", "after")));
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
                var uncertain = prepare("core.patch", PatchArguments("a.txt", EditSet(Edit("first", "written"))));
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
