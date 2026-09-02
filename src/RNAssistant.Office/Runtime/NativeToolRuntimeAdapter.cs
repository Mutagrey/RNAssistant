using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Office.Runtime
{
    // Production composition for direct typed handlers.
    internal sealed class NativeToolRuntimeAdapter : IToolRuntime
    {
        private readonly ToolRuntime _runtime;
        private readonly bool _trace;
        private readonly ChatSession _session;
        private readonly HashSet<string> _ownedToolIds;
        private readonly object _projectionSync = new object();
        private readonly Dictionary<string, IReadOnlyList<ChatAttachment>> _resourceReadAttachments =
            new Dictionary<string, IReadOnlyList<ChatAttachment>>(StringComparer.Ordinal);

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway,
            ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites,
            ExcelFindReplaceToolAdapter excelFindReplace,
            ExcelSheetToolAdapter excelSheets,
            ExcelRangeMutationToolAdapter excelRangeMutations,
            ExcelTableToolAdapter excelTables,
            ExcelChartToolAdapter excelCharts,
            WordToolAdapter wordTools,
            PowerPointToolAdapter powerPointTools,
            OutlookToolAdapter outlookTools,
            HostRuntime hostRuntime, ChatSession session,
            ToolPackSnapshot snapshot, AppSettings settings, string mode,
            bool trace = true)
            : this(gateway, excelReads, excelWrites, excelFindReplace,
                excelSheets, excelRangeMutations, excelTables, excelCharts,
                wordTools, powerPointTools, outlookTools, null, null, null,
                null, null, null, null, null, false, false, hostRuntime,
                session, snapshot, settings, mode, null, trace)
        {
        }

        internal NativeToolRuntimeAdapter(ResourceGatewayService gateway, ExcelReadToolAdapter excelReads,
            ExcelWriteToolAdapter excelWrites, ExcelFindReplaceToolAdapter excelFindReplace,
            ExcelSheetToolAdapter excelSheets,
            ExcelRangeMutationToolAdapter excelRangeMutations,
            ExcelTableToolAdapter excelTables,
            ExcelChartToolAdapter excelCharts,
            WordToolAdapter wordTools,
            PowerPointToolAdapter powerPointTools,
            OutlookToolAdapter outlookTools,
            VbaToolExecutor vbaTools,
            HtmlWorkspaceToolService htmlWorkspaceTools,
            CapabilityCatalogService capabilityTools,
            PromptSettingsService promptTools,
            ToolAuthoringService toolAuthoring,
            SkillAuthoringService skillAuthoring,
            IReadOnlyList<ToolCatalogEntry> discoveryCatalog,
            IReadOnlyList<SkillDefinition> skillCatalog,
            bool manualRun, bool dryRun,
            HostRuntime hostRuntime, ChatSession session,
            ToolPackSnapshot snapshot, AppSettings settings, string mode,
            Func<ToolExecutionContext, ToolPreparationResult, string> pendingRegistrar = null,
            bool trace = true)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var registry = new ToolHandlerRegistry();
            _ownedToolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in snapshot.Registrations.Where(OwnsRegistration))
            {
                var packageRegistration = VbaPackageToolHandler.Owns(registration);
                var binding = packageRegistration
                    ? registration.Binding
                    : DirectToolBindingCatalog.Resolve(
                        registration.Descriptor.Id);
                if (binding == null ||
                    !string.Equals(binding.HandlerId, registration.Binding.HandlerId, StringComparison.Ordinal) ||
                    !string.Equals(binding.EntryPoint, registration.Binding.EntryPoint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Pinned native binding does not match its handler: " + registration.Descriptor.Id);
                IToolHandler handler;
                if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.FindToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceFindToolHandler(gateway, session);
                }
                else if (string.Equals(registration.Descriptor.Id, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal))
                {
                    handler = new ResourceReadToolHandler(gateway, session, CaptureResourceReadAttachments);
                }
                else if (ExcelReadToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelReads == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel read handler dependencies are unavailable.");
                    handler = new ExcelReadToolHandler(registration.Descriptor.Id, excelReads, hostRuntime, session);
                }
                else if (ExcelFindReplaceToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelFindReplace == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel find/replace handler dependencies are unavailable.");
                    handler = new ExcelFindReplaceToolHandler(
                        registration.Descriptor.Id, excelFindReplace, hostRuntime, session);
                }
                else if (ExcelSheetToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelSheets == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel sheet handler dependencies are unavailable.");
                    handler = new ExcelSheetToolHandler(
                        registration.Descriptor.Id, excelSheets, hostRuntime, session);
                }
                else if (ExcelRangeMutationToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelRangeMutations == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel range mutation handler dependencies are unavailable.");
                    handler = new ExcelRangeMutationToolHandler(
                        registration.Descriptor.Id, excelRangeMutations,
                        hostRuntime, session);
                }
                else if (ExcelTableToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelTables == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel table handler dependencies are unavailable.");
                    handler = new ExcelTableToolHandler(
                        excelTables, hostRuntime, session);
                }
                else if (ExcelChartToolIds.Owns(registration.Descriptor.Id))
                {
                    if (excelCharts == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Excel chart handler dependencies are unavailable.");
                    handler = new ExcelChartToolHandler(
                        registration.Descriptor.Id, excelCharts,
                        hostRuntime, session);
                }
                else if (WordToolIds.Owns(registration.Descriptor.Id))
                {
                    if (wordTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Word handler dependencies are unavailable.");
                    handler = new WordToolHandler(
                        registration.Descriptor.Id, wordTools,
                        hostRuntime, session);
                }
                else if (PowerPointToolIds.Owns(registration.Descriptor.Id))
                {
                    if (powerPointTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "PowerPoint handler dependencies are unavailable.");
                    handler = new PowerPointToolHandler(
                        registration.Descriptor.Id, powerPointTools,
                        hostRuntime, session);
                }
                else if (OutlookToolIds.Owns(registration.Descriptor.Id))
                {
                    if (outlookTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "Outlook handler dependencies are unavailable.");
                    handler = new OutlookToolHandler(
                        registration.Descriptor.Id, outlookTools,
                        hostRuntime, session);
                }
                else if (VbaToolCatalog.Owns(registration.Descriptor.Id))
                {
                    if (vbaTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "VBA handler dependencies are unavailable.");
                    handler = new VbaToolHandler(
                        registration.Descriptor.Id, vbaTools,
                        hostRuntime, session);
                }
                else if (string.Equals(registration.Descriptor.Id,
                    UserQuestionToolCatalog.AskToolId,
                    StringComparison.Ordinal))
                {
                    handler = new UserQuestionToolHandler();
                }
                else if (PlanDocumentToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    handler = new PlanDocumentToolHandler(
                        registration.Descriptor.Id, session);
                }
                else if (TaskListToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    handler = new TaskListToolHandler(
                        registration.Descriptor.Id, session);
                }
                else if (HtmlWorkspaceToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (htmlWorkspaceTools == null)
                        throw new InvalidOperationException(
                            "HTML workspace handler dependencies are unavailable.");
                    handler = new HtmlWorkspaceToolHandler(
                        registration.Descriptor.Id, session,
                        htmlWorkspaceTools);
                }
                else if (CapabilityToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (capabilityTools == null)
                        throw new InvalidOperationException(
                            "Capability handler dependencies are unavailable.");
                    handler = new CapabilityToolHandler(
                        registration.Descriptor.Id, capabilityTools,
                        discoveryCatalog, skillCatalog, session, manualRun);
                }
                else if (PromptToolCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (promptTools == null)
                        throw new InvalidOperationException(
                            "Prompt handler dependencies are unavailable.");
                    handler = string.Equals(registration.Descriptor.Id,
                            PromptToolCatalog.ReadToolId,
                            StringComparison.Ordinal)
                        ? (IToolHandler)new PromptReadToolHandler(promptTools)
                        : new PromptSaveToolHandler(promptTools);
                }
                else if (ToolAuthoringCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (toolAuthoring == null)
                        throw new InvalidOperationException(
                            "Tool authoring handler dependencies are unavailable.");
                    handler = ToolAuthoringCatalog.IsMutation(
                            registration.Descriptor.Id)
                        ? (IToolHandler)new ToolAuthoringMutationToolHandler(
                            registration.Descriptor.Id, toolAuthoring)
                        : new ToolAuthoringReadToolHandler(
                            registration.Descriptor.Id, toolAuthoring);
                }
                else if (SkillAuthoringCatalog.Owns(
                    registration.Descriptor.Id))
                {
                    if (skillAuthoring == null)
                        throw new InvalidOperationException(
                            "Skill authoring handler dependencies are unavailable.");
                    handler = new SkillAuthoringToolHandler(
                        registration.Descriptor.Id, skillAuthoring);
                }
                else if (packageRegistration)
                {
                    if (vbaTools == null || hostRuntime == null)
                        throw new InvalidOperationException(
                            "VBA package handler dependencies are unavailable.");
                    handler = new VbaPackageToolHandler(
                        ToolPackageSource.Capture(registration), vbaTools,
                        hostRuntime, session, dryRun);
                }
                else
                {
                    if (excelWrites == null || hostRuntime == null)
                        throw new InvalidOperationException("Excel write handler dependencies are unavailable.");
                    handler = new ExcelWriteToolHandler(excelWrites, hostRuntime, session);
                }
                registry.Register(registration, handler);
                _ownedToolIds.Add(registration.Descriptor.Id);
            }
            var policy = ConversationRunPolicy.For(mode);
            _runtime = new ToolRuntime(registry, policy.Mode,
                settings != null && settings.AutoConfirmToolActions,
                policy.AllowsConfirmation, pendingRegistrar);
            _session = session;
            _trace = trace;
        }

        internal static bool Owns(string toolId)
        {
            return string.Equals(toolId, ResourceToolCatalog.FindToolId, StringComparison.Ordinal) ||
                string.Equals(toolId, ResourceToolCatalog.ReadToolId, StringComparison.Ordinal) ||
                ExcelReadToolIds.Owns(toolId) || ExcelWriteToolIds.Owns(toolId) ||
                ExcelFindReplaceToolIds.Owns(toolId) || ExcelSheetToolIds.Owns(toolId) ||
                ExcelRangeMutationToolIds.Owns(toolId) ||
                ExcelTableToolIds.Owns(toolId) || ExcelChartToolIds.Owns(toolId) ||
                WordToolIds.Owns(toolId) || PowerPointToolIds.Owns(toolId) ||
                OutlookToolIds.Owns(toolId) || VbaToolCatalog.Owns(toolId) ||
                string.Equals(toolId, UserQuestionToolCatalog.AskToolId,
                    StringComparison.Ordinal) ||
                PlanDocumentToolCatalog.Owns(toolId) ||
                TaskListToolCatalog.Owns(toolId) ||
                HtmlWorkspaceToolCatalog.Owns(toolId) ||
                CapabilityToolCatalog.Owns(toolId) ||
                PromptToolCatalog.Owns(toolId) ||
                ToolAuthoringCatalog.Owns(toolId) ||
                SkillAuthoringCatalog.Owns(toolId);
        }

        internal bool Handles(string exactToolId)
        {
            return exactToolId != null && _ownedToolIds.Contains(exactToolId);
        }

        private static bool OwnsRegistration(ToolRegistration registration)
        {
            return registration != null && registration.Descriptor != null &&
                (Owns(registration.Descriptor.Id) ||
                 VbaPackageToolHandler.Owns(registration));
        }

        public ToolPolicySnapshot Describe(ToolCall call) { return _runtime.Describe(call); }

        public async Task<ToolExecutionRecord> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Trace(context, SessionEventKind.ToolExecutionStartedObservation, null);
            try
            {
                var record = await _runtime.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                Trace(context, SessionEventKind.ToolExecutionCompletedObservation, record.Outcome.ToString());
                return record;
            }
            catch (Exception ex)
            {
                Trace(context, SessionEventKind.ToolExecutionCompletedObservation,
                    ex is OperationCanceledException ? "cancelled" : "threw");
                throw;
            }
        }

        internal ToolRunResult ExecuteManual(ToolInvocation command,
            int remainingSteps, CancellationToken token)
        {
            return ExecuteManual(command, remainingSteps, false, token);
        }

        internal ToolRunResult ExecuteManual(ToolInvocation command,
            int remainingSteps, bool confirmed, CancellationToken token)
        {
            // Manual commands also cross the same schema/policy/handler boundary;
            // these transient identities do not create an accepted model response.
            var call = new ToolCall(string.IsNullOrWhiteSpace(command.ToolCallId) ? Guid.NewGuid().ToString("N") : command.ToolCallId,
                command.ToolId, JsonConvert.SerializeObject(command.Arguments, Formatting.None));
            var policy = Describe(call);
            if (policy == null) return ToolRunResult.Error(
                "No direct handler is available for this exact tool id.",
                null, "unknown_tool", false);
            var identity = Guid.NewGuid().ToString("N");
            var runId = _session == null || _session.LastRun == null ||
                string.IsNullOrWhiteSpace(_session.LastRun.RunId)
                    ? identity : _session.LastRun.RunId;
            var turnId = _session == null || _session.LastRun == null ||
                string.IsNullOrWhiteSpace(_session.LastRun.TurnId)
                    ? identity : _session.LastRun.TurnId;
            var context = new ToolExecutionContext(call, policy, runId, turnId,
                string.IsNullOrWhiteSpace(command.RuntimeStepId) ? identity : command.RuntimeStepId,
                DateTime.UtcNow, confirmed, remainingSteps);
            var record = ExecuteAsync(context, token).GetAwaiter().GetResult();
            var materialized = TakeMaterialization(record);
            return ToolRunResultFactory.Create(record, materialized);
        }

        internal ToolResultMaterialization TakeMaterialization(ToolExecutionRecord record)
        {
            if (record == null || record.Result == null) return null;
            IReadOnlyList<ChatAttachment> attachments = null;
            lock (_projectionSync)
            {
                _resourceReadAttachments.TryGetValue(record.Context.Call.Id, out attachments);
                _resourceReadAttachments.Remove(record.Context.Call.Id);
            }
            return new ToolResultMaterialization(record.Result, attachments);
        }

        private void CaptureResourceReadAttachments(string callId, IReadOnlyList<ChatAttachment> attachments)
        {
            if (string.IsNullOrWhiteSpace(callId) || attachments == null || attachments.Count == 0) return;
            var captured = attachments.Where(item => item != null).ToArray();
            if (captured.Length == 0) return;
            lock (_projectionSync)
            {
                _resourceReadAttachments[callId] = Array.AsReadOnly(captured);
            }
        }

        private void Trace(ToolExecutionContext context, SessionEventKind kind, string status)
        {
            if (!_trace) return;
            RunCausalTrace.Record(new CausalTraceRecord(kind)
            {
                StepId = context.StepId, ToolCallId = context.Call.Id,
                ToolId = context.Call.Name, Status = status, Boundary = "tool_runtime"
            });
        }
    }
}
