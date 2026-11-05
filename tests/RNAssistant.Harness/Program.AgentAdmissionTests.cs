using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Application.Conversations;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static async Task AgentAdmissionFifoAndCancellation()
        {
            var maximum = 1;
            var scheduler = new ConversationExecutionScheduler(() => maximum);
            var first = await scheduler.EnterAsync(CancellationToken.None);
            using (var cancelled = new CancellationTokenSource())
            {
                var second = scheduler.EnterAsync(cancelled.Token);
                var third = scheduler.EnterAsync(CancellationToken.None);
                cancelled.Cancel();
                await KernelThrowsAsync<OperationCanceledException>(async () => { await second; });
                first.Dispose();
                using (await third)
                {
                    var fourth = scheduler.EnterAsync(CancellationToken.None);
                    AssertTrue(!fourth.IsCompleted && scheduler.ActiveCount == 1, "cancelled waiter cannot steal the next place");
                    maximum = 2;
                    var fifth = scheduler.EnterAsync(CancellationToken.None);
                    var fourthPermit = await fourth;
                    AssertTrue(!fifth.IsCompleted, "a new arrival cannot overtake the oldest waiter when capacity increases");
                    maximum = 1; fourthPermit.Dispose();
                    AssertTrue(!fifth.IsCompleted, "lowering capacity does not admit work while older steps still hold it");
                    maximum = 0;
                    using (await scheduler.EnterAsync(CancellationToken.None))
                    using (await fifth) AssertEqual(3, scheduler.ActiveCount, "zero admits pending and new work without a finite ceiling");
                }
            }
            first.Dispose();
            AssertEqual(0, scheduler.ActiveCount, "permit release is idempotent");
            AssertEqual(0, scheduler.QueuedCount, "cancelled waiters are removed");
            for (var i = 0; i < 20; i++)
            {
                maximum = 1;
                var held = await scheduler.EnterAsync(CancellationToken.None);
                using (var cancel = new CancellationTokenSource())
                {
                    var next = scheduler.EnterAsync(cancel.Token);
                    await Task.WhenAll(Task.Run(() => held.Dispose()), Task.Run(() => cancel.Cancel()));
                    try { (await next).Dispose(); } catch (OperationCanceledException) { }
                }
                AssertEqual(0, scheduler.ActiveCount, "grant/cancel race leaves no permit behind");
                AssertEqual(0, scheduler.QueuedCount, "grant/cancel race leaves no waiter behind");
            }
        }

        private static async Task AgentAdmissionKernelBoundaries()
        {
            foreach (var action in new[] { "cancel", "pause", "queued-store-failure", "admitted-store-failure" })
            {
                var scheduler = new ConversationExecutionScheduler(() => 1);
                var held = await scheduler.EnterAsync(CancellationToken.None);
                var fixture = new KernelFixture(KernelResponse());
                var input = new KernelPauseInput();
                using (var cancel = new CancellationTokenSource())
                {
                    if (action.EndsWith("store-failure", StringComparison.Ordinal))
                        fixture.Store.FailOn = e => e.Kind == (action == "queued-store-failure"
                            ? AgentRunEventKind.ExecutionQueued : AgentRunEventKind.ExecutionAdmitted);
                    var kernel = new AgentKernel(fixture.Model, fixture.Tools, fixture.Store, input: input, admission: scheduler);
                    var run = kernel.RunAsync(new AgentRunRequest("run", "turn", "Work", new AgentRunLimits(2, 2)), cancel.Token);
                    if (action == "cancel") cancel.Cancel();
                    if (action == "pause") input.PauseId = "queued-pause";
                    if (action == "admitted-store-failure") held.Dispose();
                    AssertTrue(await Task.WhenAny(run, Task.Delay(5000)) == run, "queued control/store failure settles without a free slot: " + action);
                    if (action.EndsWith("store-failure", StringComparison.Ordinal))
                        await KernelThrowsAsync<RunStoreException>(async () => { await run; });
                    else
                    {
                        var result = await run;
                        AssertEqual(action == "pause" ? RunLifecycle.Waiting : RunLifecycle.Cancelled, result.Summary.Lifecycle, "queued control keeps kernel lifecycle ownership");
                        AssertEqual(0, result.Summary.IterationsUsed, "queue time does not charge a model iteration");
                        if (action == "pause")
                        {
                            held.Dispose(); input.PauseId = null;
                            var resumed = await kernel.ResumeWaitAsync(result.Continuation, CancellationToken.None);
                            AssertEqual("run", resumed.Summary.RunId, "queued pause resumes the same Run");
                            AssertEqual(1, resumed.Summary.IterationsUsed, "only the actual resumed model step is charged");
                        }
                    }
                    AssertEqual(action == "pause" ? 1 : 0, fixture.Model.Requests.Count, "waiting/cancel/failure does not dispatch a model");
                    held.Dispose();
                    AssertEqual(0, scheduler.ActiveCount, "kernel/store fault releases admitted permits");
                    AssertEqual(0, scheduler.QueuedCount, "kernel/store fault removes pending admissions");
                }
            }
        }

        private static void AgentAdmissionSharedSettings()
        {
            WithTempPaths(paths => {
                var settings = new SettingsService(paths);
                var controls = RNAssistant.Office.Contracts.SettingsControlsDto.From(new AppSettings());
                AssertEqual(4, controls.MaxConcurrentAgentSteps, "default concurrency");
                controls.MaxConcurrentAgentSteps = 1; settings.Save(controls.ApplyTo(new AppSettings()));
                var office = ConversationExecutionScheduler.ForStorage(paths);
                var workspace = ConversationExecutionScheduler.ForStorage(paths);
                using (var first = office.EnterAsync(CancellationToken.None).GetAwaiter().GetResult())
                {
                    var peer = workspace.EnterAsync(CancellationToken.None);
                    AssertTrue(!peer.IsCompleted, "independent Office/Workspace bindings share the runtime cap");
                    controls.MaxConcurrentAgentSteps = 0; settings.Save(controls.ApplyTo(settings.Load()));
                    using (workspace.EnterAsync(CancellationToken.None).GetAwaiter().GetResult())
                    using (peer.GetAwaiter().GetResult()) { }
                }
                AssertEqual(0, settings.Load().MaxConcurrentAgentSteps, "unlimited choice survives typed settings persistence");
            });
        }

        private static async Task AgentAdmissionConfirmation()
        {
            var scheduler = new ConversationExecutionScheduler(() => 1);
            var fixture = new KernelFixture(KernelResponse(KernelCall("confirm")), KernelResponse());
            var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readBack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Tools.OnExecute = async (context, token) => {
                AssertEqual(1, scheduler.ActiveCount, "tool admission includes preparation and read-back");
                if (!context.IsConfirmed) return KernelRecord(context, ToolExecutionOutcome.AwaitingConfirmation);
                dispatched.SetResult(true);
                await readBack.Task;
                return KernelRecord(context);
            };
            var kernel = new AgentKernel(fixture.Model, fixture.Tools, fixture.Store, admission: scheduler);
            var waiting = await kernel.RunAsync(new AgentRunRequest("run", "turn", "Work", new AgentRunLimits(2, 1)), CancellationToken.None);
            AssertEqual(RunLifecycle.AwaitingConfirmation, waiting.Summary.Lifecycle, "confirmation remains user-owned");
            AssertEqual(0, scheduler.ActiveCount, "waiting for confirmation frees admission");
            var held = await scheduler.EnterAsync(CancellationToken.None);
            var resumed = kernel.ResumeAsync("run", waiting.Summary.PendingConfirmation.PendingId, waiting.Continuation, CancellationToken.None);
            AssertTrue(!dispatched.Task.IsCompleted, "confirmed tool queues before dispatch");
            var projection = fixture.Store.Events.Aggregate((AgentRunState)null, AgentRunState.Apply);
            AssertTrue(projection.ExecutionQueued, "existing run.updated projection distinguishes queued work");
            var roundTrip = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentRunState>(Newtonsoft.Json.JsonConvert.SerializeObject(projection));
            AssertTrue(roundTrip.ExecutionQueued, "queue diagnosis survives journal serialization");
            AssertTrue(!roundTrip.Interrupt(false, "Restart").ExecutionQueued, "restart never restores an admission slot");
            held.Dispose();
            AssertTrue(await Task.WhenAny(dispatched.Task, Task.Delay(5000)) == dispatched.Task, "confirmation starts after admission");
            var competitor = scheduler.EnterAsync(CancellationToken.None);
            AssertTrue(!competitor.IsCompleted, "slot remains held until read-back completes");
            readBack.SetResult(true);
            AssertTrue(await Task.WhenAny(competitor, Task.Delay(5000)) == competitor, "confirmed tool releases before the next model step");
            using (await competitor)
                AssertTrue(!resumed.IsCompleted, "next model step joins FIFO behind the peer");
            AssertTrue(await Task.WhenAny(resumed, Task.Delay(5000)) == resumed, "resume cannot deadlock by nesting admission");
            var result = await resumed;
            AssertEqual(RunLifecycle.Completed, result.Summary.Lifecycle, "confirmed Run completes with one slot");
            AssertEqual(2, result.Summary.IterationsUsed, "queue has no iteration cost");
            AssertEqual(1, result.Summary.ToolStepsUsed, "confirmation reuses the accepted tool reservation");
            AssertEqual(waiting.Summary.PendingConfirmation.Call.Id, fixture.Tools.Calls.Last().Call.Id, "confirmed dispatch keeps its call identity");
            AssertTrue(!fixture.Store.Events.Aggregate((AgentRunState)null, AgentRunState.Apply).ExecutionQueued, "terminal Run clears queue diagnosis");
            AssertEqual(0, scheduler.ActiveCount, "terminal Run releases its slot");
        }
    }
}
