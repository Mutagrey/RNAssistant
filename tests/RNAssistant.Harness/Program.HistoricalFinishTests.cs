using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class HistoricalFinishFixture
        {
            internal ChatStore Store;
            internal ChatSession Session;
            internal readonly ChatBlobStore Blobs;
            internal RunSummary Run;
            internal readonly List<string> Calls = new List<string>();
            internal HistoricalFinishFixture(AppDataPaths paths)
            {
                Store = new ChatStore(paths); Blobs = new ChatBlobStore(paths);
                Session = Store.Create("Workspace", "historical-finish", "History", "History");
            }
            internal void Start(string id)
            {
                Run = new RunSummary(id, "turn-" + id, RunLifecycle.Running, new ToolCounts(), 1, 0, "", null, null);
                Calls.Clear();
                Session.LastRun = new ChatRunRecord { RunId = id, TurnId = Run.TurnId };
                var user = new ChatMessage { Role = "user", Content = "Inspect again.", RunId = id };
                Session.Messages.Add(user);
                Store.Commit(Session, new ConversationCommit(new[] { user }));
            }
            internal ChatMessage Call(string id, ToolExecutionOutcome outcome = ToolExecutionOutcome.Ok,
                EvidenceEligibility eligibility = EvidenceEligibility.Eligible, bool external = false)
            {
                var policy = new ToolPolicySnapshot("test.read", "fixture", new ToolPolicy(ToolEffect.Read,
                    ToolVerification.None, false, false, new[] { "agent" }, evidenceEligibility: eligibility));
                var context = new ToolExecutionContext(new ToolCall(id, policy.ToolId, "{}"), policy,
                    Run.RunId, Run.TurnId, "step-" + id, DateTime.UtcNow, false, 1);
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id, Name = policy.ToolId },
                    "Inspect " + id, "Read.", null, ToolResultRoles.User, new AcceptedToolCallOrigin(context.StepId, "attempt-" + id, 0));
                call.RunId = Run.RunId;
                if (external)
                {
                    call.ArgumentPayload = PayloadRef.FromBlob(Blobs.StoreText("{}", "application/json"));
                    AcceptedCallPayloadService.Externalize(call, Blobs);
                }
                Session.Messages.Add(call); Calls.Add(id);
                Store.CommitRun(Session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ResponseAccepted,
                    Run, response: new AgentResponse(ConversationResponse.ExecuteAction, "Read.", "Inspect " + id,
                        new[] { context.Call })), new[] { call }));
                var activity = new ChatMessage { Role = "assistant", RunId = Run.RunId, ExcludeFromModelContext = true,
                    Activity = new ChatActivity { RunId = Run.RunId, StepId = context.StepId, ToolId = call.ToolName, ToolCallId = id } };
                Session.Messages.Add(activity);
                Store.CommitRun(Session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolStarted,
                    Run, toolContext: context), new ChatMessage[0], activity));
                Store.CommitRun(Session, new ConversationRunCommit(new AgentRunEvent(AgentRunEventKind.ToolCompleted,
                    Run, execution: KernelRecord(context, outcome)), new ChatMessage[0], activity));
                return call;
            }
            internal FinishAdmission Finish(string reference, string[] delivered = null,
                ConversationAssessment assessment = ConversationAssessment.Satisfied)
            {
                return ConversationFinishEvidenceAdmission.Evaluate(new ChatEventStoreAdapter(Store), Session,
                    Run.RunId, Run.TurnId, Calls, reference == null ? new ConversationResultClaim[0] :
                        new[] { new ConversationResultClaim("Inspect the requested source.", new[] { reference }, assessment) },
                    Blobs, delivered ?? (reference == null ? new string[0] : new[] { reference }));
            }
        }

        private static void ModelReferencesAdmitHistoricalEvidence()
        {
            WithTempPaths(paths =>
            {
                var f = new HistoricalFinishFixture(paths);
                f.Start("old"); f.Call("failed", ToolExecutionOutcome.Error); f.Call("verified");
                f.Start("new");
                AssertTrue(f.Finish("e2", new[] { "E2" }).Accepted, "historical delivered E survives run/turn boundaries");
                AssertEqual("undelivered_evidence", f.Finish("E2", new string[0]).Code, "known but hidden history cannot be cited");
                AssertEqual("contradictory_assessment", f.Finish("E1").Code, "historical error is not satisfied evidence");
                AssertTrue(f.Finish("E1", assessment: ConversationAssessment.Unverified).Accepted, "historical failure supports an honest limitation");
                foreach (var unknown in new[] { "T2", "R1@v1", "E999", "verified" })
                    AssertEqual("unknown_evidence", f.Finish(unknown).Code, "other reference types and invented IDs cannot become evidence");
                f.Call("service", eligibility: EvidenceEligibility.None);
                AssertTrue(f.Finish(null).Accepted, "service-only current run needs no invented E");
                AssertTrue(f.Finish("E2").Accepted, "service-only current run may cite prior user work");
                f.Store = new ChatStore(paths); f.Session = f.Store.Load(f.Session.Id);
                AssertTrue(f.Finish("E2").Accepted, "restart preserves historical admission");

                var fork = JsonConvert.DeserializeObject<ChatSession>(JsonConvert.SerializeObject(f.Session));
                fork.ParentSessionId = f.Session.Id; fork.ParentSessionRevision = f.Session.Revision;
                fork.Id = Guid.NewGuid().ToString("N"); fork.Revision = 0; fork.StorageHeadHash = null;
                f.Store.Save(fork); f.Session = f.Store.Load(fork.Id);
                f.Start("fork-run");
                AssertTrue(f.Finish("E2").Accepted, "canonical fork seed owns its copied terminal bindings");
                var remove = f.Session.Messages.Where(message => message.ToolCallId == "verified" ||
                    message.Activity?.ToolCallId == "verified").Select(message => message.Id).ToArray();
                f.Session.Messages.RemoveAll(message => remove.Contains(message.Id));
                f.Store.Commit(f.Session, ConversationCommit.RewriteHistory(remove, new ChatMessage[0]));
                AssertEqual("unknown_evidence", f.Finish("E2").Code, "deleted history membership cannot be recovered from old index facts");

                var foreign = new HistoricalFinishFixture(paths); foreign.Start("foreign");
                AssertEqual("unknown_evidence", foreign.Finish("E2").Code, "another chat's alias is not evidence");
                foreign.Call("pending", ToolExecutionOutcome.AwaitingConfirmation);
                AssertEqual("nonterminal_evidence", foreign.Finish(null).Code, "pending approval cannot finish as evidence-free work");
            });
        }

        private static void ModelReferencesReadOnlySelectedCanonicalBodies()
        {
            WithTempPaths(paths =>
            {
                var f = new HistoricalFinishFixture(paths);
                f.Start("old");
                var selected = f.Call("selected", external: true);
                var unrelated = f.Call("old-error", ToolExecutionOutcome.Error, external: true);
                f.Start("new"); var current = f.Call("current", external: true);
                File.Delete(f.Blobs.PathFor(unrelated.AcceptedCallPayload.Sha256));
                File.Delete(f.Blobs.PathFor(current.AcceptedCallPayload.Sha256));
                for (var i = 0; i < 30; i++)
                    f.Store.AppendTrace(f.Session, SessionEventTypes.AssistantChunk, new { Part = i }, null, null,
                        f.Run.RunId, f.Run.TurnId, "step");
                var reads = f.Store.FinishEvidenceEventReads;
                AssertTrue(f.Finish("E1").Accepted, "uncited current and old payloads are not revalidated at finish");
                AssertEqual(6L, f.Store.FinishEvidenceEventReads - reads, "warm finish reads only the selected/current canonical boundaries");
                var builds = f.Store.SqliteProjectionFullBuildCount;
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = SessionEventFile(paths, f.Session) + ".projection.sqlite", Pooling = false }.ToString()))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "UPDATE finish_facts SET payload=X'00' WHERE reference='E1'";
                        command.ExecuteNonQuery();
                    }
                }
                AssertTrue(f.Finish("E1").Accepted, "damaged disposable locator rebuilds from canonical events");
                AssertEqual(builds + 1, f.Store.SqliteProjectionFullBuildCount, "one canonical rebuild repairs locator damage");
                File.Delete(SessionEventFile(paths, f.Session) + ".projection.sqlite");
                AssertTrue(f.Finish("E1").Accepted, "deleted index rebuilds without reading unrelated accepted bodies");
                File.Delete(f.Blobs.PathFor(selected.AcceptedCallPayload.Sha256));
                RuntimeThrows<InvalidOperationException>(() => f.Finish("E1"));
            });
        }

        private static void ContextTransportMaterializesOnlySelectedCalls()
        {
            foreach (var storedRole in new[] { "user", "developer", "tool" })
            foreach (var requestRole in new[] { "user", "developer", "tool" })
                WithTempPaths(paths =>
                {
                    var blobs = new ChatBlobStore(paths);
                    var result = ContinuityResult("selected", "test.read", RNAssistant.Core.Tools.Contracts.ToolResult.Ok("Read."), storedRole);
                    var call = ContinuityCall(result);
                    call.ArgumentPayload = PayloadRef.FromBlob(blobs.StoreText("{}", "application/json"));
                    AcceptedCallPayloadService.Externalize(call, blobs);
                    var omittedResult = ContinuityResult("omitted", "test.read", RNAssistant.Core.Tools.Contracts.ToolResult.Error("Old error."), storedRole);
                    var omittedCall = ContinuityCall(omittedResult);
                    omittedCall.ArgumentPayload = call.ArgumentPayload;
                    AcceptedCallPayloadService.Externalize(omittedCall, blobs);
                    // Different IDs are embedded in native bodies only; ensure
                    // the omitted fallback body has its own immutable address.
                    if (omittedCall.AcceptedCallPayload.Sha256 == call.AcceptedCallPayload.Sha256)
                    {
                        omittedCall.AcceptedCallPayload = PayloadRef.FromBlob(blobs.StoreText("unselected", AcceptedCallPayloadService.ContentType));
                    }
                    File.Delete(blobs.PathFor(omittedCall.AcceptedCallPayload.Sha256));
                    var session = new ChatSession { Messages = new List<ChatMessage> { omittedCall, omittedResult, call, result } };
                    SetContinuityCheckpoint(session, omittedResult);
                    var durable = JsonConvert.SerializeObject(session.Messages);
                    var active = ContextCompactionRuntime.BuildActiveWindow(session);
                    var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                        "pack", new SkillCatalogSnapshot(null), null, 1);
                    var compiled = new ModelContextCompiler(blobs).Compile(authority, new ChatMessage[0], active, null,
                        new ToolCatalogEntry[0], new AppSettings { ToolResultRole = requestRole }, 10000,
                        workingSet: ContextWorkingSet.Restore(session, active, 0));
                    var wireCall = compiled.Messages.Single(message => message.ToolCallId == call.ToolCallId && message.Role == "assistant");
                    var wireResult = compiled.Messages.Single(message => message.ToolCallId == result.ToolCallId && message.Role != "assistant");
                    AssertEqual(requestRole, wireResult.Role, "selected result uses the current request transport");
                    AssertEqual(requestRole == "tool" ? 1 : 0, wireCall.ToolCalls.Count, "native and fallback call shape matches the result");
                    AssertTrue(ConversationResponseHistoryReader.Read(wireCall).Success, "selected accepted arguments remain a valid exact call");
                    AssertEqual(1, compiled.Receipt.HydratedPayloads, "unselected archive never hydrates an accepted body");
                    AssertEqual(durable, JsonConvert.SerializeObject(session.Messages), "request normalization cannot rewrite durable roles or payloads");
                });
        }
    }
}
