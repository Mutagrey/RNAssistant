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
        private static void PlanModeFiltersMutationsAndKeepsPlanningTools()
        {
            AssertEqual(ChatModes.Plan, ChatModes.Normalize("PLAN"), "plan mode normalizes");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "common.capabilities_read", "Plan includes progressive capability policy");
            AssertContains(ConversationPromptComposer.BuildInstruction(ChatModes.Plan, new AppSettings()),
                "Never substitute chat prose or an HTML workspace", "Plan requires the Markdown artifact");
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var selected = ConversationRunPolicy.For(ChatModes.Plan).SelectTools(tools);
                AssertTrue(selected.Any(item => item.Id == PlanDocumentToolCatalog.CreateToolId), "plan create available");
                AssertTrue(selected.Any(item => item.Id == PlanDocumentToolCatalog.RestoreToolId), "plan restore available");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    PlanDocumentToolCatalog.CreateToolId),
                    "Plan document tools use the native ToolRuntime");
                var planPolicy = selected.Single(item =>
                    item.Id == PlanDocumentToolCatalog.CreateToolId).RuntimePolicy;
                AssertTrue(planPolicy != null &&
                    planPolicy.Effect == ToolEffect.Write &&
                    planPolicy.Verification == ToolVerification.Tool &&
                    !planPolicy.RequiresConfirmation &&
                    planPolicy.AllowedModes.SequenceEqual(new[] { "plan" }),
                    "Plan document carries exact source-owned verified-write policy");
                AssertTrue(selected.Any(item => item.Id == TaskListToolExecutor.CreateToolId), "task list available");
                AssertTrue(selected.Any(item => item.Id == UserQuestionToolCatalog.AskToolId), "questions available");
                AssertTrue(NativeToolRuntimeAdapter.Owns(
                    UserQuestionToolCatalog.AskToolId),
                    "questions use the native ToolRuntime");
                var questionPolicy = selected.Single(item =>
                    item.Id == UserQuestionToolCatalog.AskToolId).RuntimePolicy;
                AssertTrue(questionPolicy != null &&
                    questionPolicy.Effect == ToolEffect.Read &&
                    !questionPolicy.IndependentLocalRead &&
                    questionPolicy.AllowedModes.SequenceEqual(new[] { "plan" }),
                    "questions carry exact source-owned Plan policy");
                AssertTrue(selected.Any(item => item.Id == ResourceToolCatalog.ReadToolId), "resource read available");
                AssertTrue(selected.All(item => !item.MutatesDocument), "document mutations excluded");
                AssertTrue(!ConversationRunPolicy.For(ChatModes.Plan).AllowsConfirmation, "Plan cannot confirm mutations");
            });
        }

        private static void PlanModePersistsMarkdownAndAwaitsAnswers()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var created = executor.Execute(Command(PlanDocumentToolCatalog.CreateToolId,
                    "title", "Migration plan", "markdown", "# Goal\n\nShip safely.", "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "plan document created");
                var data = JObject.Parse(created.DataJson);
                var planId = (string)data["planId"];
                var revisionId = (string)data["artifactId"];
                var createMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolCatalog.CreateToolId), created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);
                var stale = executor.Execute(Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId, "expectedRevisionArtifactId", "stale", "markdown", "# Changed", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("stale_plan_revision", stale.ErrorCode, "stale revision rejected");
                var updated = executor.Execute(Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId, "expectedRevisionArtifactId", revisionId, "markdown", "# Ready\n\nExecute.", "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "guarded revision succeeds");
                AssertContains(ConversationPromptComposer.BuildRuntimeContext(ChatModes.Plan, adapter, tools, null, null, session),
                    "revision_uri", "active plan URI enters runtime context");
                var updateMessage = AgentTranscript.CreateLocalResultMessage(
                    Command(PlanDocumentToolCatalog.UpdateToolId), updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);
                session.Messages.Remove(updateMessage);
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(session);
                ChatResourceReferenceService.PruneUnreachable(session);
                AssertEqual(revisionId, session.ActivePlanDocumentArtifactId, "history rewind restores prior plan revision");

                var question = executor.Execute(Command(UserQuestionToolCatalog.AskToolId, "questions", new JArray(
                    new JObject
                    {
                        ["id"] = "scope", ["header"] = "Scope", ["prompt"] = "Choose scope", ["selection"] = "multiple",
                        ["options"] = new JArray(
                            new JObject { ["id"] = "core", ["label"] = "Core", ["description"] = "Core only" },
                            new JObject { ["id"] = "ui", ["label"] = "UI", ["description"] = "Include UI" })
                    })), tools, new AppSettings(), false, false, session);
                AssertTrue(question.Status == "awaiting_user", "question pauses for user input: " + question.Message);
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
                            Arguments = new Dictionary<string, object>
                            {
                                ["questions"] = new JArray(new JObject
                                {
                                    ["id"] = "scope",
                                    ["header"] = "Scope",
                                    ["prompt"] = "Choose scope",
                                    ["selection"] = "single",
                                    ["options"] = new JArray(
                                        new JObject
                                        {
                                            ["id"] = "core",
                                            ["label"] = "Core",
                                            ["description"] = "Core only"
                                        },
                                        new JObject
                                        {
                                            ["id"] = "ui",
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
                var tools = adapter.GetBuiltInTools()
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
            });
        }

        private static void PlanDocumentUsesVerifiedNativeRuntime()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"),
                delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = adapter.GetBuiltInTools()
                    .Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(item =>
                    item.Id == PlanDocumentToolCatalog.CreateToolId);
                var runtime = executor.CreateNativeRuntime(session,
                    new[] { definition }, new AppSettings(), ChatModes.Plan, false);
                var call = new ToolCall("plan-native-create",
                    PlanDocumentToolCatalog.CreateToolId,
                    "{\"title\":\"Native plan\",\"markdown\":\"# Native\\n\"}");
                var policy = runtime.Describe(call);
                AssertTrue(policy != null && policy.MayHaveSideEffects &&
                    policy.Policy.Verification == ToolVerification.Tool,
                    "Plan create has one exact verified-write registration");

                var created = ExecuteNative(runtime, call, policy);
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome,
                    "native Plan create succeeds");
                AssertEqual(ToolDispatchEvidence.MayHaveDispatched,
                    created.Evidence.Dispatch,
                    "native Plan create records its session mutation boundary");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    created.Evidence.Effect,
                    "native Plan create verifies the exact active revision");
                AssertEqual((string)JObject.Parse(created.Result.DataJson)["artifactId"],
                    session.ActivePlanDocumentArtifactId,
                    "verified result identifies the exact active Plan artifact");

                var duplicateCall = new ToolCall("plan-native-duplicate",
                    PlanDocumentToolCatalog.CreateToolId,
                    "{\"title\":\"Duplicate\",\"markdown\":\"# Duplicate\"}");
                var duplicate = ExecuteNative(runtime, duplicateCall,
                    runtime.Describe(duplicateCall));
                AssertEqual(ToolExecutionOutcome.Error, duplicate.Outcome,
                    "semantic rejection stays a known error");
                AssertEqual(ToolDispatchEvidence.NotDispatched,
                    duplicate.Evidence.Dispatch,
                    "semantic rejection occurs before the mutation boundary");

                var artifactCount = session.Artifacts.Count;
                var dryRun = executor.Execute(Command(
                    PlanDocumentToolCatalog.UpdateToolId,
                    "id", (string)JObject.Parse(created.Result.DataJson)["planId"],
                    "expectedRevisionArtifactId", session.ActivePlanDocumentArtifactId,
                    "markdown", "# Preview"), tools, new AppSettings(),
                    true, true, session);
                AssertTrue(dryRun.Success && session.Artifacts.Count == artifactCount,
                    "native Plan dry-run validates schema without mutating session");
                AssertTrue(runtime.Describe(new ToolCall("wrong-case",
                    "COMMON.PLAN_DOC_CREATE", "{}")) == null,
                    "native Plan ownership has no case alias");
            });
        }

        private static void PlanDocumentPreservesExactMarkdownAndLinearHead()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var originalMarkdown = "\n# Exact plan\n\nKeep trailing Markdown spaces.  \n\n";
                var created = executor.Execute(Command(PlanDocumentToolCatalog.CreateToolId,
                    "title", "Exact plan", "markdown", originalMarkdown, "status", "draft"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(created.Success, "exact plan created");
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["planId"];
                var firstId = (string)createdData["artifactId"];
                var first = session.Artifacts.Single(item => item.Id == firstId);
                AssertEqual(originalMarkdown, first.InlineText, "create preserves the complete Markdown payload");

                var updatedMarkdown = "  \n# Exact ready plan\n\nDo not trim this revision.\n\n";
                var updated = executor.Execute(Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId,
                    "expectedRevisionArtifactId", firstId,
                    "markdown", updatedMarkdown,
                    "status", "ready"),
                    tools, new AppSettings(), false, false, session);
                AssertTrue(updated.Success, "exact guarded update succeeds");
                var secondId = (string)JObject.Parse(updated.DataJson)["artifactId"];
                var second = session.Artifacts.Single(item => item.Id == secondId);
                AssertEqual(updatedMarkdown, second.InlineText, "update preserves the complete Markdown payload");
                AssertEqual(2, second.Revision, "revision is strictly monotonic");
                AssertEqual(firstId, second.ParentArtifactId, "revision is a linear child of the exact current head");

                var stale = executor.Execute(Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId,
                    "expectedRevisionArtifactId", firstId,
                    "markdown", "# stale"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("stale_plan_revision", stale.ErrorCode, "old exact guard is rejected");

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
                var conflict = executor.Execute(Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId,
                    "expectedRevisionArtifactId", secondId,
                    "markdown", "# must not append"),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("plan_lineage_conflict", conflict.ErrorCode, "non-linear lineage is rejected");
                AssertEqual(artifactCount, session.Artifacts.Count, "lineage rejection does not append a revision");
                AssertEqual(secondId, session.ActivePlanDocumentArtifactId, "lineage rejection keeps the exact current head");
            });
        }

        private static void PlanDocumentRestoreAndRemovalStayAppendOnly()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                session.Mode = ChatModes.Plan;
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var createCommand = Command(PlanDocumentToolCatalog.CreateToolId,
                    "title", "Release plan", "markdown", "# Original\n\nKeep this exact body.\n", "status", "draft");
                var created = executor.Execute(createCommand, tools, new AppSettings(), false, false, session);
                var createdData = JObject.Parse(created.DataJson);
                var planId = (string)createdData["planId"];
                var firstId = (string)createdData["artifactId"];
                var createMessage = AgentTranscript.CreateLocalResultMessage(createCommand, created);
                session.Messages.Add(createMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 0);

                var updateCommand = Command(PlanDocumentToolCatalog.UpdateToolId,
                    "id", planId,
                    "expectedRevisionArtifactId", firstId,
                    "title", "Release plan v2",
                    "markdown", "# Current\n\nThis will be replaced by restore.\n",
                    "status", "ready");
                var updated = executor.Execute(updateCommand, tools, new AppSettings(), false, false, session);
                var secondId = (string)JObject.Parse(updated.DataJson)["artifactId"];
                var updateMessage = AgentTranscript.CreateLocalResultMessage(updateCommand, updated);
                session.Messages.Add(updateMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 1);

                var store = new ChatStore(FixturePaths.Value);
                store.Save(session);
                var first = session.Artifacts.Single(item => item.Id == firstId);
                var firstUri = ChatResourceUri.CreateArtifactRevisionUri(session, first);
                var restoreCommand = Command(PlanDocumentToolCatalog.RestoreToolId,
                    "id", planId,
                    "expectedRevisionArtifactId", secondId,
                    "sourceRevisionArtifactId", firstId);
                var restored = executor.Execute(restoreCommand, tools, new AppSettings(), false, false, session);
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
                var restoreMessage = AgentTranscript.CreateLocalResultMessage(restoreCommand, restored);
                session.Messages.Add(restoreMessage);
                ChatResourceReferenceService.LinkMessageResources(session, 2);
                store.Save(session);
                AssertAppendOnlyPlanCommit(store, session, "restore");

                var staleDelete = executor.Execute(Command(PlanDocumentToolCatalog.DeleteToolId,
                    "id", planId, "expectedRevisionArtifactId", secondId),
                    tools, new AppSettings(), false, false, session);
                AssertEqual("stale_plan_revision", staleDelete.ErrorCode, "delete requires the exact current head");
                var beforeDeleteArtifactCount = session.Artifacts.Count;
                var pinnedBefore = session.Messages
                    .SelectMany(message => message.ResourceRefs)
                    .Select(reference => reference.Uri)
                    .ToArray();
                var deleteCommand = Command(PlanDocumentToolCatalog.DeleteToolId,
                    "id", planId, "expectedRevisionArtifactId", thirdId);
                var removed = executor.Execute(deleteCommand,
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
                AssertTrue(!rewound.Artifacts.Any(item => item.Id == tombstoneId),
                    "history rewind before the removal drops its model-linked tombstone");
                AssertEqual(thirdId, rewound.ActivePlanDocumentArtifactId,
                    "history rewind restores the prior exact Plan head");
                var forkMessages = ChatCloneService.CloneMessages(loaded.Messages
                    .Where(message => message.Id != deleteMessage.Id));
                var fork = new ChatSession
                {
                    Id = "plan_fork",
                    Messages = forkMessages,
                    Artifacts = ChatCloneService.CloneArtifactsForMessages(loaded.Artifacts, forkMessages)
                };
                ChatResourceReferenceService.LinkMessageResources(fork, 0);
                ChatResourceReferenceService.RestoreActivePlanDocumentFromMessages(fork);
                AssertTrue(!fork.Artifacts.Any(item => item.Id == tombstoneId),
                    "fork before removal excludes its model-linked tombstone");
                AssertEqual(thirdId, fork.ActivePlanDocumentArtifactId,
                    "fork before removal restores the prior exact Plan head");

                var manual = ChatCloneService.CloneSessionSnapshot(loaded);
                manual.Artifacts.Single(item => item.Id == tombstoneId).SourceMessageId = null;
                manual.Messages.RemoveAll(message => message.Id == deleteMessage.Id);
                ChatResourceReferenceService.PruneUnreachable(manual);
                AssertTrue(manual.Artifacts.Any(item => item.Id == tombstoneId),
                    "direct UI tombstone remains session-level without a source message");
                AssertEqual(string.Empty, manual.ActivePlanDocumentArtifactId ?? string.Empty,
                    "session-level tombstone prevents historical Plan resurrection");

                var removedRead = executor.Execute(Command(ResourceToolCatalog.ReadToolId,
                    "uri", firstUri, "representation", "text"),
                    tools, new AppSettings(), false, false, loaded);
                AssertEqual("resource_removed", removedRead.ErrorCode,
                    "exact historical read reports stable removal instead of falling forward");
                AssertEqual(false, removedRead.Retryable, "removed resource read is terminal");
                var listed = executor.Execute(Command(ResourceToolCatalog.ListToolId,
                    "provider", "chat", "kind", ChatArtifactKinds.PlanDocument),
                    tools, new AppSettings(), false, false, loaded);
                AssertTrue(listed.Success, "resource list remains available after Plan removal");
                AssertEqual(0, (int)JObject.Parse(listed.DataJson)["total"],
                    "removed Plan revisions and tombstone are absent from discovery");

                loaded.Messages.Add(new ChatMessage { Role = "user", Content = "Keep the remaining context." });
                loaded.Messages.Add(new ChatMessage { Role = "assistant", Content = "Understood." });
                loaded.Messages.Add(new ChatMessage { Role = "user", Content = "Continue without the removed Plan." });
                LlmCompletionDelegate completion = (settings, messages, options, stream, cancellationToken) =>
                    Task.FromResult(new LlmCompletionResult { Content = "{\"summary\":\"Plan removal retained.\"}" });
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
            AssertTrue(operationTypes.Contains(SessionOperationTypes.ArtifactRevisionCreated),
                operation + " appends an artifact revision event");
            AssertTrue(!operationTypes.Contains(SessionOperationTypes.ArtifactRemove),
                operation + " never appends artifact.remove");
        }
    }
}
