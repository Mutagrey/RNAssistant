using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void DocumentPlanSharedPublication()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), (executor, adapter) =>
            {
                var a = NewSession(adapter);
                a.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var create = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Shared plan", "markdown", "# Shared\nFirst exact body.", "status", "draft"), tools, new AppSettings(), false, false, a);
                AssertTrue(create.Success, "document Plan is published by the production runtime");
                var first = a.Artifacts.Single(item => item.Id == a.ActivePlanDocumentArtifactId);
                var planId = PlanDocumentService.PlanId(first);
                var exact = ChatResourceUri.CreateArtifactRevision(a, first);
                var chats = new ChatStore(FixturePaths.Value);
                chats.Save(a);
                var reopened = new ChatStore(FixturePaths.Value).Load(a.Id);
                AssertEqual(first.Id, reopened.ActivePlanDocumentArtifactId, "selection survives even before an origin message is linked");
                AssertEqual(first.InlineText, reopened.Artifacts.Single(item => item.Id == first.Id).InlineText, "snapshot reload uses the document owner");

                var fork = NewSession(adapter);
                fork.DocumentAuthorityId = a.DocumentAuthorityId;
                fork.ParentSessionId = a.Id;
                ChatCloneService.PrepareForkResources(a, fork, chats.LoadArtifactBody,
                    new ResourceForkService(executor.ResourceAuthority, executor.Payloads));
                AssertEqual(first.Id, fork.ActivePlanDocumentArtifactId, "fork keeps the selected document Plan independently of message history");
                AssertEqual(exact.Uri, ChatResourceUri.CreateArtifactRevision(fork, fork.Artifacts.Single(item => item.Id == first.Id)).Uri,
                    "fork keeps the same Plan snapshot rather than a copied identity");

                var b = ChatCloneService.CloneSessionSnapshot(a);
                b.Id = Guid.NewGuid().ToString("N");
                b.Messages.Clear();
                var empty = NewSession(adapter);
                empty.DocumentAuthorityId = a.DocumentAuthorityId;
                var discovery = executor.ResourceGateway.List(empty, "chat", null, null, 50);
                AssertTrue(discovery.Items.Any(item => item.Reference.Uri == exact.Uri), "empty second chat discovers the Plan");
                AssertEqual(first.InlineText, executor.ResourceGateway.Read(empty, new ResourceReadRequest {
                    Reference = exact, Representation = "text", MaxChars = 32000 }).Result.Text, "another chat reads the exact Plan");
                var update = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Shared plan", "markdown", "# Shared\nChanged in B.", "status", "ready"), tools, new AppSettings(), false, false, b);
                AssertTrue(update.Success, "second chat updates the selected shared Plan");
                var stale = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Shared plan", "markdown", "# stale", "status", "draft"), tools, new AppSettings(), false, false, a);
                AssertEqual("stale_plan_revision", stale.ErrorCode, "first chat cannot overwrite the newer document head");
                AssertEqual(2, chats.DocumentArtifacts.PlanHistory(a, planId).Count, "stale call adds no revision");
                AssertEqual(first.InlineText, executor.ResourceGateway.Read(empty, new ResourceReadRequest {
                    Reference = exact, Representation = "text", MaxChars = 32000 }).Result.Text, "old references retain their exact body");

                var c = ChatCloneService.CloneSessionSnapshot(b);
                c.Id = Guid.NewGuid().ToString("N");
                var writers = new[] { b, c }.Select((session, index) => Task.Run(() => executor.ExecuteManual(
                    Command(PlanDocumentToolCatalog.SaveToolId, "title", "Shared plan", "markdown", "# writer " + index, "status", "draft"),
                    tools, new AppSettings(), false, false, session))).ToArray();
                Task.WaitAll(writers);
                AssertEqual(1, writers.Count(task => task.Result.Success), "concurrent writers sharing one base publish one child");
                AssertEqual(1, writers.Count(task => task.Result.ErrorCode == "stale_plan_revision"), "the competing writer is rejected before dispatch: " +
                    string.Join(" | ", writers.Select(task => task.Result.ErrorCode + ": " + task.Result.Message)));
                AssertEqual(3, chats.DocumentArtifacts.PlanHistory(a, planId).Count, "document lineage remains linear");

                var independent = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Independent plan", "markdown", "# Separate", "status", "draft"), tools, new AppSettings(), false, false, empty);
                AssertTrue(independent.Success, "another chat can create an independent Plan in the same document");
                AssertEqual(3, chats.DocumentArtifacts.PlanHistory(a, planId).Count, "independent Plan does not replace the first lineage");
                var winner = new[] { b, c }.Single(session => session.Artifacts.Single(item => item.Id == session.ActivePlanDocumentArtifactId).Revision == 3);
                var independentWriters = new[] { winner, empty }.Select((session, index) => Task.Run(() => executor.ExecuteManual(
                    Command(PlanDocumentToolCatalog.SaveToolId, "title", "Independent edit " + index, "markdown", "# Independent " + index, "status", "draft"),
                    tools, new AppSettings(), false, false, session))).ToArray();
                Task.WaitAll(independentWriters);
                AssertTrue(independentWriters.All(task => task.Result.Success), "concurrent edits to different Plans do not conflict on document generation");
                chats.Delete(a.Host, a.DocumentKey, a.Id);
                var gc = CasService(FixturePaths.Value, new ChatStore(FixturePaths.Value), new VbaJournalStore(FixturePaths.Value),
                    () => StorageProtector.None).Collect();
                AssertTrue(gc.Completed && gc.Health.MissingBlobCount == 0, "document Plan metadata and bodies survive GC");
                var fresh = new ChatStore(FixturePaths.Value);
                AssertEqual(first.InlineText, fresh.DocumentArtifacts.Read(empty, exact).InlineText, "origin deletion and a fresh store preserve historical Plan bytes");
                var foreign = NewSession(adapter);
                foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                RuntimeThrows<System.IO.InvalidDataException>(() => fresh.DocumentArtifacts.Read(foreign, exact));
                System.IO.File.Delete(executor.Payloads.PathFor(first.ContentSha256));
                AssertEqual("RESOURCE_SNAPSHOT_UNAVAILABLE", RuntimeThrows<ResourceRequestException>(() =>
                    executor.ResourceGateway.Read(empty, new ResourceReadRequest { Reference = exact, Representation = "text", MaxChars = 32000 })).ErrorCode,
                    "missing Plan body is explicit and never reconstructed from chat prose");
            });
        }

        private static void DocumentPlanFailedChatLink()
        {
            WithTempPaths(paths =>
            {
                var adapter = FakeOfficeAdapter.ForHost("Word");
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), paths: paths,
                    persistResourceFacts: saved => { throw new System.IO.IOException("Injected Plan chat-link failure."); });
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var definition = executor.GetControllerTools().Single(item => item.Id == PlanDocumentToolCatalog.SaveToolId);
                var runtime = executor.CreateNativeRuntime(session, new[] { definition }, new AppSettings(), ChatModes.Plan, false);
                var call = new ToolCall("plan-link-failure", PlanDocumentToolCatalog.SaveToolId,
                    "{\"title\":\"Retained plan\",\"markdown\":\"# Retained\",\"status\":\"draft\"}");
                RuntimeThrows<System.IO.IOException>(() => ExecuteNative(runtime, call, runtime.Describe(call)));
                var fresh = new ChatStore(paths);
                var artifact = fresh.DocumentArtifacts.List(session).Single(item => item.Kind == ChatArtifactKinds.PlanDocument);
                AssertEqual("# Retained", fresh.DocumentArtifacts.Read(session, ChatResourceUri.CreateArtifactRevision(session, artifact)).InlineText,
                    "resource publication survives a subsequent failed chat save");
                var linkedRetry = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertContains(linkedRetry.Result.DataJson, "plan_attempt_already_published", "retained selection cannot turn a replayed create into an update");
                session.ActivePlanDocumentArtifactId = null;
                session.Artifacts.Clear();
                var retry = ExecuteNative(runtime, call, runtime.Describe(call));
                AssertEqual(ToolDispatchEvidence.NotDispatched, retry.Evidence.Dispatch, "same creation attempt is not replayed");
                AssertContains(retry.Result.DataJson, "plan_attempt_already_published", "recovery identifies the retained publication");
                AssertEqual(1, fresh.DocumentArtifacts.List(session).Count, "failed chat-link recovery creates no duplicate Plan");
                session.Artifacts.Add(fresh.DocumentArtifacts.Read(session, ChatResourceUri.CreateArtifactRevision(session, artifact)));
                session.ActivePlanDocumentArtifactId = artifact.Id;
                var update = new ToolCall("plan-update-link-failure", PlanDocumentToolCatalog.SaveToolId,
                    "{\"title\":\"Retained plan\",\"markdown\":\"# Updated\",\"status\":\"ready\"}");
                RuntimeThrows<System.IO.IOException>(() => ExecuteNative(runtime, update, runtime.Describe(update)));
                session.Artifacts.Clear();
                session.ActivePlanDocumentArtifactId = null;
                var updateRetry = ExecuteNative(runtime, update, runtime.Describe(update));
                AssertContains(updateRetry.Result.DataJson, "plan_attempt_already_published", "a failed update cannot replay as creation after selection is lost");
                AssertEqual(2, fresh.DocumentArtifacts.List(session).Count, "only the two committed snapshots remain");
            });
        }

        private static void PlanModeFiltersMutationsAndKeepsPlanningTools()
        {
            AssertEqual(ChatModes.Plan, ChatModes.Normalize("PLAN"), "plan mode normalizes");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "common.capabilities_read", "Plan includes progressive capability policy");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "Never substitute chat prose or an HTML workspace", "Plan requires the Markdown artifact");
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var selected = ConversationRunPolicy.For(ChatModes.Plan).SelectTools(tools);
                AssertTrue(selected.Any(item => item.Id == PlanDocumentToolCatalog.SaveToolId), "plan save available");
                AssertTrue(selected.Any(item => item.Id == PlanDocumentToolCatalog.RestoreToolId), "plan restore available");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    PlanDocumentToolCatalog.SaveToolId),
                    "Plan document tools use the native ToolRuntime");
                var planPolicy = selected.Single(item =>
                    item.Id == PlanDocumentToolCatalog.SaveToolId).Policy;
                AssertTrue(planPolicy != null &&
                    planPolicy.Effect == ToolEffect.Write &&
                    planPolicy.Verification == ToolVerification.Tool &&
                    !planPolicy.RequiresConfirmation &&
                    planPolicy.AllowedModes.SequenceEqual(new[] { "plan" }),
                    "Plan document carries exact source-owned verified-write policy");
                AssertTrue(selected.Any(item => item.Id == TaskListToolCatalog.SetToolId), "task list available");
                AssertTrue(selected.Any(item => item.Id == UserQuestionToolCatalog.AskToolId), "questions available");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    UserQuestionToolCatalog.AskToolId),
                    "questions use the native ToolRuntime");
                var questionPolicy = selected.Single(item =>
                    item.Id == UserQuestionToolCatalog.AskToolId).Policy;
                AssertTrue(questionPolicy != null &&
                    questionPolicy.Effect == ToolEffect.Read &&
                    !questionPolicy.IndependentLocalRead &&
                    questionPolicy.AllowedModes.SequenceEqual(new[] { "plan" }),
                    "questions carry exact source-owned Plan policy");
                AssertTrue(selected.Any(item => item.Id == ResourceToolCatalog.ReadToolId), "resource read available");
                AssertTrue(selected.All(item => !item.MutatesDocument), "document mutations excluded");
                AssertTrue(!ConversationRunPolicy.For(ChatModes.Plan).AllowsConfirmation, "Plan cannot confirm mutations");
                string contractError;
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                    new ToolCall("old-plan", "common.plan_doc_update", "{}"),
                    out contractError),
                    "pre-11O2 Plan calls require an explicit new chat/reset");
            });
        }

        private static void PlanModePersistsMarkdownAndAwaitsAnswers()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var created = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Migration plan", "markdown", "# Goal\n\nShip safely.", "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan document created");
                var data = JObject.Parse(created.DataJson);
                var planId = (string)data["planId"];
                var revisionId = (string)data["artifactId"];
                var createMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolCatalog.SaveToolId,
                        "title", "Migration plan", "markdown", "# Goal\n\nShip safely.", "status", "draft"), created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var projectionCommand = Command(PlanDocumentToolCatalog.SaveToolId);
                projectionCommand.ToolCallId = "plan-projection";
                var projectedCreate = ModelToolResultProjection.Project(
                    AgentJsonProtocol.CreateToolResultMessage(
                        projectionCommand,
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok(
                            created.Message, created.DataJson)));
                AssertTrue(projectedCreate.Content.IndexOf("planId", StringComparison.Ordinal) < 0 &&
                    projectedCreate.Content.IndexOf("artifactId", StringComparison.Ordinal) < 0 &&
                    projectedCreate.Content.IndexOf("\"revision\"", StringComparison.Ordinal) < 0 &&
                    projectedCreate.Content.IndexOf("\"version\":1", StringComparison.Ordinal) >= 0,
                    "model Plan result keeps semantic version but omits exact runtime identity");
                var leakedIdentity = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "id", planId, "title", "Migration plan", "markdown", "# Changed", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("invalid_arguments", leakedIdentity.ErrorCode,
                    "model-owned Plan identity is rejected by the semantic schema");
                var updated = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Migration plan", "markdown", "# Ready\n\nExecute.", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "runtime-guarded revision succeeds");
                var runtimeContext = ConversationPromptComposer.BuildRuntimeContext(
                    ChatModes.Plan, adapter, tools, null, null, session);
                var activePlan = (JObject)JObject.Parse(runtimeContext)["active_plan"];
                AssertTrue(runtimeContext.IndexOf("revision_uri", StringComparison.Ordinal) < 0 &&
                    runtimeContext.IndexOf("rna://", StringComparison.Ordinal) < 0 &&
                    activePlan["id"] == null &&
                    (string)activePlan["title"] == "Migration plan" &&
                    (string)activePlan["status"] == "ready",
                    "active plan runtime context hides exact resource identity");
                var updateMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolCatalog.SaveToolId,
                        "title", "Migration plan", "markdown", "# Ready\n\nExecute.", "status", "ready"), updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);
                session.Messages.Remove(updateMessage);
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(session);
                ChatResourceReferenceService.PruneUnreachable(session);
                AssertEqual(revisionId, session.ActivePlanDocumentArtifactId, "history rewind restores prior plan revision");

                var question = executor.ExecuteManual(Command(UserQuestionToolCatalog.AskToolId, "questions", new JArray(
                    new JObject
                    {
                        ["header"] = "Scope", ["prompt"] = "Choose scope", ["selection"] = "multiple",
                        ["options"] = new JArray(
                            new JObject { ["label"] = "Core", ["description"] = "Core only" },
                            new JObject { ["label"] = "UI", ["description"] = "Include UI" })
                    })), tools, new AppSettings(), false, false, session);
                AssertTrue(question.Status == "awaiting_user", "question pauses for user input: " + question.Message);
                var questionData = JObject.Parse(question.DataJson);
                AssertTrue(!string.IsNullOrWhiteSpace((string)questionData["questionSetId"]) &&
                    !string.IsNullOrWhiteSpace((string)questionData["questions"][0]["id"]) &&
                    !string.IsNullOrWhiteSpace((string)questionData["questions"][0]["options"][0]["id"]),
                    "runtime assigns UI-only question and option identity");

                var invalidQuestion = executor.ExecuteManual(
                    Command(UserQuestionToolCatalog.AskToolId,
                        "questions", new JArray()), tools,
                    new AppSettings(), false, false, session);
                var invalidQuestionRecovery = JObject.Parse(
                    invalidQuestion.DataJson)["recovery"];
                AssertEqual("RejectedNoEffect",
                    (string)invalidQuestionRecovery?["failureKind"],
                    "invalid questions certify no interaction effect");
                AssertEqual("Replan",
                    (string)invalidQuestionRecovery?["retryPolicy"],
                    "invalid questions require a corrected question set");
            });
        }

        private static void PlanModeNativeQuestionPausesKernel()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var calls = 0;
                var responses = new Queue<string>(new[]
                {
                    LoadToolSchemaResponse(UserQuestionToolCatalog.AskToolId),
                    ModelProtocolWire.Write("Нужен выбор.", new[]
                    {
                        new ConversationToolCall
                        {
                            Name = UserQuestionToolCatalog.AskToolId,
                            Arguments = new JObject
                            {
                                ["questions"] = new JArray(new JObject
                                {
                                    ["header"] = "Scope",
                                    ["prompt"] = "Choose scope",
                                    ["selection"] = "single",
                                    ["options"] = new JArray(
                                        new JObject
                                        {
                                            ["label"] = "Core",
                                            ["description"] = "Core only"
                                        },
                                        new JObject
                                        {
                                            ["label"] = "UI",
                                            ["description"] = "Include UI"
                                        })
                                })
                            }
                        }
                    })
                });
                LlmCompletionDelegate completion =
                    (settings, messages, options, stream, cancellationToken) =>
                {
                    calls++;
                    return Task.FromResult(new LlmCompletionResult
                    {
                        Content = responses.Dequeue()
                    });
                };
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var result = CreateConversationRunService(
                    adapter, executor, completion).ExecuteAsync(
                        ChatModes.Plan,
                        "Составь план и спроси только необходимое.",
                        session,
                        NewContext(adapter),
                        new AppSettings(),
                        tools,
                        null).GetAwaiter().GetResult();

                AssertEqual(2, calls,
                    "schema admission and native question use two model steps without a third");
                AssertEqual("awaiting_user",
                    session.LastRun.KernelState.Summary.Reason,
                    "kernel owns the typed local-interaction pause");
                AssertEqual(AgentResponseStatuses.AwaitingUser,
                    result.ResponseStatus,
                    "Plan projection preserves awaiting-user status");
                var activity = session.Messages.Last(message =>
                    message.Activity != null &&
                    message.Activity.ToolId ==
                        UserQuestionToolCatalog.AskToolId).Activity;
                AssertContains(activity.DataJson, "rnassistant.questions",
                    "typed question payload reaches the existing UI projection");
                var projected = ModelToolResultProjection.Project(
                    session.Messages.Last(message =>
                        message.ToolName == UserQuestionToolCatalog.AskToolId));
                AssertTrue(projected.Content.IndexOf("questionSetId", StringComparison.Ordinal) < 0 &&
                    projected.Content.IndexOf("\"id\"", StringComparison.Ordinal) < 0,
                    "model replay omits runtime question and option ids");
            });
        }

        private static void PlanDocumentUsesVerifiedNativeRuntime()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(item =>
                    item.Id == PlanDocumentToolCatalog.SaveToolId);
                var runtime = executor.CreateNativeRuntime(session,
                    new[] { definition }, new AppSettings(), ChatModes.Plan, false);
                var call = new ToolCall("plan-native-save",
                    PlanDocumentToolCatalog.SaveToolId,
                    "{\"title\":\"Native plan\",\"markdown\":\"# Native\\n\",\"status\":\"draft\"}");
                var policy = runtime.Describe(call);
                AssertTrue(policy != null && policy.MayHaveSideEffects &&
                    policy.Policy.Verification == ToolVerification.Tool,
                    "Plan save has one exact verified-write registration");

                var created = ExecuteNative(runtime, call, policy);
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                    "native Plan save creates the missing plan");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    created.Evidence.Dispatch,
                    "native Plan save records its session mutation boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    created.Evidence.Effect,
                    "native Plan save verifies the exact active revision");
                AssertEqual((string)JObject.Parse(created.Result.DataJson)["artifactId"],
                    session.ActivePlanDocumentArtifactId,
                    "verified result identifies the exact active Plan artifact");

                session.ActivePlanDocumentArtifactId = "missing";
                var invalidCall = new ToolCall("plan-native-invalid-active",
                    PlanDocumentToolCatalog.SaveToolId,
                    "{\"title\":\"Native plan\",\"markdown\":\"# Changed\",\"status\":\"draft\"}");
                var invalid = ExecuteNative(runtime, invalidCall,
                    runtime.Describe(invalidCall));
                AssertEqual(ToolExecutionOutcome.Error, invalid.Outcome,
                    "ambiguous active state stays a known error");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    invalid.Evidence.Dispatch,
                    "semantic rejection occurs before the mutation boundary");
                var invalidRecovery =
                    JObject.Parse(invalid.Result.DataJson)["recovery"];
                AssertEqual("RejectedNoEffect",
                    (string)invalidRecovery?["failureKind"],
                    "invalid Plan state certifies no chat-state effect");
                AssertEqual("Replan",
                    (string)invalidRecovery?["retryPolicy"],
                    "invalid Plan state requires a changed model action");
                session.ActivePlanDocumentArtifactId =
                    (string)JObject.Parse(created.Result.DataJson)["artifactId"];

                var artifactCount = session.Artifacts.Count;
                var dryRun = executor.ExecuteManual(Command(
                    PlanDocumentToolCatalog.SaveToolId,
                    "title", "Native plan", "markdown", "# Preview",
                    "status", "draft"), tools, new AppSettings(),
                    true, true, session);
                AssertTrue(dryRun.Success && session.Artifacts.Count == artifactCount,
                    "native Plan dry-run validates schema without mutating session");
                AssertTrue(runtime.Describe(new ToolCall("wrong-case",
                    "COMMON.PLAN_DOC_SAVE", "{}")) == null,
                    "native Plan ownership has no case alias");
            });
        }

        private static void PlanDocumentPreservesExactMarkdownAndLinearHead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var originalMarkdown = "\n# Exact plan\n\nKeep trailing Markdown spaces.  \n\n";
                var created = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Exact plan", "markdown", originalMarkdown, "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "exact plan created");
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["planId"];
                var firstId = (string)createdData["artifactId"];
                var first = session.Artifacts.Single(item => item.Id == firstId);
                AssertEqual(originalMarkdown, first.InlineText, "create preserves the complete Markdown payload");

                var updatedMarkdown = "  \n# Exact ready plan\n\nDo not trim this revision.\n\n";
                var updated = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Exact plan",
                    "markdown", updatedMarkdown,
                    "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "exact guarded update succeeds");
                var secondId = (string)JObject.Parse(updated.DataJson)["artifactId"];
                var second = session.Artifacts.Single(item => item.Id == secondId);
                AssertEqual(updatedMarkdown, second.InlineText, "update preserves the complete Markdown payload");
                AssertEqual(2, second.Revision, "revision is strictly monotonic");
                AssertEqual(firstId, second.ParentArtifactId, "revision is a linear child of the exact current head");

                var stale = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "id", planId,
                    "title", "Exact plan",
                    "markdown", "# stale",
                    "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("invalid_arguments", stale.ErrorCode,
                    "caller-owned Plan identity is rejected");

                session.Artifacts.Add(new ChatArtifact
                {
                    Id = planId + "_r4_conflict",
                    Kind = ChatArtifactKinds.PlanDocument,
                    Title = second.Title,
                    MimeType = "text/markdown",
                    Revision = 4,
                    ParentArtifactId = second.Id,
                    InlineText = "# conflicting branch",
                    MetadataJson = second.MetadataJson
                });
                var artifactCount = session.Artifacts.Count;
                var conflict = executor.ExecuteManual(Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Exact plan",
                    "markdown", "# must not append",
                    "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(conflict.Success, "disposable lineage is rebuilt from the document owner before saving");
                AssertEqual(artifactCount, session.Artifacts.Count, "the invented revision is replaced by one committed child");
                AssertTrue(!session.Artifacts.Any(item => item.Id == planId + "_r4_conflict"), "unpublished projection cannot become lineage");
                AssertEqual(secondId, session.Artifacts.Single(item => item.Id == session.ActivePlanDocumentArtifactId).ParentArtifactId,
                    "new revision extends the exact committed head");
            });
        }

        private static void PlanDocumentRestoreAndRemovalStayAppendOnly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var createCommand = Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Release plan", "markdown", "# Original\n\nKeep this exact body.\n", "status", "draft");
                var created = executor.ExecuteManual(createCommand, tools, new AppSettings(), false, false, session);
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["planId"];
                var firstId = (string)createdData["artifactId"];
                var createMessage = AgentTranscript.CreateLocalResultMessage(createCommand, created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);

                var updateCommand = Command(PlanDocumentToolCatalog.SaveToolId,
                    "title", "Release plan v2",
                    "markdown", "# Current\n\nThis will be replaced by restore.\n",
                    "status", "ready");
                var updated = executor.ExecuteManual(updateCommand, tools, new AppSettings(), false, false, session);
                var secondId = (string)JObject.Parse(updated.DataJson)["artifactId"];
                var updateMessage = AgentTranscript.CreateLocalResultMessage(updateCommand, updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);

                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                store.LoadArtifactBody(session, firstId);
                var first = session.Artifacts.Single(item => item.Id == firstId);
                var firstUri = ChatResourceUri.CreateArtifactRevisionUri(session, first);
                var restoreCommand = Command(PlanDocumentToolCatalog.RestoreToolId,
                    "version", 1);
                var restored = executor.ExecuteManual(restoreCommand, tools, new AppSettings(), false, false, session);
                AssertTrue(restored.Success, "historical Plan revision restores as a new head");
                var restoredData = JObject.Parse(restored.DataJson);
                var thirdId = (string)restoredData["artifactId"];
                var third = session.Artifacts.Single(item => item.Id == thirdId);
                AssertEqual(3, third.Revision, "restore appends the next monotonic revision");
                AssertEqual(secondId, third.ParentArtifactId, "restore remains linear from the current head");
                AssertEqual(first.InlineText, third.InlineText, "restore copies the selected exact body");
                AssertEqual(first.Title, third.Title, "restore copies the selected title");
                AssertEqual(firstId, (string)JObject.Parse(third.MetadataJson)["restoredFromArtifactId"],
                    "restore records exact provenance");
                var authorityScope = executor.ResourceAuthority.Scope(session, true);
                var logicalPlan = DocumentArtifactStore.PlanIdentity(session, planId);
                var logicalHead = executor.ResourceAuthority.Store.GetHead(authorityScope, logicalPlan);
                var logicalMetadata = executor.ResourceAuthority.Revisions.GetRevision(authorityScope, logicalHead.Revision);
                AssertTrue(logicalMetadata.RestoredFrom != null && logicalMetadata.RestoredFrom.Identity.Equals(logicalPlan),
                    "logical restore points to the original logical Plan revision");
                AssertTrue(executor.ResourceAuthority.Revisions.GetRevision(authorityScope, logicalMetadata.RestoredFrom).Dependencies
                    .Any(dependency => dependency.Resource.Uri == firstUri), "restore authority identifies the selected historical snapshot");
                var restoreMessage = AgentTranscript.CreateLocalResultMessage(restoreCommand, restored);
                session.Messages.Add(restoreMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 2);
                store.Save(session);
                AssertAppendOnlyPlanCommit(store, session, "restore");

                var staleDelete = executor.ExecuteManual(Command(PlanDocumentToolCatalog.DeleteToolId,
                    "id", planId),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("invalid_arguments", staleDelete.ErrorCode,
                    "delete schema rejects caller-owned Plan identity");
                var beforeDeleteArtifactCount = session.Artifacts.Count;
                var pinnedBefore = session.Messages
                    .SelectMany(message => message.ResourceRefs)
                    .Select(reference => reference.Uri)
                    .ToArray();
                var deleteCommand = Command(PlanDocumentToolCatalog.DeleteToolId);
                var removed = executor.ExecuteManual(deleteCommand,
                    tools, new AppSettings(), false, false, session);
                AssertTrue(removed.Success, "guarded Plan removal succeeds");
                var removedData = JObject.Parse(removed.DataJson);
                var tombstoneId = (string)removedData["artifactId"];
                AssertEqual(beforeDeleteArtifactCount + 1, session.Artifacts.Count,
                    "removal appends one artifact without deleting revisions");
                AssertEqual(3, (int)removedData["removedRevisions"], "removal reports affected immutable revisions");
                AssertEqual(3, ((JArray)removedData["referencingMessageIds"]).Count,
                    "removal reports every currently referencing message");
                AssertEqual(string.Empty, session.ActivePlanDocumentArtifactId ?? string.Empty,
                    "removed logical Plan has no active head");
                AssertTrue(PlanDocumentService.IsTombstone(session.Artifacts.Single(item => item.Id == tombstoneId)),
                    "new terminal revision is the removal tombstone");
                AssertTrue(pinnedBefore.SequenceEqual(session.Messages
                    .SelectMany(message => message.ResourceRefs)
                    .Select(reference => reference.Uri)),
                    "removal never rewrites exact historical message references");
                var deleteMessage = AgentTranscript.CreateLocalResultMessage(deleteCommand, removed);
                session.Messages.Add(deleteMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 3);
                AssertEqual(deleteMessage.Id, session.Artifacts.Single(item => item.Id == tombstoneId).SourceMessageId,
                    "model-linked tombstone records its exact source message");

                var library = ArtifactLibraryProjectionService.Project(session);
                AssertTrue(!library.Heads.Any(item => item.LogicalId == planId),
                    "removed Plan is absent from new library heads");
                AssertTrue(library.RemovedResourceUris.Contains(firstUri, StringComparer.OrdinalIgnoreCase),
                    "library marks the exact historical revision as removed");
                AssertTrue(ChatResourcePromptIndex.Build(session, 2000).IndexOf(firstUri, StringComparison.OrdinalIgnoreCase) < 0,
                    "removed Plan is not admitted to a new resource working set");

                store.Save(session);
                AssertAppendOnlyPlanCommit(store, session, "removal");
                var loaded = new ChatStore(FixturePaths.Value).Load(session.Host, session.DocumentKey, session.Id);
                AssertEqual(4, loaded.Artifacts.Count(item => PlanDocumentService.PlanId(item) == planId),
                    "replay retains all three bodies plus the tombstone");
                AssertTrue(loaded.Messages.First().ResourceRefs.Any(reference => reference.Uri == firstUri),
                    "replay retains the original pinned message reference");
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(loaded);
                AssertEqual(string.Empty, loaded.ActivePlanDocumentArtifactId ?? string.Empty,
                    "history projection cannot resurrect a tombstoned Plan");
                ChatResourceReferenceService.PruneUnreachable(loaded);
                AssertTrue(loaded.Artifacts.Any(item => item.Id == tombstoneId),
                    "applicable model-linked tombstone survives reachability pruning");
                AssertTrue(ChatCloneService.CloneArtifactsForMessages(loaded.Artifacts, loaded.Messages)
                    .Any(item => item.Id == tombstoneId), "fork after removal retains its model-linked tombstone");

                var rewound = ChatCloneService.CloneSessionSnapshot(loaded);
                rewound.Messages.RemoveAll(message => message.Id == deleteMessage.Id);
                ChatResourceReferenceService.PruneUnreachable(rewound);
                AssertTrue(rewound.Artifacts.Any(item => item.Id == tombstoneId),
                    "history rewind preserves the document-owned tombstone");
                AssertTrue(string.IsNullOrEmpty(rewound.ActivePlanDocumentArtifactId),
                    "history rewind cannot restore a removed document Plan");
                var forkMessages = ChatCloneService.CloneMessages(loaded.Messages
                    .Where(message => message.Id != deleteMessage.Id));
                var fork = new ChatSession
                {
                    DocumentAuthorityId = loaded.DocumentAuthorityId,
                    Id = "plan_fork",
                    Messages = forkMessages,
                    Artifacts = ChatCloneService.CloneArtifactsForMessages(loaded.Artifacts, forkMessages)
                };
                ChatResourceReferenceService.LinkMessageResources(fork, 0);
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(fork);
                AssertTrue(fork.Artifacts.Any(item => item.Id == tombstoneId),
                    "fork before removal preserves the document-owned tombstone");
                AssertTrue(string.IsNullOrEmpty(fork.ActivePlanDocumentArtifactId),
                    "fork cannot restore a removed document Plan");

                var manual = ChatCloneService.CloneSessionSnapshot(loaded);
                manual.Artifacts.Single(item => item.Id == tombstoneId).SourceMessageId = null;
                manual.Messages.RemoveAll(message => message.Id == deleteMessage.Id);
                ChatResourceReferenceService.PruneUnreachable(manual);
                AssertTrue(manual.Artifacts.Any(item => item.Id == tombstoneId),
                    "direct UI tombstone remains session-level without a source message");
                AssertEqual(string.Empty, manual.ActivePlanDocumentArtifactId ?? string.Empty,
                    "session-level tombstone prevents historical Plan resurrection");

                var removedRead = executor.ExecuteManual(Command(ResourceToolCatalog.ReadToolId,
                    "target", "plan: " + first.Title, "representation", "text"),
                    tools, new AppSettings(), false, false, loaded);
                AssertEqual("resource_target_not_found", removedRead.ErrorCode,
                    "removed historical target is not resolved or silently moved to another revision");
                var listed = executor.ExecuteManual(Command(ResourceToolCatalog.FindToolId,
                    "query", first.Title, "scope", "conversation"),
                    tools, new AppSettings(), false, false, loaded);
                AssertTrue(listed.Success, "semantic resource find remains available after Plan removal");
                AssertEqual(0, (int)JObject.Parse(listed.DataJson)["total"],
                    "removed Plan revisions and tombstone are absent from discovery");

                loaded.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the remaining context." });
                loaded.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });
                loaded.Messages.Add(new ChatMessage { Role = "user", Content = "Continue without the removed Plan." });
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                    Task.FromResult(CompactionReply(messages,
                        "Plan removal retained."));
                var checkpoint = new ContextCompactionService(completion).EnsureWithinBudgetAsync(
                    loaded, new AppSettings(), null, true, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(checkpoint != null, "compaction checkpoint is created after Plan removal");
                var compactionMessage = loaded.Messages.Last(message =>
                    message.Activity != null && message.Activity.Kind == "compaction");
                AssertTrue(!compactionMessage.ResourceRefs.Any(reference =>
                    string.Equals(reference.Uri, firstUri, StringComparison.OrdinalIgnoreCase)),
                    "removed Plan is not admitted to the compaction checkpoint working set");
            });
        }

        private static void AssertAppendOnlyPlanCommit(ChatStore store, ChatSession session, string operation)
        {
            var commit = store.ReadEvents(session.Host, session.DocumentKey, session.Id)
                .Last(item => string.Equals(item.Type, SessionEventTypes.SessionCommit, StringComparison.Ordinal));
            var operationTypes = ((JArray)commit.Data["Operations"])
                .Select(item => (string)item["Type"])
                .ToList();
            AssertTrue(!operationTypes.Contains(SessionOperationTypes.ArtifactRevisionCreated),
                operation + " keeps document snapshots out of the chat event store");
            AssertTrue(!operationTypes.Contains(SessionOperationTypes.ArtifactRemove),
                operation + " never appends artifact.remove");
        }
    }
}
