using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ForkCheckpointPreservesPublication()
        {
            var boundary = new ChatMessage { Role = "user", Content = "Retained boundary" };
            var checkpoint = new ContextCheckpoint { ThroughMessageId = boundary.Id,
                SharedResource = new ResourceRef("rna://state/document/source/context", "r1"),
                SharedPublicationIssue = "Previous shared publication remains current" };
            var clone = ChatCloneService.CloneContextCheckpoints(new[] { checkpoint }, new[] { boundary }).Single();
            AssertEqual(checkpoint.SharedResource.Uri, clone.SharedResource.Uri, "fork retains exact shared provenance");
            AssertEqual(checkpoint.SharedResource.Revision, clone.SharedResource.Revision, "fork retains shared revision");
            AssertEqual(checkpoint.SharedPublicationIssue, clone.SharedPublicationIssue, "fork retains publication failure");
            checkpoint.SharedPublicationIssue = null;
            AssertTrue(clone.SharedPublicationIssue != null, "fork cannot promote a local checkpoint to shared publication");
            AssertEqual(0, ChatCloneService.CloneContextCheckpoints(new[] { checkpoint }, new ChatMessage[0]).Count,
                "checkpoint outside the retained prefix is excluded");
        }

        private static void VerifyJournalForkPreparation(AppDataPaths paths, ConversationJournal journal,
            ResourceJournal resources, ChatSession source, string selectedId)
        {
            IConversationStore store = journal; var blobs = new ChatBlobStore(paths);
            var selected = source.Artifacts.Single(item => item.Id == selectedId);
            var older = source.Artifacts.First(item => item.Kind == selected.Kind && item.Id != selectedId);
            var oldPath = blobs.PathFor(older.ContentSha256); var oldBytes = File.ReadAllBytes(oldPath);
            var selectedPath = blobs.PathFor(selected.ContentSha256); var selectedBytes = File.ReadAllBytes(selectedPath);
            var reads = new List<string>();
            Func<ChatSession> prepare = () => {
                var fork = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Fork preparation");
                fork.ParentSessionId = source.Id; fork.ParentSessionRevision = source.Revision;
                fork.ForkedThroughMessageId = source.Messages.Last().Id;
                fork.Context = ChatCloneService.CloneContext(source.Context);
                fork.Messages = ChatCloneService.CloneMessages(source.Messages);
                var authority = new ResourceAuthorityService(resources, resources, resources, blobs);
                ChatCloneService.PrepareForkResources(source, fork, (chat, id) => {
                    reads.Add(id); return store.LoadArtifactBody(chat, id);
                }, new ResourceForkService(authority, blobs));
                return fork;
            };
            try
            {
                foreach (var artifact in source.Artifacts.Where(item => item.Kind == selected.Kind)) artifact.InlineText = null;
                File.WriteAllText(oldPath, "unavailable historical body");
                var before = journal.Journal.WrittenBytes; var resourceBytes = resources.Journal.WrittenBytes;
                var bodyReads = journal.ChatBodyReads;
                var child = prepare();
                AssertEqual(selectedId, reads.Single(), "fork loads only the selected resource, not every ancestor");
                AssertEqual(selected.Kind == ChatArtifactKinds.TaskList ? 1L : 0L, journal.ChatBodyReads - bodyReads,
                    "fork hydration does not reload chat text or context; document bodies keep their resource owner");
                AssertEqual(selectedId, selected.Kind == ChatArtifactKinds.TaskList ? child.ActiveTaskListArtifactId : child.ActiveHtmlArtifactId,
                    "fork preserves the exact selected resource");
                AssertTrue(child.Artifacts.Single(item => item.Id == older.Id).InlineText == null, "unrelated historical body stays cold");
                AssertEqual(before, journal.Journal.WrittenBytes, "resource preparation does not publish a chat");
                AssertEqual(resourceBytes, resources.Journal.WrittenBytes, "shared document selection does not publish copied heads");
                AssertEqual(0, child.ResourceCopies.Count, "shared artifacts and local Task metadata do not manufacture definition copies");

                selected.InlineText = null; File.WriteAllText(selectedPath, "corrupt selected body"); reads.Clear();
                RuntimeThrows<InvalidOperationException>(() => prepare());
                AssertTrue(selected.InlineText == null, "missing selected source cannot fall back to another revision");
                AssertEqual(before, journal.Journal.WrittenBytes, "failed preparation publishes nothing");
            }
            finally
            {
                File.WriteAllBytes(oldPath, oldBytes); File.WriteAllBytes(selectedPath, selectedBytes);
                AssertTrue(store.LoadArtifactBody(source, selectedId), "fixture restores the exact selected body");
            }
            var revision = source.Revision;
            try
            {
                source.Revision--;
                RuntimeThrows<ChatConcurrencyException>(() => store.LoadArtifactBody(source, selectedId));
            }
            finally { source.Revision = revision; }
        }
    }
}
