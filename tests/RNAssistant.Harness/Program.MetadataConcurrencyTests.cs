using System;
using System.IO;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
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
