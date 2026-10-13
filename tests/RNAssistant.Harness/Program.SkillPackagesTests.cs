using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static ToolCatalogEntry PackageTool(string id = "fixture.special", ToolExposure exposure = ToolExposure.Skill,
            string binding = "fixture.handler")
        {
            return new ToolCatalogEntry { Id = id, Name = id, Description = "Fixture operation", BuiltIn = true,
                Exposure = exposure, Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None, false, true, new[] { "agent", "plan" }),
                Binding = new ToolBinding(binding) };
        }

        private static ToolPackSnapshot PackageTools(params ToolCatalogEntry[] catalog)
        {
            return new ToolPackSnapshot("fixture", "agent", "Common", catalog.Select(tool => ToolPackSnapshot.Capture(
                new ToolDescriptor(tool.Id, tool.Description, tool.ArgumentSchemaJson, tool.Exposure, tool.ContractVersion), tool.Policy, tool.Binding)));
        }

        private static SkillDefinition PackageSkill(string id = "fixture.skill", params string[] dependencies)
        {
            return new SkillDefinition { Id = id, Name = id, Description = "Fixture procedure", BodyMarkdown = "Complete immutable rules for " + id,
                Package = new SkillPackageManifest { PackageId = "fixture.package", RequiredTools = dependencies.Select(tool => new SkillToolDependency { Id = tool }).ToList() } };
        }

        private static ChatSession PackageSession()
        {
            var session = NewSession(FakeOfficeAdapter.ForHost("Word"));
            session.Mode = "agent";
            session.LastRun = new ChatRunRecord { RunId = "run", TurnId = "run", StartedUtc = DateTime.UtcNow,
                ConversationPolicy = ConversationRunPolicy.For("agent") };
            return session;
        }

        private static ModelContextSnapshot CompilePackage(ChatSession session, SkillCatalogSnapshot skills,
            ToolPackSnapshot pack, ToolCatalogEntry[] catalog, int budget = 20000, string step = "step")
        {
            var surface = SkillTaskService.Compile(session, skills, pack, catalog, catalog, step, new AppSettings());
            var authority = new ModelAuthoritySnapshot(new ResourceAuthoritySnapshotSet(new ResourceAuthoritySnapshot[0]),
                pack.Generation, skills, null, session.Revision);
            return new ModelContextCompiler().Compile(authority, new ChatMessage[0], new ChatMessage[0], null,
                catalog, new AppSettings(), budget, skillSurface: surface);
        }

        private static void SkillPackagesSelectionAndBudget()
        {
            var core = PackageTool("fixture.read", ToolExposure.Direct);
            var helper = PackageTool(); var catalog = new[] { core, helper }; var pack = PackageTools(catalog);
            var one = PackageSkill("fixture.one", helper.Id); var two = PackageSkill("fixture.two", helper.Id);
            var plain = PackageSkill("fixture.instructions"); plain.Package = null;
            var skills = new SkillCatalogSnapshot(new[] { one, two, plain }); var session = PackageSession();
            var initial = CompilePackage(session, skills, pack, catalog);
            AssertEqual(1, initial.Receipt.Arsenal.Tools.Count, "S02 specialized schema hidden initially");
            // Reading bytes or even a successful resource frame does not mutate task selection.
            session.Messages.Add(new ChatMessage { Role = "user", Content = one.BodyMarkdown, ProtocolMessage = true, ToolName = "common.resources_read" });
            AssertEqual(0, CompilePackage(session, skills, pack, catalog).Receipt.Arsenal.Selected.Count, "S12 source is not selection");
            SkillTaskService.Select(session, plain.Id, skills, pack, new AppSettings(), "load-plain");
            AssertEqual(1, CompilePackage(session, skills, pack, catalog).Receipt.Arsenal.Tools.Count, "S01 instruction-only loads");
            SkillTaskService.Select(session, one.Id, skills, pack, new AppSettings(), "load-one");
            SkillTaskService.Select(session, two.Id, skills, pack, new AppSettings(), "load-two");
            SkillTaskService.Select(session, one.Id, skills, pack, new AppSettings(), "load-again");
            AssertEqual(3, session.LastRun.SelectedSkills.Count, "idempotent selection");
            var next = CompilePackage(session, skills, pack, catalog);
            AssertEqual(2, next.Receipt.Arsenal.Tools.Count, "S03/S05 shared tool appears once");
            AssertEqual(2, next.Receipt.Arsenal.Tools.Single(tool => tool.Id == helper.Id).Skills.Count, "S28 both relationships retained");
            AssertTrue(next.Messages.Any(item => item.Content.Contains(one.BodyMarkdown)), "S13 complete instructions without old load frame");
            AssertEqual(1, initial.Receipt.Arsenal.Tools.Count, "S04 previous request remains frozen");
            RuntimeThrows<PromptBudgetExceededException>(() => CompilePackage(session, skills, pack, catalog, 1));
            AssertEqual(3, session.LastRun.SelectedSkills.Count, "S14 budget refusal does not remove selection");
            session.LastRun = new ChatRunRecord { RunId = "new-run", ConversationPolicy = ConversationRunPolicy.For("agent") };
            AssertEqual(1, CompilePackage(session, skills, pack, catalog).Receipt.Arsenal.Tools.Count, "S22 new run does not inherit procedures");
        }

        private static void SkillPackagesDependencyFailures()
        {
            var tool = PackageTool(); var catalog = new[] { tool }; var pack = PackageTools(catalog);
            var good = PackageSkill("fixture.good", tool.Id); var missing = PackageSkill("fixture.missing", "fixture.absent");
            var optional = PackageSkill("fixture.optional");
            optional.Package.OptionalTools.Add(new SkillToolDependency { Id = "fixture.absent", Fallback = "Report unavailable; do not claim validation." });
            var incompatible = PackageSkill("fixture.incompatible", tool.Id);
            incompatible.Package.RequiredTools[0].ContractVersion = "2";
            var skills = new SkillCatalogSnapshot(new[] { good, missing, optional, incompatible }); var session = PackageSession();
            RuntimeThrows<InvalidOperationException>(() => SkillTaskService.Select(session, missing.Id, skills, pack, new AppSettings(), "missing"));
            RuntimeThrows<InvalidOperationException>(() => SkillTaskService.Select(session, incompatible.Id, skills, pack, new AppSettings(), "version"));
            AssertEqual(0, session.LastRun.SelectedSkills.Count, "S06/S09 missing or incompatible dependencies never partially select");
            var loaded = SkillTaskService.Select(session, optional.Id, skills, pack, new AppSettings(), "optional");
            AssertTrue(loaded.Reason.Contains("Report unavailable"), "S07 optional fallback is explicit");
            SkillTaskService.Select(session, good.Id, skills, pack, new AppSettings(), "good");
            var replacement = PackageTool(binding: "replacement"); var updated = PackageTools(replacement);
            var suspended = CompilePackage(session, skills, updated, new[] { replacement });
            AssertEqual("unavailable", suspended.Receipt.Arsenal.Selected.Single(item => item.Id == good.Id).Status, "S10 update suspends pin");
            AssertEqual(0, suspended.Receipt.Arsenal.Tools.Count, "suspended binding is not exposed");
            var conflicting = PackageSkill("fixture.conflict", tool.Id);
            var all = new SkillCatalogSnapshot(new[] { good, conflicting, optional });
            RuntimeThrows<InvalidOperationException>(() => SkillTaskService.Select(session, conflicting.Id, all, updated, new AppSettings(), "conflict"));
            AssertTrue(session.LastRun.SelectedSkills.All(item => item.Id != conflicting.Id), "S08 no last-wins conflict");
            optional.Package.OptionalTools[0].Fallback = null;
            AssertTrue(SkillTaskService.Validate(optional.Package) != null, "fallback is mandatory");
            RuntimeThrows<InvalidOperationException>(() => PackageTools(tool, PackageTool()));
            var revoked = CompilePackage(session, new SkillCatalogSnapshot(new[] { optional }), pack, catalog);
            AssertEqual("unavailable", revoked.Receipt.Arsenal.Selected.Single(item => item.Id == good.Id).Status, "S11 revoked skill suspended");
        }

        private static async Task SkillPackagesExactDispatch()
        {
            var tool = PackageTool(); var load = SkillLoadToolHandler.Entry(); var catalog = new[] { tool, load };
            var pack = PackageTools(catalog); var skill = PackageSkill("fixture.skill", tool.Id);
            var skills = new SkillCatalogSnapshot(new[] { skill }); var session = PackageSession();
            var registry = new ToolHandlerRegistry(); var probe = new PackageProbeHandler();
            registry.Register(pack.Find(tool.Id), probe);
            registry.Register(pack.Find(load.Id), new SkillLoadToolHandler(session, () => skills, () => pack, new AppSettings()));
            var currentPack = pack;
            var runtime = new ToolRuntime(registry, "agent", false, true,
                dispatchGuard: context => SkillTaskService.CheckDispatch(session, context, currentPack));
            session.LastContextReceipt = CompilePackage(session, skills, pack, catalog).Receipt;
            Func<string, string, ToolExecutionContext> call = (id, arguments) => {
                var command = new ToolCall(Guid.NewGuid().ToString("N"), id, arguments);
                return new ToolExecutionContext(command, runtime.Describe(command), "run", "run", "step", DateTime.UtcNow, false, 5);
            };
            var guessed = await runtime.ExecuteAsync(call(tool.Id, "{}"), CancellationToken.None);
            AssertTrue(!guessed.MayHaveDispatched && probe.Calls == 0, "S02 guessed call refused");
            var selected = await runtime.ExecuteAsync(call(load.Id, "{\"id\":\"fixture.skill\"}"), CancellationToken.None);
            AssertEqual(ToolExecutionOutcome.Ok, selected.Outcome, "S03 canonical loader");
            await runtime.ExecuteAsync(call(tool.Id, "{}"), CancellationToken.None);
            AssertEqual(0, probe.Calls, "S04 same-response call remains forbidden after loader");
            session.LastContextReceipt = CompilePackage(session, skills, pack, catalog).Receipt;
            await runtime.ExecuteAsync(call(tool.Id, "{}"), CancellationToken.None);
            AssertEqual(1, probe.Calls, "next request dispatches exact handler");
            currentPack = PackageTools(PackageTool(binding: "replacement"), load);
            var changed = await runtime.ExecuteAsync(call(tool.Id, "{}"), CancellationToken.None);
            AssertTrue(!changed.MayHaveDispatched && probe.Calls == 1, "S10/S11 rebind refused before dispatch");
            currentPack = pack;
            var original = call(tool.Id, "{}");
            var origin = new RNAssistant.Core.Persistence.AgentRunState(new RunSummary("run", "run", RunLifecycle.AwaitingConfirmation,
                new ToolCounts(), 1, 1, "", "confirmation_required",
                new PendingConfirmation("pending", original.Call, original.Policy, "step", 1)), new AgentRunLimits(5, 5));
            Func<ToolCall, string, string, bool, ToolExecutionContext> resumed = (accepted, turn, step, confirmed) =>
                new ToolExecutionContext(accepted, original.Policy, "new-invocation", turn, step, DateTime.UtcNow, confirmed, 4);
            AssertEqual(null, SkillTaskService.CheckDispatch(session, resumed(original.Call, "run", "step", true), pack, origin),
                "exact approved call retains its originating request across invocation ids");
            foreach (var invalid in new[] {
                resumed(original.Call, "other-task", "step", true), resumed(original.Call, "run", "other-step", true),
                resumed(original.Call, "run", "step", false),
                resumed(new ToolCall("other-call", tool.Id, "{}"), "run", "step", true),
                resumed(new ToolCall(original.Call.Id, tool.Id, "{\"changed\":true}"), "run", "step", true) })
                AssertTrue(SkillTaskService.CheckDispatch(session, invalid, pack, origin) != null, "confirmation origin cannot authorize another call");
            AssertTrue(SkillTaskService.CheckDispatch(session, resumed(original.Call, "run", "step", true),
                PackageTools(PackageTool(binding: "replacement"), load), origin) != null, "approved origin does not bypass binding revocation");
            await runtime.ExecuteAsync(call(load.Id, "{\"id\":\"fixture.skill\",\"action\":\"remove\"}"), CancellationToken.None);
            AssertEqual(0, session.LastRun.SelectedSkills.Count, "explicit removal uses same task transition");
        }

        private sealed class PackageProbeHandler : IReadOnlyToolHandler
        {
            public int Calls;
            public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
            { Calls++; return Task.FromResult(new ToolHandlerResult(RNAssistant.Core.Tools.Contracts.ToolResult.Ok("read", "{}"))); }
        }

        private static void SkillPackagesRestartAndSupportingFiles()
        {
            WithTempPaths(paths =>
            {
                var store = new SkillStore(paths); var skill = store.SaveOne(PackageSkill("fixture.saved", "fixture.special"));
                string error; SkillReferenceMetadata reference;
                AssertTrue(store.TrySaveReference(skill, "scripts/check.py", "print('not executed')", out reference, out error), "script stored inertly: " + error);
                skill = store.Load().Single();
                AssertTrue(store.TrySaveReference(skill, "assets/template.json", "{\"total\":0}", out reference, out error), "template stored: " + error);
                skill = store.Load().Single();
                AssertEqual(2, skill.References.Count, "supporting files share package revisions");
                AssertEqual("fixture.special", skill.Package.RequiredTools.Single().Id, "manifest survives authoring storage");
                AssertEqual(SkillRevision.Compute(skill), SkillPackageSource.Capture(skill).Revision, "immutable package includes dependencies");
                AssertTrue(!SkillStore.TryNormalizeReferencePath("scripts/../escape.py", out error), "S24 traversal refused");
                var pack = PackageTools(PackageTool()); var catalog = new[] { PackageTool() }; var skills = new SkillCatalogSnapshot(new[] { skill });
                var session = PackageSession(); SkillTaskService.Select(session, skill.Id, skills, pack, new AppSettings(), "load");
                session.LastContextReceipt = CompilePackage(session, skills, pack, catalog).Receipt;
                new ChatStore(paths).Save(session);
                var restored = new ChatStore(paths).Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(skill.Id, restored.LastRun.SelectedSkills.Single().Id, "S15 run.changed replays selection");
                var recompiled = CompilePackage(restored, skills, pack, catalog);
                AssertTrue(recompiled.Messages.Any(item => item.Content.Contains(skill.BodyMarkdown)), "S13/S15 complete exact instructions rehydrated with no load frame");
                var view = AgentArsenalProjection.Build(restored, recompiled.Receipt.Arsenal, skills.Metadata, null);
                AssertEqual("selected", view.Selected.Single().Status, "S27/S31 UI projects restored selection");
                AssertEqual(1, restored.LastRun.SelectedSkills.Count, "opening arsenal is inert");
            });
        }
    }
}
