using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        // Keep this fixture on the production router/resource owners. The older
        // aggregate fixtures intentionally cannot qualify the native composition.
        private sealed class NativeToolFixture
        {
            internal AppDataPaths Paths;
            internal FakeOfficeAdapter Adapter;
            internal ConversationJournalRouter Router;
            internal ConversationJournal Journal;
            internal ResourceJournal Resources;
            internal OfficeToolExecutor Executor;
            internal ChatSession Session;
            internal AppSettings Settings;
        }

        private static void WithNativeToolFixture(Action<NativeToolFixture> test)
        { WithNativeToolFixture("Excel", ".xlsx", test); }

        private static void WithNativeToolFixture(string host, string extension, Action<NativeToolFixture> test)
        {
            WithOfficeJournalScope(host, extension, (paths, adapter, identities, router, sessions) => {
                var settings = new AppSettings { Model = "native-tool-integration", StreamResponses = false,
                    SmartChatTitles = false, AutoCompressContext = false, AutoConfirmToolActions = true,
                    AgentResponseMode = AgentResponseModes.JsonSchema, ContextWindowOverrideTokens = 65536,
                    MaxAgentFormatRetries = 0, MaxAgentIterations = 12 };
                var resources = new ResourceJournal(paths);
                IConversationStore store = router;
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths),
                    new ToolStore(paths), () => settings, value => settings = value, paths,
                    loadArtifactBody: store.LoadArtifactBody, resourceAuthorityStore: resources,
                    persistResourceFacts: store.Commit, resourceMutationJournal: resources,
                    resourceOrigin: router.MutationOrigin);
                var session = sessions.CreatePersistentChat("Native tool integration");
                var fixture = new NativeToolFixture { Paths = paths, Adapter = adapter, Router = router,
                    Journal = new ConversationJournal(paths, "document:" + session.DocumentAuthorityId),
                    Resources = resources, Executor = executor, Session = session, Settings = settings };
                try { test(fixture); }
                catch
                {
                    foreach (var message in session.Messages.Where(item => item.ToolName != null && item.Activity == null &&
                        item.AcceptedCallOrigin == null && item.ExecutionProgress != null && !item.ExecutionProgress.IndependentLocalRead))
                    {
                        var text = message.Content ?? string.Empty;
                        Console.WriteLine("native terminal " + message.ToolName + ": " +
                            Newtonsoft.Json.JsonConvert.SerializeObject(message.ExecutionProgress) + " " +
                            text.Substring(0, Math.Min(1400, text.Length)));
                    }
                    throw;
                }
            });
        }

        private static string NativeCall(string name, JObject arguments)
        {
            return ModelProtocolWire.WriteAcceptedCall("Perform the explicitly requested operation.", null,
                new ConversationToolCall { Name = name, Arguments = arguments });
        }

        private static ChatTurnResult RunNativeToolScript(NativeToolFixture fixture,
            IEnumerable<string> responses, Func<string> finish)
        {
            var script = new Queue<string>(responses);
            LlmCompletionDelegate completion = (settings, messages, options, stream, token) =>
                Task.FromResult(new LlmCompletionResult { Content = script.Count == 0 ? finish() : script.Dequeue() ?? finish() });
            var service = new ConversationRunService(fixture.Adapter, fixture.Executor, fixture.Router, fixture.Router,
                (settings, messages, options, stream, token) => FixtureCompletion(completion, settings, messages, options, stream, token));
            var catalog = OfficeToolCatalog.ForHost(fixture.Adapter.HostName).Concat(fixture.Executor.GetControllerTools()).ToArray();
            return service.ExecuteWithApprovalsAsync(ChatModes.Agent, "Perform the requested test operations.",
                fixture.Session, fixture.Session.Context, fixture.Settings, catalog, null).GetAwaiter().GetResult();
        }

        private static ChatMessage NativeTerminal(NativeToolFixture fixture, string tool)
        {
            return fixture.Session.Messages.Last(message => message.ToolName == tool && message.Activity == null &&
                message.AcceptedCallOrigin == null && message.ExecutionProgress != null);
        }

        private static string NativeUnverifiedFinish(NativeToolFixture fixture, string tool, bool normalReturn)
        {
            var evidence = fixture.Session.Messages.Where(message => message.ToolName == tool && message.Activity == null &&
                message.EvidenceReference != null).Select(message => message.EvidenceReference.Alias).ToArray();
            return ModelProtocolWire.WriteAcceptedDecision("finish", "Report only the retained execution evidence.",
                normalReturn ? "Requested invocations returned normally; their external effects remain unverified." :
                    "The requested effect could not be verified; inspect it before retrying.",
                outcome: normalReturn ? ConversationOutcome.Partial : ConversationOutcome.UnableToComplete,
                results: new[] { new ConversationResultClaim("Requested operation and its effect.", evidence,
                    normalReturn ? ConversationAssessment.PartiallySatisfied : ConversationAssessment.Unverified) });
        }

        private static void AssertNativeToolReceipt(NativeToolFixture fixture, string tool)
        {
            var result = NativeTerminal(fixture, tool);
            AssertTrue(!string.IsNullOrWhiteSpace(result.AuthorityCommitId), "native result links a durable authority outcome");
            var proof = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, result.RunId, result.ToolCallId);
            AssertTrue(proof != null, "exact chat/run/call resource proof is readable");
            AssertEqual(result.AuthorityCommitId, proof.CommitId, "conversation and resource receipts agree");
            var revision = fixture.Session.Revision;
            fixture.Journal.Rebuild();
            var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
            AssertEqual(revision, restored.Revision, "native rebuild keeps the final conversation revision");
            AssertEqual(RunLifecycle.Completed, restored.LastRun.KernelState.Summary.Lifecycle,
                "the saved run is terminal after native rebuild");
            AssertEqual(proof.Position.Hash,
                fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, result.RunId, result.ToolCallId).Position.Hash,
                "native rebuild keeps the exact immutable effect receipt");
        }

        private static void NativeToolsMacroNormalReturn()
        {
            WithNativeToolFixture(fixture => {
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse(VbaToolCatalog.RunMacro),
                    NativeCall(VbaToolCatalog.RunMacro, new JObject { ["macroName"] = "Module1.MigrateApiKey", ["arguments"] = new JArray("value", 2, true) }),
                    NativeCall(VbaToolCatalog.RunMacro, new JObject { ["macroName"] = "Module1.MigrateApiKey", ["arguments"] = new JArray("value", 2, true) }),
                    LoadToolSchemaResponse("excel.inspect"),
                    NativeCall("excel.inspect", new JObject { ["kind"] = "sheets" })
                }, () => NativeUnverifiedFinish(fixture, VbaToolCatalog.RunMacro, true));
                AssertEqual(2, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro),
                    "two admitted normal-return macro invocations dispatch exactly once each");
                AssertEqual(RunViewLifecycles.Completed, result.RunViewState.Lifecycle, "opaque invocations can finish honestly");
                AssertEqual(2, result.RunViewState.UnknownEffects, "normal return never certifies an external effect");
                AssertEqual("partial", result.RunViewState.ModelOutcome, "unverified macro effects remain explicit");
                AssertNativeToolReceipt(fixture, VbaToolCatalog.RunMacro);
                AssertEqual(2, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RunMacro), "loading and rebuilding cannot replay a macro");
            });
        }

        private static void NativeToolsExcelVerified()
        {
            foreach (var autoConfirm in new[] { true, false }) WithNativeToolFixture(fixture => {
                fixture.Settings.AutoConfirmToolActions = autoConfirm;
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse("excel.sheet.add"),
                    NativeCall("excel.sheet.add", new JObject { ["name"] = "NativeReport" }),
                    LoadToolSchemaResponse("excel.range.write"),
                    NativeCall("excel.range.write", new JObject { ["kind"] = "value", ["sheet"] = "NativeReport", ["address"] = "A1", ["value"] = 42 })
                }, () => ScriptedVerifiedFinish(fixture.Session, "Both writes have exact results.", "Created and written.",
                    "Create the report sheet and write A1.", "excel.sheet.add", "excel.range.write"));
                AssertEqual(1, fixture.Adapter.ExcelSheetRequests.Count(item => item.ToolId == "excel.sheet.add"), "one sheet dispatch");
                AssertEqual(1, fixture.Adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelWriteApplyOperation), "one range dispatch");
                AssertEqual("42", fixture.Adapter.CellValue("NativeReport", "A1"), "typed Excel read-back agrees with live value");
                AssertEqual(2, result.RunViewState.VerifiedWrites, "both verified writes retain their classification");
                AssertNativeToolReceipt(fixture, "excel.range.write");
            });
        }

        private static void NativeToolsExcelUnknownNoReplay()
        {
            WithNativeToolFixture(fixture => {
                fixture.Adapter.BeforeExcelBackendCall = operation => {
                    if (operation == FakeOfficeAdapter.ExcelWriteApplyOperation)
                        fixture.Adapter.ThrowOnExcelBackendOperation = FakeOfficeAdapter.ExcelWriteReadOperation;
                };
                var arguments = new JObject { ["kind"] = "value", ["sheet"] = "Data", ["address"] = "A1", ["value"] = 73 };
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse("excel.range.write"), NativeCall("excel.range.write", arguments),
                    NativeCall("excel.range.write", (JObject)arguments.DeepClone()),
                    NativeCall("excel.range.write", new JObject { ["kind"] = "value", ["sheet"] = "Data", ["address"] = "A2", ["value"] = 74 })
                }, () => NativeUnverifiedFinish(fixture, "excel.range.write", false));
                AssertEqual("73", fixture.Adapter.CellValue("Data", "A1"), "first dispatch may have changed the document");
                AssertEqual(1, fixture.Adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelWriteApplyOperation),
                    "neither identical retry nor different arguments bypass a genuine Unknown root");
                AssertEqual(0, result.RunViewState.VerifiedWrites, "unavailable read-back cannot become verified success");
                AssertEqual(1, result.RunViewState.UnknownEffects, "possible effect remains unknown");
                fixture.Adapter.ThrowOnExcelBackendOperation = null;
                fixture.Journal.Rebuild();
                AssertEqual(1, ((IConversationStore)fixture.Router).Load(fixture.Session.Id).LastRun.KernelState.Summary.ToolCounts.WriteUnknown,
                    "native replay preserves the unknown obligation");
                AssertEqual(1, fixture.Adapter.ExcelBackendCalls.Count(item => item == FakeOfficeAdapter.ExcelWriteApplyOperation), "rebuild performs no dispatch");
            });
        }

        private static void NativeToolsVbaReplace()
        {
            WithNativeToolFixture(fixture => {
                fixture.Adapter.SetVbaModule("NativeModule", "Sub Main()\nDebug.Print \"before\"\nEnd Sub", "StdModule");
                var target = "VBA module: NativeModule";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.search", new JObject { ["target"] = target, ["query"] = "before", ["mode"] = "literal", ["matchCase"] = true }),
                    NativeCall("core.replace", new JObject { ["target"] = target, ["query"] = "before", ["replacement"] = "after",
                        ["mode"] = "literal", ["matchCase"] = true, ["expectedMatches"] = 1 })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The exact search snapshot was replaced.", "Source replaced.",
                    "Replace the observed source text.", "core.replace"));
                AssertContains(fixture.Adapter.GetVbaModuleCode("NativeModule"), "after", "routed VBA owner writes the replacement");
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "replace dispatches once");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "verified VBA replacement survives native storage");
                AssertNativeToolReceipt(fixture, "core.replace");
            });
        }

        private static void NativeToolsSinglePatch()
        {
            WithNativeToolFixture(fixture => {
                fixture.Adapter.SetVbaModule("NativePatch", "Sub Main()\nDebug.Print \"before\"\nEnd Sub", "StdModule");
                var target = "VBA module: NativePatch";
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = target }),
                    NativeCall("core.patch", new JObject { ["targets"] = new JArray(new JObject {
                        ["target"] = target, ["edits"] = new JArray(new JObject { ["old"] = "before", ["new"] = "after" }) }) })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The prepared source has verified after-state.", "Source patched.",
                    "Patch the observed source.", "core.patch"));
                AssertContains(fixture.Adapter.GetVbaModuleCode("NativePatch"), "after", "single-target patch reaches its typed owner");
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "one patch dispatch");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "the patch aggregate retains verified outcome");
                AssertEqual(1, NativeTerminal(fixture, "core.patch").ResourceEffect.Targets.Count,
                    "the terminal retains its addressed per-target presentation");
                AssertNativeToolReceipt(fixture, "core.patch");
            });
        }

        private static void NativeToolsMultiPatch()
        {
            WithNativeToolFixture(fixture => {
                fixture.Adapter.SetVbaModule("NativePatchA", "Sub Main()\nDebug.Print \"before-a\"\nEnd Sub", "StdModule");
                fixture.Adapter.SetVbaModule("NativePatchB", "Sub Main()\nDebug.Print \"before-b\"\nEnd Sub", "StdModule");
                var targetA = "VBA module: NativePatchA"; var targetB = "VBA module: NativePatchB";
                JObject Patch(string first, string second) { return new JObject { ["targets"] = new JArray(
                    new JObject { ["target"] = targetA, ["edits"] = new JArray(new JObject { ["old"] = first, ["new"] = first.Replace("before", "after") }) },
                    new JObject { ["target"] = targetB, ["edits"] = new JArray(new JObject { ["old"] = second, ["new"] = second.Replace("before", "after") }) }) }; }
                var result = RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = targetA }),
                    NativeCall("core.patch", Patch("before-a", "before-b")),
                    NativeCall("core.read", new JObject { ["target"] = targetB }),
                    NativeCall("core.patch", Patch("before-a", "before-b"))
                }, () => ScriptedVerifiedFinish(fixture.Session, "Both prepared sources have verified after-state.", "Both sources patched.",
                    "Patch both observed sources.", "core.patch"));
                var terminals = fixture.Session.Messages.Where(item => item.ToolName == "core.patch" && item.Activity == null &&
                    item.AcceptedCallOrigin == null && item.ExecutionProgress != null).ToArray();
                AssertEqual(ToolExecutionOutcome.Error, terminals[0].ExecutionProgress.Outcome, "unobserved second target rejects the package before any write");
                AssertEqual(null, terminals[0].ResourceEffect, "rejected multi-target package retains no mutation effect");
                AssertContains(fixture.Adapter.GetVbaModuleCode("NativePatchA"), "after-a", "first target receives its owner write");
                AssertContains(fixture.Adapter.GetVbaModuleCode("NativePatchB"), "after-b", "second target receives its owner write");
                AssertEqual(2, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "exactly two target dispatches");
                var terminal = NativeTerminal(fixture, "core.patch");
                AssertEqual(2, terminal.ResourceEffect.Targets.Count, "multi-target terminal retains both addressed results");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "the shared run projection counts one accepted patch operation");
                var outcomes = fixture.Journal.LinkedResourceOutcomes(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertEqual(2, outcomes.Count, "both exact owner receipts are linked to one accepted call");
                AssertEqual(2, outcomes.Select(item => item.Fact.Call.Origin.TargetOrdinal).Distinct().Count(), "each owner receipt retains its target ordinal");
                var revision = fixture.Session.Revision;
                fixture.Journal.Rebuild();
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(revision, restored.Revision, "multi-owner receipts rebuild without replay");
                AssertEqual(2, fixture.Journal.LinkedResourceOutcomes(fixture.Session.Id, terminal.RunId, terminal.ToolCallId).Count,
                    "rebuild retains every linked owner receipt");
            });
        }

        private static void NativeToolsMultiPatchStopsAfterOwnerRejection()
        {
            WithNativeToolFixture(fixture => {
                fixture.Adapter.SetVbaModule("PartialA", "Sub Main()\nDebug.Print \"before-a\"\nEnd Sub", "StdModule");
                fixture.Adapter.SetVbaModule("PartialB", "Sub Main()\nDebug.Print \"before-b\"\nEnd Sub", "StdModule");
                var targetA = "VBA module: PartialA"; var targetB = "VBA module: PartialB";
                fixture.Adapter.BeforeVbaBackendCall = call => {
                    if (call.Operation == FakeVbaOperation.ReplaceModule && fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule) == 2)
                        throw new InvalidOperationException("second target dispatch lost its acknowledgement");
                };
                var patch = new JObject { ["targets"] = new JArray(
                    new JObject { ["target"] = targetA, ["edits"] = new JArray(new JObject { ["old"] = "before-a", ["new"] = "after-a" }) },
                    new JObject { ["target"] = targetB, ["edits"] = new JArray(new JObject { ["old"] = "before-b", ["new"] = "after-b" }) }) };
                RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = targetA }),
                    NativeCall("core.read", new JObject { ["target"] = targetB }),
                    NativeCall("core.patch", patch)
                }, () => NativeUnverifiedFinish(fixture, "core.patch", false));
                AssertContains(fixture.Adapter.GetVbaModuleCode("PartialA"), "after-a", "first target remains verified");
                AssertContains(fixture.Adapter.GetVbaModuleCode("PartialB"), "before-b", "second target was not silently retried");
                var terminal = NativeTerminal(fixture, "core.patch");
                AssertEqual(ToolExecutionOutcome.Error, terminal.ExecutionProgress.Outcome, "partial owner failure remains an error");
                AssertEqual(ResourceTargetStatus.Changed, terminal.ResourceEffect.Targets[0].Status, "first target retains Changed");
                AssertEqual(ResourceTargetStatus.Rejected, terminal.ResourceEffect.Targets[1].Status, "second target retains Rejected after verified no-change");
                var outcomes = fixture.Journal.LinkedResourceOutcomes(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertTrue(outcomes.Count >= 1 && outcomes.All(item => item.Fact.Call.Origin.TargetOrdinal.HasValue),
                    "only owner receipts tied to target ordinals are linked");
                var revision = fixture.Session.Revision;
                fixture.Journal.Rebuild();
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(revision, restored.Revision, "partial multi-target terminal rebuilds without replay");
                AssertEqual(2, fixture.Adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "rebuild does not dispatch either target again");
            });
        }

        private static void NativeToolsVbaCopyMoveReceipts()
        {
            WithNativeToolFixture(fixture => {
                const string source = "Sub Main()\nDebug.Print \"copy-move\"\nEnd Sub";
                fixture.Adapter.SetVbaModule("CopySource", source, "StdModule");
                var results = new List<ChatTurnResult>();
                results.Add(RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = "VBA module: CopySource" }),
                    LoadToolSchemaResponse(ResourceMutationToolCatalog.Copy),
                    NativeCall(ResourceMutationToolCatalog.Copy, new JObject { ["target"] = "VBA module: CopySource",
                        ["destination"] = "VBA module: CopiedModule" })
                }, () => NativeUnverifiedFinish(fixture, "core.read", false)));
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("CopySource"), "copy preserves the source module");
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("CopiedModule"), "copy publishes the exact destination source");
                var copyTerminal = NativeTerminal(fixture, ResourceMutationToolCatalog.Copy);
                var copyOutcome = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, copyTerminal.RunId, copyTerminal.ToolCallId);
                AssertTrue(copyOutcome != null && copyOutcome.Effect != null && copyOutcome.Effect.Impacts.Count >= 1,
                    "copy retains an exact owner outcome with destination publication evidence");

                results.Add(RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = "VBA module: CopiedModule" }),
                    LoadToolSchemaResponse(ResourceMutationToolCatalog.Move),
                    NativeCall(ResourceMutationToolCatalog.Move, new JObject { ["target"] = "VBA module: CopiedModule",
                        ["destination"] = "VBA module: MovedModule" })
                }, () => NativeUnverifiedFinish(fixture, "core.read", false)));
                AssertTrue(string.IsNullOrEmpty(fixture.Adapter.GetVbaModuleCode("CopiedModule")), "move removes the old destination");
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("MovedModule"), "move publishes the same source at its destination");
                var moveTerminal = NativeTerminal(fixture, ResourceMutationToolCatalog.Move);
                var moveOutcome = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, moveTerminal.RunId, moveTerminal.ToolCallId);
                AssertTrue(moveOutcome != null && moveOutcome.Effect != null, "move retains an exact owner outcome");
                var revision = fixture.Session.Revision;
                var copyVbaCalls = fixture.Adapter.CountVbaCalls(FakeVbaOperation.CreateModule);
                var renameVbaCalls = fixture.Adapter.CountVbaCalls(FakeVbaOperation.RenameModule);
                fixture.Journal.Rebuild();
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(revision, restored.Revision, "copy/move receipts rebuild without replay");
                AssertEqual(copyVbaCalls, fixture.Adapter.CountVbaCalls(FakeVbaOperation.CreateModule), "rebuild does not repeat copy");
                AssertEqual(renameVbaCalls, fixture.Adapter.CountVbaCalls(FakeVbaOperation.RenameModule), "rebuild does not repeat move");
                AssertEqual(copyOutcome.CommitId, fixture.Journal.LinkedResourceOutcome(fixture.Session.Id,
                    copyTerminal.RunId, copyTerminal.ToolCallId).CommitId, "copy receipt survives rebuild");
                AssertEqual(moveOutcome.CommitId, fixture.Journal.LinkedResourceOutcome(fixture.Session.Id,
                    moveTerminal.RunId, moveTerminal.ToolCallId).CommitId, "move receipt survives rebuild");
            });
        }

        private static void NativeToolsWordReplaceReceipt()
        {
            WithNativeToolFixture("Word", ".docx", fixture => {
                fixture.Adapter.Write(new RNAssistant.Office.Domains.Word.WordWriteRequest { Mode = "replaceselection",
                    Text = "alpha beta alpha" }, () => { });
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse(WordToolIds.ReplaceText),
                    NativeCall(WordToolIds.ReplaceText, new JObject { ["find"] = "alpha", ["replace"] = "omega",
                        ["scope"] = "main", ["mode"] = "literal", ["replaceAll"] = true, ["maxReplacements"] = 10 })
                }, () => NativeUnverifiedFinish(fixture, WordToolIds.ReplaceText, true));
                AssertEqual("omega beta omega", fixture.Adapter.WordText, "Word owner replaces the bounded live scope");
                AssertEqual(1, fixture.Adapter.WordBackendCalls.Count(item => item == FakeOfficeAdapter.WordReplaceOperation),
                    "Word replace dispatches exactly once");
                var terminal = NativeTerminal(fixture, WordToolIds.ReplaceText);
                AssertEqual(ToolExecutionOutcome.Ok, terminal.ExecutionProgress.Outcome, "Word owner reports verified execution");
                AssertTrue(!string.IsNullOrWhiteSpace(terminal.AuthorityCommitId), "Word terminal retains its exact authority commit");
                var proof = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertTrue(proof != null, "Word terminal links its existing resource receipt");
                AssertEqual(terminal.AuthorityCommitId, proof.CommitId, "Word conversation and resource receipts agree");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "Word replacement survives the shared run projection");
                var revision = fixture.Session.Revision;
                fixture.Journal.Rebuild();
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(revision, restored.Revision, "Word receipt rebuilds without replay");
                AssertEqual(1, fixture.Adapter.WordBackendCalls.Count(item => item == FakeOfficeAdapter.WordReplaceOperation),
                    "rebuild does not repeat the Word replacement");
                AssertEqual(terminal.AuthorityCommitId, fixture.Journal.LinkedResourceOutcome(fixture.Session.Id,
                    terminal.RunId, terminal.ToolCallId).CommitId, "Word receipt survives rebuild");
            });
        }

        private static void NativeToolsPowerPointReplaceReceipt()
        {
            WithNativeToolFixture("PowerPoint", ".pptx", fixture => {
                var result = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse(PowerPointToolIds.ReplaceText),
                    NativeCall(PowerPointToolIds.ReplaceText, new JObject { ["find"] = "Revenue", ["replace"] = "Sales",
                        ["scope"] = "deck", ["mode"] = "literal", ["replaceAll"] = true, ["maxReplacements"] = 10 })
                }, () => NativeUnverifiedFinish(fixture, PowerPointToolIds.ReplaceText, true));
                var text = fixture.Adapter.ReadTextTargets(new RNAssistant.Office.Domains.PowerPoint.PowerPointTextScopeRequest {
                    Scope = "deck", IncludeNotes = true, MaxSlides = 10, MaxShapesPerSlide = 100,
                    MaxTargets = 100, MaxCharacters = 100000 });
                AssertTrue(text.Any(item => item.Text.Contains("Sales")), "PowerPoint owner publishes replacement text");
                AssertTrue(text.All(item => !item.Text.Contains("Revenue")), "PowerPoint owner removes the bounded original matches");
                AssertEqual(1, fixture.Adapter.PowerPointBackendCalls.Count(item => item == FakeOfficeAdapter.PowerPointReplaceOperation),
                    "PowerPoint replace dispatches exactly once");
                var terminal = NativeTerminal(fixture, PowerPointToolIds.ReplaceText);
                AssertEqual(ToolExecutionOutcome.Ok, terminal.ExecutionProgress.Outcome, "PowerPoint owner reports verified execution");
                AssertTrue(!string.IsNullOrWhiteSpace(terminal.AuthorityCommitId), "PowerPoint terminal retains its exact authority commit");
                var proof = fixture.Journal.LinkedResourceOutcome(fixture.Session.Id, terminal.RunId, terminal.ToolCallId);
                AssertTrue(proof != null, "PowerPoint terminal links its existing resource receipt");
                AssertEqual(terminal.AuthorityCommitId, proof.CommitId, "PowerPoint conversation and resource receipts agree");
                AssertEqual(1, result.RunViewState.VerifiedWrites, "PowerPoint replacement survives the shared run projection");
                var revision = fixture.Session.Revision;
                fixture.Journal.Rebuild();
                var restored = ((IConversationStore)fixture.Router).Load(fixture.Session.Id);
                AssertEqual(revision, restored.Revision, "PowerPoint receipt rebuilds without replay");
                AssertEqual(1, fixture.Adapter.PowerPointBackendCalls.Count(item => item == FakeOfficeAdapter.PowerPointReplaceOperation),
                    "rebuild does not repeat the PowerPoint replacement");
                AssertEqual(terminal.AuthorityCommitId, fixture.Journal.LinkedResourceOutcome(fixture.Session.Id,
                    terminal.RunId, terminal.ToolCallId).CommitId, "PowerPoint receipt survives rebuild");
            });
        }

        private static void NativeToolsVbaDeleteRestore()
        {
            WithNativeToolFixture(fixture => {
                const string source = "Sub Main()\nDebug.Print \"restore me\"\nEnd Sub";
                fixture.Adapter.SetVbaModule("NativeRestore", source, "StdModule");
                var target = "VBA module: NativeRestore";
                RunNativeToolScript(fixture, new[] {
                    NativeCall("core.read", new JObject { ["target"] = target }),
                    LoadToolSchemaResponse(ResourceMutationToolCatalog.Delete),
                    NativeCall(ResourceMutationToolCatalog.Delete, new JObject { ["target"] = target })
                }, () => ScriptedVerifiedFinish(fixture.Session, "Deletion has exact verified absence.", "Source removed.",
                    "Delete the observed source.", ResourceMutationToolCatalog.Delete));
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.DeleteModule), "delete dispatches once");
                AssertEqual(string.Empty, fixture.Adapter.GetVbaModuleCode("NativeRestore"), "verified deletion removes the component");
                AssertNativeToolReceipt(fixture, ResourceMutationToolCatalog.Delete);

                var backup = new VbaJournalStore(fixture.Paths).List(fixture.Session.Host, fixture.Session.DocumentKey)
                    .Where(item => item.ModuleName == "NativeRestore").OrderByDescending(item => item.CreatedUtc).First();
                var backupTarget = VbaResourceProvider.BackupSemanticTarget(backup);
                var restored = RunNativeToolScript(fixture, new[] {
                    LoadToolSchemaResponse(ResourceMutationToolCatalog.Restore),
                    NativeCall(ResourceMutationToolCatalog.Restore, new JObject { ["target"] = backupTarget })
                }, () => ScriptedVerifiedFinish(fixture.Session, "The exact retained source was restored.", "Source restored.",
                    "Restore the selected backup.", ResourceMutationToolCatalog.Restore));
                AssertEqual(source, fixture.Adapter.GetVbaModuleCode("NativeRestore"), "explicit restore reuses the pinned backup bytes");
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.CreateModule), "restore creates the component once");
                AssertEqual(1, restored.RunViewState.VerifiedWrites, "restore retains its verified native outcome");
                AssertNativeToolReceipt(fixture, ResourceMutationToolCatalog.Restore);
                AssertEqual(1, fixture.Adapter.CountVbaCalls(FakeVbaOperation.CreateModule), "rebuild cannot repeat restore");
            });
        }
    }
}
