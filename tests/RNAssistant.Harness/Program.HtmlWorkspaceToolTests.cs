using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void HtmlWorkspaceUsesExactNativeOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var definitions = executor.GetControllerTools()
                        .Where(tool => HtmlWorkspaceToolCatalog.Owns(tool.Id))
                        .ToList();
                    AssertEqual(8, definitions.Count,
                        "complete HTML workspace family is registered");
                    foreach (var definition in definitions)
                    {
                        AssertTrue(definition.RuntimePolicy != null,
                            definition.Id + " owns an exact typed policy");
                        AssertEqual(definition.Id ==
                                HtmlWorkspaceToolCatalog.InspectWorkspaceToolId
                                    ? ToolEffect.Read : ToolEffect.Write,
                            definition.RuntimePolicy.Effect,
                            definition.Id + " effect policy");
                        AssertEqual(definition.Id ==
                                HtmlWorkspaceToolCatalog.InspectWorkspaceToolId
                                    ? ToolVerification.None :
                                        ToolVerification.Tool,
                            definition.RuntimePolicy.Verification,
                            definition.Id + " verification policy");
                        AssertEqual("agent",
                            string.Join(",", definition.RuntimePolicy.AllowedModes),
                            definition.Id + " is Agent-only");
                    }

                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);
                    foreach (var definition in definitions)
                    {
                        AssertTrue(runtime.Describe(new ToolCall(
                                "exact_" + definition.Name,
                                definition.Id, "{}")) != null,
                            definition.Id + " has an exact native binding");
                        AssertTrue(runtime.Describe(new ToolCall(
                                "alias_" + definition.Name,
                                definition.Id.ToUpperInvariant(), "{}")) == null,
                            definition.Id + " has no case alias");
                    }

                    var upsert = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.UpsertToolId,
                        new JObject
                        {
                            ["resourceType"] = "file",
                            ["name"] = "index.html",
                            ["content"] = "<main>native</main>",
                            ["setActive"] = true
                        });
                    AssertEqual(ToolExecutionOutcome.Ok, upsert.Outcome,
                        "native HTML upsert succeeds");
                    AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                        upsert.Evidence.Dispatch,
                        "HTML upsert marks dispatch before session mutation");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        upsert.Evidence.Effect,
                        "HTML upsert reports verified change");
                    AssertTrue(upsert.Result.Resources.Any(reference =>
                            reference.Uri.IndexOf("/artifact/",
                                StringComparison.Ordinal) >= 0),
                        "HTML mutation exposes the exact revision resource");

                    var unchanged = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                        new JObject
                        {
                            ["name"] = "index.html",
                            ["patch"] = new JArray(new JObject
                            {
                                ["op"] = "replace",
                                ["find"] = "native",
                                ["text"] = "native"
                            })
                        });
                    AssertEqual(ToolExecutionOutcome.Ok, unchanged.Outcome,
                        "no-change HTML patch succeeds");
                    AssertEqual(ToolDispatchEvidence.NotDispatched,
                        unchanged.Evidence.Dispatch,
                        "no-change HTML patch stays before dispatch");
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                        unchanged.Evidence.Effect,
                        "no-change HTML patch is explicit");

                    adapter.ExcelBackendCalls.Clear();
                    var bind = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject
                        {
                            ["dataName"] = "sales",
                            ["sourceTool"] = "excel.read_range",
                            ["sourceArguments"] = new JObject
                            {
                                ["sheet"] = "Data",
                                ["address"] = "A1:B4",
                                ["content"] = "values"
                            }
                        });
                    AssertEqual(ToolExecutionOutcome.Ok, bind.Outcome,
                        "native HTML bind succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        bind.Evidence.Effect,
                        "native HTML bind verifies its workspace revision");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                        "HTML bind reaches one typed bound read backend");
                    AssertEqual(0, adapter.Executed.Count(command =>
                            HtmlWorkspaceToolCatalog.Owns(command.ToolId)),
                        "HTML family never reaches generic host dispatch");
                });
        }

        private static ToolExecutionRecord ExecuteHtmlNative(
            RNAssistant.Office.Runtime.NativeToolRuntimeAdapter runtime,
            string toolId,
            JObject arguments)
        {
            var call = new ToolCall(Guid.NewGuid().ToString("N"), toolId,
                (arguments ?? new JObject()).ToString(Formatting.None));
            var policy = runtime.Describe(call);
            return runtime.ExecuteAsync(new ToolExecutionContext(
                    call, policy, "html_run", "html_turn",
                    Guid.NewGuid().ToString("N"), DateTime.UtcNow,
                    false, 4), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }
}
