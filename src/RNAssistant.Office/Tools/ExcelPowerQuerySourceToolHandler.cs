using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using Result = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    // Private owner of core.patch/write. It is deliberately not another public M write tool.
    internal sealed class ExcelPowerQuerySourceToolHandler : IPreparableToolHandler, IManagedMutationToolHandler
    {
        internal const string Operation = "excel.query.source.write";
        private readonly ResourceGatewayService _gateway;
        private readonly HostRuntime _host;
        private readonly ChatSession _session;
        private readonly string _observedSourceHash;

        internal ExcelPowerQuerySourceToolHandler(ResourceGatewayService gateway, HostRuntime host,
            ChatSession session, string observedSourceHash)
        { _gateway = gateway; _host = host; _session = session; _observedSourceHash = observedSourceHash; }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var state = _host.ReadDocument(ExcelPowerQueryToolSupport.Target(_session), cancellationToken, () => {
                    var backend = ExcelPowerQueryToolSupport.Backend(_gateway);
                    var name = ExcelResourceProvider.ValidateQueryName(ToolArgumentReader.String(context.Arguments, "queryName", string.Empty));
                    var formula = ToolArgumentReader.String(context.Arguments, "content", string.Empty);
                    ExcelPowerQueryToolSupport.ValidateFormula(formula);
                    var before = backend.ReadQuery(name);
                    if (before == null || before.Formula == null || before.QueryName != name ||
                        string.IsNullOrEmpty(before.QueryIdentity) || _observedSourceHash == null ||
                        TextPatternEngine.Sha256(before.Formula) != _observedSourceHash)
                        throw new ResourceRequestException("Power Query source changed; read the current source before editing.", "source_changed", false);
                    var bindings = backend.ReadBindings(name);
                    if (bindings == null || bindings.QueryName != name || bindings.QueryIdentity != before.QueryIdentity ||
                        string.IsNullOrEmpty(bindings.BindingVersion))
                        throw new ResourceRequestException("Power Query binding identity changed during preparation.", "source_changed", false);
                    return new ExcelPowerQuerySourcePrepared {
                        QueryName = name, QueryIdentity = before.QueryIdentity,
                        BeforeHash = _observedSourceHash, AfterHash = TextPatternEngine.Sha256(formula), BindingVersion = bindings.BindingVersion,
                        AffectedTables = (bindings.Loads ?? new ExcelPowerQueryLoadBinding[0]).Select(load => load.Table)
                            .Where(table => !string.IsNullOrWhiteSpace(table)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        BindingsComplete = bindings.IsComplete && !bindings.IsAmbiguous
                    };
                });
                return Task.FromResult(new ToolPreparationResult(Result.Ok("Power Query source change prepared; no refresh is performed."),
                    JsonConvert.SerializeObject(state)));
            }
            catch (OfficeDocumentGuardException ex) { return OfficeToolFailure.GuardPreparation(ex); }
            catch (HostRuntime.MutationLockException ex) { return OfficeToolFailure.LockPreparation(ex); }
            catch (Exception ex) when (ex is ExcelPowerQueryBackendException || ex is ResourceRequestException || ex is InvalidDataException)
            { return Task.FromResult(new ToolPreparationResult(ExcelPowerQueryToolSupport.Error(ex),
                recovery: ExcelPowerQueryToolSupport.SourceRecovery(_session, context.Arguments, false, ex))); }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var state = JsonConvert.DeserializeObject<ExcelPowerQuerySourcePrepared>(context.PreparedStateJson ?? "null");
                if (state == null || state.QueryName != ToolArgumentReader.String(context.Arguments, "queryName", string.Empty) ||
                    state.BeforeHash != _observedSourceHash || state.AfterHash != TextPatternEngine.Sha256(ToolArgumentReader.String(context.Arguments, "content", string.Empty)))
                    throw new InvalidDataException("Prepared Power Query source does not match the accepted edit.");
                return Task.FromResult(_host.ExecuteDocumentMutation(ExcelPowerQueryToolSupport.Target(_session), cancellationToken,
                    () => WriteSource(context, state, cancellationToken), result => context.Complete(result), context.CompleteFailure));
            }
            catch (OfficeDocumentGuardException ex) when (!context.MayHaveDispatched) { return OfficeToolFailure.Guard(ex); }
            catch (HostRuntime.MutationLockException ex) when (!context.MayHaveDispatched) { return OfficeToolFailure.Lock(ex); }
            catch (Exception ex) when (ex is ExcelPowerQueryBackendException || ex is ResourceRequestException || ex is InvalidDataException)
            {
                return Task.FromResult(new ToolHandlerResult(context.MayHaveDispatched ? Result.Unknown(ex.Message) : ExcelPowerQueryToolSupport.Error(ex),
                    context.MayHaveDispatched ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None,
                    recovery: ExcelPowerQueryToolSupport.SourceRecovery(_session, context.Arguments, context.MayHaveDispatched, ex)));
            }
        }

        private ToolHandlerResult WriteSource(ToolHandlerContext context, ExcelPowerQuerySourcePrepared state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var backend = ExcelPowerQueryToolSupport.Backend(_gateway);
            var before = backend.ReadQuery(state.QueryName);
            if (before == null || before.QueryName != state.QueryName || before.QueryIdentity != state.QueryIdentity || before.Formula == null ||
                TextPatternEngine.Sha256(before.Formula) != state.BeforeHash)
                throw new ResourceRequestException("Power Query definition or native identity changed after preparation.", "source_changed", false);
            var bindings = backend.ReadBindings(state.QueryName);
            if (bindings == null || bindings.QueryName != state.QueryName || bindings.QueryIdentity != state.QueryIdentity ||
                string.IsNullOrEmpty(state.BindingVersion) || bindings.BindingVersion != state.BindingVersion ||
                (bindings.IsComplete && !bindings.IsAmbiguous) != state.BindingsComplete)
                throw new ResourceRequestException("Power Query load bindings changed after the affected resources were prepared. Read source and bindings before editing again.",
                    "source_changed", false);
            var formula = ToolArgumentReader.String(context.Arguments, "content", string.Empty);
            if (before.Formula == formula)
                return new ToolHandlerResult(Result.Ok("Power Query source is unchanged. No refresh was performed.",
                    JsonConvert.SerializeObject(new ExcelPowerQuerySourceResult { Target = ExcelResourceProvider.QueryPrefix + state.QueryName, SourceSaved = true, Changed = false })),
                    ToolEffectEvidence.VerifiedNoChange,
                    resourceReadBack: ExcelPowerQueryToolSupport.SourceReadBack(_gateway, _session, before));

            Exception writeFailure = null;
            try
            {
                backend.WriteSource(new ExcelPowerQueryWriteRequest {
                    QueryName = state.QueryName, ExpectedQueryIdentity = state.QueryIdentity,
                    ExpectedFormula = before.Formula, Formula = formula
                }, context.MarkDispatchPossible);
            }
            catch (Exception ex) { if (!context.MayHaveDispatched) throw; writeFailure = ex; }

            ExcelPowerQuerySnapshot after = null;
            Exception readFailure = null;
            try { after = backend.ReadQuery(state.QueryName); }
            catch (Exception ex) { readFailure = ex; }
            if (after != null && after.QueryName == state.QueryName && after.QueryIdentity == state.QueryIdentity && after.Formula == formula)
                return new ToolHandlerResult(Result.Ok("Power Query M source saved and read back exactly. Data was not refreshed; syntax and data validity are not proven.",
                    JsonConvert.SerializeObject(new ExcelPowerQuerySourceResult { Target = ExcelResourceProvider.QueryPrefix + state.QueryName, SourceSaved = true, Changed = true,
                        NativeWriteWarning = writeFailure == null ? null : writeFailure.Message })),
                    ToolEffectEvidence.VerifiedChange,
                    resourceReadBack: ExcelPowerQueryToolSupport.SourceReadBack(_gateway, _session, after));

            var detail = after == null ? "Power Query source read-back is unavailable." :
                after.QueryIdentity != state.QueryIdentity ? "Power Query native identity changed during the write." :
                "The observed Power Query source differs from the requested source.";
            return new ToolHandlerResult(Result.Unknown(detail, JsonConvert.SerializeObject(new ExcelPowerQuerySourceResult {
                Target = ExcelResourceProvider.QueryPrefix + state.QueryName, SourceSaved = false,
                NativeWriteWarning = writeFailure?.Message ?? readFailure?.Message })), ToolEffectEvidence.Unknown,
                resourceReadBack: after != null && after.QueryName == state.QueryName && after.QueryIdentity == state.QueryIdentity
                    ? ExcelPowerQueryToolSupport.SourceReadBack(_gateway, _session, after) : null,
                recovery: ExcelPowerQueryToolSupport.SourceRecovery(_session, context.Arguments, true));
        }
    }

    internal sealed class ExcelPowerQuerySourcePrepared
    {
        public string QueryName { get; set; }
        public string QueryIdentity { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string BindingVersion { get; set; }
        public string[] AffectedTables { get; set; }
        public bool BindingsComplete { get; set; }
    }

    internal sealed class ExcelPowerQuerySourceResult
    {
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("sourceSaved")] public bool SourceSaved { get; set; }
        [JsonProperty("changed")] public bool? Changed { get; set; }
        [JsonProperty("refreshPerformed")] public bool RefreshPerformed { get { return false; } }
        [JsonProperty("syntax")] public string Syntax { get { return "not_checked"; } }
        [JsonProperty("nativeWriteWarning", NullValueHandling = NullValueHandling.Ignore)] public string NativeWriteWarning { get; set; }
    }

    internal static class ExcelPowerQueryToolSupport
    {
        internal static OfficeDocumentExecutionExpectation Target(ChatSession session)
        {
            if (session == null) throw new ResourceRequestException("An exact Excel document session is required.", "resource_session_required", false);
            return new OfficeDocumentExecutionExpectation { Host = session.Host, DocumentKey = session.DocumentKey,
                RuntimeDocumentKey = session.LastRun?.DocumentRuntimeKey ?? string.Empty };
        }

        internal static IExcelPowerQueryBackend Backend(ResourceGatewayService gateway)
        { return gateway?.PowerQueryBackend ?? throw new ResourceRequestException("Power Query is unavailable in this bound session.", "excel_query_unavailable", false); }

        internal static void ValidateFormula(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula) || formula.Length > ExcelResourceProvider.MaximumQuerySourceCharacters)
                throw new InvalidDataException("M source must be nonempty and at most one million characters.");
        }

        internal static Result Error(Exception error)
        {
            var code = error is ExcelPowerQueryBackendException backend ? backend.ErrorCode :
                error is ExcelReadBackendException read ? read.ErrorCode :
                error is ResourceRequestException resource ? resource.ErrorCode : "excel_query_invalid";
            return Result.Error(error.Message, JsonConvert.SerializeObject(new ExcelPowerQueryError { Code = code }));
        }

        internal static ToolRecoveryContract SourceRecovery(ChatSession session, IDictionary<string, object> args, bool unknown, Exception error = null)
        {
            var code = (error as ExcelPowerQueryBackendException)?.ErrorCode ?? (error as ResourceRequestException)?.ErrorCode;
            if (!unknown && code == "excel_query_busy")
                return new ToolRecoveryContract(ToolFailureKind.BusyNoEffect, ToolRetryPolicy.RetryLater);
            if (!unknown && code != "source_changed" && code != "excel_query_source_changed")
                return new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan);
            var name = ToolArgumentReader.String(args, "queryName", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) name = ToolArgumentReader.String(args, "name", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan);
            try { ExcelResourceProvider.ValidateQueryName(name); }
            catch (ResourceRequestException) { return new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan); }
            return new ToolRecoveryContract(unknown ? ToolFailureKind.ToolDefect : ToolFailureKind.ConflictNoEffect,
                unknown ? ToolRetryPolicy.None : ToolRetryPolicy.RefreshRequired,
                ExcelResourceProvider.QueryIdentity(session.DocumentAuthorityId, name), ResourceRepresentations.Source,
                ExcelResourceProvider.QueryPrefix + name, readScope: ToolRecoveryReadScope.Whole);
        }

        internal static IReadOnlyList<ResourceMutationReadBack> SourceReadBack(ResourceGatewayService gateway,
            ChatSession session, ExcelPowerQuerySnapshot query)
        {
            if (query?.Formula == null) return new ResourceMutationReadBack[0];
            var payloads = gateway?.Authority?.Payloads;
            var payload = payloads == null ? null : PayloadRef.FromBlob(payloads.StoreText(query.Formula, "text/x-powerquery; charset=utf-8"));
            return new[] { new ResourceMutationReadBack(ExcelResourceProvider.QueryIdentity(session.DocumentAuthorityId, query.QueryName),
                true, ResourceRepresentations.Source, TextPatternEngine.Sha256(query.Formula), payload) };
        }

        private sealed class ExcelPowerQueryError { [JsonProperty("code")] public string Code { get; set; } }
    }
}
