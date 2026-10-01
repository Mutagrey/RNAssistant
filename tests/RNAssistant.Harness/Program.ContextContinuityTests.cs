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
            var compiler = new ModelContextCompiler();
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

        private static async Task ContextContinuityBoundsLoops()
        {
            var repeated = new KernelFixture(KernelResponse(KernelCall("read", "{\"a\":1,\"b\":2}")),
                KernelResponse(KernelCall("read", "{\"b\":2,\"a\":1}")));
            repeated.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.Error));
            AssertEqual("repeated_tool_failure", (await repeated.RunAsync()).Summary.Reason, "JSON property order cannot bypass repetition detection");
            AssertEqual(1, repeated.Tools.Calls.Count, "guard acts before redispatch");

            var busy = new KernelFixture(KernelResponse(KernelCall("read")), KernelResponse(KernelCall("read")),
                KernelResponse(KernelCall("read")), KernelResponse(KernelCall("read")));
            busy.Tools.OnExecute = (context, token) => Task.FromResult(KernelRecord(context, ToolExecutionOutcome.Error,
                recovery: new ToolRecoveryContract(ToolFailureKind.BusyNoEffect, ToolRetryPolicy.RetryLater)));
            AssertEqual("repeated_tool_failure", (await busy.RunAsync()).Summary.Reason, "transient retries are bounded");
            AssertEqual(3, busy.Tools.Calls.Count, "RetryLater allows three attempts");

            var unchanged = new KernelFixture(KernelResponse(KernelCall("read")), KernelResponse(KernelCall("read")),
                KernelResponse(KernelCall("read")));
            unchanged.Tools.OnExecute = (context, token) => Task.FromResult(new ToolExecutionRecord(context,
                ToolExecutionOutcome.Ok, context.StartedUtc, result: ToolResult.Ok("loaded", "{\"content\":\"same\"}")));
            AssertEqual("repeated_tool_no_progress", (await unchanged.RunAsync()).Summary.Reason, "successful unchanged reads also stop a loop");

            var noChange = new KernelFixture(KernelResponse(KernelCall()), KernelResponse(KernelCall()), KernelResponse(KernelCall()));
            noChange.Tools.OnExecute = (context, token) => Task.FromResult(new ToolExecutionRecord(context,
                ToolExecutionOutcome.Ok, context.StartedUtc, result: ToolResult.Ok("unchanged", "{\"operationId\":\"" + context.Call.Id + "\"}"),
                resourceEffect: new ResourceEffect("effect_" + context.Call.Id, context.Call.Name, ResourceEffectOutcome.VerifiedNoChange)));
            AssertEqual("repeated_tool_no_progress", (await noChange.RunAsync()).Summary.Reason,
                "new runtime IDs do not turn repeated verified no-op mutations into progress");

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
            var compiled = new ModelContextCompiler().Compile(authority, new ChatMessage[0], active.Concat(restored.Messages).ToList(),
                null, new ToolCatalogEntry[0], new AppSettings(), 6000, workingSet: restored);
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
