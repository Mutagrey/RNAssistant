using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalCatalogConsumers()
        {
            foreach (var office in new[] { true, false }) WithJournalInput(office, (paths, journal, source, scope) => {
                IConversationStore store = journal; var resources = new ResourceJournal(paths); var blobs = new ChatBlobStore(paths);
                var target = store.CreateTransient(source.Host, source.DocumentKey, source.DocumentTitle, "Unopened target");
                target.Messages.AddRange(Enumerable.Range(0, 90).Select(i => new ChatMessage { Role = "user", Content = "Topic " + i }));
                var large = new string('x', 100000);
                target.Messages.Add(new ChatMessage { Role = "user", Content = large });
                target.Messages.Add(new ChatMessage { Role = "assistant", Content = "PRIVATE EXCLUDED", ExcludeFromModelContext = true });
                target.Messages.Add(new ChatMessage { Role = "assistant", Content = "PRIVATE CONTEXT", ExcludeFromModelContext = true });
                target.Messages.Add(new ChatMessage { Role = "assistant", Content = "Visible conclusion", ReasoningContent = "PRIVATE REASONING" });
                store.Commit(target, new ConversationCommit(target.Messages));
                if (office)
                    new DocumentArtifactStore(resources, resources, blobs).PublishContext(target, new ContextCheckpoint { Claims = new List<StructuredContextClaim> {
                        new StructuredContextClaim { ClaimId = "topic", Kind = "constraint", Text = "Published topic",
                            SourceRoles = new List<string> { "user" }, SourceMessageIds = new List<string> { target.Messages[0].Id } } } }, 0);
                var reads = journal.ChatBodyReads; var metadata = store.ReadOverview(source, target.Id, true);
                AssertEqual(reads, journal.ChatBodyReads, "metadata overview hydrates no message/context body");
                AssertEqual("not_requested", metadata.SummaryStatus, "metadata does not read a published summary");
                var overview = store.ReadOverview(source, target.Id);
                AssertEqual(36, overview.SampledMessages, "native overview addresses only first four and last 32 rows");
                AssertEqual(1, overview.SkippedOversizedMessages, "oversized body is skipped before hydration");
                AssertEqual(33L, journal.ChatBodyReads - reads, "excluded/oversized and reasoning bodies stay cold");
                AssertTrue(overview.Excerpts.Count == 6 && !JsonConvert.SerializeObject(overview).Contains("PRIVATE"), "same bounded privacy projection");
                if (office) AssertEqual("Published topic", overview.SummaryClaims.Single(), "native resource owner supplies only a published summary");
                var foreign = new ChatSession { Host = source.Host, DocumentKey = source.DocumentKey, WorkspaceId = "foreign", DocumentAuthorityId = source.DocumentAuthorityId };
                AssertEqual("chat_unavailable", RuntimeThrows<ResourceRequestException>(() => store.ReadOverview(foreign, target.Id)).ErrorCode,
                    "known chat identity never grants cross-scope reads");
                var loaded = 0; var started = 0; var dispatches = 0;
                var agents = new AgentCommunicationService();
                agents.Register(OverviewScope(store, new ConversationInboxService(journal), source, paths, () => loaded++, () => started++));
                var access = new ConversationCatalogAccess(agents, source, 8); var id = AgentChatAddress.IdFor(target.Id);
                AssertEqual(2, access.Find(null).Items.Count, "existing catalog includes unopened native chats");
                access.Read(OverviewContext(target.Id, !office), 10000, CancellationToken.None);
                AssertTrue(loaded == 0 && started == 0, "catalog/read never load execution state or start work");
                store.SetTitle(target, "Concurrent title");
                AssertEqual("chat_rename_stale", RuntimeThrows<ResourceRequestException>(() => access.Rename(id, "Stale title", () => dispatches++)).ErrorCode,
                    "rename requires the exact observed revision");
                access.Read(OverviewContext(target.Id, !office, "metadata"), 10000, CancellationToken.None);
                access.Rename(id, "Addressed native title", () => dispatches++);
                AssertEqual(1, dispatches, "only a fresh observed mutation dispatches");
                target = store.Load(target.Id);
                AssertTrue(target.TitleAssignedByAgent && target.Title == "Addressed native title", "native metadata preserves agent title attribution");
                var child = ForkSharedJournal(paths, journal, resources, target, target.Messages.Count);
                store.Delete(target.Host, target.DocumentKey, target.Id); journal.Rebuild();
                var inherited = store.ReadOverview(source, child.Id);
                AssertEqual(JsonConvert.SerializeObject(overview.Excerpts), JsonConvert.SerializeObject(inherited.Excerpts),
                    "overview follows immutable inherited locators after ancestor deletion/rebuild");
                File.Delete(blobs.PathFor(TextPatternEngine.Sha256(large)));
                AssertEqual(1, store.ReadOverview(source, child.Id).SkippedOversizedMessages,
                    "a cold oversized body is not opened even when unavailable");
            });
        }
    }
}
