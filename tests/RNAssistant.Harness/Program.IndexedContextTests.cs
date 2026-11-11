using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Office.Services;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ConversationContextOperation AppendIndexedContext(ConversationJournal journal, string chat, string id,
            string run, ToolResultMaterialization materialized, string tool = "core.read", JObject arguments = null,
            ChatBlobStore externalize = null)
        {
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id, Name = tool,
                Arguments = (arguments ?? new JObject { ["target"] = "source.txt" }).ToObject<Dictionary<string, object>>() }, "", "Inspect the exact source.", null,
                ToolResultRoles.User, new AcceptedToolCallOrigin("step:" + id, "attempt:" + id, 0, 1));
            call.RunId = run;
            if (externalize != null)
            {
                call.ArgumentPayload = PayloadRef.FromBlob(externalize.StoreText(arguments.ToString(Formatting.None), "application/json"));
                AcceptedCallPayloadService.Externalize(call, externalize);
            }
            journal.CommitCall(chat, "accept:" + id, call);
            var receipt = journal.CommitResult(chat, "result:" + id, id, tool, "step:" + id, run, materialized);
            call.ToolCallReference = receipt.Fact.Result.ToolCallReference;
            return new ConversationContextOperation(call, receipt);
        }

        private static ToolResultMaterialization IndexedNoise()
        { return new ToolResultMaterialization(ToolResult.Ok("Completed.")) { ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok) }; }

        private static ModelAuthoritySnapshot IndexedAuthority(ResourceAuthorityScopeId scope = null, ResourceRef resource = null)
        {
            return new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(resource == null ? new ResourceAuthoritySnapshot[0] :
                new[] { new ResourceAuthoritySnapshot(scope, 1, null, 0, new[] { ResourceHeadState.Known(resource, 1) }) }),
                "pack", new SkillCatalogSnapshot(null), null, 1);
        }

        private static void IndexedContextRequirementsAndReplay()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "workspace:requirements");
                var payloads = new ChatBlobStore(paths);
                var unknown = new ToolResultMaterialization(ToolResult.Ok("Backend returned; effect unverified.")) {
                    ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, opaqueAction: true) };
                var args = new JObject { ["value"] = 7, ["target"] = "document" };
                var first = AppendIndexedContext(journal, "chat", "unknown0", "old", unknown, "write", args, payloads);
                for (var i = 1; i < 140; i++) AppendIndexedContext(journal, "chat", "unknown" + i, "old", unknown,
                    "write", new JObject { ["target"] = "document", ["value"] = i + 7 });
                // A later opaque normal return must not hide the older repeat guard.
                AppendIndexedContext(journal, "chat", "completed", "old",
                    new ToolResultMaterialization(ToolResult.Ok("Returned normally.")) {
                        ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, opaqueAction: true, opaqueActionCompleted: true) }, "write", args);
                var last = AppendIndexedContext(journal, "chat", "active", "current", IndexedNoise());
                var restarted = new ConversationJournal(paths, "workspace:requirements");
                var reads = restarted.Journal.ReadRecords;
                var context = restarted.RestoreContext("chat", "current", 1, 0);
                AssertTrue(context.WorkingSet.Requirements.UnknownEffects, "unknown obligations survive without eager bodies");
                AssertEqual(0, context.WorkingSet.Operations.Count, "no all-unknown prompt inventory");
                AssertTrue(restarted.Journal.ReadRecords - reads <= 3, "more than 128 obligations do not expand preparation reads");
                var compiler = new ModelContextCompiler(payloads);
                var prepared = compiler.Compile(IndexedAuthority(), new ChatMessage[0], context.Recent, null,
                    new ToolCatalogEntry[0], new AppSettings(), 3000, workingSet: context.WorkingSet);
                AssertContains(string.Join("\n", prepared.Messages.Select(item => item.Content)), "CONTEXT_REQUIREMENTS", "fixed-size mandatory warning enters actual request");
                AssertEqual(0, prepared.DeliveredObservations.Count, "a summary grants no source authority");
                RuntimeThrows<PromptBudgetExceededException>(() => compiler.Compile(IndexedAuthority(), new ChatMessage[0], context.Recent, null,
                    new ToolCatalogEntry[0], new AppSettings(), 1, workingSet: context.WorkingSet));

                var visited = new HashSet<string>(); var before = long.MaxValue;
                while (true)
                {
                    var page = restarted.ReadContextRequirements("chat", "current", before, 17);
                    if (page.Count == 0) break;
                    AssertTrue(page.Count <= 17, "bounded detail pages");
                    foreach (var item in page) AssertTrue(visited.Add(item.Result.OperationId), "keyset page never repeats a requirement");
                    before = page.Last().Result.Position.Sequence;
                }
                AssertEqual(141, visited.Count, "every unknown remains addressable, including normally returned opaque effects");
                var proposal = new ToolCall("new-id", "write", "{\"target\":\"document\",\"value\":7}");
                var guard = restarted.FindContextReplayGuard("chat", proposal);
                AssertEqual(first.Result.OperationId, guard.Result.OperationId, "canonical argument signature ignores property order and new call id");
                var tracker = new AgentProgressTracker();
                tracker.Observe(ConversationResponseHistoryReader.Read(AcceptedCallPayloadService.Hydrate(guard.Call, payloads)).Response.ToolCalls[0],
                    guard.Result.Fact.Result.ExecutionProgress, "unknown", null, null);
                string reason, message; int delay;
                AssertTrue(!tracker.CanDispatch(proposal, out reason, out message, out delay), "only the unsafe exact repeat is blocked");
                AssertEqual("repeated_unknown_tool_call", reason, "existing guard policy retained");
                AssertTrue(tracker.CanDispatch(new ToolCall("read", "core.read", "{}"), out reason, out message, out delay), "unrelated reads remain available");
                AssertTrue(restarted.FindContextReplayGuard("chat", new ToolCall("other", "write", "{\"value\":999}")) == null, "different operation is not blocked by long history");
                AssertTrue(restarted.FindContextReplayGuard("foreign", proposal) == null &&
                    restarted.ReadContextOperation("foreign", first.Result.OperationId) == null, "chat scope cannot leak a guard or detail");
                AssertTrue(new ConversationJournal(paths, "workspace:other").FindContextReplayGuard("chat", proposal) == null, "stream isolation");

                // Canonical unknown + lost index acknowledgement, then schema repair.
                var faultCall = JournalReadCall("fault", "current", "step"); journal.CommitCall("chat", "accept:fault", faultCall);
                journal.Fault = stage => { throw new IOException("index fault"); };
                var fault = journal.CommitResult("chat", "result:fault", "fault", "core.read", "step", "current", unknown);
                AssertTrue(!fault.ProjectionAvailable, "canonical terminal survives projection failure"); journal.Fault = null;
                AssertTrue(restarted.FindContextReplayGuard("chat", new ToolCall("retry", "core.read", "{\"target\":\"source.txt\"}")) != null, "suffix catch-up restores action guard");
                var bytes = File.ReadAllBytes(journal.Journal.SegmentPath(1));
                using (var db = JournalIndex.Open(paths)) using (var command = db.CreateCommand())
                { command.CommandText = "PRAGMA user_version=8"; command.ExecuteNonQuery(); }
                var repaired = new ConversationJournal(paths, "workspace:requirements");
                AssertEqual(first.Result.OperationId, repaired.FindContextReplayGuard("chat", proposal).Result.OperationId, "rebuild restores externalized call signature");
                AssertTrue(bytes.SequenceEqual(File.ReadAllBytes(journal.Journal.SegmentPath(1))), "repair preserves canonical history bytes");
                visited.Clear(); before = long.MaxValue;
                while (true)
                {
                    var page = repaired.ReadContextRequirements("chat", "current", before, 64);
                    if (page.Count == 0) break;
                    foreach (var item in page) AssertTrue(visited.Add(item.Result.OperationId), "repaired pages remain unique");
                    before = page.Last().Result.Position.Sequence;
                }
                AssertEqual(142, visited.Count, "all requirements survive streaming rebuild");
                // Production archive path must also recognize runtime Unknown behind wire ok.
                var session = new ChatSession { Id = "chat" };
                session.Messages.Add(first.Call); session.Messages.Add(first.Result.Fact.Result);
                session.Messages.Add(last.Call); session.Messages.Add(last.Result.Fact.Result); SetContinuityCheckpoint(session, last.Result.Fact.Result);
                var legacy = ContextWorkingSet.Restore(session, new[] { last.Call, last.Result.Fact.Result }, 0);
                AssertTrue(legacy.Operations.Any(item => item.Required && item.Source.ToolCallId == "unknown0"), "opaque runtime unknown cannot silently disappear from production context");
            });
        }

        private static void IndexedContextRecoveryMatchesContracts()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "workspace:recovery");
                var scope = new ResourceAuthorityScopeId("workspace", "recovery");
                var reference = new ResourceRef("rna://file/recovery/source", "r1");
                var changed = new ResourceRef(reference.Uri, "r2");
                var recoveries = new[] {
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan),
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan, reference.Identity, "text", "source.txt"),
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired, reference.Identity, "text", "source.txt", readScope: ToolRecoveryReadScope.Whole),
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.RefreshRequired, reference.Identity, "text", "source.txt", readScope: ToolRecoveryReadScope.RelevantLines),
                    new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.ChangeRequired, semanticTarget: "source.txt", changeDependencies: new[] { reference }),
                    new ToolRecoveryContract(ToolFailureKind.BusyNoEffect, ToolRetryPolicy.RetryLater) };
                var observations = new[] {
                    new ResourceEvidence("whole", scope, reference, "text", ResourceCoverage.Whole(), true, 1),
                    new ResourceEvidence("partial", scope, reference, "text", new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 3), false, 1),
                    new ResourceEvidence("foreign", scope, new ResourceRef("rna://file/recovery/other", "r1"), "text", ResourceCoverage.Whole(), true, 1) };
                var effects = new[] {
                    new ResourceEffect("changed", "write", ResourceEffectOutcome.VerifiedChanged, new[] { new ResourceImpact(reference.Identity, ResourceImpactRelation.Exact, before: reference, after: changed) }),
                    new ResourceEffect("drift", "write", ResourceEffectOutcome.ExternalDriftObserved, new[] { new ResourceImpact(reference.Identity, ResourceImpactRelation.Exact, before: reference, after: changed) }),
                    new ResourceEffect("noop", "write", ResourceEffectOutcome.VerifiedNoChange, new[] { new ResourceImpact(reference.Identity, ResourceImpactRelation.Exact, before: reference, after: reference) }),
                    new ResourceEffect("partial", "write", ResourceEffectOutcome.VerifiedChanged, new[] { new ResourceImpact(reference.Identity, ResourceImpactRelation.Exact, new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 3), reference, changed) }) };
                var index = 0;
                foreach (var recovery in recoveries)
                foreach (var candidate in observations.Select(e => new ToolResultMaterialization(ToolResult.Ok("Read."), resourceEvidence: new[] { e }))
                    .Concat(effects.Select(e => new ToolResultMaterialization(ToolResult.Ok("Changed."), resourceEffect: e))))
                {
                    var chat = "chat" + index++;
                    candidate.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok);
                    // Earlier success cannot satisfy the subsequent failure.
                    AppendIndexedContext(journal, chat, "before:" + chat, "old", candidate);
                    var failed = AppendIndexedContext(journal, chat, "error:" + chat, "run",
                        new ToolResultMaterialization(ToolResult.Error("Recovery required.")) { ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error, recovery) });
                    AssertTrue(journal.RestoreContext(chat, "run", 0, 0).WorkingSet.Requirements.RecoveryRequired, "earlier evidence is not future recovery");
                    AppendIndexedContext(journal, "other", "foreign:" + chat, "run", candidate);
                    AssertTrue(journal.RestoreContext(chat, "run", 0, 0).WorkingSet.Requirements.RecoveryRequired, "another chat cannot resolve this recovery");
                    candidate.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error);
                    AppendIndexedContext(journal, chat, "failed-read:" + chat, "different-run", candidate);
                    AssertTrue(journal.RestoreContext(chat, "run", 0, 0).WorkingSet.Requirements.RecoveryRequired, "a failed read cannot satisfy recovery");
                    candidate.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok);
                    AppendIndexedContext(journal, chat, "later:" + chat, "different-run", candidate);
                    var pending = journal.ReadContextRequirements(chat, "run", long.MaxValue, 8).Any(item => item.Result.OperationId == failed.Result.OperationId);
                    AssertEqual(!recovery.IsSatisfiedBy(candidate.ResourceEvidence, candidate.ResourceEffect), pending, "indexed reduction agrees with typed recovery contract");
                    AssertEqual(pending, journal.ReadContextOperation(chat, failed.Result.OperationId).RecoveryRequired, "addressed detail distinguishes terminal history from unresolved recovery");
                }
                using (var db = JournalIndex.Open(paths)) using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM context_dependency d LEFT JOIN context_required r ON r.stream=d.stream AND r.operation=d.operation WHERE r.operation IS NULL OR r.retry=0";
                    AssertEqual(0L, Convert.ToInt64(command.ExecuteScalar()), "resolved dependencies do not accumulate orphan rows");
                }
            });
        }

        private static void IndexedContextRestartCost()
        {
            WithTempPaths(paths => {
                var journal = new ConversationJournal(paths, "workspace:context-cost");
                var payloads = new ChatBlobStore(paths); var source = new string('s', 12000) + "SOURCE_END";
                var scope = new ResourceAuthorityScopeId("workspace", "context-cost");
                var reference = new ResourceRef("rna://file/context-cost/source", "r1");
                var history = new List<ChatMessage>();
                Action<ConversationContextOperation> remember = item => { history.Add(item.Call); history.Add(item.Result.Fact.Result); };
                foreach (var whole in new[] { true, false })
                {
                    var text = whole ? source : source.Substring(0, 100);
                    var coverage = whole ? ResourceCoverage.Whole() : new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 100);
                    var payload = PayloadRef.FromBlob(payloads.StoreText(text, "text/plain"));
                    remember(AppendIndexedContext(journal, "chat", whole ? "whole" : "partial", "seed",
                        new ToolResultMaterialization(ToolResult.OkRead("Read source.", new ResourceReadProjection {
                            Target = "source.txt", Type = "file", Scope = "workspace", Representation = "text", Text = text,
                            Complete = whole, SourceComplete = whole, Coverage = coverage, RawContentIncluded = true }, new[] { reference }),
                            resourceEvidence: new[] { new ResourceEvidence(whole ? "whole" : "partial", scope, reference, "text", coverage, whole, 1, payload) }) {
                                ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok, evidenceEligibility: EvidenceEligibility.Eligible) }));
                }
                var size = 0; string expectedWire = null;
                var sizes = Environment.GetEnvironmentVariable("RNA_INDEXED_CONTEXT_COST") == "1" ? new[] { 100, 1000, 10000 } : new[] { 100, 1000 };
                foreach (var count in sizes)
                {
                    while (size < count) { size++; remember(AppendIndexedContext(journal, "chat", "noise" + size, "noise", IndexedNoise())); }
                    // Reuse the same active run regardless of archive size.
                    var session = new ChatSession { Id = "chat" }; session.Messages.AddRange(history);
                    SetContinuityCheckpoint(session, history.Last());
                    var active = history.Skip(history.Count - 2).ToArray();
                    var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                    var legacy = ContextWorkingSet.Restore(session, active, 6000);
                    var oldMs = watch.Elapsed.TotalMilliseconds; var oldAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                    var restarted = new ConversationJournal(paths, "workspace:context-cost");
                    before = GC.GetAllocatedBytesForCurrentThread(); var cpu = Process.GetCurrentProcess().TotalProcessorTime; watch.Restart();
                    var context = restarted.RestoreContext("chat", "noise", 1, 6000);
                    var newMs = watch.Elapsed.TotalMilliseconds; var newAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
                    var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                    AssertEqual(0, context.WorkingSet.VisitedMessages, "no history collection visited by indexed preparation");
                    AssertTrue(restarted.Journal.ReadRecords <= 7, "fixed source and active set have fixed metadata reads");
                    AssertEqual(2, context.WorkingSet.Bodies.Count, "whole and partial observations coexist");
                    var compiler = new ModelContextCompiler(payloads, projection: RNAssistant.Office.Services.ModelToolResultProjection.Instance);
                    var actual = compiler.Compile(IndexedAuthority(scope, reference), new ChatMessage[0], context.Recent, null,
                        new ToolCatalogEntry[0], new AppSettings(), 16000, workingSet: context.WorkingSet);
                    var old = compiler.Compile(IndexedAuthority(scope, reference), new ChatMessage[0], active, null,
                        new ToolCatalogEntry[0], new AppSettings(), 16000, workingSet: legacy);
                    var wire = string.Join("\n", actual.Messages.Select(item => item.Content));
                    AssertEqual(string.Join("\n", old.Messages.Select(item => item.Content)), wire, "same retained bytes and actual prompt as archive selection");
                    AssertContains(wire, "SOURCE_END", "selected stored source is delivered without another tool read");
                    AssertTrue(actual.DeliveredObservations.Any(item => item.Complete && item.Coverage.Kind == ResourceCoverageKinds.Whole), "actual delivery alone supplies whole authority");
                    AssertTrue(actual.DeliveredObservations.Any(item => !item.Complete && item.Coverage.Kind == ResourceCoverageKinds.CharacterRange), "partial observation keeps its coverage");
                    // Active call IDs change with history size; compare retained source membership instead of IDs.
                    var retained = string.Join("\n", context.WorkingSet.Bodies.Select(item => item.Source.ResultPayload.Sha256));
                    if (expectedWire == null) expectedWire = retained; else AssertEqual(expectedWire, retained, "same retained payload set at all H");
                    Console.WriteLine("INDEXED_CONTEXT " + JsonConvert.SerializeObject(new { historyPairs = count, oldVisits = legacy.VisitedMessages,
                        oldAllocatedBytes = oldAlloc, oldMs, records = restarted.Journal.ReadRecords, readBytes = restarted.Journal.ReadBytes,
                        allocatedBytes = newAlloc, cpuMs, elapsedMs = newMs }));
                }
            });
        }
    }
}
