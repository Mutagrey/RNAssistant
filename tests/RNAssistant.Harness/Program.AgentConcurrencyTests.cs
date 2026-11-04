using System;
using System.Diagnostics;
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
using RNAssistant.Core.Storage;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Real Inbox/journal/Office service with a fake host and 60 ms scripted provider.
        // Small local samples diagnose scaling; they are not latency qualification/SLA.
        private static void AgentConcurrencyBaseline()
        {
            foreach (var count in Environment.GetEnvironmentVariable("RNA_AGENT_BENCH_WORKERS") == "8" ? new[] { 8 } : new[] { 1, 2, 4, 8 })
            WithTempExecutor((executor, adapter) => {
                var store = new ChatStore(FixturePaths.Value); var inbox = new ConversationInboxService(EventStore(store));
                var service = new AgentCommunicationService(() => count + 1);
                var application = new ConversationApplicationService(inbox,
                    new ConversationRunOwner(ConversationLeaseService.ForOffice(FixturePaths.Value)));
                executor.AgentCommunication = service; executor.AgentInbox = inbox;
                var source = AgentChat(store, "Origin"); RegisterAgent(service, AgentEndpoint(source, inbox));
                var peers = Enumerable.Range(0, count).Select(i => AgentChat(store, "Peer " + i)).ToArray();
                var ports = new ConversationQueueFake[count];
                var offered = new double[count]; var durable = new double[count]; var first = new double[count];
                var waking = new double[count]; var busy = new double[count]; var next = new double[count];
                var calls = new int[count]; var waiting = new int[count]; var runIds = new string[count];
                var clock = Stopwatch.StartNew(); var active = 0; var peak = 0; long wireCharacters = 0;
                var settings = new AppSettings { SmartChatTitles = false, AutoCompressContext = false, StreamResponses = false,
                    MaxCollaborationAgents = count + 1, MaxAgentIterations = 4 };
                for (var index = 0; index < count; index++)
                {
                    var i = index; var port = ports[i] = new ConversationQueueFake(inbox, store);
                    port.Execute = async (current, input, token) => {
                        LlmCompletionDelegate model = async (config, messages, options, stream, ct) => {
                            var number = Interlocked.Increment(ref calls[i]);
                            Interlocked.Add(ref wireCharacters, FlattenSimple(messages).Length);
                            if (number == 1) first[i] = clock.Elapsed.TotalMilliseconds - offered[i];
                            else
                            {
                                AssertEqual(2, number, "no polling/duplicate model calls");
                                AssertContains(FlattenSimple(messages), "WAKE_" + i, "busy peer receives its own input");
                                busy[i] = clock.Elapsed.TotalMilliseconds - offered[i];
                                next[i] = clock.Elapsed.TotalMilliseconds - waking[i];
                            }
                            var concurrent = Interlocked.Increment(ref active);
                            int old; do { old = peak; } while (concurrent > old && Interlocked.CompareExchange(ref peak, concurrent, old) != old);
                            try { await Task.Delay(60, ct).ConfigureAwait(false); }
                            finally { Interlocked.Decrement(ref active); }
                            return new LlmCompletionResult { Content = number == 1
                                ? ModelProtocolWire.WriteAcceptedCall("Wait for result", null, new ConversationToolCall {
                                    Name = "agents.wait", Arguments = new JObject { ["timeout_seconds"] = 10 } })
                                : ScriptedVerifiedFinish(current, "Received", "Input received", "Wait returned on input", "agents.wait") };
                        };
                        var runtime = new ConversationRunService(adapter, executor, ConversationStore(store), EventStore(store), model,
                            null, saved: session => {
                                if (ConversationInboxService.HasPendingWait(session))
                                { runIds[i] = session.LastRun.RunId; Interlocked.Exchange(ref waiting[i], 1); }
                                else if (waiting[i] == 1 && waking[i] == 0) waking[i] = clock.Elapsed.TotalMilliseconds;
                            }, inbox: inbox);
                        await runtime.ExecuteAsync(ChatModes.Agent, input.Text, current, NewContext(adapter), settings,
                            executor.GetControllerTools().ToList(), null, null, null, BuiltInSkillProvider.GetSkills(adapter),
                            token, appendUserMessage: false).ConfigureAwait(false);
                    };
                    RegisterAgent(service, AgentEndpoint(peers[i], inbox, current => application.StartQueue(current, port)));
                }
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        offered[i] = clock.Elapsed.TotalMilliseconds;
                        service.Send(inbox, source, count + 1, "origin", "first-" + i, AgentChatAddress.IdFor(peers[i].Id), "FIRST_" + i, () => { });
                        durable[i] = clock.Elapsed.TotalMilliseconds - offered[i];
                    }
                    AssertTrue(SpinWait.SpinUntil(() => waiting.All(value => value == 1), 12000), "all independent peers reach saved wait without views: " + string.Join("; ", peers.Select((peer, i) => { var summary = store.Load(peer.Id).LastRun?.KernelState?.Summary; return i + ":" + calls[i] + "/" + waiting[i] + "/" + summary?.Lifecycle + "/" + summary?.Reason + "/" + ports[i].Completed.Task.Exception?.GetBaseException(); })));
                    var observedCalls = calls.Sum(); service.List(source, count + 1);
                    AssertEqual(observedCalls, calls.Sum(), "runtime status requires no model calls");
                    for (var i = 0; i < count; i++)
                    {
                        offered[i] = clock.Elapsed.TotalMilliseconds;
                        service.Send(inbox, source, count + 1, "origin", "wake-" + i, AgentChatAddress.IdFor(peers[i].Id), "WAKE_" + i, () => { });
                    }
                    AssertTrue(Task.WaitAll(ports.Select(port => port.Completed.Task).ToArray(), 12000) && application.Drained.Wait(12000), "all peers drain");
                    for (var i = 0; i < count; i++)
                    {
                        var saved = store.Load(peers[i].Id);
                        AssertEqual(runIds[i], saved.LastRun.RunId, "wake resumes the same Run");
                        AssertEqual(RunLifecycle.Completed, saved.LastRun.KernelState.Summary.Lifecycle, "peer completes");
                        AssertEqual(2, calls[i], "one wait and one result step");
                        AssertEqual(2, saved.Messages.Count(m => m.AgentCommunication?.Sender?.ChatId == source.Id), "both messages persist exactly once");
                    }
                    Console.WriteLine("MEASURE " + JsonConvert.SerializeObject(new { workers = count, participants = count + 1,
                        peakModelRequests = peak, modelRequests = calls.Sum(), requestCharacters = wireCharacters,
                        intakeDurableMs = AgentLatency(durable), idleContextMs = AgentLatency(first),
                        busyContextMs = AgentLatency(busy), wakeNextStepMs = AgentLatency(next) }));
                }
                finally { application.CloseAsync().Wait(5000); }
            });
        }

        private static double[] AgentLatency(double[] values)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return new[] { .5, .95, .99 }.Select(p => Math.Round(sorted[(int)Math.Ceiling(sorted.Length * p) - 1], 2)).ToArray();
        }
    }
}
