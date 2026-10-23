using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;

internal static class Program
{
    private const string Session = "session_1", Run = "run_1", Step = "run_1:1", CallId = "call_1", Tool = "test.read";
    private static SessionEvent Commit(long sequence, string operation, ChatMessage message)
    {
        return new SessionEvent { SessionId = Session, RunId = Run, TurnId = Run, Sequence = sequence,
            Type = SessionEventTypes.SessionCommit,
            Data = new JObject { ["Operations"] = new JArray(new JObject {
                ["Type"] = operation, ["Data"] = new JObject { ["Value"] = JObject.FromObject(message) }
            }) } };
    }
    private static ChatMessage Call()
    {
        return new ChatMessage { Role = "assistant", RunId = Run, ProtocolMessage = true,
            ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
            ToolResultProtocolVersion = ToolResultWire.CurrentVersion, ToolResultRole = ToolResultRoles.User,
            ToolCallId = CallId, ToolName = Tool,
            AcceptedCallOrigin = new AcceptedToolCallOrigin(Step, "attempt_1", 0),
            Content = ModelProtocolWire.WriteAcceptedCall("Read first.", "", new ConversationToolCall {
                Name = Tool, Arguments = new JObject() }) };
    }
    private static ChatMessage Activity(ToolExecutionOutcome outcome)
    {
        return new ChatMessage { Role = "assistant", RunId = Run, ExcludeFromModelContext = true,
            Activity = new ChatActivity { RunId = Run, StepId = Step, ToolId = Tool, ToolCallId = CallId },
            ExecutionProgress = new ToolExecutionProgress(outcome) };
    }
    private static FinishAdmission Check(ToolExecutionOutcome? outcome, ConversationAssessment assessment,
        string id = CallId)
    {
        var events = new List<SessionEvent> { Commit(1, SessionOperationTypes.ToolCallRecorded, Call()) };
        if (outcome.HasValue && outcome.Value != ToolExecutionOutcome.NotDispatched)
            events.Add(Commit(2, SessionOperationTypes.ToolExecutionStarted, Activity(ToolExecutionOutcome.AwaitingConfirmation)));
        if (outcome.HasValue)
            events.Add(Commit(events.Count + 1, SessionOperationTypes.ToolExecutionFinished, Activity(outcome.Value)));
        return ConversationFinishEvidenceAdmission.Evaluate(events, Session, Run, Run,
            new[] { CallId }, new[] { new ConversationResultClaim("Result", new[] { id }, assessment) }, null);
    }
    private static void Expect(bool condition, string name)
    { if (!condition) throw new Exception("Failed: " + name); Console.WriteLine("PASS " + name); }
    private static void Main()
    {
        Expect(Check(ToolExecutionOutcome.Ok, ConversationAssessment.Satisfied).Accepted, "verified terminal reference");
        Expect(Check(ToolExecutionOutcome.Error, ConversationAssessment.NotSatisfied).Accepted, "failed terminal honest claim");
        Expect(Check(ToolExecutionOutcome.Unknown, ConversationAssessment.Unverified).Accepted, "unknown terminal honest claim");
        Expect(Check(ToolExecutionOutcome.Error, ConversationAssessment.Satisfied).Code == "contradictory_assessment", "failed cannot satisfy");
        Expect(Check(null, ConversationAssessment.Unverified).Code == "nonterminal_evidence", "naked call rejected");
        Expect(Check(ToolExecutionOutcome.AwaitingConfirmation, ConversationAssessment.Unverified).Code == "nonterminal_evidence", "pending rejected");
        var confirmed = new List<SessionEvent> {
            Commit(1, SessionOperationTypes.ToolCallRecorded, Call()),
            Commit(2, SessionOperationTypes.ToolExecutionStarted, Activity(ToolExecutionOutcome.AwaitingConfirmation)),
            Commit(3, SessionOperationTypes.ToolExecutionFinished, Activity(ToolExecutionOutcome.AwaitingConfirmation)),
            Commit(4, SessionOperationTypes.ToolExecutionStarted, Activity(ToolExecutionOutcome.AwaitingConfirmation)),
            Commit(5, SessionOperationTypes.ToolExecutionFinished, Activity(ToolExecutionOutcome.Ok)) };
        Expect(ConversationFinishEvidenceAdmission.Evaluate(confirmed, Session, Run, Run,
            new[] { CallId }, new[] { new ConversationResultClaim("Confirmed result",
                new[] { CallId }, ConversationAssessment.Satisfied) }, null).Accepted,
            "pending then confirmed terminal");
        Expect(Check(ToolExecutionOutcome.Ok, ConversationAssessment.Satisfied, "foreign_id").Code == "unknown_evidence", "forged reference rejected");
        var empty = new[] { new SessionEvent { SessionId = Session, RunId = Run, TurnId = Run, Sequence = 1,
            Type = SessionEventTypes.SessionCommit, Data = new JObject { ["Operations"] = new JArray() } } };
        Expect(ConversationFinishEvidenceAdmission.Evaluate(empty, Session, Run, Run,
            new string[0], new ConversationResultClaim[0], null).Accepted, "direct finish");
        Expect(ConversationFinishEvidenceAdmission.Evaluate(empty, Session, Run, Run,
            new string[0], new[] { new ConversationResultClaim("x", new string[0], ConversationAssessment.Unverified) }, null).Code == "unknown_evidence", "old-run claim rejected");
        PromptSmoke();
        KernelSmoke();
        SteerSmoke();
        RNAssistant.Harness.V8OfficeFinishBridge.Run();
        Console.WriteLine("PASS Office HTTP premature finish rejected; partial claim survives replay");
    }

