using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;

namespace RNAssistant.Office.Tools
{
    public sealed class OfficeToolExecutor
    {
        private static readonly TimeSpan MutationLockTimeout = TimeSpan.FromSeconds(10);

        private readonly IOfficeApplicationAdapter _adapter;
        private readonly IReadOnlyList<ToolDefinition> _adapterTools;
        private readonly PipelineToolExecutor _pipelineExecutor;
        private readonly VbaToolExecutor _vbaExecutor;
        private readonly SkillToolExecutor _skillExecutor;
        private readonly ToolAuthoringExecutor _toolAuthoringExecutor;
        private readonly PromptToolExecutor _promptToolExecutor;
        private readonly HtmlArtifactToolExecutor _htmlArtifactExecutor;
        private readonly PlanToolExecutor _planToolExecutor;
        private readonly IReadOnlyList<ToolDefinition> _controllerTools;
        private readonly IDictionary<string, ControllerExecutorKind> _controllerExecutors;
        private readonly string _mutationLockDirectory;
        private readonly object _fallbackMutationGate = new object();

        public OfficeToolExecutor(
            IOfficeApplicationAdapter adapter,
            VbaBackupStore vbaBackupStore,
            SkillStore skillStore,
            ToolStore toolStore = null,
            Func<AppSettings> loadSettings = null,
            Action<AppSettings> saveSettings = null,
            AppDataPaths paths = null)
        {
            _adapter = adapter;
            _adapterTools = (_adapter.GetBuiltInTools() ?? new ToolDefinition[0]).ToArray();
            _pipelineExecutor = new PipelineToolExecutor();
            _vbaExecutor = new VbaToolExecutor(adapter, vbaBackupStore);
            _skillExecutor = new SkillToolExecutor(adapter, skillStore);
            _toolAuthoringExecutor = new ToolAuthoringExecutor(adapter, toolStore);
            _promptToolExecutor = new PromptToolExecutor(loadSettings, saveSettings);
            _htmlArtifactExecutor = new HtmlArtifactToolExecutor(_adapter, _adapterTools);
            _planToolExecutor = new PlanToolExecutor();
            _mutationLockDirectory = paths == null ? null : Path.Combine(paths.Root, "locks");
            var controllerTools = new List<ToolDefinition>();
            _controllerExecutors = new Dictionary<string, ControllerExecutorKind>(StringComparer.OrdinalIgnoreCase);
            RegisterControllerTools(controllerTools, _vbaExecutor.GetControllerTools(), ControllerExecutorKind.Vba);
            RegisterControllerTools(controllerTools, _skillExecutor.GetControllerTools(), ControllerExecutorKind.Skill);
            RegisterControllerTools(controllerTools, _toolAuthoringExecutor.GetControllerTools(), ControllerExecutorKind.ToolAuthoring);
            RegisterControllerTools(controllerTools, _promptToolExecutor.GetControllerTools(), ControllerExecutorKind.Prompt);
            RegisterControllerTools(controllerTools, _htmlArtifactExecutor.GetControllerTools(), ControllerExecutorKind.HtmlArtifact);
            RegisterControllerTools(controllerTools, _planToolExecutor.GetControllerTools(), ControllerExecutorKind.Plan);
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
            var context = new ToolExecutionContext(KnownTools(tools), settings ?? new AppSettings(), session, maxExecutionSteps, skillCatalog);
            var initialSteps = context.RemainingSteps;
            var result = ExecuteForExpectedDocument(
                session,
                () => ExecuteCommandSafely(command, context, 0, dryRun, manualRun, cancellationToken));
            if (result != null) result.ToolStepsConsumed = initialSteps - context.RemainingSteps;
            return result;
        }

