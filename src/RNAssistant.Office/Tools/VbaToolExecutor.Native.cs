using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Vba;
using RNAssistant.Office.Vba;

namespace RNAssistant.Office.Tools
{
    internal sealed partial class VbaToolExecutor
    {
        internal VbaNativePreparation PrepareNativeTool(
            string toolId,
            IDictionary<string, object> arguments,
            ToolExecutionContext execution,
            RNAssistant.Core.Models.ChatSession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VbaToolCatalog.Owns(toolId))
                return VbaNativePreparation.Failed(
                    VbaNativeOutcome.Error("Unsupported VBA tool: " + toolId,
                        "unknown_tool", false));

            var correlation = MutationCorrelation(execution, session);
            var state = new VbaNativePreparedState
            {
                Version = 1,
                ToolId = toolId,
                ArgumentsSha256 = TextPatternEngine.Sha256(
                    execution == null || execution.Call == null
                        ? string.Empty : execution.Call.ArgumentsJson)
            };

            if (string.Equals(toolId, VbaToolCatalog.RunMacro,
                StringComparison.Ordinal))
            {
                var macroName = ToolArgumentReader.String(
                    arguments, "macroName", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(macroName))
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.Error("macroName is required.",
                            "vba_macro_name_required", true));
                JArray macroArguments;
                var argumentError = ReadMacroArguments(
                    arguments, out macroArguments);
                if (argumentError != null)
                    return VbaNativePreparation.Failed(argumentError);
                return VbaNativePreparation.Ready(
                    VbaNativeOutcome.Ok(
                        "Confirmation required before running Office macro " +
                            macroName + ".",
                        new JObject
                        {
                            ["macroName"] = macroName,
                            ["argumentCount"] = macroArguments.Count
                        }),
                    state);
            }

