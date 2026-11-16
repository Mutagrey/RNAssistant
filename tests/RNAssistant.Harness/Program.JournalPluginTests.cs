using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalPluginVersionConsumers()
        {
            WithPluginPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "source"), true, true);
                var scope = "workspace:" + workspace.WorkspaceId;
                var journal = new ConversationJournal(paths, scope); IConversationStore chats = journal;
                var chat = new WorkspaceStore(paths).CreateSession(chats, workspace, "Source run");
                var initial = CheckpointState("plugin-source-run", RunLifecycle.Running, 0, 0);
                chat.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(chat, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(chats.CommitRun, chat);
                var manifest = new PluginManifestV1 { FormatVersion = 1, HostProtocol = 1, Id = "plugin-native",
                    Name = "Native plugin", Version = "1.0.0", Entry = "index.html", Files = new[] { "plugin.json", "index.html" } };
                File.WriteAllText(Path.Combine(workspace.RootPath, "plugin.json"), JsonConvert.SerializeObject(manifest));
                File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<main>Native saved version</main>");
                var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths); var files = new WorkspaceFileService(paths, resources, resources, resources);
                RuntimeThrows<ArgumentException>(() => new PluginStore(paths, chats));
                RuntimeThrows<ArgumentException>(() => new RNAssistant.Application.WorkspaceHtmlVersionService(paths, chats));
                var plugins = new PluginStore(paths, chats, resources, resources, files, blobs);
                var source = new PluginSourceWorkspace(workspace); var vendors = new VendorCatalog(paths, null);
                plugins.Connect(source); var package = plugins.Capture(source, vendors);
                var state = plugins.VersionState(manifest.Id); var before = journal.ChatBodyReads; var dispatched = 0;
                Func<string, SavedVersionActor, SavedVersionOperation> save = (operation, intentActor) => plugins.SaveVersion(manifest.Id, source,
                    vendors, state.StateRevision, package.PackageSha256, operation, intentActor, beforeDispatch: () => dispatched++);
                SavedFailure(() => save("wrong-run", new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = chat.Id, RunId = "expired" }), "version_actor_invalid");
                SavedFailure(() => save("missing-chat", new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = "missing", RunId = chat.LastRun.RunId }), "version_actor_invalid");
                AssertEqual(0, dispatched, "foreign/missing run ownership fails before publication");
                var actor = new SavedVersionActor { Kind = SavedVersionActorKind.Run, ChatId = chat.Id, RunId = chat.LastRun.RunId };
                var saved = save("save-native", actor);
                AssertEqual("Saved", saved.Outcome, "actual plugin version owner accepts a native source run");
                AssertEqual(1, dispatched, "only the verified run dispatches its Save");
                AssertEqual(before, journal.ChatBodyReads, "version admission reads headers without transcript/context bodies");
                AssertEqual(saved.VersionId, save("save-native", actor).VersionId, "same intent returns its receipt without a second save");
                AssertEqual(1, dispatched, "receipt lookup does not dispatch again");
                journal.Rebuild();
                var reopenedResources = new ResourceJournal(paths);
                var reopened = new PluginStore(paths, chats, reopenedResources, reopenedResources, files, blobs);
                AssertEqual(chat.Id, reopened.SavedVersion(manifest.Id, saved.VersionId).Actor.ChatId, "native resource replay preserves exact actor provenance");
                AssertTrue(!Directory.EnumerateFiles(paths.ResourceAuthorityDirectory, "*.jsonl").Any(), "native plugin consumers create no legacy resource stream");
                var backup = Path.Combine(paths.Root, "plugin-backup"); JournalCasService(paths, StorageProtector.None).BackupJournals(backup);
                var restored = CasMaintenanceService.RestoreJournals(backup, Path.Combine(paths.Root, "plugin-restore"), _ => StorageProtector.None);
                var restoredResources = new ResourceJournal(restored);
                var restoredPlugins = new PluginStore(restored, new ConversationJournal(restored, scope), restoredResources, restoredResources,
                    new WorkspaceFileService(restored, restoredResources, restoredResources, restoredResources), new ChatBlobStore(restored));
                var retained = restoredPlugins.ReadSavedRelease(manifest.Id, saved.VersionId);
                AssertEqual("<main>Native saved version</main>", Encoding.UTF8.GetString(restoredPlugins.ReadFile(retained, "index.html")),
                    "saved version and CAS members survive isolated journal restore without source capture");
            });
        }
    }
}
