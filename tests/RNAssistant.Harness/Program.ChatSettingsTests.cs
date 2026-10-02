using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ChatSettingsUseSessionModelWithoutMutatingGlobalSettings()
        {
            var settings = new AppSettings { Model = "global-model" };
            settings.CustomHeaders["X-Test"] = "before";
            var session = new ChatSession { Model = "  chat-model  " };

            var effective = ChatSettingsResolver.Resolve(settings, session);

            AssertEqual("chat-model", effective.Model, "effective chat model");
            AssertEqual("global-model", settings.Model, "global model");
            effective.CustomHeaders["X-Test"] = "after";
            AssertEqual("before", settings.CustomHeaders["X-Test"], "settings clone");

            session.Model = " ";
            effective = ChatSettingsResolver.Resolve(settings, session);
            AssertEqual("global-model", effective.Model, "blank chat model fallback");
        }

        private static void PromptResourcesReadPublishedTemplates()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var original = "\ufeff# Published prompt\r\n" + new string('ж', 40000) + "😀";
                var settings = new AppSettings { SystemPrompt = original, PlanSystemPrompt = string.Empty };
                var loads = 0;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                    () => { loads++; return settings; }, value => settings = value, paths);
                executor.CaptureCatalogs();
                var session = NewSession(adapter);
                var tools = executor.GetControllerTools().ToList();
                var native = executor.CreateNativeRuntime(session, tools, new AppSettings(), ChatModes.Agent, false);
                Func<string, ToolExecutionRecord> read = target => ExecutePromptNative(native, ResourceToolCatalog.ReadToolId, new JObject { ["target"] = target });
                loads = 0;
                var retired = new ToolCall("retired", "common.prompts_read", "{\"includeDefaults\":true}");
                AssertTrue(tools.All(item => item.Id != retired.Name) && native.Describe(retired) == null &&
                    DirectToolBindingCatalog.Resolve(retired.Name) == null, "old prompt reader has no catalog entry, binding or alias");
                string error;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(retired, out error), "old direct-settings reads cannot replay as current model evidence");
                var found = ExecutePromptNative(native, ResourceToolCatalog.FindToolId, new JObject { ["scope"] = "catalogs", ["query"] = "systemPrompt" });
                AssertEqual(ToolExecutionOutcome.Ok, found.Outcome, "published prompt discovery succeeds");
                var targets = ((JArray)JObject.Parse(found.Result.DataJson)["items"]).Select(item => (string)item["target"]).ToList();
                AssertTrue(targets.Contains("prompt: systemPrompt") && targets.Contains("prompt default: systemPrompt"), "current and default have distinct semantic targets");
                AssertTrue(!found.Result.DataJson.Contains("Published prompt") && !found.Result.DataJson.Contains("rna://"), "discovery exposes neither bodies nor runtime plumbing");
                var fields = executor.ResourceGateway.List(session, "catalog", "prompt", null, 50).Items;
                AssertEqual(string.Join(",", PromptSettingsService.TemplateKeys.OrderBy(key => key)),
                    string.Join(",", fields.Select(item => item.Title).OrderBy(key => key)), "only the editable prompt keys are discoverable");
                AssertTrue(fields.All(item => item.Payload == null), "listing prompt fields never hydrates their bodies");
                var first = read("prompt: systemPrompt");
                AssertEqual(ToolExecutionOutcome.Ok, first.Outcome, "current prompt reads through the generic resource handler");
                AssertEqual(original, (string)JObject.Parse(first.Result.DataJson)["text"], "complete Unicode/BOM/CRLF text survives exact internal paging");
                var defaults = read("prompt default: systemPrompt");
                AssertEqual(new AppSettings().SystemPrompt, (string)JObject.Parse(defaults.Result.DataJson)["text"], "defaults come from the committed builtin publication");
                AssertEqual(string.Empty, (string)JObject.Parse(read("prompt: planSystemPrompt").Result.DataJson)["text"], "an empty published prompt stays empty");
                AssertEqual(settings.SystemPromptRole, (string)JObject.Parse(read("prompt: systemPromptRole").Result.DataJson)["text"], "role remains a separate exact value");
                AssertEqual(0, loads, "discovery and source reads do not reload mutable settings");
                AssertEqual(original, settings.SystemPrompt, "reading defaults does not reset or activate them");
                var evidence = first.ResourceEvidence.Single();
                var defaultEvidence = defaults.ResourceEvidence.Single();
                var scope = CatalogPublicationService.ScopeId;
                AssertTrue(evidence.Complete && evidence.Payload != null && evidence.Dependencies.Single().Resource.Uri == "rna://catalog/prompts",
                    "prompt evidence retains exact source and its publication dependency");
                var reducer = new EvidenceStateReducer();
                AssertEqual(EvidenceState.Current, reducer.Reduce(evidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State, "original prompt evidence is current");
                var saved = executor.ExecuteManual(Command(PromptToolCatalog.SaveToolId, "promptKey", "systemPrompt", "value", "# Replacement"),
                    tools, new AppSettings(), false, true, session);
                AssertTrue(saved.Success, "existing guarded save publishes the replacement: " + saved.Message);
                AssertEqual(EvidenceState.Superseded, reducer.Reduce(evidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State, "saving invalidates prior prompt evidence through the shared reducer");
                AssertEqual(EvidenceState.Current, reducer.Reduce(defaultEvidence, executor.ResourceAuthority.CaptureMany(new[] { scope })).State, "editing current prompts does not replace builtin defaults");
                AssertEqual(original.Substring(0, 64), executor.ResourceGateway.Read(session, new ResourceReadRequest {
                    Reference = evidence.Resource, Representation = "text", MaxChars = 64 }).Result.Text, "historical prompt remains exact after save");
                settings.SystemPrompt = "UNPUBLISHED"; loads = 0;
                AssertEqual("# Replacement", (string)JObject.Parse(read("prompt: systemPrompt").Result.DataJson)["text"], "mutable settings cannot bypass publication");
                AssertEqual(0, loads, "later reads still use only the committed snapshot");
                AssertEqual(0, adapter.VbaBackendCalls.Count, "prompt inspection never discovers live Office/VBA");
            });
        }

        private static void PromptResourcesFailClosed()
        {
            foreach (var scenario in new[] { "prompts", "prompt-defaults", "oversized", "missing-value" })
                WithTempPaths(paths =>
                {
                    var adapter = FakeOfficeAdapter.ForHost("Word");
                    var settings = new AppSettings();
                    if (scenario == "oversized") settings.SystemPrompt = new string('x', PromptSettingsService.MaximumPromptCharacters + 1);
                    if (scenario == "missing-value") settings.SystemPrompt = null;
                    var loads = 0;
                    var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), new ToolStore(paths),
                        () => { loads++; return settings; }, value => settings = value, paths);
                    executor.CaptureCatalogs();
                    var session = NewSession(adapter);
                    var native = executor.CreateNativeRuntime(session, executor.GetControllerTools().ToList(), new AppSettings(), ChatModes.Agent, false);
                    var scope = CatalogPublicationService.ScopeId;
                    var kind = scenario == "prompt-defaults" ? CatalogPublicationService.PromptDefaultsKind : "prompts";
                    var root = executor.ResourceAuthority.Store.GetHead(scope, new ResourceIdentity(ResourceUri.Create("catalog", kind))).Revision;
                    if (scenario == "prompts" || scenario == "prompt-defaults")
                    {
                        var payload = ((IResourceRevisionStore)executor.ResourceAuthority.Store).GetRevision(scope, root).Payload;
                        File.WriteAllText(executor.Payloads.PathFor(payload.Sha256), "corrupt");
                    }
                    var generation = executor.ResourceAuthority.CaptureMany(new[] { scope }).Get(scope).Generation;
                    loads = 0;
                    var read = ExecutePromptNative(native, ResourceToolCatalog.ReadToolId,
                        new JObject { ["target"] = kind == "prompts" ? "prompt: systemPrompt" : "prompt default: systemPrompt" });
                    AssertEqual(ToolExecutionOutcome.Error, read.Outcome, "unavailable prompt fails closed: " + scenario);
                    AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", (string)JObject.Parse(read.Result.DataJson)["code"], "typed prompt snapshot failure: " + scenario);
                    AssertEqual(0, read.ResourceEvidence.Count, "no fabricated evidence on prompt failure");
                    AssertEqual(0, loads, "failure never falls back to current settings or regenerates defaults");
                    AssertEqual(generation, executor.ResourceAuthority.CaptureMany(new[] { scope }).Get(scope).Generation, "read failure cannot heal or republish authority");
                    var unknown = ExecutePromptNative(native, ResourceToolCatalog.ReadToolId, new JObject { ["target"] = "prompt: apiKey" });
                    AssertEqual(ToolExecutionOutcome.Error, unknown.Outcome, "non-template settings cannot become prompt resources");
                });
        }

        private static void PromptSavePreservesGlobalModel()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var global = new AppSettings { Model = "global-model", SystemPrompt = "Old prompt" };
                var executor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => global,
                    value => global = value);
                var runtime = global.Clone();
                runtime.Model = "per-chat-model";
                var definitions = executor.GetControllerTools()
                    .Where(tool => PromptToolCatalog.Owns(tool.Id))
                    .ToList();
                AssertEqual(1, definitions.Count,
                    "complete prompt family is registered");
                var saveDefinition = definitions.Single(tool =>
                    tool.Id == PromptToolCatalog.SaveToolId);
                AssertEqual(ToolEffect.Write,
                    saveDefinition.Policy.Effect,
                    "prompt save effect");
                AssertEqual(ToolVerification.Tool,
                    saveDefinition.Policy.Verification,
                    "prompt save verification");
                AssertTrue(saveDefinition.Policy.RequiresConfirmation,
                    "prompt save requires confirmation");
                AssertTrue(saveDefinition.ArgumentSchemaJson.Length <
                        CapabilityCatalogService.MaximumDescriptorCharacters,
                    "prompt save descriptor remains discoverable");
                AssertEqual("agent",
                    string.Join(",", saveDefinition.Policy.AllowedModes),
                    "prompt tools are Agent-only");

                var native = executor.CreateNativeRuntime(
                    NewSession(adapter), definitions,
                    new AppSettings { AutoConfirmToolActions = false },
                    "agent", false,
                    (execution, preparation) => "prompt_pending");
                AssertTrue(native.Describe(new ToolCall(
                        "prompt_save_policy", PromptToolCatalog.SaveToolId,
                        "{}")) != null,
                    "exact prompt save has a native binding");
                AssertTrue(native.Describe(new ToolCall(
                        "prompt_alias", PromptToolCatalog.SaveToolId
                            .ToUpperInvariant(), "{}")) == null,
                    "prompt save has no case alias");

                var empty = executor.ExecuteManual(
                    new ToolInvocation { ToolId = "common.prompts_save" },
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    runtime,
                    false,
                    false);
                AssertTrue(!empty.Success, "empty prompt save fails before confirmation");
                AssertEqual("invalid_arguments", empty.ErrorCode,
                    "empty prompt save is rejected by its schema");

                var guardedArguments = new JObject
                {
                    ["promptKey"] = "systemPrompt",
                    ["value"] = "Guarded prompt"
                };
                var pending = ExecutePromptNative(
                    native, PromptToolCatalog.SaveToolId, guardedArguments);
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,
                    pending.Outcome,
                    "prompt save waits for confirmation");
                AssertTrue(!string.IsNullOrWhiteSpace(
                        pending.PreparedStateJson),
                    "prompt save persists an exact preparation guard");
                AssertEqual("Old prompt", global.SystemPrompt,
                    "preparation does not mutate settings");
                var guarded = ConfirmPromptNative(native, pending);
                AssertEqual(ToolExecutionOutcome.Ok, guarded.Outcome,
                    "confirmed prompt save succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    guarded.Evidence.Dispatch,
                    "prompt save marks its dispatch boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    guarded.Evidence.Effect,
                    "prompt save verifies the written value");
                AssertEqual("Guarded prompt", global.SystemPrompt,
                    "confirmed prompt save mutates settings");

                var unchangedPending = ExecutePromptNative(
                    native, PromptToolCatalog.SaveToolId, guardedArguments);
                var unchanged = ConfirmPromptNative(
                    native, unchangedPending);
                AssertEqual(ToolExecutionOutcome.Ok, unchanged.Outcome,
                    "unchanged prompt save succeeds");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    unchanged.Evidence.Dispatch,
                    "unchanged prompt save avoids dispatch");
                AssertEqual(ToolEffectEvidence.VerifiedNoChange,
                    unchanged.Evidence.Effect,
                    "unchanged prompt save is explicit");

                var stalePending = ExecutePromptNative(
                    native, PromptToolCatalog.SaveToolId,
                    new JObject
                    {
                        ["promptKey"] = "systemPrompt",
                        ["value"] = "Intended prompt"
                    });
                global.SystemPrompt = "External prompt";
                var stale = ConfirmPromptNative(native, stalePending);
                AssertEqual(ToolExecutionOutcome.Error, stale.Outcome,
                    "stale prompt preparation is rejected");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    stale.Evidence.Dispatch,
                    "stale prompt save does not dispatch");
                AssertContains(stale.Result.DataJson,
                    "prompt_settings_changed",
                    "stale prompt save exposes a stable error code");
                AssertEqual(ToolFailureKind.RejectedNoEffect,
                    stale.Recovery.FailureKind,
                    "stale prompt preparation certifies no settings effect");
                AssertEqual(ToolRetryPolicy.Replan,
                    stale.Recovery.RetryPolicy,
                    "stale prompt preparation requires refreshed intent");

                var ignored = new AppSettings
                {
                    Model = "ignored-model",
                    SystemPrompt = "Ignored old prompt"
                };
                var mismatchExecutor = new OfficeToolExecutor(
                    adapter,
                    new VbaJournalStore(paths),
                    new SkillStore(paths),
                    new ToolStore(paths),
                    () => ignored,
                    value => { });
                var mismatchDefinitions = mismatchExecutor
                    .GetControllerTools()
                    .Where(tool => PromptToolCatalog.Owns(tool.Id))
                    .ToList();
                var mismatchRuntime = mismatchExecutor.CreateNativeRuntime(
                    NewSession(adapter), mismatchDefinitions,
                    new AppSettings { AutoConfirmToolActions = true },
                    "agent", false);
                var mismatch = ExecutePromptNative(
                    mismatchRuntime, PromptToolCatalog.SaveToolId,
                    new JObject
                    {
                        ["promptKey"] = "systemPrompt",
                        ["value"] = "Ignored new prompt"
                    });
                AssertEqual(ToolExecutionOutcome.Unknown, mismatch.Outcome,
                    "failed prompt read-back is unknown");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    mismatch.Evidence.Dispatch,
                    "failed prompt read-back retains dispatch evidence");
                AssertEqual(ToolEffectEvidence.Unknown,
                    mismatch.Evidence.Effect,
                    "failed prompt read-back retains unknown effect");

                var promptTools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                foreach (var pair in new[]
                {
                    new[] { "systemPrompt", "New prompt" },
                    new[] { "agentToolsPrompt", "New tool prompt" },
                    new[] { "agentSkillsPrompt", "New skill prompt" },
                    new[] { "attachmentAnalysisPrompt", "New attachment prompt" }
                })
                {
                    var result = executor.ExecuteManual(
                        Command("common.prompts_save", "promptKey", pair[0],
                            "value", pair[1]), promptTools, runtime,
                        false, true);
                    AssertTrue(result.Success,
                        "one-key prompt save succeeds: " + pair[0]);
                }
                AssertEqual("New prompt", global.SystemPrompt, "global prompt updated");
                AssertEqual("New tool prompt", global.AgentToolsPrompt, "tool prompt updated");
                AssertEqual("New skill prompt", global.AgentSkillsPrompt, "skill prompt updated");
                AssertEqual("New attachment prompt", global.AttachmentAnalysisPrompt, "attachment prompt updated");
                AssertEqual("global-model", global.Model, "per-chat model is not copied into global settings");
            });
        }

        private static ToolExecutionRecord ExecutePromptNative(
            NativeToolRuntimeAdapter runtime,
            string toolId,
            JObject arguments)
        {
            var call = new ToolCall(
                "prompt_" + Guid.NewGuid().ToString("N"),
                toolId,
                (arguments ?? new JObject()).ToString(Formatting.None));
            var policy = runtime.Describe(call);
            if (policy == null)
                throw new InvalidOperationException(
                    "Prompt native policy was not captured: " + toolId);
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        call, policy, "run-prompt-native",
                        "turn-prompt-native", call.Id + ":1",
                        DateTime.UtcNow, false, 5),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static ToolExecutionRecord ConfirmPromptNative(
            NativeToolRuntimeAdapter runtime,
            ToolExecutionRecord pending)
        {
            if (pending == null ||
                pending.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException(
                    "A native pending prompt save is required.");
            var source = pending.Context;
            return runtime.ExecuteAsync(
                    new ToolExecutionContext(
                        source.Call, source.Policy, source.RunId,
                        source.TurnId, source.StepId, DateTime.UtcNow,
                        true, 5, pending.PreparedStateJson),
                    CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        private static void SettingsMigratePromptsOnSchemaChange()
        {
            foreach (var version in new[] { 0, AppSettings.CurrentAgentPromptSchemaVersion - 1,
                AppSettings.CurrentAgentPromptSchemaVersion, AppSettings.CurrentAgentPromptSchemaVersion + 1 })
            {
                var json = "{\"SystemPrompt\":\"custom general\",\"AgentToolsPrompt\":\"custom tools\"," +
                    "\"ChatSystemPrompt\":\"custom chat\",\"PlanSystemPrompt\":\"custom plan\"," +
                    "\"AgentPromptSchemaVersion\":" + version + "}";
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                settings.NormalizeAgentPrompts();
                AssertEqual(AppSettings.CurrentAgentPromptSchemaVersion, settings.AgentPromptSchemaVersion,
                    "normalization selects the active built-in schema");
                AssertEqual(version == AppSettings.CurrentAgentPromptSchemaVersion ? "custom general" : AgentPromptDefaults.GeneralInstructions,
                    settings.SystemPrompt, "only an unchanged schema retains a custom prompt");
                AssertEqual(version == AppSettings.CurrentAgentPromptSchemaVersion ? "custom chat" : AgentPromptDefaults.ChatInstructions,
                    settings.ChatSystemPrompt, "Chat prompt follows the same migration");
                AssertEqual(version == AppSettings.CurrentAgentPromptSchemaVersion ? "custom plan" : AgentPromptDefaults.PlanInstructions,
                    settings.PlanSystemPrompt, "Plan prompt follows the same migration");
            }
        }

        private static void SettingsPromptMigrationArchivesStoredText()
        {
            WithTempPaths(paths =>
            {
                var service = new SettingsService(paths);
                var legacy = new AppSettings
                {
                    AgentPromptSchemaVersion = 12,
                    SystemPrompt = "custom general", ChatSystemPrompt = "custom chat",
                    PlanSystemPrompt = "custom plan", ContextCompactionPrompt = "custom compaction",
                    ChatTitlePrompt = "custom title", AttachmentAnalysisPrompt = "custom media", Model = "before"
                };
                new JsonFileStore().Save(paths.SettingsFile, legacy);
                var originalFile = File.ReadAllText(paths.SettingsFile);
                var loaded = service.Load();
                AssertEqual(AgentPromptDefaults.GeneralInstructions, loaded.SystemPrompt, "load uses current built-in Agent prompt");
                AssertEqual(new AppSettings().ContextCompactionPrompt, loaded.ContextCompactionPrompt,
                    "helper prompts are migrated too");
                AssertEqual(originalFile, File.ReadAllText(paths.SettingsFile), "load leaves the durable source available until save");
                loaded.Model = "after";
                service.Save(loaded);
                var saved = service.Load();
                AssertEqual("after", saved.Model, "unrelated controls survive prompt migration");
                AssertEqual(AppSettings.CurrentAgentPromptSchemaVersion, saved.AgentPromptSchemaVersion,
                    "save persists the migrated schema");
                var directory = Path.Combine(paths.Root, "prompt-backups");
                var backups = Directory.GetFiles(directory, "prompts-v12-*.json");
                AssertEqual(1, backups.Length, "one recovery copy is written before migration");
                var backup = JObject.Parse(File.ReadAllText(backups[0]));
                AssertEqual("custom general", backup.Value<string>("SystemPrompt"), "previous Agent text is recoverable");
                AssertEqual("custom compaction", backup.Value<string>("ContextCompactionPrompt"),
                    "previous helper text is recoverable");
                service.Save(saved);
                AssertEqual(1, Directory.GetFiles(directory, "*.json").Length, "later saves do not duplicate the backup");
            });
        }

        private static void BuiltInPromptGuidanceUsesRuntimeIdsAndToolResultV1()
        {
            var skills = BuiltInSkillProvider.GetSkills(FakeOfficeAdapter.ForHost("Excel"));
            var authoring = skills.Single(skill => skill.Id == "common.prompt_authoring").BodyMarkdown;
            AssertContains(authoring, "Each call contains only exact `name` and root object `arguments`",
                "prompt authoring keeps the model call wire free of runtime IDs");
            AssertContains(authoring, "Runtime assigns call IDs after validation and owns execution outcomes",
                "prompt authoring assigns identity and outcomes to runtime");
            AssertContains(authoring, "SystemPrompt` owns universal operating order",
                "prompt authoring separates universal lifecycle from domain guidance");
            AssertContains(authoring, "exact input/output details in tool descriptions",
                "prompt authoring keeps schemas and tool semantics authoritative");
            AssertTrue(authoring.IndexOf("Each call needs a unique id", StringComparison.OrdinalIgnoreCase) < 0,
                "R31 model-owned identity guidance cannot return");
            var htmlAuthoring = skills.Single(skill => skill.Id == "common.html_workspace_authoring").BodyMarkdown;
            AssertContains(htmlAuthoring, "exact decoded strings",
                "HTML authoring preserves model-provided source text exactly");
            AssertContains(htmlAuthoring, "Runtime stores decoded text unchanged",
                "HTML authoring forbids a second source unescape");
            AssertContains(htmlAuthoring, "echarts.getInstanceByDom(node) || echarts.init(node)",
                "HTML authoring avoids duplicate bundled chart instances");
            AssertContains(htmlAuthoring, "Do not invent globals, copy bundles",
                "HTML authoring rejects unsupported or duplicate vendor runtimes");
            AssertContains(htmlAuthoring, "do not also call `excel.create_chat_chart` for the same data",
                "HTML authoring keeps ECharts workspace and chat-chart artifacts from duplicating one visual");
            AssertContains(htmlAuthoring, "root `arguments` contains exactly `path` and `content`",
                "HTML writes put semantic properties directly at the schema root");
            foreach (var file in new[] { "`index.html`", "`styles.css`", "`app.js`" })
                AssertContains(htmlAuthoring, file, "substantial HTML workspaces split responsibilities");
            AssertContains(htmlAuthoring, "lists it read-only under Dependencies",
                "HTML authoring exposes runtime-owned dependencies");
            AssertContains(htmlAuthoring, "`XLSX` (SheetJS CE 0.20.3 full browser build)",
                "HTML authoring advertises the offline spreadsheet reader");
            AssertContains(htmlAuthoring, "`common.html_assets_list` only when",
                "HTML authoring discovers reusable packages on demand");
            AssertContains(htmlAuthoring, "`common.html_assets_publish`",
                "HTML authoring can save explicitly requested reusable source");
            AssertContains(htmlAuthoring, "bind `view=raw`",
                "HTML authoring connects uploaded spreadsheets to the binary resource plane");
            AssertContains(htmlAuthoring, "ResizeObserver",
                "HTML authoring resizes charts with their containers");
            AssertContains(htmlAuthoring, "CSS custom properties",
                "HTML authoring defines a coherent interface system");
            AssertContains(htmlAuthoring, "native buttons/selects/inputs",
                "HTML authoring requires accessible controls");
            AssertContains(htmlAuthoring, "inspect those sources first",
                "source-backed HTML inspects workbook and VBA prerequisites first");
            AssertContains(htmlAuthoring, "Never replace a rich implementation with a simplified placeholder",
                "HTML validation repair preserves the requested implementation");
            AssertContains(htmlAuthoring, "bindings have exact view/coverage evidence",
                "HTML guidance requires exact binding evidence");
            AssertContains(htmlAuthoring, "claimed refresh has bounded read-back plus visible render evidence",
                "HTML guidance requires read-back and visible render evidence");
            AssertContains(htmlAuthoring, "read a supported complete representation with `common.resources_read`, then call `common.html_data_bind`",
                "source-backed HTML establishes its binding from a complete read");
            AssertContains(htmlAuthoring, "Use exact saved binding names",
                "HTML page code uses the saved data contract");
            AssertContains(htmlAuthoring, "declared column keys, bounded batches and explicit loading/error states",
                "HTML guidance requires bounded data delivery and visible failures");
            AssertContains(htmlAuthoring, "Refresh observes canonical source authority without rewriting workspace history",
                "HTML refresh does not create a duplicate durable source");
            AssertContains(htmlAuthoring, "visible render evidence",
                "HTML guidance requires proof that refreshed JSON reached the page");
            AssertContains(htmlAuthoring, "Resource-bound pages require a resource host or an exact exported resource bundle",
                "HTML guidance distinguishes bound data from a standalone page");
            AssertContains(htmlAuthoring, "preflight has zero errors",
                "HTML definition of done includes static validation");
            AssertContains(htmlAuthoring, "Static preflight does not prove browser execution",
                "HTML guidance distinguishes static and runtime evidence");
            AssertContains(htmlAuthoring, "do not invent unrelated widgets or decorative filler",
                "HTML guidance rejects invented template filler");
            AssertTrue(htmlAuthoring.IndexOf("KPI", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML guidance does not prescribe a KPI dashboard layout");
            AssertTrue(htmlAuthoring.IndexOf("card", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML guidance does not prescribe card-based layouts");
            AssertTrue(htmlAuthoring.IndexOf("dashboard.js", StringComparison.OrdinalIgnoreCase) < 0,
                "HTML guidance does not prescribe dashboard-specific file names");
            var skillAuthoring = skills.Single(skill => skill.Id == "common.skill_authoring").BodyMarkdown;
            AssertContains(skillAuthoring, "author the skill last",
                "a requested reusable skill follows the verified primary solution");
            AssertContains(skillAuthoring, "Do not invent a skill",
                "skill authoring is not an unsolicited substitute for execution");
            var taskTracking = skills.Single(skill => skill.Id == "common.task_tracking").BodyMarkdown;
            AssertContains(taskTracking, "when it helps retain work across steps",
                "task tracking preserves multiple requested outcomes without an artificial minimum");
            AssertContains(taskTracking, "No source read or edit is required to save a plan",
                "planning is not coupled to source admission");
            AssertContains(taskTracking, "An open Task List does not prevent a final answer",
                "task tracking leaves final decisions to the model");
            foreach (var skill in skills)
                AssertTrue(skill.BodyMarkdown.IndexOf("TOOL_RESULT ok=true", StringComparison.OrdinalIgnoreCase) < 0,
                    skill.Id + " does not teach the removed result success flag");
            foreach (var id in new[] { "common.prompt_authoring", "common.task_tracking" })
            {
                var body = skills.Single(skill => skill.Id == id).BodyMarkdown;
                AssertContains(body, "TOOL_RESULT status=ok", id + " uses the active terminal success state");
                AssertContains(body, "does not by itself prove an applied effect", id + " distinguishes success from effect evidence");
            }
            var defaults = new AppSettings();
            foreach (var prompt in new[] { defaults.SystemPrompt, defaults.ChatSystemPrompt, defaults.PlanSystemPrompt })
            {
                AssertContains(prompt, "contains only `tool_call_id`, `name`, `status`, `message`, `data`, and optional `resources`",
                    "every mode teaches the same bounded result envelope");
                AssertContains(prompt, "`status` is exactly `ok`, `error`, or `unknown`", "no extra model-facing result states");
                AssertContains(prompt, "does not by itself prove an applied effect", "defaults require actual effect evidence");
                AssertContains(prompt,
                    "HTML, Prompt/Tool/Skill authoring, and VBA/macro tools",
                    "current prompt schema includes authoring in runtime-only result projection guidance");
                AssertTrue(prompt.IndexOf("ok=true", StringComparison.OrdinalIgnoreCase) < 0, "defaults do not teach the legacy success flag");
            }
            AssertContains(defaults.SystemPrompt, "1. **Understand.** Translate the request into explicit deliverables",
                "Agent begins by establishing deliverables and evidence");
            AssertContains(defaults.SystemPrompt, "Inspect the source structure and key examples",
                "Agent inspects requested sources before construction");
            AssertContains(defaults.SystemPrompt, "only after the primary solution is implemented and verified",
                "Agent follows source, deliverable, verification and reuse dependency order");
            AssertContains(defaults.SystemPrompt, "Choose checks proportionate to the changed behavior",
                "Agent selects useful verification from context");
            AssertContains(defaults.SystemPrompt, "An open Task List does not prevent a final answer",
                "saved planning state is advisory");
            AssertContains(defaults.SystemPrompt, "No task-list call is a prerequisite for final",
                "Agent does not perform bookkeeping to bypass a gate");
            AssertContains(defaults.SystemPrompt, "cannot become success prose",
                "tool and protocol errors cannot be reported as completed work");
            AssertContains(defaults.SystemPrompt, "simplified placeholder",
                "Agent does not degrade an artifact to bypass validation");
            AssertContains(defaults.AgentToolsPrompt, "Use a Task List when it helps retain remaining work",
                "tracking is selected for useful continuity");
            AssertContains(defaults.AgentToolsPrompt, "Load only missing skill bodies and schemas actually needed",
                "planning does not force repeated loading or duplicate artifacts");
            AssertContains(defaults.AgentToolsPrompt, "add, remove, rewrite or reorder stages",
                "tool policy permits reasoned replanning");
            AssertContains(defaults.ContextCompactionPrompt, "compaction does not require another capabilities_read or admission",
                "compaction guidance agrees with durable tool admission");
            AssertContains(defaults.SystemPrompt, "Report unfinished work or a concrete blocker honestly",
                "ending the answer does not imply completed deliverables");
            AssertContains(defaults.AgentToolsPrompt, "never add an inner `arguments`",
                "tool arguments are supplied at the schema root");
            AssertContains(defaults.AgentToolsPrompt, "Skills define domain workflow and quality criteria",
                "tool policy defines authority between skill guidance and schemas");
            AssertContains(defaults.AgentSkillsPrompt, "smallest complete set of clearly applicable skills",
                "Agent selects and loads applicable skills before domain mutation");
        }

        private static void BuiltInSkillReferencesResolveToCatalogs()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var toolIds = new HashSet<string>(StringComparer.Ordinal);
                var skillIds = new HashSet<string>(StringComparer.Ordinal);
                IReadOnlyList<SkillDefinition> commonSkills = null;
                foreach (var host in new[] { "Excel", "Word", "PowerPoint", "Outlook" })
                {
                    var adapter = FakeOfficeAdapter.ForHost(host);
                    var settings = new AppSettings();
                    var executor = new OfficeToolExecutor(
                        adapter,
                        new VbaJournalStore(paths),
                        new SkillStore(paths),
                        new ToolStore(paths),
                        () => settings,
                        value => settings = value,
                        paths);
                    toolIds.UnionWith(OfficeToolCatalog.ForHost(host).Select(tool => tool.Id));
                    toolIds.UnionWith(executor.GetControllerTools().Select(tool => tool.Id));

                    var skills = BuiltInSkillProvider.GetSkills(adapter);
                    AssertEqual(skills.Count, skills.Select(skill => skill.Id).Distinct(StringComparer.Ordinal).Count(),
                        host + " built-in skill ids are unique");
                    foreach (var skill in skills)
                    {
                        AssertTrue(!string.IsNullOrWhiteSpace(skill.Description), skill.Id + " has a description");
                        AssertTrue(!string.IsNullOrWhiteSpace(skill.BodyMarkdown), skill.Id + " has a body");
                        AssertTrue(skill.BodyMarkdown.IndexOf("TOOL_RESULT ok=true", StringComparison.OrdinalIgnoreCase) < 0,
                            skill.Id + " does not teach the retired result flag");
                        skillIds.Add(skill.Id);
                    }
                    if (commonSkills == null)
                        commonSkills = skills.Where(skill => string.Equals(skill.Host, "Common", StringComparison.Ordinal)).ToArray();
                }

                Action<string, string> assertReferences = (owner, text) =>
                {
                    foreach (Match match in Regex.Matches(text ?? string.Empty,
                        @"\b(?:common|excel|word|powerpoint|outlook)\.[a-z0-9_]+\b",
                        RegexOptions.CultureInvariant))
                    {
                        if (match.Index + match.Length < text.Length && text[match.Index + match.Length] == '*')
                            continue;
                        AssertTrue(toolIds.Contains(match.Value) || skillIds.Contains(match.Value),
                            owner + " references cataloged capability " + match.Value);
                    }
                };

                foreach (var skill in commonSkills ?? new SkillDefinition[0])
                    assertReferences(skill.Id, skill.BodyMarkdown);

                var root = FindHarnessRepositoryRoot();
                foreach (var relativePath in new[]
                {
                    "src/RNAssistant.OfficeHosts/ExcelAdapter.cs",
                    "src/RNAssistant.OfficeHosts/WordAdapter.cs",
                    "src/RNAssistant.OfficeHosts/PowerPointAdapter.cs",
                    "src/RNAssistant.OfficeHosts/OutlookAdapter.cs"
                })
                {
                    var source = File.ReadAllText(Path.Combine(root,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    assertReferences(relativePath, source);
                    AssertContains(source, "## Definition of done",
                        relativePath + " defines evidence-based completion");
                    AssertContains(source, "Exact loaded tool schemas remain authoritative",
                        relativePath + " keeps argument authority in current schemas");
                }
            });
        }

        private static void SettingsNormalizeInvalidNumericValues()
        {
            AssertEqual(1600, JsonConvert.DeserializeObject<AppSettings>("{}").DesktopWindowWidth,
                "older settings without a width use the new Desktop default");
            var settings = new AppSettings
            {
                Temperature = double.NaN,
                TopP = double.PositiveInfinity,
                UiFontScale = double.NegativeInfinity,
                DesktopWindowWidth = -1
            };
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(0.2, settings.Temperature, "non-finite temperature uses the default");
            AssertEqual(1.0, settings.TopP, "non-finite top-p uses the default");
            AssertEqual(1.0, settings.UiFontScale, "non-finite UI scale uses the default");
            AssertEqual(1600, settings.DesktopWindowWidth, "invalid Desktop width uses the default");

            settings.Temperature = 10;
            settings.UiFontScale = 10;
            settings.DesktopWindowWidth = int.MaxValue;
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(2.0, settings.Temperature, "temperature is clamped to the supported endpoint range");
            AssertEqual(1.30, settings.UiFontScale, "UI scale is clamped to the rendered range");
            AssertEqual(3840, settings.DesktopWindowWidth, "Desktop width is clamped to the supported range");

            settings.DesktopWindowWidth = 500;
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(900, settings.DesktopWindowWidth, "Desktop width respects the minimum form size");

            settings.DesktopWindowWidth = 1450;
            var controls = RNAssistant.Office.Contracts.SettingsControlsDto.From(settings);
            AssertEqual(1450, controls.ApplyTo(new AppSettings()).DesktopWindowWidth,
                "Desktop width survives settings bridge projection and save");
        }
    }
}
