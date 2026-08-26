using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
                Test("agent: parses final JSON", SimpleAgentParsesFinalJson),
                Test("conversation: extracts streamed message JSON", ConversationStreamExtractorHandlesChunkedJson),
                Test("conversation: streams message and thinking", ConversationStreamsMessageAndThinking),
                Test("conversation: resets stream between attempts", ConversationStreamResetsBetweenAttempts),
                Test("agent: parses one tool call", SimpleAgentParsesToolCall),
                Test("agent: requires complete unique envelope", SimpleAgentRequiresCompleteUniqueEnvelope),
                Test("agent: parses multiple tool calls", SimpleAgentParsesMultipleToolCalls),
                Test("agent: rejects batched confirmation calls", SimpleAgentRejectsBatchedConfirmationCalls),
                Test("agent: tool call requires visible step message", SimpleAgentRejectsToolCallWithoutMessage),
                Test("agent: rejects duplicate tool call ids", SimpleAgentRejectsDuplicateToolCallIds),
                Test("agent: requires exact tool names", SimpleAgentRequiresExactToolNames),
                Test("agent: rejects tool call without id", SimpleAgentRejectsMissingToolCallId),
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
                Test("tools: strict schema validates metadata and constraints", StrictToolSchemaValidatesMetadataAndConstraints),
                Test("tools: controller catalog uses strict schemas", ControllerToolCatalogUsesStrictSchemas),
                Test("agent: executes tool and receives JSON result", SimpleAgentExecutesToolAndReceivesJsonResult),
                Test("agent: executes multiple tools sequentially", SimpleAgentExecutesMultipleToolsSequentially),
                Test("agent: prompt is request-local", SimpleAgentPromptIsRequestLocal),
                Test("agent: invalid response gets bounded format repair", SimpleAgentRepairsInvalidResponse),
                Test("agent: progress-only final gets semantic repair", SimpleAgentRepairsProgressOnlyFinal),
                Test("agent: failed format repair stays out of context", SimpleAgentFailedRepairDoesNotPolluteContext),
                Test("agent: format repair limit is clamped to twenty", SimpleAgentClampsFormatRepairLimit),
                Test("agent: exposes safe VBA editing tools", SimpleAgentExposesSafeVbaEditingTools),
                Test("agent: rejects hidden VBA backend calls", SimpleAgentRejectsHiddenVbaBackendCalls),
                Test("agent: confirmation replays one final result", SimpleAgentConfirmationReplaysOnlyFinalResult),
                Test("agent: confirmed tool failure continues", SimpleAgentConfirmationFailureContinues),
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
                Test("settings: hard cutover legacy Agent prompts", SettingsHardCutoverLegacyAgentPrompts),
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
                Test("storage: html checkpoints stay internal until mutation", HtmlWorkspaceCheckpointsStayInternalUntilMutation),
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
                Test("resources: gateway reads searches resolves and pages", ResourceGatewayReadsSearchesAndPages),
                Test("resources: live Office and VBA are bounded and guarded", LiveOfficeAndVbaResourcesAreBoundedAndGuarded),
                Test("artifacts: prompt uses bounded working set", ArtifactPromptUsesBoundedWorkingSet),
                Test("artifacts: historical attachments stay reference-only", HistoricalAttachmentsStayReferenceOnly),
                Test("attachments: visual pdf payload", AttachmentBuildsVisualPdfPayload),
                Test("attachments: rejects unsupported file", AttachmentRejectsUnsupportedFile),
                Test("attachments: cleans stale drafts", AttachmentCleansStaleDrafts),

                Test("pipeline: dry-run resolves placeholders", PipelineDryRunResolvesPlaceholders),
                Test("pipeline: executes fake adapter steps", PipelineExecutesFakeAdapterSteps),
                Test("pipeline: resolves step output placeholders", PipelineResolvesStepOutputPlaceholders),
                Test("pipeline: rejects unresolved placeholders", PipelineRejectsUnresolvedPlaceholders),
                Test("pipeline: stops after failed step", PipelineStopsAfterFailedStep),
                Test("pipeline: rejects missing step tool id", PipelineRejectsMissingStepToolId),
                Test("pipeline: rejects duplicate step ids", PipelineRejectsDuplicateStepIds),
                Test("pipeline: rejects invalid definitions", PipelineRejectsInvalidDefinitions),
                Test("pipeline: rejects cycles", PipelineRejectsCycles),
                Test("pipeline: resolves nested confirmation", PipelineResolvesNestedConfirmationBeforeExecution),
                Test("pipeline: effective safety propagates", PipelineEffectiveSafetyPropagatesNestedRisk),
                Test("pipeline: custom tool needs confirmation", CustomPipelineNeedsConfirmation),
                Test("pipeline: built-in mutation can run", AgentModeGatesBuiltInMutation),
                Test("pipeline: validates arguments and budget", PipelineExecutionValidatesArgumentsAndNestedBudget),
                Test("search: regex and capture replacement", TextPatternEngineSupportsRegexpAndGroups),

                Test("tools: catalog merges visible tools", ToolCatalogMergesVisibleTools),
                Test("tools: VBA facade is common across hosts", VbaFacadeIsCommonAcrossHosts),
                Test("tools: built-in ids cannot be shadowed", BuiltInToolIdsCannotBeShadowed),
                Test("tools: nested safety is effective", RefreshedCustomToolGetsEffectiveSafety),
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
                Test("tools: confirmation matrix", ConfirmationMatrixCoversDryAndManualRuns),
                Test("tools: html workspace updates session", HtmlWorkspaceToolsUpdateChatSession),
                Test("tools: html source read search and patch", HtmlWorkspaceSourceToolsAreBoundedAndAtomic),
                Test("tools: html workspace undo", HtmlWorkspaceUndoRestoresPreviousVersion),
                Test("tools: html workspace history is bounded", HtmlWorkspaceHistoryIsBoundedAndTransportIsCompact),
                Test("storage: html workspace persists", HtmlWorkspacePersistsWithChatSession),
                Test("tools: validate payload without saving", ToolValidateChecksPayloadWithoutSaving),
                Test("plans: CRUD creates revisions and rewinds", PlanCrudCreatesRevisionsAndRewindsCleanly),
                Test("plans: duplicate step ids rejected", PlanCrudRejectsAmbiguousSteps),

                Test("vba: apply patch backs up module", VbaApplyPatchBacksUpModule),
                Test("vba: confirmed mutation rejects stale snapshot", VbaConfirmedMutationRejectsStaleSnapshot),
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
                Test("vba: reconciliation waits for active mutation", VbaReconciliationWaitsForActiveMutation),
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
                Test("vba: document tools discovered", VbaDocumentToolsAreDiscoveredAndRunnable),
                Test("vba: code hash normalizes export", VbaCodeHashIgnoresExportHeadersAndRuntimeMarkers),

                Test("chat: editing middle rewinds artifacts", EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace),
                Test("chat: editing unavailable html fails closed", EditingWithUnavailableHtmlCheckpointFailsClosed),
                Test("chat: unchanged edit replays turn", ReplayingUnchangedUserMessageRewindsHistory),
                Test("chat: editing latest avoids duplicate", EditingLatestUserMessageDoesNotDuplicateUserTurn),
                Test("chat: editing legacy clears html", EditingLegacyTurnClearsUnversionedHtmlWorkspace),
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
                Test("resources: separates head and revision", ResourceContractsSeparateHeadAndRevision),
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
