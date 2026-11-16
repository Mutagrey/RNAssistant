using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static Dictionary<string, byte[]> LegacyRecoveryBytes(params string[] roots)
        { return roots.SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)).Distinct().ToDictionary(path => path, File.ReadAllBytes); }
        private static void RequireLegacyRecoveryBytes(Dictionary<string, byte[]> before)
        { AssertTrue(before.All(pair => File.Exists(pair.Key) && pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))), "every original journal, projection, source and CAS file is unchanged"); }

        private static void LegacyRecoveryChatPages()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "archive-key", Enumerable.Repeat((byte)29, 16).ToArray()) : StorageProtector.None;
                var legacy = new ChatStore(paths, () => protector);
                IConversationStore old = new ChatConversationStoreAdapter(legacy);
                var chat = old.CreateTransient("Excel", "old-document", "Old document", "Old conversation");
                old.Commit(chat, new ConversationCommit());
                var message = new ChatMessage { Role = "user", Content = string.Concat(Enumerable.Repeat("abc\U0001f642", 1700)) };
                chat.Messages.Add(message); old.Commit(chat, ConversationCommit.UserInput(message));
                var second = old.CreateTransient("Word", "another-document", "Another document", "Another conversation");
                old.Commit(second, new ConversationCommit());
                var before = LegacyRecoveryBytes(paths.ChatDirectory, paths.ResourceAuthorityDirectory, paths.ChatBlobDirectory);
                var reader = new ChatStore(paths, () => protector);
                var first = reader.ReadLegacyRecoveryChats(limit: 1);
                AssertEqual(2, first.Total, "archive reports retained legacy chats");
                AssertEqual(1, first.Items.Count, "archive page is bounded");
                AssertEqual(1, first.NextOffset.Value, "archive exposes the next page");
                var next = reader.ReadLegacyRecoveryChats(first.NextOffset.Value, 1);
                AssertTrue(next.NextOffset == null, "last catalog page terminates");
                var item = first.Items.Concat(next.Items).Single(value => value.ChatId == chat.Id);
                AssertEqual("readable", item.Status, "configured protector reads old metadata");
                var all = reader.ReadLegacyRecoveryTranscript(item.SourceId, item.Revision, maxCharacters: 65536);
                var collected = new StringBuilder(); var index = 0; var offset = 0; var pages = 0;
                while (true)
                {
                    var part = reader.ReadLegacyRecoveryTranscript(item.SourceId, item.Revision, index, offset, 1024);
                    AssertTrue(part.Text.Length <= 1024 && part.Text.Length > 0, "each transcript page is bounded and makes progress");
                    AssertTrue(!char.IsHighSurrogate(part.Text.Last()) && !char.IsLowSurrogate(part.Text.First()), "pages do not split surrogate pairs");
                    collected.Append(part.Text); pages++;
                    if (!part.NextMessageIndex.HasValue) break;
                    index = part.NextMessageIndex.Value; offset = part.NextCharacterOffset;
                    AssertTrue(pages < 30, "transcript cursor cannot stall");
                }
                AssertEqual(all.Text, collected.ToString(), "character pages retain the whole transcript without gaps or duplicates");
                AssertTrue(collected.ToString().Contains(message.Content), "saved user text remains available");
                RuntimeThrows<ChatConcurrencyException>(() => reader.ReadLegacyRecoveryTranscript(item.SourceId, item.Revision + 1));
                RuntimeThrows<ArgumentException>(() => reader.ReadLegacyRecoveryTranscript("../outside", item.Revision));
                RuntimeThrows<OperationCanceledException>(() => reader.ReadLegacyRecoveryChats(token: new CancellationToken(true)));
                if (encrypted) AssertTrue(new ChatStore(paths, () => StorageProtector.None).ReadLegacyRecoveryChats().Items.All(value => value.Status == "unreadable"), "wrong protection never becomes an empty successful catalog");
                RequireLegacyRecoveryBytes(before);
                var native = new ConversationJournalRouter(paths, (host, key) => "document:new-generation", protector);
                AssertEqual(0, native.ListHeaders(false).Count, "archive reads do not import or create native chats");
                var path = Path.Combine(paths.ChatDirectory, item.SourceId.Replace('/', Path.DirectorySeparatorChar));
                File.AppendAllText(path, "unfinished");
                var torn = File.ReadAllBytes(path);
                AssertEqual("incomplete", reader.ReadLegacyRecoveryChats().Items.Single(value => value.SourceId == item.SourceId).Status,
                    "torn archive tail is explicitly unavailable");
                AssertTrue(torn.SequenceEqual(File.ReadAllBytes(path)), "archive never repairs or truncates a torn tail");
            });
        }

        private static void LegacyRecoveryPluginSources()
        {
            foreach (var encrypted in new[] { false, true }) WithTempPaths(paths => {
                var protector = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "plugin-archive-key", Enumerable.Repeat((byte)31, 16).ToArray()) : StorageProtector.None;
                var old = new ChatConversationStoreAdapter(new ChatStore(paths, () => protector));
                var authority = new ResourceAuthorityStore(paths); var mutations = new ResourceMutationJournal(paths);
                var files = new WorkspaceFileService(paths, authority, authority, mutations);
                var store = new PluginStore(paths, old, authority, mutations, files, new ChatBlobStore(paths, () => protector));
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "plugin-source"));
                var manifest = new PluginManifestV1 { FormatVersion = 1, HostProtocol = 1, Id = "legacy-source", Name = "Legacy plugin", Version = "1.0.0",
                    Entry = "index.html", Files = new[] { "plugin.json", "index.html" } };
                File.WriteAllText(Path.Combine(workspace.RootPath, "plugin.json"), JsonConvert.SerializeObject(manifest));
                File.WriteAllText(Path.Combine(workspace.RootPath, "index.html"), "<!doctype html><html><body>Saved source</body></html>");
                var registration = store.Connect(new PluginSourceWorkspace(workspace));
                var before = LegacyRecoveryBytes(paths.ChatDirectory, paths.ResourceAuthorityDirectory, paths.ChatBlobDirectory, workspace.RootPath);
                var recovery = new LegacyPluginRecoveryService(paths, () => protector);
                var item = recovery.List().Items.Single();
                AssertEqual(registration.PluginId, item.PluginId, "old registration is inventoried explicitly");
                AssertTrue(item.CanConnect && item.Status == "readable", "valid old source is reconnectable");
                var resolved = recovery.ResolveSource(item.PluginId);
                AssertEqual(workspace.RootPath, resolved.SourceRoot, "reconnect resolves a known old root");
                AssertEqual(workspace.WorkspaceId, resolved.SourceWorkspaceId, "reconnect retains exact workspace identity");
                RuntimeThrows<ArgumentException>(() => recovery.ResolveSource("../arbitrary-root"));
                RuntimeThrows<InvalidOperationException>(() => recovery.ResolveSource("missing-plugin"));
                if (encrypted) AssertEqual("unreadable", new LegacyPluginRecoveryService(paths, () => StorageProtector.None).List().Items.Single().Status,
                    "wrong CAS key is reported without inventing another registration");
                RequireLegacyRecoveryBytes(before);
                manifest.Id = "changed-plugin";
                File.WriteAllText(Path.Combine(workspace.RootPath, "plugin.json"), JsonConvert.SerializeObject(manifest));
                AssertEqual("sourceChanged", recovery.List().Items.Single().Status, "source identity drift cannot connect another plugin");
                RuntimeThrows<InvalidOperationException>(() => recovery.ResolveSource(item.PluginId));
                var directory = workspace.RootPath; var moved = directory + "-moved"; Directory.Move(directory, moved);
                AssertEqual("missingSource", recovery.List().Items.Single().Status, "missing source is distinct from an empty catalog");
                try
                {
                    Directory.CreateSymbolicLink(directory, moved);
                    AssertEqual("linkedPath", recovery.List().Items.Single().Status, "source links are refused without reading their contents");
                }
                catch (UnauthorizedAccessException) { }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory); Directory.Move(moved, directory); }
                var journal = Path.Combine(paths.ResourceAuthorityDirectory, RNAssistant.Core.Tools.TextPatternEngine.Sha256(
                    new ResourceAuthorityScopeId("plugins", "local").ToString()) + ".authority.jsonl");
                File.AppendAllText(journal, "unfinished"); var torn = File.ReadAllBytes(journal);
                RuntimeThrows<IOException>(() => recovery.List());
                AssertTrue(torn.SequenceEqual(File.ReadAllBytes(journal)), "old resource owner never truncates an incomplete catalog");
            });
        }
    }
}
