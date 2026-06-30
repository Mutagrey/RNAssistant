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
            public Action Run { get; set; }
        }

        private sealed class HostTaskScenario
        {
            public string Host { get; set; }
            public string UserText { get; set; }
            public string Response { get; set; }
            public string[] ExpectedTools { get; set; }
        }

        public static int Main(string[] args)
        {
            var tests = new List<HarnessTest>
            {
                new HarnessTest { Name = "parser: fenced agent steps", Run = ParsesFencedAgentSteps },
                new HarnessTest { Name = "parser: bare json array", Run = ParsesBareJsonArray },
                new HarnessTest { Name = "parser: native tool_calls", Run = ParsesNativeToolCalls },
                new HarnessTest { Name = "parser: normalizes primitive and complex args", Run = ParserNormalizesPrimitiveAndComplexArgs },
                new HarnessTest { Name = "parser: noisy embedded json", Run = ParsesNoisyEmbeddedJson },
                new HarnessTest { Name = "parser: bad json skipped", Run = SkipsBadJson },
                new HarnessTest { Name = "parser: recovers malformed agent json", Run = RecoversMalformedAgentJson },
                new HarnessTest { Name = "planner: strict json envelope", Run = PlannerStrictParsesJsonEnvelope },
                new HarnessTest { Name = "planner: strict rejects markdown and prose", Run = PlannerStrictRejectsMarkdownAndProse },
                new HarnessTest { Name = "planner: compatibility unwraps single fence", Run = PlannerCompatibilityUnwrapsSingleFence },
                new HarnessTest { Name = "planner: rejects invalid intent and steps", Run = PlannerRejectsInvalidIntentAndSteps },
                new HarnessTest { Name = "planner quality: requires tool rejects final", Run = ModelQualityRequiresToolRejectsFinal },
                new HarnessTest { Name = "desktop target: parses json descriptor", Run = ParsesOfficeTargetJsonDescriptor },
                new HarnessTest { Name = "desktop target: parses base64 descriptor", Run = ParsesOfficeTargetBase64Descriptor },
                new HarnessTest { Name = "desktop target: ignores utf8 bom", Run = OfficeTargetIgnoresUtf8Bom },
                new HarnessTest { Name = "desktop target: registry manual mode", Run = TargetRegistryManualModeKeepsSelection },
                new HarnessTest { Name = "desktop target: registry auto mode", Run = TargetRegistryAutoModeCanSwitchSelection },
                new HarnessTest { Name = "desktop com: dispatcher runs STA", Run = OfficeStaDispatcherRunsSta },
                new HarnessTest { Name = "desktop com: adapter dispatches calls", Run = DispatchedAdapterDelegatesCalls },
                new HarnessTest { Name = "documents: catalog activates selected document", Run = DocumentCatalogActivatesSelectedDocument },
                new HarnessTest { Name = "storage: chat roundtrip", Run = CreatesAndListsChatsInTempRoot },
                new HarnessTest { Name = "storage: broken chat skipped", Run = SkipsBrokenChatFiles },
                new HarnessTest { Name = "attachments: import commit delete", Run = AttachmentImportCommitDelete },
                new HarnessTest { Name = "attachments: multimodal api payload", Run = AttachmentMultimodalApiPayload },
                new HarnessTest { Name = "llm: streaming SSE response", Run = LlmStreamingResponseIsAggregated },
                new HarnessTest { Name = "attachments: extracts pdf text", Run = AttachmentExtractsPdfText },
                new HarnessTest { Name = "attachments: rejects unsupported file", Run = AttachmentRejectsUnsupportedFile },
                new HarnessTest { Name = "attachments: cleans stale drafts", Run = AttachmentCleansStaleDrafts },
                new HarnessTest { Name = "chat sessions: document key migration", Run = ChatSessionServiceMigratesDocumentKey },
                new HarnessTest { Name = "chat sessions: legacy document key migration", Run = ChatSessionServiceMigratesLegacyDocumentKey },
                new HarnessTest { Name = "chat sessions: stale requested id fallback", Run = ChatSessionServiceFallsBackForStaleRequestedId },
                new HarnessTest { Name = "pipeline: dry-run resolves placeholders", Run = PipelineDryRunResolvesPlaceholders },
                new HarnessTest { Name = "pipeline: executes fake adapter steps", Run = PipelineExecutesFakeAdapterSteps },
                new HarnessTest { Name = "pipeline: resolves step output placeholders", Run = PipelineResolvesStepOutputPlaceholders },
                new HarnessTest { Name = "pipeline: stops after failed step", Run = PipelineStopsAfterFailedStep },
                new HarnessTest { Name = "pipeline: rejects missing step tool id", Run = PipelineRejectsMissingStepToolId },
                new HarnessTest { Name = "pipeline: rejects invalid definitions", Run = PipelineRejectsInvalidDefinitions },
                new HarnessTest { Name = "pipeline: enforces nesting limit", Run = PipelineEnforcesNestingLimit },
                new HarnessTest { Name = "pipeline: custom tool needs confirmation", Run = CustomPipelineNeedsConfirmation },
                new HarnessTest { Name = "pipeline: agent mode gates built-in mutation", Run = AgentModeGatesBuiltInMutation },
                new HarnessTest { Name = "tools: catalog merges visible tools", Run = ToolCatalogMergesVisibleTools },
                new HarnessTest { Name = "tools: store saves and updates custom tools", Run = ToolStoreSavesAndUpdatesCustomTools },
                new HarnessTest { Name = "tools: store skips broken custom tool files", Run = ToolStoreSkipsBrokenCustomToolFiles },
                new HarnessTest { Name = "tools: validates save and preserves metadata", Run = ValidatesToolSaveAndPreservesMetadata },
                new HarnessTest { Name = "tools: unknown and disabled tools fail", Run = UnknownAndDisabledToolsFail },
                new HarnessTest { Name = "tools: html artifact always available", Run = HtmlArtifactToolIsAlwaysAvailable },
                new HarnessTest { Name = "tools: html workspace updates chat session", Run = HtmlWorkspaceToolsUpdateChatSession },
                new HarnessTest { Name = "tools: html workspace undo restores version", Run = HtmlWorkspaceUndoRestoresPreviousVersion },
                new HarnessTest { Name = "storage: html workspace persists with chat", Run = HtmlWorkspacePersistsWithChatSession },
                new HarnessTest { Name = "chat: agent creates html workspace", Run = ChatAgentCreatesHtmlWorkspace },
                new HarnessTest { Name = "chat: html mode forces workspace prompt", Run = ChatHtmlModeForcesWorkspacePrompt },
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
                new HarnessTest { Name = "skills: store saves markdown skills", Run = SkillStoreSavesMarkdownSkills },
                new HarnessTest { Name = "skills: store skips broken markdown skills", Run = SkillStoreSkipsBrokenMarkdownSkills },
                new HarnessTest { Name = "skills: catalog selects relevant skills", Run = SkillCatalogSelectsRelevantSkills },
                new HarnessTest { Name = "skills: prompt separates skills from tools", Run = PromptSeparatesSkillsFromTools },
                new HarnessTest { Name = "skills: prompt limits skill bodies", Run = PromptLimitsSkillBodies },
                new HarnessTest { Name = "prompt: editable agent blocks", Run = PromptUsesEditableAgentPromptBlocks },
                new HarnessTest { Name = "skills: agent can save skills with confirmation", Run = AgentCanSaveSkillsWithConfirmation },
                new HarnessTest { Name = "vba: replace text backs up module", Run = VbaReplaceTextBacksUpModule },
                new HarnessTest { Name = "vba: apply patch targets named module", Run = VbaApplyPatchTargetsNamedModule },
                new HarnessTest { Name = "vba: backup store skips broken files", Run = VbaBackupStoreSkipsBrokenFiles },
                new HarnessTest { Name = "prompt: trims oldest history", Run = PromptBuilderTrimsOldestHistory },
                new HarnessTest { Name = "prompt: usage estimator counts context", Run = ContextUsageEstimatorCountsPromptAndSession },
                new HarnessTest { Name = "chat: completion service records prose", Run = ChatCompletionServiceRecordsProseResponse },
                new HarnessTest { Name = "chat: includes vba context when enabled", Run = ChatIncludesVbaContextWhenEnabled },
                new HarnessTest { Name = "chat: vba tasks auto include vba context", Run = ChatVbaTaskAutoIncludesVbaContext },
                new HarnessTest { Name = "chat: deferred smart title setting", Run = ChatCompletionServiceUsesDeferredSmartTitleSetting },
                new HarnessTest { Name = "chat: executes typical host tasks", Run = ChatExecutesTypicalHostTasks },
                new HarnessTest { Name = "chat: general answer skips Office reads and tools", Run = ChatGeneralAnswerSkipsOfficeReadsAndTools },
                new HarnessTest { Name = "chat: stateful Excel scenario verifies result", Run = ChatExcelStatefulScenarioVerifiesResult },
                new HarnessTest { Name = "chat: scenario llm checks prompt contracts", Run = ChatScenarioLlmChecksPromptContracts },
                new HarnessTest { Name = "chat: agent activity transcript", Run = AgentTranscriptCreatesActivityTree },
                new HarnessTest { Name = "chat: prose action forces tool follow-up", Run = ChatProseActionForcesToolFollowUp },
                new HarnessTest { Name = "chat: malformed action response forces repair", Run = ChatMalformedActionResponseForcesRepair },
                new HarnessTest { Name = "chat: editable follow-up prompt", Run = ChatUsesEditableAgentFollowUpPrompt },
                new HarnessTest { Name = "chat: failed tool retries corrected call", Run = ChatFailedToolRetriesCorrectedCall },
                new HarnessTest { Name = "chat: unknown tool retries exact available id", Run = ChatUnknownToolRetriesExactAvailableId },
                new HarnessTest { Name = "chat: retry success continues", Run = ChatRetrySuccessContinuesToFinalAnswer },
                new HarnessTest { Name = "chat: mutation asks for verification", Run = ChatMutationRequestsVerificationFollowUp },
                new HarnessTest { Name = "chat: waiting tool gets pending id", Run = ChatWaitingToolGetsPendingId },
                new HarnessTest { Name = "chat: waiting tool stops batch", Run = ChatWaitingToolStopsBatch },
                new HarnessTest { Name = "chat: confirmed pending tool continues", Run = ChatConfirmedPendingToolContinuesAfterManualRun },
                new HarnessTest { Name = "chat: max iterations returns summary", Run = ChatMaxIterationsReturnsRuntimeSummary },
                new HarnessTest { Name = "chat: tool step limit stops run", Run = ChatToolStepLimitStopsRun },
                new HarnessTest { Name = "chat: auto-run disabled records failure", Run = ChatAutoRunDisabledRecordsLocalFailure },
                new HarnessTest { Name = "chat: malformed planner response is repaired", Run = ChatMalformedPlannerResponseIsRepaired },
                new HarnessTest { Name = "chat: invalid planner records response diagnostics", Run = ChatInvalidPlannerRecordsResponseDiagnostics },
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
                new HarnessTest { Name = "bridge: typed settings payload", Run = BridgeUsesTypedSettingsPayload },
                new HarnessTest { Name = "bridge: typed document activation", Run = BridgeUsesTypedDocumentPayload },
                new HarnessTest { Name = "bridge: typed tool and skill payloads", Run = BridgeUsesTypedToolAndSkillPayloads },
                new HarnessTest { Name = "bridge: typed context payload", Run = BridgeUsesTypedContextPayload },
                new HarnessTest { Name = "bridge: typed vba payload", Run = BridgeUsesTypedVbaPayload }
            };

            var failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Run();
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failed += 1;
                    Console.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine(failed == 0 ? "OK" : "FAILED " + failed);
            return failed == 0 ? 0 : 1;
        }

    }
}
