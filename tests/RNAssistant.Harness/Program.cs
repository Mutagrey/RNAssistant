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
using RNAssistant.Office.Contracts;
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

        private static HarnessTest Test(string name, Action run)
        {
            return new HarnessTest { Name = name, Category = CategoryFromName(name), Run = run };
        }

        private static HarnessTest Test(string name, Func<Task> run)
        {
            return new HarnessTest { Name = name, Category = CategoryFromName(name), RunAsync = run };
        }

        public static int Main(string[] args)
        {
            var tests = new List<HarnessTest>
            {
                Test("harness: native async execution", HarnessRunsNativeAsync),
                Test("planner: strict json envelope", PlannerStrictParsesJsonEnvelope),
                Test("planner: accepts clean json fence and rejects prose", PlannerAcceptsCleanJsonFenceAndRejectsProse),
                Test("planner: rejects alternate envelopes", PlannerRejectsAlternateEnvelopes),
                Test("planner: rejects invalid intent and steps", PlannerRejectsInvalidIntentAndSteps),
                Test("planner: boundary corpus stays strict", PlannerBoundaryCorpusStaysStrict),
                Test("planner quality: requires tool rejects final", ModelQualityRequiresToolRejectsFinal),
                Test("modes: selects chat auto and agent", ModesSelectChatAutoAndAgent),
                Test("modes: missing session mode defaults to chat", MissingSessionModeDefaultsToChat),
                Test("modes: plain chat omits planner and activities", PlainChatOmitsPlannerAndActivities),
                Test("modes: plain chat repairs thought-only json", PlainChatRepairsThoughtOnlyJson),
                Test("modes: plain chat extracts answer without thought", PlainChatExtractsAnswerWithoutThought),
                Test("models: attachment routing is request scoped", AttachmentRoutingIsRequestScoped),
                Test("models: attachment routing covers pdf and mixed media", AttachmentRoutingCoversPdfAndMixedMedia),
                Test("context: deleted message absent from rebuilt prompt", DeletedMessageIsAbsentFromRebuiltContext),
                Test("routing: required empty tool slice stops before llm", RequiredEmptyToolSliceStopsBeforeLlm),
                Test("routing: tool slice balances mutation and inspection", ToolSliceBalancesMutationAndInspection),
                Test("routing: conversation history avoids Office tools", ConversationHistoryAvoidsOfficeTools),
                Test("routing: vba creation enters mutation phase", VbaCreationRouteAllowsMutation),
                Test("routing: destructive chart advances to delete capability", DestructiveChartRouteAdvancesToMutation),
                Test("routing: short follow-up continues pending agent task", ShortFollowUpContinuesPendingAgentTask),
                Test("routing: unknown and excluded tools have precise diagnostics", ToolValidationExplainsUnknownAndExcludedTools),
                Test("routing: optional tool authoring is explicit", OptionalToolAuthoringIsExplicitAndDoesNotCompleteDocumentTask),
                Test("context: prompt budget keeps contiguous recent history", PromptBudgetKeepsContiguousRecentHistory),
                Test("context: prompt budget compresses earlier history", PromptBudgetCompressesEarlierHistory),
                Test("context: output budget reserves prompt space", OutputBudgetReservesPromptSpace),
                Test("chat runs: registry isolates sessions", ChatRunRegistryIsolatesSessions),
                Test("html network: origin requires permission", HtmlNetworkOriginRequiresPermission),
                Test("models: explicit catalog url and standard data shape", ModelCatalogUsesExplicitUrlAndStandardDataShape),
                Test("desktop target: parses json descriptor", ParsesOfficeTargetJsonDescriptor),
                Test("desktop target: parses base64 descriptor", ParsesOfficeTargetBase64Descriptor),
                Test("desktop target: ignores utf8 bom", OfficeTargetIgnoresUtf8Bom),
                Test("desktop target: registry manual mode", TargetRegistryManualModeKeepsSelection),
                Test("desktop target: registry auto mode", TargetRegistryAutoModeCanSwitchSelection),
                Test("desktop com: dispatcher runs STA", OfficeStaDispatcherRunsSta),
                Test("desktop com: adapter dispatches calls", DispatchedAdapterDelegatesCalls),
                Test("documents: catalog activates selected document", DocumentCatalogActivatesSelectedDocument),
                Test("documents: recognizes web paths", DocumentOpenServiceRecognizesWebPaths),
                Test("documents: unsaved identity is stable", UnsavedDocumentIdentityUsesStoredId),
                Test("storage: chat roundtrip", CreatesAndListsChatsInTempRoot),
                Test("storage: broken chat skipped", SkipsBrokenChatFiles),
                Test("storage: deletes document chats", DeletesDocumentChats),
                Test("attachments: import commit delete", AttachmentImportCommitDelete),
                Test("attachments: multimodal api payload", AttachmentMultimodalApiPayload),
                Test("attachments: audio import and api payload", AttachmentAudioImportAndApiPayload),
                Test("llm: streaming SSE response", LlmStreamingResponseIsAggregated),
                Test("llm: separates reasoning metadata", LlmReasoningMetadataIsSeparated),
                Test("llm: chat mode forwards reasoning progress", PlainChatForwardsReasoningProgress),
                Test("llm: rejects alternate completion formats", LlmAlternateCompletionFormatsAreRejected),
                Test("llm: reports invalid response envelope", LlmInvalidResponseEnvelopeIsReported),
                Test("attachments: extracts pdf text", AttachmentExtractsPdfText),
                Test("attachments: accepts text formats and encodings", AttachmentAcceptsTextFormatsAndEncodings),
                Test("attachments: stores extracted text sidecar", AttachmentStoresExtractedTextSidecar),
                Test("attachments: visual pdf payload", AttachmentBuildsVisualPdfPayload),
                Test("attachments: rejects unsupported file", AttachmentRejectsUnsupportedFile),
                Test("attachments: cleans stale drafts", AttachmentCleansStaleDrafts),
                Test("chat sessions: document key migration", ChatSessionServiceMigratesDocumentKey),
                Test("chat sessions: stale requested id fallback", ChatSessionServiceFallsBackForStaleRequestedId),
                Test("chat sessions: addressed id loads archived chat", AddressedSessionLoadsExplicitChatAcrossDocuments),
                Test("chat sessions: addressed id does not fallback", AddressedSessionDoesNotFallbackToDifferentChat),
                Test("chat sessions: empty drafts are transient", EmptyChatDraftsAreNotPersisted),
                Test("chat sessions: background save keeps active chat", BackgroundSaveKeepsActiveChat),
                Test("chat sessions: interrupted run is cancelled", InterruptedRunIsRecoveredAsCancelled),
                Test("pipeline: dry-run resolves placeholders", PipelineDryRunResolvesPlaceholders),
                Test("pipeline: executes fake adapter steps", PipelineExecutesFakeAdapterSteps),
                Test("pipeline: resolves step output placeholders", PipelineResolvesStepOutputPlaceholders),
                Test("pipeline: stops after failed step", PipelineStopsAfterFailedStep),
                Test("pipeline: rejects missing step tool id", PipelineRejectsMissingStepToolId),
                Test("pipeline: rejects invalid definitions", PipelineRejectsInvalidDefinitions),
                Test("pipeline: rejects duplicate step ids", PipelineRejectsDuplicateStepIds),
                Test("pipeline: rejects cycles", PipelineRejectsCycles),
                Test("pipeline: resolves nested confirmation before execution", PipelineResolvesNestedConfirmationBeforeExecution),
                Test("pipeline: effective safety propagates nested risk", PipelineEffectiveSafetyPropagatesNestedRisk),
                Test("pipeline: custom tool needs confirmation", CustomPipelineNeedsConfirmation),
                Test("pipeline: agent mode gates built-in mutation", AgentModeGatesBuiltInMutation),
                Test("tools: catalog merges visible tools", ToolCatalogMergesVisibleTools),
                Test("tools: built-in ids cannot be shadowed", BuiltInToolIdsCannotBeShadowed),
                Test("tools: refreshed custom tool gets effective safety", RefreshedCustomToolGetsEffectiveSafety),
                Test("tools: store saves and updates custom tools", ToolStoreSavesAndUpdatesCustomTools),
                Test("tools: addressed store preserves extra files", ToolStorePreservesExtraFilesAndOtherTools),
                Test("tools: store skips broken custom tool files", ToolStoreSkipsBrokenCustomToolFiles),
                Test("tools: validates save and preserves metadata", ValidatesToolSaveAndPreservesMetadata),
                Test("tools: unknown and disabled tools fail", UnknownAndDisabledToolsFail),
                Test("tools: removed legacy ids are unknown", RemovedLegacyToolIdsAreUnknown),
                Test("tools: html workspace updates chat session", HtmlWorkspaceToolsUpdateChatSession),
                Test("tools: html workspace undo restores version", HtmlWorkspaceUndoRestoresPreviousVersion),
                Test("storage: html workspace persists with chat", HtmlWorkspacePersistsWithChatSession),
                Test("chat: agent creates html workspace", ChatAgentCreatesHtmlWorkspace),
                Test("chat: html mode forces workspace prompt", ChatHtmlModeForcesWorkspacePrompt),
                Test("chat: html workspace keeps generic follow-up route", ChatHtmlWorkspaceKeepsGenericFollowUpRoute),
                Test("chat: large malformed html planner response is rebuilt", ChatLargeMalformedHtmlPlannerResponseIsRebuilt),
                Test("chat: html delete requires read before mutation", ChatHtmlDeleteRequiresReadBeforeMutation),
                Test("tools: prompt templates save", PromptToolSavesAgentPromptTemplates),
                Test("tools: prompt defaults read", PromptToolReadsDefaults),
                Test("tools: validate custom tool payload", ToolValidateChecksPayloadWithoutSaving),
                Test("tools: expanded built-ins visible", ExpandedBuiltInToolsAreVisible),
                Test("prompt: tool metadata is weak-model friendly", PromptToolMetadataIsWeakModelFriendly),
                Test("tools: safety metadata gates mutations", ToolSafetyMetadataGatesMutations),
                Test("tools: pipeline effective mutation gates false metadata", CustomPipelineWithMutatingStepNeedsConfirmationWhenMetadataLies),
                Test("tools: confirmation matrix covers dry and manual runs", ConfirmationMatrixCoversDryAndManualRuns),
                Test("tools: agent can save custom tools with confirmation", AgentCanSaveCustomToolsWithConfirmation),
                Test("tools: agent validates and creates custom tool", AgentValidatesAndCreatesCustomTool),
                Test("tools: agent can author missing capability when enabled", AgentCanCreateAndUseToolDuringDocumentTaskWhenEnabled),
                Test("skills: store saves markdown skills", SkillStoreSavesMarkdownSkills),
                Test("skills: addressed store preserves extra files", SkillStorePreservesExtraFilesAndOtherSkills),
                Test("skills: store skips broken markdown skills", SkillStoreSkipsBrokenMarkdownSkills),
                Test("skills: catalog selects relevant skills", SkillCatalogSelectsRelevantSkills),
                Test("skills: prompt separates skills from tools", PromptSeparatesSkillsFromTools),
                Test("skills: prompt limits skill bodies", PromptLimitsSkillBodies),
                Test("prompt: editable agent blocks", PromptUsesEditableAgentPromptBlocks),
                Test("prompt: settings apply on next request", PromptSettingsApplyOnNextRequest),
                Test("skills: agent can save skills with confirmation", AgentCanSaveSkillsWithConfirmation),
                Test("vba: replace text backs up module", VbaReplaceTextBacksUpModule),
                Test("vba: apply patch targets named module", VbaApplyPatchTargetsNamedModule),
                Test("vba: backup failure blocks replacement", VbaBackupFailureBlocksReplacement),
                Test("vba: patch rejects line overrun", VbaPatchRejectsLineOverrun),
                Test("vba: custom macro failure is partial", VbaCustomMacroFailureIsPartial),
                Test("vba: failed module write restores code", VbaFailedModuleWriteRestoresCode),
                Test("vba: restore exposes deterministic verification", VbaRestoreExposesVerification),
                Test("verification: controller vba patch compares expected code", VerificationUsesControllerVbaExpectedCode),
                Test("vba: backup store skips broken files", VbaBackupStoreSkipsBrokenFiles),
                Test("prompt: usage estimator counts context", ContextUsageEstimatorCountsPromptAndSession),
                Test("chat: completion service records prose", ChatCompletionServiceRecordsProseResponse),
                Test("chat: planner includes recent history", ChatPlannerIncludesRecentHistory),
                Test("chat: includes vba context when enabled", ChatIncludesVbaContextWhenEnabled),
                Test("chat: vba tasks auto include vba context", ChatVbaTaskAutoIncludesVbaContext),
                Test("chat: deferred smart title setting", ChatCompletionServiceUsesDeferredSmartTitleSetting),
                Test("chat: localized draft title is auto", ChatTitleBuilderTreatsLocalizedDraftTitlesAsAuto),
                Test("chat: draft title is published before smart rename", ControllerPublishesDraftTitleBeforeSmartRename),
                Test("chat: editing middle turn rewinds history", EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace),
                Test("chat: editing latest turn avoids duplicate user", EditingLatestUserMessageDoesNotDuplicateUserTurn),
                Test("chat: editing validation errors are reported", EditingMessageValidationErrorsAreReported),
                Test("chat: editing clears pending runtime state", EditingMessageClearsPendingToolsWaitingActivitiesAndLastRun),
                Test("chat: executes typical host tasks", ChatExecutesTypicalHostTasks),
                Test("chat: built-in mutation follows safety metadata", ChatBuiltInMutationFollowsSafetyMetadata),
                Test("chat: general answer skips Office reads and tools", ChatGeneralAnswerSkipsOfficeReadsAndTools),
                Test("chat: routing avoids substring false positives", ChatRoutingAvoidsSubstringFalsePositives),
                Test("chat: current document question uses read tool", ChatCurrentDocumentQuestionUsesReadTool),
                Test("chat: prose greeting requires strict repair", ChatProseGreetingRequiresStrictRepair),
                Test("chat: stateful Excel scenario verifies result", ChatExcelStatefulScenarioVerifiesResult),
                Test("chat: scenario llm checks prompt contracts", ChatScenarioLlmChecksPromptContracts),
                Test("chat: agent activity transcript", AgentTranscriptCreatesActivityTree),
                Test("chat: prose action forces tool follow-up", ChatProseActionForcesToolFollowUp),
                Test("chat: malformed action response forces repair", ChatMalformedActionResponseForcesRepair),
                Test("chat: repair final still forces tool", ChatRepairThenFinalStillForcesTool),
                Test("chat: invalid correction fails closed", ChatInvalidToolCorrectionDoesNotFallbackToFinal),
                Test("chat: repeated final for tool task fails closed", ChatRepeatedFinalForRequiredToolFailsClosed),
                Test("chat: editable follow-up prompt", ChatUsesEditableAgentFollowUpPrompt),
                Test("chat: failed tool retries corrected call", ChatFailedToolRetriesCorrectedCall),
                Test("chat: unknown tool retries exact available id", ChatUnknownToolRetriesExactAvailableId),
                Test("chat: retry success continues", ChatRetrySuccessContinuesToFinalAnswer),
                Test("chat: adapter exception requires successful retry", ChatAdapterExceptionRequiresSuccessfulRetry),
                Test("chat: inspection does not satisfy mutation", ChatInspectionDoesNotSatisfyMutationRoute),
                Test("chat: mutation asks for verification", ChatMutationRequestsVerificationFollowUp),
                Test("verification: sheet mutation uses lightweight read", VerificationUsesLightweightSheetRead),
                Test("verification: chart mutation reads exact chart", VerificationUsesTargetedChartRead),
                Test("verification: vba mutation reads and compares module", VerificationUsesVbaModuleReadAndComparesCode),
                Test("tools: excel chart update and delete", ExcelChartToolsUpdateAndDeleteState),
                Test("verification: hung read times out", VerificationHungReadTimesOut),
                Test("chat: unavailable verification fails closed", ChatUnavailableVerificationFailsClosed),
                Test("chat: failed verification recovers", ChatFailedVerificationRecovers),
                Test("chat: prior inspection does not verify mutation", ChatPriorInspectionDoesNotVerifyMutation),
                Test("chat: waiting tool gets pending id", ChatWaitingToolGetsPendingId),
                Test("chat: waiting tool stops run", ChatWaitingToolStopsRun),
                Test("chat: confirmed pending tool continues", ChatConfirmedPendingToolContinuesAfterManualRun),
                Test("chat: max iterations returns summary", ChatMaxIterationsReturnsRuntimeSummary),
                Test("chat: tool step limit stops run", ChatToolStepLimitStopsRun),
                Test("chat: planner batch allows bounded read-only actions", PlannerBatchAllowsBoundedReadOnlyActions),
                Test("chat: planner batch rejects excess read-only actions", PlannerBatchRejectsExcessReadOnlyActions),
                Test("chat: planner batch rejects multiple mutations and vba actions", PlannerBatchRejectsMultipleMutationsAndVbaActions),
                Test("chat: rejected mutation batch is replanned", RejectedMutationBatchIsReplanned),
                Test("chat: auto-run disabled records failure", ChatAutoRunDisabledRecordsLocalFailure),
                Test("chat: malformed planner response is repaired", ChatMalformedPlannerResponseIsRepaired),
                Test("chat: invalid planner records response diagnostics", ChatInvalidPlannerRecordsResponseDiagnostics),
                Test("chat: null completion records diagnostic", ChatNullCompletionBecomesPlannerDiagnostic),
                Test("chat: explicit clone preserves values", ChatCloneServicePreservesValues),
                Test("context: core normalizer", ContextNormalizerUsesCoreModelsOnly),
                Test("context: normalize and upsert", ContextServiceNormalizesAndUpserts),
                Test("context: trim helper", ContextServiceTrimsText),
                Test("chart: artifact default config", ChartArtifactBuildsDefaultConfig),
                Test("chart: artifact requested type truncates", ChartArtifactHonorsRequestedTypeAndTruncates),
                Test("bridge: init returns token", BridgeInitReturnsToken),
                Test("bridge: rejects missing token", BridgeRejectsMissingToken),
                Test("bridge: typed runTool payload", BridgeUsesTypedRunToolPayload),
                Test("bridge: typed sendChat progress", BridgeUsesTypedSendChatPayloadAndProgress),
                Test("bridge: typed editMessage progress", BridgeUsesTypedEditMessagePayloadAndProgress),
                Test("bridge: typed chat mode payload", BridgeUsesTypedChatModePayload),
                Test("bridge: typed settings payload", BridgeUsesTypedSettingsPayload),
                Test("bridge: typed document activation", BridgeUsesTypedDocumentPayload),
                Test("bridge: typed tool and skill payloads", BridgeUsesTypedToolAndSkillPayloads),
                Test("bridge: typed context payload", BridgeUsesTypedContextPayload),
                Test("bridge: typed vba payload", BridgeUsesTypedVbaPayload),
                Test("bridge: typed html workspace delete payloads", BridgeUsesTypedHtmlWorkspaceDeletePayloads),
                Test("bridge: typed html network payloads", BridgeUsesTypedHtmlNetworkPayloads),
                Test("bridge: cancels addressed chat run", BridgeCancelsAddressedChatRun)
            };

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
