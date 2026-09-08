using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ArtifactWorkingSetMetadataRecovery()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var paths = FixturePaths.Value;
                var chats = new ChatStore(paths);
                var authority = new ResourceAuthorityStore(paths);
                var blobs = new ChatBlobStore(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var a = NewSession(adapter);
                AssertTrue(executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Lost metadata Plan", "markdown", "# Exact retained body", "status", "draft"),
                    tools, new AppSettings(), false, false, a).Success, "publish recovery subject");
                var artifact = a.Artifacts.Single(item => item.Id == a.ActivePlanDocumentArtifactId);
                var exact = ChatResourceUri.CreateArtifactRevision(a, artifact);
                a.Messages.Add(new ChatMessage { Role = "assistant", Content = "Plan saved", ResourceRefs = new List<ResourceRef> { exact } });
                chats.Save(a);
                var b = NewSession(adapter);
                b.DocumentAuthorityId = a.DocumentAuthorityId;
                links.Change(b, LinkRequest(b, exact.Uri, false), chats.Save);
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), chats.DocumentArtifacts);
                var draft = ingestion.Stage(a, "Healthy original.md", "text/markdown", Encoding.UTF8.GetBytes("# Healthy original"));
                var message = new ChatMessage { Role = "user", Content = "Healthy original", Attachments = ingestion.LoadDrafts(a, new[] { draft.Id }).ToList() };
                a.Messages.Add(message);
                ingestion.CommitAndLink(a, message, a.Messages.Count - 1);
                chats.Save(a);
                var original = message.ResourceRefs.Single();
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(a.DocumentAuthorityId));
                var metadata = authority.GetView(scope, exact, "artifact-plan-record").Payload;
                var metadataPath = blobs.PathFor(metadata.Sha256);
                var retainedBytes = File.ReadAllBytes(metadataPath);
                File.Delete(metadataPath);
                a = new ChatStore(paths).Load(a.Id);
                b = new ChatStore(paths).Load(b.Id);
                AssertTrue(a != null && b != null, "missing Plan metadata no longer hides its origin or linked chat");
                var missing = a.Artifacts.Single(item => item.Id == artifact.Id);
                AssertEqual("metadata_unavailable", missing.AvailabilityIssue, "recovery is an explicit unavailable projection");
                AssertTrue(missing.Title == null && missing.MetadataJson == null && missing.InlineText == null && missing.ContentSha256 == null,
                    "recovery invents no durable metadata or body");
                AssertEqual(artifact.Id, b.ActivePlanDocumentArtifactId, "selection is retained as unavailable, not replaced or silently reset");
                AssertEqual("metadata_unavailable", ChatArtifactDto.From(b).Single().AvailabilityIssue, "typed UI projection carries the issue");
                var prompt = ChatResourcePromptIndex.Build(a, 1000);
                AssertContains(prompt, "unavailable=1", "model gets an actionable availability summary");
                AssertTrue(!prompt.Contains("Lost metadata Plan"), "missing metadata cannot become a guessed semantic target");
                var catalog = links.List(a, new DocumentArtifactListRequest { ChatId = a.Id });
                AssertTrue(catalog.Items.Count == 2 && catalog.Items.Any(item => item.Title == "Healthy original.md" && item.AvailabilityIssue == null),
                    "one unavailable Plan does not block other document picker entries");
                RuntimeThrows<InvalidDataException>(() => links.Change(b, LinkRequest(b, exact.Uri, false), chats.Save));
                RuntimeThrows<InvalidDataException>(() => chats.DocumentArtifacts.Read(a, exact));
                var unchangedHead = authority.Capture(scope).Generation;
                links.Change(b, LinkRequest(b, exact.Uri, true), chats.Save);
                AssertEqual(unchangedHead, authority.Capture(scope).Generation, "unlink does not repair or replace resource authority");
                AssertTrue(new ChatStore(paths).Load(b.Id).ActivePlanDocumentArtifactId == null, "unavailable Plan can be unlinked durably");
                AssertEqual(exact.Uri, a.Messages.First().ResourceRefs.Single().Uri, "historical provenance survives metadata loss");
                File.WriteAllBytes(metadataPath, retainedBytes);
                a = new ChatStore(paths).Load(a.Id);
                AssertTrue(a.Artifacts.Single(item => item.Id == artifact.Id).AvailabilityIssue == null, "restored exact metadata removes disposable issue on reload");
                b = new ChatStore(paths).Load(b.Id);
                AssertTrue(!links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Single(item => item.ResourceUri == exact.Uri).Linked,
                    "metadata recovery does not silently reattach a removed link");

                var identity = DocumentArtifactStore.PlanIdentity(a, DocumentArtifactStore.PlanIdFromSnapshot(exact));
                var before = authority.Capture(scope);
                authority.Publish(ResourceAuthorityCommit.Create(scope, before.Generation, null,
                    new[] { new ResourceHeadChange(identity, before.GetHead(identity), ResourceHeadState.Unknown(identity, before.Generation + 1, "test-lost-readback")) },
                    AuthorityCommitReason.DerivedPublication));
                var unknown = links.List(a, new DocumentArtifactListRequest { ChatId = a.Id });
                AssertEqual("head_unavailable", unknown.Items.Single(item => item.ResourceUri == exact.Uri).AvailabilityIssue,
                    "unknown current head is explicit rather than a latest-snapshot substitution");
                AssertEqual(2, unknown.Items.Count, "unknown head does not prevent listing unrelated originals");
                RuntimeThrows<InvalidDataException>(() => links.Change(a, LinkRequest(a, exact.Uri, false), chats.Save));
                links.Change(a, LinkRequest(a, exact.Uri, true), chats.Save);
                AssertEqual("# Exact retained body", chats.DocumentArtifacts.Read(a, exact).InlineText, "unknown currentness preserves exact historical reads");

                var originalMetadata = authority.GetView(scope, original, "artifact-original-record").Payload;
                File.WriteAllText(blobs.PathFor(originalMetadata.Sha256), "corrupt metadata");
                a = new ChatStore(paths).Load(a.Id);
                AssertTrue(a != null && a.Artifacts.Single(item => item.Id.StartsWith("attachment_", StringComparison.Ordinal)).AvailabilityIssue != null,
                    "corrupt original metadata does not hide the chat");
                links.Change(a, LinkRequest(a, original.Uri, true), chats.Save);
                AssertTrue(!ArtifactLibraryProjectionService.Project(new ChatStore(paths).Load(a.Id)).Heads.Any(),
                    "corrupt original can be unlinked without deleting its source message");
                var fork = NewSession(adapter);
                fork.DocumentAuthorityId = a.DocumentAuthorityId;
                fork.ParentSessionId = a.Id;
                fork.Messages = ChatCloneService.CloneMessages(a.Messages);
                ChatCloneService.PrepareForkResources(a, fork, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertTrue(fork.Artifacts.Any(item => item.AvailabilityIssue == "metadata_unavailable") &&
                    fork.Messages.Last().ResourceRefs.Single().Uri == original.Uri,
                    "fork keeps explicit unavailable state and exact provenance without rebuilding metadata from messages");

            });
        }

        private static ArtifactLinkChangeRequest LinkRequest(ChatSession session, string uri, bool detached)
        {
            return new ArtifactLinkChangeRequest { ChatId = session.Id, ResourceUri = uri,
                ExpectedSessionRevision = session.Revision, Detached = detached };
        }

        private static void ArtifactWorkingSetPlanLifecycle()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var chats = new ChatStore(FixturePaths.Value);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(FixturePaths.Value));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var a = NewSession(adapter);
                AssertTrue(executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Working set Plan", "markdown", "# First body", "status", "draft"),
                    tools, new AppSettings(), false, false, a).Success, "create shared Plan");
                var first = a.Artifacts.Single(item => item.Id == a.ActivePlanDocumentArtifactId);
                var b = NewSession(adapter);
                b.DocumentAuthorityId = a.DocumentAuthorityId;
                var listed = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id });
                var candidate = listed.Items.Single();
                AssertTrue(!candidate.Linked && !candidate.Selected && b.Artifacts.Count == 0, "discovery leaves chat membership unchanged");
                links.Change(b, LinkRequest(b, candidate.ResourceUri, false), chats.Save);
                b = new ChatStore(FixturePaths.Value).Load(b.Id);
                AssertEqual(first.Id, b.ActivePlanDocumentArtifactId, "arbitrary empty chat retains exact selected Plan on restart");
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(b);
                ChatResourceReferenceService.PruneUnreachable(b);
                AssertEqual(first.Id, b.ActivePlanDocumentArtifactId, "explicit selection survives history editing without source messages");
                AssertTrue(executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Working set Plan", "markdown", "# Continued in B", "status", "ready"),
                    tools, new AppSettings(), false, false, b).Success, "selected Plan can be continued through native runtime");
                var current = b.Artifacts.Single(item => item.Id == b.ActivePlanDocumentArtifactId);
                var exact = ChatResourceUri.CreateArtifactRevision(b, current);
                b.Messages.Add(new ChatMessage { Role = "assistant", Content = "Saved Plan", ResourceRefs = new List<ResourceRef> { exact } });
                chats.Save(b);
                links.Change(b, LinkRequest(b, exact.Uri, true), chats.Save);
                b = new ChatStore(FixturePaths.Value).Load(b.Id);
                AssertTrue(b.ActivePlanDocumentArtifactId == null, "detach clears Plan selection durably");
                AssertEqual(exact.Uri, b.Messages.Last().ResourceRefs.Single().Uri, "detach preserves immutable message provenance");
                AssertTrue(!ArtifactLibraryProjectionService.Project(b).Heads.Any(item => item.Kind == ChatArtifactKinds.PlanDocument), "detached Plan is absent from working-set heads");
                AssertTrue(!ChatResourcePromptIndex.Build(b, 1000).Contains("Working set Plan"), "next prompt manifest excludes detached Plan");
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(b);
                AssertTrue(b.ActivePlanDocumentArtifactId == null, "old messages cannot reactivate a detached Plan");
                AssertEqual("# First body", chats.DocumentArtifacts.Read(a, ChatResourceUri.CreateArtifactRevision(a, first)).InlineText,
                    "other chat's exact historical revision survives");
                var fork = NewSession(adapter);
                fork.DocumentAuthorityId = b.DocumentAuthorityId;
                fork.ParentSessionId = b.Id;
                fork.Messages = ChatCloneService.CloneMessages(b.Messages);
                ChatCloneService.PrepareForkResources(b, fork, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertTrue(fork.ActivePlanDocumentArtifactId == null && ArtifactWorkingSet.IsDetached(fork, current), "fork preserves detached membership");
                links.Change(b, LinkRequest(b, exact.Uri, false), chats.Save);
                AssertEqual(current.Id, b.ActivePlanDocumentArtifactId, "same retained Plan can be selected again without copying");
                File.Delete(new ChatBlobStore(FixturePaths.Value).PathFor(current.ContentSha256));
                b = new ChatStore(FixturePaths.Value).Load(b.Id);
                links.Change(b, LinkRequest(b, exact.Uri, true), chats.Save);
                AssertTrue(b.ActivePlanDocumentArtifactId == null, "missing body does not block unlinking metadata");
                RuntimeThrows<InvalidDataException>(() => chats.DocumentArtifacts.Read(b, exact));
            });
        }

        private static void ArtifactWorkingSetRejectsRaces()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var chats = new ChatStore(FixturePaths.Value);
                var journal = new ResourceMutationJournal(FixturePaths.Value);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, journal);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var a = NewSession(adapter);
                executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId, "title", "Race Plan", "markdown", "# First", "status", "draft"),
                    tools, new AppSettings(), false, false, a);
                var b = NewSession(adapter);
                b.DocumentAuthorityId = a.DocumentAuthorityId;
                var first = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Single();
                executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId, "title", "Race Plan", "markdown", "# Second", "status", "ready"),
                    tools, new AppSettings(), false, false, a);
                RuntimeThrows<InvalidOperationException>(() => links.Change(b, LinkRequest(b, first.ResourceUri, false), chats.Save));
                AssertEqual(0, b.Artifacts.Count, "stale picker cannot silently choose a newer Plan");
                var next = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Single();
                var request = LinkRequest(b, next.ResourceUri, false);
                chats.Save(b);
                RuntimeThrows<InvalidOperationException>(() => links.Change(b, request, chats.Save));
                request = LinkRequest(b, next.ResourceUri, false);
                using (journal.AcquireScope(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(b.DocumentAuthorityId))))
                    RuntimeThrows<IOException>(() => links.Change(b, request, chats.Save));
                AssertTrue(b.ActivePlanDocumentArtifactId == null && b.ArtifactLinks.Count == 0, "live document writer blocks selection before any session change");
                var foreign = NewSession(adapter);
                foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                RuntimeThrows<InvalidOperationException>(() => links.Change(foreign, LinkRequest(foreign, next.ResourceUri, false), chats.Save));
                request.ChatId = null;
                RuntimeThrows<InvalidOperationException>(() => links.Change(b, request, chats.Save));
                links.Change(b, LinkRequest(b, next.ResourceUri, false), chats.Save);
                var stale = ChatCloneService.CloneSessionSnapshot(b);
                links.Change(b, LinkRequest(b, next.ResourceUri, true), chats.Save);
                RuntimeThrows<ChatConcurrencyException>(() => links.Change(stale, LinkRequest(stale, next.ResourceUri, false), chats.Save));
                AssertTrue(new ChatStore(FixturePaths.Value).Load(b.Id).ActivePlanDocumentArtifactId == null,
                    "stale independent session cannot overwrite a committed unlink");
            });
        }

        private static void ArtifactWorkingSetOriginalLifecycle()
        {
            WithTempPaths(paths =>
            {
                var chats = new ChatStore(paths);
                var links = new ArtifactWorkingSetService(chats.DocumentArtifacts, new ResourceMutationJournal(paths));
                var a = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                a.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                var ingestion = new ChatResourceIngestionService(new AttachmentStore(paths), chats.DocumentArtifacts);
                var draft = ingestion.Stage(a, "Shared specification.md", "text/markdown", Encoding.UTF8.GetBytes("# Shared original"));
                var message = new ChatMessage { Role = "user", Content = "Read", Attachments = ingestion.LoadDrafts(a, new[] { draft.Id }).ToList() };
                a.Messages.Add(message);
                ingestion.CommitAndLink(a, message, 0);
                chats.Save(a);
                var b = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                b.DocumentAuthorityId = a.DocumentAuthorityId;
                var exact = message.ResourceRefs.Single();
                links.Change(b, LinkRequest(b, exact.Uri, false), chats.Save);
                b = new ChatStore(paths).Load(b.Id);
                AssertEqual(1, ArtifactLibraryProjectionService.Project(b).Heads.Count, "attached original reloads without a message copy");
                ChatResourceReferenceService.PruneUnreachable(b);
                AssertEqual(1, b.Artifacts.Count, "explicit original link is a reachability root");
                links.Change(a, LinkRequest(a, exact.Uri, true), chats.Save);
                AssertEqual(0, ArtifactLibraryProjectionService.Project(a).Heads.Count, "unlink hides origin working-set entry");
                AssertEqual(1, ArtifactLibraryProjectionService.Project(b).Heads.Count, "other chat link remains independent");
                AssertEqual(exact.Uri, a.Messages.Single().ResourceRefs.Single().Uri, "origin message still has its provenance");
                AssertTrue(links.List(a, new DocumentArtifactListRequest { ChatId = a.Id }).Items.Single().Linked == false,
                    "unlinked document original remains discoverable for reattachment");
                var clearer = new ChatHistoryEditService(_ => { }, (session, reason) => { });
                clearer.Clear(b, new DocumentContext());
                chats.Save(b);
                AssertEqual(0, new ChatStore(paths).Load(b.Id).Artifacts.Count, "clear resets memberships as well as messages");
                AssertEqual(1, chats.DocumentArtifacts.List(a).Count, "clear/unlink never deletes a document resource");
                for (var index = 0; index < 50; index++)
                {
                    var duplicate = ingestion.Stage(a, "Shared specification.md", "text/markdown", Encoding.UTF8.GetBytes("# Shared original"));
                    var nextMessage = new ChatMessage { Role = "user", Content = "Another original",
                        Attachments = ingestion.LoadDrafts(a, new[] { duplicate.Id }).ToList() };
                    a.Messages.Add(nextMessage);
                    ingestion.CommitAndLink(a, nextMessage, a.Messages.Count - 1);
                }
                var page = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id });
                AssertEqual(50, page.Items.Count, "picker page is bounded even with duplicate titles");
                var tail = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id, Cursor = page.NextCursor });
                AssertTrue(page.HasMore && !tail.HasMore && tail.Items.Count == 1 &&
                    !page.Items.Any(item => item.ResourceUri == tail.Items[0].ResourceUri), "continuation reaches every duplicate without repetition");
                RuntimeThrows<InvalidOperationException>(() => links.List(a, new DocumentArtifactListRequest { ChatId = a.Id, Cursor = page.NextCursor }));
                RuntimeThrows<InvalidOperationException>(() => links.List(b, new DocumentArtifactListRequest { ChatId = b.Id, Query = "Shared", Cursor = page.NextCursor }));
                links.Change(b, LinkRequest(b, tail.Items[0].ResourceUri, false), chats.Save);
                RuntimeThrows<InvalidOperationException>(() => links.List(b, new DocumentArtifactListRequest { ChatId = b.Id, Cursor = page.NextCursor }));

            });
        }
    }
}
