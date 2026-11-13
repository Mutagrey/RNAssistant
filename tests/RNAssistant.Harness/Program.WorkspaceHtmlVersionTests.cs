using System;
using System.IO;
using System.Linq;
using System.Threading;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void WorkspaceHtmlCheckpoints()
        {
            WithPluginPaths(paths => {
                var imported = new HtmlProjectImportService(paths).Import(HtmlProjectSourceFixture(), CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, imported.Status, imported.Message);
                var service = new WorkspaceHtmlVersionService(paths);
                var user = new BrowserOwnerScope { Kind = BrowserOwnerKind.User, ScopeId = "local" };
                var source = service.Resolve(new BrowserResolveRequest { OwnerScope = user,
                    Locator = BrowserLocalLocator.ForProject(imported.WorkspaceId, imported.EntryPath) }).Target;
                Func<BrowserVersionCommand> prepare = () => {
                    var state = service.State(new BrowserVersionRequest { Target = source });
                    return new BrowserVersionCommand { Target = source, Action = BrowserVersionAction.Save,
                        ExpectedContentRevision = state.ContentRevision, ExpectedDefinitionRevision = state.DefinitionRevision,
                        ExpectedStateRevision = state.StateRevision, OperationId = Guid.NewGuid().ToString("N") };
                };
                var firstCommand = prepare(); var first = service.Command(firstCommand);
                AssertEqual(BrowserVersionOutcome.Saved, first.Outcome, first.Message);
                AssertEqual(1L, first.Version.Ordinal, "first explicit ordinal");
                AssertEqual(0, new ChatStore(paths).ListHeaders().Count, "manual save never creates a chat");
                AssertEqual(BrowserVersionOutcome.AlreadySaved, service.Command(prepare()).Outcome, "latest available content deduplicates");
                var stale = prepare();
                var entry = Path.Combine(imported.RootPath, imported.EntryPath);
                var original = File.ReadAllText(entry); var stamp = File.GetLastWriteTimeUtc(entry);
                File.WriteAllText(entry, "<h1>different bytes</h1>"); File.SetLastWriteTimeUtc(entry, stamp);
                AssertEqual(BrowserVersionOutcome.Rejected, service.Command(stale).Outcome, "same mtime is not content proof");
                var second = service.Command(prepare()); AssertEqual(2L, second.Version.Ordinal, second.Message);
                var savedLocator = BrowserLocalLocator.ForProject(imported.WorkspaceId, imported.EntryPath, first.Version.VersionId);
                var saved = service.Resolve(new BrowserResolveRequest { OwnerScope = user, Locator = savedLocator }).Target;
                Directory.Move(imported.RootPath, imported.RootPath + "-offline");
                var restart = new WorkspaceHtmlVersionService(paths);
                var exact = restart.Resolve(new BrowserResolveRequest { OwnerScope = user, Locator = savedLocator }).Target;
                AssertEqual(original, restart.Source(new BrowserSourceRequest { OwnerScope = user, Locator = savedLocator,
                    Count = 32000, RequestGeneration = 1 }, exact).Text, "saved source comes only from CAS without live root");
                AssertEqual(2, restart.Versions(new BrowserVersionRequest { Target = exact }).Items.Count, "metadata history survives offline root");
                var removed = restart.Command(new BrowserVersionCommand { Target = exact, Action = BrowserVersionAction.Delete,
                    VersionId = first.Version.VersionId, ExpectedStateRevision = second.State.StateRevision, Confirmed = true, OperationId = "delete-first" });
                AssertEqual(BrowserVersionOutcome.Removed, removed.Outcome, removed.Message);
                AssertEqual(BrowserTargetState.Removed, restart.Resolve(new BrowserResolveRequest { OwnerScope = user, Locator = savedLocator }).Target.State, "no latest fallback");
                Directory.Move(imported.RootPath + "-offline", imported.RootPath);
                AssertTrue(restart.Command(firstCommand).Version.Removed, "original receipt reports subsequent tombstone");
                File.WriteAllText(entry, original);
                var third = service.Command(prepare()); AssertEqual(3L, third.Version.Ordinal, "same historical bytes get a new monotonic entry");
                var forged = new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = "missing", RunId = "other" };
                SavedFailure(() => service.Save(source, forged, prepare(), CancellationToken.None), "version_actor_invalid");
                var wrong = service.Resolve(new BrowserResolveRequest { OwnerScope = new BrowserOwnerScope { Kind = BrowserOwnerKind.Workspace, ScopeId = "other" }, Locator = savedLocator });
                AssertTrue(wrong.Target == null, "foreign workspace never resolves a saved entry");
                var artifacts = new ChatStore(paths).DocumentArtifacts;
                var record = artifacts.WorkspaceHtmlVersion(imported.WorkspaceId, third.Version.VersionId);
                var snapshot = artifacts.ReadWorkspaceHtml(imported.WorkspaceId, third.Version.VersionId);
                var authority = new ResourceAuthorityStore(paths);
                AssertEqual(snapshot.Members.Count, authority.GetRevision(DocumentArtifactStore.WorkspaceHtmlScope(imported.WorkspaceId), record.ContentReference).Dependencies.Count,
                    "manifest retains every exact file dependency for reachability");
            });
        }
    }
}
