using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void NativeToolsOpaqueCompletionFences()
        {
            foreach (var hadCompletedInvocation in new[] { false, true }) WithNativeToolFixture(fixture => {
                if (hadCompletedInvocation)
                {
                    RunNativeToolScript(fixture, new[] { LoadToolSchemaResponse(VbaToolCatalog.RunMacro),
                        NativeCall(VbaToolCatalog.RunMacro, new JObject { ["macroName"] = "Module1.MigrateApiKey", ["arguments"] = new JArray("first") })
                    }, () => NativeUnverifiedFinish(fixture, VbaToolCatalog.RunMacro, true));
                    var result = NativeTerminal(fixture, VbaToolCatalog.RunMacro);
                    var outcome = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, result.RunId, result.ToolCallId);
                    var completion = fixture.Journal.CompletedOpaqueInvocation(outcome);
                    AssertTrue(completion != null, "normal return has an exact retained completion witness");
                    fixture.Journal.VerifyOpaqueCompletion(completion);
                    var accepted = fixture.Journal.AcceptedCall(fixture.Session.Id, result.RunId, result.ToolCallId);
                    var foreignTerminal = new ResourceInvocationCompletion(completion.Origin, completion.Outcome, accepted.Position);
                    RuntimeThrows<InvalidDataException>(() => fixture.Journal.VerifyOpaqueCompletion(foreignTerminal));
                    AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro), "invalid witness validation invokes no macro");
                }

                // A later uncertain producer deliberately has the same Unknown
                // state/cause as the earlier head. Only its canonical producer
                // changed; the earlier normal-return terminal cannot release it.
                var scope = ResourceAuthorityScopeId.Document(new DocumentAuthorityId(fixture.Session.DocumentAuthorityId));
                var root = new ResourceIdentity(ResourceUri.Create("document", scope.Id, "root"));
                var snapshot = fixture.Resources.Capture(scope);
                var before = snapshot.GetHead(root);
                var effect = new ResourceEffect("uncertain_external", "external.action", ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(root, ResourceImpactRelation.Subtree) });
                var newer = ResourceAuthorityCommit.Create(scope, snapshot.Generation, effect,
                    new[] { new ResourceHeadChange(root, before,
                        ResourceHeadState.Unknown(root, snapshot.Generation + 1, before?.Cause ?? "unconfirmed invocation")) },
                    AuthorityCommitReason.MutationEffect);
                fixture.Resources.Publish(newer);
                var dispatches = fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro);
                RunNativeToolScript(fixture, new[] { LoadToolSchemaResponse(VbaToolCatalog.RunMacro),
                    NativeCall(VbaToolCatalog.RunMacro, new JObject { ["macroName"] = "Module1.MigrateApiKey", ["arguments"] = new JArray("fresh-different-call") })
                }, () => ModelProtocolWire.WriteAcceptedDecision("finish", "The new invocation was rejected before dispatch.",
                    "The current possible effect needs inspection before another invocation.", outcome: ConversationOutcome.UnableToComplete,
                    results: new[] { new ConversationResultClaim("Run the new invocation.", new string[0], ConversationAssessment.Unverified) }));
                var rejected = NativeTerminal(fixture, VbaToolCatalog.RunMacro);
                AssertEqual(ToolExecutionOutcome.Error, rejected.ExecutionProgress.Outcome,
                    "a fresh signature cannot bypass an unresolved current root");
                AssertTrue(rejected.AuthorityCommitId == null, "rejection cannot publish a successful or synthetic resource receipt");
                AssertEqual(dispatches, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro),
                    "missing/currently mismatched opaque completion stops before source dispatch");
                AssertEqual(newer.CommitId, fixture.Resources.Capture(scope).HighWaterCommitId,
                    "rejected preparation does not replace the current unknown producer");
                fixture.Journal.Rebuild();
                AssertEqual(dispatches, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro), "rebuild cannot replay the rejected invocation");
            });
        }

        private static void NativeToolsPatchPublicationFailure()
        {
            foreach (var failAfter in new[] { "resource.revision", "resource.authority" }) WithNativeToolFixture(fixture => {
                fixture.Adapter.SetVbaModule("UncertainPatch", "Sub Main()\nDebug.Print \"before\"\nEnd Sub", "StdModule");
                var injected = false;
                fixture.Adapter.BeforeVbaBackendCall = call => {
                    if (call.Operation != FakeVbaOperation.ReplaceModule) return;
                    fixture.Resources.Journal.Fault = stage => {
                        if (stage != "after_flush") return;
                        string line;
                        using (var stream = new FileStream(fixture.Resources.Journal.SegmentPath(1), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream))
                            line = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Last();
                        if (JsonConvert.DeserializeObject<JournalRecord>(line).Kind != failAfter) return;
                        injected = true; fixture.Resources.Journal.Fault = null;
                        throw new IOException("Lost mutation publication acknowledgement.");
                    };
                };
                var target = "VBA module: UncertainPatch";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = target }),
                    NativeCall(ResourceMutationToolCatalog.Patch, PatchArguments(target, EditSet(Edit("before", "after"))))
                }, () => NativeUnverifiedFinish(fixture, ResourceMutationToolCatalog.Patch, false));
                AssertTrue(injected, "fault occurs after the requested canonical publication boundary");
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "uncertain patch is dispatched only once");
                AssertContains(fixture.Adapter.GetVbaModuleCode("UncertainPatch"), "after", "the fault follows a real source change");
                var terminal = NativeTerminal(fixture, ResourceMutationToolCatalog.Patch);
                AssertEqual(ToolExecutionOutcome.Unknown, terminal.ExecutionProgress.Outcome, "publication acknowledgement loss retains honest Unknown");
                AssertEqual(0, result.RunViewState.VerifiedWrites, "the retained report cannot become verified success");
                AssertTrue(!terminal.ExecutionProgress.OpaqueActionCompleted, "a failed managed publication is not a normal opaque return");
                AssertEqual(failAfter == "resource.authority", terminal.AuthorityCommitId != null,
                    "only an actually committed owner outcome is linked to the Unknown report");
                fixture.Journal.Rebuild();
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "rebuild does not replay an uncertain patch");
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(1, restored.LastRun.KernelState.Summary.ToolCounts.WriteUnknown, "replay retains the unresolved patch classification");
            });
        }

        private static void NativeToolsPatchBatchRejected()
        {
            WithNativeToolFixture(fixture => {
                var published = fixture.Executor.GetControllerTools().Single(entry => entry.Id == ResourceMutationToolCatalog.Patch);
                AssertEqual(ResourcePatch.MaximumTargets,
                    (int)JObject.Parse(published.ArgumentSchemaJson)["properties"]["targets"]["maxItems"],
                    "the native published schema advertises the shared patch bound");
                const string source = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                fixture.Adapter.SetVbaModule("FirstPatchTarget", source, "StdModule");
                fixture.Adapter.SetVbaModule("SecondPatchTarget", source, "StdModule");
                var stale = ResourceMutationToolCatalog.GetTools().Single(entry => entry.Id == ResourceMutationToolCatalog.Patch);
                var runtime = fixture.Executor.CreateNativeRuntime(fixture.Session, new[] { stale }, fixture.Settings, ChatModes.Agent, false);
                var args = new JObject { ["targets"] = PatchTargets(Enumerable.Range(0, ResourcePatch.MaximumTargets + 1)
                    .Select(index => PatchSection("VBA module: Limit" + index, EditSet(Edit("before", "after")))).ToArray()) };
                var call = new ToolCall("native-multiple-targets", stale.Id, args.ToString(Formatting.None));
                var result = runtime.ExecuteAsync(new ToolExecutionContext(call, runtime.Describe(call),
                    "batch-rejection-run", "batch-rejection-turn", "batch-rejection-step", DateTime.UtcNow, false, 5),
                    System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Error, result.Outcome, "multi-target native call receives a typed no-effect refusal");
                AssertContains(result.Result.DataJson, "more items than maxItems", "the refusal comes from the shared patch schema bound");
                AssertTrue(!result.MayHaveDispatched, "batch refusal precedes every dispatch boundary");
                AssertEqual(ToolFailureKind.RejectedNoEffect, result.Recovery.FailureKind, "no partial effect is reported as an ordinary retry");
                AssertEqual(0, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "neither source is written");
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("FirstPatchTarget"), "first target remains unchanged");
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("SecondPatchTarget"), "second target remains unchanged");
            });
        }
    }
}
