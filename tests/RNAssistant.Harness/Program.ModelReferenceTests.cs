using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using TerminalToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ModelReferencesPreserveTypedWirePairs()
        {
            foreach (var bad in new[] { "R1", "e1", "E0", "E01", "E-1", "E 1", "E999999999999999999999" })
            {
                try { new ModelReference(ModelReferenceKind.Execution, bad); throw new Exception("Accepted invalid reference: " + bad); }
                catch (ArgumentException) { }
            }
            AssertEqual("R1", ModelReference.Create(ModelReferenceKind.Resource, 1).Alias, "resource is a separate type");
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "call_canonical", Name = "test.read",
                    Arguments = new Dictionary<string, object> { ["text"] = "call_canonical e1 r2 日本語\nРусский" } },
                    "Reading.", "Read requested text.", null, role, FixtureCallOrigin());
                call.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 7);
                var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.ToolCallId, ToolId = call.ToolName },
                    TerminalToolResult.Ok("Read complete.", "{\"text\":\"call_canonical e1 r2\"}"), role);
                result.ExecutionReference = call.ExecutionReference;
                var before = JsonConvert.SerializeObject(new[] { call, result });
                var wire = ModelReferenceProjection.Project(new[] { call, result });
                AssertEqual(before, JsonConvert.SerializeObject(new[] { call, result }), "projection preserves canonical bodies and identities");
                if (role == ToolResultRoles.Tool)
                {
                    AssertEqual(call.ToolCallId, wire[0].ToolCalls.Single().Id, "native transport ID is preserved");
                    AssertEqual(call.ToolCallId, wire[1].ToolCallId, "native result still pairs with transport ID");
                    AssertTrue(wire[0].Content.StartsWith("EXECUTION_REFERENCE: E7"), "native call exposes semantic reference");
                }
                else AssertEqual("E7", (string)JObject.Parse(wire[0].Content)["tool_calls"][0]["id"], "accepted model history adds runtime reference");
                var resultJson = JObject.Parse(wire[1].Content.Replace("TOOL_RESULT:\n", ""));
                AssertEqual("E7", (string)resultJson["tool_call_id"], "matching result uses the same alias");
                AssertTrue(wire[1].Content.Contains("call_canonical e1 r2"), "literal business data is not rewritten");
                AssertTrue(ModelReferenceProjection.Delivered(wire).SequenceEqual(new[] { "E7" }), "pair counts as one reference");
                RNAssistant.Core.ModelProtocol.ToolResultWireReadResult parsed; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(result, out parsed, out error), "canonical result remains readable");
                var folded = ModelContextCompiler.CompleteOperation(result, parsed, call: call);
                var projectedFold = ModelReferenceProjection.Project(new[] { folded });
                AssertTrue(projectedFold[0].Content.Contains("\"tool_call_id\":\"E7\""), "folded receipt retains the execution reference");
                AssertTrue(ModelReferenceProjection.Delivered(projectedFold).SequenceEqual(new[] { "E7" }), "folded receipt remains deliverable without the full body");
                call.ExecutionReference = null;
                try { ModelReferenceProjection.Project(new[] { call }); throw new Exception("Canonical ID leaked without reference."); }
                catch (InvalidOperationException) { }
            }
        }

        private static void ModelReferencesSurviveCompactionWithoutBodies()
        {
            var result = ContinuityResult("canonical-read", "core.read", TerminalToolResult.Ok("Read complete."));
            result.RunId = "run-refs";
            result.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 42);
            var call = ContinuityCall(result);
            call.RunId = result.RunId; call.ExecutionReference = result.ExecutionReference;
            var session = new ChatSession { LastRun = new ChatRunRecord { RunId = result.RunId },
                Messages = new List<ChatMessage> { call, result } };
            SetContinuityCheckpoint(session, result);
            var restored = ContextWorkingSet.Restore(session, new ChatMessage[0], 0);
            AssertEqual(1, restored.Operations.Count, "latest current-run read survives zero optional body budget");
            AssertTrue(restored.Operations[0].Required, "a pure read receipt is mandatory after compaction");
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(new SkillDefinition[0]), null, 1);
            var compiled = new ModelContextCompiler().Compile(authority, new ChatMessage[0], new ChatMessage[0], null,
                new ToolCatalogEntry[0], new AppSettings(), 4000, workingSet: restored);
            var projected = ModelReferenceProjection.Project(compiled.Messages);
            AssertTrue(ModelReferenceProjection.Delivered(projected).SequenceEqual(new[] { "E42" }),
                "folding never renumbers or drops the last terminal reference");
        }

        private static async Task ModelReferencesFreezeRepairAndFallback()
        {
            var request = NewProtocolRequest(2, true);
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "canonical-repair", Name = "test.read" },
                "Read.", "Read source.", null, ToolResultRoles.User, FixtureCallOrigin());
            call.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 9);
            var resultMessage = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.ToolCallId, ToolId = call.ToolName },
                TerminalToolResult.Ok("Read complete."), ToolResultRoles.User);
            resultMessage.ExecutionReference = call.ExecutionReference;
            var frozen = request.AcceptedMessages.Concat(new[] { call, resultMessage }).ToArray();
            request.AcceptedMessages = frozen;
            request.CompileRepair = notice => frozen.Concat(new[] { notice }).ToArray();
            request.ProjectReferences = ModelReferenceProjection.Project;
            var attempts = 0;
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                attempts++;
                var content = string.Join("\n", messages.Select(message => message.Content));
                AssertTrue(content.Contains("\"id\":\"E9\"") && content.Contains("\"tool_call_id\":\"E9\"") && !content.Contains("canonical-repair"),
                    "every strict/repair/fallback request keeps the same aliases");
                if (attempts == 2) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "schema unsupported");
                return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? "invalid" :
                    ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Report read.", "Done.",
                        results: new[] { new ConversationResultClaim("Read.", new[] { "E9" }, ConversationAssessment.Satisfied) }, outcome: ConversationOutcome.Completed) });
            });
            var outcome = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(outcome.Failure == null, "reference projection allows bounded repair/fallback");
            AssertEqual(3, attempts, "one invalid completion and one explicit format fallback");
            AssertTrue(outcome.DeliveredExecutionReferences.SequenceEqual(new[] { "E9" }), "delivered set comes from successful final request");
            AssertTrue(ConversationResponseHistoryReader.Read(call).Success, "canonical history remains raw-decoder compatible");
        }

        private static void ModelResourceReferencesBindSourceBlocks()
        {
            var scope = new ResourceAuthorityScopeId("workspace", "first");
            var foreignScope = new ResourceAuthorityScopeId("workspace", "second");
            var exact = new ResourceRef("rna://workspace/files/example.txt", "revision-one");
            var first = new ResourceEvidence("first-slice", scope, exact, "text",
                new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 1, end: 3), false, 1);
            var second = new ResourceEvidence("second-slice", scope, exact, "text",
                new ResourceCoverage(ResourceCoverageKinds.LineRange, start: 9, end: 11), false, 1);
            var foreign = new ResourceEvidence("other-source", foreignScope, exact, "text", first.Coverage, false, 1);
            var bindings = new List<ModelResourceReference> {
                new ModelResourceReference(foreignScope, exact, ModelReference.Create(ModelReferenceKind.Resource, 2), 1),
                new ModelResourceReference(scope, new ResourceRef(exact.Uri, "revision-two"), ModelReference.Create(ModelReferenceKind.Resource, 1), 2),
                new ModelResourceReference(scope, exact, ModelReference.Create(ModelReferenceKind.Resource, 1), 1) };
            var sequence = 0;
            Func<ResourceEvidence, ChatMessage> read = evidence => {
                var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation {
                    ToolCallId = "canonical-read-" + (++sequence), ToolId = "core.read" },
                    TerminalToolResult.Ok("Excerpt read.", new JObject {
                        ["kind"] = "resource-read", ["target"] = "example.txt", ["representation"] = "text",
                        ["coverage"] = JObject.FromObject(evidence.Coverage), ["complete"] = false,
                        ["text"] = "Русский 日本語 R999@v8 literal", ["ref"] = "untrusted-label"
                    }.ToString(Formatting.None)), ToolResultRoles.User);
                result.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, sequence);
                result.ResourceEvidence.Add(evidence);
                result.ResourceModelReferences = bindings.ToList();
                return result;
            };
            Func<ChatMessage, JObject> project = message => JObject.Parse(ModelReferenceProjection.Project(new[] {
                new ModelResultProjection().Project(message) }).Single().Content.Substring("TOOL_RESULT:\n".Length));
            var reads = new[] { read(first), read(second), read(foreign) };
            var canonical = JsonConvert.SerializeObject(reads);
            var blocks = reads.Select(project).ToArray();
            AssertTrue(blocks.Select(block => (string)block["data"]["ref"]).SequenceEqual(new[] { "R1@v1", "R1@v1", "R2@v1" }),
                "bindings use scope and revision, never alias list order or identical target labels");
            for (var i = 0; i < blocks.Length; i++)
            {
                AssertTrue(blocks[i]["resource_refs"] == null && blocks[i]["ref"] == null,
                    "no floating labels at the result envelope");
                AssertTrue(JToken.DeepEquals(blocks[i]["data"]["coverage"], JObject.FromObject(reads[i].ResourceEvidence.Single().Coverage)),
                    "each slice retains its actual coverage next to the same revision reference");
                AssertEqual("Русский 日本語 R999@v8 literal", (string)blocks[i]["data"]["text"], "source bytes stay literal");
            }
            AssertEqual(canonical, JsonConvert.SerializeObject(reads), "wire references do not change durable results or evidence");

            var ambiguous = read(first);
            ambiguous.ResourceEvidence.Add(foreign);
            AssertTrue(project(ambiguous)["data"]["ref"] == null, "ambiguous source identity must not receive a guessed label");
            var unbound = read(first);
            unbound.ResourceModelReferences.RemoveAt(2);
            AssertTrue(project(unbound)["data"]["ref"] == null, "another scope or revision cannot supply a missing binding");
            foreach (var field in new[] { "text", "coverage", "representation" })
            {
                var incomplete = read(first);
                var root = JObject.Parse(incomplete.Content.Substring("TOOL_RESULT:\n".Length));
                ((JObject)root["data"]).Remove(field);
                incomplete.Content = "TOOL_RESULT:\n" + root.ToString(Formatting.None);
                AssertTrue(project(incomplete)["data"]["ref"] == null, "missing " + field + " cannot assert source delivery");
            }
            var mismatched = read(first);
            mismatched.ResourceEvidence = new List<ResourceEvidence> { second };
            AssertTrue(project(mismatched)["data"]["ref"] == null, "a matching resource with different coverage does not bind the displayed slice");

            foreach (var tool in new[] { "core.find", "core.search", "resources.outline", "files.create" })
            {
                var navigation = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "navigation", ToolId = tool },
                    TerminalToolResult.Ok("Navigation or outcome.", "{\"target\":\"example.txt\",\"matches\":[{\"target\":\"example.txt\",\"snippet\":\"fragment\"}]}"), ToolResultRoles.User);
                navigation.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 20);
                navigation.ResourceModelReferences = bindings;
                navigation.ResourceEvidence.Add(first);
                var visible = project(navigation).ToString(Formatting.None);
                AssertTrue(!visible.Contains("resource_refs") && !visible.Contains("\"ref\":"),
                    tool + " cannot attribute a body from a loose resource list");
            }
            var projectedRead = new ModelResultProjection().Project(reads[0]);
            ToolResultWireReadResult parsed; string error;
            AssertTrue(ToolResultHistoryReader.TryRead(projectedRead, out parsed, out error), "canonical reader still accepts the projected read");
            var receipt = ModelContextCompiler.CompleteOperation(projectedRead, parsed,
                new ResourceObservationNotice { BodyIncluded = false, State = EvidenceState.Unknown, Reason = "Body omitted." });
            var receiptWire = ModelReferenceProjection.Project(new[] { receipt }).Single().Content;
            AssertContains(receiptWire, "\"tool_call_id\":\"E1\"", "omitted body keeps execution evidence");
            AssertTrue(!receiptWire.Contains("R1@v1") && !receiptWire.Contains("resource_refs"), "receipt never claims source body delivery");

            foreach (var evidence in new[] { first, second, foreign })
            {
                var header = new JObject { ["target"] = "example.txt", ["view"] = evidence.View,
                    ["bodyIncluded"] = true, ["coverage"] = JObject.FromObject(evidence.Coverage) };
                var view = new ChatMessage { Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                    ResourceEvidence = new List<ResourceEvidence> { evidence }, ResourceModelReferences = bindings,
                    Content = "CURRENT_RESOURCE_VIEW (data):\n" + header.ToString(Formatting.None) + "\nsource\n{\"ref\":\"literal\"}" };
                var visible = ModelReferenceProjection.Project(new[] { view }).Single().Content;
                AssertEqual(evidence == foreign ? "R2@v1" : "R1@v1", (string)JObject.Parse(visible.Split('\n')[1])["ref"],
                    "each retained view binds its own source without modifying the trailing bytes");
                AssertTrue(visible.EndsWith("\nsource\n{\"ref\":\"literal\"}", StringComparison.Ordinal), "view body remains exact");
                header["bodyIncluded"] = false;
                view.Content = "CURRENT_RESOURCE_VIEW (data):\n" + header.ToString(Formatting.None) + "\n";
                AssertEqual(view.Content, ModelReferenceProjection.Project(new[] { view }).Single().Content,
                    "a header with an omitted body receives no source label");
            }
        }

        private static void ModelResourceReferencesKeepIdentityAndVersions()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "resource-refs", "Resources", "Resources");
                var scope = new ResourceAuthorityScopeId("workspace", "first");
                Func<ResourceAuthorityScopeId, string, ChatMessage> observe = (owner, revision) =>
                {
                    var evidence = new ResourceEvidence("obs-" + Guid.NewGuid().ToString("N"), owner,
                        new ResourceRef("rna://workspace/files/example.txt", revision), "source", ResourceCoverage.Whole(), true, 1);
                    var message = new ChatMessage { Role = "user", Content = "Observed source.", ResourceEvidence = new List<ResourceEvidence> { evidence } };
                    session.Messages.Add(message);
                    store.Commit(session, ConversationCommit.UserInput(message));
                    return message;
                };
                var first = observe(scope, "canonical-revision-one");
                AssertEqual("R1@v1", first.ResourceModelReferences.Single().Alias, "first exact observation");
                var repeat = observe(scope, "canonical-revision-one");
                AssertEqual("R1@v1", repeat.ResourceModelReferences.Single().Alias, "same revision never receives another version");
                var changed = observe(scope, "canonical-revision-two");
                AssertEqual("R1@v2", changed.ResourceModelReferences.Single().Alias, "same resource advances its display version");
                var foreign = observe(new ResourceAuthorityScopeId("workspace", "other"), "canonical-revision-one");
                AssertEqual("R2@v1", foreign.ResourceModelReferences.Single().Alias, "scope is part of resource identity");
                var ids = session.Messages.Select(message => message.Id).ToArray();
                session.Messages.Clear();
                store.Commit(session, ConversationCommit.RewriteHistory(ids, session.Messages));
                session = new ChatStore(paths).Load(session.Id);
                var restored = observe(scope, "canonical-revision-one");
                AssertEqual("R1@v1", restored.ResourceModelReferences.Single().Alias, "removal/restart keeps the original mapping");
                AssertEqual(3, session.ResourceReferenceIndex.Count, "derived index has one entry per exact scoped revision");
                var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "canonical-call", ToolId = "core.read" },
                    TerminalToolResult.Ok("Read.", "{\"kind\":\"resource-read\",\"target\":\"example.txt\",\"representation\":\"source\",\"coverage\":{\"kind\":\"whole\",\"fields\":[]},\"text\":\"r1@v2 literal\"}"), ToolResultRoles.User);
                result.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 1);
                result.ResourceModelReferences = changed.ResourceModelReferences;
                result.ResourceEvidence = changed.ResourceEvidence;
                var wire = ModelReferenceProjection.Project(new[] { result }).Single().Content;
                AssertTrue(wire.Contains("\"ref\":\"R1@v2\"") && !wire.Contains("resource_refs") && !wire.Contains("canonical-revision"),
                    "result exposes resource/version labels without exact runtime revisions");
                AssertTrue(wire.Contains("r1@v2 literal"), "resource labels never rewrite business text");
                var view = new ChatMessage { Role = "assistant", ProtocolMessage = true, SyntheticResourceObservation = true,
                    ResourceModelReferences = changed.ResourceModelReferences,
                    ResourceEvidence = changed.ResourceEvidence,
                    Content = "CURRENT_RESOURCE_VIEW (data):\n{\"target\":\"example.txt\",\"view\":\"source\",\"bodyIncluded\":true,\"coverage\":{\"kind\":\"whole\",\"fields\":[]}}\nr1@v2 literal\n日本語" };
                var projectedView = ModelReferenceProjection.Project(new[] { view }).Single().Content;
                AssertTrue(projectedView.Contains("\"ref\":\"R1@v2\"") && projectedView.EndsWith("r1@v2 literal\n日本語"),
                    "current view changes only its owned header");
                var staleTraceSession = new ChatStore(paths).Load(session.Id);
                var trace = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "mixed-references", ContextMessages = new List<ContextMessagePresentation> {
                        new ContextMessagePresentation { Role = "user", EvidenceLinks = new List<ContextEvidenceLink> {
                            new ContextEvidenceLink { ScopeId = scope, Resource = first.ResourceEvidence.Single().Resource },
                            new ContextEvidenceLink { ScopeId = scope, Resource = changed.ResourceEvidence.Single().Resource },
                            new ContextEvidenceLink { ScopeId = scope, Resource = new ResourceRef(
                                first.ResourceEvidence.Single().Resource.Uri, "trace-only-revision") } } } } },
                    "{}", "application/json", "run", "turn", null);
                AssertEqual(4, session.ResourceReferenceIndex.Count, "trace append advances the shared registry");
                session = new ChatStore(paths).Load(session.Id);
                AssertEqual(4, session.ResourceReferenceIndex.Count, "trace bindings replay through storage after restart");
                AssertEqual("R1@v3", observe(scope, "trace-only-revision").ResourceModelReferences.Single().Alias,
                    "later message observation reuses the reference allocated to the saved request");
                try
                {
                    store.AppendTrace(staleTraceSession, SessionEventTypes.LlmRequest,
                        new LlmTraceRecord { ContextMessages = new List<ContextMessagePresentation> {
                            new ContextMessagePresentation { EvidenceLinks = new List<ContextEvidenceLink> {
                                new ContextEvidenceLink { ScopeId = scope, Resource = new ResourceRef(
                                    first.ResourceEvidence.Single().Resource.Uri, "stale-revision") } } } } },
                        "{}", "application/json", "run", "turn", null);
                    throw new Exception("Stale trace append allocated a durable reference.");
                }
                catch (ChatConcurrencyException) { }
                AssertEqual(4, new ChatStore(paths).Load(session.Id).ResourceReferenceIndex.Count,
                    "failed trace append leaves no issued alias");
                AssertEqual("R1@v4", observe(scope, "next-revision").ResourceModelReferences.Single().Alias,
                    "failed candidate cannot skip or overwrite a version");
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), new ChatBlobStore(paths), plane);
                    var rows = inspector.Query(session, new ModelContextQuery { RequestEventId = trace.EventId }, CancellationToken.None).Resources;
                    AssertTrue(rows.Select(row => row.Alias).OrderBy(alias => alias, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "R1@v1", "R1@v2", "R1@v3" }),
                        "One registry assigns all observed revisions; Inspector never allocates or merges aliases");
                    AssertEqual("canonical-revision-one", inspector.Query(session,
                        new ModelContextQuery { Alias = "R1@v1" }, CancellationToken.None).Resources
                        .Single(row => row.Alias == "R1@v1").Resource.Revision,
                        "model resource lookup cannot resolve to the trace-only revision");
                }
                var old = store.Create("Workspace", "unbound-old", "Old", "Old");
                old.FormatVersion = 7;
                store.Save(old);
                var oldRequest = store.AppendTrace(old, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { ContextMessages = new List<ContextMessagePresentation> {
                        new ContextMessagePresentation { EvidenceLinks = new List<ContextEvidenceLink> {
                            new ContextEvidenceLink { ScopeId = scope, Resource = first.ResourceEvidence.Single().Resource },
                            new ContextEvidenceLink { ScopeId = scope, Resource = changed.ResourceEvidence.Single().Resource } } } } },
                    "{}", "application/json", "old-run", "old-turn", null);
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), new ChatBlobStore(paths), plane);
                    var rows = inspector.Query(old, new ModelContextQuery { RequestEventId = oldRequest.EventId }, CancellationToken.None).Resources;
                    AssertTrue(rows.Count == 2 && rows.All(row => row.Alias == null),
                        "unbound historical revisions remain separate and visible without invented labels");
                }
            });
        }

        private static void ModelReferencesPersistAndGateFinish()
        {
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "refs", "References", "References");
                session.LastRun = new ChatRunRecord { RunId = "run-refs", TurnId = "run-refs" };
                var summary = new RunSummary("run-refs", "run-refs", RunLifecycle.Running, new ToolCounts(), 1, 0, "", null, null);
                Func<ChatSession, string, ChatMessage> accept = (target, id) =>
                {
                    var context = PersistenceContext(summary.RunId, id);
                    var message = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id, Name = context.Call.Name,
                        Arguments = new Dictionary<string, object> { ["range"] = "A1" } }, "Read.", "Read range.", null,
                        ToolResultRoles.User, new AcceptedToolCallOrigin(context.StepId, "attempt-" + id, 0));
                    message.RunId = summary.RunId;
                    target.Messages.Add(message);
                    store.CommitRun(target, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ResponseAccepted,
                        summary, response: new AgentResponse(ConversationResponse.ExecuteAction, "Read range.", "Read.", new[] { context.Call })), new[] { message }));
                    return message;
                };
                var call = accept(session, "canonical-one");
                AssertEqual("E1", call.ExecutionReference.Alias, "first alias is persisted before any dispatch");
                var trace = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "execution-reference", ContextMessages = new List<ContextMessagePresentation> {
                        new ContextMessagePresentation { Role = "assistant", ToolCallId = call.ToolCallId } } },
                    "{}", "application/json", summary.RunId, summary.TurnId, null);
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), new ChatBlobStore(paths), plane);
                    AssertEqual("E1", inspector.Query(session, new ModelContextQuery { RequestEventId = trace.EventId },
                        CancellationToken.None).Operations.Single().Alias, "Inspector shares the persisted execution alias");
                }
                var stale = new ChatStore(paths).Load(session.Id);
                var expected = new[] { call.ToolCallId };
                Func<string, string[], FinishAdmission> evaluate = (reference, delivered) => ConversationFinishEvidenceAdmission.Evaluate(
                    store.ReadEvents(session.Host, session.DocumentKey, session.Id), session.Id, summary.RunId, summary.TurnId,
                    expected, new[] { new ConversationResultClaim("Read.", new[] { reference }, ConversationAssessment.Satisfied) },
                    new ChatBlobStore(paths), delivered);
                AssertEqual("nonterminal_evidence", evaluate("E1", new[] { "E1" }).Code, "accepted alias is not a terminal outcome");
                var context1 = PersistenceContext(summary.RunId, call.ToolCallId);
                var activity = new ChatMessage { Role = "assistant", RunId = summary.RunId, ExcludeFromModelContext = true,
                    Activity = new ChatActivity { RunId = summary.RunId, StepId = context1.StepId,
                        ToolCallId = call.ToolCallId, ToolId = call.ToolName } };
                session.Messages.Add(activity);
                store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolStarted,
                    summary, toolContext: context1), new ChatMessage[0], activity));
                activity.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok);
                store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolCompleted,
                    summary, execution: KernelRecord(context1)), new ChatMessage[0], activity));
                AssertTrue(evaluate("E1", new[] { "E1" }).Accepted, "delivered canonical terminal reference is accepted");
                AssertEqual("undelivered_evidence", evaluate("E1", new string[0]).Code, "hidden valid reference is rejected");
                foreach (var invalid in new[] { call.ToolCallId, "R1", "e1", "E2" })
                    AssertEqual("unknown_evidence", evaluate(invalid, new[] { invalid }).Code, "no GUID, type, case or guessed fallback");
                try { accept(stale, "stale-call"); throw new Exception("Stale writer allocated a reference."); }
                catch (ChatConcurrencyException) { }
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Unlisted write must fail." });
                try { accept(session, "uncommitted-candidate"); throw new Exception("Incomplete batch commit succeeded."); }
                catch (InvalidOperationException) { }
                var reopened = new ChatStore(paths).Load(session.Id);
                AssertEqual(1L, reopened.ExecutionReferenceSequence, "failed append never publishes a candidate alias");
                AssertEqual("E1", reopened.Messages.Single(message => message.AcceptedCallOrigin != null).ExecutionReference.Alias,
                    "restart preserves the alias");
                var ids = reopened.Messages.Select(message => message.Id).ToArray();
                reopened.Messages.Clear();
                store.Commit(reopened, ConversationCommit.RewriteHistory(ids, reopened.Messages));
                reopened = new ChatStore(paths).Load(session.Id);
                AssertEqual(1L, reopened.ExecutionReferenceSequence, "removal cannot reset the allocation high water");
                var next = accept(reopened, "canonical-two");
                AssertEqual("E2", next.ExecutionReference.Alias, "deleted aliases are not reused");
                next.ExecutionReference = ModelReference.Create(ModelReferenceKind.Execution, 9);
                try { store.Commit(reopened, new ConversationCommit(new[] { next })); throw new Exception("Alias mutation accepted."); }
                catch (InvalidOperationException) { }
                AssertEqual("E2", new ChatStore(paths).Load(session.Id).Messages.Single().ExecutionReference.Alias,
                    "failed update does not alter durable alias");
                AssertEqual(8, ChatSession.CurrentFormatVersion, "short references ship within the existing, undeployed format 8");
                AssertTrue(!ChatSession.IsReadableFormat(9) && !ChatSession.IsReadableFormat(10),
                    "intermediate candidate versions have no compatibility path");
                var legacy = store.Create("Workspace", "old-refs", "Old", "Old");
                legacy.FormatVersion = 7;
                store.Save(legacy);
                var loadedLegacy = new ChatStore(paths).Load(legacy.Id);
                AssertEqual(7, loadedLegacy.FormatVersion, "old v7 history remains readable without rewriting");
                try { ConversationRunPolicy.RequireCurrentSession(loadedLegacy); throw new Exception("Old format ran without refs."); }
                catch (InvalidOperationException) { }
            });
        }
    }
}
