using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Application;
using RNAssistant.Application.Contracts;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static WorkspaceHtmlVersionService WorkspaceHtmlVersionsFixture(AppDataPaths paths)
        {
            var resources = new ResourceAuthorityStore(paths); var mutations = new ResourceMutationJournal(paths);
            return new WorkspaceHtmlVersionService(paths, new ChatConversationStoreAdapter(new ChatStore(paths)),
                new WorkspaceFileService(paths, resources, resources, mutations), resources, mutations);
        }

        private static void WorkspaceHtmlCheckpoints()
        {
            WithPluginPaths(paths => {
                var imported = new HtmlProjectImportService(paths).Import(HtmlProjectSourceFixture(), CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, imported.Status, imported.Message);
                var workspaces = new WorkspaceStore(paths); var workspace = workspaces.OpenConnected(imported.WorkspaceId);
                var definition = workspaces.GetHtmlProjectDefinition(workspace);
                var binary = new byte[] { 0xff, 0, 1, 2, 0x80 };
                Directory.CreateDirectory(Path.Combine(imported.RootPath, "assets"));
                File.WriteAllBytes(Path.Combine(imported.RootPath, "assets/image.bin"), binary);
                workspaces.SetHtmlProjectDefinition(workspace, definition.Fingerprint, new HtmlProjectDefinition {
                    EntryPath = definition.Definition.EntryPath, Members = definition.Definition.Members.Concat(new[] { "assets/image.bin" }).ToArray() });
                var service = WorkspaceHtmlVersionsFixture(paths);
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
                var restart = WorkspaceHtmlVersionsFixture(paths);
                var exact = restart.Resolve(new BrowserResolveRequest { OwnerScope = user, Locator = savedLocator }).Target;
                AssertEqual(original, restart.Source(new BrowserSourceRequest { OwnerScope = user, Locator = savedLocator,
                    Count = 32000, RequestGeneration = 1 }, exact).Text, "saved source comes only from CAS without live root");
                var suggestions = restart.SavedBrowserTargets(exact.OwnerScope, exact.Title, 8);
                AssertTrue(suggestions.Any(s => s.SavedVersionId == first.Version.VersionId), "saved metadata completion works with an offline root");
                AssertEqual(2, restart.Versions(new BrowserVersionRequest { Target = exact }).Items.Count, "metadata history survives offline root");
                var comparison = new BrowserSourceComparisonRequest { Target = exact, BeforeId = second.Version.VersionId, AfterId = first.Version.VersionId, Path = imported.EntryPath };
                var compared = restart.Compare(comparison);
                AssertEqual("<h1>different bytes</h1>", compared.BeforeText, "comparison reads the saved member without live files");
                AssertEqual(original, compared.AfterText, "comparison keeps the exact other snapshot");
                using (var export = restart.ExportSavedProject(exact, CancellationToken.None))
                using (var zip = new ZipArchive(export.Stream, ZipArchiveMode.Read, true)) {
                    using (var file = zip.GetEntry("assets/image.bin").Open()) using (var bytes = new MemoryStream()) {
                        file.CopyTo(bytes); AssertTrue(binary.SequenceEqual(bytes.ToArray()), "export streams exact binary CAS member offline");
                    }
                    using (var file = new StreamReader(zip.GetEntry(imported.EntryPath).Open())) AssertEqual(original, file.ReadToEnd(), "export never borrows current source");
                    AssertEqual(definition.Definition.Members.Length + 1, zip.Entries.Count, "only declared members exported");
                }
                var copied = restart.CopySavedProject(exact, CancellationToken.None);
                AssertEqual(HtmlProjectImportStatus.Created, copied.Status, copied.Message);
                AssertTrue(copied.WorkspaceId != imported.WorkspaceId, "copy creates an independent project");
                AssertTrue(binary.SequenceEqual(File.ReadAllBytes(Path.Combine(copied.RootPath, "assets/image.bin"))), "binary bytes preserved by existing file mutation owner");
                AssertEqual(original, File.ReadAllText(Path.Combine(copied.RootPath, copied.EntryPath)), "copied exact old source");
                var config = JsonConvert.DeserializeObject<WorkspaceManifest>(File.ReadAllText(Path.Combine(copied.RootPath, ".rnassistant", "workspace.json")));
                AssertTrue(config.HtmlOrigin.ResourceUri.Contains(imported.WorkspaceId), "source provenance retained in project config");
                AssertEqual(0, new ChatStore(paths).ListHeaders().Count, "copy/export never invent a chat");
                using (var cancellation = new CancellationTokenSource()) {
                    cancellation.Cancel(); RuntimeThrows<OperationCanceledException>(() => restart.ExportSavedProject(exact, cancellation.Token));
                    AssertEqual(HtmlProjectImportStatus.Rejected, restart.CopySavedProject(exact, cancellation.Token).Status, "cancelled copy does not publish");
                }
                var removed = restart.Command(new BrowserVersionCommand { Target = exact, Action = BrowserVersionAction.Delete,
                    VersionId = first.Version.VersionId, ExpectedStateRevision = second.State.StateRevision, Confirmed = true, OperationId = "delete-first" });
                AssertEqual(BrowserVersionOutcome.Removed, removed.Outcome, removed.Message);
                AssertEqual(BrowserTargetState.Removed, restart.Resolve(new BrowserResolveRequest { OwnerScope = user, Locator = savedLocator }).Target.State, "no latest fallback");
                SavedFailure(() => restart.Compare(comparison), "version_removed");
                AssertTrue(!restart.SavedBrowserTargets(exact.OwnerScope, exact.Title, 8).Any(s => s.SavedVersionId == first.Version.VersionId), "removed entry omitted from completion");
                RuntimeThrows<InvalidOperationException>(() => restart.ExportSavedProject(exact, CancellationToken.None));
                RuntimeThrows<InvalidOperationException>(() => restart.CopySavedProject(exact, CancellationToken.None));
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

        private static void WorkspaceHtmlAgentRegistration()
        {
            WithTempPaths(paths =>
            {
                var store = new WorkspaceStore(paths);
                var root = Path.Combine(paths.Root, "html-registration");
                var workspace = store.Open(root, true);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "index.html"), "<!doctype html><title>Registered</title>");
                var chats = new ChatStore(paths);
                var conversations = new ChatConversationStoreAdapter(chats);
                var session = conversations.CreateTransient("Workspace", workspace.WorkspaceId, workspace.Name, "Registration");
                session.WorkspaceId = workspace.WorkspaceId;
                session.LastRun = new ChatRunRecord { RunId = "run-register", TurnId = "turn-register", Status = RunViewLifecycles.Running };
                chats.Commit(session, new ConversationCommit());
                var resources = new ResourceAuthorityStore(paths);
                var mutations = new ResourceMutationJournal(paths);
                var service = new WorkspaceHtmlVersionService(paths, conversations,
                    new WorkspaceFileService(paths, resources, resources, mutations), resources, mutations);
                var tool = new WorkspaceHtmlRegistrationTool(workspace, session.Id, service,
                    run => run == "run-register" && conversations.ReadHeader(session.Id)?.RunId == run);
                Func<string, IDictionary<string, object>, ToolHandlerContext> context = (callId, arguments) =>
                {
                    var call = new ToolCall(callId, WorkspaceHtmlRegistrationTool.ToolId, JObject.FromObject(arguments).ToString(Formatting.None));
                    var policy = new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1);
                    var executionContext = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "fixture", policy, null),
                        "run-register", "turn-register", "step-" + callId, DateTime.UtcNow, true, 8);
                    return new ToolHandlerContext(executionContext, arguments, null, () => { }, null);
                };
                var single = new Dictionary<string, object> { ["entryPath"] = "index.html", ["members"] = new JArray("index.html") };
                var prepare = tool.PrepareAsync(context("call-one", single), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, prepare.Result.Status, prepare.Result.Message);
                var dispatched = false;
                var executeContext = new ToolHandlerContext(new ToolExecutionContext(
                    new ToolCall("call-one", WorkspaceHtmlRegistrationTool.ToolId, JObject.FromObject(single).ToString(Formatting.None)),
                    new ToolPolicySnapshot(WorkspaceHtmlRegistrationTool.ToolId, "fixture", new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1), null),
                    "run-register", "turn-register", "step-call-one", DateTime.UtcNow, true, 8, prepare.PreparedStateJson),
                    single, prepare.PreparedStateJson, () => dispatched = true, null);
                var registered = tool.ExecuteAsync(executeContext, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, registered.Result.Status, registered.Result.Message);
                AssertEqual(ToolEffectEvidence.VerifiedChange, registered.Effect, "new definition reports a verified change");
                AssertTrue(dispatched, "registration marks the write boundary");
                var resultJson = JObject.Parse(registered.Result.DataJson);
                AssertEqual("registered", (string)resultJson["status"], "typed registration status");
                AssertTrue((bool)resultJson["changed"], "new registration changed metadata");
                AssertEqual("core.find", (string)resultJson["nextAction"]["tool"], "registration points to current-run discovery");
                AssertEqual("browser", (string)resultJson["nextAction"]["arguments"]["type"], "workspace discovery uses type=browser");
                AssertEqual("index.html", (string)resultJson["nextAction"]["arguments"]["query"], "entry is the discovery query");
                var definition = store.GetHtmlProjectDefinition(workspace);
                AssertEqual("index.html", definition.Definition.EntryPath, "owner stores the entry");
                AssertEqual(1, definition.Definition.Members.Length, "owner stores the explicit member list");

                var manifestPath = Path.Combine(root, ".rnassistant", "workspace.json");
                var manifestBefore = File.ReadAllText(manifestPath);
                var repeatPrepare = tool.PrepareAsync(context("call-two", single), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Ok, repeatPrepare.Result.Status, repeatPrepare.Result.Message);
                var repeatContext = new ToolHandlerContext(new ToolExecutionContext(
                    new ToolCall("call-two", WorkspaceHtmlRegistrationTool.ToolId, JObject.FromObject(single).ToString(Formatting.None)),
                    new ToolPolicySnapshot(WorkspaceHtmlRegistrationTool.ToolId, "fixture", new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1), null),
                    "run-register", "turn-register", "step-call-two", DateTime.UtcNow, true, 8, repeatPrepare.PreparedStateJson),
                    single, repeatPrepare.PreparedStateJson, () => { }, null);
                var unchanged = tool.ExecuteAsync(repeatContext, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolEffectEvidence.VerifiedNoChange, unchanged.Effect, "identical registration is a verified no-change");
                AssertTrue(!(bool)JObject.Parse(unchanged.Result.DataJson)["changed"], "identical registration is not a write");
                AssertEqual(manifestBefore, File.ReadAllText(manifestPath), "no-change leaves the manifest byte-for-byte unchanged");

                File.WriteAllText(Path.Combine(root, "race.js"), "console.log('race');");
                var racePrepare = tool.PrepareAsync(context("call-race", single), CancellationToken.None).GetAwaiter().GetResult();
                store.SetHtmlProjectDefinition(workspace, definition.Fingerprint, new HtmlProjectDefinition {
                    EntryPath = "index.html", Members = new[] { "index.html", "race.js" } });
                var raceContext = new ToolHandlerContext(new ToolExecutionContext(
                    new ToolCall("call-race", WorkspaceHtmlRegistrationTool.ToolId, JObject.FromObject(single).ToString(Formatting.None)),
                    new ToolPolicySnapshot(WorkspaceHtmlRegistrationTool.ToolId, "fixture", new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, true, false, new[] { "agent" }, 1), null),
                    "run-register", "turn-register", "step-call-race", DateTime.UtcNow, true, 8, racePrepare.PreparedStateJson),
                    single, racePrepare.PreparedStateJson, () => { }, null);
                var race = tool.ExecuteAsync(raceContext, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Error, race.Result.Status, "a changed definition invalidates prepared no-change");
                AssertEqual("project_definition_changed", (string)JObject.Parse(race.Result.DataJson)["code"],
                    "concurrent definition change is not overwritten");
                AssertEqual(ToolEffectEvidence.None, race.Effect, "stale no-change has no effect");
                var raced = store.GetHtmlProjectDefinition(workspace);
                store.SetHtmlProjectDefinition(workspace, raced.Fingerprint, definition.Definition);

                File.WriteAllText(Path.Combine(root, "app.js"), "console.log('app');");
                var changed = new Dictionary<string, object> { ["entryPath"] = "index.html", ["members"] = new JArray("index.html", "app.js") };
                var conflict = tool.PrepareAsync(context("call-three", changed), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Error, conflict.Result.Status, conflict.Result.Message);
                AssertEqual("project_definition_conflict", (string)JObject.Parse(conflict.Result.DataJson)["code"],
                    "a different definition is never overwritten");
                AssertEqual(definition.Fingerprint, store.GetHtmlProjectDefinition(workspace).Fingerprint, "conflict keeps the original definition");

                var missing = new Dictionary<string, object> { ["entryPath"] = "missing.html", ["members"] = new JArray("missing.html") };
                var missingPrepare = tool.PrepareAsync(context("call-four", missing), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Error, missingPrepare.Result.Status, missingPrepare.Result.Message);
                AssertEqual("project_member_missing", (string)JObject.Parse(missingPrepare.Result.DataJson)["code"],
                    "missing members are rejected before dispatch");

                session.LastRun.Status = RunViewLifecycles.Completed;
                chats.Commit(session, new ConversationCommit());
                var stale = tool.PrepareAsync(context("call-five", single), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolResultStatus.Error, stale.Result.Status, "a completed run cannot register");
                AssertContains(stale.Result.Message, "действующий run", "stale run rejection names the guard");
            });
        }
    }
}
