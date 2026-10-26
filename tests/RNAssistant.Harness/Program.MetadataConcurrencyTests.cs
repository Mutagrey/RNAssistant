using System;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void CommitFaultsRetainOnlyOwnAuthority()
        {
            foreach (var scalar in new[] { false, true })
            foreach (var boundary in new[] { "before-write", "after-record", "after-flush" })
                WithTempPaths(paths =>
                {
                    var store = new ChatStore(paths);
                    var port = new ChatConversationStoreAdapter(store);
                    var session = port.CreateTransient("Word", "append-fault", "Test.docx", "Original");
                    port.Commit(session, new ConversationCommit());
                    var revision = session.Revision;
                    var bytes = File.ReadAllBytes(SessionEventFile(paths, session));
                    session.Title = "Own change";
                    store.AppendFaultPoint = point => { if (point == boundary) throw new IOException("injected " + boundary); };
                    RuntimeThrows<IOException>(() => {
                        if (scalar) port.SetTitle(session, session.Title);
                        else port.Commit(session, new ConversationCommit());
                    });
                    store.AppendFaultPoint = null;
                    var replay = port.Load(session.Id);
                    if (boundary == "before-write")
                    {
                        AssertEqual(revision, session.Revision, "refused append keeps old cursor");
                        AssertTrue(bytes.SequenceEqual(File.ReadAllBytes(SessionEventFile(paths, session))), "no bytes before dispatch");
                        AssertEqual("Original", replay.Title, "refused title never reaches replay");
                    }
                    else
                    {
                        AssertTrue(session.Revision > revision, "verified own complete append advances cursor");
                        AssertEqual(replay.Revision, session.Revision, "cursor matches canonical own append");
                        AssertEqual("Own change", replay.Title, "durable fact survives projection failure");
                    }
                });
        }

        private static void PartialCommitCannotAdvanceWriter()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var port = new ChatConversationStoreAdapter(store);
                var session = port.CreateTransient("Word", "partial-append", "Test.docx", "Before");
                port.Commit(session, new ConversationCommit());
                var revision = session.Revision;
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", Status = "running" };
                store.AppendFaultPoint = point => { if (point == "after-record") throw new IOException("partial batch"); };
                RuntimeThrows<IOException>(() => port.Commit(session, new ConversationCommit()));
                AssertEqual(revision, session.Revision, "a prefix of own batch is not a complete append");
                store.AppendFaultPoint = null;
                RuntimeThrows<ChatConcurrencyException>(() => port.Commit(session, new ConversationCommit()));
                var replay = port.Load(session.Id);
                AssertTrue(replay.Revision > revision, "reload sees the retained valid prefix without replaying effects");
            });
        }

        private static void MetadataConflictPreservesStaleRevision()
        {
            foreach (var field in new[] { "title", "model", "reasoning" })
                WithTempPaths(paths =>
                {
                    var writer = new ChatConversationStoreAdapter(new ChatStore(paths));
                    var reader = new ChatConversationStoreAdapter(new ChatStore(paths));
                    var current = writer.CreateTransient("Word", "metadata-conflict", "Review.docx", "Original");
                    writer.Commit(current, new ConversationCommit());
                    var stale = reader.Load(current.Id);
                    var staleRevision = stale.Revision;
                    var staleUpdated = stale.UpdatedUtc;
                    current.Model = "current-model";
                    var artifact = new ChatArtifact { Kind = ChatArtifactKinds.ToolResult,
                        Title = "Retained result", MimeType = "text/plain", InlineText = "Keep this result" };
                    current.Artifacts.Add(artifact);
                    writer.Commit(current, new ConversationCommit());
                    var path = SessionEventFile(paths, current);
                    var before = File.ReadAllText(path);

                    var rejected = false;
                    try
                    {
                        if (field == "title") reader.SetTitle(stale, "Rejected title");
                        else if (field == "model") reader.SetModel(stale, "rejected-model");
                        else reader.SetReasoningEnabled(stale, !stale.ReasoningEnabled);
                    }
                    catch (ChatConcurrencyException) { rejected = true; }
                    AssertTrue(rejected, field + " rejects stale metadata");
                    AssertEqual(staleRevision, stale.Revision, field + " preserves the stale write cursor");
                    AssertEqual(staleUpdated, stale.UpdatedUtc, field + " restores the caller timestamp");
                    AssertEqual(before, File.ReadAllText(path), field + " rejection appends nothing");

                    rejected = false;
                    try { reader.Commit(stale, new ConversationCommit()); }
                    catch (ChatConcurrencyException) { rejected = true; }
                    AssertTrue(rejected, field + " cannot authorize a later stale aggregate commit");
                    AssertEqual(before, File.ReadAllText(path), field + " stale reuse preserves the journal");

                    var reloaded = new ChatConversationStoreAdapter(new ChatStore(paths)).Load(current.Id);
                    AssertEqual("current-model", reloaded.Model, field + " preserves the winning model");
                    AssertEqual(artifact.Id, reloaded.Artifacts[0].Id, field + " preserves the winning artifact");
                    reloaded.Title = "After reload";
                    reader.SetTitle(reloaded, reloaded.Title);
                    var final = writer.Load(current.Id);
                    AssertEqual("After reload", final.Title, field + " permits an explicit reload then write");
                    AssertEqual(artifact.Id, final.Artifacts[0].Id, field + " reload retains the artifact");
                });
        }
    }
}
