using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ToolPresentationCallChanges()
        {
            WithTempPaths(paths =>
            {
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                session.Host = "Excel"; session.DocumentKey = "doc";
                var journal = new VbaJournalStore(paths);
                Action<string, string, string, bool> write = (call, before, after, verified) => {
                    var prepared = journal.PrepareMutation(new VbaMutationPreparation { Operation = "write", Host = session.Host,
                        DocumentKey = session.DocumentKey, SessionId = session.Id, RunId = "original", ToolCallId = call,
                        ModuleName = "Module1", ComponentType = "StdModule", BeforeExists = true, IntendedAfterExists = true }, before, after);
                    if (verified) journal.CompleteMutation(session.Host, session.DocumentKey, prepared.MutationId,
                        VbaMutationStatuses.Committed, true, prepared.IntendedAfterCodeSha256, prepared.IntendedAfterComparableCodeSha256, null, null);
                    session.Messages.Add(new ChatMessage { RunId = "run", ProtocolMessage = true, Role = "tool", ToolCallId = call });
                    session.Messages.Add(new ChatMessage { RunId = "run", Activity = new ChatActivity { ToolCallId = call,
                        ExecutionEvidence = new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched,
                            verified ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.Unknown) } });
                };
                write("one", "Sub A()\nEnd Sub", "Sub B()\nEnd Sub", true);
                write("two", "Sub B()\nEnd Sub", "Sub C()\nEnd Sub", true);
                write("noop", "Sub C()\nEnd Sub", "Sub C()\nEnd Sub", true);
                write("unknown", "Sub C()\nEnd Sub", "Sub D()\nEnd Sub", false);
                var reopened = new VbaJournalStore(paths);
                var changes = new RunChangesService(a => a.InlineText,
                    q => reopened.QueryMutations(session.Host, session.DocumentKey, q),
                    id => reopened.GetMutationDetail(session.Host, session.DocumentKey, id));
                var service = new ToolResultPresentationService(call => changes.Read(session, "run", call));
                var first = ((ToolChangesBlockDto)service.Read(session, "run", "one").Blocks.Single()).Changes.Items.Single();
                var second = ((ToolChangesBlockDto)service.Read(session, "run", "two").Blocks.Single()).Changes.Items.Single();
                AssertContains(first.Before, "Sub A()", "first call baseline survives reopened history and confirmation correlation");
                AssertContains(first.After, "Sub B()", "first call never absorbs sibling change");
                AssertContains(second.Before, "Sub B()", "second call starts from its own baseline");
                AssertContains(second.After, "Sub C()", "second call own terminal source");
                var noop = ((ToolChangesBlockDto)service.Read(session, "run", "noop").Blocks.Single()).Changes;
                AssertTrue(noop.EvidenceFound && noop.Items.Count == 0, "verified no-op differs from missing comparison");
                var unknown = ((ToolChangesBlockDto)service.Read(session, "run", "unknown").Blocks.Single()).Changes.Items.Single();
                AssertEqual("unverified", unknown.Availability, "open preparation never claims an applied diff");
                AssertTrue(unknown.After == null, "planned text never supplies actual after");
                bool rejected = false;
                try { service.Read(session, "another-run", "one"); } catch (InvalidOperationException) { rejected = true; }
                AssertTrue(rejected, "foreign run rejected before source reads");
                session.Messages.Add(new ChatMessage { RunId = "run", Activity = new ChatActivity { ToolCallId = "one" } });
                rejected = false;
                try { service.Read(session, "run", "one"); } catch (InvalidOperationException) { rejected = true; }
                AssertTrue(rejected, "ambiguous activity cannot be guessed");
            });
        }

        private static void ToolPresentationArtifactCorrelation()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Messages.Add(new ChatMessage { Id = "m1", RunId = "run", ToolCallId = "one" });
            session.Messages.Add(new ChatMessage { Id = "m2", RunId = "run", ToolCallId = "two" });
            session.Artifacts.AddRange(new[] {
                new ChatArtifact { Id = "a", Kind = ChatArtifactKinds.Markdown, InlineText = "A" },
                new ChatArtifact { Id = "b", Kind = ChatArtifactKinds.Markdown, ParentArtifactId = "a", RunId = "run", SourceMessageId = "m1", InlineText = "B" },
                new ChatArtifact { Id = "c", Kind = ChatArtifactKinds.Markdown, ParentArtifactId = "b", RunId = "run", SourceMessageId = "m2", InlineText = "C" },
                new ChatArtifact { Id = "unlinked", Kind = ChatArtifactKinds.Markdown, RunId = "run", InlineText = "Unattributed" } });
            var changes = new RunChangesService(a => a.InlineText, null, null);
            var one = changes.Read(session, "run", "one");
            var two = changes.Read(session, "run", "two");
            AssertEqual(1, one.Items.Count, "unlinked artifacts never assigned by run alone");
            AssertEqual("A", one.Items.Single().Before, "first exact parent");
            AssertEqual("B", one.Items.Single().After, "first exact revision");
            AssertEqual("B", two.Items.Single().Before, "per-call chain stops at sibling revision");
            AssertEqual("C", two.Items.Single().After, "second exact revision");
        }

        private static void ToolPresentationContentBlocks()
        {
            var blocks = new List<ToolResultBlockDto>();
            ToolResultPresentationService.AppendContent(blocks, "{\"before\":\"A\",\"after\":\"B\"}");
            AssertEqual(0, blocks.Count, "arbitrary before/after cannot manufacture a diff");
            ToolResultPresentationService.AppendContent(blocks, "{\"items\":[{\"title\":\"Found\",\"target\":\"VBA: Module1\",\"snippet\":\"<script>alert(1)</script>\"}],\"complete\":false}");
            var list = (ToolListBlockDto)blocks.Single();
            AssertTrue(!list.Complete && list.Items.Count == 1, "existing resource find shape retains partial status");
            AssertContains(list.Items[0].Detail, "<script>", "presentation keeps text inert without rewriting source");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks, "{\"kind\":\"resource-read\",\"table\":{\"columns\":[{\"key\":\"a\",\"label\":\"Value\"}],\"rows\":[{\"a\":9007199254740993}],\"totalRows\":2},\"complete\":true}");
            var table = (ToolTableBlockDto)blocks.Single();
            AssertEqual("9007199254740993", table.Rows[0][0], "table numbers arrive as exact display strings");
            AssertTrue(!table.Complete, "unreturned rows are visible as partial");
            var wire = JsonConvert.SerializeObject(table);
            AssertContains(wire, "\"kind\":\"table\"", "typed block discriminator is serialized");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks, JsonConvert.SerializeObject(new { text = new string('x', 13000) }));
            AssertTrue(!blocks.Single().Complete && ((ToolTextBlockDto)blocks.Single()).Text.Length <= 12001, "long text is bounded and labelled");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks, "{\"text\":\"first\"} {\"text\":\"second\"}");
            AssertTrue(!blocks.OfType<ToolTextBlockDto>().Any(b => b.Text == "first"), "trailing malformed data cannot produce a trusted preview");
            var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
            session.Messages.Add(new ChatMessage { RunId = "run", Activity = new ChatActivity { ToolCallId = "read", DataJson = "{\"text\":\"ok\"}",
                ExecutionEvidence = new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, ToolEffectEvidence.None) } });
            var presentation = new ToolResultPresentationService(call => { throw new Exception("Reads must not load mutation journal"); });
            AssertEqual("ok", ((ToolTextBlockDto)presentation.Read(session, "run", "read").Blocks.Single()).Text, "read previews need no mutation capture");
        }
    }
}
