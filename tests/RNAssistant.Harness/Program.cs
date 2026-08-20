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
                Test("agent: parses one tool call", SimpleAgentParsesToolCall),
                Test("agent: rejects tool call without id", SimpleAgentRejectsMissingToolCallId),
                Test("agent: prompt contains all tools and skills", SimpleAgentPromptContainsToolsAndSkills),
                Test("agent: prompt skips invalid tool schemas", SimpleAgentPromptSkipsInvalidToolSchema),
                Test("agent: executes tool and receives JSON result", SimpleAgentExecutesToolAndReceivesJsonResult),
                Test("agent: invalid response fails without repair state", SimpleAgentInvalidResponseFailsDirectly),
                Test("agent: confirmation replays one final result", SimpleAgentConfirmationReplaysOnlyFinalResult),
                Test("chat: plain mode has no agent context", SimpleChatHasNoAgentContext),
                Test("context: compaction uses one summary field", SimpleCompactionUsesOneSummaryField),

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
                Test("chat sessions: document key migration", ChatSessionServiceMigratesDocumentKey),
                Test("chat sessions: stale requested id fallback", ChatSessionServiceFallsBackForStaleRequestedId),
                Test("chat sessions: addressed id loads archived chat", AddressedSessionLoadsExplicitChatAcrossDocuments),
                Test("chat sessions: addressed id does not fallback", AddressedSessionDoesNotFallbackToDifferentChat),
                Test("chat sessions: empty drafts are transient", EmptyChatDraftsAreNotPersisted),
                Test("chat sessions: background save keeps active chat", BackgroundSaveKeepsActiveChat),
                Test("chat sessions: interrupted run is cancelled", InterruptedRunIsRecoveredAsCancelled),

                Test("attachments: import commit delete", AttachmentImportCommitDelete),
                Test("attachments: fork artifact tracks copied file", ForkedAttachmentArtifactTracksCopiedFile),
                Test("attachments: multimodal api payload", AttachmentMultimodalApiPayload),
                Test("attachments: audio import and api payload", AttachmentAudioImportAndApiPayload),
                Test("attachments: extracts pdf text", AttachmentExtractsPdfText),
                Test("attachments: accepts text formats and encodings", AttachmentAcceptsTextFormatsAndEncodings),
                Test("attachments: stores extracted text sidecar", AttachmentStoresExtractedTextSidecar),
                Test("attachments: visual pdf payload", AttachmentBuildsVisualPdfPayload),
                Test("attachments: rejects unsupported file", AttachmentRejectsUnsupportedFile),
                Test("attachments: cleans stale drafts", AttachmentCleansStaleDrafts),

                Test("pipeline: dry-run resolves placeholders", PipelineDryRunResolvesPlaceholders),
                Test("pipeline: executes fake adapter steps", PipelineExecutesFakeAdapterSteps),
                Test("pipeline: resolves step output placeholders", PipelineResolvesStepOutputPlaceholders),
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
                Test("search: regexp and capture replacement", TextPatternEngineSupportsRegexpAndGroups),

                Test("tools: catalog merges visible tools", ToolCatalogMergesVisibleTools),
                Test("tools: built-in ids cannot be shadowed", BuiltInToolIdsCannotBeShadowed),
                Test("tools: nested safety is effective", RefreshedCustomToolGetsEffectiveSafety),
                Test("tools: store saves and updates", ToolStoreSavesAndUpdatesCustomTools),
                Test("tools: store preserves extra files", ToolStorePreservesExtraFilesAndOtherTools),
                Test("tools: store skips broken files", ToolStoreSkipsBrokenCustomToolFiles),
                Test("tools: validates save metadata", ValidatesToolSaveAndPreservesMetadata),
                Test("tools: unknown and disabled fail", UnknownAndDisabledToolsFail),
                Test("tools: removed ids are unknown", RemovedToolIdsAreUnknown),
                Test("tools: expanded built-ins visible", ExpandedBuiltInToolsAreVisible),
                Test("tools: safety metadata gates mutations", ToolSafetyMetadataGatesMutations),
                Test("tools: confirmation matrix", ConfirmationMatrixCoversDryAndManualRuns),
                Test("tools: html workspace updates session", HtmlWorkspaceToolsUpdateChatSession),
                Test("tools: html workspace undo", HtmlWorkspaceUndoRestoresPreviousVersion),
                Test("storage: html workspace persists", HtmlWorkspacePersistsWithChatSession),
                Test("tools: validate payload without saving", ToolValidateChecksPayloadWithoutSaving),

                Test("vba: replace text backs up module", VbaReplaceTextBacksUpModule),
                Test("vba: apply patch targets module", VbaApplyPatchTargetsNamedModule),
                Test("vba: backup failure blocks replacement", VbaBackupFailureBlocksReplacement),
                Test("vba: patch rejects line overrun", VbaPatchRejectsLineOverrun),
                Test("vba: regexp patch and safe delete", VbaSearchRegexpPatchAndDeleteAreSafe),
                Test("vba: custom macro failure cleans session", VbaCustomMacroFailureCleansSession),
                Test("vba: failed write restores code", VbaFailedModuleWriteRestoresCode),
                Test("vba: restore applies backup", VbaRestoreAppliesBackup),
                Test("vba: backup store skips broken files", VbaBackupStoreSkipsBrokenFiles),
                Test("vba: manifest validates entry point", VbaToolManifestValidatesTypedEntryPoint),
                Test("vba: package rejects duplicate sources", VbaToolPackageRejectsDuplicateSources),
                Test("vba: internal ids are reserved", VbaToolPackageReservesInternalCommandIds),
                Test("vba: package sources roundtrip", VbaToolStoreRoundTripsPackageSources),
                Test("vba: session execution cleans package", VbaToolSessionExecutionUsesTypedArgumentsAndCleansUp),
                Test("vba: persistent install tracks ownership", VbaToolPersistentInstallRequiresMacroDocumentAndTracksOwnership),
                Test("vba: document tools discovered", VbaDocumentToolsAreDiscoveredAndRunnable),
                Test("vba: code hash normalizes export", VbaCodeHashIgnoresExportHeadersAndRuntimeMarkers),

                Test("chat: editing middle rewinds artifacts", EditingMiddleUserMessageRewindsHistoryAndClearsHtmlWorkspace),
                Test("chat: editing latest avoids duplicate", EditingLatestUserMessageDoesNotDuplicateUserTurn),
                Test("chat: editing legacy clears html", EditingLegacyTurnClearsUnversionedHtmlWorkspace),
                Test("chat: editing errors reported", EditingMessageValidationErrorsAreReported),
                Test("chat: run lease serializes", ChatRunLeaseSerializesHistoryMutations),
                Test("chat: duplicate confirmation is rejected", ConfirmedToolRunLeaseRejectsDuplicateAndSupportsCancellation),

                Test("context: clone preserves values", ChatCloneServicePreservesValues),
                Test("context: core normalizer", ContextNormalizerUsesCoreModelsOnly),
                Test("context: normalize and upsert", ContextServiceNormalizesAndUpserts),
                Test("context: trim helper", ContextServiceTrimsText),
                Test("chart: default config", ChartArtifactBuildsDefaultConfig),
                Test("chart: requested type truncates", ChartArtifactHonorsRequestedTypeAndTruncates),

                Test("bridge: init returns token", BridgeInitReturnsToken),
                Test("bridge: rejects missing token", BridgeRejectsMissingToken),
                Test("bridge: typed runTool", BridgeUsesTypedRunToolPayload),
                Test("bridge: typed sendChat", BridgeUsesTypedSendChatPayloadAndProgress),
                Test("bridge: typed editMessage", BridgeUsesTypedEditMessagePayloadAndProgress),
                Test("bridge: compact addressed context", BridgeCompactsAddressedChatContext),
                Test("bridge: confirm ids", BridgeConfirmProgressCarriesChatAndRunIds),
                Test("bridge: typed chat mode", BridgeUsesTypedChatModePayload),
                Test("bridge: typed chat reasoning", BridgeUsesTypedChatReasoningPayload),
                Test("bridge: typed settings", BridgeUsesTypedSettingsPayload),
                Test("bridge: typed document", BridgeUsesTypedDocumentPayload),
                Test("bridge: typed tools and skills", BridgeUsesTypedToolAndSkillPayloads),
                Test("bridge: typed context", BridgeUsesTypedContextPayload),
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
