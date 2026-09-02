using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
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
                    AssertEqual(7, definitions.Count,
                        "semantic HTML workspace family is registered");
                    foreach (var definition in definitions)
                    {
                        AssertTrue(definition.Policy != null,
                            definition.Id + " owns an exact typed policy");
                        AssertEqual(ToolEffect.Write, definition.Policy.Effect,
                            definition.Id + " effect policy");
                        AssertEqual(ToolVerification.Tool,
                            definition.Policy.Verification,
                            definition.Id + " verification policy");
                        AssertEqual("agent",
                            string.Join(",", definition.Policy.AllowedModes),
                            definition.Id + " is Agent-only");
                    }
                    var writeFileSchema = JObject.Parse(definitions.Single(definition =>
                        definition.Id == HtmlWorkspaceToolCatalog.WriteFileToolId).ArgumentSchemaJson);
                    AssertContains((string)writeFileSchema["properties"]["content"]["description"],
                        "one literal source backslash",
                        "HTML write schema defines the outer JSON escaping boundary");

                    var allTools = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session, allTools, new AppSettings(), "agent", false);
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
                        HtmlWorkspaceToolCatalog.WriteFileToolId,
                        new JObject
                        {
                            ["path"] = "index.html",
                            ["content"] = "<main>native</main>"
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
                    AssertTrue(JObject.Parse(upsert.Result.DataJson)
                            ["preflight"] != null,
                        "HTML write runs static preflight automatically");

                    var projectedInvocation = new ToolInvocation
                    {
                        ToolCallId = "html_projection",
                        ToolId = HtmlWorkspaceToolCatalog.WriteFileToolId
                    };
                    var projected = ModelToolResultProjection.Project(
                        AgentJsonProtocol.CreateToolResultMessage(
                            projectedInvocation, upsert.Result,
                            ToolResultRoles.Tool));
                    AssertContains(projected.Content, "index.html",
                        "HTML model result retains the semantic path");
                    AssertTrue(projected.Content.IndexOf("rna://",
                            StringComparison.Ordinal) < 0 &&
                        projected.Content.IndexOf("artifactId",
                            StringComparison.Ordinal) < 0 &&
                        projected.Content.IndexOf("revisionArtifactId",
                            StringComparison.Ordinal) < 0 &&
                        projected.Content.IndexOf("contentSha256",
                            StringComparison.Ordinal) < 0 &&
                        projected.Content.IndexOf("sourceTool",
                            StringComparison.Ordinal) < 0,
                        "HTML model result hides URI, revision, hash, and source identity");

                    string contractError;
                    AssertTrue(ModelToolResultProjection.ValidateAcceptedCall(
                            new ToolCall("html_current",
                                HtmlWorkspaceToolCatalog.WriteFileToolId,
                                "{\"path\":\"index.html\",\"content\":\"ok\"}"),
                            out contractError),
                        "current semantic HTML write is replayable");
                    AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                            new ToolCall("html_old",
                                "common.html_workspace_upsert_file", "{}"),
                            out contractError),
                        "retired HTML upsert requires reset");
                    AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                            new ToolCall("html_runtime_arg",
                                HtmlWorkspaceToolCatalog.WriteFileToolId,
                                "{\"path\":\"index.html\",\"content\":\"ok\",\"uri\":\"rna://runtime\"}"),
                            out contractError),
                        "HTML history rejects runtime-owned arguments");
                    AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                            new ToolCall("html_nested_read",
                                HtmlWorkspaceToolCatalog.BindDataToolId,
                                "{\"name\":\"sales\",\"sourceTool\":\"excel.read_range\"}"),
                            out contractError),
                        "HTML history rejects nested source execution");

                    var unchanged = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                        new JObject
                        {
                            ["path"] = "index.html",
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

                    var noSourceBind = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject { ["name"] = "missingSource" });
                    AssertEqual(ToolExecutionOutcome.Error,
                        noSourceBind.Outcome,
                        "HTML bind fails without an accepted read");
                    AssertContains(noSourceBind.Result.DataJson,
                        "html_data_source_read_required",
                        "HTML bind reports the stable missing-read code");

                    adapter.ExcelBackendCalls.Clear();
                    var sourceArguments = new JObject
                    {
                        ["sheet"] = "Data",
                        ["address"] = "A1:B4",
                        ["content"] = "values"
                    };
                    var source = ExecuteHtmlNative(runtime,
                        "excel.read_range", sourceArguments);
                    AssertEqual(ToolExecutionOutcome.Ok, source.Outcome,
                        "HTML source read succeeds before bind");
                    AppendAcceptedHtmlSource(session, "html_run",
                        "html_source", "excel.read_range",
                        sourceArguments, source.Result);
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                        "source is read once before binding");
                    var bind = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject
                        {
                            ["name"] = "sales"
                        });
                    AssertEqual(ToolExecutionOutcome.Ok, bind.Outcome,
                        "native HTML bind succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedChange,
                        bind.Evidence.Effect,
                        "native HTML bind verifies its workspace revision");
                    AssertEqual(1, adapter.ExcelBackendCalls.Count(operation =>
                            operation == FakeOfficeAdapter.ExcelRangeReadOperation),
                        "HTML bind reuses accepted data without a nested read");
                });
        }

        private static void AppendAcceptedHtmlSource(
            ChatSession session,
            string runId,
            string callId,
            string toolId,
            JObject arguments,
            RNAssistant.Core.Tools.Contracts.ToolResult result)
        {
            session.LastRun = new ChatRunRecord
            {
                RunId = runId,
                TurnId = runId + "_turn",
                ResponseProtocolVersion = ConversationResponse.ProtocolVersion
            };
            var values = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                (arguments ?? new JObject()).ToString(Formatting.None));
            var call = AgentJsonProtocol.CreateToolCallMessage(
                new AgentToolCall
                {
                    Id = callId,
                    Name = toolId,
                    Arguments = values
                }, "Read source.", null, ToolResultRoles.User,
                new AcceptedToolCallOrigin("source_step", "source_attempt", 0));
            call.RunId = runId;
            session.Messages.Add(call);
            var invocation = new ToolInvocation
            {
                ToolId = toolId,
                ToolCallId = callId,
                Arguments = values
            };
            var acceptedResult = AgentJsonProtocol.CreateToolResultMessage(
                invocation, result, ToolResultRoles.User);
            acceptedResult.RunId = runId;
            session.Messages.Add(acceptedResult);
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
