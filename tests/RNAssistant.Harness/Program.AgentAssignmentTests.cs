using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void AssignmentError(string code, Action action)
        {
            try { action(); }
            catch (AgentCommunicationException error) { AssertEqual(code, error.Code, "classified assignment rejection"); return; }
            throw new InvalidOperationException("Expected " + code);
        }

        private static void AgentAssignmentCorrelationAndRecovery()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(EventStore(store));
                var service = new AgentCommunicationService();
                var a = AgentChat(store, "A"); var b = AgentChat(store, "B"); var c = AgentChat(store, "C");
                foreach (var chat in new[] { a, b, c }) RegisterAgent(service, AgentEndpoint(chat, inbox));
                BindPeerWait(inbox, a);
                var plain = service.Send(inbox, a, 20, "wait-run", "plain", AgentChatAddress.IdFor(b.Id), "Ordinary question", () => { }, true);
                AssignmentError("agent_request_not_found", () => service.PrepareWait(inbox, a, 20, 1, new[] { plain.MessageId }, "any", true));
                ApplyPeerInput(store, inbox, b);
                AssignmentError("agent_assignment_not_found", () => service.Resolve(inbox, b, 20, "b-run", "invalid", plain.MessageId, "completed", "No", () => { }));
                var ab = service.Send(inbox, a, 20, "wait-run", "ab", AgentChatAddress.IdFor(b.Id), "Prepare result B", () => { }, assignment: true);
                var ac = service.Send(inbox, a, 20, "wait-run", "ac", AgentChatAddress.IdFor(c.Id), "Prepare result C", () => { }, assignment: true);
                AssignmentError("agent_assignment_not_found", () => service.Resolve(inbox, b, 20, "b-run", "unapplied", ab.MessageId, "completed", "No", () => { }));
                ApplyPeerInput(store, inbox, b); ApplyPeerInput(store, inbox, c);
                AssertContains(ConversationInputMessage.ModelText(b.Messages.Last()), "assignment=true", "model sees typed request without a separate task history");
                AssignmentError("agent_assignment_not_found", () => service.Resolve(inbox, c, 20, "c-run", "foreign", ab.MessageId, "completed", "No", () => { }));
                AssignmentError("agent_assignment_outcome_invalid", () => service.Resolve(inbox, b, 20, "b-run", "bad-outcome", ab.MessageId, "success", "No", () => { }));
                service.Send(inbox, b, 20, "b-run", "progress", AgentChatAddress.IdFor(a.Id), "Still working", () => { }, replyTo: ab.MessageId);
                var requests = new[] { ab.MessageId, ac.MessageId };
                var progress = service.CompleteWaitAsync(inbox, a, 20, "wait-run",
                    service.PrepareWait(inbox, a, 20, 1, requests, "all", true), CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(progress.Status == AgentWaitStatus.InputPending && progress.Matches.Count == 0, "ordinary reply is input, not completed work");
                foreach (var input in inbox.TakeRunInput(a)) ApplyPeerInput(store, inbox, a, input);
                var dispatches = 0;
                var report = service.Resolve(inbox, b, 20, "b-run", "report-b", ab.MessageId, "completed", "B result, external effect not checked", () => dispatches++);
                var duplicate = service.Resolve(inbox, b, 20, "b-run", "report-b", ab.MessageId, "completed", "B result, external effect not checked", () => dispatches++);
                AssertTrue(report.MessageId == duplicate.MessageId && report.RequestId == ab.MessageId && dispatches == 1, "exact operation read-back does not dispatch twice");
                AssignmentError("agent_assignment_already_reported", () => service.Resolve(inbox, b, 20, "b-run", "replace", ab.MessageId, "failed", "Correction", () => dispatches++));
                var partial = service.CompleteWaitAsync(inbox, a, 20, "wait-run",
                    service.PrepareWait(inbox, a, 20, 1, requests, "all", true), CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(partial.Status == AgentWaitStatus.TimedOut && partial.Matches.Single().ReportedOutcome == AgentAssignmentOutcome.Completed,
                    "all preserves an early terminal report without claiming all work succeeded");
                foreach (var input in inbox.TakeRunInput(a)) ApplyPeerInput(store, inbox, a, input);
                var condition = service.PrepareWait(inbox, a, 20, 10, requests, "all", true);
                var all = service.CompleteWaitAsync(inbox, a, 20, "wait-run", condition, CancellationToken.None);
                AssertTrue(!all.IsCompleted, "all still needs C");
                service.Resolve(inbox, c, 20, "c-run", "report-c", ac.MessageId, "failed", "C failed", () => { });
                AssertTrue(all.Wait(3000) && all.Result.Status == AgentWaitStatus.ResolutionsReady &&
                    all.Result.Matches.Any(item => item.ReportedOutcome == AgentAssignmentOutcome.Failed), "negative outcome also ends the wait through intake signal");
                AssertEqual("wait-run", a.LastRun.RunId, "wait preserves the same run");
                foreach (var input in inbox.TakeRunInput(a)) ApplyPeerInput(store, inbox, a, input);
                AssertContains(ConversationInputMessage.ModelText(a.Messages.Last()), "reported_outcome=\"failed\"", "report provenance is explicit in context");
                inbox.End(a, true, false);
                a = store.Load(a.Id); b = store.Load(b.Id); c = store.Load(c.Id);
                inbox = new ConversationInboxService(EventStore(store)); service = new AgentCommunicationService();
                foreach (var chat in new[] { a, b, c }) RegisterAgent(service, AgentEndpoint(chat, inbox));
                BindPeerWait(inbox, a, "explicit-continuation");
                var restored = JsonConvert.DeserializeObject<AgentWaitCondition>(JsonConvert.SerializeObject(condition));
                AssertTrue(restored.Completion && restored.All && restored.DeadlineUtc == condition.DeadlineUtc, "saved wait retains its precise condition and deadline");
                var recovered = service.CompleteWaitAsync(inbox, a, 20, "explicit-continuation", restored, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(recovered.Status == AgentWaitStatus.ResolutionsReady && recovered.Matches.Count == 2, "applied reports survive restart");
                AssertEqual(0, inbox.TakeRunInput(a).Count, "recovered reports are not delivered again");
                AssertTrue(service.Assignments(inbox, a, 20, "outgoing", null).Items.All(item => item.State == AgentAssignmentState.ResolutionReceived),
                    "projection reconstructs from canonical journals");
            });
        }

        private static void AgentAssignmentUncertainReport()
        {
            foreach (var destinationFails in new[] { true, false }) WithTempPaths(paths => {
                var store = new ChatStore(paths); var events = EventStore(store);
                var failing = new InboxFailingEvents(events);
                var aInbox = new ConversationInboxService(failing);
                var bInbox = new ConversationInboxService(destinationFails ? events : new AgentReceiptFailureEvents(events));
                var service = new AgentCommunicationService(); var a = AgentChat(store, "A"); var b = AgentChat(store, "B");
                var starts = 0;
                RegisterAgent(service, AgentEndpoint(a, aInbox, _ => starts++)); RegisterAgent(service, AgentEndpoint(b, bInbox));
                var request = service.Send(aInbox, a, 20, "a", "request", AgentChatAddress.IdFor(b.Id), "Result needed", () => { }, assignment: true);
                ApplyPeerInput(store, bInbox, b); failing.Fail = destinationFails;
                var dispatches = 0; var failed = false;
                try { service.Resolve(bInbox, b, 20, "b", "report", request.MessageId, "declined", "Cannot do this", () => dispatches++); }
                catch (IOException) { failed = true; }
                AssertTrue(failed && dispatches == 1, "uncertain result crosses dispatch once");
                AssertEqual(destinationFails ? 0 : 1, starts, "durably accepted report is scheduled despite receipt failure");
                AssertEqual(AgentAssignmentState.ResolutionPrepared, service.Assignments(bInbox, b, 20, "incoming", null).Items.Single().State,
                    "sender projection does not invent a confirmed receipt");
                AssertEqual(destinationFails ? AgentAssignmentState.Open : AgentAssignmentState.ResolutionReceived,
                    service.Assignments(aInbox, a, 20, "outgoing", null).Items.Single().State, "recipient reports its own durable intake");
                a = store.Load(a.Id); b = store.Load(b.Id); aInbox = new ConversationInboxService(events); bInbox = new ConversationInboxService(events);
                service = new AgentCommunicationService(); RegisterAgent(service, AgentEndpoint(a, aInbox)); RegisterAgent(service, AgentEndpoint(b, bInbox));
                AssignmentError("agent_assignment_already_reported", () => service.Resolve(bInbox, b, 20, "new-run", "replacement", request.MessageId,
                    "completed", "Try again", () => dispatches++));
                Action repeat = () => service.Resolve(bInbox, b, 20, "b", "report", request.MessageId, "declined", "Cannot do this", () => dispatches++);
                if (destinationFails) AssignmentError("agent_delivery_unknown", repeat); else repeat();
                AssertEqual(1, dispatches, "recovery reads receipts only; unknown report is never replayed or replaced");
            });
        }

        private static void AgentAssignmentBoundedProjection()
        {
            WithTempPaths(paths => {
                var store = new ChatStore(paths); var inbox = new ConversationInboxService(EventStore(store));
                var service = new AgentCommunicationService(); var a = AgentChat(store, "A"); var b = AgentChat(store, "B");
                var starts = 0;
                foreach (var chat in new[] { a, b }) RegisterAgent(service, AgentEndpoint(chat, inbox, _ => starts++));
                for (var i = 0; i < 51; i++) service.Send(inbox, a, 2, "a", "assignment-" + i,
                    AgentChatAddress.IdFor(b.Id), new string('x', 170) + "BODY_NOT_A_SUMMARY", () => { }, assignment: true);
                var before = starts; var revision = a.Revision;
                var first = service.Assignments(inbox, a, 2, "outgoing", null);
                var next = service.Assignments(inbox, a, 2, "outgoing", first.NextRequest);
                AssertTrue(first.Truncated && first.Items.Count == 50 && !next.Truncated && next.Items.Count == 1,
                    "assignment listing is paged independently of participant limits");
                AssertEqual(51, first.Items.Concat(next.Items).Select(item => item.RequestId).Distinct().Count(), "page boundary neither skips nor duplicates requests");
                AssertTrue(first.Items.All(item => item.Summary.Length <= 161 && item.State == AgentAssignmentState.Open), "open requests have bounded summaries, not assumed acceptance");
                AssertEqual(0, service.Assignments(inbox, a, 2, "incoming", null).Items.Count, "direction is local to this chat");
                AssertTrue(service.Assignments(inbox, b, 2, "incoming", null).Items.All(item => item.InputStatus == ConversationInputStatus.Pending),
                    "incoming intake is distinguished from context application");
                AssertTrue(starts == before && a.Revision == revision && !JsonConvert.SerializeObject(first).Contains("BODY_NOT_A_SUMMARY"),
                    "listing is read-only and does not wake peers or duplicate full bodies");
                var incoming = ApplyPeerInput(store, inbox, b);
                inbox.Submit(a, InboxInput("human-first", "User request before result", InputDelivery.Queue));
                service.Resolve(inbox, b, 2, "b", "behind-human", incoming.AgentCommunication.MessageId, "declined", "QUEUED_RESULT_BODY", () => { });
                var page = service.Assignments(inbox, a, 2, "outgoing", null);
                var row = page.Items.Concat(service.Assignments(inbox, a, 2, "outgoing", page.NextRequest).Items)
                    .First(item => item.RequestId == incoming.AgentCommunication.MessageId);
                AssertTrue(row.State == AgentAssignmentState.ResolutionReceived && row.InputStatus == ConversationInputStatus.Pending &&
                    !JsonConvert.SerializeObject(row).Contains("QUEUED_RESULT_BODY"), "listing reports receipt without reading the pending body past human FIFO");
                BindPeerWait(inbox, a);
                var wait = service.CompleteWaitAsync(inbox, a, 2, "wait-run", service.PrepareWait(inbox, a, 2, 1,
                    new[] { incoming.AgentCommunication.MessageId }, "any", true), CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(wait.Status == AgentWaitStatus.InputPending && wait.Matches.Count == 0 && inbox.TakeRunInput(a).Count == 0,
                    "completion wait cannot consume a report behind human input");
                AssignmentError("agent_communication_disabled", () => service.Assignments(inbox, a, 0, "outgoing", null));
            });
        }

        private static void AgentAssignmentOfficeWire()
        {
            foreach (var format in new[] { "json_object", "json_schema" }) WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value); var inbox = new ConversationInboxService(EventStore(store));
                var service = new AgentCommunicationService(); var a = AgentChat(store, "Requester"); var b = AgentChat(store, "Recipient");
                foreach (var chat in new[] { a, b }) { chat.Mode = ChatModes.Plan; chat.MutationPolicy = MutationPolicy.Deny;
                    store.Save(chat); RegisterAgent(service, AgentEndpoint(chat, inbox)); }
                var assignment = service.Send(inbox, a, 2, "a", "assignment", AgentChatAddress.IdFor(b.Id), "Report assessment", () => { }, assignment: true);
                ApplyPeerInput(store, inbox, b);
                executor.AgentCommunication = service; executor.AgentInbox = inbox;
                var calls = 0;
                LlmCompletionDelegate complete = (config, messages, options, stream, token) => {
                    AssertTrue(++calls <= 2, "resolve requires no approval or repair round trip");
                    var response = calls == 1 ? ModelProtocolWire.WriteAcceptedCall("Report the requested assessment", null,
                        new ConversationToolCall { Name = AgentCommunicationToolCatalog.Resolve, Arguments = new JObject {
                            ["request_id"] = assignment.MessageId, ["outcome"] = "declined", ["text"] = "Needs information from the user" } })
                        : ScriptedVerifiedFinish(b, "Report delivered", "Declined", "Sent a report, no external effects claimed", AgentCommunicationToolCatalog.Resolve);
                    if (format == "json_schema") response = new JObject { ["response"] = JObject.Parse(response) }.ToString();
                    return Task.FromResult(new LlmCompletionResult { Content = response });
                };
                var settings = new AppSettings { AgentResponseMode = format, StreamResponses = false, AutoCompressContext = false,
                    SmartChatTitles = false, AutoConfirmToolActions = false };
                var run = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), complete, null, inbox: inbox)
                    .ExecuteAsync(ChatModes.Plan, "Address the received assignment", b, NewContext(adapter), settings,
                        executor.GetControllerTools().ToList(), null, null, null, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(!run.WaitingForConfirmation && calls == 2, "typed report is permitted in Plan/Deny without elevation");
                var saved = store.Load(b.Id);
                AssertEqual(RunLifecycle.Completed, saved.LastRun.KernelState.Summary.Lifecycle, "report receipt supports finish of reporting, not external work");
                var input = inbox.Snapshot(a).Items.Single();
                AssertEqual(AgentAssignmentOutcome.Declined, input.AgentCommunication.Resolution.Value, "typed outcome reaches original requester");
                AssertEqual(assignment.MessageId, input.AgentCommunication.ReplyTo, "runtime routes by original applied assignment");
            });
        }
    }
}
