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
                AssertEqual(2, definitions.Count,
                    "complete prompt family is registered");
                var readDefinition = definitions.Single(tool =>
                    tool.Id == PromptToolCatalog.ReadToolId);
                var saveDefinition = definitions.Single(tool =>
                    tool.Id == PromptToolCatalog.SaveToolId);
                AssertEqual(ToolEffect.Read,
                    readDefinition.Policy.Effect,
                    "prompt read effect");
                AssertEqual(ToolVerification.None,
                    readDefinition.Policy.Verification,
                    "prompt read verification");
                AssertTrue(readDefinition.Policy.IndependentLocalRead,
                    "prompt read is an independent local read");
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
                        "prompt_read_policy", PromptToolCatalog.ReadToolId,
                        "{}")) != null,
                    "exact prompt read has a native binding");
                AssertTrue(native.Describe(new ToolCall(
                        "prompt_alias", PromptToolCatalog.ReadToolId
                            .ToUpperInvariant(), "{}")) == null,
                    "prompt read has no case alias");
                var read = ExecutePromptNative(
                    native, PromptToolCatalog.ReadToolId, new JObject());
                AssertEqual(ToolExecutionOutcome.Ok, read.Outcome,
                    "native prompt read succeeds");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    read.Evidence.Dispatch,
                    "prompt read never dispatches an effect");
                AssertEqual(ToolEffectEvidence.None, read.Evidence.Effect,
                    "prompt read reports no effect");

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

        private static void SettingsRequireExplicitPromptReview()
        {
            foreach (var version in new[] { 0, AppSettings.CurrentAgentPromptSchemaVersion - 1,
                AppSettings.CurrentAgentPromptSchemaVersion, AppSettings.CurrentAgentPromptSchemaVersion + 1 })
            {
                var json = "{\"SystemPrompt\":\" custom general \",\"AgentToolsPrompt\":\"custom tools\"," +
                    "\"AgentSkillsPrompt\":\"custom skills\",\"ChatSystemPrompt\":\"custom chat\"," +
                    "\"PlanSystemPrompt\":\"custom plan\"" +
                    (version == 0 ? string.Empty : ",\"AgentPromptSchemaVersion\":" + version) + "}";
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                settings.NormalizeAgentPrompts();
                var blocked = false;
                try { settings.EnsureAgentPromptsReviewed(); }
                catch (InvalidOperationException) { blocked = true; }
                AssertEqual(version != AppSettings.CurrentAgentPromptSchemaVersion, blocked, "only a reviewed schema is runnable");
                AssertEqual(version, settings.AgentPromptSchemaVersion, "normalization never approves an unreviewed schema");
                AssertEqual(" custom general ", settings.SystemPrompt, "custom text and whitespace are preserved");
                AssertEqual("custom tools", settings.AgentToolsPrompt, "custom tool instructions survive normalization");
                AssertEqual("custom skills", settings.AgentSkillsPrompt, "custom skill instructions survive normalization");
                AssertEqual("custom chat", settings.ChatSystemPrompt, "custom Chat instructions survive normalization");
                AssertEqual("custom plan", settings.PlanSystemPrompt, "custom Plan instructions survive normalization");

                var restored = JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings.Clone()));
                restored.NormalizeAgentPrompts();
                AssertEqual(version, restored.AgentPromptSchemaVersion, "clone and JSON roundtrip do not approve the schema");
                AssertEqual(settings.PlanSystemPrompt, restored.PlanSystemPrompt, "Plan text survives roundtrip");
                restored.SystemPrompt = string.Empty;
                restored.ChatSystemPrompt = null;
                restored.NormalizeAgentPrompts();
                AssertEqual(AgentPromptDefaults.GeneralInstructions, restored.SystemPrompt, "explicit blank uses the current default");
                AssertEqual(AgentPromptDefaults.ChatInstructions, restored.ChatSystemPrompt, "missing prompt uses the current default");
                AssertEqual(version, restored.AgentPromptSchemaVersion, "default substitution does not imply review");
            }
        }

        private static void SettingsPromptReviewPreservesStoredText()
        {
            foreach (var oldVersion in new[] { 0, 11, 12, 13, 14 })
            WithTempPaths(paths =>
            {
                var service = new SettingsService(paths);
                var legacy = new AppSettings
                {
                    AgentPromptSchemaVersion = oldVersion,
                    SystemPrompt = " custom general ", AgentToolsPrompt = "custom tools",
                    AgentSkillsPrompt = "custom skills", ChatSystemPrompt = "custom chat", PlanSystemPrompt = "custom plan",
                    ContextCompactionPrompt = "custom compaction", ChatTitlePrompt = "custom title",
                    AttachmentAnalysisPrompt = "custom media", Model = "before"
                };
                Func<AppSettings, string[]> prompts = value => new[] { value.SystemPrompt, value.AgentToolsPrompt,
                    value.AgentSkillsPrompt, value.ChatSystemPrompt, value.PlanSystemPrompt };
                new JsonFileStore().Save(paths.SettingsFile, legacy);
                var originalFile = File.ReadAllText(paths.SettingsFile);
                var loaded = service.Load();
                AssertTrue(prompts(legacy).SequenceEqual(prompts(loaded)), "loading preserves all five saved prompts");
                AssertEqual(originalFile, File.ReadAllText(paths.SettingsFile), "loading never rewrites the settings file");

                loaded.Model = "after";
                loaded.AgentPromptSchemaVersion = AppSettings.CurrentAgentPromptSchemaVersion;
                service.Save(loaded);
                loaded = service.Load();
                AssertEqual(oldVersion, loaded.AgentPromptSchemaVersion, "ordinary save cannot approve stored legacy prompts using a fresh marker");
                AssertEqual("after", loaded.Model, "unrelated settings can still be saved before review");
                AssertTrue(prompts(legacy).SequenceEqual(prompts(loaded)), "ordinary save preserves custom prompts");

                foreach (var requestReview in new[] { false, true })
                {
                    var rejected = loaded.Clone();
                    rejected.HistoryIntegrityMode = HistoryIntegrityModes.HmacSha256;
                    rejected.HistoryKeySource = HistoryKeySources.CustomSecret;
                    var rejectedFile = File.ReadAllText(paths.SettingsFile);
                    var failed = false;
                    try { service.Save(rejected, null, null, requestReview); }
                    catch (InvalidOperationException) { failed = true; }
                    AssertTrue(failed, "invalid protection settings reject the whole save, review=" + requestReview);
                    AssertEqual(oldVersion, rejected.AgentPromptSchemaVersion, "failed save does not mutate the caller's marker");
                    AssertTrue(prompts(legacy).SequenceEqual(prompts(rejected)), "failed save preserves the caller's custom prompts");
                    AssertEqual(rejectedFile, File.ReadAllText(paths.SettingsFile), "failed save does not alter durable settings");
                }

                service.Save(loaded, null, null, true);
                var reviewed = service.Load();
                reviewed.EnsureAgentPromptsReviewed();
                AssertEqual(oldVersion, loaded.AgentPromptSchemaVersion, "save stages review on a copy, not the caller's draft");
                AssertTrue(prompts(legacy).SequenceEqual(prompts(reviewed)), "explicit review preserves custom text");
                AssertEqual(AppSettings.CurrentAgentPromptSchemaVersion, reviewed.AgentPromptSchemaVersion,
                    "explicit review persists the current prompt schema");
                AssertTrue(File.ReadAllText(paths.SettingsFile).IndexOf("reviewAgentPrompts", StringComparison.OrdinalIgnoreCase) < 0,
                    "review command is transient, not a sticky settings flag");

                // Keep future-marker reset coverage once, and reset the actual legacy schemas too.
                legacy.AgentPromptSchemaVersion = oldVersion == 0 ? AppSettings.CurrentAgentPromptSchemaVersion + 1 : oldVersion;
                new JsonFileStore().Save(paths.SettingsFile, legacy);
                var reset = service.Load();
                reset.SystemPrompt = reset.AgentToolsPrompt = reset.AgentSkillsPrompt = reset.ChatSystemPrompt = reset.PlanSystemPrompt = string.Empty;
                service.Save(reset, null, null, true);
                var defaults = service.Load();
                defaults.EnsureAgentPromptsReviewed();
                AssertEqual(AppSettings.CurrentAgentPromptSchemaVersion, defaults.AgentPromptSchemaVersion,
                    "explicit reset persists the current prompt schema");
                foreach (var instruction in new[] { defaults.SystemPrompt, defaults.ChatSystemPrompt, defaults.PlanSystemPrompt })
                {
                    AssertContains(instruction, "conversation-response-v4", "explicit reset installs actual v4 defaults");
                    AssertContains(instruction, "`TOOL_RESULT` v1", "explicit reset installs Tool Result v1 in every mode");
                }
                AssertTrue(prompts(new AppSettings()).SequenceEqual(prompts(defaults)), "explicit cleared prompts and review select current defaults");
                AssertEqual("custom compaction", defaults.ContextCompactionPrompt, "conversation review leaves helper instructions alone");
                AssertEqual("custom title", defaults.ChatTitlePrompt, "title prompt is not implicitly reset");
                AssertEqual("custom media", defaults.AttachmentAnalysisPrompt, "media prompt is not implicitly reset");
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
            AssertContains(htmlAuthoring, "Do not create `echarts.js`",
                "HTML authoring rejects remote or duplicate chart runtimes");
            AssertContains(htmlAuthoring, "root `arguments` contains exactly `path` and `content`",
                "HTML writes put semantic properties directly at the schema root");
            foreach (var file in new[] { "`index.html`", "`styles.css`", "`dashboard.js`" })
                AssertContains(htmlAuthoring, file, "substantial HTML workspaces split responsibilities");
            AssertContains(htmlAuthoring, "Dependencies/echarts.min.js",
                "HTML authoring exposes the runtime-owned chart dependency");
            AssertContains(htmlAuthoring, "ResizeObserver",
                "HTML authoring resizes charts with their containers");
            AssertContains(htmlAuthoring, "CSS custom properties",
                "HTML authoring defines a coherent interface system");
            AssertContains(htmlAuthoring, "native buttons/selects/inputs",
                "HTML authoring requires accessible controls");
            AssertContains(htmlAuthoring, "inspect those sources first",
                "source-backed dashboards inspect workbook and VBA prerequisites first");
            AssertContains(htmlAuthoring, "Never replace a rich dashboard with a simplified placeholder",
                "HTML validation repair preserves the requested implementation");
            AssertContains(htmlAuthoring, "both the Office read and bind return `status=ok`",
                "HTML guidance cannot claim live data before binding evidence");
            AssertContains(htmlAuthoring, "finish the source read and binding before writing the data adapter or charts",
                "source-backed HTML establishes its live data contract before construction");
            AssertContains(htmlAuthoring, "columns:[{key,label,type}], rows:[{...}], rowCount",
                "HTML guidance defines the bound table envelope used by page code");
            AssertContains(htmlAuthoring, "A host refresh with changed values/status creates a new workspace head without adding an Undo step",
                "HTML guidance requires durable refresh read-back");
            AssertContains(htmlAuthoring, "Standalone export embeds the exact current JSON snapshot plus the local ECharts runtime",
                "HTML guidance distinguishes a self-contained export from live Office refresh");
            AssertContains(htmlAuthoring, "preflight has zero errors",
                "HTML definition of done includes static validation");
            AssertContains(htmlAuthoring, "Static preflight does not prove browser execution",
                "HTML guidance distinguishes static and runtime evidence");
            var skillAuthoring = skills.Single(skill => skill.Id == "common.skill_authoring").BodyMarkdown;
            AssertContains(skillAuthoring, "author the skill last",
                "a requested reusable skill follows the verified primary solution");
            AssertContains(skillAuthoring, "Do not invent a skill",
                "skill authoring is not an unsolicited substitute for execution");
            var taskTracking = skills.Single(skill => skill.Id == "common.task_tracking").BodyMarkdown;
            AssertContains(taskTracking, "three explicit deliverables or meaningful user-level stages",
                "task tracking uses the complex-request threshold");
            AssertContains(taskTracking, "Before the first Office/source read or mutation",
                "the execution checklist precedes domain work");
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
            AssertContains(defaults.SystemPrompt, "3. **Inspect.** Read enough of every requested Office/VBA/source",
                "Agent inspects requested sources before construction");
            AssertContains(defaults.SystemPrompt, "only after the primary solution is implemented and verified",
                "Agent follows source, deliverable, verification and reuse dependency order");
            AssertContains(defaults.SystemPrompt, "compare every explicit deliverable and every active task-list step",
                "Agent verifies requested outcomes before ending the loop");
            AssertContains(defaults.SystemPrompt, "cannot become success prose",
                "tool and protocol errors cannot be reported as completed work");
            AssertContains(defaults.SystemPrompt, "simplified placeholder",
                "Agent does not degrade an artifact to bypass validation");
            AssertContains(defaults.AgentToolsPrompt, "discovery -> construction -> verification",
                "complex Agent work creates a task list before execution");
            AssertContains(defaults.AgentToolsPrompt, "common.task_tracking` and `common.task_list_set",
                "task tracking explains separate skill and tool-schema loading");
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
            var settings = new AppSettings
            {
                Temperature = double.NaN,
                TopP = double.PositiveInfinity,
                UiFontScale = double.NegativeInfinity
            };
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(0.2, settings.Temperature, "non-finite temperature uses the default");
            AssertEqual(1.0, settings.TopP, "non-finite top-p uses the default");
            AssertEqual(1.0, settings.UiFontScale, "non-finite UI scale uses the default");

            settings.Temperature = 10;
            settings.UiFontScale = 10;
            settings.NormalizeSamplingAndUiValues();
            AssertEqual(2.0, settings.Temperature, "temperature is clamped to the supported endpoint range");
            AssertEqual(1.30, settings.UiFontScale, "UI scale is clamped to the rendered range");
        }
    }
}
