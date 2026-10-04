using RNAssistant.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ContextContinuityPreservesFacts()
        {
            var scope = new ResourceAuthorityScopeId("document", "continuity");
            var source = new ResourceRef("rna://vba/continuity/module", "r1");
            var resources = new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(source, 1) }) });
            var authority = new ModelAuthoritySnapshot(resources, "pack1", new SkillCatalogSnapshot(null), null, 1);
            var compiler = new ModelContextCompiler(projection: ModelToolResultProjection.Instance);
            foreach (var role in new[] { "user", "developer", "tool" })
            {
                var failed = ContinuityResult("failed", "common.resources_read", ToolResult.Error(
                    "This target exposes records at $. Omit path or pass that exact value.",
                    "{\"code\":\"RESOURCE_VIEW_PATH_MISMATCH\",\"retryable\":false,\"recovery\":{\"retryPolicy\":\"Replan\"}}"), role);
                var compiled = compiler.Compile(authority, new ChatMessage[0],
                    new[] { ContinuityCall(failed), failed }, null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(compiled.Messages.Last(), out wire, out error), "error stays a result");
                AssertEqual(ToolResultStatus.Error, wire.Result.Status, "error status preserved");
                AssertEqual("RESOURCE_VIEW_PATH_MISMATCH", (string)JObject.Parse(wire.Result.DataJson)["code"], "original code preserved");
                AssertContains(wire.Result.Message, "Omit path", "actionable argument correction preserved");
                AssertEqual("Replan", (string)JObject.Parse(wire.Result.DataJson)["recovery"]["retryPolicy"], "recovery preserved");
            }
            var evidence = new ResourceEvidence("read", scope, source, "source", ResourceCoverage.Whole(), true, 1);
            var claim = new StructuredContextClaim { ClaimId = "claim", Kind = "observation", Text = "STABLE_OBSERVATION",
                SourceRoles = new List<string> { "tool" }, SourceMessageIds = new List<string> { "read" },
                Evidence = new List<ResourceEvidence> { evidence } };
            var checkpoint = new ContextCheckpoint { ThroughMessageId = "not-in-tail", Claims = new List<StructuredContextClaim> { claim } };
            var session = new ChatSession { ActiveContextCheckpointId = checkpoint.Id,
                ContextCheckpoints = new List<ContextCheckpoint> { checkpoint } };
            var otherPack = new ModelAuthoritySnapshot(resources, "pack2", new SkillCatalogSnapshot(null), null, 2);
            var projected = compiler.Compile(otherPack, new ChatMessage[0], ContextCompactionService.BuildActiveWindow(session),
                null, new ToolCatalogEntry[0], new AppSettings(), 10000);
            var text = string.Join("\n", projected.Messages.Select(m => m.Content));
            AssertContains(text, "STABLE_OBSERVATION", "unrelated tool admission cannot erase a resource fact");
            AssertContains(text, "SKILL_CONTEXT_NOTICE", "capability notice survives claim rendering");
            AssertContains(text, "TOOL_SCHEMA_NOTICE", "admission notice survives claim rendering");
            var changed = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Known(new ResourceRef(source.Uri, "r2"), 2) }) }),
                "pack2", new SkillCatalogSnapshot(null), null, 2);
            var stale = compiler.Compile(changed, new ChatMessage[0], ContextCompactionService.BuildActiveWindow(session),
                null, new ToolCatalogEntry[0], new AppSettings(), 10000);
            AssertTrue(!string.Join("\n", stale.Messages.Select(m => m.Content)).Contains(claim.Text), "actual dependency change invalidates claim");
            AssertEqual(1, stale.Receipt.RejectedClaims, "claim rejection is observable");

            var mutation = ContinuityResult("saved", "common.vba_write_module", ToolResult.Ok("Saved and read back.", "{\"changed\":true}"));
            mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged, verification: "read-back");
            var folded = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation },
                null, new ToolCatalogEntry[0], new AppSettings(), 10000);
            AssertEqual(ToolResultStatus.Ok, folded.Messages.Single().CompletedOperation.Status, "typed operation survives folding");
            AssertEqual(ResourceEffectOutcome.VerifiedChanged, folded.Messages.Single().ResourceEffect.Outcome, "verified effect survives folding");
            LlmCompletionDelegate unused = (s, m, o, p, c) => Task.FromResult(new LlmCompletionResult());
            var compactor = new ContextCompactionService(unused);
            var arguments = new object[] { new ChatSession(), folded.Messages, 10000, new AppSettings(), authority, null };
            typeof(ContextCompactionService).GetMethod("BuildCompactionSource", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(compactor, arguments);
            var inputs = (Dictionary<string, StructuredContextClaim>)arguments[5];
            AssertEqual("operation_source", inputs.Values.Single().Kind, "compaction receives execution provenance, not assistant interpretation");
            AssertEqual("tool", inputs.Values.Single().SourceRoles.Single(), "operation retains source role through all projections");
        }

        private static ChatMessage ContinuityResult(string id, string tool, ToolResult result, string role = "tool")
        {
            var message = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = id, ToolId = tool },
                new ToolResultMaterialization(result), int.MaxValue, role);
            message.RunId = "continuity";
            return message;
        }

        private static void ContextContinuityKeepsMutationReceipts()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            foreach (var resource in new[] {
                new { Tool = "common.vba_write_module", Data = "{\"moduleName\":\"NormalizeReport\",\"source\":\"HISTORICAL_BODY\"}",
                    Targets = new[] { "VBA module: NormalizeReport" } },
                new { Tool = "common.html_workspace_apply_patch", Data = "{\"members\":[{\"target\":\"HTML file: index.html\"},{\"target\":\"HTML file: app.js\"}],\"source\":\"HISTORICAL_BODY\"}",
                    Targets = new[] { "HTML file: index.html", "HTML file: app.js" } } })
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var outcome in new[] { ResourceEffectOutcome.VerifiedChanged, ResourceEffectOutcome.VerifiedNoChange,
                ResourceEffectOutcome.FailedNoEffect, ResourceEffectOutcome.UnknownAfterDispatch })
            {
                var status = outcome == ResourceEffectOutcome.FailedNoEffect ? ToolResultStatus.Error :
                    outcome == ResourceEffectOutcome.UnknownAfterDispatch ? ToolResultStatus.Unknown : ToolResultStatus.Ok;
                var mutation = ContinuityResult("mutation", resource.Tool, new ToolResult(status,
                    "Recorded outcome.", resource.Data), role);
                mutation.ResourceEffect = new ResourceEffect("effect", "write", outcome);
                var compiler = new ModelContextCompiler(projection: ModelToolResultProjection.Instance);
                var immediate = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation },
                    null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(status, immediate.Messages.Single(m => m.CompletedOperation != null).CompletedOperation.Status,
                    "next request preserves mutation status for every result role");
                var checkpoint = new ContextCheckpoint { ThroughMessageId = mutation.Id,
                    Claims = new List<StructuredContextClaim> { new StructuredContextClaim {
                        ClaimId = "c", Kind = "interpretation", Text = "Continue.",
                        SourceRoles = new List<string> { "assistant" }, SourceMessageIds = new List<string> { mutation.Id } } } };
                var session = new ChatSession { ActiveContextCheckpointId = checkpoint.Id,
                    ContextCheckpoints = new List<ContextCheckpoint> { checkpoint },
                    Messages = new List<ChatMessage> { ContinuityCall(mutation), mutation } };
                var active = ContextCompactionService.BuildActiveWindow(session);
                AssertTrue(!active.Any(m => m.Id == mutation.Id), "fixture actually compacts the original mutation");
                var restored = ContextWorkingSet.Restore(session, active, authority, new AppSettings(), 6000);
                var next = compiler.Compile(authority, new ChatMessage[0], active.Concat(restored.Messages).ToArray(),
                    null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                var receipt = next.Messages.Single(m => m.CompletedOperation != null);
                AssertEqual("mutation", receipt.CompletedOperation.ToolCallId, "operation correlation survives compaction");
                AssertEqual(status, receipt.CompletedOperation.Status, "compaction cannot upgrade error or unknown to success");
                AssertEqual(outcome, receipt.ResourceEffect.Outcome, "verified, no-op, rejected and unknown effects remain distinct");
                AssertEqual(string.Join(",", resource.Targets), string.Join(",", receipt.CompletedOperation.Targets),
                    "all mutation targets survive even when the original data body is omitted");
                foreach (var target in resource.Targets)
                    AssertContains(receipt.Content, target, "target is delivered in the actual model projection");
                AssertTrue(!receipt.Content.Contains("HISTORICAL_BODY"), "historical receipt does not replay a source body as current");
            }
        }

        private static async Task ContextContinuityBoundsLoops()
        {
            var repeated = new KernelFixture(KernelResponse(KernelCall("read", "{\"a\":1,\"b\":2}")),
                KernelResponse(KernelCall("read", "{\"b\":2,\"a\":1}")),
                KernelResponse(KernelCall("alternative_read")), KernelResponse());
            repeated.Tools.Policies.Add("alternative_read", new ToolPolicySnapshot("alternative_read", "r1",
                new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true, new[] { "agent" })));
            repeated.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                context.Call.Name == "read" ? ToolExecutionOutcome.Error : ToolExecutionOutcome.Ok));
            AssertEqual(RunLifecycle.Completed, (await repeated.RunAsync()).Summary.Lifecycle, "model can select another tool after rejection");
            AssertEqual("read,alternative_read", string.Join(",", repeated.Tools.Calls.Select(c => c.Call.Name)),
                "JSON property order cannot bypass rejection; alternative tool still dispatches");
            AssertContains(repeated.Model.Requests[2].AcceptedMessages.Last().Text, "Choose another tool",
                "rejection reaches the next model request with actionable recovery");

            var busy = new KernelFixture(Enumerable.Range(0, 6).Select(_ => KernelResponse(KernelCall("read"))).ToArray());
            busy.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.Error,
                recovery: new ToolRecoveryContract(ToolFailureKind.BusyNoEffect, ToolRetryPolicy.RetryLater)));
            AssertEqual("repeated_tool_no_progress", (await busy.RunAsync()).Summary.Reason, "only repeated ignored recovery ends the run");
            AssertEqual(3, busy.Tools.Calls.Count, "RetryLater allows three attempts");

            var unchanged = new KernelFixture(Enumerable.Range(0, 6).Select(_ => KernelResponse(KernelCall("read"))).ToArray());
            unchanged.Tools.OnExecute = (context, token) => Task.FromResult(new ToolExecutionRecord(context,
                ToolExecutionOutcome.Ok, context.StartedUtc, result: ToolResult.Ok("loaded", "{\"content\":\"same\"}")));
            AssertEqual("repeated_tool_no_progress", (await unchanged.RunAsync()).Summary.Reason, "successful unchanged reads also stop a loop");
            AssertEqual(3, unchanged.Tools.Calls.Count, "the repeated success is rejected before a fourth dispatch");
            AssertEqual(6, unchanged.Model.Requests.Count, "model receives rejection feedback before stopping");

            var noChange = new KernelFixture(Enumerable.Range(0, 6).Select(_ => KernelResponse(KernelCall())).ToArray());
            noChange.Tools.OnExecute = (context, token) => Task.FromResult(new ToolExecutionRecord(context,
                ToolExecutionOutcome.Ok, context.StartedUtc, result: ToolResult.Ok("unchanged", "{\"operationId\":\"" + context.Call.Id + "\"}"),
                resourceEffect: new ResourceEffect("effect_" + context.Call.Id, context.Call.Name, ResourceEffectOutcome.VerifiedNoChange)));
            AssertEqual("repeated_tool_no_progress", (await noChange.RunAsync()).Summary.Reason,
                "new runtime IDs do not turn repeated verified no-op mutations into progress");
            AssertEqual(3, noChange.Tools.Calls.Count, "no-op mutation is not dispatched again after its repeated result");

            var batch = new KernelFixture(KernelResponse(KernelCall("read")),
                KernelResponse(KernelCall("read"), KernelCall("read"), KernelCall("read")),
                KernelResponse(KernelCall("read", "{\"target\":\"corrected\"}")), KernelResponse());
            batch.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context,
                context.Call.ArgumentsJson == "{}" ? ToolExecutionOutcome.Error : ToolExecutionOutcome.Ok));
            AssertEqual(RunLifecycle.Completed, (await batch.RunAsync()).Summary.Lifecycle,
                "three rejected calls in one response still permit a corrected next step");
            AssertEqual(2, batch.Tools.Calls.Count, "only the original and corrected read dispatch");

            var call = new ToolCall("known_call", "write", "{}");
            var progress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown);
            var durable = Newtonsoft.Json.JsonConvert.DeserializeObject<ChatMessage>(Newtonsoft.Json.JsonConvert.SerializeObject(
                new ChatMessage { ExecutionProgress = progress }));
            var tracker = new AgentProgressTracker();
            tracker.Restore(new[] { AgentMessage.Assistant(new AgentResponse("", new[] { call }, false)),
                AgentMessage.AcceptedToolResult(call.Id, "unknown", "{}", durable.ExecutionProgress) });
            string reason, message; int delay;
            AssertTrue(!tracker.CanDispatch(new ToolCall("new_call", "write", "{}"), out reason, out message, out delay),
                "restored terminal facts prevent replay after confirmation/restart");
            AssertEqual("repeated_unknown_tool_call", reason, "operation identity does not depend on newly allocated call id");
        }

        private static void ContextContinuityDeliversReplanFeedback()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"CURRENT_BODY\"\nEnd Sub";
                Func<string, JObject, string> response = (tool, arguments) => new JObject {
                    ["message"] = "Читаю модуль.", ["action"] = "tool",
                    ["tool_calls"] = new JArray(new JObject { ["name"] = tool, ["arguments"] = arguments })
                }.ToString();
                var invalidRead = response("common.resources_read", new JObject {
                    ["target"] = "VBA module: Module1", ["representation"] = "table" });
                var responses = new Queue<string>(new[] { invalidRead, invalidRead,
                    response("common.resources_find", new JObject { ["scope"] = "vba", ["query"] = "Module1" }),
                    response("common.resources_read", new JObject { ["target"] = "VBA module: Module1", ["representation"] = "source" }),
                    "{\"message\":\"Прочитано.\",\"action\":\"done\",\"tool_calls\":[]}" });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                });
                var session = NewSession(adapter);
                var completed = service.ExecuteAsync(ChatModes.Agent, "Прочитай Module1.", session,
                    NewContext(adapter), new AppSettings(), OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList(),
                    (Action<string, string, ChatActivity>)null, null).GetAwaiter().GetResult();
                AssertEqual(AgentResponseStatuses.Completed, completed.ResponseStatus,
                    "production loop permits discovery and corrected read after a duplicate failure");
                AssertEqual(5, requests.Count, "rejection creates a next model step without protocol repair");
                AssertContains(FlattenSimple(requests[2]), "repeated_tool_failure",
                    "specific rejection code survives materialization and context compilation");
                AssertContains(FlattenSimple(requests[2]), "Choose another tool",
                    "actionable recovery reaches the actual next request");
                AssertContains(FlattenSimple(requests.Last()), "CURRENT_BODY",
                    "the alternative route delivers the source to the model");
                AssertEqual(1, session.Messages.Count(m => m.ExecutionProgress?.Outcome == ToolExecutionOutcome.NotDispatched),
                    "only the duplicate is suppressed; corrected reads are executed");
            });
        }

        private static void ContextContinuityRestoresWorkingSet()
        {
            var scope = new ResourceAuthorityScopeId("catalog", "test");
            var source = new ResourceRef("rna://catalog/skills/test/body", "r1");
            var definition = new SkillDefinition { Id = "test", Name = "test", Enabled = true, BodyMarkdown = "KEEP_COMPLETE_BODY" };
            var skill = ContinuityResult("skill_body", "common.capabilities_read", ToolResult.Ok("loaded",
                "{\"kind\":\"skill\",\"id\":\"test\",\"revision\":\"" + SkillRevision.Compute(definition) + "\",\"loaded\":true,\"complete\":true,\"truncated\":false,\"bodyMarkdown\":\"KEEP_COMPLETE_BODY\"}"));
            skill.ResourceEvidence.Add(new ResourceEvidence("e1", scope, source, "text", ResourceCoverage.Whole(), true, 1, immutable: true));
            var rows = ContinuityResult("rows", "common.resources_read", ToolResult.Ok("Selected rows.",
                "{\"target\":\"Excel range: Sheet!A62:B85\",\"representation\":\"records\",\"complete\":false,\"table\":{\"rows\":[{\"id\":7,\"revision\":\"business value\"}],\"columns\":[{\"key\":\"id\"}]}}"));
            rows.ResourceEvidence.Add(new ResourceEvidence("rows", scope, source, "records",
                new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: 61, end: 84, path: "$", fields: new[] { "id", "revision" }), false, 1));
            var mutation = ContinuityResult("write_done", "common.html_workspace_write_file", ToolResult.Ok("Saved and read back."));
            mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged);
            var checkpoint = new ContextCheckpoint { ThroughMessageId = mutation.Id, Claims = new List<StructuredContextClaim> {
                new StructuredContextClaim { ClaimId = "c", Kind = "interpretation", Text = "Continue.",
                    SourceRoles = new List<string> { "assistant" }, SourceMessageIds = new List<string> { mutation.Id } } } };
            var session = new ChatSession { ActiveContextCheckpointId = checkpoint.Id,
                ContextCheckpoints = new List<ContextCheckpoint> { checkpoint },
                Messages = new List<ChatMessage> { ContinuityCall(skill), skill, ContinuityCall(rows), rows, ContinuityCall(mutation), mutation } };
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(source, 1) }) }),
                "pack", new SkillCatalogSnapshot(new[] { definition }), null, 4);
            var active = ContextCompactionService.BuildActiveWindow(session);
            var restored = ContextWorkingSet.Restore(session, active, authority, new AppSettings(), 4000);
            AssertEqual(2, restored.IncludedBodies, "loaded skill and selected rows survive checkpoint");
            AssertTrue(restored.Messages.Any(m => (m.Content ?? "").Contains("KEEP_COMPLETE_BODY")), "complete skill body is reused");
            AssertTrue(restored.Messages.Any(m => m.CompletedOperation?.ToolCallId == "write_done" &&
                m.CompletedOperation.Status == ToolResultStatus.Ok), "mutation receipt survives independently of LLM summary");
            var compiled = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(authority, new ChatMessage[0], active.Concat(restored.Messages).ToList(),
                null, new ToolCatalogEntry[0], new AppSettings(), 6000,
                retainedBodies: restored.IncludedBodies, evictedBodies: restored.OmittedBodies);
            AssertEqual(2, compiled.Receipt.RetainedBodies, "receipt exposes real retention");
            AssertContains(string.Join("\n", compiled.Messages.Select(m => m.Content)), "KEEP_COMPLETE_BODY", "model receives the still current complete skill after compaction");
            var retainedRows = compiled.Messages.Single(m => m.ToolCallId == "rows" && m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion);
            ToolResultWireReadResult rowWire; string rowError;
            AssertTrue(ToolResultHistoryReader.TryRead(retainedRows, out rowWire, out rowError), "restored row result retains its protocol frame");
            AssertEqual("business value", (string)JObject.Parse(rowWire.Result.DataJson).SelectToken("table.rows[0].revision"), "business field names are unchanged");
            AssertTrue(!retainedRows.ResourceEvidence.Single().Complete, "partial row selection cannot become whole-read evidence");
            var evicted = ContextWorkingSet.Restore(session, active, authority, new AppSettings(), 0);
            AssertEqual(2, evicted.OmittedBodies, "body budget is enforced before hydration");
            AssertTrue(!evicted.Messages.Any(m => (m.Content ?? "").Contains("KEEP_COMPLETE_BODY")), "eviction cannot silently retain bytes");
            AssertContains(string.Join("\n", evicted.Messages.Select(m => m.Content)), "not missing resources", "body absence is distinct from resource absence");
        }

        private static ChatMessage ContinuityCall(ChatMessage result)
        {
            return new ChatMessage { Role = "assistant", RunId = result.RunId, ProtocolMessage = true, ToolCallId = result.ToolCallId,
                ToolName = result.ToolName, ToolCalls = new List<LlmToolCall> {
                    new LlmToolCall { Id = result.ToolCallId, Name = result.ToolName, Type = "function", ArgumentsJson = "{}" } } };
        }
    }
}
