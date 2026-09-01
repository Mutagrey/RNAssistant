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
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Domains.Word;
using RNAssistant.Office.Domains.PowerPoint;
using RNAssistant.Office.Domains.Outlook;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly IReadOnlyList<ToolCatalogEntry> _adapterTools;
        private readonly VbaToolExecutor _vbaExecutor;
        private readonly SkillAuthoringService _skillAuthoringService;
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
        private readonly IReadOnlyList<ToolCatalogEntry> _controllerTools;
        private readonly ISet<string> _controllerToolIds;
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
            _adapterTools = OfficeToolCatalog.ForHost(
                _adapter.HostName).ToArray();
            _controllerToolIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            _vbaExecutor = new VbaToolExecutor(adapter, vbaJournalStore);
            _capabilityCatalogService = new CapabilityCatalogService(
                adapter, skillStore);
            _toolAuthoringService = new ToolAuthoringService(
                adapter, toolStore, id => IsProtectedToolId(id));
            _skillAuthoringService = new SkillAuthoringService(
                adapter, skillStore, id => IsProtectedToolId(id) ||
                    toolStore != null && toolStore.Load().Any(tool =>
                        tool != null && string.Equals(tool.Id, id,
                            StringComparison.OrdinalIgnoreCase)));
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
            var controllerTools = new List<ToolCatalogEntry>();
            if (_vbaExecutor.HostSupportsVba())
                RegisterControllerTools(controllerTools,
                    VbaToolCatalog.GetTools());
            RegisterControllerTools(controllerTools,
                SkillAuthoringCatalog.GetTools(_skillAuthoringService));
            RegisterControllerTools(controllerTools,
                CapabilityToolCatalog.GetTools());
            RegisterControllerTools(controllerTools,
                ToolAuthoringCatalog.GetTools(_toolAuthoringService));
            RegisterControllerTools(controllerTools,
                PromptToolCatalog.GetTools(_promptSettingsService));
            RegisterControllerTools(controllerTools,
                ResourceToolCatalog.GetControllerTools());
            RegisterControllerTools(controllerTools,
                HtmlWorkspaceToolCatalog.GetTools(_htmlWorkspaceService));
            RegisterControllerTools(controllerTools,
                TaskListToolCatalog.GetTools());
            RegisterControllerTools(controllerTools,
                PlanDocumentToolCatalog.GetTools());
            RegisterControllerTools(controllerTools,
                UserQuestionToolCatalog.GetTools());
            _controllerTools = controllerTools.ToArray();
            var duplicate = _adapterTools.FirstOrDefault(tool =>
                tool != null && _controllerToolIds.Contains(
                    tool.Id ?? string.Empty));
            if (duplicate != null)
            {
                throw new InvalidOperationException("Duplicate built-in tool id: " + duplicate.Id);
            }
        }

        public IEnumerable<ToolCatalogEntry> GetControllerTools()
        {
            return _controllerTools;
        }

        internal IEnumerable<ToolCatalogEntry> GetHostTools()
        {
            return _adapterTools.Select(tool => tool.Clone()).ToArray();
        }

        internal ResourceGatewayService ResourceGateway { get { return _resourceGateway; } }

        internal NativeToolRuntimeAdapter CreateNativeRuntime(ChatSession session, ToolPackSnapshot snapshot,
            AppSettings settings, string mode, bool trace = true,
            Func<RNAssistant.Core.Tools.ToolExecutionContext,
                ToolPreparationResult, string> pendingRegistrar = null,
            IReadOnlyList<ToolCatalogEntry> discoveryCatalog = null,
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
                _toolAuthoringService, _skillAuthoringService,
                discoveryCatalog, skillCatalog,
                manualRun, dryRun, _hostRuntime,
                session, snapshot, settings, mode, pendingRegistrar, trace);
        }

        internal NativeToolRuntimeAdapter CreateNativeRuntime(ChatSession session, IEnumerable<ToolCatalogEntry> catalog,
            AppSettings settings, string mode, bool trace = true,
            Func<RNAssistant.Core.Tools.ToolExecutionContext,
                ToolPreparationResult, string> pendingRegistrar = null,
            IReadOnlyList<ToolCatalogEntry> discoveryCatalog = null,
            IReadOnlyList<SkillDefinition> skillCatalog = null,
            bool manualRun = false,
            bool dryRun = false)
        {
            var catalogList = (catalog ?? new ToolCatalogEntry[0]).ToArray();
            return CreateNativeRuntime(session,
                ToolPackSnapshotFactory.Capture(
                    mode, _adapter.HostName, catalogList),
                settings, mode, trace, pendingRegistrar,
                discoveryCatalog ?? catalogList, skillCatalog, manualRun,
                dryRun);
        }

        internal List<ToolCatalogEntry> AvailableConversationToolsForSession(
            IEnumerable<ToolCatalogEntry> tools,
            ChatSession session)
        {
            var source = (tools ?? new ToolCatalogEntry[0])
                .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Id))
                .ToList();
            if (OfficeDocumentExecutionGuardState.SessionMatchesAdapter(_adapter, session))
            {
                return source;
            }

            return source.Where(tool => !RequiresOfficeDocument(tool)).ToList();
        }

        public ToolRunResult ExecuteManual(ToolInvocation command,
            IReadOnlyList<ToolCatalogEntry> tools, AppSettings settings,
            bool dryRun, bool authorized,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteManual(command, tools, settings, dryRun,
                authorized, null, cancellationToken);
        }

        public ToolRunResult ExecuteManual(ToolInvocation command,
            IReadOnlyList<ToolCatalogEntry> tools, AppSettings settings,
            bool dryRun, bool authorized, ChatSession session,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteManual(
                command,
                tools,
                settings,
                dryRun,
                authorized,
                session,
                settings == null ? AppSettings.DefaultMaxAgentToolSteps : settings.MaxAgentToolSteps,
                cancellationToken);
        }

        public ToolRunResult ExecuteManual(ToolInvocation command,
            IReadOnlyList<ToolCatalogEntry> tools, AppSettings settings,
            bool dryRun, bool authorized, ChatSession session,
            int maxExecutionSteps,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteManual(command, tools, settings, dryRun,
                authorized, session, maxExecutionSteps, null,
                cancellationToken);
        }

        public ToolRunResult ExecuteManual(
            ToolInvocation command,
            IReadOnlyList<ToolCatalogEntry> tools,
            AppSettings settings,
            bool dryRun,
            bool authorized,
            ChatSession session,
            int maxExecutionSteps,
            IReadOnlyList<SkillDefinition> skillCatalog,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
                return ToolRunResult.Error("Tool command is empty.", null,
                    "invalid_tool_command", false);
            var known = KnownTools(tools);
            var matches = known.Where(candidate => candidate != null &&
                string.Equals(candidate.Id, command.ToolId,
                    StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) return UnknownTool(command.ToolId, known);
            var tool = matches[0];
            if (!tool.Enabled) return DisabledTool(command.ToolId, known);
            if (string.Equals(tool.Executor, "pipeline",
                StringComparison.OrdinalIgnoreCase))
                return ToolRunResult.Error(
                    "Pipelines are disabled during stabilization.", null,
                    "pipeline_disabled", false);
            if (!string.IsNullOrWhiteSpace(tool.CapabilityStatus) &&
                !string.Equals(tool.CapabilityStatus, "available",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tool.CapabilityStatus, "partial",
                    StringComparison.OrdinalIgnoreCase))
                return ToolRunResult.Error(
                    "Tool is unavailable: " + command.ToolId + ". " +
                        (tool.Limitations ?? tool.CapabilityStatus),
                    null, "tool_capability_unavailable", false);
            if (dryRun && tool.Policy != null &&
                tool.Policy.MayHaveSideEffects &&
                !VbaPackageToolHandler.IsDefinition(tool))
            {
                var validation = ValidateCommandArguments(command, tool);
                return validation ?? ToolRunResult.Ok(
                    "Dry run: would execute " + command.ToolId,
                    JsonConvert.SerializeObject(command.Arguments));
            }
            try
            {
                var runtimeSettings = settings ?? new AppSettings();
                if (authorized && !runtimeSettings.AutoConfirmToolActions)
                    runtimeSettings = new AppSettings
                    {
                        AutoConfirmToolActions = true
                    };
                return CreateNativeRuntime(
                        session,
                        new[] { tool },
                        runtimeSettings,
                        ManualMode(tool),
                        true,
                        (execution, preparation) =>
                            Guid.NewGuid().ToString("N"),
                        tools,
                        skillCatalog,
                        true,
                        dryRun)
                    .ExecuteManual(command,
                        Math.Max(1, maxExecutionSteps),
                        cancellationToken);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                return ToolRunResult.Error(
                    "Tool execution failed: " + command.ToolId + ". " +
                        DeepestMessage(ex),
                    null,
                    "tool_execution_exception",
                    false);
            }
        }

        private static string ManualMode(ToolCatalogEntry tool)
        {
            var modes = tool == null || tool.Policy == null
                ? null : tool.Policy.AllowedModes;
            if (modes != null && modes.Contains(ChatModes.Agent,
                    StringComparer.Ordinal))
                return ChatModes.Agent;
            if (modes != null && modes.Contains(ChatModes.Plan,
                    StringComparer.Ordinal))
                return ChatModes.Plan;
            if (modes != null && modes.Contains(ChatModes.Chat,
                    StringComparer.Ordinal))
                return ChatModes.Chat;
            return ChatModes.Agent;
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

        internal ToolRunResult ReadVbaProjectForEditor(ChatSession session)
        {
            return ExecuteLiveVbaEditorRead(
                session,
                () => ((IVbaResourceSource)_vbaExecutor).ListResourceModules());
        }

        internal ToolRunResult ReadVbaModuleForEditor(ChatSession session, string moduleName, int maxChars)
        {
            return ExecuteLiveVbaEditorRead(
                session,
                () => ((IVbaResourceSource)_vbaExecutor).ReadResourceModule(
                    session,
                    moduleName,
                    maxChars));
        }

        private ToolRunResult ExecuteLiveVbaEditorRead(ChatSession session, Func<ToolRunResult> action)
        {
            // An editor request is a separate operation, not a nested read from an
            // unrelated UI callback that happens to run on the same STA.
            try
            {
                return _hostRuntime.ExecuteForExpectedDocument(DocumentTarget(session), true, action);
            }
            catch (ResourceRequestException ex)
            {
                return ToolRunResult.Error(ex.Message, null, ex.ErrorCode, ex.Retryable);
            }
        }

        public ToolRunResult RunVbaMacro(
            string macroName,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var tool = VbaToolCatalog.GetTools().First(item =>
                string.Equals(item.Id, VbaToolCatalog.RunMacro,
                    StringComparison.Ordinal));
            var command = new ToolInvocation { ToolId = tool.Id };
            command.Arguments["macroName"] = macroName;
            return ExecuteManual(command, new[] { tool }, new AppSettings(),
                false, true, session, cancellationToken);
        }

        public ToolRunResult ValidateToolDefinition(ToolCatalogEntry tool)
        {
            var validation = _toolAuthoringService.ValidateDefinition(tool);
            return validation.Success
                ? ToolRunResult.Ok(validation.Message, validation.DataJson)
                : ToolRunResult.Error(validation.Message, validation.DataJson,
                    validation.ErrorCode, validation.Retryable);
        }

        internal SkillManualMutationResult ExecuteSkillLibraryMutation(
            SkillLibraryCoreMutation mutation)
        {
            return _skillAuthoringService.ExecuteManualCoreMutation(mutation);
        }

        internal ToolManualMutationResult ExecuteToolLibraryMutation(
            ToolLibraryCoreMutation mutation)
        {
            return _toolAuthoringService.ExecuteManualCoreMutation(mutation);
        }

        internal SkillReferenceReadResult ReadSkillLibraryReference(
            string skillId, string path, string expectedRevision)
        {
            return _skillAuthoringService.ReadManualReference(
                skillId, path, expectedRevision);
        }

        internal SkillManualMutationResult ExecuteSkillLibraryReferenceMutation(
            string kind, string skillId, string path, string content,
            string expectedRevision)
        {
            return _skillAuthoringService.ExecuteManualReferenceMutation(
                kind, skillId, path, content, expectedRevision);
        }

        internal bool RequiresSessionLeaseForManualRun(
            string toolId,
            IEnumerable<ToolCatalogEntry> tools)
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

        private static ToolRunResult ValidateCommandArguments(
            ToolInvocation command, ToolCatalogEntry tool)
        {
            JObject schema;
            string schemaError;
            if (!ToolSchemaSupport.TryParse(tool, out schema, out schemaError))
            {
                return ToolRunResult.Error(schemaError, null,
                    "invalid_tool_schema", false);
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
                return ToolRunResult.Error(
                    "Tool arguments are invalid: " + ex.Message, null,
                    "invalid_arguments", true);
            }

            string argumentError;
            if (!ToolSchemaSupport.ValidateArguments(arguments, schema, true, out argumentError))
            {
                return ToolRunResult.Error(argumentError, null,
                    "invalid_arguments", true);
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

        private static string DeepestMessage(Exception exception)
        {
            var current = exception;
            while (current != null && current.InnerException != null)
            {
                current = current.InnerException;
            }
            return current == null ? "Unknown error." : current.Message;
        }

        private void RegisterControllerTools(
            ICollection<ToolCatalogEntry> target,
            IEnumerable<ToolCatalogEntry> tools)
        {
            foreach (var tool in tools ?? new ToolCatalogEntry[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id))
                {
                    continue;
                }

                if (_controllerToolIds.Contains(tool.Id))
                {
                    throw new InvalidOperationException("Duplicate controller tool id: " + tool.Id);
                }

                _controllerToolIds.Add(tool.Id);
                target.Add(tool);
            }
        }

        private bool RequiresOfficeDocument(ToolCatalogEntry tool)
        {
            var id = tool.Id;
            if (string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase)) return false;
            if (_adapterTools.Any(candidate => candidate != null &&
                string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))) return true;

            if (_controllerToolIds.Contains(id))
            {
                return VbaToolCatalog.Owns(id) ||
                    HtmlWorkspaceToolCatalog.RequiresOfficeDocument(id);
            }
            return true;
        }

        private IReadOnlyList<ToolCatalogEntry> KnownTools(IEnumerable<ToolCatalogEntry> providedTools)
        {
            var result = new List<ToolCatalogEntry>();
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
                 _controllerToolIds.Contains(id));
        }

        private static void AddTools(ICollection<ToolCatalogEntry> result, ISet<string> seen, IEnumerable<ToolCatalogEntry> tools)
        {
            foreach (var tool in tools ?? new ToolCatalogEntry[0])
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Id) || seen.Contains(tool.Id))
                {
                    continue;
                }

                seen.Add(tool.Id);
                result.Add(tool);
            }
        }

        private static ToolRunResult UnknownTool(string requestedToolId,
            IReadOnlyList<ToolCatalogEntry> knownTools)
        {
            var suggestions = ToolIdSuggester.Suggest(requestedToolId, knownTools, 5);
            var message = "Unknown tool id: " + requestedToolId + ". Use only available tool ids.";
            if (suggestions.Count > 0)
            {
                message += " Did you mean: " + string.Join(", ", suggestions.ToArray()) + "?";
            }

            return ToolRunResult.Error(message,
                ToolDiagnosticJson(requestedToolId, knownTools,
                    suggestions, false),
                "unknown_tool", true);
        }

        private static ToolRunResult DisabledTool(string requestedToolId,
            IReadOnlyList<ToolCatalogEntry> knownTools)
        {
            return ToolRunResult.Error(
                "Tool is disabled: " + requestedToolId + ". Enable it or use another available tool id.",
                ToolDiagnosticJson(requestedToolId, knownTools, new List<string>(), true),
                "tool_disabled",
                false);
        }

        private static string ToolDiagnosticJson(string requestedToolId, IReadOnlyList<ToolCatalogEntry> knownTools, IReadOnlyList<string> suggestions, bool disabled)
        {
            return JsonConvert.SerializeObject(new
            {
                requestedToolId = requestedToolId,
                disabled = disabled,
                suggestions = suggestions ?? new string[0],
                availableToolIds = (knownTools ?? new ToolCatalogEntry[0])
                    .Where(tool => tool != null && tool.Enabled && !string.IsNullOrWhiteSpace(tool.Id))
                    .Select(tool => tool.Id)
                    .ToArray()
            });
        }

    }
}
