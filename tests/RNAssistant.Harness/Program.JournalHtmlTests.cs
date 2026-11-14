using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static string JournalHtmlResponse(int number, ChatSession chat, DocumentArtifactStore owner)
        {
            if (number == 3 && chat.Artifacts.Count == 0) Console.WriteLine("HTML create: " + string.Join(" | ",
                chat.Messages.Select(item => item.Content + " " + item.Activity?.DataJson)));
            if (number == 4) return ScriptedVerifiedFinish(chat, "Source published.", "Done.", "HTML created and written.", "html.workspace.create", "core.write");
            var call = number == 1 ? new ConversationToolCall { Name = "core.skills.load", Arguments = new JObject { ["id"] = "skills.html.workspace.authoring" } } :
                number == 2 ? new ConversationToolCall { Name = "html.workspace.create", Arguments = new JObject { ["title"] = "Journal HTML" } } :
                new ConversationToolCall { Name = "core.write", Arguments = new JObject { ["target"] = "HTML file: index.html in " +
                    HtmlWorkspaceTargets.Workspace(owner, chat, chat.Artifacts.Single(item => item.Kind == ChatArtifactKinds.HtmlWorkspace)), ["content"] = "<main>Native HTML</main>" } };
            return ModelProtocolWire.WriteAcceptedCall("Prepare the requested HTML source.", null, call);
        }

        private static void VerifyJournalHtmlRuntime(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources, ChatSession chat, int calls)
        {
            if (calls != 4 || chat.LastRun.KernelState.Summary.ToolCounts.WriteOk != 2)
                Console.WriteLine("HTML runtime: " + chat.LastRun.KernelState.Summary.Reason + " | " + string.Join(" | ", chat.Messages.Select(item => item.Content)));
            AssertEqual(4, calls, "schema, create, write and finish without replay");
            AssertEqual(2, chat.LastRun.KernelState.Summary.ToolCounts.WriteOk, "actual Office kernel verifies HTML mutations");
            AssertEqual("skills.html.workspace.authoring", chat.LastRun.SelectedSkills.Single().Id, "native reload retains exact skill selection and pins");
            IConversationStore store = journal; var blobs = new ChatBlobStore(paths);
            var owner = new DocumentArtifactStore(resources, resources, blobs);
            AssertTrue(chat.ActiveHtmlArtifactId == null && chat.Artifacts.All(item => item.InlineText == null), "model publication does not select or hydrate the UI");
            foreach (var call in chat.Messages.Where(item => item.AcceptedCallOrigin != null && item.ToolName != "core.skills.load"))
                AssertTrue(journal.LinkedResourceOutcome(chat.Id, call.RunId, call.ToolCallId) != null, "accepted call has exact resource proof");
            var latest = chat.Artifacts.OrderBy(item => item.Revision).Last();
            var exact = ChatResourceUri.CreateArtifactRevision(chat, latest);
            var links = new ArtifactWorkingSetService(owner, resources);
            journal.Fault = point => { if (point == "before_index") throw new IOException("lost selection index"); };
            links.Change(chat, LinkRequest(chat, exact.Uri, false), saved => store.Commit(saved, new ConversationCommit()));
            journal.Fault = null; chat = store.Load(chat.Id);
            AssertEqual(latest.Id, chat.ActiveHtmlArtifactId, "selection survives index loss");
            AssertEqual("<main>Native HTML</main>", chat.HtmlWorkspace.Files.Single().Content, "shared projection reads only selected source");
            AssertEqual(1, chat.Artifacts.Count(item => item.InlineText != null), "older HTML bodies stay cold");
            AssertEqual(1, chat.HtmlWorkspace.History.Count, "navigation is rebuilt from source metadata");
            var member = new ResourceRef(ResourceUri.Create("chat", chat.DocumentAuthorityId, "artifact", latest.Id, "revision", latest.Revision.ToString(),
                "member", "file", chat.HtmlWorkspace.Files.Single().Id), latest.Revision.ToString());
            var note = new ChatMessage { Role = "user", Content = "Keep this exact source", HtmlWorkspaceCheckpoint = exact,
                ResourceRefs = new List<ResourceRef> { member } };
            chat.Messages.Add(note); var command = ConversationCommit.UserInput(note);
            journal.Journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost checkpoint acknowledgement"); };
            RuntimeThrows<IOException>(() => store.Commit(chat, command)); journal.Journal.Fault = null;
            store.Commit(chat, command); var written = journal.Journal.WrittenBytes;
            store.Commit(chat, command); AssertEqual(written, journal.Journal.WrittenBytes, "lost receipt cannot duplicate HTML membership");
            var reads = journal.ChatBodyReads;
            var page = store.ReadMessageWindow(chat.Id, chat.Messages.Count, 1);
            AssertEqual(2L, journal.ChatBodyReads - reads, "page hydrates text and manifest, not the HTML aggregate");
            AssertEqual(latest.Id, page.Artifacts.Single().Id, "member and checkpoint resolve one snapshot header");
            AssertTrue(page.Artifacts.Single().InlineText == null && page.ActiveHtmlArtifactId == latest.Id, "page preserves selected metadata without source hydration");

            var bodyPath = blobs.PathFor(latest.ContentSha256); var retainedBytes = File.ReadAllBytes(bodyPath);
            try
            {
                File.WriteAllText(bodyPath, "corrupt");
                var degraded = store.Load(chat.Id);
                AssertTrue(!degraded.HtmlWorkspaceRecovery.CanMutate && degraded.HtmlWorkspace.Files.Count == 0,
                    "unavailable active body yields explicit noneditable recovery");
                var unrelated = new ChatMessage { Role = "user", Content = "Chat remains usable" };
                degraded.Messages.Add(unrelated); store.Commit(degraded, ConversationCommit.UserInput(unrelated));
                AssertEqual(latest.Id, store.Load(chat.Id).ActiveHtmlArtifactId, "independent input never drops the broken selection");
            }
            finally { File.WriteAllBytes(bodyPath, retainedBytes); }
            chat = store.Load(chat.Id);

            var versions = new DocumentHtmlVersionService(paths, owner, resources); var metadata = store.ReadMetadata(chat.Id);
            var target = versions.Resolve(metadata, new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope {
                Kind = BrowserOwnerKind.Document, ScopeId = chat.DocumentAuthorityId, ChatId = chat.Id },
                Locator = BrowserLocalLocator.ForResource(HtmlWorkspaceIdentity.LogicalId(latest.Id)) }).Target;
            var state = versions.State(metadata, new BrowserVersionRequest { Target = target });
            var savedVersion = versions.Command(metadata, new BrowserVersionCommand { Target = target, Action = BrowserVersionAction.Save,
                ExpectedStateRevision = state.StateRevision, ExpectedContentRevision = state.ContentRevision, OperationId = "native-html-version" }, CancellationToken.None);
            AssertEqual(BrowserVersionOutcome.Saved, savedVersion.Outcome, savedVersion.Message);
            journal.Rebuild(); chat = store.Load(chat.Id);
            AssertEqual(exact.Uri, chat.Messages.Single(item => item.Id == note.Id).HtmlWorkspaceCheckpoint.Uri, "rebuild preserves exact message checkpoint");
            AssertEqual("<main>Native HTML</main>", chat.HtmlWorkspace.Files.Single().Content, "restart reconstructs selected HTML");

            var authority = new ResourceAuthorityService(resources, resources, resources, blobs);
            var observer = new ResourceMutationAuthorityObserver(authority, resources, chat, blobs, current => store.Commit(current, new ConversationCommit()));
            var context = new ToolExecutionContext(new ToolCall("html-unknown", "html.source.write", "{}"), new ToolPolicySnapshot("html.source.write", "fixture", true),
                "run", "turn", "step", DateTime.UtcNow, true, 1);
            var prepared = JsonConvert.SerializeObject(new HtmlWorkspaceCommand { LogicalId = HtmlWorkspaceIdentity.LogicalId(latest.Id), Snapshot = exact });
            var args = new Dictionary<string, object> { ["path"] = "index.html" };
            var attempt = observer.Prepare(context, args, prepared);
            observer.MarkDispatchMayHaveOccurred(attempt); observer.ReleaseUnresolved(attempt);
            ResourceMutationAuthorityObserver.ReconcileInterrupted(authority, resources);
            AssertEqual(HeadKnowledge.Unknown, resources.GetHead(ResourceAuthorityScopeId.Document(new DocumentAuthorityId(chat.DocumentAuthorityId)),
                HtmlWorkspaceIdentity.Identity(chat, HtmlWorkspaceIdentity.LogicalId(latest.Id))).Knowledge, "restart retains unknown, never replays HTML");
            RuntimeThrows<ToolMutationPreparationException>(() => observer.Prepare(context, args, prepared));
            AssertContains(owner.Read(ConversationMetadata.From(chat), exact).InlineText, "Native HTML", "exact historical reads remain available");
            var independent = new ChatMessage { Role = "user", Content = "Independent input" };
            chat.Messages.Add(independent); store.Commit(chat, ConversationCommit.UserInput(independent));
            AssertEqual(independent.Id, store.Load(chat.Id).Messages.Last().Id, "unknown HTML does not disable chat input");
        }

        private static void JournalWorkspaceHtml()
        {
            WithTempPaths(paths => {
                var workspaces = new WorkspaceStore(paths);
                var workspace = workspaces.CreateHiddenHtmlProject(Path.Combine(paths.Root, "project"), "Native project", "index.html");
                var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var files = new WorkspaceFileService(paths, resources, resources, resources);
                files.CreateText(workspace, "index.html", "<main>Saved native project</main>");
                files.CreateText(workspace, "style.css", "main{color:green}");
                workspaces.SetHtmlProjectDefinition(workspace, null, new HtmlProjectDefinition { EntryPath = "index.html", Members = new[] { "index.html", "style.css" } });
                workspace = workspaces.ConnectProject(workspace.RootPath, false);
                var scope = "workspace:" + workspace.WorkspaceId; var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var chat = workspaces.CreateSession(store, workspace, "HTML versions");
                chat.LastRun = new ChatRunRecord { RunId = "version-run", TurnId = "version-turn", Status = "running" };
                var input = new ChatMessage { Role = "user", Content = "Save this project", RunId = chat.LastRun.RunId };
                chat.Messages.Add(input); store.Commit(chat, ConversationCommit.UserInput(input));
                var reads = journal.ChatBodyReads;
                AssertEqual(chat.LastRun.RunId, store.ReadHeader(chat.Id).RunId, "addressed header retains exact run owner");
                AssertEqual(reads, journal.ChatBodyReads, "header requires no transcript or runtime body");
                var versions = new WorkspaceHtmlVersionService(paths, store, files, resources, resources);
                var caller = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = workspace.WorkspaceId, ChatId = chat.Id };
                var target = versions.Resolve(new BrowserResolveRequest { OwnerScope = caller, Locator = BrowserLocalLocator.ForProject(workspace.WorkspaceId, "index.html") }).Target;
                var state = versions.State(new BrowserVersionRequest { Target = target });
                var save = new BrowserVersionCommand { Target = target, Action = BrowserVersionAction.Save, ExpectedStateRevision = state.StateRevision,
                    ExpectedContentRevision = state.ContentRevision, ExpectedDefinitionRevision = state.DefinitionRevision, OperationId = "native-project-save" };
                var actor = new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = chat.Id, RunId = chat.LastRun.RunId };
                var result = versions.Save(target, actor, save, CancellationToken.None);
                AssertEqual(result.VersionId, versions.Save(target, actor, save, CancellationToken.None).VersionId, "same publication receipt does not recapture or write files");
                RuntimeThrows<SavedVersionException>(() => versions.Save(target, new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = chat.Id, RunId = "foreign" }, save, CancellationToken.None));
                var owner = new DocumentArtifactStore(resources, resources, blobs);
                var reference = result.Version.ContentReference;
                var artifact = owner.Read(ConversationMetadata.From(chat), reference, false); chat.Artifacts.Add(artifact);
                ArtifactWorkingSet.Set(chat, artifact, false);
                var message = new ChatMessage { Role = "user", Content = "Retained project", ResourceRefs = new List<ResourceRef> { reference } };
                chat.Messages.Add(message); store.Commit(chat, ConversationCommit.UserInput(message));
                journal.Rebuild(); var restored = store.Load(chat.Id);
                AssertEqual(artifact.Id, restored.Artifacts.Single().Id, "workspace saved artifact survives native rebuild");
                AssertTrue(restored.Artifacts.Single().InlineText == null && restored.ActiveHtmlArtifactId == null, "saved project is metadata, not document HTML selection");
                AssertTrue(store.ReadMessageWindow(chat.Id, 2, 1).Artifacts.Single().InlineText == null, "saved project pages stay cold");
                var backup = Path.Combine(paths.Root, "html-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var copiedPaths = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "html-restore"), _ => StorageProtector.None);
                var copiedResources = new ResourceJournal(copiedPaths); IConversationStore copiedChats = new ConversationJournal(copiedPaths, scope);
                AssertEqual(artifact.Id, copiedChats.Load(chat.Id).Artifacts.Single().Id, "restored journal retains exact project membership");
                Directory.Move(workspace.RootPath, workspace.RootPath + "-offline");
                var restarted = new WorkspaceHtmlVersionService(paths, store, files, resources, resources);
                var savedTarget = restarted.Resolve(new BrowserResolveRequest { OwnerScope = caller,
                    Locator = BrowserLocalLocator.ForProject(workspace.WorkspaceId, "index.html", result.VersionId) }).Target;
                AssertTrue(savedTarget != null && savedTarget.State == BrowserTargetState.Ready, "actual saved-project consumer works after restart with no live root");
                var savedOwner = new DocumentArtifactStore(copiedResources, copiedResources, new ChatBlobStore(copiedPaths));
                AssertEqual("<main>Saved native project</main>", System.Text.Encoding.UTF8.GetString(savedOwner.ReadWorkspaceHtmlMember(workspace.WorkspaceId, result.VersionId, "index.html")), "restore reads exact CAS member");
                var copiedProject = restarted.CopySavedProject(savedTarget, CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, copiedProject.Status, copiedProject.Message);
                AssertEqual("<main>Saved native project</main>", File.ReadAllText(Path.Combine(copiedProject.RootPath, copiedProject.EntryPath)), "copy uses supplied native file owner");
                AssertTrue(!Directory.Exists(paths.ChatDirectory) || !Directory.EnumerateFiles(paths.ChatDirectory, "*", SearchOption.AllDirectories).Any(), "no legacy chat writer is introduced");
            });
        }
    }
}
