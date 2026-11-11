using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class HarnessBrowserActionSource : HarnessBrowserReadSource, IExternalBrowserActionSource
        {
            internal int Dispatches, Preparations;
            internal Action BeforeDispatch, AfterDispatch;
            internal Action<ExternalBrowserEffect> OnPresent;
            internal ExternalBrowserEffect Effect = ExternalBrowserEffect.VerifiedChange;
            internal bool Blocked;
            public void BlockActions(ExternalBrowserReadTarget target) { Blocked = true; }
            public void PresentResult(ExternalBrowserReadTarget target, string preparationId, ExternalBrowserEffect effect) { OnPresent?.Invoke(effect); }
            private string _prepared;
            internal ExternalBrowserActionRequest LastRequest;
            public Task PrepareAsync(ExternalBrowserReadTarget target, string revision, string preparationId,
                ExternalBrowserActionRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Preparations++;
                LastRequest = request;
                if (!target.Matches(Target) || revision != ExternalBrowserResourceProvider.Revision(Target, Content))
                    throw ExternalBrowserResourceProvider.Error("Read the page again.", "browser_observation_stale");
                _prepared = preparationId;
                return Task.CompletedTask;
            }
            public Task<ExternalBrowserActionResult> ActAsync(ExternalBrowserReadTarget target, string preparationId,
                Action beforeDispatch, CancellationToken token, bool navigation = false)
            {
                token.ThrowIfCancellationRequested();
                AssertTrue(target.Matches(Target) && preparationId == _prepared, "exact native preparation reaches dispatch");
                BeforeDispatch?.Invoke(); beforeDispatch(); Dispatches++; AfterDispatch?.Invoke();
                if (navigation && Effect == ExternalBrowserEffect.VerifiedChange)
                    Target = new ExternalBrowserReadTarget(target.ChatId, target.SessionId, target.DocumentGeneration + 1);
                return Task.FromResult(new ExternalBrowserActionResult { Effect = Effect, Code = "browser_verified", NavigationTarget = navigation ? Target : null });
            }
        }

        private static void ExternalBrowserActionsUseDeliveredRead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                AssertTrue(!executor.GetControllerTools().Any(tool => ExternalBrowserToolHandler.Owns(tool.Id)), "actions stay hidden without the native pilot");
                var session = NewSession(adapter);
                var source = new HarnessBrowserActionSource { Target = new ExternalBrowserReadTarget(session.Id, "page", 1) };
                executor.ExternalBrowserReadSource = source;
                ToolPreparationResult pendingPreparation = null;
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToArray(),
                    new AppSettings(), ChatModes.Agent, false,
                    (context, prepared) => { pendingPreparation = prepared; return "pending"; });
                var journal = new ResourceMutationJournal(FixturePaths.Value);
                ExternalBrowserEffect? presented = null;
                source.OnPresent = effect => {
                    AssertEqual(0, journal.Unresolved().Count, "presentation follows the published terminal");
                    presented = effect;
                };
                var sequence = 0;
                Func<string, object, IReadOnlyList<ResourceEvidence>, ToolExecutionRecord> execute = (name, arguments, evidence) => {
                    var step = "step-" + (++sequence);
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), name, JsonConvert.SerializeObject(arguments));
                    if (evidence != null) session.Messages.Add(new ChatMessage { Role = "assistant", RunId = "run", ToolCallId = call.Id,
                        AcceptedCallOrigin = new AcceptedToolCallOrigin(step, "attempt", 0), ResourceEvidence = evidence.ToList() });
                    var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", step,
                        DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                    return result.Outcome != ToolExecutionOutcome.AwaitingConfirmation ? result :
                        runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", step,
                            DateTime.UtcNow, true, 1, pendingPreparation.PreparedStateJson), CancellationToken.None).GetAwaiter().GetResult();
                };
                var args = new { target = ExternalBrowserResourceProvider.Target, name = "Name", value = "Test" };
                var read = execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "core.read creates the actual observation evidence");
                var missing = execute(ExternalBrowserToolHandler.Fill, args, null);
                AssertEqual(ToolExecutionOutcome.Error, missing.Outcome, "a successful but undelivered read cannot authorize an action");
                AssertEqual(0, source.Preparations, "omitted evidence does not reach the native preparation");
                source.BeforeDispatch = () => {
                    AssertEqual(MutationAttemptState.Prepared, journal.Unresolved().Single().State, "prepared fact exists before effects");
                    AssertEqual(read.ResourceEvidence.Single().Resource.Revision, journal.Unresolved().Single().ExpectedRevision, "journal pins delivered observation");
                };
                source.AfterDispatch = () => AssertEqual(MutationAttemptState.DispatchMayHaveOccurred,
                    journal.Unresolved().Single().State, "dispatch boundary is durable before native effect");
                var filled = execute(ExternalBrowserToolHandler.Fill, args, read.ResourceEvidence);
                AssertEqual(ToolExecutionOutcome.Ok, filled.Outcome, filled.Message);
                AssertEqual(ToolEffectEvidence.VerifiedChange, filled.Evidence.Effect, "verified native effect reaches runtime");
                AssertEqual(ExternalBrowserEffect.VerifiedChange, presented.Value, "only published success reaches presentation");
                AssertTrue(filled.AuthorityCommitId != null, "existing authority publishes the terminal");
                AssertEqual(0, journal.Unresolved().Count, "terminal closes the attempt");
                source.BeforeDispatch = null; source.AfterDispatch = null;
                read = execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                var navigated = execute(ExternalBrowserToolHandler.Navigate, new { target = args.target, url = "https://example.com/" }, read.ResourceEvidence);
                AssertEqual(ToolExecutionOutcome.Ok, navigated.Outcome, "verified navigation accepts the next generation of the same owned page");
                AssertEqual(2L, source.Target.DocumentGeneration, "navigation intentionally advances the document generation");
                read = execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                AssertEqual(ToolExecutionOutcome.Error, execute(ExternalBrowserToolHandler.Navigate,
                    new { target = args.target, url = "https://example.com/", linkName = "Help" }, read.ResourceEvidence).Outcome,
                    "an explicit URL and observed link cannot compete for authority");
                var followed = execute(ExternalBrowserToolHandler.Navigate, new { target = args.target, linkName = "Help" }, read.ResourceEvidence);
                AssertEqual(ToolExecutionOutcome.Ok, followed.Outcome, "semantic link uses the existing navigation runtime");
                AssertTrue(source.LastRequest.Url == null && source.LastRequest.Name == "Help", "model supplies a name, never an inferred hidden href");
                execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                AssertEqual(ToolExecutionOutcome.Error, execute(ExternalBrowserToolHandler.Fill, args, read.ResourceEvidence).Outcome,
                    "older delivered read cannot replace the current exact native observation");
                AssertEqual(3, source.Dispatches, "stale observation causes no additional effect");
                read = execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                source.AfterDispatch = () => { throw new TimeoutException("Simulated channel loss after dispatch."); };
                var unknown = execute(ExternalBrowserToolHandler.Click, new { target = args.target, name = "Save", expectedText = "Saved" }, read.ResourceEvidence);
                AssertEqual(ToolExecutionOutcome.Unknown, unknown.Outcome, "transport loss after dispatch is never no-effect or success");
                AssertEqual(ToolEffectEvidence.Unknown, unknown.Evidence.Effect, "unknown effect is retained");
                AssertTrue(source.Blocked, "lost terminal evidence blocks further native mutations");
                AssertEqual(ExternalBrowserEffect.Unknown, presented.Value, "channel loss never paints success");
                AssertEqual(0, journal.Unresolved().Count, "unknown is a terminal fact, not a replay request");
                var attempts = source.Dispatches;
                read = execute(ResourceToolCatalog.ReadToolId, new { target = args.target }, null);
                source.AfterDispatch = null;
                adapter.DocumentKeyValue = "another-document"; adapter.RuntimeDocumentKeyValue = "another-runtime";
                AssertEqual(ToolExecutionOutcome.Error, execute(ExternalBrowserToolHandler.Fill, args, read.ResourceEvidence).Outcome, "document mismatch blocks action preparation");
                AssertEqual(attempts, source.Dispatches, "another Office document receives no effect");
            });
        }

        private static void ExternalBrowserWorkspaceAction()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = new ChatSession { Id = "workspace-browser", WorkspaceId = "workspace" };
                var source = new HarnessBrowserActionSource { Target = new ExternalBrowserReadTarget(session.Id, "page", 1) };
                executor.ExternalBrowserReadSource = source;
                var office = new WorkspaceOfficeCapabilityService(adapter, executor, FixturePaths.Value);
                var bound = office.ForTarget(office.CaptureCurrentTarget(), session, new AppSettings(), "run");
                var runtime = new ToolRuntime(RegisterOfficeContributions(bound), "agent", true, true, mutationObserver: bound.MutationObserver);
                var readCall = new ToolCall("read", "core.read", "{}");
                var read = bound.ResourceRead.ExecuteAsync(new ToolHandlerContext(new ToolExecutionContext(readCall,
                    new ToolPolicySnapshot(readCall.Name, "test", ResourceReadToolHandler.Policy), "run", "turn", "read-step", DateTime.UtcNow, false, 1),
                    new Dictionary<string, object> { ["target"] = ExternalBrowserResourceProvider.Target }), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, read.Result.Status, read.Result.Message);
                var call = new ToolCall("fill", ExternalBrowserToolHandler.Fill,
                    JsonConvert.SerializeObject(new { target = ExternalBrowserResourceProvider.Target, name = "Name", value = "Test" }));
                session.Messages.Add(new ChatMessage { Role = "assistant", RunId = "run", ToolCallId = call.Id,
                    AcceptedCallOrigin = new AcceptedToolCallOrigin("step", "attempt", 0), ResourceEvidence = read.ResourceEvidence.ToList() });
                var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "step", DateTime.UtcNow, false, 1),
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, result.Outcome, result.Message);
                AssertEqual(1, source.Dispatches, "bound Workspace shares the same native action owner and runtime evidence");
            });
        }

        private static void ExternalBrowserConfirmationRechecksOwner()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter);
                var source = new HarnessBrowserActionSource { Target = new ExternalBrowserReadTarget(session.Id, "page", 1) };
                executor.ExternalBrowserReadSource = source;
                ToolPreparationResult prepared = null;
                var runtime = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToArray(), new AppSettings(), ChatModes.Agent,
                    false, (context, value) => { prepared = value; return "pending"; });
                var readCall = new ToolCall("read", ResourceToolCatalog.ReadToolId, JsonConvert.SerializeObject(new { target = ExternalBrowserResourceProvider.Target }));
                var read = runtime.ExecuteAsync(new ToolExecutionContext(readCall, runtime.Describe(readCall), "run", "turn", "read-step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                var call = new ToolCall("fill", ExternalBrowserToolHandler.Fill,
                    JsonConvert.SerializeObject(new { target = ExternalBrowserResourceProvider.Target, name = "Name", value = "Test" }));
                session.Messages.Add(new ChatMessage { Role = "assistant", RunId = "run", ToolCallId = call.Id,
                    AcceptedCallOrigin = new AcceptedToolCallOrigin("action-step", "attempt", 0), ResourceEvidence = read.ResourceEvidence.ToList() });
                var pending = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "action-step",
                    DateTime.UtcNow, false, 1), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Outcome, "existing policy confirmation is retained");
                AssertEqual(0, source.Dispatches, "confirmation waiting has no effect or held native operation");
                source.Target = new ExternalBrowserReadTarget(session.Id, "new-page", 1);
                var changed = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call), "run", "turn", "action-step",
                    DateTime.UtcNow, true, 1, prepared.PreparedStateJson), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Error, changed.Outcome, "confirmation cannot retarget a reopened page");
                AssertTrue(!changed.MayHaveDispatched, "changed owner fails before dispatch");
                AssertEqual(0, source.Dispatches, "no native mutation after owner replacement");
                AssertEqual(0, new ResourceMutationJournal(FixturePaths.Value).Unresolved().Count, "rejected preparation leaves no unresolved attempt");
            });
        }
    }
}
