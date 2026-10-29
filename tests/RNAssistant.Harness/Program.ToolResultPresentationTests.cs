using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ToolPresentationPdfMedia()
        {
            WithTempPaths(paths => {
                var payloads = new ChatBlobStore(paths);
                var reference = new ResourceRef("rna://artifact/pdf/report", "revision");
                var scope = new ResourceAuthorityScopeId("chat", "pdf-media");
                var page = ResourceReadProjection.FromPdfPage(new ResourceReadResult {
                    Resource = new ResourceDescriptor { Reference = reference }, Representation = "text",
                    Text = "日本語", ReturnedCharacters = 3, TotalCharacters = 20, Offset = 4,
                    Complete = false, SourceComplete = false, PageIndex = 1, PageCount = 6,
                    Coverage = new ResourceCoverage(ResourceCoverageKinds.CharacterRange, address: "pdf-page:1", start: 4, end: 7)
                }, "report.pdf", "artifact", "conversation", 1, 4, 3, true);
                var range = ResourceReadProjection.FromPdfRange(new ResourceReadResult {
                    Resource = new ResourceDescriptor { Reference = reference }, Representation = "text",
                    Text = "Страница 3\nСтраница 4", ReturnedCharacters = 21, Complete = false, SourceComplete = false, PageCount = 6,
                    Coverage = new ResourceCoverage(ResourceCoverageKinds.PageRange, start: 2, end: 4),
                    SourceCoverage = new ResourceCoverage(ResourceCoverageKinds.PageRange, start: 2, end: 4)
                }, "report.pdf", "artifact", "conversation", 2, 3);
                foreach (var data in new[] { page, range, new ResourceReadProjection {
                    Target = "photo.png", Representation = "media", Coverage = ResourceCoverage.Whole(),
                    Complete = true, HydratedForNextModelStep = true, RawContentIncluded = false } })
                {
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "read", ToolId = "core.read" },
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read.", JsonConvert.SerializeObject(data)), ToolResultRoles.User);
                    terminal.RunId = "run";
                    terminal.ResourceEvidence.Add(new ResourceEvidence("observation", scope, reference, data.Representation, data.Coverage, false, 1));
                    terminal.ResourceModelReferences.Add(new ModelResourceReference(scope, reference, ModelReference.Create(ModelReferenceKind.Resource, 5), 1));
                    terminal.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(terminal.Content, "application/json"));
                    terminal.Content = "Retained in CAS.";
                    var session = new ChatSession { Messages = new List<ChatMessage> { terminal, new ChatMessage { RunId = "run",
                        Activity = new ChatActivity { ToolId = terminal.ToolName, ToolCallId = terminal.ToolCallId } } } };
                    var before = JsonConvert.SerializeObject(session);
                    var result = new ToolResultPresentationService(null, payloads).Read(session, "run", "read");
                    var block = result.Blocks.OfType<ToolTextBlockDto>().Single();
                    if (data == page) {
                        AssertEqual(1, block.Source.PageIndex.Value, "PDF page remains typed and zero-based until display");
                        AssertEqual("R5@v1", block.Source.Reference, "PDF text uses the exact saved scoped binding");
                        AssertTrue(block.Complete && block.Note.Contains("весь источник не прочитан"), "complete selection is distinct from partial source");
                        AssertEqual("日本語", block.Text, "selected PDF bytes are not replaced with the whole document");
                    } else if (data == range) {
                        AssertTrue(!block.Complete && block.Note.Contains("страницы 5"), "partial range keeps exact continuation in one-based UI coordinates");
                        AssertEqual(4L, block.Source.Coverage.End.Value, "range caption describes delivered pages, not requested pages");
                    } else {
                        AssertTrue(block.Source.Reference == null && block.Source.Excerpt == null, "media receipt does not manufacture a body citation");
                        AssertContains(block.Text, "Фактическую отправку", "preparation is not model delivery");
                        AssertTrue(ToolResultSummaryProjection.Summarize("core.read", result.RawJson).HydratedForNextModelStep,
                            "shared workspace summary preserves resource-owned media preparation");
                    }
                    AssertEqual(before, JsonConvert.SerializeObject(session), "presentation leaves canonical results and evidence unchanged");
                }
                foreach (var status in new[] { RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown }) {
                    var data = JsonConvert.SerializeObject(new ResourceReadProjection { Target = "sound.wav", Representation = "media",
                        Complete = true, HydratedForNextModelStep = true, Coverage = ResourceCoverage.Whole() });
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "failed", ToolId = "core.read" },
                        new RNAssistant.Core.Tools.Contracts.ToolResult(status, "Unverified.", data), ToolResultRoles.User);
                    terminal.RunId = "run";
                    var session = new ChatSession { Messages = new List<ChatMessage> { terminal, new ChatMessage { RunId = "run",
                        Activity = new ChatActivity { ToolId = terminal.ToolName, ToolCallId = terminal.ToolCallId } } } };
                    var block = (ToolTextBlockDto)new ToolResultPresentationService(null).Read(session, "run", "failed").Blocks.Single();
                    AssertContains(block.Text, "подтверждение подготовки", "failed/unknown media cannot claim prepared delivery");
                }
                var custom = new List<ToolResultBlockDto>();
                ToolResultPresentationService.AppendContent(custom,
                    "{\"kind\":\"resource-read\",\"target\":\"image.png\",\"representation\":\"media\",\"hydratedForNextModelStep\":true}", "custom.inspect");
                AssertEqual(0, custom.Count, "arbitrary custom data cannot claim a resource-owned media receipt");
            });
        }

        private static void ToolPresentationSourceReferences()
        {
            WithTempPaths(paths => {
                var payloads = new ChatBlobStore(paths);
                var scope = new ResourceAuthorityScopeId("workspace", "source-preview");
                var resource = new ResourceRef("rna://workspace/files/src/A.cs", "revision-a");
                var coverage = new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 42, end: 43);
                var evidence = new ResourceEvidence("observation", scope, resource, "text", coverage, false, 1);
                var references = new List<ModelResourceReference> {
                    new ModelResourceReference(new ResourceAuthorityScopeId("workspace", "foreign"), resource,
                        ModelReference.Create(ModelReferenceKind.Resource, 8), 2),
                    new ModelResourceReference(scope, resource, ModelReference.Create(ModelReferenceKind.Resource, 3), 1) };
                foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
                {
                    var data = JsonConvert.SerializeObject(new ResourceReadProjection { Target = "src/A.cs", Representation = "text",
                        Coverage = coverage, Text = "искомый текст 日本語 <script>literal</script>", Complete = false, SelectionComplete = true });
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "read", ToolId = "core.read" },
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read.", data), role);
                    terminal.RunId = "run"; terminal.ResourceEvidence.Add(evidence); terminal.ResourceModelReferences = references;
                    terminal.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 7);
                    terminal.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(terminal.Content, "application/json"));
                    terminal.Content = "Body retained in CAS.";
                    var session = new ChatSession { Messages = new List<ChatMessage> { terminal, new ChatMessage { RunId = "run",
                        Activity = new ChatActivity { ToolId = "core.read", ToolCallId = "read",
                            DataJson = "{\"kind\":\"resource-read\",\"target\":\"src/A.cs\",\"text\":\"unrelated preview\",\"ref\":\"R999\"}" } } } };
                    var before = JsonConvert.SerializeObject(session);
                    var service = new ToolResultPresentationService(null, payloads);
                    var result = service.Read(session, "run", "read");
                    var block = result.Blocks.OfType<ToolTextBlockDto>().Single();
                    AssertEqual("E7", result.ExecutionReference, "all result roles use the saved execution alias");
                    AssertEqual("R3@v1", block.Source.Reference, "source label resolves scope/revision instead of list order");
                    AssertEqual("src/A.cs", block.Source.Target, "semantic path is a display label");
                    AssertEqual(42L, block.Source.Coverage.Start.Value, "caption retains actual line coordinates");
                    AssertEqual("искомый текст 日本語 <script>literal</script>", block.Text,
                        "caption and body come from the same canonical CAS result, never a similar activity preview");
                    AssertEqual(before, JsonConvert.SerializeObject(session), "UI does not rewrite storage or model messages");
                    terminal.ResourceEvidence.Add(new ResourceEvidence("foreign", references[0].Scope, resource, "text", coverage, false, 1));
                    AssertTrue(service.Read(session, "run", "read").Blocks.Single().Source.Reference == null, "ambiguous observations get no invented alias");
                    terminal.ResourceEvidence.RemoveAt(1);
                    System.IO.File.Delete(payloads.PathFor(terminal.ResultPayload.Sha256));
                    AssertTrue(service.Read(session, "run", "read").Blocks.All(b => b.Source == null),
                        "missing exact payload cannot fall back to the activity body with a trusted citation");
                }
                foreach (var status in new[] { RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown })
                {
                    var terminal = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "failed", ToolId = "core.read" },
                        new RNAssistant.Core.Tools.Contracts.ToolResult(status, "Unverified read.", JsonConvert.SerializeObject(new ResourceReadProjection {
                            Target = "src/A.cs", Representation = "text", Coverage = coverage, Text = "Recovery information" })), ToolResultRoles.User);
                    terminal.RunId = "run"; terminal.ResourceEvidence.Add(evidence); terminal.ResourceModelReferences = references;
                    var session = new ChatSession { Messages = new List<ChatMessage> { terminal, new ChatMessage { RunId = "run",
                        Activity = new ChatActivity { ToolId = terminal.ToolName, ToolCallId = terminal.ToolCallId } } } };
                    AssertTrue(new ToolResultPresentationService(null).Read(session, "run", "failed").Blocks.Single().Source.Reference == null,
                        "error/unknown cannot advertise successful source delivery");
                }
                var blocks = new List<ToolResultBlockDto>();
                ToolResultPresentationService.AppendContent(blocks,
                    "{\"matches\":[{\"target\":\"src/B.cs\",\"startLine\":17,\"startColumn\":2,\"snippet\":\"другое совпадение\",\"ref\":\"R8@v2\"}]}", "core.search");
                var match = ((ToolListBlockDto)blocks[0]).Items.Single().Source;
                AssertTrue(match.Reference == null && match.Line == 17 && match.Excerpt == "другое совпадение",
                    "search retains the compact format without accepting a business ref field as authority");
                blocks.Clear();
                ToolResultPresentationService.AppendContent(blocks, "{\"kind\":\"resource-read\",\"target\":\"data.json\",\"representation\":\"tree\",\"complete\":false," +
                    "\"coverage\":{\"kind\":\"json-path\",\"path\":\"/\"},\"tree\":{\"nodes\":[{\"pointer\":\"/name\",\"type\":\"string\",\"value\":\"日本語\"}],\"nextOffset\":1}}", "core.read");
                var tree = blocks.OfType<ToolListBlockDto>().Single();
                AssertTrue(!tree.Complete && tree.Source.Target == "data.json" && tree.Items.Single().Title == "/name", "bounded tree values have a typed preview and source caption");
            });
        }

        private static void ToolPresentationQuestionsSurviveStorage()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths);
                var payloads = new ChatBlobStore(paths);
                var session = store.Create("Excel", "questions", "Book", "Questions");
                var questions = new UserQuestionSet { QuestionSetId = "set", Questions = new List<UserQuestion> {
                    new UserQuestion { Id = "q1", Header = "Scope", Prompt = "Choose scope " + new string('x', 10000),
                        Selection = "single", Options = new List<UserQuestionOption> {
                            new UserQuestionOption { Id = "a", Label = "Core", Description = "Core only" },
                            new UserQuestionOption { Id = "b", Label = "UI", Description = "Include UI" } } } } };
                var message = RNAssistant.Office.AgentTranscript.CreateLocalResultMessage(
                    new ToolInvocation { ToolId = UserQuestionToolCatalog.AskToolId, ToolCallId = "ask" },
                    ToolRunResult.AwaitingUser("Ответьте на вопросы.", JsonConvert.SerializeObject(questions)));
                message.RunId = "run"; message.Activity.RunId = "run";
                session.Messages.Add(message);
                store.Save(session);
                var loaded = store.Load(session.Id);
                var saved = loaded.Messages.Single(m => m.Activity != null).Activity;
                AssertTrue(saved.DataJson == null && saved.ResultPayload != null,
                    "the question uses the normal externalized activity body after save/reload");
                var bridge = ChatCloneService.CloneMessagesForBridge(loaded, loaded.Messages).Single(m => m.Activity != null);
                AssertTrue(bridge.Activity.DataJson == null && bridge.Activity.ResultPayload == null,
                    "metadata projection alone cannot deliver externalized questions");
                var service = new ToolResultPresentationService(null, payloads);
                var presentation = service.Read(loaded, "run", "ask");
                AssertEqual(questions.Questions[0].Prompt, presentation.Questions.Questions[0].Prompt,
                    "typed presentation hydrates complete questions from exact retained CAS");
                AssertTrue(presentation.QuestionsUnavailableReason == null, "readable questions have no unavailable notice");
                saved.ResultPayload = new PayloadRef(new string('e', 64), 20000, "application/json");
                var unavailable = service.Read(loaded, "run", "ask");
                AssertTrue(unavailable.Questions == null && !string.IsNullOrWhiteSpace(unavailable.QuestionsUnavailableReason),
                    "missing question data is explicit, not an empty waiting card");
            });
        }

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
            var invocation = new ToolInvocation { ToolId = "custom.inspect", ToolCallId = "custom" };
            var terminal = AgentJsonProtocol.CreateToolResultMessage(invocation,
                RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read.", "{\"text\":\"canonical result\"}"), ToolResultRoles.User);
            terminal.RunId = "run";
            session.Messages.Add(terminal);
            session.Messages.Add(new ChatMessage { RunId = "run", Activity = new ChatActivity {
                ToolId = invocation.ToolId, ToolCallId = invocation.ToolCallId, Status = "completed",
                Display = new ToolDisplayMetadata("Проверка объекта"), Subtitle = "Object A", DataJson = "{\"text\":\"unrelated activity\"}" } });
            var custom = presentation.Read(session, "run", "custom");
            AssertEqual("canonical result", ((ToolTextBlockDto)custom.Blocks.Single()).Text,
                "all result families use the canonical body when retained, including workspace activities without data");
            AssertEqual("Проверка объекта", custom.Action, "receipt uses catalog action");
            AssertEqual("Object A", custom.Target, "receipt identifies the exact attempted target");
            AssertEqual("{\"text\":\"canonical result\"}", custom.RawJson, "raw view uses the same canonical result instead of a second activity body");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks,
                "{\"text\":\"Summary\",\"table\":{\"columns\":[{\"key\":\"n\"}],\"rows\":[{\"n\":2}],\"totalRows\":1},\"items\":[{\"title\":\"Diagnostic\",\"text\":\"Detail\"}]}");
            AssertTrue(blocks.OfType<ToolTableBlockDto>().Count() == 1 && blocks.OfType<ToolListBlockDto>().Count() == 1 &&
                blocks.OfType<ToolTextBlockDto>().Count() == 1, "a composite result retains table, list and text independently");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks,
                "{\"kind\":\"tool-schema\",\"descriptor\":{\"function\":{\"description\":\"Reads a source\",\"parameters\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}}}}",
                "core.tools.read");
            AssertTrue(blocks.OfType<ToolTextBlockDto>().Any(b => b.Text == "Reads a source") &&
                blocks.OfType<ToolTextBlockDto>().Any(b => b.Format == "json" && b.Text.Contains("\"properties\"")),
                "tool schema presents description and parameter tree without raw-result navigation");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks,
                "{\"kind\":\"skill\",\"complete\":true,\"bodyMarkdown\":\"# VBA\\nRead modules\"}", "core.tools.read");
            AssertEqual("markdown", ((ToolTextBlockDto)blocks.Single()).Format, "skill body uses safe Markdown renderer");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks,
                "{\"id\":\"skills.vba.code.editing\",\"status\":\"selected_for_next_request\",\"tools\":[\"vba.read\"],\"references\":[\"README.md\"]}",
                "core.skills.load");
            AssertTrue(blocks.OfType<ToolListBlockDto>().Count() == 2,
                "selected skill exposes its tools and references instead of opaque JSON");
            blocks.Clear();
            ToolResultPresentationService.AppendContent(blocks,
                "{\"items\":[{\"id\":\"skills.vba.code.editing\",\"summary\":\"VBA editing\"}],\"complete\":true}", "core.tools.search");
            AssertEqual("skills.vba.code.editing", ((ToolListBlockDto)blocks.Single()).Items.Single().Title,
                "capability search preserves identifiers in the visible result");
            foreach (var effect in new[] { ToolEffectEvidence.VerifiedChange, ToolEffectEvidence.VerifiedNoChange, ToolEffectEvidence.Unknown }) {
                var mutation = new ChatSession { Messages = new List<ChatMessage> { new ChatMessage { RunId = "run",
                    Activity = new ChatActivity { ToolId = "files.patch", ToolCallId = "patch",
                        ExecutionEvidence = new ToolExecutionEvidence(ToolDispatchEvidence.MayHaveDispatched, effect),
                        DataJson = "{\"before\":\"invented\",\"after\":\"invented\",\"replacements\":7}" } } } };
                var receipt = new ToolResultPresentationService(null).Read(mutation, "run", "patch");
                AssertEqual(1, receipt.Blocks.Count, "arbitrary business counts and before/after never become verified comparisons");
                AssertContains(((ToolTextBlockDto)receipt.Blocks.Single()).Text, effect == ToolEffectEvidence.VerifiedChange ? "Сохранённое сравнение" :
                    effect == ToolEffectEvidence.VerifiedNoChange ? "Изменений нет" : "Результат действия не подтверждён",
                    "missing comparison preserves the exact effect, including confirmed no-op");
            }
        }
    }
}
