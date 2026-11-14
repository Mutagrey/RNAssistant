using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RetainedAttemptProtocolBarriers()
        {
            var retained = new List<RetainedModelAttempt>(); var calls = 0;
            var request = NewProtocolRequest(2);
            request.RetainAttemptAsync = attempt => { retained.Add(attempt); return Task.CompletedTask; };
            var client = new ModelProtocolClient((settings, messages, options, stream, token) => {
                calls++;
                if (calls > 1) AssertEqual(1, retained.Count, "retry waits for the mandatory commit");
                return Task.FromResult(new LlmCompletionResult { Content = calls == 1
                    ? "{\"action\":\"finish\",\"reason\":\"Visible reason\",\"message\":\"Visible rejected text\",\"decision\":\"Visible decision\",\"bad\":true}"
                    : ModelProtocolWire.WriteAcceptedDecision("finish", "Answer.", "Accepted", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed),
                    ReasoningContent = "Provider reasoning" });
            });
            var result = client.GetResponseAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            AssertTrue(result.Response != null, "one repaired response admitted");
            AssertEqual(1, retained.Count, "exactly one rejected attempt retained");
            AssertEqual("Visible rejected text", retained[0].Body.Message, "message survives rejection");
            AssertEqual("Visible reason", retained[0].Body.Reason, "reason survives rejection");
            AssertEqual("Visible decision", retained[0].Body.Decision, "decision survives rejection");
            AssertEqual("Provider reasoning", retained[0].Body.Reasoning, "provider reasoning survives rejection");
            AssertTrue(retained[0].Body.AttemptId != result.SourceModelAttemptId, "HTTP requests have distinct immutable identities");
            calls = 0; RetainedModelAttempt unsaved = null;
            request.RetainAttemptAsync = attempt => throw new IOException("disk full");
            result = client.GetResponseAsync(request, new ModelProtocolProgress { RetentionFailed = attempt => unsaved = attempt },
                CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(1, calls, "failed mandatory commit prevents a second model request");
            AssertEqual(ModelProtocolFailureKind.Persistence, result.Failure.Kind, "storage failure remains explicit");
            AssertEqual("Visible rejected text", unsaved.Body.Message, "unsaved text is still available for presentation");
            using (var cancellation = new CancellationTokenSource())
            {
                request = NewProtocolRequest(); retained.Clear();
                request.RetainAttemptAsync = attempt => {
                    AssertTrue(cancellation.IsCancellationRequested, "request is already cancelled at retention");
                    retained.Add(attempt); return Task.CompletedTask;
                };
                client = new ModelProtocolClient((settings, messages, options, stream, token) => {
                    stream(new LlmStreamUpdate { ContentDelta = "{\"reason\":\"partial reason\",\"message\":\"partial", ReasoningDelta = "partial thinking" });
                    cancellation.Cancel(); throw new OperationCanceledException(token);
                });
                client.GetResponseAsync(request, null, cancellation.Token).GetAwaiter().GetResult();
                AssertEqual(ModelRuntimeDisposition.Cancelled, retained.Single().Runtime, "cancelled partial is retained");
                AssertEqual("partial", retained.Single().Body.Message, "partial JSON is observed, never executed");
            }
        }

        private static void RetainedAttemptRuntimeHistory()
        {
            foreach (var native in new[] { false, true }) WithTempExecutor((executor, adapter) => {
                var paths = FixturePaths.Value;
                var journal = native ? new ConversationJournal(paths, "document:" + DocumentAuthorityId.Create().Id) : null;
                var legacy = native ? null : new ChatStore(paths);
                IConversationStore store = native ? (IConversationStore)journal : ConversationStore(legacy);
                IEventStore events = native ? (IEventStore)journal : EventStore(legacy);
                var chat = new ChatSessionService(adapter, store).CreatePersistentChat("Retained attempts");
                var calls = 0; var modelContexts = new List<string>();
                var service = new ConversationRunService(adapter, executor, store, events,
                    (settings, messages, options, stream, token) => {
                        calls++; modelContexts.Add(string.Join("\n", messages.Select(message => message.Content)));
                        return Task.FromResult(new LlmCompletionResult { ReasoningContent = "Retained thinking " + calls,
                            Content = calls <= 2
                                ? ModelProtocolWire.WriteAcceptedDecision("reflection", "Reflection reason " + calls, "", decision: "Decision " + calls)
                                : ModelProtocolWire.WriteAcceptedDecision("finish", "Answer ready.", "Final answer", results: new ConversationResultClaim[0], outcome: ConversationOutcome.Completed) });
                    });
                var result = service.ExecuteAsync(ChatModes.Agent, "Answer directly", chat, NewContext(adapter),
                    new AppSettings { SmartChatTitles = false, AutoCompressContext = false, ContextWindowOverrideTokens = 65536,
                        MaxConsecutiveReflections = 1 }, new ToolCatalogEntry[0], null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, result.ResponseStatus, "runtime continues after retained reflection rejection");
                var attemptMessage = chat.Messages.Single(message => message.Activity?.Kind == "model_attempt");
                var attempt = ConversationRunProjection.ReadRetainedAttempt(attemptMessage);
                AssertEqual(ModelProtocolDisposition.Accepted, attempt.Protocol, "valid JSON differs from runtime admission");
                AssertEqual("Decision 2", attempt.Body.Decision, "runtime rejected decision remains available");
                AssertTrue(modelContexts.Last().IndexOf("Retained thinking 2", StringComparison.Ordinal) < 0 &&
                    modelContexts.Last().IndexOf("Decision 2", StringComparison.Ordinal) < 0,
                    "unaccepted provider reasoning never becomes ordinary model context");
                var copy = store.Load(chat.Id).Messages.Single(message => message.Id == attemptMessage.Id);
                AssertEqual(attemptMessage.Content, copy.Content, "successful commit survives reopen");
                AssertEqual("Decision 2", ChatMessageViewDto.From(copy).RetainedAttempt.Body.Decision, "shared UI receives the retained body");
                using (var cancellation = new CancellationTokenSource())
                {
                    var cancelService = new ConversationRunService(adapter, executor, store, events,
                        (settings, messages, options, stream, token) => {
                            stream(new LlmStreamUpdate { ContentDelta = "{\"message\":\"Durable partial", ReasoningDelta = "Interrupted thinking" });
                            cancellation.Cancel(); throw new OperationCanceledException(token);
                        });
                    cancelService.ExecuteAsync(ChatModes.Agent, "Cancel request", chat, NewContext(adapter),
                        new AppSettings { SmartChatTitles = false, AutoCompressContext = false, ContextWindowOverrideTokens = 65536 },
                        new ToolCatalogEntry[0], null, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
                    var cancelled = store.Load(chat.Id).Messages.Where(message => message.Activity?.Kind == "model_attempt")
                        .Select(ConversationRunProjection.ReadRetainedAttempt).Single(item => item.Runtime == ModelRuntimeDisposition.Cancelled);
                    AssertEqual("Durable partial", cancelled.Body.Message, "cancelled request does not cancel mandatory history commit");
                    AssertEqual("Interrupted thinking", cancelled.Body.Reasoning, "partial reasoning survives reopen");
                }
                if (native)
                {
                    journal.Rebuild();
                    AssertEqual(copy.Content, store.Load(chat.Id).Messages.Single(message => message.Id == copy.Id).Content,
                        "journal rebuild preserves semantic attempt and CAS closure");
                }
            });
        }

        private static void RetainedAttemptLostAcknowledgement()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "document:retained-receipt"); IConversationStore store = journal;
                var chat = store.CreateTransient("Word", "doc", "Document", "Retained receipt"); store.Commit(chat, new ConversationCommit());
                var initial = CheckpointState("run", RunLifecycle.Running, 0, 0);
                chat.LastRun = new ChatRunRecord { RunId = initial.Summary.RunId, TurnId = initial.Summary.TurnId,
                    StartedUtc = DateTime.UtcNow, KernelState = initial, ConversationPolicy = ConversationRunPolicy.For(ChatModes.Agent) };
                new ConversationRunCommit(chat, new AgentRunEvent(AgentRunEventKind.Started, initial.Summary, limits: initial.Limits),
                    new ChatMessage[0]).Persist(store.CommitRun, chat);
                ConversationAttemptSnapshot body;
                using (var stream = new ConversationStreamProgressProjector("retained-ack", false, null))
                {
                    stream.OnUpdate(new LlmStreamUpdate { ContentDelta = "{\"message\":\"Retained after lost ACK" });
                    body = stream.End(ModelTransportOutcome.Cancelled);
                }
                var fact = new AgentRunEvent(AgentRunEventKind.ModelAttemptRetained, initial.Summary, "step",
                    retainedAttempt: new RetainedModelAttempt(body, ModelProtocolDisposition.Unvalidated, ModelRuntimeDisposition.Cancelled, "Stopped"));
                var message = ConversationRunProjection.CreateRetainedAttempt(fact); chat.Messages.Add(message);
                var command = new ConversationRunCommit(chat, fact, new[] { message });
                journal.Journal.Fault = point => { if (point == "after_flush") throw new IOException("lost ACK"); };
                command.Persist(store.CommitRun, chat); journal.Journal.Fault = null;
                AssertTrue(command.Position != null, "exact receipt resolves lost retention acknowledgement");
                var revision = chat.Revision; store.CommitRun(command);
                AssertEqual(revision, store.ReadMetadata(chat.Id).Revision, "receipt lookup does not append twice");
                journal.Rebuild();
                var semantic = ((IEventStore)journal).Read(ConversationMetadata.From(store.Load(chat.Id)), SessionEventReadMode.Validated)
                    .Single(item => item.Type == SessionEventTypes.ModelAttemptRetained);
                AssertEqual("retained-ack", (string)semantic.Data["Body"]["AttemptId"], "journal exposes the mandatory semantic attempt through its existing event port");
                AssertEqual(SessionEventDurability.Mandatory, SessionEventDescriptors.For(SessionEventKind.ModelAttemptRetained).Durability,
                    "retained attempt is not optional diagnostic traffic");
                AssertEqual("Retained after lost ACK", ChatMessageViewDto.From(store.Load(chat.Id).Messages.Single()).RetainedAttempt.Body.Message,
                    "lost acknowledgement and rebuild preserve one retained body");
            });
        }
    }
}