        private ToolResult ExecuteForExpectedDocument(ChatSession session, Func<ToolResult> action)
        {
            var runtimeDocumentKey = session == null || session.LastRun == null
                ? string.Empty
                : session.LastRun.DocumentRuntimeKey;
            var expectation = session == null ||
                string.IsNullOrWhiteSpace(session.Host) ||
                (string.IsNullOrWhiteSpace(session.DocumentKey) && string.IsNullOrWhiteSpace(runtimeDocumentKey))
                ? null
                : new OfficeDocumentExecutionExpectation
                {
                    Host = session.Host,
                    DocumentKey = session.DocumentKey,
                    RuntimeDocumentKey = runtimeDocumentKey
                };
            var documentGuard = _adapter as IOfficeDocumentExecutionGuard;
            if (expectation != null && documentGuard == null)
            {
                var mismatch = OfficeDocumentExecutionGuardState.Validate(_adapter, expectation);
                if (mismatch != null) return mismatch;
            }

            using (documentGuard == null || expectation == null
                ? null
                : documentGuard.BeginExpectedDocument(
                    expectation.Host,
                    expectation.DocumentKey,
                    expectation.RuntimeDocumentKey))
            {
                return action();
            }
        }

        public string VbaToolId(string suffix)
        {
            return _vbaExecutor.ToolId(suffix);
        }

        public string VbaBackendToolId(string suffix)
        {
            return _vbaExecutor.BackendToolId(suffix);
        }

        public void ObserveVbaHash(ChatSession session, string moduleName, string codeSha256)
        {
            _vbaExecutor.ObserveExpectedHash(session, moduleName, codeSha256);
        }

        public ToolResult ValidateToolDefinition(ToolDefinition tool)
        {
            var validation = ToolAuthoringExecutor.ValidateToolDefinition(tool);
            return validation.Success && IsProtectedToolId(tool == null ? null : tool.Id)
                ? ReservedToolId(tool.Id)
                : validation;
        }

        public ToolResult InstallVbaTool(
            ToolDefinition tool,
            bool dryRun,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (dryRun)
            {
                return ExecuteForExpectedDocument(session, () => _vbaExecutor.InstallCustomTool(tool, false, true));
            }
            return ExecuteDirectMutation(
                session,
                false,
                true,
                cancellationToken,
                () => _vbaExecutor.InstallCustomTool(tool, false, false));
        }

        public ToolResult RemoveVbaTool(
            ToolDefinition tool,
            ChatSession session = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteDirectMutation(
                session,
                false,
                true,
                cancellationToken,
                () => _vbaExecutor.RemoveCustomTool(tool));
        }

        public string GetVbaInstallationStatus(ToolDefinition tool)
        {
            return _vbaExecutor.GetInstallationStatus(tool);
        }

