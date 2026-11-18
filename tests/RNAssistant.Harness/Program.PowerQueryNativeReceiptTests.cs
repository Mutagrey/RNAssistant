using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerQueryNativeSourcePatchReceipt()
        {
            var backend = new StubPowerQueryBackend();
            backend.Seed("NativeSource", "let value = 1 in value");
            WithNativeToolFixture(fixture => {
                const string target = "Excel query: NativeSource";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = target, ["representation"] = "source" }),
                    NativeCall(ResourceMutationToolCatalog.Patch, new JObject { ["targets"] = new JArray(new JObject {
                        ["target"] = target, ["edits"] = new JArray(new JObject { ["old"] = "value = 1", ["new"] = "value = 2" }) }) })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The exact M source was read back.", "Query source patched.",
                    "Patch the observed M source.", ResourceMutationToolCatalog.Patch));
                var sourceRead = NativeTerminal(fixture, "core.read");
                AssertEqual(ToolExecutionOutcome.Ok, sourceRead.ExecutionProgress.Outcome, "the accepted source read succeeds");
                AssertTrue(sourceRead.ResultBody.Read.RawContentIncluded, "the exact M text is declared as a delivered source body");
                var accepted = fixture.Session.Messages.Single(message => message.ToolName == ResourceMutationToolCatalog.Patch && message.AcceptedCallOrigin != null);
                AssertTrue(accepted.ResourceEvidence.Any(evidence => evidence.View == "source" && evidence.Complete &&
                    evidence.Coverage.Kind == ResourceCoverageKinds.Whole), "the delivered M source reaches the accepted patch");
                AssertEqual("let value = 2 in value", backend.Formula("NativeSource"), "the native source route changes only the observed M");
                AssertEqual(1, backend.Dispatches, "the native journal accepts one source dispatch");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "the accepted terminal retains the verified source result");
                var terminal = NativeTerminal(fixture, ResourceMutationToolCatalog.Patch);
                var receipt = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertEqual(ExcelPowerQuerySourceToolHandler.Operation, receipt.Effect.Operation, "the durable receipt belongs to the exact Power Query source owner");
                AssertEqual(receipt.Effect.EffectId, terminal.ResourceEffect.Targets.Single().Effect.EffectId,
                    "the one-target patch aggregate retains that exact owner effect");
                AssertNativeToolReceipt(fixture, ResourceMutationToolCatalog.Patch);
                AssertEqual(1, backend.Dispatches, "rebuild never repeats the source write");
                AssertEqual(0, backend.Refreshes.Count + backend.Loads.Count, "source publication never evaluates or loads M");
            }, adapter => adapter.ExcelPowerQueryBackend = backend);
        }

        private static void PowerQueryNativeStoppedResolution()
        {
            var backend = new StubPowerQueryBackend();
            backend.Seed("NativeRefresh", "42");
            var load = new ExcelPowerQueryLoadBinding { Sheet = "Data", Table = "NativeOutput", Rows = 1, Columns = 1 };
            backend.BindingsOverride = query => new ExcelPowerQueryBindingsSnapshot {
                QueryName = query, QueryIdentity = "fixture-query:" + query, BindingVersion = "native-bindings",
                IsComplete = true, Loads = new[] { load }, Connections = new ExcelPowerQueryConnectionBinding[0],
                ObservedAtUtc = DateTime.UtcNow
            };
            backend.RefreshOverride = (request, dispatch) => {
                dispatch();
                return new ExcelPowerQueryRefreshResult {
                    State = backend.Dispatches == 1 ? ExcelPowerQueryRefreshState.Unresolved : ExcelPowerQueryRefreshState.Succeeded,
                    NativeRunning = false, SourceStillCurrent = true, BindingStillCurrent = true, Binding = load,
                    ErrorCode = backend.Dispatches == 1 ? "excel_query_overflow_observation_unavailable" : null,
                    ObservedAtUtc = DateTime.UtcNow
                };
            };
            WithNativeToolFixture(fixture => {
                var arguments = new JObject { ["target"] = "Excel query: NativeRefresh", ["table"] = "NativeOutput" };
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse(ExcelPowerQueryToolCatalog.Refresh),
                    NativeCall(ExcelPowerQueryToolCatalog.Refresh, arguments),
                    NativeCall(ExcelPowerQueryToolCatalog.Refresh, (JObject)arguments.DeepClone()),
                    NativeCall("core.read", new JObject { ["target"] = "Excel query: NativeRefresh", ["representation"] = "status" }),
                    NativeCall(ExcelPowerQueryToolCatalog.Refresh, (JObject)arguments.DeepClone())
                }, () => NativeUnverifiedFinish(fixture, ExcelPowerQueryToolCatalog.Refresh, false));
                AssertEqual(2, backend.Dispatches, "only the original call and a new call after accepted status may dispatch");
                AssertEqual(2, backend.Refreshes.Count, "the unreconciled identical request never reaches the backend");
                AssertEqual(1, result.RunViewState.UnknownEffects, "the original unverified effect is not rewritten as success");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "the new invocation has its own verified result");
                var original = fixture.Session.Messages.Single(message => message.ToolName == ExcelPowerQueryToolCatalog.Refresh &&
                    message.Activity == null && message.AcceptedCallOrigin == null && message.ExecutionProgress?.Outcome == ToolExecutionOutcome.Unknown);
                AssertTrue(!original.ExecutionProgress.OpaqueActionCompleted, "positive native stop is not a normal-return opaque witness");
                var receipt = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, original.RunId, original.ToolCallId);
                AssertEqual(ResourceEffectOutcome.UnknownAfterDispatch, receipt.Effect.Outcome, "the exact original owner receipt remains Unknown");
                var resolution = fixture.Journal.ReadOperationResolution(fixture.Session.Id, original.ExecutionProgress.Operation);
                AssertEqual(ToolOperationState.StoppedUnverified, resolution.OperationObservation.State,
                    "only the independent accepted status carries the exact stopped-unverified resolution");
                AssertNativeToolReceipt(fixture, ExcelPowerQueryToolCatalog.Refresh);
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(ToolExecutionOutcome.Unknown, restored.Messages.Single(message => message.ToolCallId == original.ToolCallId &&
                    message.Activity == null && message.AcceptedCallOrigin == null).ExecutionProgress.Outcome,
                    "native context rebuild preserves the original Unknown terminal");
                AssertEqual(receipt.Position.Hash, fixture.Journal.LinkedResourceOutcome(fixture.Session.Id,
                    original.RunId, original.ToolCallId).Position.Hash, "the original immutable resource receipt survives rebuild");
                AssertEqual(2, backend.Dispatches, "reconciliation and native rebuild never replay an operation");
            }, adapter => adapter.ExcelPowerQueryBackend = backend);
        }
    }
}
