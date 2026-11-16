using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void JournalForkFinishRun(ConversationJournal journal, OfficeToolExecutor executor,
            FakeOfficeAdapter adapter, ChatSession session, bool read, string reference)
        {
            var requests = 0;
            var settings = new AppSettings { Model = "scripted-fork-finish", StreamResponses = false,
                SmartChatTitles = false, AutoCompressContext = false, ContextWindowOverrideTokens = 65536,
                MaxAgentFormatRetries = 0 };
            LlmCompletionDelegate complete = (configuration, messages, options, stream, token) => {
                requests++;
                if (read && requests == 1)
                    return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAcceptedCall(
                        "Inspect the source.", null, new ConversationToolCall { Name = "core.read",
                            Arguments = new JObject { ["target"] = "Excel range: Data!A1:B2", ["representation"] = "text" } }) });
                if (reference != null)
                    AssertTrue(ModelReferenceProjection.Delivered(messages.ToArray()).Contains(reference),
                        "the real request delivers the cited historical evidence");
                return Task.FromResult(new LlmCompletionResult { Content = ModelProtocolWire.WriteAcceptedDecision(
                    "finish", "Use the retained observation.", "Observed data.", outcome: ConversationOutcome.Completed,
                    results: reference == null ? new ConversationResultClaim[0] : new[] {
                        new ConversationResultClaim("Inspect requested data.", new[] { reference }, ConversationAssessment.Satisfied) }) });
            };
            var service = new ConversationRunService(adapter, executor, journal, journal, complete);
            service.ExecuteAsync(ChatModes.Agent, read ? "Read Data!A1:B2." : "Use the prior observation.", session,
                session.Context, settings, executor.GetControllerTools().ToArray(), null).GetAwaiter().GetResult();
            AssertEqual(RunLifecycle.Completed, session.LastRun.KernelState.Summary.Lifecycle,
                "native journal admits the finish through the real kernel");
            AssertEqual(read ? 2 : 1, requests, "valid evidence needs no semantic correction or redispatch");
        }

        private static FinishAdmission JournalForkFinishAdmission(ConversationJournal journal, ChatSession session,
            string reference, params string[] delivered)
        {
            return ConversationFinishEvidenceAdmission.Evaluate(journal, session, session.LastRun.RunId,
                session.LastRun.TurnId, new string[0], new[] {
                    new ConversationResultClaim("Use the historical observation.", new[] { reference }, ConversationAssessment.Satisfied) },
                new ChatBlobStore(FixturePaths.Value), delivered);
        }

        private static void JournalForkFinishAncestry()
        {
            WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value;
                var scope = "document:" + DocumentAuthorityId.Create().Id;
                var journal = new ConversationJournal(paths, scope); IConversationStore store = journal;
                var source = new ChatSessionService(adapter, store).CreatePersistentChat("Fork evidence");
                JournalForkFinishRun(journal, executor, adapter, source, true, "E1");
                var originalRun = source.LastRun.RunId;
                source = store.Load(source.Id);
                var child = PrepareTextFork(store, source, source.Messages.Count);
                store.Commit(child, new ConversationCommit()); child = store.Load(child.Id);
                JournalForkFinishRun(journal, executor, adapter, child, false, "E1");
                var proof = ((IEventStore)journal).ReadFinishEvidence(child, child.LastRun.RunId, new[] { "E1" });
                AssertEqual(3, proof.Facts.Count, "inherited proof contains canonical call, start and terminal");
                AssertTrue(proof.Facts.All(fact => fact.RunId == originalRun), "proof keeps the original execution owner/run");
                AssertEqual(0, journal.ReadWindow(child.Id, long.MaxValue, 8).Count, "fork creates no child terminal facts");
                AssertEqual(0, child.LastRun.KernelState.Summary.ToolStepsUsed, "historical proof adds no child dispatch");
                AssertEqual("undelivered_evidence", JournalForkFinishAdmission(journal, child, "E1").Code,
                    "ancestry does not bypass delivery admission");

                JournalForkFinishRun(journal, executor, adapter, source, true, "E2");
                AssertEqual("unknown_evidence", JournalForkFinishAdmission(journal, child, "E2", "E2").Code,
                    "source results after the frozen fork revision cannot be inherited");
                var originalCall = source.Messages.Single(message => message.AcceptedCallOrigin != null && message.RunId == originalRun);
                var previous = source.Messages.Select(message => message.Id).ToArray();
                source.Messages.RemoveAll(message => message.ToolCallId == originalCall.ToolCallId ||
                    message.Activity?.ToolCallId == originalCall.ToolCallId);
                store.Commit(source, ConversationCommit.RewriteHistory(previous, source.Messages));
                AssertTrue(JournalForkFinishAdmission(journal, child, "E1", "E1").Accepted,
                    "removing source history cannot erase the child's retained canonical proof");
                JournalForkFinishRun(journal, executor, adapter, child, true, "E2");
                var ownProof = ((IEventStore)journal).ReadFinishEvidence(child, child.LastRun.RunId, new[] { "E2" });
                AssertTrue(ownProof.Facts.Count == 3 && ownProof.Facts.All(fact => fact.RunId == child.LastRun.RunId),
                    "child E2 resolves its own execution despite the source's later E2 ordinal collision");
                var grandchild = PrepareTextFork(store, child, child.Messages.Count);
                store.Commit(grandchild, new ConversationCommit()); grandchild = store.Load(grandchild.Id);
                JournalForkFinishRun(journal, executor, adapter, grandchild, false, "E1");
                store.Delete(source.Host, source.DocumentKey, source.Id);
                store.Delete(child.Host, child.DocumentKey, child.Id);
                journal = new ConversationJournal(paths, scope); journal.Rebuild(); store = journal;
                grandchild = store.Load(grandchild.Id);
                JournalForkFinishRun(journal, executor, adapter, grandchild, false, "E1");
                AssertEqual(0, journal.ReadWindow(grandchild.Id, long.MaxValue, 8).Count,
                    "nested ancestry and restart never copy parent executions into a descendant");
            });
        }

        private static void JournalForkFinishMembership()
        {
            WithTempExecutor((executor, adapter) => {
                var journal = new ConversationJournal(FixturePaths.Value, "document:" + DocumentAuthorityId.Create().Id);
                IConversationStore store = journal;
                var source = new ChatSessionService(adapter, store).CreatePersistentChat("Fork membership");
                JournalForkFinishRun(journal, executor, adapter, source, true, "E1"); source = store.Load(source.Id);
                var accepted = source.Messages.Single(message => message.AcceptedCallOrigin != null);
                var prefix = PrepareTextFork(store, source, source.Messages.IndexOf(accepted) + 1);
                store.Commit(prefix, new ConversationCommit()); prefix = store.Load(prefix.Id);
                JournalForkFinishRun(journal, executor, adapter, prefix, false, null);
                AssertEqual("unknown_evidence", JournalForkFinishAdmission(journal, prefix, "E1", "E1").Code,
                    "an alias outside the retained prefix cannot prove an incomplete inherited exchange");

                var child = PrepareTextFork(store, source, source.Messages.Count);
                store.Commit(child, new ConversationCommit()); child = store.Load(child.Id);
                JournalForkFinishRun(journal, executor, adapter, child, false, "E1");
                var grandchild = PrepareTextFork(store, child, child.Messages.Count);
                store.Commit(grandchild, new ConversationCommit()); grandchild = store.Load(grandchild.Id);
                var previous = child.Messages.Select(message => message.Id).ToArray();
                child.Messages.RemoveAll(message => message.ToolCallId == accepted.ToolCallId ||
                    message.Activity?.ToolCallId == accepted.ToolCallId);
                store.Commit(child, ConversationCommit.RewriteHistory(previous, child.Messages));
                AssertEqual("unknown_evidence", JournalForkFinishAdmission(journal, child, "E1", "E1").Code,
                    "removing the exchange fences old canonical facts from this child's finish");
                JournalForkFinishRun(journal, executor, adapter, grandchild, false, "E1");

                var unrelated = new ChatSessionService(adapter, store).CreatePersistentChat("Unrelated");
                JournalForkFinishRun(journal, executor, adapter, unrelated, false, null);
                AssertEqual("unknown_evidence", JournalForkFinishAdmission(journal, unrelated, "E1", "E1").Code,
                    "same-scope unrelated chats cannot borrow an ancestor alias");
                var foreign = new ConversationJournal(FixturePaths.Value, "document:" + DocumentAuthorityId.Create().Id);
                RuntimeThrows<ChatConcurrencyException>(() => ((IEventStore)foreign).ReadFinishEvidence(
                    grandchild, grandchild.LastRun.RunId, new[] { "E1" }));
            });
        }

        private sealed class PatchCompletionFaultOwner : IPreparableToolHandler
        {
            private readonly ResourceRef _source;
            private readonly string _path;
            internal int Dispatches;
            internal PatchCompletionFaultOwner(ResourceRef source, string path) { _source = source; _path = path; }
            public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
            {
                return Task.FromResult(new ToolPreparationResult(ToolResult.Ok("Prepared"), "{\"guard\":1}",
                    patchTarget: new PatchTargetPlan { Source = _source, SourceHash = "pinned", EffectIdentity = _source.Identity,
                        Preview = new List<PatchPreview> { new PatchPreview { Before = "old", After = "new" } } }));
            }
            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
            {
                context.MarkDispatchPossible(); Dispatches++; File.WriteAllText(_path, "new\n");
                return Task.FromResult(context.Complete(new ToolHandlerResult(ToolResult.Ok("Updated"), ToolEffectEvidence.VerifiedChange)));
            }
        }

        private sealed class PatchCompletionFaultObserver : IToolMutationObserver
        {
            public string Prepare(ToolExecutionContext context, IDictionary<string, object> arguments, string preparedStateJson = null) { return "attempt"; }
            public void MarkDispatchMayHaveOccurred(string mutationId) { }
            public ResourceAuthorityCommit Complete(string mutationId, ToolExecutionRecord record, string preparedStateJson = null)
            { throw new IOException("Lost completion acknowledgement after the target wrote."); }
            public void AbandonBeforeDispatch(string mutationId) { }
            public void ReleaseUnresolved(string mutationId) { }
        }

        private static void UniversalPatchCompletionFailureRetainsInspection()
        {
            WithTempPaths(paths => {
                var workspace = new WorkspaceStore(paths).Open(Path.Combine(paths.Root, "patch-completion-fault"));
                var path = Path.Combine(workspace.RootPath, "a.txt"); File.WriteAllText(path, "old\n");
                File.WriteAllText(Path.Combine(workspace.RootPath, "other.txt"), "unrelated\n");
                var files = new WorkspaceFileService(paths); var source = files.ReadText(workspace, "a.txt").Evidence.Resource;
                var owner = new PatchCompletionFaultOwner(source, path);
                var entry = ResourceMutationToolCatalog.GetTools().Single(item => item.Id == ResourceMutationToolCatalog.Patch);
                var registry = new ToolHandlerRegistry();
                registry.Register(ToolPackSnapshot.Capture(new ToolDescriptor(entry.Id, entry.Description, entry.ArgumentSchemaJson),
                    entry.Policy, entry.Binding), new ResourcePatchBatchHandler(owner));
                var runtime = new ToolRuntime(registry, "agent", true, true, mutationObserver: new PatchCompletionFaultObserver());
                var call = new ToolCall("patch-completion-fault", entry.Id,
                    "{\"targets\":[{\"target\":\"a.txt\",\"edits\":[{\"old\":\"old\",\"new\":\"new\"}]}]}");
                var record = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual("new\n", File.ReadAllText(path), "the observer fails only after the actual target write");
                AssertEqual(1, owner.Dispatches, "the target dispatches once");
                AssertEqual(ToolExecutionOutcome.Unknown, record.Outcome, "lost completion never becomes verified success");
                AssertEqual(ResourceEffectOutcome.UnknownAfterDispatch, record.ResourceEffect.Outcome, "aggregate effect remains unknown");
                var target = record.ResourceEffect.Targets.Single();
                AssertTrue(target.Status == ResourceTargetStatus.Unknown && target.WriteStarted && target.After == null,
                    "target retains uncertain dispatch without inventing an after revision");
                AssertTrue(record.ResourceEffect.Impacts.Single().Identity.Equals(source.Identity) &&
                    record.ResourceEffect.Impacts.Single().After == null, "prepared target identity survives completion failure");

                var tracker = new AgentProgressTracker();
                tracker.Observe(call, ToolExecutionProgress.Capture(record), record.Message, record.ResourceEvidence, record.ResourceEffect);
                AssertTrue(tracker.InspectionRequired, "unknown target requires a fresh inspection");
                Thread.Sleep(2);
                Action<string> inspect = name => tracker.Observe(new ToolCall("inspect-" + name, "core.read", "{\"target\":\"" + name + "\"}"),
                    new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true, completedUtc: DateTime.UtcNow),
                    "Read current file", new[] { files.ReadText(workspace, name).Evidence }, null);
                inspect("other.txt");
                AssertTrue(tracker.InspectionRequired, "another file cannot discharge the known target obligation");
                inspect("a.txt");
                AssertTrue(!tracker.InspectionRequired, "complete fresh target read discharges the inspection obligation");
                string reason, message; int delay;
                AssertTrue(!tracker.CanDispatch(call, out reason, out message, out delay, runtime.Describe(call)),
                    "inspection never authorizes replay of the uncertain patch");
                AssertEqual("repeated_unknown_tool_call", reason, "the original unknown replay guard is preserved");
            });
        }
    }
}
