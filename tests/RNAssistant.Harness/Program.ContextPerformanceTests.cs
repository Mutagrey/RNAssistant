using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ContextBudgetIncrementalEstimate()
        {
            foreach (var multiplier in new[] { 0.25, 1.0, 1.3, 4.0 })
            foreach (var calibrated in new[] { false, true })
            {
                var settings = new AppSettings { Model = "cost-fixture", TokenEstimateMultiplier = multiplier,
                    AutoCalibrateTokenEstimate = calibrated };
                if (calibrated)
                    settings.TokenEstimateCalibrations[settings.Model] = new TokenEstimateCalibrationSettings {
                        SampleCount = 2, Multiplier = 1.625, InterceptTokens = 123.25 };
                var text = new ChatMessage { Role = "tool", Content = "One exact result: число 17.", ToolCallId = "cost" };
                var image = new ChatMessage { Role = "user", Content = "Inspect the image.",
                    Attachments = new List<ChatAttachment> { new ChatAttachment { Id = "img", Kind = "image" } } };
                var audio = new ChatMessage { Role = "user", Content = "Inspect audio.",
                    Attachments = new List<ChatAttachment> { new ChatAttachment { Id = "audio", Kind = "audio", Size = 12345 } } };
                var analyzed = new ChatMessage { Role = "user", Content = "Use analyzed media.", Attachments = image.Attachments,
                    AttachmentAnalysis = new AttachmentAnalysisContext { AttachmentIds = new List<string> { "img" }, Content = "A table." } };
                var excluded = new ChatMessage { Role = "user", ExcludeFromModelContext = true, Attachments = audio.Attachments };
                var messages = new[] { text, image, audio, analyzed, excluded, null };
                var cost = new ModelContextBudget.MessageTokenCost();
                var parts = messages.Select(message => ModelContextBudget.MeasureMessages(new[] { message }, settings)).ToArray();
                for (var index = 0; index < messages.Length; index++)
                {
                    cost.Replace(default(ModelContextBudget.MessageTokenCost), parts[index]);
                    AssertEqual(ModelContextBudget.EstimateMessagesTokens(messages.Take(index + 1), settings), cost.Estimate(settings),
                        "incremental estimate preserves calibration, rounding and media branch");
                }
                for (var index = 0; index < messages.Length; index++)
                {
                    cost.Replace(parts[index], default(ModelContextBudget.MessageTokenCost));
                    AssertEqual(ModelContextBudget.EstimateMessagesTokens(messages.Skip(index + 1), settings), cost.Estimate(settings),
                        "removing the last media or body preserves exact request cost");
                }
            }
        }

        // Opt-in comparison of the real compiler, without provider calls or timing assertions.
        private static void ContextPerformanceProfile()
        {
            if (Environment.GetEnvironmentVariable("RNA_CONTEXT_PERF") != "1")
            {
                Console.WriteLine("SKIP context perf: set RNA_CONTEXT_PERF=1");
                return;
            }
            var report = new JObject();
            foreach (var count in new[] { 4, 96 })
            foreach (var media in new[] { false, true })
            foreach (var role in new[] { "user", "developer", "tool" })
            foreach (var calibrated in new[] { false, true })
            {
                var settings = new AppSettings { Model = "context-profile", TokenEstimateMultiplier = 1.3,
                    AutoCalibrateTokenEstimate = calibrated };
                if (calibrated)
                    settings.TokenEstimateCalibrations[settings.Model] = new TokenEstimateCalibrationSettings {
                        SampleCount = 10, Multiplier = 1.625, InterceptTokens = 123.25 };
                var facts = ContextPerformanceHistory(count, role);
                var required = new[] { new ChatMessage { Id = "instruction", Role = "system", Content = "Preserve exact results." } };
                if (media)
                    required[0].Attachments.Add(new ChatAttachment { Id = "image", Kind = "image", Size = 8000,
                        CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
                var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                    "pack", new SkillCatalogSnapshot(null), null, 1);
                foreach (var budget in new[] { 250000, 18000, 1 })
                {
                    var name = count + "/" + media + "/" + role + "/" + calibrated + "/" + budget;
                    var compiler = new ModelContextCompiler();
                    Func<JObject> run = () =>
                    {
                        try
                        {
                            var snapshot = compiler.Compile(authority, required, facts, null, new ToolCatalogEntry[0], settings, budget);
                            var messages = snapshot.Messages;
                            var receipt = snapshot.Receipt;
                            AssertEqual(ModelContextBudget.EstimateMessagesTokens(messages, settings), receipt.EstimatedTokens,
                                "profile receipt matches the whole-request estimator");
                            var exact = JsonConvert.SerializeObject(messages.Select(message => new {
                                message.Role, message.Content, message.ToolCallId, message.ToolCalls,
                                message.CompletedOperation, message.ResourceEvidence, message.ResourceEffect, message.Attachments }));
                            return JObject.FromObject(new { status = "ok", hash = TextPatternEngine.Sha256(exact),
                                receipt.EstimatedTokens, receipt.OperationReceipts, receipt.AtomCounts,
                                receipt.ExcludedUnavailable, receipt.ExcludedSuperseded, receipt.ExcludedUnknown,
                                receipt.RetainedBodies, receipt.EvictedBodies, receipt.HydratedBytes,
                                messages = messages.Count, characters = exact.Length });
                        }
                        catch (PromptBudgetExceededException ex)
                        { return JObject.FromObject(new { status = "refused", message = ex.Message }); }
                    };
                    var expected = run();
                    // Time just the production compile, not JSON inspection or detached snapshot getters.
                    Action measure = () => { try { compiler.Compile(authority, required, facts, null,
                        new ToolCatalogEntry[0], settings, budget); } catch (PromptBudgetExceededException) { } };
                    measure();
                    var samples = new List<double>();
                    for (var iteration = 0; iteration < 5; iteration++)
                    {
                        var timer = Stopwatch.StartNew(); measure(); timer.Stop();
                        samples.Add(timer.Elapsed.TotalMilliseconds);
                    }
                    AssertTrue(JToken.DeepEquals(expected, run()), "repeated compilation is deterministic");
                    expected["medianMs"] = samples.OrderBy(value => value).ElementAt(2);
                    report[name] = expected;
                }
            }
            report["prompt"] = JObject.FromObject(new { schema = AppSettings.CurrentAgentPromptSchemaVersion,
                characters = AgentPromptDefaults.GeneralInstructions.Length + AgentPromptDefaults.ToolInstructions.Length +
                    AgentPromptDefaults.SkillInstructions.Length });
            var output = Environment.GetEnvironmentVariable("RNA_CONTEXT_PERF_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, report.ToString(Formatting.Indented));
            Console.WriteLine("PROFILE context cases=" + (report.Count - 1) + " output=" + output);
        }

        private static void ContextRemainingHotspotsProfile()
        {
            if (Environment.GetEnvironmentVariable("RNA_CONTEXT_PERF") != "1")
            { Console.WriteLine("SKIP context perf: set RNA_CONTEXT_PERF=1"); return; }
            var report = new JObject();
            var settings = new AppSettings();
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                "pack", new SkillCatalogSnapshot(null), null, 1);
            foreach (var count in new[] { 128, 512, 1024 })
            {
                var history = ContextPerformanceHistory(count, "tool");
                foreach (var result in history.Where(message => message.Role == "tool"))
                    result.ExecutionProgress = new ToolExecutionProgress(ToolExecutionOutcome.Error,
                        new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan));
                var checkpoint = new ContextCheckpoint { ThroughMessageId = history.Last().Id,
                    Claims = new List<StructuredContextClaim> { new StructuredContextClaim { ClaimId = "profile",
                        Kind = "interpretation", Text = "Continue.", SourceRoles = new List<string> { "assistant" },
                        SourceMessageIds = new List<string> { history.Last().Id } } } };
                var session = new ChatSession { Messages = history, ActiveContextCheckpointId = checkpoint.Id,
                    ContextCheckpoints = new List<ContextCheckpoint> { checkpoint },
                    LastRun = new ChatRunRecord { RunId = "continuity" } };
                Action restore = () => {
                    var set = ContextWorkingSet.Restore(session, new ChatMessage[0], 0);
                    AssertEqual(count, set.Operations.Count(item => item.Required), "all unresolved recoveries stay mandatory");
                };
                report["workingSet/" + count] = ProfileMedian(restore);
            }
            foreach (var count in new[] { 8, 128, 512 })
            {
                var snapshot = new HtmlWorkspaceSnapshot { Id = "profile" };
                for (var index = 0; index < count; index++)
                    snapshot.Files.Add(new HtmlWorkspaceFile { Id = "file" + index, Path = "file" + index + ".js",
                        Kind = "js", Content = new string('x', 16384) });
                var artifact = new ChatArtifact { Id = "profile", Kind = ChatArtifactKinds.HtmlWorkspace,
                    Revision = 1, InlineText = JsonConvert.SerializeObject(snapshot) };
                var session = new ChatSession { ActiveHtmlArtifactId = artifact.Id, Artifacts = new List<ChatArtifact> { artifact } };
                var catalog = new ChatHtmlResourceCatalog(null);
                var parseMs = ProfileMedian(() => JsonConvert.DeserializeObject<HtmlWorkspaceSnapshot>(artifact.InlineText));
                var listMs = ProfileMedian(() => {
                    var page = catalog.List(session, ChatHtmlResourceCatalog.FileKind, null, 1);
                    AssertEqual(1, page.Items.Count, "metadata page is bounded");
                    AssertEqual(count, page.Total, "complete collection is retained");
                });
                report["html/" + count] = JObject.FromObject(new { characters = artifact.InlineText.Length, parseMs, listMs });
            }
            var output = Environment.GetEnvironmentVariable("RNA_CONTEXT_PERF_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output)) File.WriteAllText(output, report.ToString(Formatting.Indented));
            Console.WriteLine("PROFILE remaining hotspots " + report.ToString(Formatting.None));
        }

        private static double ProfileMedian(Action action)
        {
            action();
            var samples = new List<double>();
            for (var iteration = 0; iteration < 5; iteration++)
            {
                var timer = Stopwatch.StartNew(); action(); timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            return samples.OrderBy(value => value).ElementAt(2);
        }

        private static List<ChatMessage> ContextPerformanceHistory(int count, string role)
        {
            var facts = new List<ChatMessage>();
            for (var index = 0; index < count; index++)
            {
                var id = "profile-" + index;
                var result = ContinuityResult(id, "files.create", ToolResult.Error("Already exists.",
                    "{\"code\":\"target_exists\"}"), role);
                result.Id = "result-" + id;
                result.ResourceEffect = new ResourceEffect("effect-" + id, "create", ResourceEffectOutcome.FailedNoEffect);
                var call = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = id, Name = "files.create",
                    Arguments = new Dictionary<string, object> { { "relativePath", id + ".html" },
                        { "content", new string('x', 2048) } } }, "Create " + id,
                    "Record the requested file creation.", null, role, FixtureCallOrigin(id));
                call.Id = "call-" + id;
                facts.Add(call); facts.Add(result);
            }
            return facts;
        }
    }
}
