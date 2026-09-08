using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
        private static void SharedHtmlFailedLinkDoesNotReplay()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), paths: paths,
                    persistResourceFacts: saved => { throw new System.IO.IOException("Injected HTML link failure."); });
                var session = NewSession(adapter);
                var definition = executor.GetControllerTools().Single(item => item.Id == HtmlWorkspaceToolCatalog.WriteFileToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Agent, false);
                var call = new RNAssistant.Core.Agent.ToolCall("html-link-failure", HtmlWorkspaceToolCatalog.WriteFileToolId,
                    "{\"path\":\"index.html\",\"content\":\"<main>retained</main>\"}");
                RuntimeThrows<System.IO.IOException>(() => ExecuteNative(runtime, call, runtime.Describe(call)));
                var owner = new ChatStore(paths).DocumentArtifacts;
                var artifact = owner.List(session).Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace);
                AssertContains(owner.Read(session, ChatResourceUri.CreateArtifactRevision(session, artifact)).InlineText, "retained", "HTML remains discoverable after failed actor-link save");
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
                Func<ChatSession, string, ToolRunResult> write = (session, html) => executor.ExecuteManual(
                    Command(HtmlWorkspaceToolCatalog.WriteFileToolId, "path", "index.html", "content", html), catalog, new AppSettings(), false, false, session);
                var created = write(a, "<main>first</main>");
                AssertTrue(created.Success, "native HTML publication: " + created.Message);
                var first = a.Artifacts.Single(item => item.Id == a.ActiveHtmlArtifactId);
                var firstRef = ChatResourceUri.CreateArtifactRevision(a, first);
                AssertEqual(a.DocumentAuthorityId, first.DocumentAuthorityId, "HTML snapshot is document-owned before result projection");
                var dataWrite = executor.ExecuteManual(Command(HtmlWorkspaceToolCatalog.WriteDataToolId,
                    "name", "sales", "json", "{\"total\":42}"), catalog, new AppSettings(), false, false, a);
                AssertTrue(dataWrite.Success, "native bound JSON publication: " + dataWrite.Message);
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
                var picker = links.List(b, new DocumentArtifactListRequest { ChatId = b.Id });
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
                executor.MutateLocalResources(b, "common.html_workspace_restore", new Dictionary<string, object> { ["snapshotId"] = first.Id }, () => {
                    HtmlWorkspaceArtifactService.RestoreAsRevision(b, first.Id); return true;
                });
                var restored = b.Artifacts.Single(item => item.Id == b.ActiveHtmlArtifactId);
                AssertTrue(restored.Id != first.Id && restored.Revision > head.Revision, "restore creates a new causal revision");
                AssertEqual(prior, restored.ParentArtifactId, "restore parent is the previous shared head");
                AssertEqual(first.Id, (string)JObject.Parse(restored.MetadataJson)["restoredFromArtifactId"], "restore records its exact source");
                AssertEqual("<main>first</main>", b.HtmlWorkspace.Files.Single().Content, "restored aggregate is verified");
                chats.Save(b);
                b = chats.Load(b.Id);
                b.Artifacts.RemoveAll(item => item.Id == first.Id);
                HtmlWorkspaceArtifactService.RebuildNavigation(b);
                AssertEqual(HtmlWorkspaceRecoveryIssues.ParentArtifactMissing, b.HtmlWorkspaceRecovery.Issue,
                    "missing restore navigation source is explicit instead of falling back to causal history");
                AssertTrue(b.HtmlWorkspaceRecovery.CanMutate, "readable active content survives missing undo metadata");
                b = chats.Load(b.Id);
                var redoId = b.HtmlWorkspace.RedoBranches.Single().Id;
                AssertEqual(head.Id, redoId, "redo survives restart and retains the former content revision");
                executor.MutateLocalResources(b, "common.html_workspace_redo", new Dictionary<string, object> { ["snapshotId"] = redoId },
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
                AssertEqual(2, links.List(b, new DocumentArtifactListRequest { ChatId = b.Id }).Items.Count(item => item.Kind == ChatArtifactKinds.HtmlWorkspace),
                    "independent workspaces coexist in the document");
                var c = NewSession(adapter); c.DocumentAuthorityId = b.DocumentAuthorityId;
                links.Change(c, LinkRequest(c, ChatResourceUri.ResolveArtifactRevision(b, b.ActiveHtmlArtifactId).Uri, false), chats.Save);
                var writers = new[] { b, c }.Select((chat, index) => Task.Run(() => write(chat, "<main>writer " + index + "</main>"))).ToArray();
                Task.WaitAll(writers);
                AssertEqual(1, writers.Count(task => task.Result.Success), "simultaneous writers publish only one child from the same base");
                AssertEqual(1, writers.Count(task => task.Result.ErrorCode == "RESOURCE_REVISION_CHANGED"), "loser is rejected before dispatch");
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
