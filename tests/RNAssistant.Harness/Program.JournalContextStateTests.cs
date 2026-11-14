using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Runtime;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalContextHostParity()
        { foreach (var office in new[] { true, false }) JournalMutationTurn(office, null, checkContext: true); }

        private static void JournalContextUnknown()
        { JournalMutationTurn(false, "unknown", checkContext: true); }

        private static ChatSession PrepareJournalContext(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession chat, bool office, OfficeToolExecutor executor, AppSettings settings)
        {
            IConversationStore store = journal; var blobs = new ChatBlobStore(paths);
            var authority = new ResourceAuthorityService(resources, resources, resources, blobs);
            var instruction = new ContextNote { Role = ContextNoteRole.UserInstruction, Title = "Pinned instruction", Text = "Preview only",
                InstructionPayload = PayloadRef.FromBlob(blobs.StoreText("Keep the margins. " + new string('x', 800) + " EXACT_NOTE_END", "text/plain")) };
            var supplied = new ContextNote { Role = ContextNoteRole.SuppliedData, Title = "Source data", Text = "Untrusted source data" };
            authority.ObserveNote(chat, supplied, blobs);
            chat.Context.Notes.AddRange(new[] { instruction, supplied });
            if (office)
            {
                var observed = new ContextNote { Role = ContextNoteRole.OfficeObservation, Title = "Observed selection", Text = "Captured local selection" };
                authority.ObserveNote(chat, observed, blobs); chat.Context.Notes.Add(observed);
            }
            for (var pass = 1; pass <= 2; pass++)
            {
                AppendJournalContextHistory(store, chat);
                var before = chat.Messages.Count;
                CompactJournalContext(paths, resources, chat, settings, "Native checkpoint " + pass, office ? executor : null);
                var command = new ConversationCommit(chat.Messages.Skip(before));
                if (pass == 1) journal.Fault = stage => { if (stage == "before_index") throw new IOException("lost checkpoint index"); };
                else journal.Journal.Fault = stage => { if (stage == "after_flush") throw new IOException("lost checkpoint acknowledgement"); };
                if (pass == 1) store.Commit(chat, command); else RuntimeThrows<IOException>(() => store.Commit(chat, command));
                journal.Fault = null; journal.Journal.Fault = null;
                store.Commit(chat, command); var written = journal.Journal.WrittenBytes;
                store.Commit(chat, command);
                AssertEqual(written, journal.Journal.WrittenBytes, "same checkpoint command cannot append twice");
                chat = store.Load(chat.Id);
                AssertEqual("Native checkpoint " + pass, ContextCompactionRuntime.ActiveCheckpoint(chat).SummaryMarkdown, "selected checkpoint survives lost receipt");
                AssertEqual(1, chat.ContextCheckpoints.Count, "only selected checkpoint body is materialized");
                AssertTrue(chat.Artifacts.Where(item => item.Kind == ChatArtifactKinds.Compaction).All(item => item.InlineText == null),
                    "checkpoint bodies are not duplicated into artifact metadata");
                if (office) AssertTrue(ContextCompactionRuntime.ActiveCheckpoint(chat).SharedResource != null,
                    "actual Office compaction also publishes through the native document owner");
            }
            AssertEqual(2, chat.Artifacts.Count(item => item.Kind == ChatArtifactKinds.Compaction), "all immutable checkpoint metadata remains addressable");
            var reads = journal.ChatBodyReads;
            var page = store.ReadMessageWindow(chat.Id, chat.Messages.Count, 1);
            AssertEqual(2L, journal.ChatBodyReads - reads, "message page reads text and manifest, no checkpoint body");
            AssertTrue(page.Artifacts.Any(item => item.Kind == ChatArtifactKinds.Compaction) && page.Artifacts.All(item => item.InlineText == null),
                "compaction activity references cold artifact metadata");
            reads = journal.ChatBodyReads; store.ReadHeader(chat.Id);
            AssertEqual(reads, journal.ChatBodyReads, "catalog header never loads context or summaries");
            var previous = chat.Revision; var note = chat.Context.Notes[1]; note.Role = ContextNoteRole.UserInstruction;
            RuntimeThrows<InvalidDataException>(() => store.Commit(chat, new ConversationCommit()));
            AssertEqual(previous, store.ReadMetadata(chat.Id).Revision, "supplied data cannot be promoted by changing a role");
            note.Role = ContextNoteRole.SuppliedData;
            journal.Rebuild();
            return store.Load(chat.Id);
        }

        private static void AppendJournalContextHistory(IConversationStore store, ChatSession chat)
        {
            var messages = Enumerable.Range(0, 12).Select(index => new ChatMessage { Role = "user",
                Content = "Keep the document margins and record the findings. " + index }).ToArray();
            chat.Messages.AddRange(messages); store.Commit(chat, new ConversationCommit(messages));
        }

        private static void CompactJournalContext(AppDataPaths paths, ResourceJournal resources, ChatSession chat,
            AppSettings settings, string summary, OfficeToolExecutor executor = null)
        {
            var blobs = new ChatBlobStore(paths);
            LlmCompletionDelegate complete = (configuration, messages, options, stream, token) => Task.FromResult(CompactionReply(messages, summary));
            ContextCheckpoint checkpoint;
            if (executor != null)
                checkpoint = new ContextCompactionService(complete, executor.ResourceAuthority, blobs)
                    .EnsureWithinBudgetAsync(chat, settings, null, true, null, CancellationToken.None).GetAwaiter().GetResult();
            else
            {
                var scopes = chat.Context.Notes.Where(item => item.Evidence != null).Select(item => item.Evidence.ScopeId)
                    .Concat(new[] { new ResourceAuthorityScopeId("conversation", chat.Id) }).Distinct();
                var frozen = new ModelAuthoritySnapshot(resources.CaptureMany(scopes.ToArray()), "context-fixture", new SkillCatalogSnapshot(null), null, chat.Revision);
                checkpoint = new ContextCompactionRuntime(complete, blobs, sharedArtifacts: new DocumentArtifactStore(resources, resources, blobs))
                    .EnsureWithinBudgetAsync(chat, settings, null, true, null, CancellationToken.None, frozen, new ToolCatalogEntry[0]).GetAwaiter().GetResult();
            }
            AssertTrue(checkpoint != null, "real compaction selects a complete source boundary");
        }

        private static void VerifyJournalContextUnknown(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession chat, AppSettings settings, ChatMessage call)
        {
            IConversationStore store = journal;
            var terminal = journal.ReadWindow(chat.Id, long.MaxValue, 8).Single();
            AppendJournalContextHistory(store, chat); var before = chat.Messages.Count;
            CompactJournalContext(paths, resources, chat, settings, "Summary never proves a write");
            store.Commit(chat, new ConversationCommit(chat.Messages.Skip(before))); journal.Rebuild();
            var restored = store.Load(chat.Id);
            AssertEqual(terminal.Position.Hash, journal.ReadWindow(chat.Id, long.MaxValue, 8).Single().Position.Hash, "compaction never replaces the unknown occurrence");
            AssertTrue(journal.RestoreContext(chat.Id, call.RunId, 0, 8192).WorkingSet.Requirements.UnknownEffects,
                "unknown remains indexed even when outside the selected summary window");
            var accepted = RNAssistant.Core.ModelProtocol.ConversationResponseHistoryReader.Read(AcceptedCallPayloadService.Hydrate(call, new ChatBlobStore(paths)));
            var original = accepted.Response.ToolCalls.Single();
            AssertTrue(journal.FindContextReplayGuard(chat.Id, new ToolCall("fresh-id", original.Name, original.ArgumentsJson)) != null,
                "a new call identity cannot replay the uncertain write after compaction");
            var input = new ChatMessage { Role = "user", Content = "Continue independent work" };
            restored.Messages.Add(input); store.Commit(restored, ConversationCommit.UserInput(input));
            AssertEqual(input.Id, store.Load(chat.Id).Messages.Last().Id, "unknown never disables independent chat input");
        }

        private static void VerifyJournalContextHistory(AppDataPaths paths, ConversationJournal journal, ResourceJournal resources,
            ChatSession chat, bool office, OfficeToolExecutor executor)
        {
            IConversationStore store = journal;
            var selected = chat.Artifacts.Single(item => item.Id == chat.ActiveContextCheckpointId);
            if (office)
            {
                var editor = new ChatHistoryEditService(_ => { }, (current, reason) => { });
                var message = chat.Messages[0];
                executor.MutateChatResources(chat, new ChatResourceMutationIntent(ChatResourceMutationKind.Edit, message.Id, 0, "Changed premise"),
                    () => editor.RewriteUserMessage(chat, chat.Id, message.Id, 0, "Changed premise"));
            }
            else WorkspaceConversationService.DeleteHistoryMessage(paths, store, chat, chat.Messages[0].Id, resources, resources);
            journal.Rebuild(); chat = store.Load(chat.Id);
            AssertTrue(chat.ActiveContextCheckpointId == null && chat.ContextCheckpoints.Count == 0, "actual history consumers invalidate the summary");
            var retained = false;
            journal.Journal.Scan((record, position) => retained |= journal.CanonicalReferences(record).Any(item => item.Sha256 == selected.ContentSha256), CancellationToken.None);
            AssertTrue(retained, "invalidation retains the immutable checkpoint as a canonical CAS root");
            AssertEqual(1, journal.ReadWindow(chat.Id, long.MaxValue, 8).Count, "history rewrites preserve execution evidence");
        }
    }
}