            var moduleName = ToolArgumentReader.String(
                arguments, "moduleName", string.Empty);
            VbaMutationOutcome preview;
            if (string.Equals(toolId, VbaToolCatalog.ApplyPatch,
                StringComparison.Ordinal))
            {
                var prepared = _mutationService.PrepareApplyPatchGuard(
                    new VbaApplyPatchGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = correlation
                    });
                if (!prepared.Success)
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.From(prepared.Error));
                state.ModuleName = prepared.ResolvedModuleName;
                state.Guard = prepared.Guard;
                preview = ExecutePreparedMutation(
                    toolId, arguments, state, correlation, true,
                    cancellationToken);
            }
            else if (string.Equals(toolId, VbaToolCatalog.DeleteModule,
                StringComparison.Ordinal))
            {
                var prepared = _mutationService.PrepareDeleteModuleGuard(
                    new VbaDeleteModuleGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = correlation
                    });
                if (!prepared.Success)
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.From(prepared.Error));
                state.ModuleName = prepared.ResolvedModuleName;
                state.Guard = prepared.Guard;
                preview = ExecutePreparedMutation(
                    toolId, arguments, state, correlation, true,
                    cancellationToken);
            }
            else if (string.Equals(toolId, VbaToolCatalog.RenameModule,
                StringComparison.Ordinal))
            {
                var prepared = _mutationService.PrepareRenameGuard(
                    new VbaRenameGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        RequestedTargetModuleName = ToolArgumentReader.String(
                            arguments, "newModuleName", string.Empty),
                        Correlation = correlation
                    });
                if (!prepared.Success)
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.From(prepared.Error));
                state.ModuleName = prepared.ResolvedModuleName;
                state.TargetModuleName = prepared.ResolvedTargetModuleName;
                state.Guard = prepared.Guard;
                preview = ExecutePreparedMutation(
                    toolId, arguments, state, correlation, true,
                    cancellationToken);
            }
            else if (string.Equals(toolId, VbaToolCatalog.WriteModule,
                StringComparison.Ordinal))
            {
                var prepared = _mutationService.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = correlation
                    });
                if (!prepared.Success)
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.From(prepared.Error));
                state.ModuleName = prepared.ResolvedModuleName;
                state.Guard = prepared.Guard;
                preview = ExecutePreparedMutation(
                    toolId, arguments, state, correlation, true,
                    cancellationToken);
            }
            else
            {
                string backupId;
                var selectorError = ResolveRestoreIntent(
                    arguments, out backupId, out moduleName);
                if (selectorError != null)
                    return VbaNativePreparation.Failed(selectorError);
                var prepared = _mutationService.PrepareRestoreGuard(
                    new VbaRestoreGuardRequest
                    {
                        BackupId = backupId,
                        ModuleName = moduleName,
                        Correlation = correlation
                    });
                if (!prepared.Success)
                    return VbaNativePreparation.Failed(
                        VbaNativeOutcome.From(prepared.Error));
                state.BackupId = prepared.BackupId;
                state.ModuleName = prepared.ModuleName;
                state.RestoreGuard = prepared.Guard;
                preview = ExecutePreparedMutation(
                    toolId, arguments, state, correlation, true,
                    cancellationToken);
            }

            var outcome = VbaNativeOutcome.From(preview);
            return outcome.Status == VbaNativeOutcomeStatus.Ok
                ? VbaNativePreparation.Ready(outcome, state)
                : VbaNativePreparation.Failed(outcome);
        }

        internal VbaNativeOutcome ExecuteNativeTool(
            string toolId,
            IDictionary<string, object> arguments,
            ToolExecutionContext execution,
            RNAssistant.Core.Models.ChatSession session,
            string preparedStateJson,
            Action markDispatchPossible,
            CancellationToken cancellationToken)
        {
            VbaNativePreparedState state;
            try
            {
                state = JsonConvert.DeserializeObject<VbaNativePreparedState>(
                    preparedStateJson ?? string.Empty,
                    new JsonSerializerSettings
                    {
                        DateParseHandling = DateParseHandling.None,
                        MissingMemberHandling = MissingMemberHandling.Error
                    });
            }
            catch (JsonException ex)
            {
                return VbaNativeOutcome.Error(
                    "Prepared VBA state is invalid: " + ex.Message,
                    "vba_prepared_state_invalid", false);
            }
            if (state == null || state.Version != 1 ||
                !string.Equals(state.ToolId, toolId, StringComparison.Ordinal) ||
                execution == null || execution.Call == null ||
                !string.Equals(state.ArgumentsSha256,
                    TextPatternEngine.Sha256(execution.Call.ArgumentsJson),
                    StringComparison.OrdinalIgnoreCase))
            {
                return VbaNativeOutcome.Error(
                    "Prepared VBA state does not match the accepted tool call.",
                    "vba_prepared_state_mismatch", false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(toolId, VbaToolCatalog.RunMacro,
                StringComparison.Ordinal))
            {
                var macroName = ToolArgumentReader.String(
                    arguments, "macroName", string.Empty).Trim();
                JArray macroArguments;
                var error = ReadMacroArguments(arguments, out macroArguments);
                if (error != null) return error;
                using (_dispatchBoundary.Bind(markDispatchPossible))
                {
                    _dispatchBoundary.Mark();
                    return VbaNativeOutcome.From(_backend.RunMacro(
                        new VbaRunMacroRequest
                        {
                            MacroName = macroName,
                            Arguments = MacroArguments(macroArguments)
                        }));
                }
            }

            var reconciliation = ReconcilePendingMutationOutcome();
            if (reconciliation != null)
                return VbaNativeOutcome.From(reconciliation);
            using (_dispatchBoundary.Bind(markDispatchPossible))
            {
                return VbaNativeOutcome.From(ExecutePreparedMutation(
                    toolId, arguments, state,
                    MutationCorrelation(execution, session),
                    false, cancellationToken));
            }
        }

        private VbaMutationOutcome ExecutePreparedMutation(
            string toolId,
            IDictionary<string, object> arguments,
            VbaNativePreparedState state,
            VbaMutationCorrelation correlation,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            if (string.Equals(toolId, VbaToolCatalog.RestoreBackup,
                StringComparison.Ordinal))
                return _mutationService.RestoreBackup(new VbaRestoreRequest
                {
                    BackupId = state.BackupId,
                    ModuleName = state.ModuleName,
                    DryRun = dryRun,
                    Guard = state.RestoreGuard,
                    Correlation = correlation
                }, cancellationToken);
            if (string.Equals(toolId, VbaToolCatalog.ApplyPatch,
                StringComparison.Ordinal))
            {
                object patch;
                arguments.TryGetValue("patch", out patch);
                return _mutationService.ApplyPatch(new VbaApplyPatchRequest
                {
                    RequestedModuleName = state.ModuleName,
                    Operations = ParsePatchOperations(patch as JArray),
                    DryRun = dryRun,
                    Guard = state.Guard,
                    Correlation = correlation
                }, cancellationToken);
            }
            if (string.Equals(toolId, VbaToolCatalog.DeleteModule,
                StringComparison.Ordinal))
                return _mutationService.DeleteModule(
                    new RNAssistant.Office.Vba.VbaDeleteModuleRequest
                {
                    ModuleName = state.ModuleName,
                    DryRun = dryRun,
                    Guard = state.Guard,
                    Correlation = correlation
                }, cancellationToken);

            if (string.Equals(toolId, VbaToolCatalog.RenameModule,
                StringComparison.Ordinal))
                return _mutationService.RenameModule(new VbaRenameRequest
                {
                    ModuleName = state.ModuleName,
                    NewModuleName = state.TargetModuleName,
                    DryRun = dryRun,
                    Guard = state.Guard,
                    Correlation = correlation
                }, cancellationToken);
            var mode = ToolArgumentReader.String(arguments, "mode", "upsert");
            return _mutationService.WriteWholeModule(
                new VbaWholeModuleWriteRequest
                {
                    ModuleName = state.ModuleName,
                    Code = ToolArgumentReader.String(
                        arguments, "code", string.Empty),
                    ComponentType = ToolArgumentReader.String(
                        arguments, "componentType", "StdModule"),
                    Mode = WholeModuleWriteMode(mode),
                    DryRun = dryRun,
                    Guard = state.Guard,
                    Correlation = correlation
                }, cancellationToken);
        }

        internal IReadOnlyList<RNAssistant.Core.Models.ResourceMutationReadBack> CaptureMutationReadBack(
            RNAssistant.Core.Models.ChatSession session, string preparedStateJson)
        {
            var state = JsonConvert.DeserializeObject<VbaNativePreparedState>(preparedStateJson);
            var result = new List<RNAssistant.Core.Models.ResourceMutationReadBack>();
            if (state == null || state.ToolId == VbaToolCatalog.RunMacro) return result;
            var names = new[] { state.ModuleName, state.TargetModuleName, state.RestoreGuard?.ModuleName }
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);
            return CaptureModules(session, names);
        }

        internal IReadOnlyList<RNAssistant.Core.Models.ResourceMutationReadBack> CaptureModules(
            RNAssistant.Core.Models.ChatSession session, IEnumerable<string> names)
        {
            var result = new List<RNAssistant.Core.Models.ResourceMutationReadBack>();
            var reader = new VbaMutationHostReader(_reader);
            foreach (var name in names)
            {
                var identity = RNAssistant.Office.Services.VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, name);
                var read = reader.ReadModule(name, 1000000);
                if (read == null) continue;
                if (read.IsNotFound) result.Add(new RNAssistant.Core.Models.ResourceMutationReadBack(identity, false));
                else if (read.Success && read.Module != null && !read.Module.Truncated)
                {
                    var payload = RNAssistant.Core.Models.PayloadRef.FromBlob(_vbaJournalStore.Payloads.StoreText(
                        read.Module.Code, "text/plain; charset=utf-8"));
                    result.Add(new RNAssistant.Core.Models.ResourceMutationReadBack(identity, true,
                        RNAssistant.Core.Models.ResourceRepresentations.Source,
                        VbaTextCanonicalizer.LiveCodeSha256(read.Module.Code), payload));
                }
            }
            return result;
        }

        private VbaMutationCorrelation MutationCorrelation(
            ToolExecutionContext execution,
            RNAssistant.Core.Models.ChatSession session)
        {
            return new VbaMutationCorrelation
            {
                SessionId = session == null ? string.Empty : session.Id,
                DocumentAuthorityId = session == null ? null : session.DocumentAuthorityId,
                Authority = _authority.Store.CaptureMany(new[] { _authority.Scope(session, true) }),
                Evidence = session == null ? new RNAssistant.Core.Models.ResourceEvidence[0] :
                    session.Messages.SelectMany(message => message.ResourceEvidence ??
                        new List<RNAssistant.Core.Models.ResourceEvidence>()).ToArray(),
                ExpectedContentSha256 = execution == null ? null : execution.ExpectedContentSha256,
                ObserveExternalDrift = module => _authority.ReportExternalDrift(_authority.Scope(session, true),
                    RNAssistant.Office.Services.VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, module)),
                RunId = execution == null ? session?.LastRun?.RunId : execution.RunId,
                TurnId = execution == null ? session?.LastRun?.TurnId : execution.TurnId,
                StepId = execution == null ? null : execution.StepId,
                ToolCallId = execution == null || execution.Call == null
                    ? null : execution.Call.Id
            };
        }

        private static VbaNativeOutcome ReadMacroArguments(
            IDictionary<string, object> arguments,
            out JArray values)
        {
            values = new JArray();
            object raw;
            if (arguments == null || !arguments.TryGetValue("arguments", out raw) || raw == null)
                return null;
            values = raw as JArray;
            return values == null
                ? VbaNativeOutcome.Error(
                    "Macro arguments must be a native JSON array.",
                    "vba_macro_arguments_invalid", true)
                : null;
        }

        private static IReadOnlyList<object> MacroArguments(JArray arguments)
        {
            return (arguments ?? new JArray()).Select(item =>
            {
                if (item == null || item.Type == JTokenType.Null ||
                    item.Type == JTokenType.Undefined) return null;
                var value = item as JValue;
                return value == null ? (object)item.ToString(Formatting.None) :
                    value.Value;
            }).ToArray();
        }
    }

    internal enum VbaNativeOutcomeStatus { Ok, Error, Unknown }

    internal sealed class VbaNativeOutcome
    {
        internal VbaNativeOutcomeStatus Status { get; private set; }
        internal string Message { get; private set; }
        internal string DataJson { get; private set; }

        private VbaNativeOutcome(VbaNativeOutcomeStatus status,
            string message, JObject data)
        {
            Status = status;
            Message = message ?? string.Empty;
            DataJson = data == null || !data.HasValues
                ? null : data.ToString(Formatting.None);
        }

        internal static VbaNativeOutcome Ok(string message, JObject data = null)
        {
            return new VbaNativeOutcome(
                VbaNativeOutcomeStatus.Ok, message, data);
        }

        internal static VbaNativeOutcome Error(string message,
            string code, bool? retryable, JObject data = null)
        {
            return new VbaNativeOutcome(VbaNativeOutcomeStatus.Error,
                message, ErrorData(data, code, retryable));
        }

        internal static VbaNativeOutcome From(VbaMutationOutcome outcome)
        {
            if (outcome == null)
                return Error("VBA mutation returned no typed outcome.",
                    "vba_mutation_missing_outcome", false);
            if (outcome.Status == VbaMutationOutcomeStatus.Ok)
                return Ok(outcome.Message, outcome.Data);
            return new VbaNativeOutcome(
                outcome.Status == VbaMutationOutcomeStatus.Unknown
                    ? VbaNativeOutcomeStatus.Unknown
                    : VbaNativeOutcomeStatus.Error,
                outcome.Message,
                ErrorData(outcome.Data,
                    string.IsNullOrWhiteSpace(outcome.ErrorCode)
                        ? outcome.Status == VbaMutationOutcomeStatus.Unknown
                            ? "vba_mutation_unknown" : "vba_mutation_failed"
                        : outcome.ErrorCode,
                    outcome.Retryable));
        }

        internal static VbaNativeOutcome From(VbaBackendActionResult outcome)
        {
            if (outcome == null)
                return Error("VBA backend returned no typed outcome.",
                    "vba_backend_missing_result", false);
            if (outcome.Status == VbaBackendActionStatus.Ok)
                return Ok(outcome.Message, outcome.Data);
            return new VbaNativeOutcome(
                outcome.Status == VbaBackendActionStatus.Unknown
                    ? VbaNativeOutcomeStatus.Unknown
                    : VbaNativeOutcomeStatus.Error,
                outcome.Message,
                ErrorData(outcome.Data, outcome.ErrorCode,
                    outcome.Retryable));
        }

        private static JObject ErrorData(JObject data, string code,
            bool? retryable)
        {
            var result = data == null
                ? new JObject() : (JObject)data.DeepClone();
            result["code"] = string.IsNullOrWhiteSpace(code)
                ? "vba_operation_failed" : code;
            if (retryable.HasValue) result["retryable"] = retryable.Value;
            return result;
        }
    }

    internal sealed class VbaNativePreparation
    {
        internal VbaNativeOutcome Outcome { get; private set; }
        internal string StateJson { get; private set; }

        private VbaNativePreparation(VbaNativeOutcome outcome,
            string stateJson)
        {
            Outcome = outcome;
            StateJson = stateJson;
        }

        internal static VbaNativePreparation Ready(
            VbaNativeOutcome outcome, VbaNativePreparedState state)
        {
            return new VbaNativePreparation(outcome,
                JsonConvert.SerializeObject(state, Formatting.None));
        }

        internal static VbaNativePreparation Failed(VbaNativeOutcome outcome)
        {
            return new VbaNativePreparation(outcome, null);
        }
    }

    internal sealed class VbaNativePreparedState
    {
        public int Version { get; set; }
        public string ToolId { get; set; }
        public string ArgumentsSha256 { get; set; }
        public string ModuleName { get; set; }
        public string TargetModuleName { get; set; }
        public string BackupId { get; set; }
        public VbaMutationGuard Guard { get; set; }
        public VbaRestoreGuard RestoreGuard { get; set; }
    }
}
