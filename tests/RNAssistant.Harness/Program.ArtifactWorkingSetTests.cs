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
