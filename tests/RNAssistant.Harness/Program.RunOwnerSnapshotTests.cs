using System;
using System.Diagnostics;
using System.Globalization;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Models;
using RNAssistant.Office.Services;
using System.Linq;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void RunOwnerSnapshotCostCurve()
        {
            WithTempPaths(paths =>
            {
                long smallest = long.MaxValue, largest = 0;
                foreach (var history in new[] { 10, 100, 1000, 10000 })
                {
                    var session = new ChatSession { Id = "bounded-run", Host = "Excel", DocumentKey = "document",
                        DocumentTitle = "Book", Title = "Run title" };
                    for (var i = 0; i < history; i++)
                        session.Messages.Add(new ChatMessage { Role = "user", Content = new string('x', 256) });
                    var owner = new ConversationRunOwner(ConversationLeaseService.ForOffice(paths));
                    using (owner.Start(session.Id, "run", session))
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var clock = Stopwatch.StartNew();
                        for (var step = 0; step < 12; step++)
                        {
                            owner.Update(session.Id, "run", "tool_result", "Read result");
                            owner.UpdateIdentity(session, "run");
                            AssertEqual(session.Title, owner.Get(session.Id).Title, "current scalar identity");
                        }
                        clock.Stop();
                        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (history > 10)
                        {
                            smallest = Math.Min(smallest, allocated); largest = Math.Max(largest, allocated);
                            Console.WriteLine("run-owner H=" + history + " updates=12 reads=12 allocated=" + allocated +
                                " ms=" + clock.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture));
                        }
                        var detached = owner.Get(session.Id);
                        detached.DocumentKey = "other"; detached.Title = "mutated";
                        AssertEqual("document", owner.Get(session.Id).DocumentKey, "returned identity cannot change scope");
                        session.Title = "Next title";
                        owner.UpdateIdentity(session, "stale-run");
                        AssertEqual("Run title", owner.Get(session.Id).Title, "stale progress cannot replace identity");
                        owner.UpdateIdentity(session, "run");
                        AssertEqual("Next title", owner.Get(session.Id).Title, "exact run updates identity");
                    }
                }
                AssertTrue(largest - smallest < 4096, "live metadata allocation stays independent of historical message count");
            });
        }
        private static void LiveRunReadsCanonicalSessionAndCatalog()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new ChatStore(paths);
                var reads = new CatalogReadStore(ConversationStore(store));
                var owner = new ConversationRunOwner(ConversationLeaseService.ForOffice(paths));
                var service = new ChatSessionService(adapter, reads) {
                    RunStateProvider = owner.Get, ActiveRunsProvider = owner.Active,
                    RunOwnershipProvider = owner.IsConversationReserved };
                var session = service.LoadSession(null);
                using (owner.Start(session.Id, "run", session))
                {
                    reads.ForbidFullLoads = true;
                    AssertEqual(session.Id, service.GetChatSummaries(session.Id).Single().Id, "running transient chat stays visible");
                    session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", Status = "running",
                        KernelState = new AgentRunState(new RunSummary("run", "turn", RunLifecycle.Running,
                            new ToolCounts(), 1, 0, "", null, null), new AgentRunLimits(4, 4)) };
                    session.Title = "Committed title";
                    session.Messages.Add(new ChatMessage { Role = "user", Content = "Committed input" });
                    session.Messages.Add(new ChatMessage { Role = "assistant", RunId = "run", RuntimeProjection = true,
                        ExcludeFromModelContext = true, Activity = new ChatActivity { Kind = "step", Title = "Reading" } });
                    // Kernel persistence stamps its visible activity before committing.
                    RunViewStateProjector.StampCurrentRun(session);
                    store.Save(session); service.NotifySaved(session);
                    session.Title = "Uncommitted title";
                    session.Messages[0].Content = "Uncommitted input";
                    owner.UpdateIdentity(session, "run");
                    var summary = service.GetChatCatalogForOfficeState(out var activeId).Single();
                    AssertEqual("Committed title", summary.Title, "catalog uses the committed header while a run is live");
                    AssertEqual(session.Id, activeId, "catalog preserves selection");
                    AssertEqual(2, summary.MessageCount, "catalog count comes from the committed history");
                    AssertEqual(RunViewLifecycles.Running, summary.RunViewState.Lifecycle, "kernel lifecycle survives canonical catalog read");
                    reads.ForbidFullLoads = false;
                    var selected = service.LoadSession(session.Id);
                    AssertEqual("Committed input", selected.Messages[0].Content, "explicit selection reads canonical messages");
                    AssertEqual("running", selected.LastRun.Status, "live ownership prevents interrupted-run recovery");
                    selected.Messages[0].Content = "Changed returned detail";
                    AssertEqual("Committed input", service.GetActiveSession().Messages[0].Content, "detail mutation cannot leak into another read");
                    adapter.ActivateDocument("forecast-doc");
                    AssertTrue(service.TryLoadCurrentDocumentChat(session.Id) == null, "live run cannot bypass exact document navigation");
                }
            });
        }

        private static void LiveRunStopAddressesExistingInbox()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = NewSession(FakeOfficeAdapter.ForHost("Excel"));
                session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "turn", Status = "running" };
                store.Save(session);
                var events = new InboxFailingEvents(new ChatEventStoreAdapter(store));
                var inbox = new ConversationInboxService(events);
                inbox.Bind(session);
                var owner = new ConversationRunOwner(ConversationLeaseService.ForOffice(paths));
                using (owner.Start(session.Id, "run", session))
                {
                    var run = owner.Get(session.Id);
                    AssertTrue(!new ConversationInboxService(null).PauseActiveRun(run), "Stop cannot replay or create an absent live Inbox");
                    foreach (var target in new[] { "host", "workspace", "document", "chat", "run" })
                    {
                        var wrong = owner.Get(session.Id);
                        if (target == "host") wrong.Host = "Word";
                        if (target == "workspace") wrong.WorkspaceId = "other";
                        if (target == "document") wrong.DocumentKey = "other";
                        if (target == "chat") wrong.ChatId = "other";
                        if (target == "run") wrong.RunId = "other";
                        AssertTrue(!inbox.PauseActiveRun(wrong), "Stop refuses a different " + target);
                    }
                    AssertTrue(!inbox.Snapshot(session).Paused, "refused Stop preserves live queue");
                    AssertTrue(inbox.PauseActiveRun(run), "exact identity pauses the bound Inbox");
                    AssertTrue(inbox.Snapshot(session).Paused, "exact pause takes effect");
                    AssertTrue(new ConversationInboxService(new ChatEventStoreAdapter(new ChatStore(paths))).Snapshot(
                        new ChatStore(paths).Load(session.Id)).Paused, "pause survives reload");
                    // The existing failure contract also applies to metadata-addressed Stop.
                    events.Fail = true;
                    var failed = false;
                    try { inbox.PauseActiveRun(run); } catch (System.IO.IOException) { failed = true; }
                    AssertTrue(failed && inbox.Snapshot(session).Paused, "failed durable pause is reported and scheduling stays stopped");
                }
            });
        }
    }
}