    private static void PromptSmoke()
    {
        var legacy = new ChatSession { FormatVersion = 7 };
        var refused = false;
        try { ConversationRunPolicy.RequireCurrentSession(legacy); }
        catch (InvalidOperationException) { refused = true; }
        Expect(ChatSession.CurrentFormatVersion == 8 && ChatSession.IsReadableFormat(7) && refused,
            "v7 history remains readable but cannot gain v8 execution authority");
        var composer = typeof(ConversationRunService).Assembly.GetType(
            "RNAssistant.Office.Services.ConversationPromptComposer", true);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (var method in new[] { "BuildAgentInstruction", "BuildPlanInstruction" })
        {
            var custom = new AppSettings { SystemPrompt = "Custom agent guidance.",
                PlanSystemPrompt = "Custom plan guidance.", AgentToolsPrompt = "Custom tools.",
                AgentSkillsPrompt = "Custom skills." };
            var content = (string)composer.GetMethod(method, flags).Invoke(null, new object[] { custom });
            Expect(content.Contains(ModelProtocolWire.ActionGuidance) &&
                content.Contains("Custom tools.") && !content.Contains("conversation-response-v7"),
                method + " pins v8 guidance for editable prompts");
        }
        Expect(AgentPromptDefaults.StructuredResponseContract.Contains(ModelProtocolWire.ActionGuidance) &&
            !AgentPromptDefaults.GeneralInstructions.Contains("conversation-response-v7"),
            "default prompts use v8 guidance");

        var tool = new ToolCatalogEntry { Id = Tool, Description = "Read source.",
            ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}" };
        var catalog = (JArray)composer.GetMethod("BuildTools", flags).Invoke(null,
            new object[] { new[] { tool } });
        var schema = ConversationResponseSchemaBuilder.Build(new[] { tool },
            new ModelProtocolCallContext(new[] { Tool }));
        var source = new ChatMessage { Role = "developer", Content = "Instructions\n\nRUNTIME_CONTEXT:\n" +
            new JObject { ["tools"] = catalog }.ToString(Formatting.None) + "\n\nUSER_REQUEST:\nRead" };
        var messages = new List<ChatMessage> { source };
        var projected = (IReadOnlyList<ChatMessage>)composer.GetMethod("ProjectJsonSchemaMessages", flags)
            .Invoke(null, new object[] { messages, new[] { tool }, schema });
        var context = projected[0].Content.Split(new[] { "RUNTIME_CONTEXT:\n" }, StringSplitOptions.None)[1]
            .Split(new[] { "\n\nUSER_REQUEST" }, StringSplitOptions.None)[0];
        Expect(!object.ReferenceEquals(projected[0], source) &&
            JObject.Parse(context).SelectToken("tools[0].function.parameters") == null &&
            source.Content.Contains("parameters"),
            "v8 schema projects duplicate tool schemas out of prompt");
    }

