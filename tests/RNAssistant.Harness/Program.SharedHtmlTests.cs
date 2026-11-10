using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolRunResult WriteSharedHtmlFixture(OfficeToolExecutor executor, ChatSession session, string content)
        {
            try
            {
                executor.MutateLocalResources(session, "html.source.write", new Dictionary<string, object> { ["path"] = "index.html" },
                    () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", content, true));
                return new ToolRunResult { Success = true };
            }
            catch (Exception ex) { return new ToolRunResult { Success = false, Message = ex.Message }; }
        }

        private static void SharedHtmlExplicitTargetsAcrossAgents()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var chats = new ChatStore(FixturePaths.Value);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(FixturePaths.Value));
                var catalog = executor.GetControllerTools().ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var author = NewSession(adapter);
                var reader = NewSession(adapter); reader.DocumentAuthorityId = author.DocumentAuthorityId;
                Action<ChatSession, ToolInvocation> run = (chat, command) => {
                    var result = executor.ExecuteManual(command, catalog, settings, false, true, chat);
                    AssertTrue(result.Success, command.ToolId + ": " + result.Message);
                };
                run(author, Command("html.workspace.create", "title", "Chess"));
                reader.DocumentAuthorityId = author.DocumentAuthorityId;
                var original = author.Artifacts.Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var workspace = HtmlWorkspaceTargets.Workspace(chats.DocumentArtifacts, author, original);
                Func<string, string> target = path => "HTML file: " + path + " in " + workspace;
                Func<string, string> read = path => executor.ResourceGateway.ReadWhole(reader,
                    executor.ResourceGateway.ResolveIntentTarget(reader, target(path), true).Reference, "source").Result.Text;
                run(author, Command("core.write", "target", target("index.html"), "content", "<main>shared</main>"));
                run(author, Command("core.write", "target", target("style.css"), "content", "body{color:red;}"));
                AssertTrue(string.IsNullOrEmpty(author.ActiveHtmlArtifactId), "model creation never selects the author's UI");
                AssertTrue(executor.ResourceGateway.Find(reader, "index.html", "html").Items.Any(item => item.Target.Contains(workspace)),
                    "another agent discovers the shared current source without linking: " + JsonConvert.SerializeObject(executor.ResourceGateway.Find(reader, "index.html", "html")));
                var firstMember = executor.ResourceGateway.ResolveIntentTarget(reader, target("index.html"), true).Descriptor;
                AssertEqual("<main>shared</main>", read("index.html"), "unselected reader reads the current file");
                run(reader, Command("html.workspace.create", "title", "Other"));
                var other = reader.Artifacts.Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var otherTarget = HtmlWorkspaceTargets.Workspace(chats.DocumentArtifacts, reader, other);
                run(reader, Command("core.write", "target", "HTML file: index.html in " + otherTarget, "content", "<main>local</main>"));
                var otherHead = chats.DocumentArtifacts.CurrentSnapshot(reader, HtmlWorkspaceIdentity.Identity(reader, HtmlWorkspaceIdentity.LogicalId(other.Id)));
                links.Change(reader, LinkRequest(reader, otherHead.Uri, false), chats.Save);
                var selection = reader.ActiveHtmlArtifactId;
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.ResolveIntentTarget(reader, "HTML file: index.html", true));
                var pending = PrepareVbaNative(executor, reader, Command("core.patch", "targets",
                    PatchTargets(PatchSection(target("index.html"), EditSet(Edit("shared", "edited"))))));
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "shared source prepares without selecting it");
                run(author, Command("core.write", "target", target("style.css"), "content", "body{color:green;}"));
                var confirmed = ConfirmVbaNative(pending);
                AssertEqual(ToolExecutionOutcome.Ok, confirmed.Outcome, "sibling changes survive a prepared edit: " + confirmed.Message + " " + confirmed.Result?.DataJson);
                AssertEqual("body{color:green;}", read("style.css"), "latest aggregate preserves the other agent's CSS");
                AssertEqual("<main>edited</main>", read("index.html"), "shared source was changed");
                AssertEqual(selection, reader.ActiveHtmlArtifactId, "writing another workspace never changes selected UI");
                AssertEqual("<main>local</main>", reader.HtmlWorkspace.Files.Single().Content, "selected editor projection remains intact");
                var stale = PrepareVbaNative(executor, reader, Command("core.patch", "targets",
                    PatchTargets(PatchSection(target("index.html"), EditSet(Edit("edited", "stale"))))));
                run(author, Command("core.write", "target", target("index.html"), "content", "<main>newer</main>"));
                run(author, Command("core.write", "target", target("index.html"), "content", "<main>edited</main>"));
                var rejected = ConfirmVbaNative(stale);
                AssertEqual(ToolExecutionOutcome.Error, rejected.Outcome, "same-file ABA conflicts");
                AssertTrue(!rejected.MayHaveDispatched, "conflict happens before dispatch");
                AssertEqual("<main>shared</main>", executor.ResourceGateway.ReadWhole(reader,
                    executor.ResourceGateway.ResolveIntentTarget(reader, firstMember.SnapshotTarget).Reference, "source").Result.Text,
                    "historical target remains exact");
                var historical = PrepareVbaNative(executor, reader, Command("core.write", "target", firstMember.SnapshotTarget, "content", "bad")).Record;
                AssertEqual(ToolExecutionOutcome.Error, historical.Outcome, "history is never a mutation target");
                var foreign = NewSession(adapter); foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                RuntimeThrows<ResourceRequestException>(() => executor.ResourceGateway.ResolveIntentTarget(foreign, target("index.html"), true));
                chats.Save(reader); reader = new ChatStore(FixturePaths.Value).Load(reader.Id);
                AssertEqual("<main>edited</main>", read("index.html"), "stable target survives restart without changing selection");
                run(reader, Command("html.data.write", "workspace", workspace, "name", "rows", "json", "{\"value\":1}"));
                run(reader, Command("html.data.bind", "workspace", workspace, "name", "markup",
                    "target", executor.ResourceGateway.ResolveIntentTarget(reader, target("index.html"), true).Descriptor.CurrentTarget, "view", "source", "policy", "head"));
                run(reader, Command("html.data.freeze", "workspace", workspace, "name", "markup"));
                var boundRoot = executor.ResourceGateway.ResolveIntentTarget(reader, workspace, true).Reference;
                var boundBody = JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(chats.DocumentArtifacts.Read(reader, boundRoot).InlineText);
                AssertEqual("exact", boundBody.DataSources.Single(item => item.Name == "markup").Binding.Policy, "freeze uses explicit workspace");
                run(reader, Command("html.assets.publish", "workspace", workspace, "id", "chess-assets", "version", "1.0.0",
                    "title", "Chess assets", "description", "Shared chess source", "files", new JArray("index.html")));
                run(author, Command("html.assets.list", "query", "chess-assets"));
                run(author, Command("html.workspace.create", "title", "Imported"));
                var imported = author.Artifacts.Last(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var importTarget = HtmlWorkspaceTargets.Workspace(chats.DocumentArtifacts, author, imported);
                run(author, Command("html.assets.import", "workspace", importTarget, "id", "chess-assets", "version", "1.0.0"));
                AssertEqual("<main>edited</main>", executor.ResourceGateway.ReadWhole(author,
                    executor.ResourceGateway.ResolveIntentTarget(author, "HTML file: index.html in " + importTarget, true).Reference, "source").Result.Text,
                    "assets import uses its explicit workspace");
                AssertEqual(selection, reader.ActiveHtmlArtifactId, "data and asset publication preserve UI selection");
                var creating = PrepareVbaNative(executor, reader, Command("core.write", "target", target("race.js"), "content", "old"));
                run(author, Command("core.write", "target", target("race.js"), "content", "new"));
                var createConflict = ConfirmVbaNative(creating);
                AssertTrue(createConflict.Outcome == ToolExecutionOutcome.Error && !createConflict.MayHaveDispatched, "concurrent creation fails before dispatch");
                var deleting = PrepareVbaNative(executor, reader, Command("resources.delete", "target", target("race.js")));
                run(author, Command("core.write", "target", target("style.css"), "content", "body{color:blue;}"));
                AssertEqual(ToolExecutionOutcome.Ok, ConfirmVbaNative(deleting).Outcome, "delete preserves sibling edits");
                var missingWorkspace = executor.ExecuteManual(Command("html.data.write", "name", "bad", "json", "{}"), catalog, settings, false, true, reader);
                AssertTrue(!missingWorkspace.Success, "data mutation cannot infer the selected workspace");
                var selectedHead = executor.ResourceGateway.ResolveIntentTarget(reader, workspace, true).Reference;
                links.Change(reader, LinkRequest(reader, selectedHead.Uri, false), chats.Save);
                run(author, Command("core.write", "target", target("index.html"), "content", "<main>from another agent</main>"));
                var latest = executor.ResourceGateway.ResolveIntentTarget(reader, workspace, true).Reference;
                AssertTrue(!reader.Artifacts.Any(item => ChatResourceUri.CreateArtifactRevision(reader, item).Uri == latest.Uri),
                    "reader has not linked the other agent's new snapshot");
                HtmlWorkspaceEditorResourceService.RefreshSelected(reader, chats.DocumentArtifacts);
                AssertEqual("<main>from another agent</main>", reader.HtmlWorkspace.Files.Single(item => item.Path == "index.html").Content,
                    "selected view refreshes a publication absent from this chat's artifact list");
            });
        }

        private static void SharedHtmlSavedHistoryFollowsDocument()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var registry = new DocumentAuthorityRegistry(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = new ArtifactCatalogService(registry, chats.DocumentArtifacts, links, new ResourceAuthorityStore(paths), new ChatBlobStore(paths));
                var author = NewSession(adapter);
                author.DocumentAuthorityId = registry.Resolve("word", "source", System.IO.Path.Combine(paths.Root, "source.docx")).Id;
                AssertTrue(WriteSharedHtmlFixture(executor, author, "<h1>first</h1>").Success, "initial publication");
                var first = author.Artifacts.Single(item => item.Id == author.ActiveHtmlArtifactId);
                var reference = ChatResourceUri.CreateArtifactRevision(author, first);
                var reader = NewSession(adapter);
                reader.DocumentAuthorityId = registry.Resolve("word", "reader", System.IO.Path.Combine(paths.Root, "reader.docx")).Id;
                var request = new SavedHtmlHistoryRequest { ChatId = reader.Id, DocumentId = author.DocumentAuthorityId, ResourceUri = reference.Uri };
                AssertEqual(1, catalog.SavedHtmlHistory(reader, request).TotalCount, "single revision available without a captured library head");
                var writer = NewSession(adapter); writer.DocumentAuthorityId = author.DocumentAuthorityId;
                links.Change(writer, LinkRequest(writer, reference.Uri, false), chats.Save);
                for (var i = 2; i <= 51; i++)
                    AssertTrue(WriteSharedHtmlFixture(executor, writer, "<h1>version " + i + "</h1>").Success, "other agent publishes " + i);
                reader.Revision += 5;
                var page = catalog.SavedHtmlHistory(reader, request);
                AssertEqual(51, page.TotalCount, "new publications appear without reopening the saved snapshot");
                AssertEqual(50, page.Items.Count, "history remains paged");
                AssertEqual(writer.ActiveHtmlArtifactId, page.HeadArtifactId, "head comes from document authority");
                AssertTrue(page.Items[0].IsHead && page.Items[0].Revision == 51, "current revision is labelled");
                AssertEqual(reference.Uri, page.ResourceUri, "selected immutable snapshot is echoed unchanged");
                request.Cursor = page.NextCursor;
                var tail = catalog.SavedHtmlHistory(reader, request);
                AssertEqual(first.Id, tail.Items.Single().ArtifactId, "old snapshot remains in document history");
                AssertTrue(WriteSharedHtmlFixture(executor, writer, "<h1>new head</h1>").Success, "publication during paging");
                RuntimeThrows<InvalidOperationException>(() => catalog.SavedHtmlHistory(reader, request));
                request.Cursor = null;
                AssertEqual(52, catalog.SavedHtmlHistory(reader, request).TotalCount, "restarting reads fresh history");
                AssertEqual(first.Id, author.ActiveHtmlArtifactId, "read never retargets the old author's selection");
                AssertTrue(reader.Artifacts.Count == 0 && reader.ActiveHtmlArtifactId == null, "foreign history read never attaches or selects snapshots");
                request.DocumentId = reader.DocumentAuthorityId;
                RuntimeThrows<System.IO.InvalidDataException>(() => catalog.SavedHtmlHistory(reader, request));
            });
        }

        private static void SharedHtmlMetadataPagesPreserveDescriptors()
        {
            WithTempPaths(paths =>
            {
                var snapshot = new HtmlWorkspaceSnapshot { Id = "paging", ActiveFileId = "b" };
                snapshot.Files.Add(new HtmlWorkspaceFile { Id = "a", Path = "a.js", Content = "Привет 😀" });
                snapshot.Files.Add(new HtmlWorkspaceFile { Id = "b", Path = "b.js", Content = "active" });
                snapshot.Files.Add(new HtmlWorkspaceFile { Id = "c", Path = "empty.js", Content = "" });
                snapshot.DataSources.Add(new HtmlWorkspaceDataSource { Id = "rows", Name = "Rows",
                    Binding = new HtmlWorkspaceDataBinding { Resource = new ResourceRef("rna://catalog/skills/test/body", "r1"),
                        Policy = "exact", View = "text", ViewPath = "$" } });
                var artifact = new ChatArtifact { Id = "paging", Kind = ChatArtifactKinds.HtmlWorkspace,
                    Revision = 1, InlineText = JsonConvert.SerializeObject(snapshot) };
                var session = new ChatSession { ActiveHtmlArtifactId = artifact.Id,
                    Artifacts = new List<ChatArtifact> { artifact } };
                var catalog = new ChatHtmlResourceCatalog(null, new ChatBlobStore(paths));
                var expected = catalog.DescribeMembers(session, artifact)
                    .OrderByDescending(item => item.Metadata["active"] == "true")
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
                var revision = ResourceReadCursor.CollectionRevision(expected, "source:0");
                var binding = ResourceReadCursor.ListBinding(ChatArtifactResourceProvider.ProviderName, null);
                var first = catalog.List(session, null, null, 1, "source");
                AssertEqual("b.js", first.Items.Single().Title, "active file remains first");
                var page = first;
                for (var index = 0; index < expected.Count; index++)
                {
                    AssertEqual(expected.Count, page.Total, "each page reports the whole collection");
                    AssertEqual(ResourceReadCursor.CreateRevisionBound(index, revision, binding), page.Cursor,
                        "cursor identity is byte-for-byte identical to complete metadata");
                    AssertTrue(JToken.DeepEquals(JObject.FromObject(expected[index]), JObject.FromObject(page.Items.Single())),
                        "paged descriptors preserve UTF-8 byte lengths, content hashes, raw capability and binding metadata");
                    if (index + 1 < expected.Count) page = catalog.List(session, null, page.NextCursor, 1, "source");
                }
                AssertTrue(page.NextCursor == null && !page.Truncated, "last page ends the collection");
                snapshot.Files[2].Content = "changed outside the returned page";
                artifact.InlineText = JsonConvert.SerializeObject(snapshot);
                ResourceRequestException changed = null;
                try { catalog.List(session, null, first.NextCursor, 1, "source"); }
                catch (ResourceRequestException ex) { changed = ex; }
                AssertEqual("resource_revision_changed", changed?.ErrorCode,
                    "unreturned member metadata still invalidates continuation");
            });
        }

        private static void SharedHtmlTextDiscoveryViews()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var a = NewSession(adapter);
                var html = "<!--" + new string('x', 145000) + "--><main>late-html-marker</main>";
                AssertTrue(WriteSharedHtmlFixture(executor, a, html).Success, "long HTML published");
                AssertTrue(executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.WriteDataToolId, "workspace", HtmlWorkspaceTargets.Workspace(chats.DocumentArtifacts, a, a.Artifacts.Last()), "name", "sales", "json", "{\"secret\":\"bound-value-only\"}"),
                    catalog, new AppSettings(), false, false, a).Success, "bound JSON published");
                HtmlWorkspaceEditorResourceService.RefreshSelected(a, chats.DocumentArtifacts);
                var parent = ChatResourceUri.CreateArtifactRevision(a, a.Artifacts.Single(item => item.Id == a.ActiveHtmlArtifactId));
                var b = NewSession(adapter); b.DocumentAuthorityId = a.DocumentAuthorityId;
                links.Change(b, LinkRequest(b, parent.Uri, false), chats.Save);
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: chats.DocumentArtifacts);
                var authority = new ResourceAuthorityStore(paths);
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(b.DocumentAuthorityId));
                var memberDescriptor = provider.List(b, ChatHtmlResourceCatalog.FileKind, null, 20).Items.Single();
                var beforeHead = Newtonsoft.Json.JsonConvert.SerializeObject(authority.GetHead(scope, memberDescriptor.Reference.Identity));
                var beforeGeneration = authority.Capture(scope).Generation;
                var found = provider.Search(b, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600);
                AssertEqual(beforeGeneration, authority.Capture(scope).Generation, "text search does not publish authority changes");
                var match = found.Matches.Single();
                AssertTrue(match.MatchOffset > 128000 && !found.ScanTruncated && found.UnavailableResources == 0, "shared HTML search reaches late source content");
                AssertEqual(ResourceRepresentations.Source, match.Representation, "file search remains source-code search");
                AssertEqual(null, match.SectionTitle, "HTML source is not parsed as Markdown or executed");
                var read = provider.Read(b, new ResourceReadRequest { Reference = match.Reference, Representation = ResourceRepresentations.Source,
                    Cursor = ResourceReadCursor.CreateImmutable(match.SnippetOffset, ResourceReadCursor.ReadBinding(match.Reference.Uri, ResourceRepresentations.Source)), MaxChars = match.Snippet.Length });
                AssertEqual(match.Snippet, read.Result.Text, "member snippet round-trips to exact source read");
                var gateway = new ResourceGatewayService(new[] { provider });
                AssertEqual("html", gateway.Find(b, "late-html-marker", "html").Items.Single().Scope, "document-owned member remains in model HTML scope");
                var data = provider.List(b, ChatHtmlResourceCatalog.DataKind, null, 20).Items.Single();
                var binding = provider.Read(b, new ResourceReadRequest { Reference = data.Reference, MaxChars = 2000 }).Result.Text;
                var bindingMatch = provider.Search(b, "view", ChatHtmlResourceCatalog.DataKind, 20, 600).Matches.Single();
                AssertEqual(binding.Substring(bindingMatch.SnippetOffset, bindingMatch.Snippet.Length), bindingMatch.Snippet, "data member search indexes the binding JSON");
                AssertEqual(0, provider.Search(b, "bound-value-only", ChatHtmlResourceCatalog.DataKind, 20, 600).Matches.Count, "search never dereferences bound data as member content");
                var memberKey = ResourceUri.Parse(match.Reference.Uri).Segments[7];
                var view = authority.GetView(scope, parent, "artifact-member-text-index-v1:member/file/" + memberKey);
                AssertEqual(parent.Uri, view.Reference.Uri, "member index is retained under its published parent");
                AssertTrue(view.Coverage.Kind == ResourceCoverageKinds.CharacterRange && view.Coverage.Path == "member/file/" + memberKey,
                    "member index cannot claim whole-parent coverage");
                AssertEqual(beforeHead, Newtonsoft.Json.JsonConvert.SerializeObject(authority.GetHead(scope, match.Reference.Identity)), "search leaves the existing authority head unchanged");
                System.IO.File.Delete(executor.Payloads.PathFor(view.Payload.Sha256));
                AssertEqual(1, provider.Search(b, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count, "missing member manifest is regenerated from exact aggregate");
                System.IO.File.WriteAllText(executor.Payloads.PathFor(view.Parts[0].Sha256), "corrupt member part");
                AssertEqual(1, provider.Search(b, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count, "corrupt member part is repaired from exact aggregate");
                var gc = CasService(paths, chats, new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed && gc.Health.MissingBlobCount == 0, "parent retains derived member bytes through GC");
                provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: new ChatStore(paths).DocumentArtifacts);
                AssertEqual(1, provider.Search(b, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count, "member index survives owner restart");
                AssertTrue(WriteSharedHtmlFixture(executor, b, "<main>replacement</main>").Success, "new HTML revision published");
                AssertEqual(0, provider.Search(b, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count, "current member search excludes the old index");
                var stale = provider.Search(a, "late-html-marker", ChatHtmlResourceCatalog.FileKind, 20, 600);
                AssertTrue(stale.UnavailableResources == 0 && stale.Matches.Count == 0, "discovery follows shared publication regardless of stale selection");
                AssertEqual(html.Substring(0, 1000), provider.Read(a, new ResourceReadRequest { Reference = match.Reference, MaxChars = 1000 }).Result.Text,
                    "historical member reads stay exact");
                var current = b.Artifacts.Single(item => item.Id == b.ActiveHtmlArtifactId);
                System.IO.File.Delete(executor.Payloads.PathFor(current.ContentSha256));
                found = provider.Search(b, "replacement", ChatHtmlResourceCatalog.FileKind, 20, 600);
                AssertTrue(found.UnavailableResources == 1 && found.Matches.Count == 0, "member search cannot conceal missing parent bytes");
            });
        }

        private static void SharedHtmlTextDiscoveryDrift()
        {
            foreach (var scenario in new[] { "selection", "generation", "unavailable" })
            WithTempExecutor(FakeOfficeAdapter.ForHost("Word"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                AssertTrue(WriteSharedHtmlFixture(executor, session, "<main>needle</main>").Success, "HTML race fixture published");
                var selected = session.ActiveHtmlArtifactId;
                var authority = new ResourceAuthorityStore(FixturePaths.Value);
                var counted = new DiscoveryPageStore(authority);
                counted.ViewRegistered = view =>
                {
                    if (!view.View.StartsWith("artifact-member-text-index-v1:")) return;
                    counted.ViewRegistered = null;
                    if (scenario == "selection") { session.ActiveHtmlArtifactId = null; return; }
                    if (scenario == "unavailable") throw new System.IO.IOException("Injected member index registration failure.");
                    var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(session.DocumentAuthorityId));
                    var before = authority.Capture(scope);
                    var identity = new ResourceIdentity(ResourceUri.Create("state", scope.Kind, scope.Id, "test-html-index-drift"));
                    authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                        new[] { new ResourceHeadChange(identity, null, ResourceHeadState.Unknown(identity, before.Generation + 1, "test-drift")) }, AuthorityCommitReason.DerivedPublication));
                };
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads,
                    documentArtifacts: new DocumentArtifactStore(counted, counted, executor.Payloads));
                if (scenario == "unavailable")
                {
                    var partial = provider.Search(session, "needle", null, 20, 600);
                    AssertTrue(partial.UnavailableResources == 1 && partial.Matches.Count == 0, "combined resource search preserves member index failure");
                }
                else
                {
                    if (scenario == "selection")
                    {
                        AssertEqual(1, provider.Search(session, "needle", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count,
                            "selection change does not invalidate shared discovery");
                    }
                    else AssertEqual(1, provider.Search(session, "needle", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count,
                        "unrelated authority publication does not invalidate HTML search");
                }
                session.ActiveHtmlArtifactId = selected;
                AssertEqual(1, provider.Search(session, "needle", ChatHtmlResourceCatalog.FileKind, 20, 600).Matches.Count, "fresh search succeeds after drift");
            });
        }

        private static void SharedHtmlSequentialSnapshotsAndCursors()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var store = new ChatStore(FixturePaths.Value);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var css = "body { color: red; }\n/*" + new string('x', 256) + "*/";
                AssertTrue(executor.ExecuteManual(Command("html.workspace.create", "title", "Sequential"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session).Success, "named workspace created");
                var root = session.Artifacts.Last(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var workspace = HtmlWorkspaceTargets.Workspace(store.DocumentArtifacts, session, root);
                foreach (var file in new[] { new[] { "index.html", "<main>old</main>" }, new[] { "styles.css", css } })
                    AssertTrue(executor.ExecuteManual(Command("core.write", "target", "HTML file: " + file[0] + " in " + workspace, "content", file[1]),
                        tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session).Success, "baseline file published");
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: store.DocumentArtifacts);
                var beforeCss = provider.List(session, ChatHtmlResourceCatalog.FileKind, null, 20).Items.Single(item => item.Title == "styles.css").Reference;
                var firstPage = provider.Read(session, new ResourceReadRequest { Reference = beforeCss, Representation = "source", MaxChars = 128 }).Result;
                AssertTrue(firstPage.NextCursor != null, "cursor fixture has a second page");
                var published = store.DocumentArtifacts.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(root.Id)));
                new ArtifactWorkingSetService(store.DocumentArtifacts, new ResourceMutationJournal(FixturePaths.Value))
                    .Change(session, LinkRequest(session, published.Uri, false), store.Save);
                var baseline = session.ActiveHtmlArtifactId;
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings { AutoConfirmToolActions = true }, "agent", false, manualRun: true);
                const string runId = "html-sequential-run";
                var authored = new List<string>();
                foreach (var edit in new[] { new[] { "styles.css", "red", "green" }, new[] { "index.html", "old", "updated" } })
                {
                    var call = new ToolCall("edit-" + authored.Count, ResourceMutationToolCatalog.Patch,
                        PatchArguments("HTML file: " + edit[0], EditSet(Edit(edit[1], edit[2]))).ToString());
                    var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), runId, "turn", call.Id,
                        DateTime.UtcNow, false, 4), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, "sequential patch published");
                    AssertEqual(ToolEffectEvidence.VerifiedChange, result.Evidence.Effect, "each patch has verified publication evidence");
                    HtmlWorkspaceEditorResourceService.RefreshSelected(session, store.DocumentArtifacts);
                    authored.Add(session.ActiveHtmlArtifactId);
                }
                store.Save(session);
                session = store.Load(session.Id);
                AssertEqual(authored.Last(), session.ActiveHtmlArtifactId, "restart selects the latest sequential snapshot");
                AssertEqual("<main>updated</main>", session.HtmlWorkspace.Files.Single(item => item.Path == "index.html").Content, "HTML update survives restart");
                AssertEqual(css.Replace("red", "green"), session.HtmlWorkspace.Files.Single(item => item.Path == "styles.css").Content, "later HTML write preserves earlier CSS edit");
                var afterCss = provider.List(session, ChatHtmlResourceCatalog.FileKind, null, 20).Items.Single(item => item.Title == "styles.css").Reference;
                var oldRemainder = provider.Read(session, new ResourceReadRequest { Reference = beforeCss, Representation = "source", Cursor = firstPage.NextCursor, MaxChars = 1000 }).Result;
                AssertEqual(css, firstPage.Text + oldRemainder.Text, "historical cursor remains pinned to old exact source");
                RuntimeThrows<ResourceRequestException>(() => provider.Read(session, new ResourceReadRequest {
                    Reference = afterCss, Representation = "source", Cursor = firstPage.NextCursor, MaxChars = 100 }));
                var current = store.DocumentArtifacts.CurrentSnapshot(session,
                    HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(session.ActiveHtmlArtifactId)));
                AssertEqual(session.ActiveHtmlArtifactId, store.DocumentArtifacts.Read(session, current, false).Id, "selected snapshot equals the document's published head");
                AssertEqual(baseline, session.Artifacts.Single(item => item.Id == authored[0]).ParentArtifactId, "first edit retains the exact original baseline");
                AssertEqual(authored[0], session.Artifacts.Single(item => item.Id == authored[1]).ParentArtifactId, "second edit advances the first snapshot");
            });
        }

        private static void SharedHtmlRunChangesSurviveRestart()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var store = new ChatStore(FixturePaths.Value);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                AssertTrue(executor.ExecuteManual(Command("html.workspace.create", "title", "Sequential"),
                    tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session).Success, "named workspace created");
                var root = session.Artifacts.Last(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                var workspace = HtmlWorkspaceTargets.Workspace(store.DocumentArtifacts, session, root);
                foreach (var file in new[] { new[] { "index.html", "<main>old</main>" }, new[] { "styles.css", "body { color: red; }" } })
                    AssertTrue(executor.ExecuteManual(Command("core.write", "target", "HTML file: " + file[0] + " in " + workspace, "content", file[1]),
                        tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session).Success, "baseline file published");
                var published = store.DocumentArtifacts.CurrentSnapshot(session, HtmlWorkspaceIdentity.Identity(session, HtmlWorkspaceIdentity.LogicalId(root.Id)));
                new ArtifactWorkingSetService(store.DocumentArtifacts, new ResourceMutationJournal(FixturePaths.Value))
                    .Change(session, LinkRequest(session, published.Uri, false), store.Save);
                var baseline = session.ActiveHtmlArtifactId;
                var provider = new ChatArtifactResourceProvider(payloads: executor.Payloads, documentArtifacts: store.DocumentArtifacts);
                var targets = provider.List(session, ChatHtmlResourceCatalog.FileKind, null, 20).Items
                    .ToDictionary(item => item.Title, ResourceGatewayService.IntentTarget);
                Func<string, string, string, string> patch = (path, find, text) =>
                    ModelProtocolWire.WriteAcceptedCall("Apply the requested file patch.", "",
                        new ConversationToolCall { Name = ResourceMutationToolCatalog.Patch, Arguments = PatchArguments(targets[path], EditSet(Edit(find, text))) });
                Func<string, string> read = path => ModelProtocolWire.WriteAcceptedCall("Read the current file source.", "",
                    new ConversationToolCall { Name = "core.read", Arguments = new JObject { ["target"] = targets[path], ["representation"] = "source" } });
                var responses = new Queue<Func<string>>(new Func<string>[] {
                    () => read("styles.css"), () => patch("styles.css", "red", "green"),
                    () => read("index.html"), () => patch("index.html", "old", "updated"),
                    () => ScriptedVerifiedFinish(session, "Both file patches completed.", "Updated the HTML and CSS.",
                        "HTML and CSS were updated.", ResourceMutationToolCatalog.Patch,
                        ResourceMutationToolCatalog.Patch) });
                var checkAfterSource = true;
                var service = CreateConversationRunService(adapter, executor,
                    (settings, messages, options, stream, token) => {
                        if (checkAfterSource && responses.Count == 3)
                        {
                            var afterSource = messages.Single(message => message.SyntheticResourceObservation &&
                                message.Content.Contains("body { color: green; }"));
                            AssertContains(afterSource.Content, targets["styles.css"], "verified HTML source keeps its semantic target in the next request");
                            checkAfterSource = false;
                        }
                        return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue()() });
                    });
                Func<ChatTurnResult> run = () => service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Update the HTML and CSS.", session,
                    NewContext(adapter), new AppSettings { AutoConfirmToolActions = true, ContextWindowOverrideTokens = 131072 }, tools, null).GetAwaiter().GetResult();
                var completed = run();
                AssertEqual(RunViewLifecycles.Completed, completed.RunViewState.Lifecycle,
                    "production conversation completes sequential patches: " + completed.AssistantText + "; " +
                    string.Join("; ", session.Messages.Where(message => message.Activity != null).Select(message =>
                        message.Activity.ToolId + ": " + message.Activity.ResultMessage + " " + message.Activity.DataJson)));
                var runId = session.LastRun.RunId;
                var activities = session.Messages.Where(message => message.RunId == runId && message.Activity?.ToolId == ResourceMutationToolCatalog.Patch).ToArray();
                var foreign = NewSession(adapter); foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var firstEffect = session.Messages.First(message => message.RunId == runId &&
                    message.ToolName == ResourceMutationToolCatalog.Patch && message.ResourceEffect?.Targets?.Count > 0).ResourceEffect;
                AssertEqual("RESOURCE_ACCESS_DENIED", RuntimeThrows<ResourceRequestException>(() =>
                    executor.ResourceAuthority.ScopeFor(foreign, firstEffect.Targets[0].After, false)).ErrorCode,
                    "HTML member references cannot cross document authority");
                AssertEqual(2, activities.Length, "both patch activities are retained: " + string.Join("; ", session.Messages
                    .Where(message => message.Activity != null).Select(message => message.RunId + "/" + message.Activity.ToolId + "/" + message.Activity.ResultMessage)));
                Func<RunChangesService> changes = () => new RunChangesService(artifact => store.DocumentArtifacts.Read(session,
                    ChatResourceUri.CreateArtifactRevision(session, artifact)).InlineText, null, null, executor.ResourceAuthority);
                AssertEqual(2, changes().Read(session, runId).Items.Count, "sequential publication preserves both run changes before relinking");
                foreach (var activity in activities)
                {
                    // Exercise normal activity externalization without changing the canonical tool result.
                    var data = JObject.Parse(activity.Activity.DataJson);
                    data["diagnostic"] = new string('x', 9000);
                    activity.Activity.DataJson = data.ToString();
                }
                store.Save(session);
                session = store.Load(session.Id);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var result = changes().Read(session, runId);
                AssertEqual(2, result.Items.Count, "archived activity bodies and restart preserve the complete run diff");
                AssertEqual("<main>old</main>", result.Items.Single(item => item.Title == targets["index.html"]).Before, "HTML baseline stays exact");
                AssertEqual("<main>updated</main>", result.Items.Single(item => item.Title == targets["index.html"]).After, "HTML after-state stays exact");
                AssertEqual("body { color: red; }", result.Items.Single(item => item.Title == targets["styles.css"]).Before, "CSS baseline stays exact");
                AssertEqual("body { color: green; }", result.Items.Single(item => item.Title == targets["styles.css"]).After, "CSS survives the later HTML patch");
                for (var index = 0; index < activities.Length; index++)
                {
                    var activity = session.Messages.Single(message => message.Id == activities[index].Id);
                    AssertTrue(activity.Activity.ResultPayload != null && activity.Activity.DataJson == null, "activity is archived");
                    var perCall = changes().Read(session, runId, activity.Activity.ToolCallId);
                    AssertEqual(1, perCall.Items.Count, "each call retains only its own file change");
                    AssertEqual(targets[index == 0 ? "styles.css" : "index.html"], perCall.Items.Single().Title, "call attribution is exact");
                    var artifact = session.Artifacts.Single(item => item.SourceMessageId == activity.Id && item.Kind == ChatArtifactKinds.HtmlWorkspace);
                    var retained = store.DocumentArtifacts.Read(session, ChatResourceUri.CreateArtifactRevision(session, artifact), false);
                    AssertEqual(runId, retained.RunId, "run identity is retained in the immutable publication");
                    AssertEqual(activity.Id, retained.SourceMessageId, "activity identity is retained before result archival");
                }
                var selected = session.ActiveHtmlArtifactId;
                var snapshotCount = session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                responses.Enqueue(() => LoadSkillResponse("skills.html.workspace.authoring"));
                responses.Enqueue(() => patch("index.html", "updated", "updated"));
                responses.Enqueue(() => ScriptedVerifiedFinish(session, "The repeated patch made no change.",
                    "No changes needed.", "The file already has the requested text.",
                    ResourceMutationToolCatalog.Patch));
                AssertEqual(RunViewLifecycles.Completed, run().RunViewState.Lifecycle, "later no-op run completes");
                var noOpRun = session.LastRun.RunId;
                var noOp = session.Messages.Single(message => message.RunId == noOpRun &&
                    message.Activity?.ToolId == ResourceMutationToolCatalog.Patch).Activity;
                AssertEqual(ToolEffectEvidence.VerifiedNoChange, noOp.ExecutionEvidence.Effect, "later patch executes as a verified no-op");
                store.Save(session);
                session = store.Load(session.Id);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                AssertEqual(selected, session.ActiveHtmlArtifactId, "no-op keeps the same snapshot");
                AssertEqual(snapshotCount, session.Artifacts.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace), "no-op creates no revision");
                AssertEqual(ResourceTargetStatus.Unchanged, changes().Read(session, noOpRun).Items.Single().TargetStatus, "no-op retains its own unchanged status");
                AssertEqual(2, changes().Read(session, runId).Items.Count, "no-op leaves original run attribution intact");
                AssertTrue(session.Artifacts.Single(item => item.Id == baseline).RunId != runId, "historical baseline is never reassigned");
            });
        }

        private static void SharedHtmlFailedLinkDoesNotReplay()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), paths: paths,
                    persistResourceFacts: (saved, commit) => { throw new System.IO.IOException("Injected HTML link failure."); });
                var session = NewSession(adapter);
                var definition = executor.GetControllerTools().Single(item => item.Id == HtmlWorkspaceToolCatalog.CreateToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Agent, false, manualRun: true);
                var call = new RNAssistant.Core.Agent.ToolCall("html-link-failure", HtmlWorkspaceToolCatalog.CreateToolId,
                    "{\"title\":\"Retained\"}");
                RuntimeThrows<System.IO.IOException>(() => ExecuteNative(runtime, call, runtime.Describe(call)));
                var owner = new ChatStore(paths).DocumentArtifacts;
                var artifact = owner.List(session).Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                AssertEqual("Retained", artifact.Title, "HTML remains discoverable after failed actor-link save");
                session.ActiveHtmlArtifactId = null; session.Artifacts.Clear(); session.HtmlWorkspace = new HtmlWorkspace();
                var retry = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertContains(retry.Result.DataJson, "html_attempt_already_published", "same operation cannot become a second workspace after selection loss");
                AssertEqual(RNAssistant.Core.Tools.ToolDispatchEvidence.NotDispatched, retry.Evidence.Dispatch, "recovery never replays the mutation");
                AssertEqual(1, owner.List(session).Count, "one retained workspace revision only");
            });
        }

        private static void SharedHtmlPublicationAndSelection()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var catalog = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var a = NewSession(adapter);
                Func<ChatSession, string, ToolRunResult> write = (session, html) => WriteSharedHtmlFixture(executor, session, html);
                var created = write(a, "<main>first</main>");
                AssertTrue(created.Success, "native HTML publication: " + created.Message);
                var first = a.Artifacts.Single(item => item.Id == a.ActiveHtmlArtifactId);
                var firstRef = ChatResourceUri.CreateArtifactRevision(a, first);
                AssertEqual(a.DocumentAuthorityId, first.DocumentAuthorityId, "HTML snapshot is document-owned before result projection");
                var dataWrite = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.WriteDataToolId,
                    "workspace", HtmlWorkspaceTargets.Workspace(chats.DocumentArtifacts, a, first), "name", "sales", "json", "{\"total\":42}"), catalog, new AppSettings(), false, false, a);
                AssertTrue(dataWrite.Success, "native bound JSON publication: " + dataWrite.Message);
                HtmlWorkspaceEditorResourceService.RefreshSelected(a, chats.DocumentArtifacts);
                var selected = a.Artifacts.Single(item => item.Id == a.ActiveHtmlArtifactId);
                var selectedRef = ChatResourceUri.CreateArtifactRevision(a, selected);
                var logicalId = HtmlWorkspaceIdentity.LogicalId(selected.Id);
                var logical = HtmlWorkspaceIdentity.Identity(a, logicalId);
                AssertEqual(selectedRef.Uri, chats.DocumentArtifacts.CurrentSnapshot(a, logical).Uri, "one shared current head");
                AssertTrue(executor.ResourceAuthority.Store.Capture(executor.ResourceAuthority.Scope(a, false)).Heads.Values
                    .All(entry => !entry.Identity.Uri.EndsWith("/html-workspace")), "no conversation HTML head is published");
                a.Messages.Add(new ChatMessage { Role = "assistant", Content = "HTML", ResourceRefs = new List<ResourceRef> { firstRef }, HtmlWorkspaceCheckpoint = firstRef });
                chats.Save(a);
                var b = NewSession(adapter); b.DocumentAuthorityId = a.DocumentAuthorityId;
                var picker = WorkingSetCatalog(links).List(b, new ArtifactCatalogRequest { Scope = "document", ChatId = b.Id });
                AssertEqual(1, picker.Items.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace), "picker exposes one current revision per workspace");
                links.Change(b, LinkRequest(b, selectedRef.Uri, false), chats.Save);
                b = new ChatStore(paths).Load(b.Id);
                AssertEqual(selected.Id, b.ActiveHtmlArtifactId, "selection survives restart from exact document references");
                AssertEqual("<main>first</main>", b.HtmlWorkspace.Files.Single().Content, "second chat reconstructs selected HTML from CAS");
                var dataRef = b.HtmlWorkspace.DataSources.Single().Binding.Resource;
                AssertEqual("{\"total\":42}", chats.DocumentArtifacts.Read(b, dataRef).InlineText, "JSON binding survives the actor change");
                var provider = new ChatArtifactResourceProvider(payloads: new ChatBlobStore(paths), documentArtifacts: chats.DocumentArtifacts);
                var member = provider.List(b, ChatHtmlResourceCatalog.FileKind, null, 20).Items.Single().Reference;
                var resolved = provider.ResolveIdentity(b, member.Identity);
                AssertEqual(member.Uri, resolved.Uri, "HTML member identity round-trips with document owner");
                AssertEqual("<main>first</main>", provider.Read(b, new ResourceReadRequest { Reference = member, MaxChars = 1000 }).Result.Text,
                    "exact member reads use the shared provider/CAS path");
                var stale = chats.Load(a.Id);
                var updated = write(b, "<main>second</main>");
                AssertTrue(updated.Success, "second chat edit: " + updated.Message);
                var head = b.Artifacts.Single(item => item.Id == b.ActiveHtmlArtifactId);
                var rejected = write(stale, "<main>must not win</main>");
                AssertTrue(!rejected.Success, "old source chat cannot overwrite the shared head");
                AssertEqual(head.Id, b.ActiveHtmlArtifactId, "stale mutation changes no selected snapshot");
                var headRef = chats.DocumentArtifacts.CurrentSnapshot(b, logical);
                AssertEqual(head.Id, chats.DocumentArtifacts.Read(b, headRef).Id, "current document head survives stale writer");
                AssertEqual("<main>first</main>", JObject.Parse(chats.DocumentArtifacts.Read(b, firstRef).InlineText)["Files"][0]["Content"].Value<string>(),
                    "historical snapshot remains immutable after the cross-chat write");
                var fork = NewSession(adapter); fork.DocumentAuthorityId = a.DocumentAuthorityId; fork.ParentSessionId = b.Id;
                fork.Messages = ChatCloneService.CloneMessages(a.Messages);
                ChatCloneService.PrepareForkResources(b, fork, chats.LoadArtifactBody, new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertEqual(b.ActiveHtmlArtifactId, fork.ActiveHtmlArtifactId, "fork preserves the selected shared revision instead of restoring an old message checkpoint");
                AssertEqual(firstRef.Uri, fork.Messages.Single().HtmlWorkspaceCheckpoint.Uri, "fork keeps old message evidence exact");
                AssertEqual(headRef.Uri, chats.DocumentArtifacts.CurrentSnapshot(b, logical).Uri, "fork never publishes a shared rollback");
                chats.Delete(a.Host, a.DocumentKey, a.Id);
                AssertEqual("{\"total\":42}", chats.DocumentArtifacts.Read(b, dataRef).InlineText, "origin chat deletion does not remove shared binding bytes");
                var prior = head.Id;
                executor.MutateLocalResources(b, "html.workspace.restore", new Dictionary<string, object> { ["snapshotId"] = first.Id }, () => {
                    HtmlWorkspaceArtifactService.RestoreAsRevision(b, first.Id); return true;
                });
                var restored = b.Artifacts.Single(item => item.Id == b.ActiveHtmlArtifactId);
                AssertTrue(restored.Id != first.Id && restored.Revision > head.Revision, "restore creates a new causal revision");
                AssertEqual(prior, restored.ParentArtifactId, "restore parent is the previous shared head");
                AssertEqual(first.Id, (string)JObject.Parse(restored.MetadataJson)["restoredFromArtifactId"], "restore records its exact source");
                AssertEqual("<main>first</main>", b.HtmlWorkspace.Files.Single().Content, "restored aggregate is verified");
                chats.Save(b);
                b = chats.Load(b.Id);
                AssertEqual(1, b.Artifacts.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace && item.InlineText != null),
                    "shared HTML restart loads only the active body, including after restore");
                b.Artifacts.RemoveAll(item => item.Id == first.Id);
                HtmlWorkspaceArtifactService.RebuildNavigation(b);
                AssertEqual(HtmlWorkspaceRecoveryIssues.ParentArtifactMissing, b.HtmlWorkspaceRecovery.Issue,
                    "missing restore navigation source is explicit instead of falling back to causal history");
                AssertTrue(b.HtmlWorkspaceRecovery.CanMutate, "readable active content survives missing undo metadata");
                b = chats.Load(b.Id);
                var redoId = b.HtmlWorkspace.RedoBranches.Single().Id;
                AssertEqual(head.Id, redoId, "redo survives restart and retains the former content revision");
                executor.MutateLocalResources(b, "html.workspace.redo", new Dictionary<string, object> { ["snapshotId"] = redoId },
                    () => { HtmlWorkspaceArtifactService.RestoreAsRevision(b, redoId, true); return true; });
                var redone = b.Artifacts.Single(item => item.Id == b.ActiveHtmlArtifactId);
                AssertEqual(restored.Id, redone.ParentArtifactId, "redo advances the causal head instead of selecting an old one");
                AssertEqual("<main>second</main>", b.HtmlWorkspace.Files.Single().Content, "redo restores content");
                AssertEqual(0, b.HtmlWorkspace.RedoBranches.Count, "redo consumes the pending branch");
                var restoredRef = ChatResourceUri.CreateArtifactRevision(b, redone);
                links.Change(b, LinkRequest(b, restoredRef.Uri, true), chats.Save);
                AssertTrue(string.IsNullOrEmpty(b.ActiveHtmlArtifactId), "unlink clears only the chat selection");
                AssertEqual(restoredRef.Uri, chats.DocumentArtifacts.CurrentSnapshot(b, logical).Uri, "unlink never rewinds or deletes the shared head");
                var oldPrompt = new ChatMessage { Role = "user", Content = "Earlier request", HtmlWorkspaceCheckpoint = firstRef };
                b.Messages.Add(oldPrompt);
                new ChatHistoryEditService(_ => { }, (_, reason) => { }, chats.LoadArtifactBody)
                    .RewriteUserMessage(b, b.Id, oldPrompt.Id, -1, "Rewritten request");
                AssertTrue(string.IsNullOrEmpty(b.ActiveHtmlArtifactId), "dialogue rewrite cannot resurrect detached shared HTML");
                var detachedFork = NewSession(adapter); detachedFork.DocumentAuthorityId = b.DocumentAuthorityId; detachedFork.ParentSessionId = b.Id;
                detachedFork.Messages = ChatCloneService.CloneMessages(b.Messages);
                ChatCloneService.PrepareForkResources(b, detachedFork, chats.LoadArtifactBody, new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertTrue(string.IsNullOrEmpty(detachedFork.ActiveHtmlArtifactId), "fork cannot resurrect detached shared HTML from an old checkpoint");
                AssertTrue(write(b, "<main>independent</main>").Success, "an unselected chat can create another workspace");
                AssertEqual(2, WorkingSetCatalog(links).List(b, new ArtifactCatalogRequest { Scope = "document", ChatId = b.Id }).Items.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace),
                    "independent workspaces coexist in the document");
                var c = NewSession(adapter); c.DocumentAuthorityId = b.DocumentAuthorityId;
                links.Change(c, LinkRequest(c, ChatResourceUri.ResolveArtifactRevision(b, b.ActiveHtmlArtifactId).Uri, false), chats.Save);
                var writers = new[] { b, c }.Select((chat, index) => Task.Run(() => write(chat, "<main>writer " + index + "</main>"))).ToArray();
                Task.WaitAll(writers);
                AssertEqual(1, writers.Count(task => task.Result.Success), "simultaneous writers publish only one child from the same base");
                AssertEqual(1, writers.Count(task => !task.Result.Success && task.Result.Message.Contains("changed after preparation")), "loser is rejected before dispatch");
                var gc = CasService(paths, new ChatStore(paths), new VbaJournalStore(paths), () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed && gc.Health.MissingBlobCount == 0, "shared HTML and JSON survive origin deletion and CAS collection");
                AssertContains(chats.DocumentArtifacts.Read(b, firstRef).InlineText, "first", "historical HTML remains readable after GC");
                var winner = new[] { b, c }.Single(chat => chat.Artifacts.Single(item => item.Id == chat.ActiveHtmlArtifactId).Revision == 2);
                var missing = winner.Artifacts.Single(item => item.Id == winner.ActiveHtmlArtifactId);
                System.IO.File.Delete(executor.Payloads.PathFor(missing.ContentSha256));
                links.Change(winner, LinkRequest(winner, ChatResourceUri.CreateArtifactRevision(winner, missing).Uri, true), chats.Save);
                AssertTrue(string.IsNullOrEmpty(winner.ActiveHtmlArtifactId), "missing HTML body does not prevent unlinking");
                AssertEqual(0, new ResourceMutationJournal(paths).Unresolved().Count, "publication and refusals leave no unknown attempt");
            });
        }
    }
}
