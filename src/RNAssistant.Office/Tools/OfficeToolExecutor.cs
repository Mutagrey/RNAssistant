using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Vba;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly IReadOnlyList<ToolDefinition> _adapterTools;
        private readonly VbaToolExecutor _vbaExecutor;
        private readonly SkillToolExecutor _skillExecutor;
        private readonly CapabilityCatalogService _capabilityCatalogService;
        private readonly ToolAuthoringService _toolAuthoringService;
        private readonly PromptSettingsService _promptSettingsService;
        private readonly ResourceGatewayService _resourceGateway;
        private readonly ExcelReadToolAdapter _excelReadAdapter;
        private readonly ExcelWriteToolAdapter _excelWriteAdapter;
        private readonly ExcelFindReplaceToolAdapter _excelFindReplaceAdapter;
        private readonly ExcelSheetToolAdapter _excelSheetAdapter;
        private readonly ExcelRangeMutationToolAdapter _excelRangeMutationAdapter;
        private readonly ExcelTableToolAdapter _excelTableAdapter;
        private readonly ExcelChartToolAdapter _excelChartAdapter;
        private readonly WordToolAdapter _wordAdapter;
        private readonly PowerPointToolAdapter _powerPointAdapter;
        private readonly OutlookToolAdapter _outlookAdapter;
        private readonly HtmlWorkspaceToolService _htmlWorkspaceService;
        private readonly IReadOnlyList<ToolDefinition> _controllerTools;
        private readonly IDictionary<string, ControllerExecutorKind> _controllerExecutors;
        private readonly HostRuntime _hostRuntime;

        internal HostRuntime DocumentRuntime { get { return _hostRuntime; } }
        internal VbaReader VbaReader { get { return _vbaExecutor.Reader; } }

        public OfficeToolExecutor(
            IOfficeApplicationAdapter adapter,
            VbaJournalStore vbaJournalStore,
            SkillStore skillStore,
            ToolStore toolStore = null,
            Func<AppSettings> loadSettings = null,
            Action<AppSettings> saveSettings = null,
            AppDataPaths paths = null,
            Func<ChatSession, string, bool> loadArtifactBody = null,
            Func<ChatAttachment, int, string> readAttachmentText = null)
        {
            _adapter = adapter;
            _adapterTools = (_adapter.GetBuiltInTools() ?? new ToolDefinition[0]).ToArray();
            _vbaExecutor = new VbaToolExecutor(adapter, vbaJournalStore);
            _skillExecutor = new SkillToolExecutor(adapter, skillStore);
            _capabilityCatalogService = new CapabilityCatalogService(
                adapter, skillStore);
            _toolAuthoringService = new ToolAuthoringService(
                adapter, toolStore, id => IsProtectedToolId(id));
            _promptSettingsService = new PromptSettingsService(
                loadSettings, saveSettings);
            _hostRuntime = new HostRuntime(adapter, paths);
            _resourceGateway = new ResourceGatewayService(
                adapter,
                _vbaExecutor,
                vbaJournalStore,
                loadArtifactBody,
                readAttachmentText,
                BeginLiveOfficeRead);
            var excelBackends = _adapter as IExcelBackendProvider;
            _excelReadAdapter = excelBackends == null || excelBackends.ExcelReadBackend == null
                ? null : new ExcelReadToolAdapter(excelBackends.ExcelReadBackend);
            _excelWriteAdapter = excelBackends == null || excelBackends.ExcelWriteBackend == null
                ? null : new ExcelWriteToolAdapter(excelBackends.ExcelWriteBackend);
            _excelFindReplaceAdapter = excelBackends == null || excelBackends.ExcelFindReplaceBackend == null
                ? null : new ExcelFindReplaceToolAdapter(excelBackends.ExcelFindReplaceBackend);
            _excelSheetAdapter = excelBackends == null || excelBackends.ExcelSheetBackend == null
                ? null : new ExcelSheetToolAdapter(excelBackends.ExcelSheetBackend);
            _excelRangeMutationAdapter = excelBackends == null ||
                excelBackends.ExcelRangeMutationBackend == null
                ? null : new ExcelRangeMutationToolAdapter(
                    excelBackends.ExcelRangeMutationBackend);
            _excelTableAdapter = excelBackends == null ||
                excelBackends.ExcelTableBackend == null
                ? null : new ExcelTableToolAdapter(excelBackends.ExcelTableBackend);
            _excelChartAdapter = excelBackends == null ||
                excelBackends.ExcelChartBackend == null
                ? null : new ExcelChartToolAdapter(excelBackends.ExcelChartBackend);
            var wordBackend = _adapter as IWordBackendProvider;
            _wordAdapter = wordBackend == null || wordBackend.WordBackend == null
                ? null : new WordToolAdapter(wordBackend.WordBackend);
            var powerPointBackend = _adapter as IPowerPointBackendProvider;
            _powerPointAdapter = powerPointBackend == null ||
                powerPointBackend.PowerPointBackend == null
                ? null : new PowerPointToolAdapter(
                    powerPointBackend.PowerPointBackend);
            var outlookBackend = _adapter as IOutlookBackendProvider;
            _outlookAdapter = outlookBackend == null ||
                outlookBackend.OutlookBackend == null
                ? null : new OutlookToolAdapter(outlookBackend.OutlookBackend);
            _htmlWorkspaceService = new HtmlWorkspaceToolService(
                _adapter, _adapterTools, BeginLiveOfficeRead,
                ExecuteHtmlDataSourceUnderCurrentAccess);
            var controllerTools = new List<ToolDefinition>();
            _controllerExecutors = new Dictionary<string, ControllerExecutorKind>(StringComparer.OrdinalIgnoreCase);
            if (_vbaExecutor.HostSupportsVba())
                RegisterControllerTools(controllerTools,
                    VbaToolCatalog.GetTools(), ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools, _skillExecutor.GetControllerTools(), ControllerExecutorKind.Skill);
            RegisterControllerTools(controllerTools,
                CapabilityToolCatalog.GetTools(), ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                ToolAuthoringCatalog.GetTools(_toolAuthoringService),
                ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                PromptToolCatalog.GetTools(_promptSettingsService),
                ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools, ResourceToolCatalog.GetControllerTools(), ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                HtmlWorkspaceToolCatalog.GetTools(_htmlWorkspaceService),
                ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                TaskListToolCatalog.GetTools(), ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                PlanDocumentToolCatalog.GetTools(), ControllerExecutorKind.Native);
            RegisterControllerTools(controllerTools,
                UserQuestionToolCatalog.GetTools(), ControllerExecutorKind.Native);
            _controllerTools = controllerTools.ToArray();
            var duplicate = _adapterTools.FirstOrDefault(tool => tool != null && _controllerExecutors.ContainsKey(tool.Id ?? string.Empty));
            if (duplicate != null)
            {
                throw new InvalidOperationException("Duplicate built-in tool id: " + duplicate.Id);
            }
        }

        public IEnumerable<ToolDefinition> GetControllerTools()
        {
            return _controllerTools;
        }

        internal ResourceGatewayService ResourceGateway { get { return _resourceGateway; } }

        internal NativeToolRuntimeAdapter CreateNativeRuntime(ChatSession session, ToolPackSnapshot snapshot,
            AppSettings settings, string mode, bool trace = true,
            Func<RNAssistant.Core.Tools.ToolExecutionContext,
                ToolPreparationResult, string> pendingRegistrar = null,
            IReadOnlyList<ToolDefinition> discoveryCatalog = null,
            IReadOnlyList<SkillDefinition> skillCatalog = null,
            bool manualRun = false,
            bool dryRun = false)
        {
            return new NativeToolRuntimeAdapter(_resourceGateway, _excelReadAdapter, _excelWriteAdapter,
                _excelFindReplaceAdapter, _excelSheetAdapter,
                _excelRangeMutationAdapter, _excelTableAdapter,
                _excelChartAdapter, _wordAdapter, _powerPointAdapter,
                _outlookAdapter, _vbaExecutor, _htmlWorkspaceService,
                _capabilityCatalogService, _promptSettingsService,
                _toolAuthoringService,
                discoveryCatalog, skillCatalog,
                manualRun, dryRun, _hostRuntime,
                session, snapshot, settings, mode, pendingRegistrar, trace);
        }

        internal NativeToolRuntimeAdapter CreateNativeRuntime(ChatSession session, IEnumerable<ToolDefinition> catalog,
            AppSettings settings, string mode, bool trace = true,
            Func<RNAssistant.Core.Tools.ToolExecutionContext,
                ToolPreparationResult, string> pendingRegistrar = null,
            IReadOnlyList<ToolDefinition> discoveryCatalog = null,
            IReadOnlyList<SkillDefinition> skillCatalog = null,
            bool manualRun = false,
            bool dryRun = false)
        {
            var catalogList = (catalog ?? new ToolDefinition[0]).ToArray();
            return CreateNativeRuntime(session,
                ToolPackSnapshotFactory.Capture(
                    mode, _adapter.HostName, catalogList),
                settings, mode, trace, pendingRegistrar,
                discoveryCatalog ?? catalogList, skillCatalog, manualRun,
                dryRun);
        }

        internal List<ToolDefinition> AvailableConversationToolsForSession(
            IEnumerable<ToolDefinition> tools,
            ChatSession session)
        {
            var source = (tools ?? new ToolDefinition[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .ToList();
            if (OfficeDocumentExecutionGuardState.SessionMatchesAdapter(_adapter, session))
            {
                return source;
            }

            return source.Where(tool => !RequiresOfficeDocument(tool)).ToList();
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> tools, AppSettings settings, bool dryRun, bool manualRun, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Execute(command, tools, settings, dryRun, manualRun, null, cancellationToken);
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> tools, AppSettings settings, bool dryRun, bool manualRun, ChatSession session, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Execute(
                command,
                tools,
                settings,
                dryRun,
                manualRun,
                session,
                settings == null ? AppSettings.DefaultMaxAgentToolSteps : settings.MaxAgentToolSteps,
                cancellationToken);
        }

        public ToolResult Execute(ToolCommand command, IReadOnlyList<ToolDefinition> tools, AppSettings settings, bool dryRun, bool manualRun, ChatSession session, int maxExecutionSteps, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Execute(command, tools, settings, dryRun, manualRun, session, maxExecutionSteps, null, cancellationToken);
        }

        public ToolResult Execute(
            ToolCommand command,
            IReadOnlyList<ToolDefinition> tools,
            AppSettings settings,
            bool dryRun,
            bool manualRun,
            ChatSession session,
            int maxExecutionSteps,
            IReadOnlyList<SkillDefinition> skillCatalog,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var context = new ToolExecutionContext(
                KnownTools(tools),
                tools,
                settings ?? new AppSettings(),
                session,
                maxExecutionSteps,
                skillCatalog);
            var initialSteps = context.RemainingSteps;
            TraceExecution(command, SessionEventKind.ToolExecutionStartedObservation, null, null);
            try
            {
                // Native handlers own their exact document scope. Wrapping them here
                // would create a second operation root and defeat synchronous reentry.
                var result = command != null && IsNativeTool(command.ToolId, context.Tools)
                    ? ExecuteCommandSafely(command, context, dryRun, manualRun, cancellationToken)
                    : _hostRuntime.ExecuteForExpectedDocument(
                        DocumentTarget(session),
                        RequiresOfficeDocument(command, context.Tools),
                        cancellationToken,
                        () => ExecuteCommandSafely(command, context, dryRun, manualRun, cancellationToken));
                if (result != null) result.ToolStepsConsumed = initialSteps - context.RemainingSteps;
                TraceExecution(command, SessionEventKind.ToolExecutionCompletedObservation,
                    result == null ? "missing_result" : result.Status, result == null ? null : result.ErrorCode);
                return result;
            }
            catch (Exception ex)
            {
                TraceExecution(command, SessionEventKind.ToolExecutionCompletedObservation,
                    ex is OperationCanceledException ? "cancelled" : "threw", null);
                throw;
            }
        }

        private static void TraceExecution(
            ToolCommand command,
            SessionEventKind kind,
            string status,
            string code)
        {
            RunCausalTrace.Record(new CausalTraceRecord(kind)
            {
                StepId = command == null ? null : command.RuntimeStepId,
                ToolCallId = command == null ? null : command.ToolCallId,
                ToolId = command == null ? null : command.ToolId,
                Status = status,
                Code = code,
                Boundary = "office_tool_executor"
            });
        }

        private static OfficeDocumentExecutionExpectation DocumentTarget(ChatSession session)
        {
            return session == null ? null : new OfficeDocumentExecutionExpectation
            {
                Host = session.Host,
                DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun == null ? string.Empty : session.LastRun.DocumentRuntimeKey
            };
        }

        internal string VbaToolId(string suffix)
        {
            return _vbaExecutor.ToolId(suffix);
        }

        public void ObserveVbaHash(ChatSession session, string moduleName, string codeSha256)
        {
            _vbaExecutor.ObserveExpectedHash(session, moduleName, codeSha256);
        }

        internal ToolResult ReadVbaProjectForEditor(ChatSession session)
        {
            return ExecuteLiveVbaEditorRead(
                session,
                () => ((IVbaResourceSource)_vbaExecutor).ListResourceModules());
        }

        internal ToolResult ReadVbaModuleForEditor(ChatSession session, string moduleName, int maxChars)
        {
            return ExecuteLiveVbaEditorRead(
                session,
                () => ((IVbaResourceSource)_vbaExecutor).ReadResourceModule(
                    session,
                    moduleName,
                    maxChars));
        }

        private ToolResult ExecuteLiveVbaEditorRead(ChatSession session, Func<ToolResult> action)
        {
            // An editor request is a separate operation, not a nested read from an
            // unrelated UI callback that happens to run on the same STA.
            try
            {
                return _hostRuntime.ExecuteForExpectedDocument(DocumentTarget(session), true, action);
            }
            catch (ResourceRequestException ex)
            {
                return ToolResult.Fail(ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
        }

        public ToolResult RunVbaMacro(
            string macroName,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var tool = VbaToolCatalog.GetTools().First(item =>
                string.Equals(item.Id, VbaToolCatalog.RunMacro,
                    StringComparison.Ordinal));
            var command = new ToolCommand { ToolId = tool.Id };
            command.Arguments["macroName"] = macroName;
            return Execute(command, new[] { tool }, new AppSettings(),
                false, true, session, cancellationToken);
        }

        public ToolResult ValidateToolDefinition(ToolDefinition tool)
        {
            var validation = _toolAuthoringService.ValidateDefinition(tool);
            return validation.Success
                ? ToolResult.Ok(validation.Message, validation.DataJson)
                : ToolResult.Fail(validation.Message, validation.DataJson,
                    validation.ErrorCode, validation.Retryable);
        }

        internal bool RequiresSessionLeaseForManualRun(
            string toolId,
            IEnumerable<ToolDefinition> tools)
        {
            var known = KnownTools(tools);
            var tool = known.FirstOrDefault(item => item != null &&
                string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));
            if (tool == null) return false;
            var safety = ToolSafetyPolicy.Resolve(tool, known);
            return safety.Valid && (safety.MutatesDocument || safety.MutatesLocalState);
        }

        internal static ChatSession CreateIsolatedManualSession(ChatSession session)
        {
            var snapshot = ChatCloneService.CloneSessionSnapshot(session);
            if (snapshot != null)
            {
                // Keep document/run identity for execution guards, but do not let a library read
                // advance chat-scoped observations that the running model never received.
                snapshot.Id = "manual_" + Guid.NewGuid().ToString("N");
            }
            return snapshot;
        }

        internal VbaPackageResult InstallVbaTool(
            ToolPackageSource source,
            bool dryRun,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecutePackageMutation(source, session, dryRun,
                cancellationToken, markDispatch =>
                    _vbaExecutor.InstallCustomPackage(
                        source, dryRun, session,
                        markDispatchPossible: markDispatch,
                        cancellationToken: cancellationToken));
        }

        internal VbaPackageResult RemoveVbaTool(
            ToolPackageSource source,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecutePackageMutation(source, session, false,
                cancellationToken, markDispatch =>
                    _vbaExecutor.RemoveCustomPackage(
                        source, session,
                        markDispatchPossible: markDispatch,
                        cancellationToken: cancellationToken));
        }

        internal VbaPackageStatusResult GetVbaInstallationStatus(
            ToolPackageSource source)
        {
            return _vbaExecutor.GetInstallationStatus(source);
        }

        internal VbaPackageStatusResult GetVbaInstallationStatus(
            ToolPackageSource globalSource,
            ToolPackageSource documentSource)
        {
            return _vbaExecutor.GetInstallationStatus(
                globalSource, documentSource);
        }

        private ToolResult ExecuteCommandSafely(ToolCommand command, ToolExecutionContext context, bool dryRun, bool manualRun, CancellationToken cancellationToken)
        {
            try
            {
                return ExecuteCommand(command, context, dryRun, manualRun, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HostRuntime.MutationLockException ex)
            {
                return MutationLockFailure(ex);
            }
            catch (Exception ex)
            {
                var toolId = command == null ? string.Empty : command.ToolId ?? string.Empty;
                var tool = context == null ? null : context.Find(toolId);
                var safety = tool == null || context == null ? null : context.Safety(tool);
                var effectUncertain = safety != null && (safety.MutatesDocument || safety.MutatesLocalState);
                return ToolResult.Fail(
                    "Tool execution failed: " + toolId + ". " + DeepestMessage(ex) +
                        (effectUncertain ? " The external effect may have been applied; inspect state before retrying." : string.Empty),
                    null,
                    effectUncertain ? "tool_effect_uncertain" : "tool_execution_exception",
                    !effectUncertain);
            }
        }

        private ToolResult ExecuteCommand(ToolCommand command, ToolExecutionContext context, bool dryRun, bool manualRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            var tool = context.Find(command.ToolId);
            if (tool == null)
            {
                return UnknownTool(command.ToolId, context.Tools);
            }
            if (!tool.Enabled)
            {
                return DisabledTool(command.ToolId, context.Tools);
            }
            if (string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail("Pipelines are disabled during stabilization.", null, "pipeline_disabled", false);
            }
            if (!string.IsNullOrWhiteSpace(tool.CapabilityStatus) &&
                !string.Equals(tool.CapabilityStatus, "available", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.CapabilityStatus, "partial", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Fail(
                    "Tool is unavailable: " + command.ToolId + ". " + (tool.Limitations ?? tool.CapabilityStatus),
                    null,
                    "tool_capability_unavailable",
                    false);
            }

            if (IsNativeTool(command.ToolId, new[] { tool }))
            {
                if (dryRun && (ExcelWriteToolIds.Owns(command.ToolId) ||
                    ExcelFindReplaceToolIds.IsMutation(command.ToolId) ||
                    ExcelSheetToolIds.Owns(command.ToolId) ||
                    ExcelRangeMutationToolIds.Owns(command.ToolId) ||
                    ExcelTableToolIds.Owns(command.ToolId) ||
                    ExcelChartToolIds.IsMutation(command.ToolId) ||
                    WordToolIds.IsMutation(command.ToolId) ||
                    PowerPointToolIds.IsMutation(command.ToolId) ||
                    OutlookToolIds.IsMutation(command.ToolId) ||
                    VbaToolCatalog.Owns(command.ToolId) ||
                    PromptToolCatalog.IsMutation(command.ToolId) ||
                    ToolAuthoringCatalog.IsMutation(command.ToolId) ||
                    PlanDocumentToolCatalog.Owns(command.ToolId) ||
                    TaskListToolCatalog.Owns(command.ToolId) ||
                    HtmlWorkspaceToolCatalog.IsMutation(command.ToolId)))
                {
                    var validation = ValidateCommandArguments(command, tool);
                    if (validation != null) return validation;
                    if (!context.TryConsumeStep())
                        return ToolResult.Fail("Tool execution budget exceeded.", null, "tool_step_limit_exceeded", false);
                    return ToolResult.Ok("Dry run: would execute " + command.ToolId,
                        JsonConvert.SerializeObject(command.Arguments));
                }
                var remainingSteps = context.RemainingSteps;
                if (!context.TryConsumeStep())
                    return ToolResult.Fail("Tool execution budget exceeded.", null, "tool_step_limit_exceeded", false);
                var nativeSettings = context.Settings;
                var nativeConfirmed = manualRun;
                if (manualRun && (VbaToolCatalog.Owns(command.ToolId) ||
                    VbaPackageToolHandler.IsDefinition(tool) ||
                    PromptToolCatalog.IsMutation(command.ToolId) ||
                    ToolAuthoringCatalog.IsMutation(command.ToolId)))
                {
                    // A direct UI action is already authorized, but a guarded
                    // handler must still prepare and consume its exact state.
                    nativeSettings = new AppSettings { AutoConfirmToolActions = true };
                    nativeConfirmed = false;
                }
                return CreateNativeRuntime(context.Session, new[] { tool }, nativeSettings,
                    ChatModes.Normalize(context.Session == null ? null : context.Session.Mode), false,
                    (execution, preparation) => Guid.NewGuid().ToString("N"),
                    context.DiscoveryCatalog, context.SkillCatalog, manualRun,
                    dryRun)
                    .ExecuteCommand(command, remainingSteps, nativeConfirmed, cancellationToken);
            }

            var argumentValidation = ValidateCommandArguments(command, tool);
            if (argumentValidation != null)
            {
                return argumentValidation;
            }

            var customTool = tool != null && !tool.BuiltIn ? tool : null;
            var safety = context.Safety(tool);
            if (!safety.Valid)
            {
                return ToolResult.Fail(safety.Error);
            }

            ControllerExecutorKind controllerKind;
            var isController = _controllerExecutors.TryGetValue(command.ToolId, out controllerKind);
            if (ToolSafetyPolicy.RequiresConfirmation(tool, safety, context.Settings, dryRun, manualRun))
            {
                ToolResult preview = null;
                if (isController)
                {
                    preview = ExecuteControllerTool(
                        controllerKind,
                        CloneCommand(command),
                        context,
                        true,
                        true,
                        cancellationToken);
                    if (preview != null && !preview.Success) return preview;
                }
                var waiting = ToolResult.WaitingConfirmation(
                    preview == null
                        ? "Tool requires confirmation before execution: " + command.ToolId
                        : "Confirmation required. " + preview.Message);
                return waiting;
            }

            if (!context.TryConsumeStep())
            {
                return ToolResult.Fail("Tool execution budget exceeded.", null, "tool_step_limit_exceeded", false);
            }

            var needsMutationScope = !dryRun &&
                (safety.MutatesDocument || safety.MutatesLocalState);
            if (needsMutationScope)
            {
                return _hostRuntime.ExecuteMutation(
                    DocumentTarget(context.Session),
                    safety.MutatesLocalState && !string.Equals(tool.Scope, "session", StringComparison.OrdinalIgnoreCase),
                    safety.MutatesDocument,
                    cancellationToken,
                    () => ExecuteResolvedCommand(command, context, dryRun, manualRun, cancellationToken, customTool));
            }

            return ExecuteResolvedCommand(command, context, dryRun, manualRun, cancellationToken, customTool);
        }

        private static ToolResult ValidateCommandArguments(ToolCommand command, ToolDefinition tool)
        {
            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError))
            {
                return ToolResult.Fail(schemaError, null, "invalid_tool_schema", false);
            }

            JObject arguments;
            try
            {
                arguments = JObject.FromObject(command.Arguments ?? new Dictionary<string, object>());
                RejectStringifiedStructuredArguments(arguments, schema);
                ToolSchemaSupport.RemoveOptionalNulls(arguments, schema);
            }
            catch (JsonException ex)
            {
                return ToolResult.Fail("Tool arguments are invalid: " + ex.Message, null, "invalid_arguments", true);
            }

            string argumentError;
            if (!ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError))
            {
                return ToolResult.Fail(argumentError, null, "invalid_arguments", true);
            }

            command.Arguments.Clear();
            ToolArgumentNormalizer.AddProperties(arguments, command.Arguments);
            return null;
        }

        private static void RejectStringifiedStructuredArguments(JObject arguments, JObject schema)
        {
            var properties = schema == null ? null : schema["properties"] as JObject;
            if (arguments == null || properties == null) return;
            foreach (var property in properties.Properties())
            {
                var value = arguments[property.Name];
                var propertySchema = property.Value as JObject;
                if (value == null || value.Type != JTokenType.String || propertySchema == null) continue;
                var type = Convert.ToString(propertySchema["type"]);
                if (!string.Equals(type, "array", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "object", StringComparison.OrdinalIgnoreCase)) continue;
                throw new JsonException(
                    "$." + property.Name + " must be a native JSON " + type +
                    ", not quoted/stringified JSON.");
            }
        }

        private ToolResult ExecuteResolvedCommand(ToolCommand command, ToolExecutionContext context, bool dryRun, bool manualRun, CancellationToken cancellationToken, ToolDefinition customTool)
        {

            if (customTool != null)
            {
                return ToolResult.Fail("Tool executor is not runnable yet: " + customTool.Executor);
            }

            ControllerExecutorKind controllerExecutor;
            if (_controllerExecutors.TryGetValue(command.ToolId, out controllerExecutor))
            {
                return ExecuteControllerTool(controllerExecutor, command, context, dryRun, manualRun, cancellationToken);
            }

            if (dryRun)
            {
                return ToolResult.Ok("Dry run: would execute " + command.ToolId, JsonConvert.SerializeObject(command.Arguments));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _adapter.ExecuteTool(command);
        }

        private VbaPackageResult ExecutePackageMutation(
            ToolPackageSource source,
            ChatSession session,
            bool dryRun,
            CancellationToken cancellationToken,
            Func<Action, VbaPackageResult> action)
        {
            var dispatched = false;
            Action markDispatch = delegate { dispatched = true; };
            try
            {
                if (dryRun)
                {
                    return _hostRuntime.ReadDocument(
                        DocumentTarget(session), cancellationToken,
                        () => action(markDispatch));
                }
                return _hostRuntime.ExecuteDocumentMutation(
                    DocumentTarget(session), cancellationToken,
                    () => action(markDispatch));
            }
            catch (HostRuntime.MutationLockException ex)
            {
                return VbaPackageResult.Error(source, ex.Message,
                    ex.Retryable ? "tool_mutation_busy" :
                        "tool_mutation_lock_unavailable",
                    ex.Retryable, dispatched);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return VbaPackageResult.Error(source,
                    "VBA package operation failed. " + DeepestMessage(ex) +
                    (dispatched
                        ? " The document effect may have been applied; inspect state before retrying."
                        : string.Empty),
                    dispatched ? "tool_effect_uncertain" :
                        "vba_package_operation_failed",
                    false, dispatched);
            }
        }

        private static bool IsNativeTool(string exactToolId,
            IEnumerable<ToolDefinition> tools)
        {
            if (NativeToolRuntimeAdapter.Owns(exactToolId)) return true;
            var definition = (tools ?? new ToolDefinition[0])
                .FirstOrDefault(item => item != null &&
                    string.Equals(item.Id, exactToolId,
                        StringComparison.Ordinal));
            return VbaPackageToolHandler.IsDefinition(definition);
        }

        private IDisposable BeginLiveOfficeRead(ChatSession session)
        {
            try
            {
                return _hostRuntime.BeginDocumentAccess(DocumentTarget(session));
            }
            catch (OfficeDocumentGuardException ex)
            {
                throw new ResourceRequestException(ex.Message, ex.ErrorCode, ex.Retryable);
            }
            catch (HostRuntime.MutationLockException ex)
            {
                throw new ResourceRequestException(
                    ex.Message,
                    ex.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                    ex.Retryable);
            }
        }

        private HtmlDataSourceReadOutcome ExecuteHtmlDataSourceUnderCurrentAccess(
            string toolId,
            IDictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ExcelReadToolIds.Owns(toolId))
            {
                if (_excelReadAdapter == null)
                    return HtmlDataSourceReadOutcome.Error(
                        "The bound Excel read backend is unavailable.", null,
                        "excel_backend_unavailable", false);
                var outcome = _excelReadAdapter.ExecuteOutcome(
                    toolId, arguments);
                return outcome.Success
                    ? HtmlDataSourceReadOutcome.Ok(
                        outcome.Message, outcome.DataJson)
                    : HtmlDataSourceReadOutcome.Error(
                        outcome.Message, outcome.DataJson,
                        outcome.ErrorCode, outcome.Retryable);
            }
            if (WordToolIds.IsRead(toolId))
            {
                if (_wordAdapter == null)
                    return HtmlDataSourceReadOutcome.Error(
                        "The bound Word read backend is unavailable.", null,
                        "word_backend_unavailable", false);
                var outcome = _wordAdapter.Execute(
                    toolId, arguments, null, cancellationToken);
                return outcome.Status == WordOutcomeStatus.Ok
                    ? HtmlDataSourceReadOutcome.Ok(
                        outcome.Message, outcome.DataJson)
                    : HtmlDataSourceReadOutcome.Error(
                        outcome.Message, outcome.DataJson,
                        outcome.ErrorCode, outcome.Retryable);
            }
            if (PowerPointToolIds.IsRead(toolId))
            {
                if (_powerPointAdapter == null)
                    return HtmlDataSourceReadOutcome.Error(
                        "The bound PowerPoint read backend is unavailable.", null,
                        "powerpoint_backend_unavailable", false);
                var outcome = _powerPointAdapter.Execute(
                    toolId, arguments, null, cancellationToken);
                return outcome.Status == PowerPointOutcomeStatus.Ok
                    ? HtmlDataSourceReadOutcome.Ok(
                        outcome.Message, outcome.DataJson)
                    : HtmlDataSourceReadOutcome.Error(
                        outcome.Message, outcome.DataJson,
                        outcome.ErrorCode, outcome.Retryable);
            }
            if (OutlookToolIds.IsRead(toolId))
            {
                if (_outlookAdapter == null)
                    return HtmlDataSourceReadOutcome.Error(
                        "The bound Outlook read backend is unavailable.", null,
                        "outlook_backend_unavailable", false);
                var outcome = _outlookAdapter.Execute(
                    toolId, arguments, null, cancellationToken);
                return outcome.Status == OutlookOutcomeStatus.Ok
                    ? HtmlDataSourceReadOutcome.Ok(
                        outcome.Message, outcome.DataJson)
                    : HtmlDataSourceReadOutcome.Error(
                        outcome.Message, outcome.DataJson,
                        outcome.ErrorCode, outcome.Retryable);
            }
            return HtmlDataSourceReadOutcome.Error(
                "HTML data source tool has no typed backend: " + toolId + ".",
                null, "html_data_source_backend_missing", false);
        }

        private static ToolResult MutationLockFailure(HostRuntime.MutationLockException exception)
        {
            return ToolResult.Fail(
                exception.Message,
                null,
                exception.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                exception.Retryable);
        }

        private static string DeepestMessage(Exception exception)
        {
            var current = exception;
            while (current != null && current.InnerException != null)
            {
                current = current.InnerException;
            }
            return current == null ? "Unknown error." : current.Message;
        }

        private ToolResult ExecuteControllerTool(ControllerExecutorKind executor, ToolCommand command, ToolExecutionContext context, bool dryRun, bool manualRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (executor)
            {
                case ControllerExecutorKind.Skill:
                    return _skillExecutor.ExecuteControllerTool(command, context.Settings, dryRun, manualRun, context.SkillCatalog);
                case ControllerExecutorKind.Native:
                    return ToolResult.Fail("Native tool did not enter its registered handler.", null,
                        "native_handler_unavailable", false);
                default:
                    return ToolResult.Fail("Unknown controller executor for tool: " + command.ToolId);
            }
        }

        private void RegisterControllerTools(ICollection<ToolDefinition> target, IEnumerable<ToolDefinition> tools, ControllerExecutorKind executor)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
                {
                    continue;
                }

                if (_controllerExecutors.ContainsKey(tool.Id))
                {
                    throw new InvalidOperationException("Duplicate controller tool id: " + tool.Id);
                }

                _controllerExecutors.Add(tool.Id, executor);
                target.Add(tool);
            }
        }

        private bool RequiresOfficeDocument(ToolCommand command, IReadOnlyList<ToolDefinition> tools)
        {
            var id = command == null ? null : command.ToolId;
            var catalog = (tools ?? new ToolDefinition[0])
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Id))
                .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ToolDefinition tool;
            if (string.IsNullOrWhiteSpace(id) || !catalog.TryGetValue(id, out tool)) return false;
            return RequiresOfficeDocument(tool);
        }

        private bool RequiresOfficeDocument(ToolDefinition tool)
        {
            var id = tool.Id;
            if (string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase)) return false;
            if (_adapterTools.Any(candidate => candidate != null &&
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))) return true;

            ControllerExecutorKind controller;
            if (_controllerExecutors.TryGetValue(id, out controller))
            {
                return VbaToolCatalog.Owns(id) ||
                    HtmlWorkspaceToolCatalog.RequiresOfficeDocument(id);
            }
            return true;
        }

        private IReadOnlyList<ToolDefinition> KnownTools(IEnumerable<ToolDefinition> providedTools)
        {
            var result = new List<ToolDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTools(result, seen, _adapterTools);
            AddTools(result, seen, _controllerTools);
            AddTools(result, seen, providedTools);
            return result;
        }

        internal bool IsProtectedToolId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                (_adapterTools.Any(tool => tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                 _controllerExecutors.ContainsKey(id));
        }

        private static void AddTools(ICollection<ToolDefinition> result, ISet<string> seen, IEnumerable<ToolDefinition> tools)
        {
            foreach (var tool in tools ?? new ToolDefinition[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id) || seen.Contains(tool.Id))
                {
                    continue;
                }

                seen.Add(tool.Id);
                result.Add(tool);
            }
        }

        private static ToolCommand CloneCommand(ToolCommand command)
        {
            var clone = new ToolCommand
            {
                ToolId = command == null ? string.Empty : command.ToolId,
                Description = command == null ? string.Empty : command.Description,
                ToolCallId = command == null ? string.Empty : command.ToolCallId,
                RuntimeGuardJson = command == null ? null : command.RuntimeGuardJson,
                RuntimeStepId = command == null ? null : command.RuntimeStepId
            };
            if (command != null && command.Arguments != null)
            {
                foreach (var pair in command.Arguments) clone.Arguments[pair.Key] = pair.Value;
            }
            return clone;
        }

        private static ToolResult UnknownTool(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools)
        {
            var suggestions = ToolIdSuggester.Suggest(requestedToolId, knownTools, 5);
            var message = "Unknown tool id: " + requestedToolId + ". Use only available tool ids.";
            if (suggestions.Count > 0)
            {
                message += " Did you mean: " + string.Join(", ", suggestions.ToArray()) + "?";
            }

            return ToolResult.Fail(message, ToolDiagnosticJson(requestedToolId, knownTools, suggestions, false), "unknown_tool", true);
        }

        private static ToolResult DisabledTool(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools)
        {
            return ToolResult.Fail(
                "Tool is disabled: " + requestedToolId + ". Enable it or use another available tool id.",
                ToolDiagnosticJson(requestedToolId, knownTools, new List<string>(), true),
                "tool_disabled",
                false);
        }

        private static string ToolDiagnosticJson(string requestedToolId, IReadOnlyList<ToolDefinition> knownTools, IReadOnlyList<string> suggestions, bool disabled)
        {
            return JsonConvert.SerializeObject(new
            {
                requestedToolId = requestedToolId,
                disabled = disabled,
                suggestions = suggestions ?? new string[0],
                availableToolIds = (knownTools ?? new ToolDefinition[0])
                    .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                    .Select(tool => tool.Id)
                    .ToArray()
            });
        }

        private sealed class ToolExecutionContext
        {
            private readonly IDictionary<string, ToolDefinition> _toolsById;
            private readonly IDictionary<string, ToolSafetyProfile> _safetyById;

            public ToolExecutionContext(
                IReadOnlyList<ToolDefinition> tools,
                IReadOnlyList<ToolDefinition> discoveryCatalog,
                AppSettings settings,
                ChatSession session,
                int maxExecutionSteps,
                IReadOnlyList<SkillDefinition> skillCatalog)
            {
                Tools = tools ?? new ToolDefinition[0];
                DiscoveryCatalog = discoveryCatalog ?? new ToolDefinition[0];
                Settings = settings;
                Session = session;
                SkillCatalog = skillCatalog;
                _toolsById = Tools
                    .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                    .ToDictionary(tool => tool.Id, StringComparer.OrdinalIgnoreCase);
                _safetyById = new Dictionary<string, ToolSafetyProfile>(StringComparer.OrdinalIgnoreCase);
                RemainingSteps = Math.Max(1, maxExecutionSteps);
            }

            public IReadOnlyList<ToolDefinition> Tools { get; private set; }

            public IReadOnlyList<ToolDefinition> DiscoveryCatalog { get; private set; }

            public AppSettings Settings { get; private set; }

            public ChatSession Session { get; private set; }

            public IReadOnlyList<SkillDefinition> SkillCatalog { get; private set; }

            public int RemainingSteps { get; private set; }

            public bool TryConsumeStep()
            {
                if (RemainingSteps <= 0) return false;
                RemainingSteps -= 1;
                return true;
            }

            public ToolDefinition Find(string toolId)
            {
                ToolDefinition tool;
                return !string.IsNullOrWhiteSpace(toolId) && _toolsById.TryGetValue(toolId, out tool)
                    ? tool
                    : null;
            }

            public ToolSafetyProfile Safety(ToolDefinition tool)
            {
                ToolSafetyProfile safety;
                if (!_safetyById.TryGetValue(tool.Id, out safety))
                {
                    safety = ToolSafetyPolicy.Resolve(tool, Tools);
                    _safetyById[tool.Id] = safety;
                }

                return safety;
            }
        }

        private enum ControllerExecutorKind
        {
            Skill,
            Native,
        }
    }
}
