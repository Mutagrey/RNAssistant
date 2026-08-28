using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Tools;

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
                if (RunAsync != null) return RunAsync();
                Run();
                return Task.CompletedTask;
            }
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
                Test("harness: production projects include all source files", ProductionProjectsIncludeAllSourceFiles),
                Test("harness: versioning ordinary builds and commits need no bump", VersioningOrdinaryBuildsNeedNoBump),
                Test("harness: versioning source archives build without Git", VersioningSourceArchivesBuildWithoutGit),
                Test("harness: versioning rejects malformed metadata", VersioningRejectsMalformedMetadata),
                Test("harness: versioning release gates are explicit", VersioningReleaseGatesAreExplicit),
                Test("harness: versioning tags cannot be reused", VersioningTagsCannotBeReused),
                Test("harness: versioning SDK and old-style assembly metadata", VersioningGeneratesAssemblyMetadata),
                Test("conversation: extracts streamed message JSON", ConversationStreamExtractorHandlesChunkedJson),
                Test("conversation: streams message and thinking", ConversationStreamsMessageAndThinking),
                Test("conversation: streams provider reasoning", ConversationStreamsProviderReasoning),
                Test("conversation: resets stream and thinking between repairs", ConversationStreamResetsBetweenAttempts),
                Test("tool result wire: terminal states", ToolResultWireRoundTripsTerminalStates),
                Test("tool result wire: literal payload strings", ToolResultWirePreservesLiteralPayloadStrings),
                Test("tool result wire: exact resources", ToolResultWirePreservesExactResources),
                Test("tool result wire: invalid envelope shapes", ToolResultWireRejectsInvalidEnvelopeShapes),
                Test("tool result wire: strict JSON and duplicates", ToolResultWireRejectsNonJsonAndDuplicateFields),
                Test("tool result wire: invalid resources", ToolResultWireRejectsInvalidResources),
                Test("tool result wire: invalid writer inputs", ToolResultWireRejectsInvalidWriterInputs),
                Test("tool result wire: no inferred runtime control", ToolResultWireDoesNotInferRuntimeControl),
                Test("tool result materialization: legacy terminal boundary", ToolRuntimeLegacyTerminalWire),
                Test("tool result materialization: pauses remain runtime controls", ToolRuntimeLegacyPausesStayRuntimeOnly),
                Test("tool result materialization: error data preserves literals", ToolRuntimeLegacyErrorDataRemainsLiteral),
                Test("tool result materialization: projection preserves execution", ToolRuntimeProjectionCannotChangeExecution),
                Test("tool result materialization: conversion failure preserves known outcome", ToolRuntimeConversionFailurePreservesKnownOutcome),
                Test("tool runtime: exact registry and binding identity", ToolRuntimeUsesExactRegistry),
                Test("tool runtime: invalid registration", ToolRuntimeRejectsInvalidRegistrations),
                Test("tool runtime: schema defaults preserve date strings", ToolRuntimePreservesDefaultsAndDateStrings),
                Test("tool runtime: schema gate before handler", ToolRuntimeValidatesArgumentsBeforeHandler),
                Test("tool runtime: mode and captured policy gate", ToolRuntimeEnforcesModeAndPolicySnapshot),
                Test("tool runtime: confirmation gates dispatch", ToolRuntimeGatesAndResumesConfirmation),
                Test("tool runtime: unavailable and automatic confirmation", ToolRuntimeHandlesUnavailableAndAutomaticConfirmation),
                Test("tool runtime: read results and required verification", ToolRuntimeNormalizesReadResults),
                Test("tool runtime: write effect evidence", ToolRuntimeSeparatesWriteEffects),
                Test("tool runtime: exceptions and missing results", ToolRuntimeClassifiesExceptionsAndMissingResults),
                Test("tool runtime: cancellation preserves evidence", ToolRuntimePreservesCancellationEvidence),
                Test("tool runtime: resources and awaiting user", ToolRuntimePreservesResourcesAndAwaitingUser),
                Test("tool runtime: contracts and evidence replay", ToolRuntimeContractsRoundTrip),
                Test("kernel: read ok", () => KernelAggregatesOutcome("read", ToolExecutionOutcome.Ok, ExecutionHealth.Clean, "1,0,0,0,0")),
                Test("kernel: read error", () => KernelAggregatesOutcome("read", ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,1,0,0,0")),
                Test("kernel: write ok", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Ok, ExecutionHealth.Clean, "0,0,1,0,0")),
                Test("kernel: write error", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,0,0,1,0")),
                Test("kernel: write unknown", () => KernelAggregatesOutcome("write", ToolExecutionOutcome.Unknown, ExecutionHealth.Unknown, "0,0,0,0,1")),
                Test("kernel: external unknown", () => KernelAggregatesOutcome("external", ToolExecutionOutcome.Unknown, ExecutionHealth.Unknown, "0,0,0,0,1")),
                Test("kernel: error then success", () => KernelPreservesCumulativeHealth(ToolExecutionOutcome.Error, ToolExecutionOutcome.Ok, ExecutionHealth.Errors, "0,0,1,1,0")),
                Test("kernel: success then error", () => KernelPreservesCumulativeHealth(ToolExecutionOutcome.Ok, ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,0,1,1,0")),
                Test("kernel: unknown then model says done", () => KernelPreservesCumulativeHealth(ToolExecutionOutcome.Unknown, ToolExecutionOutcome.Ok, ExecutionHealth.Unknown, "0,0,1,0,1")),
                Test("kernel: narrative cannot claim effects", KernelNarrativeCannotClaimEffects),
                Test("kernel: independent reads execute in order", KernelReadsAreSequentialAndBounded),
                Test("kernel: unsafe batches fail before dispatch", KernelRejectsUnsafeBatches),
                Test("kernel: runtime id collision within batch", () => KernelRejectsAllocationCollisions(false)),
                Test("kernel: runtime id collision across steps", () => KernelRejectsAllocationCollisions(true)),
                Test("kernel: repeated calls receive unique runtime ids", KernelAllocatesUniqueIdsForRepeatedCalls),
                Test("kernel: invalid allocator output fails before acceptance", KernelRejectsInvalidAllocatorOutput),
                Test("kernel: restored pending call retains its allocated id", KernelRestoredContinuationDoesNotAllocatePendingId),
                Test("kernel: requests are detached turn snapshots", KernelRequestsAreDetachedSnapshots),
                Test("kernel: typed model failures and native refusal", KernelClassifiesModelFailures),
                Test("kernel: cancellation at run start", () => KernelCancellationBeforeDispatch(AgentRunEventKind.Started)),
                Test("kernel: cancellation before model dispatch", () => KernelCancellationBeforeDispatch(AgentRunEventKind.ModelStepStarted)),
                Test("kernel: cancellation after accepted response", () => KernelCancellationBeforeDispatch(AgentRunEventKind.ResponseAccepted)),
                Test("kernel: cancellation before tool dispatch", () => KernelCancellationBeforeDispatch(AgentRunEventKind.ToolStarted)),
                Test("kernel: cancellation during policy recheck", () => KernelCancellationBeforeDispatch(AgentRunEventKind.ToolStarted, true)),
                Test("kernel: late cancelled response is not accepted", KernelIgnoresLateCancelledResponse),
                Test("kernel: cancellation after possible write dispatch", () => KernelCancellationAfterPossibleDispatch(true)),
                Test("kernel: cancellation after read entry", () => KernelCancellationAfterPossibleDispatch(false)),
                Test("kernel: cancellation preserves terminal evidence", KernelCancellationPreservesTerminalEvidence),
                Test("kernel: cancelled batch closes all calls", KernelCancelledBatchClosesAllCalls),
                Test("kernel: iteration limit", () => KernelHonorsLimits(true)),
                Test("kernel: tool step limit", () => KernelHonorsLimits(false)),
                Test("kernel: confirmation preserves prior error", () => KernelConfirmationSharesAccounting(ToolExecutionOutcome.Error, ToolExecutionOutcome.Ok, ExecutionHealth.Errors, "0,0,1,1,0")),
                Test("kernel: confirmation preserves prior unknown", () => KernelConfirmationSharesAccounting(ToolExecutionOutcome.Unknown, ToolExecutionOutcome.Ok, ExecutionHealth.Unknown, "0,0,1,0,1")),
                Test("kernel: confirmation records error", () => KernelConfirmationSharesAccounting(ToolExecutionOutcome.Ok, ToolExecutionOutcome.Error, ExecutionHealth.Errors, "0,0,1,1,0")),
                Test("kernel: confirmation records unknown", () => KernelConfirmationSharesAccounting(ToolExecutionOutcome.Ok, ToolExecutionOutcome.Unknown, ExecutionHealth.Unknown, "0,0,1,0,1")),
                Test("kernel: runtime id collision after confirmation", KernelRejectsAllocationCollisionAfterConfirmation),
                Test("kernel: stale confirmation cannot dispatch twice", KernelRejectsStaleConfirmation),
                Test("kernel: cancelled confirmation closes pending call", KernelCancelsPendingWithoutDanglingCall),
                Test("kernel: policy change stops accepted call", () => KernelPolicyChangeStopsDispatch(false)),
                Test("kernel: policy change stops confirmation", () => KernelPolicyChangeStopsDispatch(true)),
                Test("kernel: store failure stops before dispatch", KernelStoreFailureStopsBeforeDispatch),
                Test("kernel: store failure preserves unpersisted evidence", KernelStoreFailurePreservesUnpersistedEvidence),
                Test("kernel: missing execution evidence remains unknown", KernelRejectsMissingExecutionEvidence),
                Test("kernel: local interaction ends the invocation", KernelLocalInteractionEndsWithoutExtraModelStep),
                Test("agent: prompt contains tools and skill catalog", SimpleAgentPromptContainsToolsAndSkills),
                Test("agent: hydrates artifact media only after read", AgentHydratesArtifactMediaOnlyAfterRead),
                Test("agent: routes artifact media through helper", AgentRoutesArtifactMediaThroughHelper),
                Test("agent: closed document keeps local tools", AgentContinuesWithLocalToolsForClosedDocument),
                Test("agent: loads full skill through tool", SimpleAgentLoadsFullSkillThroughTool),
                Test("agent: prompt skips invalid tool schemas", SimpleAgentPromptSkipsInvalidToolSchema),
                Test("agent: default prompts are structured Markdown", DefaultPromptsAreStructuredMarkdown),
                Test("agent: supports selectable response formats", AgentSupportsSelectableResponseFormats),
                Test("agent: json schema mirrors tool contracts", AgentJsonSchemaMirrorsToolContracts),
                Test("agent: json schema supports type-named arguments", AgentJsonSchemaSupportsTypeNamedArguments),
                Test("agent: supports selectable tool result roles", AgentSupportsSelectableToolResultRoles),
                Test("agent: json schema fallback is request-local", AgentJsonSchemaFallbackIsRequestLocal),
                Test("conversation v4: status-free round trip", ConversationV4RoundTripsWithoutStatus),
                Test("conversation v4: reads all accepted history forms", ConversationHistoryReadsAcceptedForms),
                Test("conversation v4: rejects ambiguous history records", ConversationHistoryRejectsAmbiguousRecords),
                Test("conversation v4: rejects unknown root fields", ConversationV4RejectsUnknownRootFields),
                Test("conversation v4: rejects malformed JSON and extensions", ConversationV4RejectsMalformedJson),
                Test("conversation v4: requires exact call shape", ConversationV4RequiresExactCallShape),
                Test("conversation v4: requires callable authority", ConversationV4RequiresCallableAuthority),
                Test("conversation v4: wire has no call identity", ConversationV4KeepsIdenticalCallsWithoutIds),
                Test("conversation v4: batches only explicit read-only calls", ConversationV4BatchesOnlyExplicitReadOnlyCalls),
                Test("conversation v4: validates arguments before acceptance", ConversationV4ValidatesArgumentsBeforeAcceptance),
                Test("conversation v4: bounds call count", ConversationV4BoundsCallCount),
                Test("conversation v4: schema agrees with parser and transport", ConversationV4SchemaMatchesParserAndWire),
                Test("conversation v4: schema exposes only callable tools", ConversationV4SchemaAllowsOnlyCallableTools),
                Test("model protocol: repairs from accepted prompt", ModelProtocolRepairsFromAcceptedPrompt),
                Test("model protocol: preflight rejects incomplete context", ModelProtocolPreflightRejectsIncompleteContext),
                Test("model protocol: v4 rejects model ids and validates singleton safety", ModelProtocolValidatesV4Context),
                Test("protocol preflight: incompatible history stops preparation", ProtocolPreflightStopsIncompatibleHistory),
                Test("protocol preflight: incomplete confirmation stops preparation", ProtocolPreflightStopsIncompleteConfirmation),
                Test("kernel replay: native resource read evidence", KernelNativeResourceReadEvidenceReplays),
                Test("kernel replay: normal effect and own trace CAS", () => KernelSummaryReplaysOutcome("ok")),
                Test("kernel replay: write error", () => KernelSummaryReplaysOutcome("error")),
                Test("kernel replay: unknown then model done", () => KernelSummaryReplaysOutcome("unknown")),
                Test("kernel replay: confirmation and stale cursor", KernelSummaryReplaysConfirmation),
                Test("kernel replay: cancelled confirmation", KernelConfirmationCancellationReplays),
                Test("kernel replay: known effect survives model preparation failure", KernelSummaryRetainsEffectAfterPreparationFailure),
                Test("kernel replay: known unprojected effect survives recovery", KernelRecoveryPreservesKnownUnprojectedEffect),
                Test("kernel replay: failed append prevents dispatch", () => KernelStoreFailureStopsAndRecovers(false)),
                Test("kernel replay: interrupted write remains unknown", () => KernelStoreFailureStopsAndRecovers(true)),
                Test("protocol context: immutable complete snapshots", ProtocolContextSnapshotsAreIndependent),
                Test("protocol context: reconstructs full accepted turn", ProtocolContextSeedsFullAcceptedTurn),
                Test("protocol context: incomplete confirmation fails closed", ProtocolContextRejectsIncompleteContinuation),
                Test("protocol context: batch safety uses local authority", ProtocolContextBatchSafetyUsesLocalAuthority),
                Test("protocol context: loop tracks only accepted calls", ProtocolContextBoundaryTracksOnlyAcceptedCalls),
                Test("protocol context: loop restores confirmation scope", ProtocolContextBoundaryRestoresConfirmation),
                Test("model protocol: typed exhaustion excludes rejected payloads", ModelProtocolReturnsTypedExhaustion),
                Test("model protocol: provider failures do not use protocol retry", ModelProtocolSeparatesProviderFailures),
                Test("model protocol: cancellation stops raw attempts", ModelProtocolCancellationStopsAttempts),
                Test("model protocol: fallback stays within one run", ModelProtocolFallbackStaysWithinRun),
                Test("model protocol: fallback is explicit and bounded", ModelProtocolFallbackIsBounded),
                Test("model protocol: fallback works during format repair", ModelProtocolFallbackDuringRepair),
                Test("model protocol: provider recovery preserves protocol slots", ModelProtocolProviderRecoveryKeepsProtocolSlots),
                Test("model protocol: provider budget spans the whole step", ModelProtocolProviderBudgetSpansWholeStep),
                Test("model protocol: combined retry budgets are bounded", ModelProtocolCombinedBudgetsAreBounded),
                Test("model protocol: cancellation during backoff", ModelProtocolCancellationDuringBackoff),
                Test("model protocol: refusal and trace failure policy", ModelProtocolPreservesRefusalAndTracePolicy),
                Test("model protocol: oversized prompt stops before dispatch", ModelProtocolStopsBeforeOversizedRequest),
                Test("tools: strict schema validates metadata and constraints", StrictToolSchemaValidatesMetadataAndConstraints),
                Test("tools: controller catalog uses strict schemas", ControllerToolCatalogUsesStrictSchemas),
                Test("tools: discovery is complete and exact", ToolDiscoveryIsCompleteAndLoadsExactSchema),
                Test("agent: progressive tools require exact read", ProgressiveAgentRequiresExactToolRead),
                Test("agent: tool working set evicts and replays", ProgressiveToolWorkingSetEvictsAndReplaysDeterministically),
                Test("agent: model session rebuilds authority after compaction", ConversationModelSessionRebuildsAuthorityAfterCompaction),
                Test("agent: characterization write ok preserves final status", SimpleAgentExecutesToolAndReceivesJsonResult),
                Test("agent: characterization completed after write error", SimpleAgentCharacterizesCompletedAfterWriteError),
                Test("agent: characterization completed after write unknown", SimpleAgentCharacterizesCompletedAfterWriteUnknown),
                Test("agent: characterization completed without write", SimpleAgentCharacterizesCompletedWithoutWrite),
                Test("completion guard: actual outcomes and metadata", RunSummaryUsesActualOutcomesAndMetadata),
                Test("completion guard: conservative legacy mapping", RunSummaryMapsLegacyUncertaintyConservatively),
                Test("completion guard: cancellation preserves unknown", RunSummarySurvivesCancellationAfterUnknown),
                Test("completion guard: confirmation preserves errors", () => SimpleAgentConfirmationPreservesExecutionHealth("errors")),
                Test("completion guard: confirmation preserves unknown", () => SimpleAgentConfirmationPreservesExecutionHealth("unknown")),
                Test("causal trace: successful mutation", () => CausalTraceCorrelatesMutation("ok", 0)),
                Test("causal trace: failed mutation", () => CausalTraceCorrelatesMutation("error", 0)),
                Test("causal trace: twentieth response and unknown mutation", () => CausalTraceCorrelatesMutation("unknown", 19)),
                Test("causal trace: async scopes are isolated", CausalTraceScopesAreIsolated),
                Test("causal trace: confirmation preserves correlation", CausalTraceConfirmationKeepsTurnAndJournalOrigin),
                Test("causal trace: optional failure preserves execution", CausalTraceFailureDoesNotChangeExecution),
                Test("agent: unsafe write batch repairs to singleton calls", SimpleAgentExecutesMultipleToolsSequentially),
                Test("agent: prompt is request-local", SimpleAgentPromptIsRequestLocal),
                Test("agent: invalid response gets bounded format repair", SimpleAgentRepairsInvalidResponse),
                Test("agent: characterization repair succeeds on attempt twenty", SimpleAgentRepairsOnTwentiethAttempt),
                Test("agent: status-free response controls loop", SimpleAgentUsesStatusFreeResponse),
                Test("agent: characterization twenty invalid responses stay out of history", SimpleAgentFailedRepairDoesNotPolluteContext),
                Test("agent: characterization limit includes initial response", SimpleAgentClampsFormatRepairLimit),
                Test("agent: exposes safe VBA editing tools", SimpleAgentExposesSafeVbaEditingTools),
                Test("agent: loads and runs arbitrary macro", SimpleAgentLoadsAndRunsArbitraryMacro),
                Test("agent: confirmation replays one final result", SimpleAgentConfirmationReplaysOnlyFinalResult),
                Test("agent: confirmed tool failure continues", SimpleAgentConfirmationFailureContinues),
                Test("agent: native read batch keeps paired replay", NativeReadBatchKeepsPairedReplay),
                Test("agent: runtime ids preserve complete HTML in user history", () => RuntimeIdsPreserveCompleteHtml("user")),
                Test("agent: runtime ids preserve complete HTML in native history", () => RuntimeIdsPreserveCompleteHtml("tool")),
                Test("agent: provider refusal is terminal", ModelRefusalIsTerminalInAgentAndChat),
                Test("agent: bounds oversized tool result data", AgentToolResultDataIsBounded),
                Test("agent: keeps tool result within prompt budget", AgentToolResultFitsRemainingPromptBudget),
                Test("model compatibility: accepts exact sentinels", ModelCompatibilityAcceptsExactSentinels),
                Test("model compatibility: rejects loose responses", ModelCompatibilityRejectsLooseResponses),
                Test("model diagnostics: connection probe reports timings", ModelConnectionProbeReportsTimings),
                Test("model diagnostics: tracker lifecycle", ModelDiagnosticsTrackerReportsOneTerminalLifecycle),
                Test("model diagnostics: stream reports first chunk", ModelDiagnosticsStreamReportsFirstChunk),
                Test("chat: uses only read-only resource loop", ChatUsesReadOnlyResourceLoop),
                Test("chat: rereads referenced artifact on demand", ChatRereadsReferencedArtifactOnDemand),
                Test("chat: session model overrides global settings", ChatSettingsUseSessionModelWithoutMutatingGlobalSettings),
                Test("chat: prompt save preserves global model", PromptSavePreservesGlobalModel),
                Test("settings: prompt schema requires explicit review", SettingsRequireExplicitPromptReview),
                Test("settings: built-in guidance uses runtime IDs and result v1", BuiltInPromptGuidanceUsesRuntimeIdsAndToolResultV1),
                Test("settings: prompt review preserves stored text", SettingsPromptReviewPreservesStoredText),
                Test("settings: prompt review gates conversation dispatch", SettingsPromptReviewGatesConversationDispatch),
                Test("settings: invalid numeric values are normalized", SettingsNormalizeInvalidNumericValues),
                Test("context: compaction uses one summary field", SimpleCompactionUsesOneSummaryField),
                Test("context: compaction preserves tool protocol pairs", CompactionPreservesToolProtocolPairs),
                Test("context inspector: builds agent snapshot", PromptContextInspectorBuildsAgentSnapshot),
                Test("context inspector: raw JSON is opt-in", PromptContextInspectorRawJsonIsOptIn),
                Test("context inspector: concurrent settings are isolated", PromptContextInspectorIsolatesConcurrentSettings),
                Test("token estimate: manual multiplier", TokenEstimateMultiplierAppliesToPromptParts),
                Test("token estimate: learns from API usage", TokenEstimateCalibrationLearnsFromApiUsage),
                Test("token estimate: learns linear overhead", TokenEstimateCalibrationLearnsLinearOverhead),
                Test("token estimate: calibration can be disabled", TokenEstimateCalibrationCanBeDisabled),
                Test("token estimate: actual usage is authoritative", TokenEstimateUsesActualApiUsage),

                Test("desktop target: parses json descriptor", ParsesOfficeTargetJsonDescriptor),
                Test("desktop target: parses base64 descriptor", ParsesOfficeTargetBase64Descriptor),
                Test("desktop target: ignores utf8 bom", OfficeTargetIgnoresUtf8Bom),
                Test("desktop target: registry manual mode", TargetRegistryManualModeKeepsSelection),
                Test("desktop target: registry auto mode", TargetRegistryAutoModeCanSwitchSelection),
                Test("desktop com: dispatcher runs STA", OfficeStaDispatcherRunsSta),
                Test("desktop com: adapter dispatches calls", DispatchedAdapterDelegatesCalls),
                Test("host runtime: queued mutation cancellation releases access", HostRuntimeCancelsQueuedMutationAndReleasesAccess),
                Test("host runtime: nested reads reuse access and failures release", HostRuntimeReusesNestedReadAccessAndReleasesOnFailure),
                Test("host runtime: bound session lifetime and identity", HostRuntimeBoundSessionPreservesTargetAcrossSaveAsAndRejectsReopen),
                Test("host runtime: bound owner STA busy and nested read", HostRuntimeBoundOwnerStaBusyAndNestedReads),
                Test("host runtime: bound operation isolation", HostRuntimeBoundOperationDoesNotLeakAccess),
                Test("host runtime: bound queued STA cancellation skips action", HostRuntimeBoundQueuedCancellationSkipsActionAndReleasesGate),
                Test("host runtime: gate order and failed acquisition cleanup", HostRuntimeGateOrderAndFailedAcquisitionCleanup),
                Test("documents: catalog activates selected document", DocumentCatalogActivatesSelectedDocument),
                Test("documents: recognizes web paths", DocumentOpenServiceRecognizesWebPaths),
                Test("documents: unsaved identity uses runtime key", UnsavedDocumentIdentityUsesRuntimeKey),
                Test("documents: saved identity uses full path or legacy id", SavedDocumentIdentityUsesFullPathOrLegacyId),

                Test("storage: chat roundtrip", CreatesAndListsChatsInTempRoot),
                Test("storage: json save remains atomic", JsonFileStoreWritesAtomicUtf8),
                Test("storage: jsonl byte offsets are exact", JsonlByteOffsetsAreExact),
                Test("storage: bounded cache honors LRU and weights", BoundedCacheHonorsLruAndWeights),
                Test("storage: lock pair releases both resources", DisposablePairReleasesBothResources),
                Test("storage: projection cache trusts only owned appends", ProjectionCacheTrustsOnlyOwnedAppends),
                Test("storage: cache rejects tampered prefix before suffix", CacheRejectsTamperedPrefixBeforeSuffix),
                Test("storage: streaming queue is ordered", StreamingTraceQueueIsOrdered),
                Test("storage: streaming queue drains before terminal", StreamingTraceQueueDrainsBeforeTerminal),
                Test("storage: event log is canonical", SessionEventLogIsCanonical),
                Test("storage: natural list changes omit reorder", NaturalListChangesOmitReorder),
                Test("storage: headers use artifact metadata", ChatHeadersUseArtifactMetadata),
                Test("storage: header cache trusts only owned appends", HeaderCacheTrustsOnlyOwnedAppends),
                Test("storage: headers report JSONL and CAS usage", ChatHeadersReportStorageUsage),
                Test("storage: trajectory query paginates and filters", TrajectoryQueryPaginatesAndFilters),
                Test("storage: trajectory derived views retain sources and usage", TrajectoryDerivedViewsRetainSourcesAndUsage),
                Test("storage: trajectory export redacts and verifies bundle", TrajectoryExportRedactsAndVerifiesBundle),
                Test("storage: fork lineage is canonical", SessionForkLineageIsCanonical),
                Test("storage: stale chat save is rejected", StaleChatRevisionIsRejected),
                Test("storage: event integrity rejects tampering", SessionEventIntegrityRejectsTampering),
                Test("storage: pre-cutover formats are reset-only", PreCutoverSessionFormatsAreResetOnly),
                Test("storage: event HMAC requires matching key", SessionEventHmacRequiresMatchingKey),
                Test("storage: encrypted history protects events and CAS", EncryptedHistoryProtectsEventsAndCas),
                Test("storage: protection handles block boundaries", StorageProtectionHandlesBlockBoundaries),
                Test("storage: CAS GC collects blob-before-event orphan", CasGcCollectsBlobBeforeEventOrphan),
                Test("storage: CAS health reports missing and corrupt blobs", CasHealthReportsMissingAndCorruptBlobs),
                Test("storage: CAS GC fails closed for invalid sources", CasGcFailsClosedForInvalidSources),
                Test("storage: CAS health scans protected streams", CasHealthScansProtectedStreams),
                Test("storage: CAS maintenance always uses gate", CasMaintenanceAlwaysUsesGate),
                Test("storage: CAS rejects reparse traversal", CasMaintenanceRejectsReparsePointTraversal),
                Test("storage: managed roots reject reparse points", ManagedStorageRejectsReparseRoots),
                Test("storage: deletes document event logs", DeletesDocumentEventLogs),
                Test("storage: delete preserves shared CAS", DeletesDocumentChats),
                Test("storage: artifact bodies use CAS", ArtifactBodiesUseContentAddressing),
                Test("storage: unchanged artifacts skip CAS externalization", UnchangedArtifactsSkipCasExternalization),
                Test("storage: CAS compression is transparent", CasCompressionIsTransparent),
                Test("storage: file CAS streams hash compression and encryption", FileCasStreamsHashCompressionAndEncryption),
                Test("storage: plaintext CAS accepts envelope prefix", PlaintextCasAcceptsEnvelopePrefix),
                Test("storage: model trace shares event stream", ModelTraceSharesSessionStream),
                Test("storage: turn lifecycle is first-class", TurnLifecycleIsFirstClass),
                Test("storage: interrupted step gets synthetic end", InterruptedStepGetsSyntheticEnd),
                Test("storage: streaming frames use exact chunks", StreamingFramesAreBufferedAsExactChunks),
                Test("storage: model request trace precedes dispatch", ModelRequestTracePrecedesDispatch),
                Test("storage: incomplete event tail recovers", IncompleteEventTailRecovers),
                Test("storage: unterminated valid tail recovers", UnterminatedValidEventTailRecovers),
                Test("storage: terminated corrupt tails are rejected", TerminatedCorruptTailsAreRejected),
                Test("storage: corrupted artifact blob is safe", CorruptedArtifactBlobIsSafe),
                Test("storage: chart activity projects from artifact", ChartActivityProjectsFromArtifact),
                Test("storage: compaction projects from artifact", CompactionProjectsFromArtifact),
                Test("storage: html navigation projects from artifacts", HtmlNavigationProjectsFromArtifacts),
                Test("storage: html redo branches are explicit and lazy", HtmlRedoBranchesAreExplicitAndLazy),
                Test("storage: html recovery blocks mutation and selects healthy revision", HtmlRecoveryBlocksMutationAndSelectsHealthyRevision),
                Test("storage: html recovery keeps readable active with broken parent", HtmlRecoveryKeepsReadableActiveWithBrokenParent),
                Test("storage: html messages use canonical resource refs", HtmlWorkspaceMessagesUseCanonicalResourceReferences),
                Test("chat sessions: document key migration", ChatSessionServiceMigratesDocumentKey),
                Test("chat sessions: stale requested id fallback", ChatSessionServiceFallsBackForStaleRequestedId),
                Test("chat sessions: addressed id loads archived chat", AddressedSessionLoadsExplicitChatAcrossDocuments),
                Test("chat sessions: addressed transient survives document switch", AddressedTransientSessionSurvivesDocumentSwitch),
                Test("chat sessions: addressed id does not fallback", AddressedSessionDoesNotFallbackToDifferentChat),
                Test("chat sessions: empty drafts are transient", EmptyChatDraftsAreNotPersisted),
                Test("chat sessions: background save keeps active chat", BackgroundSaveKeepsActiveChat),
                Test("chat sessions: active persisted state refreshes", LoadingActiveChatRefreshesPersistedState),
                Test("chat sessions: follows Office document switches", ChatSessionServiceFollowsOfficeDocumentSwitches),
                Test("chat sessions: unsaved documents stay isolated", UnsavedDocumentChatsStayIsolated),
                Test("chat sessions: legacy chat rebinds by full path", LegacyChatRebindsByFullPath),
                Test("chat sessions: interrupted run is marked unknown", InterruptedRunIsRecoveredAsUnknown),
                Test("chat sessions: saved run boundary preserves protocol", InterruptedRunAtSavedBoundaryPreservesProtocol),

                Test("attachments: import commit delete", AttachmentImportCommitDelete),
                Test("attachments: resource link precedes model dispatch", AttachmentPromotionLinksResourceBeforeModelDispatch),
                Test("attachments: fork artifact reuses shared blob", ForkedAttachmentArtifactTracksCopiedFile),
                Test("attachments: multimodal api payload", AttachmentMultimodalApiPayload),
                Test("attachments: image import bypasses pdf extraction", AttachmentImageImportBypassesPdfExtraction),
                Test("attachments: media routing is request scoped", AttachmentRoutingIsRequestScoped),
                Test("attachments: routes scanned pdf and mixed media", AttachmentRoutingCoversPdfAndMixedMedia),
                Test("attachments: helper isolates media from primary context", AttachmentAnalysisIsolatesMedia),
                Test("attachments: helper limits are configurable", AttachmentAnalysisLimitsAreConfigurable),
                Test("attachments: multimodal primary bypasses helper", MultimodalPrimaryBypassesHelper),
                Test("attachments: audio import and api payload", AttachmentAudioImportAndApiPayload),
                Test("attachments: extracts pdf text", AttachmentExtractsPdfText),
                Test("attachments: accepts text formats and encodings", AttachmentAcceptsTextFormatsAndEncodings),
                Test("attachments: stores extracted text sidecar", AttachmentStoresExtractedTextSidecar),
                Test("tool runtime: native resource list manual and model paths", NativeResourceListUsesRuntimeForManualAndModelCalls),
                Test("resources: gateway reads searches resolves and pages", ResourceGatewayReadsSearchesAndPages),
                Test("resources: live Office and VBA are bounded and guarded", LiveOfficeAndVbaResourcesAreBoundedAndGuarded),
                Test("artifacts: prompt uses bounded working set", ArtifactPromptUsesBoundedWorkingSet),
                Test("artifacts: historical attachments stay reference-only", HistoricalAttachmentsStayReferenceOnly),
                Test("attachments: visual pdf payload", AttachmentBuildsVisualPdfPayload),
                Test("attachments: rejects unsupported file", AttachmentRejectsUnsupportedFile),
                Test("attachments: cleans stale drafts", AttachmentCleansStaleDrafts),

                Test("search: regex and capture replacement", TextPatternEngineSupportsRegexpAndGroups),

                Test("pipeline: disabled for direct and manual execution", PipelinesCannotExecute),
                Test("pipeline: omitted from storage and discovery", PipelinesAreNotLoadedOrAdvertised),
                Test("pipeline: authoring is unavailable", PipelinesCannotBeAuthored),
                Test("tools: catalog merges visible tools", ToolCatalogMergesVisibleTools),
                Test("tools: VBA facade is common across hosts", VbaFacadeIsCommonAcrossHosts),
                Test("tools: built-in ids cannot be shadowed", BuiltInToolIdsCannotBeShadowed),
                Test("tools: VBA safety is effective", RefreshedCustomToolGetsEffectiveSafety),
                Test("tools: store saves and updates", ToolStoreSavesAndUpdatesCustomTools),
                Test("tools: store preserves extra files", ToolStorePreservesExtraFilesAndOtherTools),
                Test("tools: store skips broken files", ToolStoreSkipsBrokenCustomToolFiles),
                Test("tools: validates save metadata", ValidatesToolSaveAndPreservesMetadata),
                Test("tools: agent CRUD preserves omitted fields", AgentToolCrudPreservesOmittedFields),
                Test("skills: CRUD preserves omitted fields", AgentSkillCrudPreservesOmittedFields),
                Test("skills: revision and validation are deterministic", SkillRevisionAndValidationAreDeterministic),
                Test("skills: references are revisioned and paged", SkillReferencesAreRevisionedAndPaged),
                Test("skills: ids do not collide and disabled reads fail", SkillIdsDoNotCollideAndDisabledReadsFail),
                Test("tools: unknown and disabled fail", UnknownAndDisabledToolsFail),
                Test("tools: removed ids are unknown", RemovedToolIdsAreUnknown),
                Test("tools: compact catalog rejects removed aliases", CompactToolCatalogRejectsRemovedAliases),
                Test("tools: expanded built-ins visible", ExpandedBuiltInToolsAreVisible),
                Test("tools: safety metadata gates mutations", ToolSafetyMetadataGatesMutations),
                Test("tools: manual read-only run skips chat lease", ManualReadOnlyRunSkipsChatLease),
                Test("tools: html workspace updates session", HtmlWorkspaceToolsUpdateChatSession),
                Test("tools: html source read search and patch", HtmlWorkspaceSourceToolsAreBoundedAndAtomic),
                Test("tools: html workspace undo", HtmlWorkspaceUndoRestoresPreviousVersion),
                Test("tools: html workspace history is bounded", HtmlWorkspaceHistoryIsBoundedAndTransportIsCompact),
                Test("storage: html workspace persists", HtmlWorkspacePersistsWithChatSession),
                Test("tools: validate payload without saving", ToolValidateChecksPayloadWithoutSaving),
                Test("task lists: CRUD creates revisions and closes", TaskListCrudCreatesRevisionsAndClosesCleanly),
                Test("task lists: duplicate step ids rejected", TaskListCrudRejectsAmbiguousSteps),
                Test("plan mode: filters mutations and keeps planning tools", PlanModeFiltersMutationsAndKeepsPlanningTools),
                Test("plan mode: plan document revisions and questions", PlanModePersistsMarkdownAndAwaitsAnswers),

                Test("vba: apply patch backs up module", VbaApplyPatchBacksUpModule),
                Test("vba: confirmed mutation rejects stale snapshot", VbaConfirmedMutationRejectsStaleSnapshot),
                Test("vba: queued guard reads wait for mutation", VbaQueuedGuardReadsWaitForMutation),
                Test("vba: create rejects confirmation race", VbaCreateRejectsConfirmationRace),
                Test("vba: write upserts and normalizes name", VbaWriteUpsertsAndNormalizesName),
                Test("vba: write rename is strict and atomic", VbaWriteRenameIsStrictAndAtomic),
                Test("vba: rename rejects confirmation races", VbaRenameRejectsConfirmationRace),
                Test("vba: delete reads internally", VbaDeleteNeedsNoPublicRead),
                Test("vba: guard resolves stable and changed identities", VbaGuardHandlesStableAndChangedDocumentIdentities),
                Test("vba: apply patch targets module", VbaApplyPatchTargetsNamedModule),
                Test("vba: exact patch preserves complete lines", VbaExactPatchPreservesCompleteLines),
                Test("vba: invalid state blocks write", VbaInvalidStateBlocksWrite),
                Test("vba: patch rejects addressing modes", VbaPatchRejectsAddressingModes),
                Test("vba: patch rejects stale exact source", VbaPatchRejectsStaleExactSource),
                Test("vba: live hash preserves line structure", VbaLiveHashPreservesLineStructure),
                Test("vba: VBE normalization is accepted", VbaReadBackAcceptsVbeNormalization),
                Test("vba: COM write accepts VBE line metadata", VbaProjectWriteAcceptsVbeNormalization),
                Test("vba: COM rename preserves component identity", VbaProjectRenamePreservesComponentIdentity),
                Test("vba: backend compare-and-swap rejects drift", VbaBackendCompareAndSwapRejectsDrift),
                Test("vba: UserForm create and code edit", VbaUserFormCreateAndCodeEdit),
                Test("vba: code-only UserForm authoring skill", VbaCodeOnlyUserFormSkillIsExplicit),
                Test("vba: resources read bounded source", VbaResourcesReadBoundedSource),
                Test("vba: patch rejects ambiguous exact source", VbaPatchRejectsAmbiguousExactSource),
                Test("vba: exact patch preserves boundary newlines", VbaExactPatchPreservesBoundaryNewlines),
                Test("vba: write rejects hidden controls", VbaWriteRejectsHiddenControlCharacters),
                Test("vba: regex patch and safe delete", VbaSearchRegexpPatchAndDeleteAreSafe),
                Test("vba: custom macro failure cleans session", VbaCustomMacroFailureCleansSession),
                Test("vba: failed write restores code", VbaFailedModuleWriteRestoresCode),
                Test("vba: read-back rejects write drift", VbaReadBackRejectsWriteDrift),
                Test("vba: read-back rejects delete drift", VbaReadBackRejectsDeleteDrift),
                Test("vba: restore applies backup", VbaRestoreAppliesBackup),
                Test("vba: restore pins backup before confirmation", VbaRestorePinsBackupBeforeConfirmation),
                Test("vba: journal recovers tail and rejects corruption", VbaJournalRecoversTailAndRejectsCorruption),
                Test("vba: journal records mutation correlation", VbaJournalRecordsMutationAndCorrelation),
                Test("vba: mutation diagnostics paginate and hydrate diffs", VbaMutationDiagnosticsPaginateAndHydrateDiffs),
                Test("vba: journal reconciles interrupted mutations", VbaJournalReconcilesInterruptedMutations),
                Test("vba: reconciliation waits for active mutation", () => VbaReconciliationWaitsForActiveMutation("vba resource")),
                Test("vba: document resource waits for active mutation", () => VbaReconciliationWaitsForActiveMutation("document resource")),
                Test("vba: editor module waits for active mutation", () => VbaReconciliationWaitsForActiveMutation("editor module")),
                Test("vba: editor project waits for active mutation", () => VbaReconciliationWaitsForActiveMutation("editor project")),
                Test("vba: manual read waits for active mutation", () => VbaReconciliationWaitsForActiveMutation("manual read")),
                Test("vba: journal uses history protection", VbaJournalUsesHistoryProtection),
                Test("vba: manifest validates entry point", VbaToolManifestValidatesTypedEntryPoint),
                Test("vba: package rejects duplicate sources", VbaToolPackageRejectsDuplicateSources),
                Test("vba: internal ids are reserved", VbaToolPackageReservesInternalCommandIds),
                Test("vba: package sources roundtrip", VbaToolStoreRoundTripsPackageSources),
                Test("vba: session execution cleans package", VbaToolSessionExecutionUsesTypedArgumentsAndCleansUp),
                Test("vba: persistent install tracks ownership", VbaToolPersistentInstallRequiresMacroDocumentAndTracksOwnership),
                Test("vba: package journal is atomic", VbaPackageJournalRecordsAtomicTransactions),
                Test("vba: package journal reconciles interruption", VbaPackageJournalReconcilesInterruptedTransaction),
                Test("vba: code-only UserForm package roundtrip", VbaCodeOnlyUserFormPackageRoundTrips),
                Test("vba: COM code-only UserForm package lifecycle", VbaCodeOnlyUserFormPackageComLifecycle),
                Test("vba: package accepts VBE normalization", VbaPackageAcceptsVbeNormalization),
                Test("vba: COM package accepts VBE normalization", VbaComPackageAcceptsVbeNormalization),
                Test("vba: document tools discovered", VbaDocumentToolsAreDiscoveredAndRunnable),
                Test("vba: code hash normalizes export", VbaCodeHashIgnoresExportHeadersAndRuntimeMarkers),

                Test("chat: editing middle rewinds artifacts", EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace),
                Test("chat: editing unavailable html fails closed", EditingWithUnavailableHtmlCheckpointFailsClosed),
                Test("chat: unchanged edit replays turn", ReplayingUnchangedUserMessageRewindsHistory),
                Test("chat: editing latest avoids duplicate", EditingLatestUserMessageDoesNotDuplicateUserTurn),
                Test("chat: editing without checkpoint clears unversioned html", EditingTurnWithoutCheckpointClearsUnversionedHtmlWorkspace),
                Test("chat: editing errors reported", EditingMessageValidationErrorsAreReported),
                Test("chat: run lease serializes", ChatRunLeaseSerializesHistoryMutations),
                Test("chat: duplicate confirmation is rejected", ConfirmedToolRunLeaseRejectsDuplicateAndSupportsCancellation),
                Test("chat: tool deletion is exchange-scoped", ToolExchangeDeletionIsScoped),

                Test("context: clone preserves values", ChatCloneServicePreservesValues),
                Test("context: core normalizer", ContextNormalizerUsesCoreModelsOnly),
                Test("context: normalize and upsert", ContextServiceNormalizesAndUpserts),
                Test("context: trim helper", ContextServiceTrimsText),
                Test("resources: canonical URI roundtrip", ResourceUriRoundTripsCanonicalAddress),
                Test("resources: rejects ambiguous URI", ResourceUriRejectsAmbiguousAddresses),
                Test("resources: reference pins revision", ResourceReferencePinsRevision),
                Test("resources: registry rejects duplicate providers", ResourceRegistryRejectsDuplicateProviders),
                Test("resources: gateway discovers providers", ResourceGatewayDiscoversProvidersBeforeListing),
                Test("resources: hard cutover artifact tools", ResourceToolsHardCutoverArtifactTools),
                Test("chart: default config", ChartArtifactBuildsDefaultConfig),
                Test("chart: requested type truncates", ChartArtifactHonorsRequestedTypeAndTruncates),

                Test("bridge: init returns token", BridgeInitReturnsToken),
                Test("webview: restricts messages and navigation", WebViewSecurityRestrictsMessagesAndNavigation),
                Test("bridge: rejects missing token", BridgeRejectsMissingToken),
                Test("bridge: typed runTool", BridgeUsesTypedRunToolPayload),
                Test("bridge: typed sendChat", BridgeUsesTypedSendChatPayloadAndProgress),
                Test("bridge: typed resource ingestion", BridgeUsesTypedResourceIngestionPayloads),
                Test("bridge: typed editMessage", BridgeUsesTypedEditMessagePayloadAndProgress),
                Test("bridge: compact addressed context", BridgeCompactsAddressedChatContext),
                Test("bridge: confirm ids", BridgeConfirmProgressCarriesChatAndRunIds),
                Test("bridge: typed chat mode", BridgeUsesTypedChatModePayload),
                Test("bridge: typed chat reasoning", BridgeUsesTypedChatReasoningPayload),
                Test("bridge: typed settings", BridgeUsesTypedSettingsPayload),
                Test("bridge: model diagnostics", BridgeReportsModelConnectionDiagnostics),
                Test("bridge: typed trajectory query", BridgeUsesTypedTrajectoryQuery),
                Test("bridge: CAS maintenance", BridgeReportsCasMaintenance),
                Test("bridge: typed document", BridgeUsesTypedDocumentPayload),
                Test("bridge: typed tools and skills", BridgeUsesTypedToolAndSkillPayloads),
                Test("bridge: typed context", BridgeUsesTypedContextPayload),
                Test("bridge: typed prompt context inspector", BridgeUsesTypedPromptContextInspectorPayload),
                Test("bridge: typed vba", BridgeUsesTypedVbaPayload),
                Test("bridge: typed html delete", BridgeUsesTypedHtmlWorkspaceDeletePayloads),
                Test("bridge: typed html network", BridgeUsesTypedHtmlNetworkPayloads),
                Test("bridge: cancels addressed run", BridgeCancelsAddressedChatRun)
            };

            var duplicates = tests.GroupBy(test => test.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicates.Length > 0)
            {
                Console.WriteLine("Duplicate test names: " + string.Join(", ", duplicates));
                return 2;
            }

            var arguments = (args ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value) && value != "--").ToArray();
            if (arguments.Any(value => string.Equals(value, "--list", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(value, "list", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var test in tests.OrderBy(test => test.Category).ThenBy(test => test.Name))
                    Console.WriteLine(test.Category + "\t" + test.Name);
                return 0;
            }

            var filter = arguments.Length == 0 ? string.Empty : string.Join(" ", arguments).Trim();
            var selected = string.IsNullOrWhiteSpace(filter)
                ? tests
                : tests.Where(test => test.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
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
            Console.WriteLine((failed == 0 ? "OK" : "FAILED") + " passed=" + (selected.Count - failed) +
                " failed=" + failed + " total=" + selected.Count);
            return failed == 0 ? 0 : 1;
        }

        private static string CategoryFromName(string name)
        {
            var separator = (name ?? string.Empty).IndexOf(':');
            return separator <= 0 ? "other" : name.Substring(0, separator).Trim().ToLowerInvariant();
        }

        private static async Task HarnessRunsNativeAsync()
        {
            await Task.Yield();
            AssertTrue(true, "native async harness execution");
        }
    }
}