    private static void KernelSmoke()
    {
        var store = new KernelStore();
        var model = new QueueModel(new[] { Reflection(), Reflection(), (AgentModelResult)null });
        var tools = new NoTools();
        var kernel = new AgentKernel(model, tools, store);
        var stopped = kernel.RunAsync(new AgentRunRequest("run2", "run2", "hello",
            new AgentRunLimits(9, 2)), CancellationToken.None).GetAwaiter().GetResult();
        Expect(stopped.Summary.Lifecycle == RunLifecycle.Cancelled && store.State.ConsecutiveReflections == 2,
            "two reflections persisted before restart");
        var checkpoint = JsonConvert.DeserializeObject<AgentRunState>(JsonConvert.SerializeObject(store.State));
        var restoredMessages = new List<AgentMessage> { AgentMessage.User("hello") };
        foreach (var accepted in stopped.AcceptedMessages.Where(item => item.Kind == AgentMessageKind.Assistant))
        {
            var durable = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(new ChatMessage {
                Role = "assistant", ProtocolMessage = true,
                ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                ResponseModelAttemptId = accepted.SourceModelAttemptId,
                Content = ModelProtocolWire.WriteAcceptedDecision(accepted.Action, accepted.Reason,
                    accepted.Text, accepted.Decision, accepted.Results) }));
            var parsed = ConversationResponseHistoryReader.Read(durable);
            Expect(parsed.Success, "durable reflection history parse");
            restoredMessages.Add(AgentMessage.Assistant(parsed.Response));
        }
        var continuation = AgentRunContinuation.RestoreCheckpoint(checkpoint, store.Revision, restoredMessages);
        var resumedModel = new QueueModel(new[] { Reflection(), Reflection() });
        var resumed = new AgentKernel(resumedModel, tools, store).ContinueAsync(continuation,
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(resumed.Summary.Lifecycle == RunLifecycle.Failed && resumed.Summary.Reason == "reflection_limit" &&
            resumed.AcceptedMessages.Count(item => item.Kind == AgentMessageKind.Assistant) == 2 &&
            resumedModel.Feedback[1].Contains("reflection_limit_feedback"), "reflection bound survives restart");

        var directStore = new KernelStore();
        var finish = new AgentResponseDraft(ConversationResponse.FinishAction, "Direct answer.", "answer",
            results: new ConversationResultClaim[0]);
        var direct = new AgentKernel(new QueueModel(new[] { AgentModelResult.Accepted(finish, "attempt_direct") }),
            tools, directStore).RunAsync(new AgentRunRequest("run3", "run3", "question",
                new AgentRunLimits(3, 2)), CancellationToken.None).GetAwaiter().GetResult();
        Expect(direct.Summary.Lifecycle == RunLifecycle.Completed && direct.Summary.Reason == "model_finish",
            "direct finish accepted");

        var callStore = new KernelStore();
        var callTools = new OneTool();
        var execute = new AgentResponseDraft(ConversationResponse.ExecuteAction, "Read source.", "",
            calls: new[] { new ToolCallDraft("test.read", "{}") });
        var emptyFinish = new AgentResponseDraft(ConversationResponse.FinishAction, "Premature.", "promise",
            results: new ConversationResultClaim[0]);
        var validFinish = new AgentResponseDraft(ConversationResponse.FinishAction, "Have result.", "done",
            results: new[] { new ConversationResultClaim("Read", new[] { "call_1" }, ConversationAssessment.Satisfied) });
        var callModel = new QueueModel(new[] { AgentModelResult.Accepted(execute, "attempt_execute"),
            AgentModelResult.Accepted(emptyFinish, "attempt_empty"), AgentModelResult.Accepted(validFinish, "attempt_finish") });
        var run = new AgentKernel(callModel, callTools, callStore, newCallId: () => "call_1").RunAsync(
            new AgentRunRequest("run4", "run4", "read", new AgentRunLimits(5, 2)),
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(run.Summary.Lifecycle == RunLifecycle.Completed && callTools.Executions == 1 &&
            callModel.Feedback[2].Contains("missing_finish_evidence"), "premature finish rejected without replay");
        var finishMessage = new ChatMessage { Role = "assistant", RunId = run.Summary.RunId,
            ProtocolMessage = true, ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
            ResponseModelAttemptId = "attempt_finish",
            Content = ModelProtocolWire.WriteAcceptedDecision(validFinish.Action, validFinish.Reason,
                validFinish.Message, results: validFinish.Results) };
        var view = RunViewStateProjector.Create(new ChatRunRecord {
            KernelState = callStore.State, StartedUtc = DateTime.UtcNow },
            new[] { finishMessage });
        var restoredView = JsonConvert.DeserializeObject<RunViewState>(JsonConvert.SerializeObject(view));
        Expect(restoredView.ModelResults.Count == 1 &&
            restoredView.ModelResults[0].Assessment == "satisfied" &&
            restoredView.ModelResults[0].Evidence.SequenceEqual(new[] { "call_1" }) &&
            restoredView.GoalVerification == RunGoalVerification.NotAssessed,
            "finish claims project with exact references without verifying the user goal");

        var restartStore = new KernelStore();
        var beforeRestart = new AgentKernel(new QueueModel(new[] { Reflection(), (AgentModelResult)null }),
            new NoTools(), restartStore).RunAsync(new AgentRunRequest("run5", "run5", "inspect",
                new AgentRunLimits(6, 2)), CancellationToken.None).GetAwaiter().GetResult();
        var restored = AgentRunContinuation.RestoreCheckpoint(restartStore.State, restartStore.Revision,
            beforeRestart.AcceptedMessages);
        var afterRestartTools = new OneTool();
        var afterRestart = new AgentKernel(new QueueModel(new[] {
            AgentModelResult.Accepted(execute, "attempt_after_restart"),
            AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.FinishAction,
                "Read complete.", "answer", results: new[] { new ConversationResultClaim("Read",
                    new[] { "call_2" }, ConversationAssessment.Satisfied) }), "attempt_done") }),
            afterRestartTools, restartStore, newCallId: () => "call_2").ContinueAsync(restored,
            CancellationToken.None).GetAwaiter().GetResult();
        Expect(afterRestart.Summary.Lifecycle == RunLifecycle.Completed && afterRestartTools.Executions == 1 &&
            afterRestart.AcceptedMessages.Count(item => item.Kind == AgentMessageKind.Assistant) == 3,
            "reflection execute terminal finish after restart");
    }

    private static AgentModelResult Reflection()
    { return AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.ReflectionAction,
        "Consider next step.", "", decision: "Read before acting."), "attempt_reflection"); }

    private static void SteerSmoke()
    {
        var lateInput = new SteeringInput();
        var lateModel = new QueueModel(new[] {
            AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.ExecuteAction,
                "Read stale source.", "", calls: new[] { new ToolCallDraft(Tool, "{}") }), "attempt_stale"),
            AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.FinishAction,
                "Answer revised request.", "done", results: new ConversationResultClaim[0]), "attempt_revised") });
        lateModel.BeforeSend = (request, token) => {
            if (request.StepId == "run_steer_late:1") lateInput.Add("Do not read the source.");
        };
        var lateTools = new OneTool();
        var late = new AgentKernel(lateModel, lateTools, new KernelStore(), input: lateInput)
            .RunAsync(new AgentRunRequest("run_steer_late", "run_steer_late", "Read source.",
                new AgentRunLimits(4, 2)), CancellationToken.None).GetAwaiter().GetResult();
        Expect(late.Summary.Lifecycle == RunLifecycle.Completed && lateTools.Executions == 0 &&
            lateModel.Requests[1].AcceptedMessages.Any(item => item.Kind == AgentMessageKind.User &&
                item.Text == "Do not read the source."),
            "steer discards stale execute before dispatch");

        var batchInput = new SteeringInput();
        var batchStore = new KernelStore();
        var batchTools = new OneTool { OnExecute = context => batchInput.Add("Use only the first read.") };
        var batchModel = new QueueModel(new[] {
            AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.ExecuteAction,
                "Read two independent sources.", "", calls: new[] {
                    new ToolCallDraft(Tool, "{}"), new ToolCallDraft(Tool, "{\"x\":2}") }), "attempt_batch"),
            AgentModelResult.Accepted(new AgentResponseDraft(ConversationResponse.FinishAction,
                "Report first result.", "done", results: new[] {
                    new ConversationResultClaim("First read is available.",
                        new[] { "call_steer_1" }, ConversationAssessment.Satisfied) }), "attempt_after_steer") });
        var nextCall = 0;
        var batch = new AgentKernel(batchModel, batchTools, batchStore,
            newCallId: () => "call_steer_" + ++nextCall, input: batchInput)
            .RunAsync(new AgentRunRequest("run_steer_batch", "run_steer_batch", "Read both.",
                new AgentRunLimits(4, 3)), CancellationToken.None).GetAwaiter().GetResult();
        var afterSteer = batchModel.Requests[1].AcceptedMessages;
        Expect(batch.Summary.Lifecycle == RunLifecycle.Completed && batchTools.Executions == 1 &&
            batch.Summary.ToolCounts.ReadOk == 1 &&
            batchStore.Events.Any(item => item.Kind == AgentRunEventKind.ToolCompleted &&
                item.Execution.Context.Call.Id == "call_steer_2" &&
                item.Execution.Outcome == ToolExecutionOutcome.NotDispatched) &&
            afterSteer.Any(item => item.Kind == AgentMessageKind.User && item.Text == "Use only the first read.") &&
            afterSteer.Any(item => item.Kind == AgentMessageKind.ToolResult &&
                item.ToolCallId == "call_steer_1") &&
            afterSteer.Any(item => item.Kind == AgentMessageKind.ToolResult &&
                item.ToolCallId == "call_steer_2"),
            "steer closes batch and retains exact terminal IDs for finish");
    }

    private sealed class SteeringInput : IRunInputChannel
    {
        private CancellationTokenSource _signal = new CancellationTokenSource();
        private readonly List<AgentMessage> _pending = new List<AgentMessage>();
        public CancellationToken InterruptToken { get { return _signal.Token; } }
        public bool HasPendingInput { get { return _pending.Count != 0; } }
        internal void Add(string text) { _pending.Add(AgentMessage.User(text)); _signal.Cancel(); }
        public Task<IReadOnlyList<AgentMessage>> TakeInputAsync(CancellationToken token)
        {
            var messages = _pending.ToArray(); _pending.Clear();
            _signal.Dispose(); _signal = new CancellationTokenSource();
            return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
        }
        public bool TryCloseInput() { return !HasPendingInput; }
    }

    private sealed class QueueModel : IModelProtocol
    {
        private readonly Queue<AgentModelResult> _items;
        internal readonly List<string> Feedback = new List<string>();
        internal readonly List<AgentModelRequest> Requests = new List<AgentModelRequest>();
        internal Action<AgentModelRequest, CancellationToken> BeforeSend;
        internal QueueModel(IEnumerable<AgentModelResult> items) { _items = new Queue<AgentModelResult>(items); }
        public Task<AgentModelResult> SendAsync(AgentModelRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (BeforeSend != null) BeforeSend(request, token);
            Feedback.Add(request.CompletionFeedback ?? "");
            var next = _items.Dequeue();
            if (next == null) throw new OperationCanceledException();
            return Task.FromResult(next);
        }
    }
    private sealed class NoTools : IToolRuntime
    {
        public ToolPolicySnapshot Describe(ToolCall call) { throw new Exception("Unexpected tool call"); }
        public Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken token)
        { throw new Exception("Unexpected dispatch"); }
    }
    private sealed class OneTool : IToolRuntime
    {
        internal int Executions;
        internal Action<ToolExecutionContext> OnExecute;
        public ToolPolicySnapshot Describe(ToolCall call)
        { return new ToolPolicySnapshot("test.read", "rev1", false, independentLocalRead: true); }
        public Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken token)
        {
            Executions++;
            if (OnExecute != null) OnExecute(context);
            return Task.FromResult(new ToolExecutionRecord(context, ToolExecutionOutcome.Ok,
                DateTime.UtcNow, "ok", result: ToolResult.Ok("ok", "{}")));
        }
    }
    private sealed class KernelStore : IRunStore
    {
        internal AgentRunState State;
        internal long Revision;
        internal readonly List<AgentRunEvent> Events = new List<AgentRunEvent>();
        public Task<long> AppendAsync(AgentRunEvent fact, long expectedRevision, CancellationToken token)
        {
            if (expectedRevision != Revision) throw new Exception("stale");
            State = AgentRunState.Apply(State, fact); Events.Add(fact); Revision++;
            return Task.FromResult(Revision);
        }
        public Task<FinishAdmission> EvaluateFinishAsync(string runId, string turnId,
            IReadOnlyList<string> acceptedCallIds, IReadOnlyList<ConversationResultClaim> results,
            long expectedRevision, CancellationToken token)
        {
            if (expectedRevision != Revision) throw new Exception("stale finish");
            if (acceptedCallIds.Count == 0) return Task.FromResult(results.Count == 0 ? FinishAdmission.Allow() :
                FinishAdmission.Reject("unknown_evidence", "No calls."));
            if (results.Count == 0 || results.All(item => item.Evidence.Count == 0))
                return Task.FromResult(FinishAdmission.Reject("missing_finish_evidence", "Evidence required."));
            var terminal = Events.Where(item => item.Kind == AgentRunEventKind.ToolCompleted &&
                item.Execution.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
                .Select(item => item.Execution.Context.Call.Id).ToArray();
            return Task.FromResult(results.SelectMany(item => item.Evidence).All(id => terminal.Contains(id))
                ? FinishAdmission.Allow() : FinishAdmission.Reject("nonterminal_evidence", "Not terminal."));
        }
    }
}
