using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RunChangesRetainedSources()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            var baseline = new ChatArtifact { Id = "md-0", Kind = ChatArtifactKinds.Markdown, Title = "Notes.md", InlineText = "before" };
            var intermediate = new ChatArtifact { Id = "md-1", ParentArtifactId = baseline.Id, RunId = "run", Kind = baseline.Kind, Title = baseline.Title, InlineText = "intermediate" };
            var final = new ChatArtifact { Id = "md-2", ParentArtifactId = intermediate.Id, RunId = "run", Kind = baseline.Kind, Title = baseline.Title, InlineText = "after" };
            var workspaceBefore = new HtmlWorkspaceSnapshot { Files = new List<HtmlWorkspaceFile> {
                new HtmlWorkspaceFile { Id = "html", Path = "index.html", Content = "<h1>Old</h1>" },
                new HtmlWorkspaceFile { Id = "js", Path = "old.js", Content = "alert(1);" },
                new HtmlWorkspaceFile { Id = "removed", Path = "style.css", Content = "body {}" } } };
            var workspaceAfter = new HtmlWorkspaceSnapshot { Files = new List<HtmlWorkspaceFile> {
                new HtmlWorkspaceFile { Id = "html", Path = "index.html", Content = "<h1>New</h1>" },
                new HtmlWorkspaceFile { Id = "js", Path = "new.js", Content = "alert(1);" },
                new HtmlWorkspaceFile { Id = "md", Path = "README.md", Content = "# Hi" } } };
            var html0 = new ChatArtifact { Id = "html-0", Kind = ChatArtifactKinds.HtmlWorkspace, InlineText = JsonConvert.SerializeObject(workspaceBefore) };
            var html1 = new ChatArtifact { Id = "html-1", Kind = html0.Kind, ParentArtifactId = html0.Id, RunId = "run", InlineText = JsonConvert.SerializeObject(workspaceAfter) };
            session.Artifacts.AddRange(new[] { baseline, intermediate, final, html0, html1,
                new ChatArtifact { Id = "upload", Kind = ChatArtifactKinds.Attachment, MimeType = "text/plain", RunId = "run", InlineText = "read-only input" },
                new ChatArtifact { Id = "tool", Kind = ChatArtifactKinds.ToolResult, RunId = "run", InlineText = "not source" },
                new ChatArtifact { Id = "missing", ParentArtifactId = "absent", Kind = ChatArtifactKinds.Markdown, Title = "Missing.md", RunId = "run", InlineText = "after" } });
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                store.Save(session);
                var loaded = store.Load(session.Host, session.DocumentKey, session.Id);
                var stored = loaded.Artifacts.Single(a => a.Id == final.Id);
                AssertEqual("after", RunChangesService.ReadRetainedSource(loaded, stored, store.LoadArtifactBody, null),
                    "production loader reads exact persisted CAS source");
                stored.InlineText = "tampered";
                var rejected = false;
                try { RunChangesService.ReadRetainedSource(loaded, stored, store.LoadArtifactBody, null); }
                catch (InvalidOperationException) { rejected = true; }
                AssertTrue(rejected, "modified in-memory text cannot masquerade as retained evidence");
            });
            var result = new RunChangesService(a => a.InlineText, null, null).Read(session, "run");
            AssertEqual(6, result.Items.Count, "one net markdown, four workspace members and explicit missing baseline");
            var md = result.Items.Single(i => i.Id == final.Id);
            AssertEqual("before", md.Before, "first retained parent baseline");
            AssertEqual("after", md.After, "last authored source");
            var rename = result.Items.Single(i => i.Title == "new.js");
            AssertEqual("old.js", rename.BeforeTitle, "rename follows member id, not filename");
            AssertEqual(rename.Before, rename.After, "rename has no fabricated line change");
            AssertTrue(!result.Items.Single(i => i.Title == "style.css").AfterExists, "deletion is explicit");
            AssertEqual("unavailable", result.Items.Single(i => i.Id == "missing").Availability, "missing baseline is not empty file");
        }

        private static void RunChangesVbaEvidence()
        {
            WithTempPaths(paths =>
            {
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                session.Host = "Excel"; session.DocumentKey = "doc";
                var journal = new VbaJournalStore(paths);
                Action<string, string, string, bool> write = (module, before, after, verified) => {
                    var prepared = journal.PrepareMutation(new VbaMutationPreparation {
                        Operation = "write", Host = session.Host, DocumentKey = session.DocumentKey,
                        SessionId = session.Id, RunId = "run", ToolCallId = "call-" + module + after, ModuleName = module, ComponentType = "StdModule",
                        BeforeExists = true, IntendedAfterExists = true }, before, after);
                    if (verified) journal.CompleteMutation(session.Host, session.DocumentKey, prepared.MutationId,
                        VbaMutationStatuses.Committed, true, prepared.IntendedAfterCodeSha256,
                        prepared.IntendedAfterComparableCodeSha256, null, null);
                };
                write("Module1", "Sub A()\nEnd Sub", "Sub B()\nEnd Sub", true);
                write("Module1", "Sub B()\nEnd Sub", "Sub C()\nEnd Sub", true);
                write("Noop", "Sub Same()\nEnd Sub", "Sub Same()\nEnd Sub", true);
                write("Unknown", "Sub Before()\nEnd Sub", "Sub Intended()\nEnd Sub", false);
                // Re-open the journal: UI compares retained evidence, never live Office or model output.
                var reopened = new VbaJournalStore(paths);
                var firstRow = reopened.QueryMutations(session.Host, session.DocumentKey,
                    new VbaMutationQueryRequest { RunId = "run" }).Rows.First();
                var bounded = false;
                try { reopened.GetMutationDetail(session.Host, session.DocumentKey, firstRow.MutationId, 2); }
                catch (VbaJournalException) { bounded = true; }
                AssertTrue(bounded, "bounded journal comparison refuses oversized source before publishing text");
                var result = new RunChangesService(a => a.InlineText,
                    query => reopened.QueryMutations(session.Host, session.DocumentKey, query),
                    id => reopened.GetMutationDetail(session.Host, session.DocumentKey, id)).Read(session, "run");
                AssertEqual(2, result.Items.Count, "net module change plus unknown; no-op omitted");
                var changed = result.Items.Single(i => i.Title == "Module1");
                AssertContains(changed.Before, "Sub A()", "first durable baseline");
                AssertContains(changed.After, "Sub C()", "exact read-back matched after");
                var unknown = result.Items.Single(i => i.Title == "Unknown");
                AssertEqual("unverified", unknown.Availability, "open preparation cannot prove intended source");
                AssertTrue(unknown.After == null, "never publish intended code as actual unknown effect");
                AssertContains(unknown.Before, "Sub Before()", "preview retains actual baseline");
                AssertContains(unknown.IntendedAfter, "Sub Intended()", "unverified planned source remains inspectable");
                AssertEqual(true, unknown.IntendedAfterExists, "intended existence is separate from actual result");
                var moduleRows = reopened.QueryMutations(session.Host, session.DocumentKey,
                    new VbaMutationQueryRequest { RunId = "run", Search = "Module1" }).Rows;
                foreach (var row in moduleRows) session.Messages.Add(new ChatMessage {
                    ProtocolMessage = true, Role = "tool", ToolCallId = row.ToolCallId, RunId = "confirmed" });
                var continued = new RunChangesService(a => a.InlineText,
                    query => reopened.QueryMutations(session.Host, session.DocumentKey, query),
                    id => reopened.GetMutationDetail(session.Host, session.DocumentKey, id)).Read(session, "confirmed");
                AssertEqual(1, continued.Items.Count, "confirmation run finds original journal run through exact accepted call correlation");
                AssertContains(continued.Items[0].Before, "Sub A()", "confirmation preserves earliest baseline");
                var renamed = new RunChangesService(a => a.InlineText,
                    query => new VbaMutationQueryPage { Rows = new List<VbaMutationQueryRow> {
                        new VbaMutationQueryRow { MutationId = "rename", SessionId = session.Id } } },
                    id => new VbaMutationDetail { Operation = "rename", Components = new List<VbaMutationComponentDetail> {
                        new VbaMutationComponentDetail { ModuleName = "Old", BeforeExists = true, BeforeCode = "Sub A()\nEnd Sub", ActualExists = false },
                        new VbaMutationComponentDetail { ModuleName = "New", IntendedAfterExists = true,
                            IntendedAfterCode = "Sub A()\nEnd Sub", ActualExists = true,
                            IntendedAfterCodeSha256 = "exact", ActualCodeSha256 = "exact" } } }).Read(session, "run");
                AssertEqual(1, renamed.Items.Count, "identity-preserving VBA rename is one entry");
                AssertEqual("Old", renamed.Items[0].BeforeTitle, "old module name retained");
                AssertEqual("New", renamed.Items[0].Title, "new module name retained");
                AssertEqual(renamed.Items[0].Before, renamed.Items[0].After, "rename does not fabricate source edits");
                session.Id = "another-chat";
                AssertEqual(0, new RunChangesService(a => a.InlineText,
                    query => reopened.QueryMutations(session.Host, session.DocumentKey, query),
                    id => reopened.GetMutationDetail(session.Host, session.DocumentKey, id)).Read(session, "run").Items.Count,
                    "journal rows are scoped to exact chat as well as run");
            });
        }
        private static void RunChangesIntendedPreviews()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            Func<VbaMutationComponentDetail[], RNAssistant.Office.Contracts.RunChangesDto> project = components =>
                new RunChangesService(a => a.InlineText,
                    query => new VbaMutationQueryPage { Rows = components.Select((c, i) =>
                        new VbaMutationQueryRow { MutationId = i.ToString(), SessionId = session.Id, FirstSequence = i }).ToList() },
                    id => new VbaMutationDetail { Components = new List<VbaMutationComponentDetail> { components[int.Parse(id)] } })
                    .Read(session, "run");
            var result = project(new[] {
                new VbaMutationComponentDetail { ModuleName = "Chain", BeforeExists = true, BeforeCode = "a",
                    IntendedAfterExists = true, IntendedAfterCode = "b", IntendedAfterCodeSha256 = "b", ActualExists = true, ActualCodeSha256 = "b" },
                new VbaMutationComponentDetail { ModuleName = "Chain", BeforeExists = true, BeforeCode = "b",
                    IntendedAfterExists = true, IntendedAfterCode = "c" },
                new VbaMutationComponentDetail { ModuleName = "Chain", BeforeExists = true, BeforeCode = "c",
                    IntendedAfterExists = true, IntendedAfterCode = "d", IntendedAfterCodeSha256 = "d", ActualExists = true, ActualCodeSha256 = "d" },
                new VbaMutationComponentDetail { ModuleName = "Formatted", BeforeExists = true, BeforeCode = "before",
                    IntendedAfterExists = true, IntendedAfterCode = "planned", IntendedAfterCodeSha256 = "planned",
                    ActualExists = true, ActualCodeSha256 = "formatted", MatchesIntendedAfter = true },
                new VbaMutationComponentDetail { ModuleName = "Create", IntendedAfterExists = true, IntendedAfterCode = "new" },
                new VbaMutationComponentDetail { ModuleName = "Delete", BeforeExists = true, BeforeCode = "old" },
                new VbaMutationComponentDetail { ModuleName = "Missing", BeforeExists = true, IntendedAfterExists = true, IntendedAfterCode = "new" }
            });
            AssertEqual(7, result.Items.Count, "unknown interrupts net comparison rather than joining verified chains");
            AssertEqual("a", result.Items[0].Before, "verified first baseline preserved");
            AssertEqual("b", result.Items[0].After, "preview is not appended to verified result");
            AssertEqual("c", result.Items[1].IntendedAfter, "interrupted step has its own plan");
            AssertEqual("c", result.Items[2].Before, "later verified chain starts separately");
            AssertEqual("unverified", result.Items[3].Availability, "comparable-only verification remains explicitly unverified as exact text");
            AssertEqual("planned", result.Items[3].IntendedAfter, "formatting mismatch keeps planned diff");
            AssertEqual("", result.Items[4].Before, "creation has known absent baseline");
            AssertEqual("", result.Items[5].IntendedAfter, "deletion has known absent intended source");
            AssertTrue(result.Items[6].Before == null && result.Items[6].IntendedAfter == null,
                "missing source cannot be replaced with empty text");
            var bounded = project(Enumerable.Range(0, 3).Select(i => new VbaMutationComponentDetail {
                ModuleName = "Large" + i, BeforeExists = true, BeforeCode = new string('a', RunChangesService.MaximumSourceCharacters),
                IntendedAfterExists = true, IntendedAfterCode = new string('b', RunChangesService.MaximumSourceCharacters) }).ToArray());
            AssertTrue(bounded.Items[2].Before == null && bounded.Items[2].IntendedAfter == null,
                "planned source counts toward shared response character budget");
            AssertEqual("unverified", bounded.Items[2].Availability, "budget limit does not erase effect uncertainty");
            var renamed = new RunChangesService(a => a.InlineText,
                query => new VbaMutationQueryPage { Rows = new List<VbaMutationQueryRow> {
                    new VbaMutationQueryRow { MutationId = "rename", SessionId = session.Id } } },
                id => new VbaMutationDetail { Operation = "rename", Components = new List<VbaMutationComponentDetail> {
                    new VbaMutationComponentDetail { ModuleName = "Old", BeforeExists = true, BeforeCode = "code" },
                    new VbaMutationComponentDetail { ModuleName = "New", IntendedAfterExists = true, IntendedAfterCode = "code" } } })
                .Read(session, "run");
            AssertEqual(1, renamed.Items.Count, "planned rename remains one identity-preserving comparison");
            AssertEqual("Old", renamed.Items[0].BeforeTitle, "planned old name");
            AssertEqual("New", renamed.Items[0].Title, "planned new name");
            AssertEqual(renamed.Items[0].Before, renamed.Items[0].IntendedAfter, "planned rename has no fabricated line churn");
            AssertTrue(renamed.Items[0].After == null, "planned rename is never actual after source");
        }
    }
}
