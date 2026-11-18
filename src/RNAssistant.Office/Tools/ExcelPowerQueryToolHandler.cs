using System;
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
    internal sealed class ExcelPowerQueryToolHandler : IPreparableToolHandler, IManagedMutationToolHandler, IReadOnlyToolHandler
    {
        internal const int NativeWaitMilliseconds = 30000;
        private readonly string _id;
        private readonly ResourceGatewayService _gateway;
        private readonly HostRuntime _host;
        private readonly ChatSession _session;

        internal ExcelPowerQueryToolHandler(string id, ResourceGatewayService gateway, HostRuntime host, ChatSession session)
        {
            if (!ExcelPowerQueryToolCatalog.Owns(id)) throw new ArgumentException("Unknown Power Query operation.", nameof(id));
            _id = id; _gateway = gateway; _host = host; _session = session;
        }

        public Task<ToolPreparationResult> PrepareAsync(ToolHandlerContext context, CancellationToken token)
        {
            if (_id == ExcelPowerQueryToolCatalog.Validate)
                return Task.FromResult(new ToolPreparationResult(Result.Ok("Read-only Power Query validation."), "{}"));
            try
            {
                var prepared = _host.ReadDocument(ExcelPowerQueryToolSupport.Target(_session), token, () => Prepare(context));
                return Task.FromResult(new ToolPreparationResult(Result.Ok("Power Query operation prepared for the exact bound definition."),
                    JsonConvert.SerializeObject(prepared)));
            }
            catch (OfficeDocumentGuardException ex) { return OfficeToolFailure.GuardPreparation(ex); }
            catch (HostRuntime.MutationLockException ex) { return OfficeToolFailure.LockPreparation(ex); }
            catch (Exception ex) when (ex is ExcelPowerQueryBackendException || ex is ResourceRequestException || ex is InvalidDataException)
            { return Task.FromResult(new ToolPreparationResult(ExcelPowerQueryToolSupport.Error(ex), recovery: FailureRecovery(ex))); }
        }

        private ExcelPowerQueryToolPrepared Prepare(ToolHandlerContext context)
        {
            var backend = ExcelPowerQueryToolSupport.Backend(_gateway);
            var capabilities = backend.GetCapabilities();
            if (capabilities?.Supported != true)
                throw new ResourceRequestException(capabilities?.UnavailableReason ?? "Power Query is unavailable.", "excel_query_unavailable", false);
            var prepared = new ExcelPowerQueryToolPrepared { Operation = _id,
                ArgumentsHash = TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson) };
            if (_id == ExcelPowerQueryToolCatalog.Create)
            {
                if (!capabilities.CreateDefinition) throw new ResourceRequestException("Creating Power Query definitions is unavailable.", "excel_query_create_unavailable", false);
                var name = ExcelResourceProvider.ValidateQueryName(ToolArgumentReader.String(context.Arguments, "name", string.Empty));
                var formula = ToolArgumentReader.String(context.Arguments, "source", string.Empty);
                ExcelPowerQueryToolSupport.ValidateFormula(formula);
                RequireQueryAbsent(backend, name);
                prepared.QueryName = name;
                prepared.SourceHash = TextPatternEngine.Sha256(formula);
                return prepared;
            }
            var queryName = ExcelResourceProvider.QueryNameFromTarget(ToolArgumentReader.String(context.Arguments, "target", string.Empty));
            var query = backend.ReadQuery(queryName);
            if (query == null || query.QueryName != queryName || query.Formula == null || string.IsNullOrEmpty(query.QueryIdentity))
                throw new ResourceRequestException("The exact query definition is unavailable.", "excel_query_unavailable", false);
            var bindings = backend.ReadBindings(queryName);
            if (bindings == null || !bindings.IsComplete || bindings.IsAmbiguous || bindings.QueryIdentity != query.QueryIdentity)
                throw new ResourceRequestException("Power Query bindings are incomplete or ambiguous; read bindings before selecting a load.", "excel_query_bindings_ambiguous", false);
            prepared.QueryName = queryName;
            prepared.SourceHash = TextPatternEngine.Sha256(query.Formula);
            if (_id == ExcelPowerQueryToolCatalog.Refresh)
            {
                if (!capabilities.WorksheetRefresh) throw new ResourceRequestException("Worksheet refresh is unavailable.", "excel_query_refresh_unavailable", false);
                var selected = SelectLoad(bindings, ToolArgumentReader.String(context.Arguments, "table", null));
                prepared.Refresh = new ExcelPowerQueryRefreshRequest {
                    QueryName = queryName, ExpectedQueryIdentity = query.QueryIdentity,
                    ExpectedSourceVersion = query.SourceVersion, ExpectedBindingVersion = bindings.BindingVersion,
                    Sheet = selected.Sheet, Table = selected.Table
                };
            }
            else
            {
                if (!capabilities.WorksheetLoad) throw new ResourceRequestException("Creating a worksheet load is unavailable.", "excel_query_load_unavailable", false);
                prepared.Load = new ExcelPowerQueryLoadRequest {
                    QueryName = queryName, ExpectedQueryIdentity = query.QueryIdentity,
                    ExpectedSourceVersion = query.SourceVersion, ExpectedBindingVersion = bindings.BindingVersion,
                    Sheet = ToolArgumentReader.String(context.Arguments, "sheet", string.Empty),
                    Address = ToolArgumentReader.String(context.Arguments, "address", string.Empty),
                    Table = ToolArgumentReader.String(context.Arguments, "table", string.Empty),
                    CreateSheet = ToolArgumentReader.Boolean(context.Arguments, "createSheet", false)
                };
            }
            return prepared;
        }

        public async Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken token)
        {
            try
            {
                if (_id == ExcelPowerQueryToolCatalog.Validate)
                {
                    var name = ExcelResourceProvider.QueryNameFromTarget(ToolArgumentReader.String(context.Arguments, "target", string.Empty));
                    if (ToolArgumentReader.String(context.Arguments, "kind", "source") == "source")
                        return await ExcelPowerQuerySyntaxService.ValidateAsync(_gateway, _host, _session, name, token).ConfigureAwait(false);
                    return _host.ReadDocument(ExcelPowerQueryToolSupport.Target(_session), token, () => {
                        var backend = ExcelPowerQueryToolSupport.Backend(_gateway);
                        backend.ReadQuery(name); // Require the exact definition; this never evaluates M.
                        if (ToolArgumentReader.String(context.Arguments, "kind", "source") == "loadedData")
                        {
                            var checks = new PowerQueryDataCheckRequest {
                                Table = ToolArgumentReader.String(context.Arguments, "table", null),
                                ExpectedRows = context.Arguments.ContainsKey("expectedRows") ? (int?)ToolArgumentReader.Int32(context.Arguments, "expectedRows", 0) : null,
                                RequiredColumns = StringList(context, "requiredColumns"), NonEmptyColumns = StringList(context, "nonEmptyColumns"),
                                KeyColumns = StringList(context, "keyColumns")
                            };
                            var checkedData = _gateway.PowerQueryProvider.ValidateQueryData(_session, name, checks, token);
                            return new ToolHandlerResult(Result.Ok("Loaded worksheet data observed. Report each check and its coverage; no refresh or M execution was performed.",
                                JsonConvert.SerializeObject(checkedData)), ToolEffectEvidence.None);
                        }
                        throw new ResourceRequestException("Unknown Power Query validation kind.", "excel_query_validation_kind_invalid", false);
                    });
                }
                var prepared = JsonConvert.DeserializeObject<ExcelPowerQueryToolPrepared>(context.PreparedStateJson ?? "null");
                if (prepared == null || prepared.Operation != _id ||
                    prepared.ArgumentsHash != TextPatternEngine.Sha256(context.Execution.Call.ArgumentsJson))
                    throw new InvalidDataException("Prepared Power Query operation does not match the accepted call.");
                if (_id == ExcelPowerQueryToolCatalog.Create)
                    return _host.ExecuteDocumentMutation(ExcelPowerQueryToolSupport.Target(_session), token,
                        () => Create(context, prepared, token), result => context.Complete(result), context.CompleteFailure);
                var service = ExcelPowerQueryExecutionService.For(_gateway.PowerQueryAdapter);
                if (_id == ExcelPowerQueryToolCatalog.Refresh)
                {
                    if (prepared.Refresh == null) throw new InvalidDataException("Missing prepared Power Query refresh.");
                    return await service.RefreshAsync(_host, _session, _gateway, context, prepared.Refresh,
                        NativeWaitMilliseconds, token).ConfigureAwait(false);
                }
                if (prepared.Load == null) throw new InvalidDataException("Missing prepared Power Query load.");
                return await service.LoadAsync(_host, _session, _gateway, context, prepared.Load,
                    NativeWaitMilliseconds, token).ConfigureAwait(false);
            }
            catch (OfficeDocumentGuardException ex) when (!context.MayHaveDispatched) { return await OfficeToolFailure.Guard(ex).ConfigureAwait(false); }
            catch (HostRuntime.MutationLockException ex) when (!context.MayHaveDispatched) { return await OfficeToolFailure.Lock(ex).ConfigureAwait(false); }
            catch (Exception ex) when (ex is ExcelPowerQueryBackendException || ex is ExcelReadBackendException || ex is ResourceRequestException || ex is InvalidDataException)
            {
                return new ToolHandlerResult(context.MayHaveDispatched ? Result.Unknown(ex.Message) : ExcelPowerQueryToolSupport.Error(ex),
                    context.MayHaveDispatched ? ToolEffectEvidence.Unknown : ToolEffectEvidence.None,
                    recovery: context.MayHaveDispatched ? null : FailureRecovery(ex));
            }
        }

        private ToolHandlerResult Create(ToolHandlerContext context, ExcelPowerQueryToolPrepared prepared, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var backend = ExcelPowerQueryToolSupport.Backend(_gateway);
            RequireQueryAbsent(backend, prepared.QueryName);
            var formula = ToolArgumentReader.String(context.Arguments, "source", string.Empty);
            if (TextPatternEngine.Sha256(formula) != prepared.SourceHash) throw new InvalidDataException("Prepared M source changed.");
            Exception failure = null;
            try { backend.CreateDefinition(new ExcelPowerQueryCreateRequest { QueryName = prepared.QueryName, Formula = formula,
                Description = ToolArgumentReader.String(context.Arguments, "description", string.Empty) }, context.MarkDispatchPossible); }
            catch (Exception ex) { if (!context.MayHaveDispatched) throw; failure = ex; }
            ExcelPowerQuerySnapshot after = null;
            try { after = backend.ReadQuery(prepared.QueryName); }
            catch (Exception ex) { if (failure == null) failure = ex; }
            if (after != null && after.QueryName == prepared.QueryName && after.Formula == formula)
                return new ToolHandlerResult(Result.Ok("Power Query definition created and exact M read back. No load or refresh was created.",
                    JsonConvert.SerializeObject(new ExcelPowerQuerySourceResult { Target = ExcelResourceProvider.QueryPrefix + after.QueryName,
                        SourceSaved = true, Changed = true, NativeWriteWarning = failure?.Message })), ToolEffectEvidence.VerifiedChange,
                    resourceReadBack: ExcelPowerQueryToolSupport.SourceReadBack(_gateway, _session, after));
            return new ToolHandlerResult(Result.Unknown("Power Query creation may have taken effect; exact requested source could not be confirmed. Inspect the query before another create.",
                JsonConvert.SerializeObject(new ExcelPowerQuerySourceResult { Target = ExcelResourceProvider.QueryPrefix + prepared.QueryName,
                    SourceSaved = false, NativeWriteWarning = failure?.Message })), ToolEffectEvidence.Unknown,
                resourceReadBack: after == null ? null : ExcelPowerQueryToolSupport.SourceReadBack(_gateway, _session, after),
                recovery: new ToolRecoveryContract(ToolFailureKind.ToolDefect, ToolRetryPolicy.None,
                    ExcelResourceProvider.QueryIdentity(_session.DocumentAuthorityId, prepared.QueryName), ResourceRepresentations.Source,
                    ExcelResourceProvider.QueryPrefix + prepared.QueryName, readScope: ToolRecoveryReadScope.Whole));
        }

        internal static ExcelPowerQueryLoadBinding SelectLoad(ExcelPowerQueryBindingsSnapshot bindings, string table)
        {
            var loads = (bindings?.Loads ?? new ExcelPowerQueryLoadBinding[0])
                .Where(load => string.IsNullOrEmpty(table) || string.Equals(load.Table, table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bindings == null || !bindings.IsComplete || bindings.IsAmbiguous || loads.Length != 1)
                throw new ResourceRequestException(loads.Length == 0 ? "No matching worksheet load exists; create one explicitly with excel.query.load." :
                    "Select one exact table from the query bindings.", loads.Length == 0 ? "excel_query_load_required" : "excel_query_load_ambiguous", false);
            return loads[0];
        }

        private static void RequireQueryAbsent(IExcelPowerQueryBackend backend, string name)
        {
            try { backend.ReadQuery(name); }
            catch (ExcelPowerQueryBackendException ex) when (ex.ErrorCode == "excel_query_not_found") { return; }
            throw new ResourceRequestException("A Power Query with this exact name already exists. Read and patch its source instead.", "excel_query_exists", false);
        }

        private static ToolRecoveryContract FailureRecovery(Exception error)
        {
            var code = (error as ExcelPowerQueryBackendException)?.ErrorCode ?? (error as ResourceRequestException)?.ErrorCode;
            return code == "excel_query_busy" ? new ToolRecoveryContract(ToolFailureKind.BusyNoEffect, ToolRetryPolicy.RetryLater) :
                new ToolRecoveryContract(ToolFailureKind.RejectedNoEffect, ToolRetryPolicy.Replan);
        }

        private static string[] StringList(ToolHandlerContext context, string key)
        {
            object value;
            var values = context.Arguments.TryGetValue(key, out value) && value != null
                ? JsonConvert.DeserializeObject<string[]>(JsonConvert.SerializeObject(value)) ?? new string[0] : new string[0];
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new InvalidDataException(key + " must contain each exact heading at most once.");
            return values;
        }
    }

    internal sealed class ExcelPowerQueryToolPrepared
    {
        public string Operation { get; set; }
        public string ArgumentsHash { get; set; }
        public string QueryName { get; set; }
        public string SourceHash { get; set; }
        public ExcelPowerQueryRefreshRequest Refresh { get; set; }
        public ExcelPowerQueryLoadRequest Load { get; set; }
    }
}
