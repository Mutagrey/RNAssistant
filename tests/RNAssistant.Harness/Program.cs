using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private sealed class HarnessTest
        {
            public string Name { get; set; }
            public string Category { get; set; }
            public Action Run { get; set; }
            public Func<Task> RunAsync { get; set; }

            public Task ExecuteAsync()
            {
                if (RunAsync != null)
                {
                    return RunAsync();
                }
                Run();
                return Task.CompletedTask;
            }
        }

        private sealed class HostTaskScenario
        {
            public string Host { get; set; }
            public string UserText { get; set; }
            public string[] Responses { get; set; }
            public string[] ExpectedTools { get; set; }
        }

        public static int Main(string[] args)
        {
            var tests = new List<HarnessTest>
            {
                new HarnessTest { Name = "harness: native async execution", RunAsync = HarnessRunsNativeAsync },
                new HarnessTest { Name = "planner: strict json envelope", Run = PlannerStrictParsesJsonEnvelope },
                new HarnessTest { Name = "planner: accepts clean json fence and rejects prose", Run = PlannerAcceptsCleanJsonFenceAndRejectsProse },
                new HarnessTest { Name = "planner: rejects alternate envelopes", Run = PlannerRejectsAlternateEnvelopes },
                new HarnessTest { Name = "planner: rejects invalid intent and steps", Run = PlannerRejectsInvalidIntentAndSteps },
                new HarnessTest { Name = "planner: boundary corpus stays strict", Run = PlannerBoundaryCorpusStaysStrict },
                new HarnessTest { Name = "planner quality: requires tool rejects final", Run = ModelQualityRequiresToolRejectsFinal },
                new HarnessTest { Name = "modes: selects chat auto and agent", Run = ModesSelectChatAutoAndAgent },
                new HarnessTest { Name = "modes: missing session mode defaults to chat", Run = MissingSessionModeDefaultsToChat },
                new HarnessTest { Name = "modes: plain chat omits planner and activities", Run = PlainChatOmitsPlannerAndActivities },
                new HarnessTest { Name = "modes: plain chat repairs thought-only json", Run = PlainChatRepairsThoughtOnlyJson },
                new HarnessTest { Name = "modes: plain chat extracts answer without thought", Run = PlainChatExtractsAnswerWithoutThought },
                new HarnessTest { Name = "modes: image switches to compatible model", Run = ImageSwitchesToCompatibleModel },
                new HarnessTest { Name = "context: deleted message absent from rebuilt prompt", Run = DeletedMessageIsAbsentFromRebuiltContext },
                new HarnessTest { Name = "routing: required empty tool slice stops before llm", Run = RequiredEmptyToolSliceStopsBeforeLlm },
                new HarnessTest { Name = "routing: tool slice balances mutation and inspection", Run = ToolSliceBalancesMutationAndInspection },
                new HarnessTest { Name = "routing: vba creation enters mutation phase", Run = VbaCreationRouteAllowsMutation },
                new HarnessTest { Name = "routing: destructive chart advances to delete capability", Run = DestructiveChartRouteAdvancesToMutation },
                new HarnessTest { Name = "routing: short follow-up continues pending agent task", Run = ShortFollowUpContinuesPendingAgentTask },
                new HarnessTest { Name = "routing: unknown and excluded tools have precise diagnostics", Run = ToolValidationExplainsUnknownAndExcludedTools },
                new HarnessTest { Name = "routing: optional tool authoring is explicit", Run = OptionalToolAuthoringIsExplicitAndDoesNotCompleteDocumentTask },
                new HarnessTest { Name = "context: prompt budget keeps contiguous recent history", Run = PromptBudgetKeepsContiguousRecentHistory },
                new HarnessTest { Name = "context: prompt budget compresses earlier history", Run = PromptBudgetCompressesEarlierHistory },
                new HarnessTest { Name = "models: explicit catalog url and standard data shape", Run = ModelCatalogUsesExplicitUrlAndStandardDataShape },
                new HarnessTest { Name = "desktop target: parses json descriptor", Run = ParsesOfficeTargetJsonDescriptor },
                new HarnessTest { Name = "desktop target: parses base64 descriptor", Run = ParsesOfficeTargetBase64Descriptor },
                new HarnessTest { Name = "desktop target: ignores utf8 bom", Run = OfficeTargetIgnoresUtf8Bom },
                new HarnessTest { Name = "desktop target: registry manual mode", Run = TargetRegistryManualModeKeepsSelection },
                new HarnessTest { Name = "desktop target: registry auto mode", Run = TargetRegistryAutoModeCanSwitchSelection },
                new HarnessTest { Name = "desktop com: dispatcher runs STA", Run = OfficeStaDispatcherRunsSta },
                new HarnessTest { Name = "desktop com: adapter dispatches calls", Run = DispatchedAdapterDelegatesCalls },
                new HarnessTest { Name = "documents: catalog activates selected document", Run = DocumentCatalogActivatesSelectedDocument },
                new HarnessTest { Name = "documents: recognizes web paths", Run = DocumentOpenServiceRecognizesWebPaths },
                new HarnessTest { Name = "documents: unsaved identity is stable", Run = UnsavedDocumentIdentityUsesStoredId },
                new HarnessTest { Name = "storage: chat roundtrip", Run = CreatesAndListsChatsInTempRoot },
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles },
                new HarnessTest { Name = "storage: deletes document chats", Run = DeletesDocumentChats },
                new HarnessTest { Name = "attachments: import commit delete", Run = AttachmentImportCommitDelete },
                new HarnessTest { Name = "attachments: multimodal api payload", Run = AttachmentMultimodalApiPayload },
                new HarnessTest { Name = "llm: streaming SSE response", Run = LlmStreamingResponseIsAggregated },
                new HarnessTest { Name = "llm: separates reasoning metadata", Run = LlmReasoningMetadataIsSeparated },
                new HarnessTest { Name = "llm: rejects alternate completion formats", Run = LlmAlternateCompletionFormatsAreRejected },
                new HarnessTest { Name = "llm: reports invalid response envelope", Run = LlmInvalidResponseEnvelopeIsReported },
                new HarnessTest { Name = "attachments: extracts pdf text", Run = AttachmentExtractsPdfText },
                new HarnessTest { Name = "attachments: accepts text formats and encodings", Run = AttachmentAcceptsTextFormatsAndEncodings },
                new HarnessTest { Name = "attachments: stores extracted text sidecar", Run = AttachmentStoresExtractedTextSidecar },
                new HarnessTest { Name = "attachments: visual pdf payload", Run = AttachmentBuildsVisualPdfPayload },
                new HarnessTest { Name = "attachments: rejects unsupported file", Run = AttachmentRejectsUnsupportedFile },
                new HarnessTest { Name = "attachments: cleans stale drafts", Run = AttachmentCleansStaleDrafts },
                new HarnessTest { Name = "chat sessions: document key migration", Run = ChatSessionServiceMigratesDocumentKey },
                new HarnessTest { Name = "chat sessions: stale requested id fallback", Run = ChatSessionServiceFallsBackForStaleRequestedId },
                new HarnessTest { Name = "chat sessions: empty drafts are transient", Run = EmptyChatDraftsAreNotPersisted },
                new HarnessTest { Name = "pipeline: dry-run resolves placeholders", Run = PipelineDryRunResolvesPlaceholders },
                new HarnessTest { Name = "pipeline: executes fake adapter steps", Run = PipelineExecutesFakeAdapterSteps },
                new HarnessTest { Name = "pipeline: resolves step output placeholders", Run = PipelineResolvesStepOutputPlaceholders },
                new HarnessTest { Name = "pipeline: stops after failed step", Run = PipelineStopsAfterFailedStep },
                new HarnessTest { Name = "pipeline: rejects missing step tool id", Run = PipelineRejectsMissingStepToolId },
                new HarnessTest { Name = "pipeline: rejects invalid definitions", Run = PipelineRejectsInvalidDefinitions },
                new HarnessTest { Name = "pipeline: rejects cycles", Run = PipelineRejectsCycles },
                new HarnessTest { Name = "pipeline: resolves nested confirmation before execution", Run = PipelineResolvesNestedConfirmationBeforeExecution },
                new HarnessTest { Name = "pipeline: effective safety propagates nested risk", Run = PipelineEffectiveSafetyPropagatesNestedRisk },
                new HarnessTest { Name = "pipeline: custom tool needs confirmation", Run = CustomPipelineNeedsConfirmation },
                new HarnessTest { Name = "pipeline: agent mode gates built-in mutation", Run = AgentModeGatesBuiltInMutation },
                new HarnessTest { Name = "tools: catalog merges visible tools", Run = ToolCatalogMergesVisibleTools },
                new HarnessTest { Name = "tools: store saves and updates custom tools", Run = ToolStoreSavesAndUpdatesCustomTools },
                new HarnessTest { Name = "tools: addressed store preserves extra files", Run = ToolStorePreservesExtraFilesAndOtherTools },
                new HarnessTest { Name = "tools: store skips broken custom tool files", Run = ToolStoreSkipsBrokenCustomToolFiles },
                new HarnessTest { Name = "tools: validates save and preserves metadata", Run = ValidatesToolSaveAndPreservesMetadata },
                new HarnessTest { Name = "tools: unknown and disabled tools fail", Run = UnknownAndDisabledToolsFail },
                new HarnessTest { Name = "tools: removed legacy ids are unknown", Run = RemovedLegacyToolIdsAreUnknown },
                new HarnessTest { Name = "tools: html workspace updates chat session", Run = HtmlWorkspaceToolsUpdateChatSession },
                new HarnessTest { Name = "tools: html workspace undo restores version", Run = HtmlWorkspaceUndoRestoresPreviousVersion },
                new HarnessTest { Name = "storage: html workspace persists with chat", Run = HtmlWorkspacePersistsWithChatSession },
                new HarnessTest { Name = "chat: agent creates html workspace", Run = ChatAgentCreatesHtmlWorkspace },
                new HarnessTest { Name = "chat: html mode forces workspace prompt", Run = ChatHtmlModeForcesWorkspacePrompt },
                new HarnessTest { Name = "chat: html workspace keeps generic follow-up route", Run = ChatHtmlWorkspaceKeepsGenericFollowUpRoute },
                new HarnessTest { Name = "chat: large malformed html planner response is rebuilt", Run = ChatLargeMalformedHtmlPlannerResponseIsRebuilt },
                new HarnessTest { Name = "chat: html delete requires read before mutation", Run = ChatHtmlDeleteRequiresReadBeforeMutation },
                new HarnessTest { Name = "tools: prompt templates save", Run = PromptToolSavesAgentPromptTemplates },
                new HarnessTest { Name = "tools: prompt defaults read", Run = PromptToolReadsDefaults },
                new HarnessTest { Name = "tools: validate custom tool payload", Run = ToolValidateChecksPayloadWithoutSaving },
                new HarnessTest { Name = "tools: expanded built-ins visible", Run = ExpandedBuiltInToolsAreVisible },
                new HarnessTest { Name = "prompt: tool metadata is weak-model friendly", Run = PromptToolMetadataIsWeakModelFriendly },
                new HarnessTest { Name = "tools: safety metadata gates mutations", Run = ToolSafetyMetadataGatesMutations },
                new HarnessTest { Name = "tools: pipeline effective mutation gates false metadata", Run = CustomPipelineWithMutatingStepNeedsConfirmationWhenMetadataLies },
                new HarnessTest { Name = "tools: confirmation matrix covers dry and manual runs", Run = ConfirmationMatrixCoversDryAndManualRuns },
                new HarnessTest { Name = "tools: agent can save custom tools with confirmation", Run = AgentCanSaveCustomToolsWithConfirmation },
                new HarnessTest { Name = "tools: agent validates and creates custom tool", Run = AgentValidatesAndCreatesCustomTool },
                new HarnessTest { Name = "tools: agent can author missing capability when enabled", Run = AgentCanCreateAndUseToolDuringDocumentTaskWhenEnabled },
                new HarnessTest { Name = "skills: store saves markdown skills", Run = SkillStoreSavesMarkdownSkills },
                new HarnessTest { Name = "skills: addressed store preserves extra files", Run = SkillStorePreservesExtraFilesAndOtherSkills },
                new HarnessTest { Name = "skills: store skips broken markdown skills", Run = SkillStoreSkipsBrokenMarkdownSkills },
                new HarnessTest { Name = "skills: catalog selects relevant skills", Run = SkillCatalogSelectsRelevantSkills },
                new HarnessTest { Name = "skills: prompt separates skills from tools", Run = PromptSeparatesSkillsFromTools },
                new HarnessTest { Name = "skills: prompt limits skill bodies", Run = PromptLimitsSkillBodies },
                new HarnessTest { Name = "prompt: editable agent blocks", Run = PromptUsesEditableAgentPromptBlocks },
                new HarnessTest { Name = "prompt: settings apply on next request", Run = PromptSettingsApplyOnNextRequest },
                new HarnessTest { Name = "skills: agent can save skills with confirmation", Run = AgentCanSaveSkillsWithConfirmation },
                new HarnessTest { Name = "vba: replace text backs up module", Run = VbaReplaceTextBacksUpModule },
                new HarnessTest { Name = "vba: apply patch targets named module", Run = VbaApplyPatchTargetsNamedModule },
                new HarnessTest { Name = "vba: backup store skips broken files", Run = VbaBackupStoreSkipsBrokenFiles },
                new HarnessTest { Name = "prompt: usage estimator counts context", Run = ContextUsageEstimatorCountsPromptAndSession },
                new HarnessTest { Name = "chat: completion service records prose", Run = ChatCompletionServiceRecordsProseResponse },
                new HarnessTest { Name = "chat: planner includes recent history", Run = ChatPlannerIncludesRecentHistory },
                new HarnessTest { Name = "chat: includes vba context when enabled", Run = ChatIncludesVbaContextWhenEnabled },
                new HarnessTest { Name = "chat: vba tasks auto include vba context", Run = ChatVbaTaskAutoIncludesVbaContext },
                new HarnessTest { Name = "chat: deferred smart title setting", Run = ChatCompletionServiceUsesDeferredSmartTitleSetting },
                new HarnessTest { Name = "chat: executes typical host tasks", Run = ChatExecutesTypicalHostTasks },
                new HarnessTest { Name = "chat: built-in mutation follows safety metadata", Run = ChatBuiltInMutationFollowsSafetyMetadata },
                new HarnessTest { Name = "chat: general answer skips Office reads and tools", Run = ChatGeneralAnswerSkipsOfficeReadsAndTools },
                new HarnessTest { Name = "chat: routing avoids substring false positives", Run = ChatRoutingAvoidsSubstringFalsePositives },
                new HarnessTest { Name = "chat: current document question uses read tool", Run = ChatCurrentDocumentQuestionUsesReadTool },
                new HarnessTest { Name = "chat: prose greeting requires strict repair", Run = ChatProseGreetingRequiresStrictRepair },
                new HarnessTest { Name = "chat: stateful Excel scenario verifies result", Run = ChatExcelStatefulScenarioVerifiesResult },
                new HarnessTest { Name = "chat: scenario llm checks prompt contracts", Run = ChatScenarioLlmChecksPromptContracts },
                new HarnessTest { Name = "chat: agent activity transcript", Run = AgentTranscriptCreatesActivityTree },
                new HarnessTest { Name = "chat: prose action forces tool follow-up", Run = ChatProseActionForcesToolFollowUp },
                new HarnessTest { Name = "chat: malformed action response forces repair", Run = ChatMalformedActionResponseForcesRepair },
                new HarnessTest { Name = "chat: repair final still forces tool", Run = ChatRepairThenFinalStillForcesTool },
                new HarnessTest { Name = "chat: invalid correction fails closed", Run = ChatInvalidToolCorrectionDoesNotFallbackToFinal },
                new HarnessTest { Name = "chat: repeated final for tool task fails closed", Run = ChatRepeatedFinalForRequiredToolFailsClosed },
                new HarnessTest { Name = "chat: editable follow-up prompt", Run = ChatUsesEditableAgentFollowUpPrompt },
                new HarnessTest { Name = "chat: failed tool retries corrected call", Run = ChatFailedToolRetriesCorrectedCall },
                new HarnessTest { Name = "chat: unknown tool retries exact available id", Run = ChatUnknownToolRetriesExactAvailableId },
                new HarnessTest { Name = "chat: retry success continues", Run = ChatRetrySuccessContinuesToFinalAnswer },
                new HarnessTest { Name = "chat: adapter exception requires successful retry", Run = ChatAdapterExceptionRequiresSuccessfulRetry },
                new HarnessTest { Name = "chat: inspection does not satisfy mutation", Run = ChatInspectionDoesNotSatisfyMutationRoute },
                new HarnessTest { Name = "chat: mutation asks for verification", Run = ChatMutationRequestsVerificationFollowUp },
                new HarnessTest { Name = "verification: sheet mutation uses lightweight read", Run = VerificationUsesLightweightSheetRead },
                new HarnessTest { Name = "verification: chart mutation reads exact chart", Run = VerificationUsesTargetedChartRead },
                new HarnessTest { Name = "verification: vba mutation reads and compares module", Run = VerificationUsesVbaModuleReadAndComparesCode },
                new HarnessTest { Name = "tools: excel chart update and delete", Run = ExcelChartToolsUpdateAndDeleteState },
                new HarnessTest { Name = "verification: hung read times out", Run = VerificationHungReadTimesOut },
                new HarnessTest { Name = "chat: unavailable verification fails closed", Run = ChatUnavailableVerificationFailsClosed },
                new HarnessTest { Name = "chat: failed verification recovers", Run = ChatFailedVerificationRecovers },
                new HarnessTest { Name = "chat: prior inspection does not verify mutation", Run = ChatPriorInspectionDoesNotVerifyMutation },
                new HarnessTest { Name = "chat: waiting tool gets pending id", Run = ChatWaitingToolGetsPendingId },
                new HarnessTest { Name = "chat: waiting tool stops run", Run = ChatWaitingToolStopsRun },
                new HarnessTest { Name = "chat: confirmed pending tool continues", Run = ChatConfirmedPendingToolContinuesAfterManualRun },
                new HarnessTest { Name = "chat: max iterations returns summary", Run = ChatMaxIterationsReturnsRuntimeSummary },
                new HarnessTest { Name = "chat: tool step limit stops run", Run = ChatToolStepLimitStopsRun },
                new HarnessTest { Name = "chat: planner batch allows bounded read-only actions", Run = PlannerBatchAllowsBoundedReadOnlyActions },
                new HarnessTest { Name = "chat: planner batch rejects excess read-only actions", Run = PlannerBatchRejectsExcessReadOnlyActions },
                new HarnessTest { Name = "chat: planner batch rejects multiple mutations and vba actions", Run = PlannerBatchRejectsMultipleMutationsAndVbaActions },
                new HarnessTest { Name = "chat: rejected mutation batch is replanned", Run = RejectedMutationBatchIsReplanned },
                new HarnessTest { Name = "chat: auto-run disabled records failure", Run = ChatAutoRunDisabledRecordsLocalFailure },
                new HarnessTest { Name = "chat: malformed planner response is repaired", Run = ChatMalformedPlannerResponseIsRepaired },
                new HarnessTest { Name = "chat: invalid planner records response diagnostics", Run = ChatInvalidPlannerRecordsResponseDiagnostics },
                new HarnessTest { Name = "chat: null completion records diagnostic", Run = ChatNullCompletionBecomesPlannerDiagnostic },
                new HarnessTest { Name = "chat: explicit clone preserves values", Run = ChatCloneServicePreservesValues },
                new HarnessTest { Name = "context: core normalizer", Run = ContextNormalizerUsesCoreModelsOnly },
                new HarnessTest { Name = "context: normalize and upsert", Run = ContextServiceNormalizesAndUpserts },
                new HarnessTest { Name = "context: trim helper", Run = ContextServiceTrimsText },
                new HarnessTest { Name = "chart: artifact default config", Run = ChartArtifactBuildsDefaultConfig },
                new HarnessTest { Name = "chart: artifact requested type truncates", Run = ChartArtifactHonorsRequestedTypeAndTruncates },
                new HarnessTest { Name = "bridge: init returns token", Run = BridgeInitReturnsToken },
                new HarnessTest { Name = "bridge: rejects missing token", Run = BridgeRejectsMissingToken },
                new HarnessTest { Name = "bridge: typed runTool payload", Run = BridgeUsesTypedRunToolPayload },
                new HarnessTest { Name = "bridge: typed sendChat progress", Run = BridgeUsesTypedSendChatPayloadAndProgress },
                new HarnessTest { Name = "bridge: typed chat mode payload", Run = BridgeUsesTypedChatModePayload },
                new HarnessTest { Name = "bridge: typed settings payload", Run = BridgeUsesTypedSettingsPayload },
                new HarnessTest { Name = "bridge: typed document activation", Run = BridgeUsesTypedDocumentPayload },
                new HarnessTest { Name = "bridge: typed tool and skill payloads", Run = BridgeUsesTypedToolAndSkillPayloads },
                new HarnessTest { Name = "bridge: typed context payload", Run = BridgeUsesTypedContextPayload },
                new HarnessTest { Name = "bridge: typed vba payload", Run = BridgeUsesTypedVbaPayload },
                new HarnessTest { Name = "bridge: typed html workspace delete payloads", Run = BridgeUsesTypedHtmlWorkspaceDeletePayloads }
            };

            foreach (var test in tests)
            {
                test.Category = CategoryFromName(test.Name);
            }
            var duplicates = tests
                .GroupBy(test => test.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicates.Length > 0)
            {
                Console.WriteLine("Duplicate test names: " + string.Join(", ", duplicates));
                return 2;
            }

            var arguments = (args ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "--")
                .ToArray();
            if (arguments.Any(value => string.Equals(value, "--list", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(value, "list", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var test in tests.OrderBy(test => test.Category).ThenBy(test => test.Name))
                {
                    Console.WriteLine(test.Category + "\t" + test.Name);
                }
                return 0;
            }

            var filter = arguments.Length == 0 ? string.Empty : string.Join(" ", arguments).Trim();
            var selected = string.IsNullOrWhiteSpace(filter)
                ? tests
                : tests.Where(test =>
                    test.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (selected.Count == 0)
            {
                Console.WriteLine("No tests matched: " + filter);
                return 2;
            }

            var failed = 0;
            foreach (var test in selected)
            {
                try
                {
                    test.ExecuteAsync().GetAwaiter().GetResult();
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failed += 1;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine((failed == 0 ? "OK" : "FAILED") + " passed=" + (selected.Count - failed) + " failed=" + failed + " total=" + selected.Count);
            return failed == 0 ? 0 : 1;
        }

        private static string CategoryFromName(string name)
        {
            var separator = (name ?? string.Empty).IndexOf(':');
            var prefix = separator <= 0 ? "other" : name.Substring(0, separator).Trim().ToLowerInvariant();
            if (prefix == "chat sessions")
            {
                return "storage";
            }
            if (prefix == "chat" || prefix.StartsWith("planner", StringComparison.Ordinal) || prefix == "prompt")
            {
                return "agent-loop";
            }
            if (prefix == "pipeline" || prefix == "tools" || prefix == "vba")
            {
                return "tools-safety";
            }
            if (prefix == "storage" || prefix == "attachments")
            {
                return "storage";
            }
            return prefix;
        }

        private static async Task HarnessRunsNativeAsync()
        {
            await Task.Yield();
            AssertTrue(true, "native async harness execution");
        }

    }
}
