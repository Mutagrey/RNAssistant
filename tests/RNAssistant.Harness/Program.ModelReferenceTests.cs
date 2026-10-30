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
        private static void ModelReferencesSeparateEligibilityAndRevision()
        {
            AssertTrue(!JsonConvert.DeserializeObject<ChatSession>("{\"FormatVersion\":8}").UsesCurrentProtocol,
                "a missing reference revision is never upgraded by the constructor");
            AssertTrue(new ChatSession().UsesCurrentProtocol, "new chats use the current reference revision");
            string normalized;
            AssertTrue(ModelReference.TryNormalize("r12@V3", out normalized) && normalized == "R12@v3", "typed resource lookup normalizes case");
            AssertTrue(!ModelReference.TryNormalize("r012@v3", out normalized), "lookup never repairs malformed aliases");
            WithTempPaths(paths =>
            {
                var store = new ChatStore(paths);
                var session = store.Create("Workspace", "eligibility", "Eligibility", "Eligibility");
                var summary = new RunSummary("eligibility-run", "eligibility-run", RunLifecycle.Running,
                    new ToolCounts(), 1, 0, "", null, null);
                session.LastRun = new ChatRunRecord { RunId = summary.RunId, TurnId = summary.TurnId };
                var ids = new List<string>();
                Func<ConversationResultClaim[], string[], FinishAdmission> finish = (claims, delivered) =>
                    ConversationFinishEvidenceAdmission.Evaluate(store.ReadEvents(session.Host, session.DocumentKey, session.Id),
                        session.Id, summary.RunId, summary.TurnId, ids, claims, new ChatBlobStore(paths), delivered);
                for (var i = 1; i <= 3; i++)
                {
                    var policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None, i == 2, false, new[] { "agent" },
                        evidenceEligibility: i == 1 ? EvidenceEligibility.None : EvidenceEligibility.Eligible);
                    var call = new ToolCall("canonical-" + i, i == 1 ? "core.tools.read" : "test.read", "{}");
                    ids.Add(call.Id);
                    var context = new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "fixture", policy),
                        summary.RunId, summary.TurnId, "step-" + i, DateTime.UtcNow, false, 1);
                    var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = call.Id, Name = call.Name },
                        "", "Inspect.", null, ToolResultRoles.User, new AcceptedToolCallOrigin(context.StepId, "attempt-" + i, 0));
                    accepted.RunId = summary.RunId;
                    session.Messages.Add(accepted);
                    store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ResponseAccepted,
                        summary, response: new AgentResponse(ConversationResponse.ExecuteAction, "Inspect.", "", new[] { call })), new[] { accepted }));
                    AssertEqual("T" + i, accepted.ToolCallReference.Alias, "all accepted calls get T");
                    AssertTrue(accepted.EvidenceReference == null, "acceptance issues no E");
                    var activity = new ChatMessage { Role = "assistant", RunId = summary.RunId, ExcludeFromModelContext = true,
                        Activity = new ChatActivity { RunId = summary.RunId, StepId = context.StepId, ToolId = call.Name, ToolCallId = call.Id } };
                    session.Messages.Add(activity);
                    store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolStarted,
                        summary, toolContext: context), new ChatMessage[0], activity));
                    if (i == 2)
                    {
                        store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolCompleted,
                            summary, execution: KernelRecord(context, ToolExecutionOutcome.AwaitingConfirmation)), new ChatMessage[0], activity));
                        AssertTrue(activity.EvidenceReference == null, "pending approval cannot issue evidence");
                        session = new ChatStore(paths).Load(session.Id);
                        activity = session.Messages.Single(message => message.Activity?.ToolCallId == call.Id);
                        AssertEqual(0L, session.EvidenceReferenceSequence, "restart while pending consumes no E ordinal");
                        context = new ToolExecutionContext(call, context.Policy, context.RunId, context.TurnId,
                            context.StepId, context.StartedUtc, true, 1);
                    }
                    var outcome = i == 2 ? ToolExecutionOutcome.Error : ToolExecutionOutcome.Ok;
                    store.CommitRun(session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolCompleted,
                        summary, execution: KernelRecord(context, outcome)), new ChatMessage[0], activity));
                    AssertEqual(i == 1 ? null : "E" + (i - 1), activity.EvidenceReference?.Alias,
                        "only trusted eligible terminal results get a separate E");
                    if (i == 1) AssertTrue(finish(new ConversationResultClaim[0], new string[0]).Accepted,
                        "service-only work can finish without inventing evidence");
                }
                var failed = new ConversationResultClaim("Read failed.", new[] { "e1" }, ConversationAssessment.Unverified);
                var satisfied = new ConversationResultClaim("Read succeeded.", new[] { "e2" }, ConversationAssessment.Satisfied);
                AssertTrue(finish(new[] { failed }, new[] { "E1" }).Accepted, "failed eligible result can support an honest limitation");
                AssertTrue(finish(new[] { satisfied }, new[] { "E2" }).Accepted, "an uncited earlier error does not block success");
                var contradiction = new ConversationResultClaim("Read succeeded.", new[] { "E1", "E2" }, ConversationAssessment.Satisfied);
                AssertEqual("contradictory_assessment", finish(new[] { contradiction }, new[] { "E1", "E2" }).Code,
                    "a successful result cannot hide a cited failed result");
                var reopened = new ChatStore(paths).Load(session.Id);
                AssertEqual(3L, reopened.ToolCallReferenceSequence, "call high water survives SQLite/replay");
                AssertEqual(2L, reopened.EvidenceReferenceSequence, "evidence high water is independent");
                AssertEqual(3, reopened.ReferenceContractVersion, "reference boundary is durable");
                var last = reopened.Messages.Single(message => message.Activity?.ToolCallId == ids.Last());
                var trace = store.AppendTrace(reopened, SessionEventTypes.LlmRequest, new LlmTraceRecord {
                    ContextMessages = new List<ContextMessagePresentation> { new ContextMessagePresentation {
                        Role = "assistant", ToolCallId = ids.Last(), EvidenceReference = last.EvidenceReference,
                        Presentation = ContextPresentationKind.Summary } } }, "{}", "application/json", summary.RunId, summary.TurnId, null);
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), new ChatBlobStore(paths), plane);
                    var detail = inspector.Query(reopened, new ModelContextQuery { Alias = "e2" }, CancellationToken.None);
                    AssertEqual("E2", detail.SelectedAlias, "Inspector lookup displays canonical evidence case");
                    AssertEqual("E2", detail.EligibleEvidence.Single().Alias, "a folded assistant receipt can deliver eligible evidence");
                }
                AssertTrue(CapabilityToolCatalog.GetTools().All(tool => tool.Policy.EvidenceEligibility == EvidenceEligibility.None) &&
                    SkillLoadToolHandler.Entry().Policy.EvidenceEligibility == EvidenceEligibility.None,
                    "capability and skill loading never issue finish evidence");
                var readOnly = new ChatSession { ReferenceContractVersion = 2, Host = "Workspace", DocumentKey = "legacy-refs" };
                store.Save(readOnly);
                var old = new ChatStore(paths).Load(readOnly.Id);
                AssertTrue(!old.UsesCurrentProtocol, "revision 2 remains readable without reinterpretation");
                try { ConversationRunPolicy.RequireCurrentSession(old); throw new Exception("Legacy references executed."); }
                catch (InvalidOperationException) { }
                old.ReferenceContractVersion = ConversationResponse.ReferenceContractVersion;
                try { store.Save(old); throw new Exception("Reference upgrade rewrote the old stream."); }
                catch (InvalidOperationException) { }
            });
        }

        private static void ModelReferencesPreserveTypedWirePairs()
        {
            foreach (var bad in new[] { "R1", "t1", "T0", "T01", "T-1", "T 1", "T999999999999999999999" })
            {
                try { new ModelReference(ModelReferenceKind.ToolCall, bad); throw new Exception("Accepted invalid reference: " + bad); }
                catch (ArgumentException) { }
            }
            AssertEqual("R1", ModelReference.Create(ModelReferenceKind.Resource, 1).Alias, "resource is a separate type");
            foreach (var role in new[] { ToolResultRoles.User, ToolResultRoles.Developer, ToolResultRoles.Tool })
            {
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "call_canonical", Name = "test.read",
                    Arguments = new Dictionary<string, object> { ["text"] = "call_canonical e1 r2 日本語\nРусский" } },
                    "Reading.", "Read requested text.", null, role, FixtureCallOrigin());
                call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 7);
                var result = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.ToolCallId, ToolId = call.ToolName },
                    TerminalToolResult.Ok("Read complete.", "{\"text\":\"call_canonical e1 r2\"}"), role);
                result.ToolCallReference = call.ToolCallReference;
                result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, 3);
                var before = JsonConvert.SerializeObject(new[] { call, result });
                var wire = ModelReferenceProjection.Project(new[] { call, result });
                AssertEqual(before, JsonConvert.SerializeObject(new[] { call, result }), "projection preserves canonical bodies and identities");
                if (role == ToolResultRoles.Tool)
                {
                    AssertEqual("T7", wire[0].ToolCalls.Single().Id, "native transport uses the call alias");
                    AssertEqual("T7", wire[1].ToolCallId, "native result pairs literally with the call alias");
                    AssertEqual(call.Content, wire[0].Content, "native narration has no duplicate reference marker");
                    var http = JArray.FromObject(new LlmMessageBuilder().Build(wire, new AppSettings()).Messages);
                    AssertEqual("T7", (string)http[0]["tool_calls"][0]["id"], "serialized native call uses T");
                    AssertEqual("T7", (string)http[1]["tool_call_id"], "serialized native result pairs exactly");
                    AssertTrue(JObject.Parse((string)http[1]["content"])["tool_call_id"] == null,
                        "serialized native content contains no duplicate correlation ID");
                }
                else AssertEqual("T7", (string)JObject.Parse(wire[0].Content)["tool_calls"][0]["id"], "accepted model history adds runtime reference");
                var resultJson = JObject.Parse(wire[1].Content.Replace("TOOL_RESULT:\n", ""));
                AssertEqual(role == ToolResultRoles.Tool ? null : "T7", (string)resultJson["tool_call_id"], "native content omits duplicate correlation; fallback retains T");
                AssertEqual("E3", (string)resultJson["evidence"], "terminal evidence has its own sequence");
                AssertTrue(wire[1].Content.Contains("call_canonical e1 r2"), "literal business data is not rewritten");
                AssertTrue(ModelReferenceProjection.Delivered(wire).SequenceEqual(new[] { "E3" }), "pair counts as one reference");
                RNAssistant.Core.ModelProtocol.ToolResultWireReadResult parsed; string error;
                AssertTrue(ToolResultHistoryReader.TryRead(result, out parsed, out error), "canonical result remains readable");
                var folded = ModelContextCompiler.CompleteOperation(result, parsed, call: call);
                var projectedFold = ModelReferenceProjection.Project(new[] { folded });
                AssertTrue(projectedFold[0].Content.Contains("\"tool_call_id\":\"T7\""), "folded receipt retains the execution reference");
                AssertTrue(ModelReferenceProjection.Delivered(projectedFold).SequenceEqual(new[] { "E3" }), "folded receipt remains deliverable without the full body");
                call.ToolCallReference = null;
                try { ModelReferenceProjection.Project(new[] { call }); throw new Exception("Canonical ID leaked without reference."); }
                catch (InvalidOperationException) { }
            }
        }

        private static void ModelReferencesSurviveCompactionWithoutBodies()
        {
            var result = ContinuityResult("canonical-read", "core.read", TerminalToolResult.Ok("Read complete."));
            result.RunId = "run-refs";
            result.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 42);
            result.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, 16);
            var call = ContinuityCall(result);
            call.RunId = result.RunId; call.ToolCallReference = result.ToolCallReference;
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
            AssertTrue(compiled.Receipt.Messages.Any(message => message.EvidenceReference?.Alias == "E16"),
                "compiled receipt retains typed evidence attribution through compaction");
            AssertTrue(ModelReferenceProjection.Delivered(projected).SequenceEqual(new[] { "E16" }),
                "folding never renumbers or drops the last terminal reference");
        }

        private static async Task ModelReferencesFreezeRepairAndFallback()
        {
            var request = NewProtocolRequest(2, true);
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = "canonical-repair", Name = "test.read" },
                "Read.", "Read source.", null, ToolResultRoles.User, FixtureCallOrigin());
            call.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 9);
            var resultMessage = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = call.ToolCallId, ToolId = call.ToolName },
                TerminalToolResult.Ok("Read complete."), ToolResultRoles.User);
            resultMessage.ToolCallReference = call.ToolCallReference;
            resultMessage.EvidenceReference = ModelReference.Create(ModelReferenceKind.Evidence, 5);
            var frozen = request.AcceptedMessages.Concat(new[] { call, resultMessage }).ToArray();
            request.AcceptedMessages = frozen;
            request.CompileRepair = notice => frozen.Concat(new[] { notice }).ToArray();
            request.ProjectReferences = ModelReferenceProjection.Project;
            var attempts = 0;
            var protocol = new ModelProtocolClient((settings, messages, options, stream, token) =>
            {
                attempts++;
                var content = string.Join("\n", messages.Select(message => message.Content));
                AssertTrue(content.Contains("\"id\":\"T9\"") && content.Contains("\"tool_call_id\":\"T9\"") && !content.Contains("canonical-repair"),
                    "every strict/repair/fallback request keeps the same aliases");
                if (attempts == 2) throw new LlmRequestException(LlmFailureKind.ResponseFormatUnsupported, "schema unsupported");
                return Task.FromResult(new LlmCompletionResult { Content = attempts == 1 ? "invalid" :
                    ModelProtocolWire.WriteAcceptedDecision(ConversationResponse.FinishAction, "Report read.", "Done.",
                        results: new[] { new ConversationResultClaim("Read.", new[] { "E5" }, ConversationAssessment.Satisfied) }, outcome: ConversationOutcome.Completed) });
            });
            var outcome = await protocol.GetResponseAsync(request, null, CancellationToken.None);
            AssertTrue(outcome.Failure == null, "reference projection allows bounded repair/fallback");
            AssertEqual(3, attempts, "one invalid completion and one explicit format fallback");
            AssertTrue(outcome.DeliveredEvidenceReferences.SequenceEqual(new[] { "E5" }), "delivered set comes from successful final request");
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
                result.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, sequence);
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

            foreach (var tool in new[] { "core.find", "core.search", "core.write" })
            {
                var navigation = AgentJsonProtocol.CreateToolResultMessage(new ToolInvocation { ToolCallId = "navigation", ToolId = tool },
                    TerminalToolResult.Ok("Navigation or outcome.", "{\"target\":\"example.txt\",\"matches\":[{\"target\":\"example.txt\",\"snippet\":\"fragment\"}]}"), ToolResultRoles.User);
                navigation.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 20);
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
            AssertContains(receiptWire, "\"tool_call_id\":\"T1\"", "omitted body keeps execution evidence");
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
                result.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 1);
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
                        new ModelContextQuery { Alias = "r1@V1" }, CancellationToken.None).Resources
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
                AssertEqual("T1", call.ToolCallReference.Alias, "first alias is persisted before any dispatch");
                var trace = store.AppendTrace(session, SessionEventTypes.LlmRequest,
                    new LlmTraceRecord { RequestId = "execution-reference", ContextMessages = new List<ContextMessagePresentation> {
                        new ContextMessagePresentation { Role = "assistant", ToolCallId = call.ToolCallId } } },
                    "{}", "application/json", summary.RunId, summary.TurnId, null);
                using (var plane = new ResourceDataPlaneService(new ResourceGatewayService()))
                {
                    var inspector = new ModelContextInspectorService(new ChatEventStoreAdapter(store), new ChatBlobStore(paths), plane);
                    AssertEqual("T1", inspector.Query(session, new ModelContextQuery { RequestEventId = trace.EventId },
                        CancellationToken.None).Operations.Single().Alias, "Inspector shares the persisted execution alias");
                }
                var stale = new ChatStore(paths).Load(session.Id);
                var expected = new[] { call.ToolCallId };
                Func<string, string[], FinishAdmission> evaluate = (reference, delivered) => ConversationFinishEvidenceAdmission.Evaluate(
                    store.ReadEvents(session.Host, session.DocumentKey, session.Id), session.Id, summary.RunId, summary.TurnId,
                    expected, new[] { new ConversationResultClaim("Read.", new[] { reference }, ConversationAssessment.Satisfied) },
                    new ChatBlobStore(paths), delivered);
                AssertTrue(call.EvidenceReference == null, "accepted call is never evidence");
                AssertEqual("unknown_evidence", evaluate("E1", new[] { "E1" }).Code, "evidence is not issued before terminal");
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
                foreach (var invalid in new[] { call.ToolCallId, "R1", "T1", "E2" })
                    AssertEqual("unknown_evidence", evaluate(invalid, new[] { invalid }).Code, "no GUID, call/resource type or guessed fallback");
                AssertTrue(evaluate("e1", new[] { "E1" }).Accepted, "evidence lookup is case insensitive");
                try { accept(stale, "stale-call"); throw new Exception("Stale writer allocated a reference."); }
                catch (ChatConcurrencyException) { }
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Unlisted write must fail." });
                try { accept(session, "uncommitted-candidate"); throw new Exception("Incomplete batch commit succeeded."); }
                catch (InvalidOperationException) { }
                var reopened = new ChatStore(paths).Load(session.Id);
                AssertEqual(1L, reopened.ToolCallReferenceSequence, "failed append never publishes a candidate alias");
                AssertEqual("T1", reopened.Messages.Single(message => message.AcceptedCallOrigin != null).ToolCallReference.Alias,
                    "restart preserves the alias");
                var ids = reopened.Messages.Select(message => message.Id).ToArray();
                reopened.Messages.Clear();
                store.Commit(reopened, ConversationCommit.RewriteHistory(ids, reopened.Messages));
                reopened = new ChatStore(paths).Load(session.Id);
                AssertEqual(1L, reopened.ToolCallReferenceSequence, "removal cannot reset the allocation high water");
                AssertEqual(1L, reopened.EvidenceReferenceSequence, "terminal evidence high water survives removal/restart");
                var next = accept(reopened, "canonical-two");
                AssertEqual("T2", next.ToolCallReference.Alias, "deleted aliases are not reused");
                next.ToolCallReference = ModelReference.Create(ModelReferenceKind.ToolCall, 9);
                try { store.Commit(reopened, new ConversationCommit(new[] { next })); throw new Exception("Alias mutation accepted."); }
                catch (InvalidOperationException) { }
                AssertEqual("T2", new ChatStore(paths).Load(session.Id).Messages.Single().ToolCallReference.Alias,
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
