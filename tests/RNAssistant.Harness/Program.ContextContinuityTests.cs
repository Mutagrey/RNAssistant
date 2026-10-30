using RNAssistant.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ContextContinuityKeepsRecentPair()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            var compiler = new ModelContextCompiler();
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var status in new[] { ToolResultStatus.Ok, ToolResultStatus.Error, ToolResultStatus.Unknown })
            {
                var result = ContinuityResult("recent", "core.write", new ToolResult(status,
                    "Exact terminal result.", "{\"code\":\"fixture_result\",\"path\":\"index.html\"}"), role);
                result.ResourceEffect = new ResourceEffect("effect", "create", status == ToolResultStatus.Ok
                    ? ResourceEffectOutcome.VerifiedChanged : status == ToolResultStatus.Error
                    ? ResourceEffectOutcome.FailedNoEffect : ResourceEffectOutcome.UnknownAfterDispatch);
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "recent", Name = "core.write",
                    Arguments = new Dictionary<string, object> { { "target", "index.html" }, { "content", "<p>exact body</p>" } } },
                    "Create the page.", "Create the requested page.", null, role, FixtureCallOrigin("recent"));
                var snapshot = compiler.Compile(authority, new ChatMessage[0], new[] { call, result }, null,
                    new ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(2, snapshot.Messages.Count, "recent mutation remains a call/result pair");
                AssertEqual(role, snapshot.Messages[1].Role, "compiler respects result transport role");
                AssertEqual("recent", snapshot.Messages[1].ToolCallId, "result correlation preserved");
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(snapshot.Messages[1], out wire, out error), "result stays Tool Result v1");
                AssertEqual(status, wire.Result.Status, "outcome is not upgraded or replaced");
                AssertContains(wire.Result.DataJson, "fixture_result", "short receipt stays complete");
                AssertEqual(0, snapshot.Receipt.OperationReceipts, "no unconditional folding");
                var request = JArray.FromObject(new LlmMessageBuilder().Build(snapshot.Messages, new AppSettings()).Messages);
                AssertEqual(role, (string)request[1]["role"], "actual wire uses selected role");
                if (role == "tool")
                {
                    AssertEqual("recent", (string)request[0]["tool_calls"][0]["id"], "native call reaches wire");
                    AssertEqual("recent", (string)request[1]["tool_call_id"], "native result reaches wire");
                    AssertContains((string)request[0]["tool_calls"][0]["function"]["arguments"], "<p>exact body</p>", "native arguments are exact");
                }
                else AssertContains((string)request[0]["content"], "<p>exact body</p>", "v8 accepted arguments are exact");
                AssertEqual(result.Content, snapshot.Messages[1].Content, "short result is unchanged");
                var repaired = new LlmMessageBuilder().Build(compiler.CompileRepair(snapshot,
                    new ChatMessage { Role = "user", Content = "Repair format only." }), new AppSettings()).Messages;
                AssertTrue(JToken.DeepEquals(request, JArray.FromObject(repaired.Take(2))), "format repair reuses the frozen pair in every role");

                var context = new ChatMessage { Role = "developer", Content = "Current required reads: 0/3." };
                var withContext = compiler.Compile(authority, new[] { context }, new[] { call, result }, null,
                    new ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(3, withContext.Messages.Count, "required context is moved, never duplicated");
                AssertEqual(context.Id, withContext.Messages[0].Id, "current requirements precede the complete causal pair");
                AssertEqual(0, withContext.Receipt.Messages.Single(item => item.SourceMessageId == context.Id).MessageIndex,
                    "receipt describes the actual delivered order");
                AssertEqual(ModelContextBudget.EstimateMessagesTokens(withContext.Messages, new AppSettings()),
                    withContext.Receipt.EstimatedTokens, "required context participates in the exact request budget");
                var withContextWire = JArray.FromObject(new LlmMessageBuilder().Build(withContext.Messages, new AppSettings()).Messages);
                AssertTrue(JToken.DeepEquals(request, new JArray(withContextWire.Skip(1))), "moving context preserves tool pairing and result roles");
                var frozenRepair = compiler.CompileRepair(withContext, new ChatMessage { Role = "user", Content = "Repair format only." });
                context.Content = "Changed after compilation.";
                AssertEqual("Current required reads: 0/3.", frozenRepair[0].Content, "repair retains the frozen requirement");
            }
        }

        private static void ContextContinuityFoldsOnlyForBudget()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            foreach (var role in new[] { "user", "developer", "tool" })
            {
                var old = ContinuityResult("old", "core.write", ToolResult.Error("Already exists.", "{\"code\":\"target_exists\"}"), role);
                old.ResourceEffect = new ResourceEffect("effect", "create", ResourceEffectOutcome.FailedNoEffect);
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "old", Name = "core.write",
                    Arguments = new Dictionary<string, object> { { "target", "index.html" }, { "content", new string('x', 20000) } } },
                    "Attempted create.", "Try the requested create.", null, role, FixtureCallOrigin("old"));
                var latest = ContinuityResult("last", "core.patch", ToolResult.Error("Ambiguous patch.", "{\"code\":\"patch_ambiguous\",\"targets\":[]}"), role);
                var latestCall = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "last", Name = "core.patch",
                    Arguments = new Dictionary<string, object> { { "targets", new[] { new { target = "index.html",
                        edits = new[] { new { old = "repeated", @new = "changed" } } } } } } },
                    "Attempted patch.", "Try the requested patch.", null, role, FixtureCallOrigin("last"));
                var facts = new[] { call, old, latestCall, latest };
                var compiler = new ModelContextCompiler();
                var roomy = compiler.Compile(authority, new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), 20000);
                AssertEqual(4, roomy.Messages.Count, "older pairs also remain intact when they fit");
                var tight = compiler.Compile(authority, new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), 1400);
                AssertEqual(4, tight.Messages.Count, "old arguments fold while commentary and the latest pair remain");
                var update = tight.Messages.Single(message => message.Id == call.Id);
                AssertContains(update.Content, "Attempted create.", "the old decision survives argument folding");
                AssertTrue(update.ToolCallId == null && update.ToolCalls.Count == 0 && update.ResourceEvidence.Count == 0,
                    "commentary grants neither execution nor source authority");
                AssertEqual("assistant-update", tight.Receipt.Messages.Single(item => item.SourceMessageId == call.Id).Kind,
                    "inspector distinguishes commentary from operation evidence");
                var receipt = tight.Messages.Single(message => message.CompletedOperation != null).CompletedOperation;
                AssertEqual(ToolResultStatus.Error, receipt.Status, "budget folding preserves failure");
                AssertContains(receipt.DataJson, "target_exists", "rejection detail survives folding");
                AssertTrue(receipt.Targets.Contains("index.html"), "target recovered from accepted semantic arguments when result omits it");
                AssertEqual(role, tight.Messages.Last().Role, "latest pair survives budget folding");
                AssertEqual(latest.Content, tight.Messages.Last().Content, "latest short receipt stays complete");
                AssertEqual(latestCall.Content, tight.Messages[2].Content, "latest call text stays exact");
                AssertTrue(tight.Receipt.EstimatedTokens <= 1400, "compiled request fits the actual budget");
            }
        }

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
                var failed = ContinuityResult("failed", "core.read", ToolResult.Error(
                    "This target exposes records at $. Omit path or pass that exact value.",
                    "{\"code\":\"RESOURCE_VIEW_PATH_MISMATCH\",\"retryable\":false}"), role);
                failed.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error,
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
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

            var mutation = ContinuityResult("saved", "vba.module.write", ToolResult.Ok("Saved and read back.", "{\"changed\":true}"));
            mutation.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.VerifiedChanged, verification: "read-back");
            var folded = compiler.Compile(authority, new ChatMessage[0], new[] { ContinuityCall(mutation), mutation },
                null, new ToolCatalogEntry[0], new AppSettings(), 10000);
            ToolResultWireReadResult terminal; string wireError;
            AssertTrue(ToolResultHistoryReader.TryRead(folded.Messages.Last(), out terminal, out wireError), "recent outcome retains its result transport");
            var operation = ModelContextCompiler.CompleteOperation(folded.Messages.Last(), terminal);
            AssertEqual(ToolResultStatus.Ok, operation.CompletedOperation.Status, "typed operation survives folding");
            AssertEqual(ResourceEffectOutcome.VerifiedChanged, operation.ResourceEffect.Outcome, "verified effect survives folding");
            LlmCompletionDelegate unused = (s, m, o, p, c) => Task.FromResult(new LlmCompletionResult());
            var compactor = new ContextCompactionRuntime(unused);
            foreach (var compactionInput in new IEnumerable<ChatMessage>[] { new[] { operation }, folded.Messages })
            {
                var arguments = new object[] { new ChatSession(), compactionInput, 10000, new AppSettings(), authority, null };
                typeof(ContextCompactionRuntime).GetMethod("BuildCompactionSource", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(compactor, arguments);
                var inputs = (Dictionary<string, StructuredContextClaim>)arguments[5];
                var terminalSource = inputs.Values.Single(item => item.ClaimId == mutation.Id);
                AssertEqual("operation_source", terminalSource.Kind, "both paired and folded results retain execution provenance");
                AssertEqual("tool", terminalSource.SourceRoles.Single(), "operation retains source role through all projections");
                AssertEqual(0, terminalSource.Evidence.Count, "an execution outcome does not expire with its after-state");
            }
        }

        private static void ContextContinuityNarrationSurvivesCompaction()
        {
            foreach (var role in new[] { "user", "developer", "tool" })
            WithTempPaths(paths => {
                const string finding = "Totals differ; inspect formulas before choosing a correction.";
                var chats = new ChatStore(paths);
                var session = chats.Create("Excel", "narration", "Book", "Investigation");
                var scope = new ResourceAuthorityScopeId("document", "narration");
                var source = new ResourceRef("rna://file/narration/notes", "r1");
                var evidence = new ResourceEvidence("read", scope, source, "text", ResourceCoverage.Whole(), true, 1);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(source, 1) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                for (var index = 0; index < 3; index++)
                {
                    var id = "read-" + index;
                    var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id,
                        Name = "core.read", Arguments = new Dictionary<string, object> { { "target", "notes.txt" } } },
                        index == 0 ? finding : "", "Read the requested notes.", null, role, FixtureCallOrigin(id));
                    call.RunId = "continuity";
                    var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolId = call.ToolName, ToolCallId = id },
                        new ToolResultMaterialization(ToolResult.Ok("Read", "{\"target\":\"notes.txt\",\"text\":\"42 and 43\"}"),
                            resourceEvidence: new[] { evidence }), int.MaxValue, role);
                    result.RunId = call.RunId;
                    call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, index + 1);
                    result.ToolCallReference = call.ToolCallReference;
                    result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, index + 1);
                    session.ToolCallReferenceSequence = session.EvidenceReferenceSequence = index + 1;
                    session.Messages.AddRange(new[] { call, result });
                }
                var original = JsonConvert.SerializeObject(session.Messages);
                var compiler = new ModelContextCompiler();
                var snapshot = compiler.Compile(authority, new ChatMessage[0], session.Messages, null,
                    new ToolCatalogEntry[0], new AppSettings(), 4096);
                AssertEqual(2, snapshot.Receipt.Deduplicated, "duplicate results fold without repeating reads");
                var update = snapshot.Messages.Single(item => item.Content.Contains(finding));
                AssertEqual("assistant", update.Role, "retained explanation stays an assistant assessment");
                AssertTrue(update.ToolCallId == "read-0" && update.ResourceEvidence.Count == 0 && update.EvidenceReference == null,
                    "accepted commentary retains correlation without source/effect authority");
                AssertEqual(6, snapshot.Messages.Count, "read folding keeps all pairs without adding separate updates");
                AssertEqual(original, JsonConvert.SerializeObject(session.Messages), "projection never rewrites the durable transcript");
                var again = compiler.Compile(authority, new ChatMessage[0], snapshot.Messages, null,
                    new ToolCatalogEntry[0], new AppSettings(), 4096);
                AssertEqual(1, again.Messages.Count(item => item.Content.Contains(finding)), "recompiling does not duplicate commentary");

                const string reflectionDecision = "Inspect current formulas before choosing a correction.";
                var reflection = new ChatMessage { Role = "assistant", RunId = "continuity",
                    ProtocolMessage = true, ResponseProtocolVersion = ConversationResponse.ProtocolVersion,
                    ResponseModelAttemptId = "attempt-reflection",
                    Content = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.ReflectionAction,
                        "The totals disagree.", "", decision: reflectionDecision) };
                session.Messages.Add(reflection);
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Continue investigating." });
                session.Messages.Add(new ChatMessage { Role = "assistant", Content = "The discrepancy is not resolved yet." });
                string claimKind = "observation";
                var calls = 0;
                LlmCompletionDelegate complete = (settings, messages, options, stream, token) => {
                    calls++;
                    var sources = messages.SelectMany(item => item.Content.Split('\n'))
                        .Where(line => line.StartsWith("{\"sourceId\":", StringComparison.Ordinal)).Select(JObject.Parse).ToList();
                    var input = sources.Single(item => ((string)item["text"] ?? "").Contains(finding));
                    AssertTrue((string)input["role"] == "assistant" || (string)input["kind"] == "interpretation",
                        "folding and recompaction preserve the source kind");
                    var reflected = sources.Single(item => ((string)item["text"] ?? "").Contains(reflectionDecision));
                    AssertTrue((string)reflected["role"] == "assistant" || (string)reflected["kind"] == "interpretation",
                        "accepted reflection remains an assistant interpretation source");
                    var response = new JObject { ["claims"] = new JArray(
                        new JObject { ["kind"] = claimKind, ["text"] = finding,
                            ["sourceIds"] = new JArray(input["sourceId"]) },
                        new JObject { ["kind"] = claimKind, ["text"] = reflectionDecision,
                            ["sourceIds"] = new JArray(reflected["sourceId"]) }) };
                    string schemaError;
                    AssertEqual(claimKind == "interpretation", ToolSchemaSupport.ValidateArguments(response,
                        JObject.Parse(options.ResponseSchemaJson), false, out schemaError), "strict schema rejects upgraded assistant claims");
                    return Task.FromResult(new LlmCompletionResult { Content = response.ToString() });
                };
                var compactor = new ContextCompactionRuntime(complete);
                // This fixture counts explicit compactions, not format repair attempts.
                var settingsForRun = new AppSettings { AgentResponseMode = AgentResponseModes.JsonSchema,
                    MaxAgentFormatRetries = 1 };
                foreach (var invalid in new[] { "observation", "decision" })
                {
                    claimKind = invalid;
                    RuntimeThrows<InvalidOperationException>(() => compactor.EnsureWithinBudgetAsync(session, settingsForRun,
                        null, true, null, CancellationToken.None, authority, new ToolCatalogEntry[0]).GetAwaiter().GetResult());
                    AssertTrue(session.ActiveContextCheckpointId == null, "invalid source promotion publishes no checkpoint");
                }
                claimKind = "interpretation";
                for (var pass = 0; pass < 2; pass++)
                {
                    var checkpoint = compactor.EnsureWithinBudgetAsync(session, settingsForRun, null, true, null,
                        CancellationToken.None, authority, new ToolCatalogEntry[0]).GetAwaiter().GetResult();
                    AssertTrue(checkpoint != null, "completed source boundary permits compaction");
                    var narrationClaim = checkpoint.Claims.Single(claim => claim.Text == finding);
                    var reflectionClaim = checkpoint.Claims.Single(claim => claim.Text == reflectionDecision);
                    AssertEqual("assistant", narrationClaim.SourceRoles.Single(), "assistant provenance remains explicit");
                    AssertEqual(session.Messages[0].Id, narrationClaim.SourceMessageIds.Single(), "original accepted response remains the source");
                    AssertEqual("assistant", reflectionClaim.SourceRoles.Single(), "reflection has assistant provenance, not observation authority");
                    AssertEqual(reflection.Id, reflectionClaim.SourceMessageIds.Single(), "reflection keeps its accepted source after recompaction");
                    chats.Save(session); session = new ChatStore(paths).Load(session.Id);
                    var resumed = compiler.Compile(authority, new ChatMessage[0], ContextCompactionRuntime.BuildActiveWindow(session),
                        null, new ToolCatalogEntry[0], settingsForRun, 4096);
                    AssertContains(string.Join("\n", resumed.Messages.Select(item => item.Content)), finding,
                        "the decision basis survives checkpoint save and restart");
                    AssertContains(string.Join("\n", resumed.Messages.Select(item => item.Content)), reflectionDecision,
                        "accepted reflection decision survives checkpoint save and restart");
                    session.Messages.Add(new ChatMessage { Role = "user", Content = "Continue with that unresolved question." });
                    session.Messages.Add(new ChatMessage { Role = "assistant", Content = "No new source evidence yet." });
                }
                AssertEqual(4, calls, "only explicit compaction invokes the helper; narration adds no model calls");
            });
        }

        private static ChatMessage ContinuityResult(string id, string tool, ToolResult result, string role = "tool")
        {
            var message = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = id, ToolId = tool },
                new ToolResultMaterialization(result), int.MaxValue, role);
            message.RunId = "continuity";
            return message;
        }

        private static void ContextContinuityTracksFailedObservation()
        {
            WithTempPaths(paths =>
            {
                var scope = new ResourceAuthorityScopeId("workspace", "failed-observation");
                var script = new ResourceRef("rna://file/failed-observation/script", "r1");
                var style = new ResourceRef("rna://file/failed-observation/style", "r1");
                var failed = ContinuityResult("browser", "web.verify", ToolResult.Error(
                    "ReferenceError: increment is not defined", "{\"entryPath\":\"index.html\",\"errors\":[\"CLICK_ERROR\"]," +
                    "\"checks\":[{\"id\":\"after-plus\",\"status\":\"Failed\"}]}"), "user");
                failed.ResourceEvidence.Add(new ResourceEvidence("script", scope, script, "text", ResourceCoverage.Whole(), true, 1));
                failed.ResourceEvidence.Add(new ResourceEvidence("style", scope, style, "text", ResourceCoverage.Whole(), true, 1));
                var blobs = new RNAssistant.Core.Storage.ChatBlobStore(paths);
                var full = failed.Content;
                failed.ResultPayload = PayloadRef.FromBlob(blobs.StoreText(full, "application/json"));
                failed.Content = "TOOL_RESULT:\n" + ToolResultWire.Write("browser", "web.verify", ToolResult.Error("Archived diagnostic."));
                var compiler = new ModelContextCompiler(blobs);
                Func<ResourceHeadState, ModelAuthoritySnapshot> authority = head => new ModelAuthoritySnapshot(
                    new ResourceAuthoritySnapshotSet(new[] { new ResourceAuthoritySnapshot(scope, 2, null, 0,
                        new[] { ResourceHeadState.Known(script, 1), head }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var facts = new[] { ContinuityCall(failed), failed };
                var current = compiler.Compile(authority(ResourceHeadState.Known(style, 2)), new ChatMessage[0],
                    facts, null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(current.Messages.Last(), out wire, out error),
                    "current failed observation remains a terminal tool result");
                AssertContains(wire.Result.Message, "ReferenceError", "CAS diagnostic is hydrated before projection");
                AssertEqual(0, current.Receipt.ExcludedSuperseded, "unrelated generation change does not stale an observation");

                var changed = compiler.Compile(authority(ResourceHeadState.Known(new ResourceRef(style.Uri, "r2"), 2)),
                    new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertEqual(2, changed.Messages.Count, "even a stale latest observation keeps its causal pair");
                var receipt = changed.Messages.Last();
                AssertEqual("user", receipt.Role, "stale diagnostic retains the selected result role");
                AssertTrue(ToolResultHistoryReader.TryRead(receipt, out wire, out error), "historical result remains valid wire");
                AssertEqual(ToolResultStatus.Error, wire.Result.Status, "historical failure is not upgraded or replaced");
                AssertContains(wire.Result.Message, "ReferenceError", "original diagnostic survives hydration");
                AssertContains(wire.Result.DataJson, "CLICK_ERROR", "structured error remains available as historical data");
                AssertContains(wire.Result.DataJson, "after-plus", "failed assertion is retained");
                var body = JObject.Parse(wire.Result.DataJson);
                AssertTrue(body["targets"].Values<string>().Contains("index.html"), "historical check keeps its semantic entry");
                AssertEqual("Superseded", (string)body["observation"]["state"], "change to either source invalidates diagnostic currency");
                AssertContains((string)body["observation"]["nextAction"], "completed", "historical error cannot instruct mutation replay");
                AssertTrue(changed.Receipt.ExcludedSuperseded > 0, "receipt records source invalidation");
                AssertTrue(!receipt.Content.Contains("rna://"), "source identities stay runtime-only");
                AssertContains(failed.Content, "Archived diagnostic", "compilation does not mutate durable facts");
                AssertTrue(failed.ResultPayload != null, "historical exact payload remains retained");
                var later = ContinuityResult("later", "core.patch", ToolResult.Error("No dispatch.", "{\"code\":\"no_effect\"}"));
                var older = compiler.Compile(authority(ResourceHeadState.Known(new ResourceRef(style.Uri, "r2"), 2)),
                    new ChatMessage[0], facts.Concat(new[] { ContinuityCall(later), later }).ToArray(), null,
                    new ToolCatalogEntry[0], new AppSettings(), 10000);
                AssertContains(older.Messages.Single(m => m.CompletedOperation != null).CompletedOperation.DataJson,
                    "CLICK_ERROR", "older historical frame also preserves the actual diagnostic");
                var rejected = false;
                try { new ModelContextCompiler().Compile(authority(ResourceHeadState.Known(new ResourceRef(style.Uri, "r2"), 2)),
                    new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), 10000); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("diagnostic result payload"); }
                AssertTrue(rejected, "missing historical diagnostic reader cannot substitute its placeholder");
            });
        }

        private static void ContextContinuityHydratesArchivedDiagnostics()
        {
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var status in new[] { ToolResultStatus.Error, ToolResultStatus.Unknown })
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var chats = new ChatStore(paths);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var data = new JObject { ["path"] = "app.js", ["code"] = "PATCH_CONFLICT", ["field"] = "changes[0]",
                    ["hunkIndex"] = 2, ["diagnostic"] = new string('x', 10000), ["uri"] = "rna://private/internal" };
                var inline = ContinuityResult("diagnostic", "test.patch", new ToolResult(status, "Inspect the failed hunk.", data.ToString()), role);
                inline.ResourceEffect = new ResourceEffect("effect", "patch", status == ToolResultStatus.Unknown
                    ? ResourceEffectOutcome.UnknownAfterDispatch : ResourceEffectOutcome.FailedNoEffect);
                var full = inline.Content;
                AssertTrue(full.Length > 8192, "fixture crosses the ordinary CAS externalization threshold");
                var archived = HistoricalContextProjector.Project(inline);
                archived.ResultPayload = PayloadRef.FromBlob(blobs.StoreText(full, "application/vnd.rnassistant.tool-result+json"));
                var header = new JObject { ["payload_externalized"] = true, ["complete"] = true, ["characters"] = full.Length };
                var json = ToolResultWire.WriteParsed(inline.ToolCallId, inline.ToolName,
                    new ToolResult(status, "Inspect the failed hunk.", header.ToString()), header);
                archived.Content = role == "tool" ? json : "TOOL_RESULT:\n" + json;
                var checkpoint = new ContextCheckpoint { ThroughMessageId = archived.Id,
                    Claims = new List<StructuredContextClaim> { new StructuredContextClaim {
                        ClaimId = "c", Kind = "interpretation", Text = "Continue.", SourceRoles = new List<string> { "assistant" },
                        SourceMessageIds = new List<string> { archived.Id } } } };
                var session = chats.Create("Word", "archived-receipt", "Receipt.docx", "Diagnostic");
                session.Messages.Add(ContinuityCall(archived)); session.Messages.Add(archived);
                session.ActiveContextCheckpointId = checkpoint.Id; session.ContextCheckpoints.Add(checkpoint);
                session.Artifacts.Add(new ChatArtifact { Id = checkpoint.Id, Kind = ChatArtifactKinds.Compaction,
                    MimeType = "application/json", InlineText = JsonConvert.SerializeObject(checkpoint) });
                chats.Save(session);
                session = new ChatStore(paths).Load(session.Host, session.DocumentKey, session.Id);
                var durable = session.Messages.Single(m => m.Id == archived.Id);
                var active = ContextCompactionService.BuildActiveWindow(session);
                AssertTrue(!active.Any(m => m.Id == durable.Id), "diagnostic is beyond the compaction checkpoint");
                var restored = ContextWorkingSet.Restore(session, active, 6000);
                AssertEqual(durable.ResultPayload.Sha256, restored.Operations.Single().Source.ResultPayload.Sha256,
                    "selection retains the exact result provenance");
                foreach (var projection in new[] { new ModelResultProjection(), ModelToolResultProjection.Instance })
                {
                    var compiler = new ModelContextCompiler(blobs, projection);
                    var retained = compiler.Compile(authority, new ChatMessage[0], active,
                        null, new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: restored);
                    var controlSet = ContextWorkingSet.Restore(session, active, 6000,
                        archive: new[] { ContinuityCall(inline), inline });
                    var control = compiler.Compile(authority, new ChatMessage[0], active,
                        null, new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: controlSet);
                    var receipt = retained.Messages.Single(m => m.CompletedOperation != null);
                    AssertEqual(control.Messages.Single(m => m.CompletedOperation != null).Content, receipt.Content,
                        "inline and CAS results produce identical bounded receipts");
                    AssertContains(receipt.CompletedOperation.DataJson, "PATCH_CONFLICT", "diagnostic code survives");
                    AssertContains(receipt.CompletedOperation.DataJson, "changes[0]", "diagnostic field survives");
                    AssertTrue(!receipt.Content.Contains("rna://") && !receipt.Content.Contains(new string('x', 100)),
                        "receipt omits internal authority and the unselected diagnostic body");
                    AssertEqual(1, retained.Receipt.HydratedPayloads, "the compiler reads the selected body once");
                    AssertEqual(durable.ResultPayload.Sha256, retained.Receipt.Messages.Single(m =>
                        m.SourceMessageId == durable.Id).OriginalPayload.Sha256, "inspector retains exact provenance");
                }
                AssertEqual(archived.Content, durable.Content, "compilation never rewrites the durable header");
                AssertEqual(full, blobs.ReadText(durable.ResultPayload.ToBlobReference()), "historical bytes remain unchanged");
                if (status == ToolResultStatus.Unknown)
                    AssertContains(RuntimeThrows<PromptBudgetExceededException>(() => new ModelContextCompiler(blobs).Compile(authority,
                        new ChatMessage[0], active, null, new ToolCatalogEntry[0],
                        new AppSettings(), 1, workingSet: restored)).Message, "required archived operation",
                        "an oversized unresolved outcome fails before any payload can be replaced or omitted");
                System.IO.File.Delete(blobs.PathFor(durable.ResultPayload.Sha256));
                RuntimeThrows<InvalidOperationException>(() => new ModelContextCompiler(blobs).Compile(authority,
                    new ChatMessage[0], active, null, new ToolCatalogEntry[0],
                    new AppSettings(), 12000, workingSet: restored));
                if (status == ToolResultStatus.Error)
                {
                    var omitted = ContextWorkingSet.Restore(session, active, 0);
                    var request = new ModelContextCompiler(blobs).Compile(authority, new ChatMessage[0], active,
                        null, new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: omitted);
                    AssertEqual(0, request.Receipt.HydratedPayloads, "an unselected optional receipt never reads CAS");
                }
            });
        }

        private static void ContextContinuityKeepsArchivedTargets()
        {
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var tool in new[] { "core.write", "resources.move" })
            foreach (var externalized in new[] { false, true })
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var chats = new ChatStore(paths);
                var result = ContinuityResult("target", tool, ToolResult.Error("Already exists.", "{\"code\":\"target_exists\"}"), role);
                result.ResourceEffect = new ResourceEffect("effect", "write", ResourceEffectOutcome.FailedNoEffect);
                var arguments = new Dictionary<string, object> { { "target", "index.html" } };
                arguments.Add(tool == "resources.move" ? "destination" : "content", tool == "resources.move" ? "archive/index.html" : new string('x', 10000));
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = result.ToolCallId, Name = tool, Arguments = arguments }, "Attempted write.",
                    "Try the requested write.", null, role, FixtureCallOrigin("target"));
                call.RunId = result.RunId;
                if (externalized)
                {
                    call.ArgumentPayload = PayloadRef.FromBlob(blobs.StoreText(JsonConvert.SerializeObject(arguments), "application/json"));
                    AcceptedCallPayloadService.Externalize(call, blobs);
                }
                var session = chats.Create("Word", "archived-target", "Target.docx", "Target");
                session.Messages.Add(call); session.Messages.Add(result);
                SetContinuityCheckpoint(session, result);
                chats.Save(session);
                session = new ChatStore(paths).Load(session.Host, session.DocumentKey, session.Id);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var active = ContextCompactionService.BuildActiveWindow(session);
                var set = ContextWorkingSet.Restore(session, active, 6000);
                var compiled = new ModelContextCompiler(blobs).Compile(authority, new ChatMessage[0], active,
                    null, new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: set);
                var receipt = compiled.Messages.Single(item => item.CompletedOperation != null);
                AssertEqual(tool == "resources.move" ? "index.html,archive/index.html" : "index.html",
                    string.Join(",", receipt.CompletedOperation.Targets), "accepted targets survive checkpoint and restart without result path fields");
                AssertEqual(ToolResultStatus.Error, receipt.CompletedOperation.Status, "target restoration never changes the outcome");
                AssertContains(receipt.CompletedOperation.DataJson, "target_exists", "the original failure remains visible");
                AssertTrue(!receipt.Content.Contains(new string('x', 100)), "accepted write bodies never enter the operation receipt");
                if (externalized)
                {
                    AssertEqual(1, compiled.Receipt.HydratedPayloads, "selected accepted call is hydrated once");
                    var storedCall = session.Messages.Single(item => item.Id == call.Id);
                    AssertTrue(storedCall.ToolCalls.Count == 0, "compilation never rewrites the durable accepted-call header");
                    System.IO.File.Delete(blobs.PathFor(storedCall.AcceptedCallPayload.Sha256));
                    RuntimeThrows<InvalidOperationException>(() => new ModelContextCompiler(blobs).Compile(authority,
                        new ChatMessage[0], active, null, new ToolCatalogEntry[0],
                        new AppSettings(), 12000, workingSet: set));
                }
            });
        }

        private static void ContextContinuityBoundsArchivedMaterialization()
        {
            foreach (var externalized in new[] { false, true })
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var session = new ChatSession();
                var count = externalized ? 40 : 100;
                for (var index = 0; index < count; index++)
                {
                    var result = ContinuityResult("large_" + index, "test.failure", ToolResult.Error(new string('m', 2000),
                        new JObject { ["code"] = "conflict", ["details"] = externalized ? new string('x', 10000) : "details" }.ToString()));
                    if (externalized)
                    {
                        result.ResultPayload = PayloadRef.FromBlob(blobs.StoreText(result.Content, "application/vnd.rnassistant.tool-result+json"));
                        result.Content = ToolResultWire.Write(result.ToolCallId, result.ToolName,
                            ToolResult.Error(new string('m', 2000), "{\"payload_externalized\":true}"));
                    }
                    session.Messages.Add(ContinuityCall(result)); session.Messages.Add(result);
                }
                SetContinuityCheckpoint(session, session.Messages.Last());
                var set = ContextWorkingSet.Restore(session, new ChatMessage[0], 120);
                var projected = 0;
                var projection = new ModelResultProjection((tool, result, tools, skills, callableTools) => { projected++; return result; });
                var compiler = new ModelContextCompiler(blobs, projection);
                var compiled = compiler.Compile(authority, new ChatMessage[0], new ChatMessage[0], null, new ToolCatalogEntry[0],
                    new AppSettings(), externalized ? 12000 : 100000, workingSet: set);
                AssertEqual(0, compiled.Receipt.OperationReceipts, "oversized receipts do not fit the optional token budget");
                var budgetOmission = compiled.Receipt.Messages.First(item => item.Role == "tool" &&
                    item.ExclusionKind == ContextExclusionKind.OperationBudget);
                var skippedCall = session.Messages.First(item => item.Role == "assistant" &&
                    item.ToolCallId == budgetOmission.ToolCallId);
                AssertEqual(ContextExclusionKind.OperationBudget,
                    compiled.Receipt.Messages.Single(item => item.SourceMessageId == skippedCall.Id).ExclusionKind.Value,
                    "a skipped archived call and result share their actual budget decision");
                AssertTrue(compiled.Receipt.Messages.Any(item => item.Role == "tool" &&
                    item.ExclusionKind == ContextExclusionKind.OperationSelectionLimit),
                    "bounded candidate admission has a distinct trace reason");
                AssertTrue(projected > 0 && projected <= 64, "rejected optional candidates count toward the work limit");
                if (externalized)
                {
                    AssertTrue(compiled.Receipt.HydratedPayloads > 0 && compiled.Receipt.HydratedPayloads < count,
                        "byte admission stops hydration before every rejected archive body is read");
                    AssertTrue(compiled.Receipt.HydratedBytes <= 256L * 1024, "cumulative optional input bytes remain bounded");
                }
                var unknown = ContinuityResult("unresolved", "test.failure", ToolResult.Unknown("Inspect before any retry.",
                    new JObject { ["code"] = "uncertain", ["details"] = new string('x', 10000) }.ToString()));
                unknown.ResultPayload = PayloadRef.FromBlob(blobs.StoreText(unknown.Content, "application/vnd.rnassistant.tool-result+json"));
                unknown.Content = ToolResultWire.Write(unknown.ToolCallId, unknown.ToolName,
                    ToolResult.Unknown("Inspect before any retry.", "{\"payload_externalized\":true}"));
                session.Messages.Add(ContinuityCall(unknown)); session.Messages.Add(unknown);
                set = ContextWorkingSet.Restore(session, new ChatMessage[0], 0);
                var required = compiler.Compile(authority, new ChatMessage[0], new ChatMessage[0], null, new ToolCatalogEntry[0],
                    new AppSettings(), 12000, workingSet: set);
                AssertEqual("unresolved", required.Messages.Single(item => item.CompletedOperation != null).CompletedOperation.ToolCallId,
                    "unresolved effects remain mandatory with no optional work budget");
                AssertEqual(ContextExclusionKind.OperationReceipt,
                    required.Receipt.Messages.Single(item => item.SourceMessageId == session.Messages[session.Messages.Count - 2].Id)
                        .ExclusionKind.Value, "mandatory archived call is represented by its retained receipt");
                AssertEqual(1, required.Receipt.HydratedPayloads, "zero optional budget reads only the mandatory exact body");
            });
        }

        private static void SetContinuityCheckpoint(ChatSession session, ChatMessage through)
        {
            var checkpoint = new ContextCheckpoint { ThroughMessageId = through.Id, Claims = new List<StructuredContextClaim> {
                new StructuredContextClaim { ClaimId = "c", Kind = "interpretation", Text = "Continue.",
                    SourceRoles = new List<string> { "assistant" }, SourceMessageIds = new List<string> { through.Id } } } };
            session.ContextCheckpoints.Add(checkpoint); session.ActiveContextCheckpointId = checkpoint.Id;
            session.Artifacts.Add(new ChatArtifact { Id = checkpoint.Id, Kind = ChatArtifactKinds.Compaction,
                MimeType = "application/json", InlineText = JsonConvert.SerializeObject(checkpoint) });
        }

        private static void ContextContinuityKeepsMutationReceipts()
        {
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            foreach (var resource in new[] {
                new { Tool = "core.write", Targets = new[] { "VBA module: NormalizeReport" } },
                new { Tool = "core.patch", Targets = new[] { "app.js" } },
                new { Tool = "core.patch",
                    Targets = new[] { "HTML file: index.html", "HTML file: app.js" } } })
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var outcome in new[] { ResourceEffectOutcome.VerifiedChanged, ResourceEffectOutcome.VerifiedNoChange,
                ResourceEffectOutcome.FailedNoEffect, ResourceEffectOutcome.UnknownAfterDispatch })
            {
                var status = outcome == ResourceEffectOutcome.FailedNoEffect ? ToolResultStatus.Error :
                    outcome == ResourceEffectOutcome.UnknownAfterDispatch ? ToolResultStatus.Unknown : ToolResultStatus.Ok;
                var patchStatus = outcome == ResourceEffectOutcome.VerifiedChanged ? ResourceTargetStatus.Changed :
                    outcome == ResourceEffectOutcome.VerifiedNoChange ? ResourceTargetStatus.Unchanged :
                    outcome == ResourceEffectOutcome.FailedNoEffect ? ResourceTargetStatus.Rejected : ResourceTargetStatus.Unknown;
                var data = resource.Tool == "core.patch" ? JsonConvert.SerializeObject(new PatchBatchResult {
                    Phase = "execution", WriteStarted = status != ToolResultStatus.Error,
                    Targets = resource.Targets.Select(target => new PatchTargetResult {
                        Target = target, Status = patchStatus, WriteStarted = status != ToolResultStatus.Error }).ToList() }) :
                    JsonConvert.SerializeObject(new { target = resource.Targets[0], source = "HISTORICAL_BODY" });
                var mutation = ContinuityResult("mutation", resource.Tool, new ToolResult(status,
                    "Recorded outcome.", data), role);
                mutation.ResourceEffect = new ResourceEffect("effect", "write", outcome);
                var arguments = resource.Tool == "core.patch" ? new Dictionary<string, object> {
                    { "targets", PatchTargets(resource.Targets.Select(target => PatchSection(target, LineHunk("old", "new"))).ToArray()) } } :
                    new Dictionary<string, object> { { "target", resource.Targets[0] }, { "content", "HISTORICAL_BODY" } };
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = mutation.ToolCallId, Name = resource.Tool, Arguments = arguments },
                    "", "Edit the requested source.", null, role, FixtureCallOrigin(mutation.ToolCallId));
                call.RunId = mutation.RunId;
                var compiler = new ModelContextCompiler(projection: ModelToolResultProjection.Instance);
                var immediate = compiler.Compile(authority, new ChatMessage[0], new[] { call, mutation },
                    null, new ToolCatalogEntry[0], new AppSettings(), 10000);
                ToolResultWireReadResult wire; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(immediate.Messages.Last(), out wire, out error), "recent receipt remains a result");
                AssertEqual(status, wire.Result.Status,
                    "next request preserves mutation status for every result role");
                var checkpoint = new ContextCheckpoint { ThroughMessageId = mutation.Id,
                    Claims = new List<StructuredContextClaim> { new StructuredContextClaim {
                        ClaimId = "c", Kind = "interpretation", Text = "Continue.",
                        SourceRoles = new List<string> { "assistant" }, SourceMessageIds = new List<string> { mutation.Id } } } };
                var session = new ChatSession { ActiveContextCheckpointId = checkpoint.Id,
                    ContextCheckpoints = new List<ContextCheckpoint> { checkpoint },
                    Messages = new List<ChatMessage> { call, mutation } };
                var active = ContextCompactionService.BuildActiveWindow(session);
                AssertTrue(!active.Any(m => m.Id == mutation.Id), "fixture actually compacts the original mutation");
                var restored = ContextWorkingSet.Restore(session, active, 6000);
                var next = compiler.Compile(authority, new ChatMessage[0], active,
                    null, new ToolCatalogEntry[0], new AppSettings(), 10000, workingSet: restored);
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
            tracker.Restore(new[] { AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                    "Track the uncertain call.", "", new[] { call })),
                AgentMessage.AcceptedToolResult(call.Id, "unknown", "{}", durable.ExecutionProgress) });
            string reason, message; int delay;
            AssertTrue(!tracker.CanDispatch(new ToolCall("new_call", "write", "{}"), out reason, out message, out delay),
                "restored terminal facts prevent replay after confirmation/restart");
            AssertEqual("repeated_unknown_tool_call", reason, "operation identity does not depend on newly allocated call id");

            var target = new ResourceRef(ResourceUri.Create("workspace", "fixture", "file", "a.txt")).Identity;
            var other = new ResourceRef(ResourceUri.Create("workspace", "fixture", "file", "b.txt")).Identity;
            foreach (var recovery in new[] {
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan),
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan, target, "text", "a.txt"),
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired, target, "text", "a.txt") })
            {
                var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<ToolExecutionProgress>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(new ToolExecutionProgress(ToolExecutionOutcome.Error, recovery)));
                var history = new[] { AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                        "Track the failed call.", "try", new[] { call })),
                    AgentMessage.AcceptedToolResult(call.Id, "state refused", "{}", restored) };
                tracker = new AgentProgressTracker();
                tracker.Restore(history);
                tracker.Observe(new ToolCall("read", "read", "{}"), new ToolExecutionProgress(ToolExecutionOutcome.Ok),
                    "fresh read", new[] { new ResourceEvidence("read", new ResourceAuthorityScopeId("workspace", "fixture"),
                        new ResourceRef(target.Uri, "r2"), "text", ResourceCoverage.Whole(), true, 2) }, null);
                AssertEqual(recovery.RetryPolicy == ToolRetryPolicy.RefreshRequired,
                    tracker.CanDispatch(call, out reason, out message, out delay), "only refresh recovery accepts a fresh read");

                tracker = new AgentProgressTracker();
                tracker.Restore(history);
                foreach (var outcome in new[] { ResourceEffectOutcome.VerifiedNoChange, ResourceEffectOutcome.UnknownAfterDispatch })
                {
                    tracker.Observe(new ToolCall("uncertain", "repair", "{}"),
                        new ToolExecutionProgress(outcome == ResourceEffectOutcome.UnknownAfterDispatch
                            ? ToolExecutionOutcome.Unknown : ToolExecutionOutcome.Ok), "not repaired", null,
                        new ResourceEffect("effect", "repair", outcome, new[] { new ResourceImpact(target, ResourceImpactRelation.Exact) }));
                    AssertTrue(!tracker.CanDispatch(call, out reason, out message, out delay), "no-op or uncertain effects do not release failures");
                }
                foreach (var changed in new[] { other, target })
                {
                    var change = new ToolCall("change", "repair", "{\"changed\":true}");
                    var effect = new ResourceEffect("effect", "repair", ResourceEffectOutcome.VerifiedChanged,
                        new[] { new ResourceImpact(changed, ResourceImpactRelation.ContainerMembership) });
                    tracker.Observe(change, new ToolExecutionProgress(ToolExecutionOutcome.Ok), "changed", null, effect);
                    var allowed = recovery.ResourceIdentity != null && changed.Equals(target);
                    AssertEqual(allowed,
                        tracker.CanDispatch(call, out reason, out message, out delay),
                        "only a verified change of the declared dependency releases the failed call");
                    var replay = new AgentProgressTracker();
                    replay.Restore(history.Concat(new[] { AgentMessage.Assistant(new AgentResponse(
                            ConversationResponse.ExecuteAction, "Track the repair.", "repair", new[] { change })),
                        AgentMessage.AcceptedToolResult(change.Id, "changed", "{}", new ToolExecutionProgress(ToolExecutionOutcome.Ok), effect: effect) }));
                    AssertEqual(allowed, replay.CanDispatch(call, out reason, out message, out delay),
                        "restoring canonical recovery facts retains the same retry decision");
                }
                AssertTrue(!restored.Recovery.ModelProjection().ToString().Contains("rna://"), "exact recovery identity stays runtime-only");
            }
        }

        private static void ContextContinuityChangeRecovery()
        {
            var first = new ResourceRef(ResourceUri.Create("workspace", "fixture", "file", "index.html"), "r1");
            var script = new ResourceRef(ResourceUri.Create("workspace", "fixture", "file", "app.js"), "r1");
            var changed = new ResourceRef(script.Uri, "r2");
            var other = new ResourceRef(ResourceUri.Create("workspace", "fixture", "file", "other.txt"), "r2");
            var recovery = new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.ChangeRequired,
                semanticTarget: "index.html", changeDependencies: new[] { first, script });
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<ToolExecutionProgress>(
                Newtonsoft.Json.JsonConvert.SerializeObject(new ToolExecutionProgress(ToolExecutionOutcome.Error, recovery)));
            AssertEqual(2, restored.Recovery.ChangeDependencies.Count, "exact dependencies survive canonical progress serialization");
            AssertEqual(script.Revision, restored.Recovery.ChangeDependencies[1].Revision, "failed revision survives restart");
            var model = restored.Recovery.ModelProjection().ToString();
            AssertContains(model, "ChangeRequired", "model receives a repair condition");
            AssertTrue(!model.Contains("rna://") && !model.Contains("ChangeDependencies"), "dependency authority remains runtime-only");
            var failed = new ToolCall("verify", "read", "{}");
            var history = new[] { AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                    "Track the failed verification.", "verify", new[] { failed })),
                AgentMessage.AcceptedToolResult(failed.Id, "snapshot failed", "{}", restored) };
            Func<AgentProgressTracker> tracker = () => { var value = new AgentProgressTracker(); value.Restore(history); return value; };
            string reason, message; int delay;
            var evidence = new ResourceEvidence("read", new ResourceAuthorityScopeId("workspace", "fixture"),
                changed, "text", ResourceCoverage.Whole(), true, 2);
            var observed = tracker();
            observed.Observe(new ToolCall("read-source", "read_source", "{}"), new ToolExecutionProgress(ToolExecutionOutcome.Ok),
                "source read", new[] { evidence }, null);
            AssertTrue(!observed.CanDispatch(failed, out reason, out message, out delay), "even a new complete read is not a verified repair");
            var effects = new[]
            {
                new ResourceEffect("other", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(other.Identity, ResourceImpactRelation.Exact, before: new ResourceRef(other.Uri, "r1"), after: other) }),
                new ResourceEffect("no-change", "write", ResourceEffectOutcome.VerifiedNoChange,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: script, after: script) }),
                new ResourceEffect("unknown", "write", ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: script, after: changed) }),
                new ResourceEffect("same-revision", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: script, after: script) }),
                new ResourceEffect("old-result", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: changed, after: script) }),
                new ResourceEffect("indirect", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.DependsOn, before: script, after: changed) }),
                new ResourceEffect("partial", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact,
                        new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 2), script, changed) }),
                new ResourceEffect("missing-before", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, after: changed) }),
                new ResourceEffect("wrong-identity", "write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: script, after: other) })
            };
            foreach (var effect in effects)
            {
                var guarded = tracker();
                guarded.Observe(new ToolCall("repair", "write", "{}"), new ToolExecutionProgress(ToolExecutionOutcome.Ok),
                    "unrelated or unproven repair", null, effect);
                AssertTrue(!guarded.CanDispatch(failed, out reason, out message, out delay), "rejected recovery: " + effect.EffectId);
                AssertEqual("repeated_tool_failure", reason, "failed observation remains guarded");
            }
            var repair = new ResourceEffect("repair", "write", ResourceEffectOutcome.VerifiedChanged,
                new[] { new ResourceImpact(script.Identity, ResourceImpactRelation.Exact, before: script, after: changed) });
            var acceptedRepair = AgentMessage.AcceptedToolResult("repair", "verified", "{}",
                new ToolExecutionProgress(ToolExecutionOutcome.Ok), effect: repair);
            var continued = new AgentProgressTracker();
            continued.Restore(history.Concat(new[] { AgentMessage.Assistant(new AgentResponse(
                ConversationResponse.ExecuteAction, "Track the repair.", "repair",
                new[] { new ToolCall("repair", "write", "{}") })), acceptedRepair }));
            AssertTrue(continued.CanDispatch(failed, out reason, out message, out delay), "verified dependency repair permits explicit retry after restore");
            var unknown = new AgentProgressTracker();
            unknown.Observe(failed, new ToolExecutionProgress(ToolExecutionOutcome.Unknown), "unknown", null, null);
            unknown.Observe(new ToolCall("repair", "write", "{}"), new ToolExecutionProgress(ToolExecutionOutcome.Ok), "repair", null, repair);
            AssertTrue(!unknown.CanDispatch(failed, out reason, out message, out delay), "a dependency repair cannot replay an uncertain call");
            AssertEqual("repeated_unknown_tool_call", reason, "unknown protection is independent of recovery");
        }

        private static void ContextContinuityDeliversReplanFeedback()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"CURRENT_BODY\"\nEnd Sub";
                Func<string, JObject, string> response = (tool, arguments) => new JObject {
                    ["action"] = ConversationResponse.ExecuteAction,
                    ["reason"] = "Read the requested module or discover its target.",
                    ["tool_calls"] = new JArray(new JObject { ["name"] = tool, ["arguments"] = arguments }),
                    ["message"] = "Читаю модуль."
                }.ToString();
                var invalidRead = response("core.read", new JObject {
                    ["target"] = "VBA module: Module1", ["representation"] = "table" });
                var responses = new Queue<string>(new[] { invalidRead, invalidRead,
                    response("core.find", new JObject { ["scope"] = "vba", ["query"] = "Module1" }),
                    response("core.read", new JObject { ["target"] = "VBA module: Module1", ["representation"] = "source" }) });
                var requests = new List<IReadOnlyList<ChatMessage>>();
                ChatSession session = null;
                var service = CreateConversationRunService(adapter, executor, (settings, messages, options, stream, token) =>
                {
                    requests.Add(messages.ToList());
                    if (responses.Count > 0)
                        return Task.FromResult(new LlmCompletionResult { Content = responses.Dequeue() });
                    var read = session.Messages.Last(item => item.ToolName == "core.read" &&
                        item.ExecutionProgress?.Outcome == ToolExecutionOutcome.Ok);
                    var finish = ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction,
                        "The corrected read returned the current module source.", "Прочитано.",
                        results: new[] { new ConversationResultClaim("Module source read.",
                            new[] { read.EvidenceReference.Alias }, ConversationAssessment.Satisfied) }, outcome: ConversationOutcome.Completed);
                    return Task.FromResult(new LlmCompletionResult { Content = finish });
                });
                session = NewSession(adapter);
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
                var deliveredRead = requests.Last().Where(message => message.Content != null &&
                        message.Content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal))
                    .Select(message => JObject.Parse(message.Content.Substring("TOOL_RESULT:\n".Length)))
                    .Last(root => (string)root["name"] == "core.read" && (string)root["status"] == "ok");
                var canonicalRead = session.Messages.Last(message => message.ToolName == "core.read" &&
                    message.ExecutionProgress?.Outcome == ToolExecutionOutcome.Ok);
                AssertEqual(canonicalRead.ResourceModelReferences.Single().Alias, (string)deliveredRead["data"]?["ref"],
                    "actual next model request binds the read body to its event-backed resource reference");
                AssertTrue(deliveredRead["data"]?["coverage"] != null && deliveredRead["resource_refs"] == null,
                    "model receives the slice beside its label, without a loose resource list");
                AssertEqual(1, session.Messages.Where(m => m.ExecutionProgress?.Outcome == ToolExecutionOutcome.NotDispatched)
                    .Select(m => m.ToolCallId ?? m.Activity?.ToolCallId).Distinct(StringComparer.Ordinal).Count(),
                    "only one accepted duplicate is suppressed; corrected reads are executed");
            });
        }

        private static void ContextContinuitySelectsBodiesOnce()
        {
            WithTempPaths(paths =>
            {
                var blobs = new ChatBlobStore(paths);
                var body = "CURRENT_SOURCE_" + new string('Ж', 1500);
                var payload = PayloadRef.FromBlob(blobs.StoreText(body, "text/plain"));
                var scope = new ResourceAuthorityScopeId("document", "review");
                var exact = new ResourceRef("rna://vba/review/module", "r1");
                var observation = new ResourceEvidence("source", scope, exact, "source", ResourceCoverage.Whole(), true, 1, payload);
                var result = ContinuityResult("archived_source", "core.read", ToolResult.Ok("Read.",
                    "{\"target\":\"VBA module: Review\",\"representation\":\"source\",\"complete\":true}"));
                result.ResourceEvidence.Add(observation);
                result.ResourceModelReferences.Add(new ModelResourceReference(scope, exact,
                    ModelReference.Create(ModelReferenceKind.Resource, 1), 1));
                var session = new ChatSession { Messages = new List<ChatMessage> { ContinuityCall(result), result } };
                SetContinuityCheckpoint(session, result);
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(exact, 1) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var compiler = new ModelContextCompiler(blobs);
                var set = ContextWorkingSet.Restore(session, new ChatMessage[0], 1000);
                var restored = compiler.Compile(authority, new ChatMessage[0], new ChatMessage[0], null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: set);
                AssertEqual(1, restored.Receipt.RetainedBodies, "body admission uses projected token cost, not a second byte-as-character estimate");
                AssertEqual(1, restored.Receipt.HydratedPayloads, "one selected exact body is hydrated once");
                AssertContains(string.Join("\n", restored.Messages.Select(item => item.Content)), body, "the complete current source survives compaction");
                AssertEqual("source", restored.DeliveredObservations.Single().EvidenceId, "synthetic archived body retains delivery authority");
                var restoredView = ModelReferenceProjection.Project(restored.Messages.Where(message => message.SyntheticResourceObservation).ToArray()).Single();
                var restoredHeader = JObject.Parse(restoredView.Content.Split('\n')[1]);
                AssertEqual("R1@v1", (string)restoredHeader["ref"], "compiler binds the hydrated CAS view to its retained reference");
                AssertTrue(restoredView.Content.EndsWith(body, StringComparison.Ordinal), "reference projection preserves the hydrated CAS bytes");
                var write = ContinuityResult("large_write", "core.write", ToolResult.Ok("Written.", "{\"target\":\"other.txt\"}"));
                write.ResourceEffect = new ResourceEffect("fx", "core.write", ResourceEffectOutcome.VerifiedChanged);
                var writeCall = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "large_write", Name = "core.write",
                    Arguments = new Dictionary<string, object> { ["target"] = "other.txt", ["content"] = new string('x', 12000) } },
                    "Saved another file.", "Save the requested file.", null, "tool", FixtureCallOrigin("large_write"));
                var latest = ContinuityResult("latest", "fixture.read", ToolResult.Ok("Small result.", "{}"));
                var withLargeFrame = new[] { writeCall, write, ContinuityCall(latest), latest };
                var enoughRoom = compiler.Compile(authority, new ChatMessage[0], withLargeFrame, null,
                    new ToolCatalogEntry[0], new AppSettings(), 6000,
                    workingSet: ContextWorkingSet.Restore(session, withLargeFrame, 5000));
                AssertTrue(enoughRoom.Messages.SelectMany(item => item.ToolCalls).Any(item =>
                    item.Id == "large_write" && item.ArgumentsJson.Contains(new string('x', 12000))),
                    "actual fitting source does not fold history merely to fund an unused reservation");
                var funded = compiler.Compile(authority, new ChatMessage[0], withLargeFrame, null,
                    new ToolCatalogEntry[0], new AppSettings(), 2500,
                    workingSet: ContextWorkingSet.Restore(session, withLargeFrame, 1000));
                AssertEqual(1, funded.Receipt.RetainedBodies, "folded old write funds archived source before admission");
                AssertContains(string.Join("\n", funded.Messages.Select(item => item.Content)), body, "freed budget delivers exact source");
                restored.Messages.Single(item => item.SyntheticResourceObservation).ResourceEvidence.Clear();
                AssertEqual(1, restored.DeliveredObservations.Count, "delivery is detached from mutable message projections");
                foreach (var diagnostic in new[] { ToolResult.Ok("Verified.", "{\"checks\":[]}"),
                    ToolResult.Error("Verification failed.", "{\"code\":\"check_failed\"}") })
                {
                    var verification = ContinuityResult("verify", "web.verify", diagnostic);
                    verification.ResourceEvidence.Add(observation);
                    var facts = new[] { ContinuityCall(verification), verification };
                    var compiled = compiler.Compile(authority, new ChatMessage[0], facts, null,
                        new ToolCatalogEntry[0], new AppSettings(), 12000,
                        workingSet: ContextWorkingSet.Restore(session, facts, 1000));
                    AssertContains(string.Join("\n", compiled.Messages.Select(item => item.Content)), body,
                        "diagnostic input evidence cannot suppress the archived source body");
                    AssertEqual(1, compiled.Receipt.RetainedBodies, "source body restored beside diagnostics");
                    AssertEqual(1, compiled.DeliveredObservations.Count, "delivered source authorizes reuse");
                    var diagnosticsOnly = compiler.Compile(authority, new ChatMessage[0], facts, null,
                        new ToolCatalogEntry[0], new AppSettings(), 12000);
                    AssertEqual(0, diagnosticsOnly.DeliveredObservations.Count, "diagnostics alone never authorize a source read");
                }
                var missingRead = ContinuityResult("missing_active", "core.read", ToolResult.Ok("Read.",
                    "{\"target\":\"VBA module: Review\",\"representation\":\"source\",\"complete\":true}"));
                missingRead.ResourceEvidence.Add(observation);
                missingRead.ResultPayload = PayloadRef.FromBlob(blobs.StoreText(missingRead.Content, "application/json"));
                System.IO.File.Delete(blobs.PathFor(missingRead.ResultPayload.Sha256));
                var missingFacts = new[] { ContinuityCall(missingRead), missingRead };
                var recovered = compiler.Compile(authority, new ChatMessage[0], missingFacts, null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000,
                    workingSet: ContextWorkingSet.Restore(session, missingFacts, 1000));
                AssertEqual(1, recovered.Receipt.RetainedBodies, "unavailable active result cannot suppress a usable archive body");
                AssertEqual(1, recovered.DeliveredObservations.Count, "only the hydrated archive body grants authority");
                var staleAuthority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] {
                        ResourceHeadState.Known(new ResourceRef(exact.Uri, "r2"), 2) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var stale = compiler.Compile(staleAuthority, new ChatMessage[0], new ChatMessage[0], null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: set);
                AssertEqual(0, stale.DeliveredObservations.Count, "stale archive evidence never authorizes a write");
                AssertEqual(ContextExclusionKind.SupersededEvidence,
                    stale.Receipt.Messages.Single(item => item.SourceMessageId == result.Id).ExclusionKind.Value,
                    "stale archived body reports its actual exclusion decision");
                var removedAuthority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                    new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Removed(exact.Identity, 2) }) }),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                var removed = compiler.Compile(removedAuthority, new ChatMessage[0], new ChatMessage[0], null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000, workingSet: set);
                AssertEqual(ContextExclusionKind.UnavailableEvidence,
                    removed.Receipt.Messages.Single(item => item.SourceMessageId == result.Id).ExclusionKind.Value,
                    "removed resource reports unavailable evidence instead of a generic exclusion");
                System.IO.File.Delete(blobs.PathFor(payload.Sha256));
                var omitted = compiler.Compile(authority, new ChatMessage[0], new ChatMessage[0], null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000,
                    workingSet: ContextWorkingSet.Restore(session, new ChatMessage[0], 0));
                AssertEqual(0, omitted.Receipt.HydratedPayloads, "zero body budget never touches missing CAS bytes");
                AssertEqual(1, omitted.Receipt.EvictedBodies, "budget omission is observable independently of missing storage");
                AssertEqual(0, omitted.DeliveredObservations.Count, "omitted body does not grant authority");
                AssertEqual(ContextExclusionKind.Budget,
                    omitted.Receipt.Messages.Single(item => item.SourceMessageId == result.Id).ExclusionKind.Value,
                    "zero budget identifies the archived body's exclusion without reading missing CAS");
                var activeResult = ContinuityResult("active_source", "core.read", ToolResult.Ok("Read.",
                    new JObject { ["target"] = "VBA module: Review", ["representation"] = "source", ["complete"] = true, ["text"] = body }.ToString()));
                activeResult.ResourceEvidence.Add(observation);
                var active = new[] { ContinuityCall(activeResult), activeResult };
                var deduplicated = compiler.Compile(authority, new ChatMessage[0], active, null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000,
                    workingSet: ContextWorkingSet.Restore(session, active, 1000));
                AssertEqual(0, deduplicated.Receipt.HydratedPayloads, "active coverage excludes the archived copy before CAS hydration");
                AssertEqual(0, deduplicated.Receipt.RetainedBodies, "an active body is not counted as archive restoration");
                AssertTrue(deduplicated.Receipt.Deduplicated > 0, "active and archived bodies use one deduplication policy");
                AssertEqual(ContextExclusionKind.DuplicateBody,
                    deduplicated.Receipt.Messages.Single(item => item.SourceMessageId == result.Id).ExclusionKind.Value,
                    "archive copy records deduplication without claiming its body was delivered elsewhere");
                AssertContains(string.Join("\n", deduplicated.Messages.Select(item => item.Content)), body, "the active source remains complete");
                AssertEqual(1, deduplicated.DeliveredObservations.Count, "active and archived source share delivery rules");
                AssertEqual(activeResult.Id, deduplicated.Receipt.Messages.Single(item => item.SourceMessageId == result.Id).RetainedAtMessageId,
                    "archive exclusion points to the exact included active result");

                blobs.StoreText(body, "text/plain");
                var differentBytes = JsonConvert.DeserializeObject<ChatMessage>(JsonConvert.SerializeObject(activeResult));
                differentBytes.Content = differentBytes.Content.Replace(body, body + "_DIFFERENT");
                var different = new[] { ContinuityCall(differentBytes), differentBytes };
                var separate = compiler.Compile(authority, new ChatMessage[0], different, null,
                    new ToolCatalogEntry[0], new AppSettings(), 12000,
                    workingSet: ContextWorkingSet.Restore(session, different, 1000));
                AssertEqual(1, separate.Receipt.RetainedBodies, "equal evidence alone cannot suppress different archived bytes");
                AssertEqual(1, separate.Receipt.HydratedPayloads, "different exact archive body is hydrated");
                AssertEqual(0, separate.Receipt.Deduplicated, "byte comparison guards current-view omission");
            });
        }

        private static void ContextContinuityKeepsChronologicalReadBodies()
        {
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var scenario in new[] { "identical", "partial", "table", "empty", "metadata", "different-body", "scope", "resource", "coverage", "view" })
            {
                var scope = new ResourceAuthorityScopeId("document", "p4");
                var otherScope = scenario == "scope" ? new ResourceAuthorityScopeId("document", "p4-other") : scope;
                var source = new ResourceRef("rna://vba/p4/module", "v1");
                var other = scenario == "resource" ? new ResourceRef("rna://vba/p4/other", "v1") : source;
                var partial = scenario == "partial" || scenario == "coverage";
                var coverage = partial ? new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 2) : ResourceCoverage.Whole();
                var otherCoverage = scenario == "coverage" ? new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 3, end: 4) : coverage;
                var body = scenario == "empty" ? "" : "EXACT_READ_" + new string('Ж', 2048);
                var view = scenario == "table" ? "table" : "source";
                Func<int, ChatMessage> read = number => {
                    var selectedView = number == 2 && scenario == "view" ? "text" : view;
                    var selectedCoverage = number == 2 ? otherCoverage : coverage;
                    var evidence = new ResourceEvidence("read" + number, number == 2 ? otherScope : scope,
                        number == 2 ? other : source, selectedView, selectedCoverage, !partial, 1);
                    var data = new JObject { ["kind"] = "resource-read", ["type"] = "VBA module",
                        ["target"] = "VBA module: M", ["representation"] = selectedView,
                        ["coverage"] = JObject.FromObject(selectedCoverage), ["complete"] = !partial,
                        ["rawContentIncluded"] = true };
                    if (scenario == "table") data["table"] = new JObject { ["rows"] = new JArray(new JArray("value", body)) };
                    else data["text"] = number == 2 && scenario == "different-body" ? body + "_CHANGED" : body;
                    if (scenario == "metadata") data["requestedLines"] = 100 * number;
                    var result = ContinuityResult("p4-read-" + number, "core.read", ToolResult.Ok(scenario == "metadata" ? "Read " + number : "Read.", data.ToString()), role);
                    result.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, number);
                    result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, number);
                    result.ResourceEvidence.Add(evidence);
                    result.ResourceModelReferences.Add(new ModelResourceReference(evidence.ScopeId, evidence.Resource,
                        ModelReference.Create(ModelReferenceKind.Resource, scenario == "scope" || scenario == "resource" ? number : 1), 1));
                    return result;
                };
                var first = read(1); var last = read(2);
                var firstCall = ContinuityCall(first); firstCall.ToolCallReference = first.ToolCallReference;
                var lastCall = ContinuityCall(last); lastCall.ToolCallReference = last.ToolCallReference;
                var facts = new[] { new ChatMessage { Role = "user", Content = "Read the module." }, firstCall, first,
                    new ChatMessage { Role = "assistant", Content = "First inspection done." },
                    new ChatMessage { Role = "user", Content = "Check the same source again." }, lastCall, last };
                var original = JsonConvert.SerializeObject(facts);
                var scopes = new List<ResourceAuthoritySnapshot> { new ResourceAuthoritySnapshot(scope, 1, null, 0,
                    scenario == "resource" ? new[] { ResourceHeadState.Known(source, 1), ResourceHeadState.Known(other, 1) } :
                        new[] { ResourceHeadState.Known(source, 1) }) };
                if (scenario == "scope") scopes.Add(new ResourceAuthoritySnapshot(otherScope, 1, null, 0, new[] { ResourceHeadState.Known(other, 1) }));
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(scopes), "tools", new SkillCatalogSnapshot(null), null, 7);
                var compiler = new ModelContextCompiler();
                var snapshot = compiler.Compile(authority, new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), 16000);
                AssertTrue(facts.Select(message => message.Id).SequenceEqual(snapshot.Messages.Select(message => message.Id)),
                    scenario + ": both user turns and both read pairs keep their exact order in " + role);
                var duplicate = new[] { "identical", "partial", "table", "empty", "metadata" }.Contains(scenario);
                var earlier = snapshot.Receipt.Messages.Single(item => item.SourceMessageId == first.Id);
                AssertEqual(duplicate ? last.Id : null, earlier.RetainedAtMessageId, scenario + ": retained target identifies the included body");
                AssertEqual(duplicate ? 1 : 2, snapshot.DeliveredObservations.Count, scenario + ": only actual source bodies authorize reuse");
                AssertEqual(!duplicate, earlier.EvidenceLinks.Single().BodyIncludedInMessage.Value, "receipt reports actual body delivery");
                var wire = JArray.FromObject(new LlmMessageBuilder().Build(ModelReferenceProjection.Project(snapshot.Messages), new AppSettings()).Messages);
                if (role == "tool")
                {
                    AssertEqual("T1", (string)wire[1]["tool_calls"][0]["id"], "earlier native call remains");
                    AssertEqual("T1", (string)wire[2]["tool_call_id"], "earlier native result remains adjacent");
                    AssertEqual("T2", (string)wire[5]["tool_calls"][0]["id"], "later native call remains");
                    AssertEqual("T2", (string)wire[6]["tool_call_id"], "later native result remains adjacent");
                }
                Func<int, JObject> dataAt = index => {
                    var content = (string)wire[index]["content"];
                    return JObject.Parse(content.StartsWith("TOOL_RESULT:\n", StringComparison.Ordinal) ? content.Substring(13) : content);
                };
                var deliveredFirst = dataAt(2);
                AssertEqual("ok", (string)deliveredFirst["status"], "body omission never rewrites the actual read outcome");
                AssertEqual("E1", (string)deliveredFirst["evidence"], "historical eligible evidence remains visible");
                if (duplicate)
                {
                    AssertEqual("T2", (string)deliveredFirst["data"]["observation"]["retainedAt"], "wire pointer uses the issued call alias");
                    AssertEqual("R1@v1", (string)deliveredFirst["data"]["observation"]["ref"], "omitted body keeps its observed version");
                    AssertEqual("duplicate", (string)deliveredFirst["data"]["observation"]["omittedReason"], "omission is explicit");
                    AssertEqual(ContextExclusionKind.DuplicateBody, earlier.ExclusionKind.Value, "Inspector identifies deduplication");
                    AssertTrue(JToken.DeepEquals(JObject.FromObject(coverage), deliveredFirst["data"]["coverage"]), "omission preserves exact coverage");
                    if (scenario == "metadata")
                    {
                        AssertEqual(100, (int)deliveredFirst["data"]["requestedLines"], "folding preserves each read's own metadata");
                        AssertEqual("Read 1", (string)deliveredFirst["message"], "folding preserves each read's actual outcome message");
                    }
                }
                AssertTrue(!wire.ToString().Contains("rna://"), "runtime resource identity never enters wire");
                AssertEqual(original, JsonConvert.SerializeObject(facts), "compilation leaves durable reads intact");
                if (scenario != "identical" && scenario != "partial" && scenario != "table") continue;
                var tight = compiler.Compile(authority, new ChatMessage[0], facts, null, new ToolCatalogEntry[0], new AppSettings(), snapshot.Receipt.EstimatedTokens - 1);
                AssertTrue(!string.Join("\n", tight.Messages.Select(message => message.Content)).Contains("retainedAt"), "final budget cannot leave a dangling pointer");
                AssertEqual(null, tight.Receipt.Messages.Single(item => item.SourceMessageId == first.Id).RetainedAtMessageId, "Inspector also drops the omitted destination");
                AssertEqual(0, tight.DeliveredObservations.Count, "budget notices confer no authority for full, partial or table bodies");
            }
        }

        private static void ContextContinuityRetainsVbaLines()
        {
            const string excerpt = "Sub Main()\nDebug.Print \"before\"\nEnd Sub\n";
            var scope = new ResourceAuthorityScopeId("document", "vba-lines");
            var reference = new ResourceRef("rna://vba/vba-lines/module-lines/Module1/2/5", "r1");
            var data = new JObject {
                ["kind"] = "resource-read", ["type"] = "VBA module",
                ["target"] = "VBA module: Module1", ["representation"] = "source",
                ["text"] = excerpt, ["complete"] = true, ["sourceComplete"] = false,
                ["selectionComplete"] = true,
                ["coverage"] = JObject.FromObject(ResourceCoverage.Whole()),
                ["sourceCoverage"] = JObject.FromObject(new ResourceCoverage(
                    ResourceCoverageKinds.LineRange, start: 2, end: 5))
            };
            var result = ContinuityResult("lines", "core.read",
                ToolResult.Ok("Exact selected lines.", data.ToString()));
            result.ResourceEvidence.Add(new ResourceEvidence("lines", scope, reference, "source",
                ResourceCoverage.Whole(), false, 1));
            var session = new ChatSession { Messages = new List<ChatMessage> { ContinuityCall(result), result } };
            SetContinuityCheckpoint(session, result);
            var current = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(reference, 1) }) }),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            var set = ContextWorkingSet.Restore(session, new ChatMessage[0], 2000);
            var restored = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(
                current, new ChatMessage[0], new ChatMessage[0], null, new ToolCatalogEntry[0],
                new AppSettings(), 6000, workingSet: set);
            AssertEqual(1, restored.Receipt.RetainedBodies, "current exact line window is retained after compaction");
            var retained = restored.Messages.Single(item => item.Role == "tool" && item.ToolCallId == "lines" &&
                item.ToolResultProtocolVersion == ToolResultWire.CurrentVersion);
            ToolResultWireReadResult wire; string error;
            AssertTrue(ToolResultHistoryReader.TryRead(retained, out wire, out error),
                "retained line window remains a tool result");
            AssertEqual(excerpt, (string)JObject.Parse(wire.Result.DataJson)["text"],
                "the model receives the exact selected lines without another read");
            AssertEqual(1, restored.DeliveredObservations.Count,
                "retained line body has an exact delivered observation");
            AssertTrue(!restored.DeliveredObservations.Single().Complete &&
                restored.DeliveredObservations.Single().Resource.Identity.Equals(reference.Identity),
                "a complete line subresource never authorizes whole-module replacement");
            var stale = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Removed(reference.Identity, 2) }) }),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            var invalidated = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(
                stale, new ChatMessage[0], new ChatMessage[0], null, new ToolCatalogEntry[0],
                new AppSettings(), 6000, workingSet: set);
            AssertEqual(0, invalidated.Receipt.RetainedBodies, "invalidated line window is not restored");
        }

        private static void ContextContinuityRestoresWorkingSet()
        {
            var scope = new ResourceAuthorityScopeId("catalog", "test");
            var source = new ResourceRef("rna://catalog/skills/test/body", "r1");
            var definition = new SkillDefinition { Id = "test", Name = "test", Enabled = true, BodyMarkdown = "KEEP_COMPLETE_BODY" };
            var skill = ContinuityResult("skill_body", "core.tools.read", ToolResult.Ok("loaded",
                "{\"kind\":\"skill\",\"id\":\"test\",\"revision\":\"" + SkillRevision.Compute(definition) + "\",\"loaded\":true,\"complete\":true,\"truncated\":false,\"bodyMarkdown\":\"KEEP_COMPLETE_BODY\"}"));
            skill.ResourceEvidence.Add(new ResourceEvidence("e1", scope, source, "text", ResourceCoverage.Whole(), true, 1, immutable: true));
            var rows = ContinuityResult("rows", "core.read", ToolResult.Ok("Selected rows.",
                "{\"target\":\"Excel range: Sheet!A62:B85\",\"representation\":\"records\",\"complete\":false,\"table\":{\"rows\":[{\"id\":7,\"revision\":\"business value\"}],\"columns\":[{\"key\":\"id\"}]}}"));
            rows.ResourceEvidence.Add(new ResourceEvidence("rows", scope, source, "records",
                new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: 61, end: 84, path: "$", fields: new[] { "id", "revision" }), false, 1));
            var mutation = ContinuityResult("write_done", "core.write", ToolResult.Ok("Saved and read back."));
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
            var restored = ContextWorkingSet.Restore(session, active, 4000);
            var compiled = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(authority, new ChatMessage[0], active,
                null, new ToolCatalogEntry[0], new AppSettings(), 6000,
                workingSet: restored);
            AssertTrue(compiled.Messages.Any(m => m.CompletedOperation?.ToolCallId == "write_done" &&
                m.CompletedOperation.Status == ToolResultStatus.Ok), "mutation receipt survives independently of LLM summary");
            var operationCallReceipts = compiled.Receipt.Messages.Where(item =>
                item.SourceMessageId == session.Messages[4].Id).ToArray();
            AssertEqual(1, operationCallReceipts.Length, "one archived call receipt");
            AssertEqual(ContextExclusionKind.OperationReceipt,
                operationCallReceipts.Single().ExclusionKind.Value,
                "retained archived operation explains why its original call is not a separate message");
            Func<ContextWorkingSet, IReadOnlyList<ChatMessage>> compileSet = set => new ModelContextCompiler().Compile(authority,
                new ChatMessage[0], active, null, new ToolCatalogEntry[0], new AppSettings(),
                6000, workingSet: set).Messages;
            AssertEqual(2, compiled.Receipt.RetainedBodies, "receipt exposes real retention");
            AssertContains(string.Join("\n", compiled.Messages.Select(m => m.Content)), "KEEP_COMPLETE_BODY", "model receives the still current complete skill after compaction");
            var retainedRows = compiled.Messages.Single(m => m.ToolCallId == "rows" && m.Role != "assistant" &&
                m.ToolResultProtocolVersion == ToolResultWire.CurrentVersion);
            ToolResultWireReadResult rowWire; string rowError;
            AssertTrue(ToolResultHistoryReader.TryRead(retainedRows, out rowWire, out rowError), "restored row result retains its protocol frame");
            AssertEqual("business value", (string)JObject.Parse(rowWire.Result.DataJson).SelectToken("table.rows[0].revision"), "business field names are unchanged");
            AssertTrue(!retainedRows.ResourceEvidence.Single().Complete, "partial row selection cannot become whole-read evidence");
            var removedAuthority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new[] {
                new ResourceAuthoritySnapshot(scope, 2, null, 0, new[] { ResourceHeadState.Removed(source.Identity, 2) }) }),
                "pack", new SkillCatalogSnapshot(new[] { definition }), null, 4);
            var removedRequest = new ModelContextCompiler(projection: ModelToolResultProjection.Instance).Compile(removedAuthority,
                new ChatMessage[0], active, null, new ToolCatalogEntry[0], new AppSettings(), 6000, workingSet: restored);
            AssertEqual(0, removedRequest.Receipt.RetainedBodies, "removed resources do not restore current bodies");
            AssertTrue(removedRequest.Receipt.ExcludedUnavailable > 0, "compilation records removed evidence as unavailable");
            AssertTrue(!removedRequest.Messages.Any(m => (m.Content ?? "").Contains("KEEP_COMPLETE_BODY")),
                "a previously restored body cannot survive removal in the next frozen request");
            AssertContains(skill.Content, "KEEP_COMPLETE_BODY", "removal does not rewrite historical observations");
            var oversized = ContinuityResult("large_receipt", "core.write",
                ToolResult.Error(new string('x', 24000)));
            session.Messages.Add(ContinuityCall(oversized)); session.Messages.Add(oversized);
            checkpoint.ThroughMessageId = oversized.Id;
            var withLarge = ContextWorkingSet.Restore(session, active, 4000);
            AssertTrue(compileSet(withLarge).Any(m => m.CompletedOperation?.ToolCallId == "write_done"),
                "oversized optional receipt does not hide older affordable receipts");
            var evicted = ContextWorkingSet.Restore(session, active, 0);
            var evictedRequest = new ModelContextCompiler().Compile(authority, new ChatMessage[0], active,
                null, new ToolCatalogEntry[0], new AppSettings(), 6000, workingSet: evicted);
            AssertEqual(2, evictedRequest.Receipt.EvictedBodies, "body budget is enforced before hydration");
            AssertEqual(0, evictedRequest.Receipt.HydratedPayloads, "omitted bodies do not read CAS");
            AssertTrue(!evictedRequest.Messages.Any(m => (m.Content ?? "").Contains("KEEP_COMPLETE_BODY")), "eviction cannot silently retain bytes");
            AssertContains(string.Join("\n", evictedRequest.Messages.Select(m => m.Content)), "not missing resources", "body absence is distinct from resource absence");
            var pending = ContinuityResult("uncertain", "core.patch", ToolResult.Unknown("Inspect before retry."));
            pending.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown);
            pending.ResourceEffect = new ResourceEffect("unknown", "core.patch", ResourceEffectOutcome.UnknownAfterDispatch);
            session.Messages.Add(ContinuityCall(pending)); session.Messages.Add(pending);
            checkpoint.ThroughMessageId = pending.Id;
            var pinned = ContextWorkingSet.Restore(session, active, 0);
            AssertTrue(compileSet(pinned).Any(m => m.CompletedOperation?.ToolCallId == "uncertain"),
                "unknown effects are retained even when the optional budget is zero");
            var exhausted = false;
            try { new ModelContextCompiler().Compile(authority, new ChatMessage[0], new ChatMessage[0],
                null, new ToolCatalogEntry[0], new AppSettings(), 1, workingSet: pinned); }
            catch (PromptBudgetExceededException) { exhausted = true; }
            AssertTrue(exhausted, "mandatory receipts cannot be silently dropped to fit a request");
            var neededRead = ContinuityResult("needs_refresh", "core.patch", ToolResult.Error("Read first."));
            skill.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok);
            neededRead.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error,
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired,
                    source.Identity, "text", "test"));
            session.Messages.Add(ContinuityCall(neededRead)); session.Messages.Add(neededRead);
            var recoverySet = ContextWorkingSet.Restore(session, active, 0);
            AssertContains(string.Join("\n", compileSet(recoverySet).Select(m => m.Content)), "RefreshRequired",
                "an earlier successful observation cannot release a later recovery");
            var failedRefresh = ContinuityResult("failed_refresh", "core.read", ToolResult.Error("Read failed."));
            failedRefresh.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error);
            failedRefresh.ResourceEvidence.Add(skill.ResourceEvidence.Single());
            session.Messages.Add(ContinuityCall(failedRefresh)); session.Messages.Add(failedRefresh);
            AssertTrue(compileSet(ContextWorkingSet.Restore(session, active, 0))
                .Any(m => m.CompletedOperation?.ToolCallId == "needs_refresh"),
                "a failed later read cannot release recovery even with matching evidence");
            var refreshed = ContinuityResult("refreshed", "core.read", ToolResult.Ok("Read."));
            refreshed.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok);
            refreshed.ExcludeFromModelContext = true;
            refreshed.ResourceEvidence.Add(skill.ResourceEvidence.Single());
            session.Messages.Add(ContinuityCall(refreshed)); session.Messages.Add(refreshed);
            var resolved = ContextWorkingSet.Restore(session, active, 0);
            AssertTrue(!compileSet(resolved).Any(m => m.CompletedOperation?.ToolCallId == "needs_refresh"),
                "a later successful observation releases recovery even when excluded from the prompt");
            neededRead.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error,
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
            session.LastRun = new ChatRunRecord { RunId = "new-user-run" };
            var newRun = ContextWorkingSet.Restore(session, active, 0);
            AssertTrue(!compileSet(newRun).Any(m => m.CompletedOperation?.ToolCallId == "needs_refresh") &&
                compileSet(newRun).Any(m => m.CompletedOperation?.ToolCallId == "uncertain"),
                "a new user run releases old replan receipts but preserves unresolved effects");


        }

        private static ChatMessage ContinuityCall(ChatMessage result)
        {
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                Id = result.ToolCallId, Name = result.ToolName, Arguments = new Dictionary<string, object>() },
                "", "Read the recorded tool result.", null, result.ToolResultRole, FixtureCallOrigin(result.ToolCallId));
            call.RunId = result.RunId;
            return call;
        }
    }
}
