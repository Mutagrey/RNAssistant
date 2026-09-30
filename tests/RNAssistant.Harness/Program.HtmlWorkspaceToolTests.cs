using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
                    string nestedArgumentsError;
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject
                        {
                            ["path"] = "index.html",
                            ["content"] = "<main>root</main>",
                            ["arguments"] = new JObject
                            {
                                ["path"] = "index.html",
                                ["content"] = "<main>nested</main>"
                            }
                        }, writeFileSchema, false, out nestedArgumentsError),
                        "HTML write rejects a second arguments wrapper");
                    AssertContains(nestedArgumentsError,
                        "$ contains unsupported property arguments",
                        "HTML write reports the exact repairable wrapper error");
                    var bindDefinition = definitions.Single(definition =>
                        definition.Id == HtmlWorkspaceToolCatalog.BindDataToolId);
                    AssertContains(bindDefinition.Description,
                        "RN.resources.open(name)",
                        "HTML bind describes its bounded resource API");
                    var bindSchema = JObject.Parse(bindDefinition.ArgumentSchemaJson);
                    AssertTrue(bindSchema["properties"]["target"] != null &&
                        bindSchema["properties"]["transform"] == null,
                        "HTML binding selects a resource view, not a second transform pipeline");
                    string bindError;
                    AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject {
                        ["name"] = "sales", ["target"] = "attachment: records.json",
                        ["view"] = "records", ["path"] = "$.records"
                    }, bindSchema, false, out bindError), "HTML binding admits an explicit structural path");
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject {
                        ["name"] = "sales", ["target"] = "attachment: records.json",
                        ["view"] = "records", ["path"] = "$[*]"
                    }, bindSchema, false, out bindError), "HTML binding rejects wildcard structural paths before execution");
                    AssertTrue(!ToolSchemaSupport.ValidateArguments(new JObject {
                        ["name"] = "sales", ["target"] = "attachment: records.json",
                        ["view"] = "text", ["path"] = "$.records"
                    }, bindSchema, false, out bindError), "HTML binding rejects structural selectors on complete views");
                    AssertTrue(ToolSchemaSupport.ValidateArguments(new JObject {
                        ["name"] = "page", ["target"] = "File: report.pdf",
                        ["view"] = "render-page", ["path"] = "0"
                    }, bindSchema, false, out bindError), "HTML binding admits an explicit page index");
                    AssertEqual(@"^\$(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
                        (string)ToolSchemaSupport.ForStructuredOutput(bindSchema)
                            .SelectToken("anyOf[1].properties.path.pattern"),
                        "structured model schema retains the path constraint");
                    var invalidPattern = (JObject)bindSchema.DeepClone();
                    invalidPattern.SelectToken("anyOf[1].properties.path.pattern").Replace("[");
                    JObject ignoredSchema;
                    AssertTrue(!ToolSchemaSupport.TryParse(new ToolCatalogEntry {
                        Id = "test.invalid_pattern", ArgumentSchemaJson = invalidPattern.ToString(Formatting.None)
                    }, out ignoredSchema, out bindError), "invalid schema patterns fail catalog admission");

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

                    var searchBinding = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject { ["name"] = "search", ["target"] = "Excel search scope: sheet 'Data'",
                            ["view"] = "records", ["path"] = "$" });
                    AssertEqual(ToolExecutionOutcome.Error, searchBinding.Outcome,
                        "Excel search evidence is not accepted as tabular source data");
                    AssertContains(searchBinding.Result.Message, "Excel range, table, or name",
                        "search binding failure gives the exact recovery route");
                    AssertEqual(ToolFailureKind.RejectedNoEffect,
                        searchBinding.Recovery.FailureKind,
                        "invalid HTML binding certifies no workspace effect");
                    AssertEqual(ToolRetryPolicy.Replan,
                        searchBinding.Recovery.RetryPolicy,
                        "invalid HTML binding requires a different semantic target");

                    adapter.AddExcelTableForTest(
                        "Data", "A1:B4", "Sales", true, string.Empty);
                    var tableBinding = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject { ["name"] = "table-sales",
                            ["target"] = "Excel table: Sales",
                            ["view"] = "records" });
                    AssertEqual(ToolExecutionOutcome.Ok, tableBinding.Outcome,
                        "HTML binding applies an Office target's canonical record view");
                    AssertEqual("$.values", session.HtmlWorkspace.DataSources
                            .Single(item => item.Name == "table-sales").Binding.ViewPath,
                        "HTML binding persists the runtime-selected canonical path");

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
                    AssertTrue(upsert.Result.Message.IndexOf("SHA-256",
                            StringComparison.OrdinalIgnoreCase) < 0,
                        "HTML write does not put a runtime content hash in the visible result");
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

                    var sourceArtifact = new ChatArtifact { Kind = ChatArtifactKinds.File, Title = "native-sales.json",
                        MimeType = "application/json", InlineText = "[{\"sales\":120}]" };
                    session.Artifacts.Add(sourceArtifact);
                    var target = executor.ResourceGateway.Find(session, "native-sales.json", "conversation").Items.Single().Target;
                    var bind = ExecuteHtmlNative(runtime, HtmlWorkspaceToolCatalog.BindDataToolId,
                        new JObject { ["name"] = "sales", ["target"] = target, ["policy"] = "head" });
                    AssertEqual(ToolExecutionOutcome.Ok, bind.Outcome, "native canonical binding succeeds: " + bind.Result.Message + " " + bind.Result.DataJson);
                    AssertEqual(ToolEffectEvidence.VerifiedChange, bind.Evidence.Effect, "binding change is verified");
                    var binding = session.HtmlWorkspace.DataSources.Single(item => item.Name == "sales").Binding;
                    AssertContains(binding.Resource.Uri, sourceArtifact.Id, "binding points to resource, not a tool result");
                    var head = session.ActiveHtmlArtifactId;
                    var refresh = ExecuteHtmlNative(runtime, HtmlWorkspaceToolCatalog.RefreshDataToolId,
                        new JObject { ["name"] = "sales" });
                    AssertEqual(ToolExecutionOutcome.Ok, refresh.Outcome, "canonical refresh succeeds");
                    AssertEqual(ToolEffectEvidence.VerifiedNoChange, refresh.Evidence.Effect, "refresh observes source; it does not mutate workspace");
                    AssertEqual(head, session.ActiveHtmlArtifactId, "refresh does not duplicate workspace history");
                });
        }

        private static void HtmlWorkspacePatchMismatchReturnsCurrentSource()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var definitions = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);
                    var source = "const first = 1;\nconst second = 2;\r\nconst third = 3;";
                    var created = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.WriteFileToolId,
                        new JObject { ["path"] = "app.js", ["content"] = source });
                    AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                        "mixed-newline source is created");
                    var revision = session.ActiveHtmlArtifactId;

                    var failed = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                        new JObject
                        {
                            ["path"] = "app.js",
                            ["patch"] = new JArray(
                                new JObject { ["op"] = "replace",
                                    ["find"] = "const first = 1;\nconst second = 2;",
                                    ["text"] = "const first = 4;" },
                                new JObject { ["op"] = "replace",
                                    ["find"] = "const missing = 9;",
                                    ["text"] = "const missing = 10;" })
                        });
                    AssertEqual(ToolExecutionOutcome.Error, failed.Outcome,
                        "missing second anchor refuses the whole patch");
                    AssertEqual(ToolDispatchEvidence.NotDispatched, failed.Evidence.Dispatch,
                        "failed patch never reaches the workspace write");
                    AssertEqual(revision, session.ActiveHtmlArtifactId,
                        "failed patch keeps the active revision");
                    AssertEqual(source, session.HtmlWorkspace.Files.Single().Content,
                        "earlier in-memory hunk is not saved after a later failure");
                    var data = JObject.Parse(failed.Result.DataJson);
                    AssertEqual("text_patch_not_found", (string)data["code"],
                        "mismatch has a precise code");
                    AssertEqual(2, (int)data["hunkIndex"],
                        "mismatch identifies the failed hunk");
                    AssertEqual(source, (string)data["recovery"]["currentContent"],
                        "model-facing result contains the current source");
                    AssertEqual(source, failed.Recovery.CurrentContent,
                        "bounded recovery returns the exact current source");
                    AssertTrue(failed.Recovery.CurrentContentComplete,
                        "recovery labels the complete source");
                    AssertEqual(ToolRetryPolicy.Replan, failed.Recovery.RetryPolicy,
                        "model can repair the patch from returned source");

                    var corrected = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                        new JObject
                        {
                            ["path"] = "app.js",
                            ["patch"] = new JArray(new JObject { ["op"] = "replace",
                                ["find"] = "const first = 1;\nconst second = 2;",
                                ["text"] = "const first = 4;" })
                        });
                    AssertEqual(ToolExecutionOutcome.Ok, corrected.Outcome,
                        "a copied exact anchor across mixed line endings applies");
                    AssertContains(session.HtmlWorkspace.Files.Single().Content,
                        "const first = 4;\r\nconst third = 3;",
                        "replacement affects only the copied block");

                    var largeSource = string.Concat(Enumerable.Repeat("// source line\n", 900));
                    var large = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.WriteFileToolId,
                        new JObject { ["path"] = "large.js", ["content"] = largeSource });
                    AssertEqual(ToolExecutionOutcome.Ok, large.Outcome,
                        "large source is created");
                    var missingLarge = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.ApplyPatchToolId,
                        new JObject { ["path"] = "large.js",
                            ["patch"] = new JArray(new JObject { ["op"] = "replace",
                                ["find"] = "missing anchor", ["text"] = "new" }) });
                    AssertEqual(ToolExecutionOutcome.Error, missingLarge.Outcome,
                        "large missing anchor is rejected");
                    AssertEqual(ToolRetryPolicy.RefreshRequired,
                        missingLarge.Recovery.RetryPolicy,
                        "large source requires a fresh resource read");
                    AssertTrue(missingLarge.Recovery.CurrentContent == null,
                        "large source is not silently truncated in recovery");
                });
        }

        private static void HtmlWorkspaceBatchedWritesUsePerCallOperationIdentity()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var definitions = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);
                    Func<string, string, string, ToolExecutionRecord> write = delegate(string callId, string path, string content)
                    {
                        var call = new ToolCall(callId, HtmlWorkspaceToolCatalog.WriteFileToolId,
                            new JObject { ["path"] = path, ["content"] = content }.ToString(Formatting.None));
                        return runtime.ExecuteAsync(new ToolExecutionContext(
                                call, runtime.Describe(call), "html_batch_run", "html_batch_turn",
                                "html_batch_step", DateTime.UtcNow, false, 4), CancellationToken.None)
                            .GetAwaiter().GetResult();
                    };

                    var first = write("html_batch_styles", "styles.css", "body { color: #123456; }");
                    var second = write("html_batch_app", "app.js", "window.ready = true;");
                    AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "first same-step HTML write succeeds");
                    AssertEqual(ToolExecutionOutcome.Ok, second.Outcome, "second same-step HTML write is not treated as a replay");
                    AssertEqual(2, session.HtmlWorkspace.Files.Count, "both files are retained in one workspace");
                    AssertTrue(session.HtmlWorkspace.Files.Any(item => item.Path == "styles.css" && item.Content.Contains("#123456")),
                        "CSS content is present");
                    AssertTrue(session.HtmlWorkspace.Files.Any(item => item.Path == "app.js" && item.Content.Contains("ready")),
                        "JS content is present");
                    var scope = executor.ResourceAuthority.Scope(session, true);
                    var operationHeads = executor.ResourceAuthority.Store.Capture(scope).Heads.Values
                        .Count(head => head.Identity.Uri.IndexOf("/html_operation_", StringComparison.Ordinal) >= 0);
                    AssertEqual(2, operationHeads, "each same-step HTML write publishes its own operation receipt");
                });
        }

        private static void HtmlWorkspaceReplacementRequiresCurrentSource()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
                {
                    var session = NewSession(adapter);
                    var definitions = OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList();
                    var runtime = executor.CreateNativeRuntime(
                        session, definitions, new AppSettings(), "agent", false);
                    var created = ExecuteHtmlNative(runtime,
                        HtmlWorkspaceToolCatalog.WriteFileToolId,
                        new JObject { ["path"] = "index.html", ["content"] = "<main>first</main>" });
                    AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                        "initial HTML file is created");
                    AssertContains(created.Result.Message, "Current files (1): index.html",
                        "next model step receives the saved workspace inventory");

                    Func<string, string, IList<ResourceEvidence>, ToolExecutionRecord> replace =
                        delegate(string step, string content, IList<ResourceEvidence> evidence)
                        {
                            var callId = "html_replace_" + step;
                            var call = new ToolCall(callId, HtmlWorkspaceToolCatalog.WriteFileToolId,
                                new JObject { ["path"] = "index.html", ["content"] = content }
                                    .ToString(Formatting.None));
                            session.Messages.Add(new ChatMessage
                            {
                                Id = "accepted_" + step,
                                Role = "assistant",
                                RunId = "html_guard_run",
                                ToolCallId = callId,
                                AcceptedCallOrigin = new AcceptedToolCallOrigin(step, "attempt_" + step, 0),
                                ResourceEvidence = (evidence ?? new List<ResourceEvidence>()).ToList()
                            });
                            return runtime.ExecuteAsync(new ToolExecutionContext(
                                    call, runtime.Describe(call), "html_guard_run", "html_guard_turn",
                                    step, DateTime.UtcNow, false, 4), CancellationToken.None)
                                .GetAwaiter().GetResult();
                        };

                    var original = session.ActiveHtmlArtifactId;
                    var unseen = replace("unseen", "<main>unseen</main>", null);
                    AssertEqual(ToolExecutionOutcome.Error, unseen.Outcome,
                        "unobserved replacement is rejected");
                    AssertEqual("<main>first</main>", session.HtmlWorkspace.Files.Single().Content,
                        "rejected replacement keeps exact source");
                    AssertEqual(original, session.ActiveHtmlArtifactId,
                        "rejected replacement creates no HTML revision");

                    var gateway = executor.ResourceGateway;
                    var member = gateway.List(session, "chat", ChatHtmlResourceCatalog.FileKind,
                        null, 10).Items.Single(item => item.Title == "index.html");
                    var read = gateway.Read(session, new ResourceReadRequest
                    {
                        Reference = member.Reference,
                        Representation = ResourceRepresentations.Source,
                        MaxChars = 32000
                    }).Result;
                    var observed = gateway.Evidence(session, read).ToList();
                    AssertEqual(ToolExecutionOutcome.Ok,
                        replace("observed", "<main>second</main>", observed).Outcome,
                        "replacement after current complete source read succeeds");
                    AssertEqual("<main>second</main>", session.HtmlWorkspace.Files.Single().Content,
                        "observed replacement changes source");
                    AssertEqual(ToolExecutionOutcome.Error,
                        replace("stale", "<main>stale</main>", observed).Outcome,
                        "earlier source evidence cannot overwrite a later revision");
                    AssertEqual("<main>second</main>", session.HtmlWorkspace.Files.Single().Content,
                        "stale replacement keeps the newer source");

                    member = gateway.List(session, "chat", ChatHtmlResourceCatalog.FileKind,
                        null, 10).Items.Single(item => item.Title == "index.html");
                    read = gateway.Read(session, new ResourceReadRequest
                    {
                        Reference = member.Reference,
                        Representation = ResourceRepresentations.Source,
                        MaxChars = 32000
                    }).Result;
                    var currentEvidence = gateway.Evidence(session, read).Single();
                    var sourceLength = session.HtmlWorkspace.Files.Single().Content.Length;
                    var prefix = new ResourceEvidence("prefix", currentEvidence.ScopeId,
                        currentEvidence.Resource, ResourceRepresentations.Source,
                        new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 0, end: 8),
                        false, currentEvidence.AuthorityGeneration,
                        contentSha256: currentEvidence.ContentSha256);
                    var suffix = new ResourceEvidence("suffix", currentEvidence.ScopeId,
                        currentEvidence.Resource, ResourceRepresentations.Source,
                        new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: 8, end: sourceLength),
                        true, currentEvidence.AuthorityGeneration,
                        contentSha256: currentEvidence.ContentSha256);
                    AssertEqual(ToolExecutionOutcome.Error,
                        replace("partial", "<main>partial</main>", new[] { suffix }).Outcome,
                        "final source chunk alone does not authorize full replacement");
                    AssertEqual(ToolExecutionOutcome.Ok,
                        replace("chunked", "<main>third</main>", new[] { prefix, suffix }).Outcome,
                        "contiguous source chunks from one current revision authorize replacement");

                    member = gateway.List(session, "chat", ChatHtmlResourceCatalog.FileKind,
                        null, 10).Items.Single(item => item.Title == "index.html");
                    read = gateway.Read(session, new ResourceReadRequest
                    {
                        Reference = member.Reference,
                        Representation = ResourceRepresentations.Source,
                        MaxChars = 32000
                    }).Result;
                    var earlierInput = gateway.Evidence(session, read).ToList();
                    session.Messages.Add(new ChatMessage { Role = "assistant",
                        RunId = "html_guard_run", ToolCallId = "later-read-step",
                        AcceptedCallOrigin = new AcceptedToolCallOrigin("later-read-step", "attempt", 0),
                        ResourceEvidence = earlierInput });
                    executor.MutateLocalResources(session, HtmlWorkspaceToolCatalog.WriteFileToolId,
                        null, () => HtmlWorkspaceToolService.UpsertFile(session, "app.js", "script", "run();", false));
                    AssertEqual(ToolExecutionOutcome.Ok,
                        replace("carried", "<main>fourth</main>", null).Outcome,
                        "same-run complete source stays usable after an unrelated file changes the workspace root");
                    AssertEqual("<main>fourth</main>", session.HtmlWorkspace.Files.Single(item => item.Path == "index.html").Content,
                        "carried observation replaces only its matching file");
                    member = gateway.List(session, "chat", ChatHtmlResourceCatalog.FileKind,
                        null, 10).Items.Single(item => item.Title == "index.html");
                    read = gateway.Read(session, new ResourceReadRequest
                    {
                        Reference = member.Reference,
                        Representation = ResourceRepresentations.Source,
                        MaxChars = 32000
                    }).Result;
                    session.Messages.Add(new ChatMessage { Role = "assistant",
                        RunId = "another-run", ToolCallId = "foreign-read-step",
                        AcceptedCallOrigin = new AcceptedToolCallOrigin("foreign-read-step", "attempt", 0),
                        ResourceEvidence = gateway.Evidence(session, read).ToList() });
                    AssertEqual(ToolExecutionOutcome.Error,
                        replace("foreign", "<main>fifth</main>", null).Outcome,
                        "a source observation from another run cannot authorize replacement");
                });
        }

        private static void HtmlWorkspaceVisibleSourceReadAuthorizesNextWrite()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var settings = new AppSettings { ContextWindowOverrideTokens = 64000 };
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths),
                    new SkillStore(paths), new ToolStore(paths), () => settings,
                    value => settings = value, paths);
                var session = NewSession(adapter);
                session.Mode = ChatModes.Agent;
                session.LastRun = new ChatRunRecord { RunId = "html-visible-run",
                    TurnId = "html-visible-turn", ResponseProtocolVersion = ConversationResponse.ProtocolVersion };
                executor.MutateLocalResources(session, HtmlWorkspaceToolCatalog.WriteFileToolId,
                    null, () => HtmlWorkspaceToolService.UpsertFile(session, "index.html", "html", "<main>before</main>", true));
                var catalogs = executor.CaptureCatalogs();
                var tools = ConversationRunService.PrepareToolsForRun(executor.GetHostTools()
                    .Concat(executor.GetControllerTools()).Concat(executor.CapturePublishedGlobalTools(catalogs)));
                var skills = executor.CaptureSkills(catalogs);
                var runtime = executor.CreateNativeRuntime(session, tools, settings, "agent", false);
                var target = executor.ResourceGateway.Find(session, "index.html", "html").Items
                    .Single(item => item.Type == "HTML file").Target;
                var readCall = new ToolCall("html-visible-read", ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = target, ["representation"] = "source" }.ToString(Formatting.None));
                var read = runtime.ExecuteAsync(new ToolExecutionContext(readCall, runtime.Describe(readCall),
                    session.LastRun.RunId, session.LastRun.TurnId, "read-step", DateTime.UtcNow, false, 4),
                    CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "exact current HTML source is read");
                var args = new Dictionary<string, object> { ["target"] = target, ["representation"] = "source" };
                var acceptedRead = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall {
                    Id = readCall.Id, Name = readCall.Name, Arguments = args }, "Read source.", null,
                    settings.ToolResultRole, new AcceptedToolCallOrigin("read-step", "read-attempt", 0));
                acceptedRead.RunId = session.LastRun.RunId;
                var invocation = new ToolInvocation { ToolId = readCall.Name, ToolCallId = readCall.Id,
                    Arguments = args };
                var acceptedResult = AgentJsonProtocol.CreateToolResultMessage(invocation,
                    new ToolResultMaterialization(read.Result, resourceEvidence: read.ResourceEvidence),
                    settings.ToolResultRole);
                acceptedResult.RunId = session.LastRun.RunId;
                session.Messages.Add(new ChatMessage { Role = "user", Content = "Update the HTML page." });
                session.Messages.Add(acceptedRead);
                session.Messages.Add(acceptedResult);
                var store = new ChatStore(paths);
                store.Save(session);
                using (var model = ConversationModelSession.CreateAsync(adapter, null, null,
                    EventStore(store), ChatModes.Agent, "Update the HTML page.", session,
                    NewContext(adapter), settings, tools, skills.Skills, null, false, null,
                    CancellationToken.None, executor.ResourceAuthority, executor.Payloads,
                    () => skills, catalogs.Authority.Generation).GetAwaiter().GetResult())
                {
                    var request = model.CreateRequest("write-step",
                        new ModelProtocolCallContext(new string[0]));
                    AssertTrue(request.AcceptedMessages.Any(item => item.ToolCallId == readCall.Id &&
                        item.ResourceEvidence.Any(evidence => evidence.View == ResourceRepresentations.Source)),
                        "complete read remains visible in the next model input");
                    const string writeId = "html-visible-write";
                    model.AppendToolCall(new AgentToolCall { Id = writeId,
                        Name = HtmlWorkspaceToolCatalog.WriteFileToolId,
                        Arguments = new Dictionary<string, object> { ["path"] = "index.html",
                            ["content"] = "<main>after</main>" } }, "Update page.", null,
                        new AcceptedToolCallOrigin("write-step", "write-attempt", 0));
                    var accepted = session.Messages.Last();
                    accepted.RunId = session.LastRun.RunId;
                    AssertTrue(accepted.ResourceEvidence.Any(evidence =>
                        evidence.View == ResourceRepresentations.Source),
                        "accepted write keeps source evidence from its exact model request");
                    var writeCall = new ToolCall(writeId, HtmlWorkspaceToolCatalog.WriteFileToolId,
                        new JObject { ["path"] = "index.html", ["content"] = "<main>after</main>" }
                            .ToString(Formatting.None));
                    var written = runtime.ExecuteAsync(new ToolExecutionContext(writeCall,
                        runtime.Describe(writeCall), session.LastRun.RunId, session.LastRun.TurnId,
                        "write-step", DateTime.UtcNow, false, 4), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssertEqual(ToolExecutionOutcome.Ok, written.Outcome,
                        "visible complete source authorizes replacement without another read");
                }
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
