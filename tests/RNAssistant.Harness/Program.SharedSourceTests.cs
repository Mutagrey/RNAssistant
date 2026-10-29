using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void SharedPlanSourcePackage()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var session = NewSession(adapter); session.Mode = ChatModes.Plan;
                var catalog = executor.GetControllerTools().ToList();
                foreach (var title in new[] { "Alpha", "Beta" })
                    AssertTrue(executor.ExecuteManual(PlanWriteCommand("title", title, "markdown", "# Tasks\nold\n"), catalog,
                        new AppSettings(), false, false, session).Success, "Plan creation through core.write");
                var runtime = executor.CreateNativeRuntime(session, catalog, new AppSettings(), ChatModes.Plan, false);
                Func<string, Dictionary<string, object>, ToolExecutionRecord> execute = (id, inputs) => {
                    var call = new ToolCall(Guid.NewGuid().ToString("N"), id, JsonConvert.SerializeObject(inputs));
                    return ExecuteNative(runtime, call, runtime.Describe(call));
                };
                var body = "*** Update Target: plan: Alpha\n@@\n-status: draft\n+status: ready\n@@ # Tasks\n-old\n+first\n" +
                    "*** Update Target: plan: Beta\n@@ # Tasks\n-old\n+second\n";
                var changed = execute("core.patch", new Dictionary<string, object> { ["patch"] = body });
                AssertEqual(ToolExecutionOutcome.Ok, changed.Outcome, "two Plan targets need no confirmation: " + changed.Result?.DataJson);
                AssertTrue(changed.ResourceEffect.Targets.All(target => target.Status == ResourceTargetStatus.Changed), "both targets verified");
                var a = PlanResourceToolHandler.ResolveCurrent(new ChatStore(FixturePaths.Value).DocumentArtifacts, session, "plan: Alpha");
                var source = executor.ResourceGateway.ReadWhole(session, ChatResourceUri.CreateArtifactRevision(session, a), "source");
                AssertEqual("---\ntitle: \"Alpha\"\nstatus: ready\n---\n# Tasks\nfirst\n", source.Result.Text, "source includes exact header and body");
                var effect = changed.ResourceEffect.Targets[0];
                AssertContains(PatchChangesProjection.ReadSource(executor.ResourceAuthority, effect.After, 10000), "status: ready", "retained logical after-source supports diff");
                AssertContains(PatchChangesProjection.ReadSource(executor.ResourceAuthority, effect.Source, 10000), "status: draft", "retained before-source survives mutation");
                var materialized = new ToolResultMaterialization(changed.Result, resourceEffect: changed.ResourceEffect);
                var invocation = new ToolInvocation { ToolId = "core.patch", ToolCallId = changed.Context.Call.Id };
                var terminal = AgentJsonProtocol.CreateToolResultMessage(invocation, materialized, int.MaxValue, ToolResultRoles.User);
                ConversationModelSession.AddVerifiedSourceEvidence(session, executor.ResourceAuthority, executor.Payloads, terminal, invocation, materialized);
                AssertEqual(2, terminal.ResourceEvidence.Count, "every confirmed Plan gets its complete source projection");
                var sourceObservation = terminal.ResourceEvidence.Single(e => e.Resource.Uri == effect.After.Uri);
                AssertEqual(source.Result.Text, executor.Payloads.ReadText(sourceObservation.Payload.ToBlobReference()), "after-source includes title and status header");
                var same = PlanWriteCommand("title", "Alpha", "status", "ready", "markdown", "# Tasks\nfirst\n");
                same.ToolCallId = Guid.NewGuid().ToString("N");
                var fromSource = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = same.ToolCallId, Name = same.ToolId, Arguments = same.Arguments },
                    "Save.", "Use the delivered source.", null, ToolResultRoles.User, FixtureCallOrigin());
                fromSource.ResourceEvidence = terminal.ResourceEvidence; session.Messages.Add(fromSource);
                var sameCall = new ToolCall(same.ToolCallId, same.ToolId, JsonConvert.SerializeObject(same.Arguments));
                AssertEqual(ToolExecutionOutcome.Ok, ExecuteNative(runtime, sameCall, runtime.Describe(sameCall)).Outcome, "verified logical Plan source authorizes the next source edit");
                var createOnly = PlanWriteCommand("title", "Alpha", "markdown", "must not replace");
                createOnly.ExpectedTargetAbsent = true;
                AssertTrue(!executor.ExecuteManual(createOnly, catalog, new AppSettings(), false, false, session).Success, "UI creation never upserts an existing title");
                var staleEditor = PlanWriteCommand("title", "Alpha", "markdown", "must not replace");
                staleEditor.ExpectedArtifactId = "older-version";
                AssertTrue(!executor.ExecuteManual(staleEditor, catalog, new AppSettings(), false, false, session).Success, "UI save pins the displayed revision");

                var beforeCount = session.Artifacts.Count;
                var refused = execute("core.patch", new Dictionary<string, object> { ["patch"] =
                    "*** Update Target: plan: Alpha\n@@\n-first\n+bad\n*** Update Target: plan: Beta\n@@\n-missing\n+bad\n" });
                AssertEqual(ToolExecutionOutcome.Error, refused.Outcome, "invalid second target rejects entire package");
                AssertTrue(!refused.MayHaveDispatched && session.Artifacts.Count == beforeCount, "no partial preparation effects");
                var mixed = execute("core.patch", new Dictionary<string, object> { ["patch"] =
                    "*** Update Target: plan: Alpha\n@@\n-first\n+bad\n*** Update Target: VBA module: Module1\n@@\n+x\n" });
                AssertTrue(mixed.Outcome == ToolExecutionOutcome.Error && !mixed.MayHaveDispatched, "Plan mode rejects mixed owners before prepare");
                var alias = execute("core.patch", new Dictionary<string, object> { ["patch"] =
                    "*** Update Target: plan: Alpha\n@@\n-first\n+x\n*** Update Target: Plan: Alpha\n@@\n-first\n+y\n" });
                AssertTrue(alias.Outcome == ToolExecutionOutcome.Error && !alias.MayHaveDispatched, "aliases reject before write");

                // A body-only observation must not authorize source/header edits.
                var observed = execute("core.read", new Dictionary<string, object> { ["target"] = "plan: Alpha", ["representation"] = "text" });
                var callId = Guid.NewGuid().ToString("N");
                var args = new Dictionary<string, object> { ["patch"] = "*** Update Target: plan: Alpha\n@@\n-status: ready\n+status: draft\n" };
                var accepted = AgentJsonProtocol.CreateToolCallMessage(new AgentToolCall { Id = callId, Name = "core.patch", Arguments = args },
                    "Edit.", "Edit Plan.", null, ToolResultRoles.User, FixtureCallOrigin());
                accepted.ResourceEvidence = observed.ResourceEvidence.ToList(); session.Messages.Add(accepted);
                var unobserved = new ToolCall(callId, "core.patch", JsonConvert.SerializeObject(args));
                var rejected = ExecuteNative(runtime, unobserved, runtime.Describe(unobserved));
                AssertTrue(rejected.Outcome == ToolExecutionOutcome.Error && !rejected.MayHaveDispatched, "body coverage cannot cover source header");
                string error;
                foreach (var old in new[] { "plan.document.save", "plan.document.restore", "plan.document.delete" })
                    AssertTrue(!catalog.Any(tool => tool.Id == old) && !ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", old, "{}"), out error), "old Plan contract rejected: " + old);
            });
        }

        private static void SharedCatalogSourceEdits()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new SkillStore(paths);
                var skill = store.SaveOne(new SkillDefinition { Id = "custom.guide", Name = "Guide", Description = "Source edits", Host = "Common", BodyMarkdown = "# Guide", Enabled = true });
                string saveError; SkillReferenceMetadata metadata;
                AssertTrue(store.TrySaveReference(skill, "references/details.md", "# Details\r\nold\r\n", out metadata, out saveError), "seed reference");
                var referencePath = Path.Combine(skill.StoragePath, "references", "details.md");
                File.WriteAllText(referencePath, "# Details\r\nold\r\n", new UTF8Encoding(true));
                var settings = new AppSettings { ChatTitlePrompt = "# Title\nold\n", AutoConfirmToolActions = true };
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), store,
                    loadSettings: () => settings.Clone(), saveSettings: updated => settings = updated.Clone(), paths: paths);
                var session = NewSession(adapter); var tools = executor.GetControllerTools().ToList();
                var target = "skill reference: custom.guide / references/details.md";
                var command = Command("core.patch", "patch", "*** Update Target: " + target + "\n@@ # Details\n-old\n+new\n" +
                    "*** Update Target: prompt: chatTitlePrompt\n@@ # Title\n-old\n+new\n");
                var pending = PrepareVbaNative(executor, session, command);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation, pending.Record.Outcome, "catalog source edits retain confirmation: " + pending.Record.Result?.DataJson);
                var changed = ConfirmVbaNative(pending);
                AssertEqual(ToolExecutionOutcome.Ok, changed.Outcome, "independent catalog owners apply sequentially: " + changed.Result?.DataJson);
                AssertEqual("# Title\nnew\n", settings.ChatTitlePrompt, "prompt field read-back");
                var bytes = File.ReadAllBytes(referencePath);
                AssertTrue(bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf, "reference retains BOM");
                AssertEqual("# Details\r\nnew\r\n", File.ReadAllText(referencePath), "reference retains CRLF");
                foreach (var effect in changed.ResourceEffect.Targets)
                {
                    AssertContains(executor.ResourceGateway.ReadRetainedCatalogText(effect.Source, 10000), "old", "retained catalog before");
                    AssertContains(executor.ResourceGateway.ReadRetainedCatalogText(effect.After, 10000), "new", "retained catalog after");
                }
                var stale = PrepareVbaNative(executor, session, Command("core.patch", "patch", "*** Update Target: prompt: chatTitlePrompt\n@@\n-new\n+bad\n"));
                settings.ChatTitlePrompt = "external change";
                var rejected = ConfirmVbaNative(stale);
                AssertTrue(rejected.Outcome == ToolExecutionOutcome.Error && !rejected.MayHaveDispatched, "unpublished settings drift cannot be overwritten");
                AssertEqual("external change", settings.ChatTitlePrompt, "external prompt survives");
                var created = executor.ExecuteManual(Command("core.write", "target", "skill reference: custom.guide / references/new.md", "content", "new reference"), tools, settings, false, true, session);
                AssertTrue(created.Success, "new reference shares core.write: " + created.Message);
                var deleted = executor.ExecuteManual(Command("resources.delete", "target", "skill reference: custom.guide / references/new.md"), tools, settings, false, true, session);
                AssertTrue(deleted.Success && !File.Exists(Path.Combine(skill.StoragePath, "references", "new.md")), "reference lifecycle uses existing owner");
                string error;
                foreach (var old in new[] { "prompts.save", "skills.reference.upsert", "skills.reference.delete", "html.data.refresh" })
                    AssertTrue(!tools.Any(tool => tool.Id == old) && !ModelToolResultProjection.ValidateAcceptedCall(new ToolCall("old", old, "{}"), out error), "old contract rejected: " + old);
            });
        }
    }
}
