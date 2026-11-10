using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ResourceJournalPublicationAndLateOutcome()
        {
            foreach (var encrypted in new[] { false, true })
            WithTempPaths(paths => {
                var protection = encrypted ? new StorageProtector(HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256, "synthetic-resource", Enumerable.Repeat((byte)9, 16).ToArray()) : StorageProtector.None;
                var bodies = new ChatBlobStore(paths, () => protection);
                var resources = new ResourceJournal(paths, protection);
                const string conversationScope = "workspace:resource-proof";
                var conversations = new ConversationJournal(paths, conversationScope, protection);
                var scope = new ResourceAuthorityScopeId("workspace", "resource-proof");
                var target = new ResourceIdentity("rna://file/resource-proof/source");
                var file = Path.Combine(paths.Root, "source.txt"); File.WriteAllText(file, "before");
                Func<string, string, ResourceRevisionMetadata> revision = (id, text) => {
                    var payload = PayloadRef.FromBlob(bodies.StoreText(text, "text/plain"));
                    return new ResourceRevisionMetadata(new ResourceRef(target.Uri, id), payload.Sha256, payload);
                };
                Func<ResourceRevisionMetadata, ResourceRevisionView> view = value =>
                    new ResourceRevisionView(value.Reference, "text", value.ContentSha256, value.Payload, ResourceCoverage.Whole());
                var before = revision("r1", File.ReadAllText(file));
                resources.Publish("initial", new ResourcePublication(scope, target, null, before, view(before), null, null, "initial"));
                var initial = resources.Head(scope, target);
                var binding = ConversationResourceBinding.ForCall(conversationScope, "chat", "run", "write", scope, target);
                conversations.CommitCall("chat", "accepted", JournalMutationCall("write", "run", "step"), resourceBinding: binding);
                var prepared = new MutationAttempt(binding.AttemptId, scope, "core.write", target, before.Reference.Revision,
                    before.Payload, TextPatternEngine.Sha256("after"), MutationAttemptState.Prepared, DateTime.UtcNow);
                resources.Prepare("prepare", prepared); resources.MarkDispatch("dispatch", scope, binding.AttemptId);
                var dispatches = 1; File.WriteAllText(file, "after");
                var after = revision("r2", File.ReadAllText(file));
                var effect = new ResourceEffect("effect-1", "core.write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(target, ResourceImpactRelation.Exact, before: before.Reference, after: after.Reference) }, "exact read-back");
                resources.Fault = point => { throw new IOException("index response lost"); };
                var published = resources.Publish("outcome", new ResourcePublication(scope, target, initial, after, view(after), effect, binding.AttemptId, "ac-1"));
                AssertTrue(!published.ProjectionAvailable, "resource publication survives index failure");
                resources.Fault = null;

                // Simulated restart between resource publication and conversation result.
                var restarted = new ConversationJournal(paths, conversationScope, protection);
                var pending = restarted.AcceptedCall("chat", "run", "write");
                var outcome = new ResourceJournal(paths, protection).Attempt(pending.ResourceBinding.Scope, pending.ResourceBinding.AttemptId);
                AssertEqual(MutationAttemptState.Resolved, outcome.Fact.Attempt.State, "resource owner catches up its own suffix before receipt lookup");
                AssertTrue(restarted.Receipt("terminal") == null, "known effect is not yet attached to the conversation");
                var evidence = new ResourceEvidence("after", scope, after.Reference, "text", ResourceCoverage.Whole(), true, outcome.Fact.Generation, after.Payload);
                var result = new ToolResultMaterialization(ToolResult.Ok("Changed", "{\"changed\":true}", new[] { after.Reference }),
                    resourceEvidence: new[] { evidence }, resourceEffect: effect, authorityCommitId: "ac-1") {
                    ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Ok, evidenceEligibility: EvidenceEligibility.Eligible) };
                restarted.Journal.Fault = point => { if (point == "after_flush") throw new IOException("conversation acknowledgement lost"); };
                RuntimeThrows<IOException>(() => restarted.CommitResult("chat", "terminal", "write", "core.write", "step", "run", result, resourceOutcome: outcome));
                var recovered = new ConversationJournal(paths, conversationScope, protection).Receipt("terminal");
                AssertEqual("ac-1", recovered.Fact.ResourceOutcome.CommitId, "restart attaches exact resource proof without dispatch");
                AssertEqual("E1", recovered.Fact.Result.EvidenceReference.Alias, "eligible terminal receives scoped evidence");
                AssertEqual(1, dispatches, "receipt recovery never executes the effect again");
                restarted.Journal.Fault = null;
                var length = restarted.Journal.WrittenBytes;
                restarted.CommitResult("chat", "terminal", "write", "core.write", "step", "run", result, resourceOutcome: outcome);
                AssertEqual(length, restarted.Journal.WrittenBytes, "terminal retry adds no second occurrence");

                // A later invocation becomes unknown, then obtains a delayed
                // verified outcome after a newer external observation was published.
                var lateBinding = ConversationResourceBinding.ForCall(conversationScope, "chat", "cancelled-run", "late", scope, target);
                restarted.CommitCall("chat", "accepted-late", JournalMutationCall("late", "cancelled-run", "late-step"), resourceBinding: lateBinding);
                var secondHead = resources.Head(scope, target);
                resources.Prepare("prepare-late", new MutationAttempt(lateBinding.AttemptId, scope, "core.write", target,
                    after.Reference.Revision, after.Payload, TextPatternEngine.Sha256("late"), MutationAttemptState.Prepared, DateTime.UtcNow));
                resources.MarkDispatch("dispatch-late", scope, lateBinding.AttemptId);
                dispatches++; File.WriteAllText(file, "late"); var late = revision("r3", File.ReadAllText(file));
                var unknownEffect = new ResourceEffect("effect-unknown", "core.write", ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(target, ResourceImpactRelation.Exact, before: after.Reference) });
                var unknown = resources.Publish("unknown", new ResourcePublication(scope, target, secondHead, null, null, unknownEffect, lateBinding.AttemptId, "ac-unknown"));
                var unknownResult = new ToolResultMaterialization(ToolResult.Unknown("Read-back completion pending."), resourceEffect: unknownEffect,
                    authorityCommitId: "ac-unknown") { ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown) };
                var originalTerminal = restarted.CommitResult("chat", "unknown-terminal", "late", "core.write", "late-step", "cancelled-run", unknownResult, resourceOutcome: unknown);
                File.WriteAllText(file, "newer"); var newer = revision("r4", File.ReadAllText(file));
                resources.Publish("external", new ResourcePublication(scope, target, resources.Head(scope, target), newer, view(newer), null, null, "ac-external"));
                var lateEffect = new ResourceEffect("effect-late", "core.write", ResourceEffectOutcome.VerifiedChanged,
                    new[] { new ResourceImpact(target, ResourceImpactRelation.Exact, before: after.Reference, after: late.Reference) }, "delayed exact read-back");
                var lateReceipt = resources.Publish("late-known", new ResourcePublication(scope, target, secondHead, late, view(late), lateEffect, lateBinding.AttemptId, "ac-late"));
                AssertTrue(!lateReceipt.Fact.AdvancesHead, "late result cannot overwrite a newer head");
                AssertEqual("r4", resources.Head(scope, target).Revision.Revision, "newer current source survives delayed completion");
                restarted.LinkResourceOutcome("chat", "cancelled-run", "late", lateReceipt);
                var linkBytes = restarted.Journal.WrittenBytes;
                restarted.LinkResourceOutcome("chat", "cancelled-run", "late", lateReceipt);
                AssertEqual(linkBytes, restarted.Journal.WrittenBytes, "late receipt linkage is idempotent");
                AssertEqual("ac-late", restarted.LinkedResourceOutcome("chat", "cancelled-run", "late").Fact.Publication.CommitId, "late exact proof remains addressable");
                AssertEqual(originalTerminal.Position.Hash, restarted.Receipt("unknown-terminal").Position.Hash, "late proof never rewrites the original terminal");
                AssertEqual(ToolExecutionOutcome.Unknown, restarted.Receipt("unknown-terminal").Fact.Result.ExecutionProgress.Outcome, "linkage does not invent a resumed/completed kernel run");
                RuntimeThrows<InvalidDataException>(() => restarted.LinkResourceOutcome("chat", "run", "write", lateReceipt));
                AssertEqual(2, dispatches, "late linkage and retries do not replay either write");

                var canonical = Directory.EnumerateFiles(paths.Root, "*.jsonl", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
                restarted.Rebuild();
                foreach (var pair in canonical) AssertTrue(pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)), "combined resource/conversation rebuild preserves bytes");
                AssertEqual("ac-late", restarted.LinkedResourceOutcome("chat", "cancelled-run", "late").Fact.Publication.CommitId, "cross-owner linkage rebuilds without sidecar authority");
                var reads = resources.Journal.ReadRecords;
                AssertEqual(late.Payload.Sha256, resources.Revision(scope, late.Reference).Payload.Sha256, "historical revision is retained without selecting it");
                AssertTrue(resources.Journal.ReadRecords - reads <= 2, "revision query reads boundary and exact locator only");
                reads = resources.Journal.ReadRecords;
                AssertEqual(late.Payload.Sha256, resources.View(scope, late.Reference, "text", ResourceCoverage.Whole()).Payload.Sha256, "exact view lookup");
                AssertTrue(resources.Journal.ReadRecords - reads <= 2, "view query does not hydrate/archive-scan bodies");
                AssertEqual("r4", resources.Head(scope, target).Revision.Revision, "historical lookup does not roll back current authority");
            });
        }

        private static ChatMessage JournalMutationCall(string id, string run, string step)
        {
            var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id, Name = "core.write",
                Arguments = new Dictionary<string, object> { ["target"] = "source.txt", ["content"] = "new source" } },
                "", "Replace the exact delivered source.", null, ToolResultRoles.User, new AcceptedToolCallOrigin(step, "model:" + id, 0, 1));
            call.RunId = run; return call;
        }
    }
}