        private ToolResult ExecuteCommandSafely(ToolCommand command, ToolExecutionContext context, int depth, bool dryRun, bool manualRun, CancellationToken cancellationToken)
        {
            try
            {
                return ExecuteCommand(command, context, depth, dryRun, manualRun, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MutationLockException ex)
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

        private ToolResult ExecuteCommand(ToolCommand command, ToolExecutionContext context, int depth, bool dryRun, bool manualRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == null || string.IsNullOrWhiteSpace(command.ToolId))
            {
                return ToolResult.Fail("Tool command is empty.");
            }

            NormalizeCompatibleCommand(command);

            if (depth > 8)
            {
                return ToolResult.Fail("Pipeline nesting limit exceeded.");
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

            var reservedIdResult = ValidateAuthoredToolId(command);
            if (reservedIdResult != null)
            {
                return reservedIdResult;
            }

            ControllerExecutorKind controllerKind;
            var isController = _controllerExecutors.TryGetValue(command.ToolId, out controllerKind);
            var isVbaController = isController &&
                controllerKind == ControllerExecutorKind.Vba;
            if (isVbaController)
            {
                var preparation = _vbaExecutor.PrepareControllerTool(command, context.Session);
                if (preparation != null) return preparation;
            }

            if (ToolSafetyPolicy.RequiresConfirmation(tool, safety, context.Settings, dryRun, manualRun))
            {
                ToolResult preview = null;
                if (isVbaController)
                {
                    preview = _vbaExecutor.PreviewPreparedControllerTool(command, context.Session, cancellationToken);
                    if (preview != null && !preview.Success) return preview;
                }
                else if (isController)
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
                if (preview != null && isVbaController) waiting.DataJson = preview.DataJson;
                return waiting;
            }

            if (!context.TryConsumeStep())
            {
                return ToolResult.Fail("Tool execution budget exceeded (including nested pipeline steps).", null, "tool_step_limit_exceeded", false);
            }

            if (depth == 0 && !dryRun && (safety.MutatesDocument || safety.MutatesLocalState))
            {
                return ExecuteMutation(
                    context.Session,
                    safety.MutatesLocalState && !string.Equals(tool.Scope, "session", StringComparison.OrdinalIgnoreCase),
                    safety.MutatesDocument,
                    cancellationToken,
                    () => ExecuteResolvedCommand(command, context, depth, dryRun, manualRun, cancellationToken, customTool));
            }

            return ExecuteResolvedCommand(command, context, depth, dryRun, manualRun, cancellationToken, customTool);
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
                CoerceStructuredStrings(arguments, schema);
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

        private static void CoerceStructuredStrings(JObject arguments, JObject schema)
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
                    !string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = value.Value<string>();
                    bool boolean;
                    long integer;
                    double number;
                    if (string.Equals(type, "boolean", StringComparison.OrdinalIgnoreCase) && bool.TryParse(raw, out boolean)) arguments[property.Name] = boolean;
                    else if (string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase) && long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out integer)) arguments[property.Name] = integer;
                    else if (string.Equals(type, "number", StringComparison.OrdinalIgnoreCase) && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)) arguments[property.Name] = number;
                    continue;
                }
                try
                {
                    arguments[property.Name] = JToken.Parse(value.Value<string>());
                }
                catch (JsonException ex)
                {
                    throw new JsonException(
                        "$." + property.Name + " must be a native JSON " + type +
                        ", not quoted/stringified JSON. The supplied string could not be parsed: " + ex.Message,
                        ex);
                }
            }
        }

        private ToolResult ExecuteResolvedCommand(ToolCommand command, ToolExecutionContext context, int depth, bool dryRun, bool manualRun, CancellationToken cancellationToken, ToolDefinition customTool)
        {

            if (customTool != null && string.Equals(customTool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase))
            {
                return _pipelineExecutor.Execute(
                    customTool,
                    command,
                    depth + 1,
                    dryRun,
                    manualRun,
                    (nested, nestedDepth, nestedDryRun, nestedManualRun, nestedCancellationToken) =>
                        ExecuteCommandSafely(nested, context, nestedDepth, nestedDryRun, nestedManualRun, nestedCancellationToken),
                    cancellationToken);
            }

            if (customTool != null && string.Equals(customTool.Executor, "vba", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _vbaExecutor.ExecuteCustomTool(customTool, command, context.Settings, dryRun, manualRun);
            }

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

            if (string.Equals(command.ToolId, VbaBackendToolId("vba_replace_module"), StringComparison.OrdinalIgnoreCase))
            {
                var backupError = _vbaExecutor.PrepareBackupBeforeReplace(command, context.Session);
                if (backupError != null)
                {
                    return backupError;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _adapter.ExecuteTool(command);
        }

        private static void EnterMutationGate(object gate, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.Add(MutationLockTimeout);
            while (!Monitor.TryEnter(gate, 100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new MutationLockException(
                        "Another RNAssistant action is still changing the same state. Retry after it finishes.",
                        true);
                }
            }
        }

        private string DocumentMutationKey(ChatSession session)
        {
            return session == null
                ? (_adapter.HostName + "|" + _adapter.DocumentKey)
                : ((session.Host ?? _adapter.HostName) + "|" + (session.DocumentKey ?? _adapter.DocumentKey));
        }

        private IDisposable AcquireMutationFileLock(string lockName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_mutationLockDirectory) || string.IsNullOrWhiteSpace(lockName)) return null;
            try
            {
                Directory.CreateDirectory(_mutationLockDirectory);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            catch (IOException ex)
            {
                throw new MutationLockException("RNAssistant cannot access its mutation lock directory.", false, ex);
            }
            var path = Path.Combine(_mutationLockDirectory, lockName + ".lck");
            var deadline = DateTime.UtcNow.Add(MutationLockTimeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    if (DateTime.UtcNow >= deadline)
                    {
                        throw new MutationLockException(
                            "Another RNAssistant action is still changing the same state. Retry after it finishes.",
                            true,
                            ex);
                    }
                    if (cancellationToken.WaitHandle.WaitOne(100)) cancellationToken.ThrowIfCancellationRequested();
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new MutationLockException("RNAssistant cannot acquire its mutation lock.", false, ex);
                }
            }
        }

        private ToolResult ExecuteDirectMutation(
            ChatSession session,
            bool mutatesSharedLocalState,
            bool mutatesDocument,
            CancellationToken cancellationToken,
            Func<ToolResult> action)
        {
            try
            {
                return ExecuteForExpectedDocument(
                    session,
                    () => ExecuteMutation(session, mutatesSharedLocalState, mutatesDocument, cancellationToken, action));
            }
            catch (MutationLockException ex)
            {
                return MutationLockFailure(ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(
                    "VBA package mutation failed. " + DeepestMessage(ex) +
                        " The document effect may have been applied; inspect state before retrying.",
                    null,
                    "tool_effect_uncertain",
                    false);
            }
        }

        private ToolResult ExecuteMutation(
            ChatSession session,
            bool mutatesSharedLocalState,
            bool mutatesDocument,
            CancellationToken cancellationToken,
            Func<ToolResult> action)
        {
            var actionStarted = false;
            try
            {
                if (!mutatesSharedLocalState && !mutatesDocument)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionStarted = true;
                    return action();
                }
                if (string.IsNullOrWhiteSpace(_mutationLockDirectory))
                {
                    EnterMutationGate(_fallbackMutationGate, cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        actionStarted = true;
                        return action();
                    }
                    finally
                    {
                        Monitor.Exit(_fallbackMutationGate);
                    }
                }

                using (AcquireMutationFileLock(mutatesSharedLocalState ? "local_state" : null, cancellationToken))
                using (AcquireMutationFileLock(
                    mutatesDocument ? "document_" + AppDataPaths.SafeFileName(DocumentMutationKey(session)) : null,
                    cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actionStarted = true;
                    return action();
                }
            }
            catch (OperationCanceledException) when (actionStarted)
            {
                return ToolResult.Fail(
                    "Cancellation was observed after mutation execution started. The external effect may have been applied; inspect state before retrying.",
                    null,
                    "tool_effect_uncertain",
                    false);
            }
        }

        private static ToolResult MutationLockFailure(MutationLockException exception)
        {
            return ToolResult.Fail(
                exception.Message,
                null,
                exception.Retryable ? "tool_mutation_busy" : "tool_mutation_lock_unavailable",
                exception.Retryable);
        }

        private sealed class MutationLockException : InvalidOperationException
        {
            public MutationLockException(string message, bool retryable, Exception innerException = null)
                : base(message, innerException)
            {
                Retryable = retryable;
            }

            public bool Retryable { get; private set; }
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
                case ControllerExecutorKind.Vba:
                    return _vbaExecutor.ExecuteControllerTool(command, dryRun, context.Session, cancellationToken);
                case ControllerExecutorKind.Skill:
                    return _skillExecutor.ExecuteControllerTool(command, context.Settings, dryRun, manualRun, context.SkillCatalog);
                case ControllerExecutorKind.ToolAuthoring:
                    return _toolAuthoringExecutor.ExecuteControllerTool(command, context.Settings, dryRun, manualRun);
                case ControllerExecutorKind.Prompt:
                    return _promptToolExecutor.ExecuteControllerTool(command, dryRun);
                case ControllerExecutorKind.HtmlArtifact:
                    return _htmlArtifactExecutor.ExecuteControllerTool(command, context.Session, dryRun, cancellationToken);
                case ControllerExecutorKind.Plan:
                    return _planToolExecutor.ExecuteControllerTool(command, context.Session, dryRun);
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

        internal string CanonicalizeVbaToolId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            var hostPrefix = (_adapter.HostName ?? string.Empty).ToLowerInvariant() + ".";
            var candidate = id.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase)
                ? "common." + id.Substring(hostPrefix.Length)
                : id;
            candidate = VbaPublicToolIds.Canonicalize(candidate);
            ControllerExecutorKind executor;
            return _controllerExecutors.TryGetValue(candidate, out executor) && executor == ControllerExecutorKind.Vba
                ? candidate
                : id;
        }

        internal string CanonicalizeToolId(string id)
        {
            return BuiltInToolAliases.Canonicalize(CanonicalizeVbaToolId(id));
        }

        internal void CanonicalizePipeline(ToolDefinition tool)
        {
            if (tool == null || !string.Equals(tool.Executor, "pipeline", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(tool.PipelineJson)) return;
            try
            {
                var pipeline = JObject.Parse(tool.PipelineJson);
                var changed = false;
                foreach (var step in (pipeline["steps"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var id = (string)step["toolId"];
                    var command = new ToolCommand { ToolId = id };
                    ToolArgumentNormalizer.AddProperties(step["arguments"] as JObject ?? new JObject(), command.Arguments);
                    NormalizeCompatibleCommand(command);
                    var normalizedArguments = JObject.FromObject(command.Arguments);
                    if (!string.Equals(id, command.ToolId, StringComparison.Ordinal) ||
                        !JToken.DeepEquals(step["arguments"] as JObject ?? new JObject(), normalizedArguments))
                    {
                        step["toolId"] = command.ToolId;
                        step["arguments"] = normalizedArguments;
                        changed = true;
                    }
                }
                if (changed) tool.PipelineJson = pipeline.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                // Definition validation reports malformed pipelines; compatibility rewriting stays best-effort.
            }
            catch (InvalidOperationException)
            {
                // Conflicting legacy arguments are reported when the pipeline step executes.
            }
        }

        private void NormalizeCompatibleCommand(ToolCommand command)
        {
            if (command == null) return;
            var requestedToolId = command.ToolId;
            command.ToolId = CanonicalizeVbaToolId(command.ToolId);
            _vbaExecutor.NormalizeLegacyArguments(command, requestedToolId);
            command.ToolId = BuiltInToolAliases.Canonicalize(command.ToolId);
            BuiltInToolAliases.NormalizeCommand(command, requestedToolId);
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
            var canonicalId = CanonicalizeToolId(id);
            return !string.IsNullOrWhiteSpace(id) &&
                (_adapterTools.Any(tool => tool != null && string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                 _controllerExecutors.ContainsKey(id) ||
                 _controllerExecutors.ContainsKey(canonicalId ?? string.Empty) ||
                 BuiltInToolAliases.IsLegacyAlias(id) ||
                 _vbaExecutor.IsInternalToolId(id));
        }

        private ToolResult ValidateAuthoredToolId(ToolCommand command)
        {
            if (command == null ||
                (!string.Equals(command.ToolId, "common.tools_validate", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(command.ToolId, "common.tools_upsert", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var id = ToolArgumentReader.String(command.Arguments, "id", string.Empty);
            return IsProtectedToolId(id) ? ReservedToolId(id) : null;
        }

        private static ToolResult ReservedToolId(string id)
        {
            return ToolResult.Fail("Tool id is reserved by a built-in tool: " + id, null, "reserved_tool_id", false);
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
                RuntimeGuardJson = command == null ? null : command.RuntimeGuardJson
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
                AppSettings settings,
                ChatSession session,
                int maxExecutionSteps,
                IReadOnlyList<SkillDefinition> skillCatalog)
            {
                Tools = tools ?? new ToolDefinition[0];
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
            Vba,
            Skill,
            ToolAuthoring,
            Prompt,
            HtmlArtifact,
            Plan
        }
    }
}
