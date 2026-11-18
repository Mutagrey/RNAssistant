using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelPowerQueryExecutionService
    {
        internal PowerQueryOperationStatus ReadStatus(string queryName)
        {
            queryName = ExcelResourceProvider.ValidateQueryName(queryName);
            lock (_sync) return Snapshot(_operations.LastOrDefault(item => item.Query == queryName), queryName);
        }

        internal ToolHandlerResult ReadStatusTool(ResourceGatewayService gateway, ChatSession session,
            string queryName, int maximumCharacters)
        {
            RequireSession(session);
            queryName = ExcelResourceProvider.ValidateQueryName(queryName);
            lock (_sync)
            {
                var entries = _operations.Where(item => item.Query == queryName &&
                    item.DocumentAuthorityId == session.DocumentAuthorityId).ToArray();
                var latest = entries.LastOrDefault();
                var snapshot = Snapshot(latest, queryName);
                var selection = RetainStatus(gateway, session, snapshot, maximumCharacters);
                var evidence = StatusEvidence(gateway, session, selection.Result, entries);
                var projection = ResourceReadProjection.From(selection.Result,
                    ExcelResourceProvider.QueryPrefix + queryName, "Excel query", "document");
                return new ToolHandlerResult(ToolResult.OkRead(
                    "Complete local operation-status observation. Native timestamps are preserved; this does not read or validate worksheet values.",
                    projection, selection.ResourceRefs), ToolEffectEvidence.None, resourceEvidence: evidence);
            }
        }

        internal ResourceReadSelection ReadStatusSelection(ResourceGatewayService gateway, ChatSession session,
            string queryName, int maximumCharacters)
        {
            RequireSession(session);
            queryName = ExcelResourceProvider.ValidateQueryName(queryName);
            lock (_sync)
            {
                var latest = _operations.LastOrDefault(item => item.Query == queryName &&
                    item.DocumentAuthorityId == session.DocumentAuthorityId);
                return RetainStatus(gateway, session, Snapshot(latest, queryName), maximumCharacters);
            }
        }

        private ToolHandlerResult ResultFor(Operation operation, ResourceGatewayService gateway,
            ChatSession session, bool verified, string message)
        {
            var snapshot = Snapshot(operation, operation.Query);
            var selection = RetainStatus(gateway, session, snapshot, ResourceReadRequest.MaximumCharacters);
            var observations = StatusEvidence(gateway, session, selection.Result, new[] { operation });
            var text = JsonConvert.SerializeObject(snapshot);
            var result = verified ? ToolResult.Ok(message, text, selection.ResourceRefs)
                : ToolResult.Unknown(message, text, selection.ResourceRefs);
            var view = selection.Result;
            var readBack = verified ? new[] { new ResourceMutationReadBack(view.Resource.Reference.Identity,
                true, "status", view.ContentSha256, view.Payload, ResourceCoverage.Whole(), revision: view.Resource.Reference) }
                : new ResourceMutationReadBack[0];
            return new ToolHandlerResult(result, verified ? ToolEffectEvidence.VerifiedChange : ToolEffectEvidence.Unknown,
                resourceEvidence: observations, resourceReadBack: readBack,
                recovery: verified ? null : new ToolRecoveryContract(ToolFailureKind.ToolDefect, ToolRetryPolicy.None,
                    operation.Reference.StatusResource, "status", ExcelResourceProvider.QueryPrefix + operation.Query,
                    readScope: ToolRecoveryReadScope.Whole));
        }

        private static ResourceReadSelection RetainStatus(ResourceGatewayService gateway, ChatSession session,
            PowerQueryOperationStatus status, int maximumCharacters)
        {
            var text = JsonConvert.SerializeObject(status);
            if (text.Length > maximumCharacters)
                throw new ResourceRequestException("Power Query status exceeds the configured read budget.", "resource_read_budget_exceeded", false);
            var descriptor = new ResourceDescriptor {
                Reference = new ResourceRef(ExcelResourceProvider.QueryStatusIdentity(session.DocumentAuthorityId, status.Query).Uri),
                Provider = "excel", Kind = "excel-query-status", Title = status.Query + " — operation status",
                CurrentTarget = ExcelResourceProvider.QueryPrefix + status.Query,
                MimeType = "application/json; charset=utf-8", Mutable = true, Tracking = "session-observed",
                ContentSha256 = TextPatternEngine.Sha256(text), Coverage = ResourceCoverage.Whole()
            };
            descriptor.Representations.Add("status"); descriptor.Capabilities.Add("read");
            var selection = new ResourceReadSelection { Result = new ResourceReadResult {
                Resource = descriptor, Representation = "status", Text = text, Complete = true,
                ContentSha256 = descriptor.ContentSha256, Coverage = ResourceCoverage.Whole(),
                ReturnedCharacters = text.Length, TotalCharacters = text.Length
            } };
            if (gateway.Authority == null)
            {
                descriptor.Reference = new ResourceRef(descriptor.Reference.Uri, descriptor.ContentSha256);
                selection.ResourceRefs = new[] { descriptor.Reference };
                return selection;
            }
            return gateway.Authority.PublishRead(session, selection, null, true);
        }

        private static IReadOnlyList<ResourceEvidence> StatusEvidence(ResourceGatewayService gateway,
            ChatSession session, ResourceReadResult result, IEnumerable<Operation> operations)
        {
            var basis = gateway.Authority == null ? new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"),
                new ResourceAuthorityScopeId("document", session.DocumentAuthorityId), result.Resource.Reference,
                "status", ResourceCoverage.Whole(), true, 0) : gateway.Authority.Observe(session, result, true).Single();
            var observedAt = DateTime.UtcNow;
            var entries = operations.ToArray();
            if (entries.Length == 0) return new[] { basis };
            return entries.Select(item => {
                // Positive native stop is independent of business-result proof.
                // Closure only ends ownership; it cannot authorize a new invocation.
                var stopped = item.ExecutionStopped && item.Dispatched && !item.SessionClosed;
                var state = stopped && item.State == ToolOperationState.Unresolved
                    ? ToolOperationState.StoppedUnverified : item.State;
                return new ResourceEvidence("ev_" + Guid.NewGuid().ToString("N"),
                    basis.ScopeId, basis.Resource, basis.View, basis.Coverage, basis.Complete, basis.AuthorityGeneration,
                    basis.Payload, basis.Dependencies, observedAt, immutable: false, contentSha256: basis.ContentSha256,
                    operationObservation: new ToolOperationObservation(item.Reference, state, item.NativeObservedAtUtc,
                        stopped, stopped));
            }).ToArray();
        }

        private static PowerQueryOperationStatus Snapshot(Operation item, string query)
        {
            var refresh = item?.Native?.Refresh;
            var load = item?.Native?.Load;
            var binding = refresh?.Binding ?? load?.Binding;
            var loadedTable = binding?.Table ?? (load?.TableCreated == true ? load.Table : null);
            return new PowerQueryOperationStatus {
                Query = query, Target = ExcelResourceProvider.QueryPrefix + query,
                NativeState = item == null ? "not_observed" : item.SessionClosed ? "session_closed" : item.State.ToString().ToLowerInvariant(),
                WaitState = item?.WaitState ?? "not_started",
                StatusObservedAtUtc = DateTime.UtcNow, NativeObservedAtUtc = item?.NativeObservedAtUtc,
                NativeProbeAvailable = false, NativeCancellationAvailable = false,
                ExecutionStopped = item?.ExecutionStopped ?? false,
                SourceMatchedAtCompletion = refresh?.SourceStillCurrent,
                BindingMatchedAtCompletion = refresh?.BindingStillCurrent,
                RequestedTableTarget = item?.Table == null ? null : "Excel table: " + item.Table,
                LoadedTableTarget = loadedTable == null ? null : "Excel table: " + loadedTable,
                Sheet = load?.Sheet ?? binding?.Sheet ?? item?.Sheet,
                RowsObserved = binding?.Rows, ColumnsObserved = binding?.Columns,
                SheetCreated = item?.Native?.Load?.SheetCreated, TableCreated = item?.Native?.Load?.TableCreated,
                DataValidation = "not_run", ErrorCode = refresh?.ErrorCode ?? item?.Native?.Load?.ErrorCode ??
                    (item?.Error as RNAssistant.Office.Domains.Excel.ExcelPowerQueryBackendException)?.ErrorCode,
                Message = item == null ? "No execution has been observed for this query in this document session."
                    : item.SessionClosed ? "The exact workbook session is closed. Native ownership ended; effects before closure remain unresolved and are not safe to replay automatically."
                    : item.State == ToolOperationState.Running || item.State == ToolOperationState.Pending
                    ? "The session-owned native call is outstanding. This is local task state; a live Excel probe is unavailable."
                    : item.Native?.Load?.Message ?? refresh?.Message ??
                    "Native completion is unresolved. Existing table values do not establish refresh success."
            };
        }
    }

    internal sealed class PowerQueryOperationStatus
    {
        [JsonProperty("query")] public string Query { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("nativeState")] public string NativeState { get; set; }
        [JsonProperty("waitState")] public string WaitState { get; set; }
        // Fresh read time belongs to ResourceEvidence. Keeping it out of the body
        // allows unchanged status snapshots to reuse their retained CAS payload.
        [JsonIgnore] public DateTime StatusObservedAtUtc { get; set; }
        [JsonProperty("nativeObservedAtUtc")] public DateTime? NativeObservedAtUtc { get; set; }
        [JsonProperty("nativeProbeAvailable")] public bool NativeProbeAvailable { get; set; }
        [JsonProperty("nativeCancellationAvailable")] public bool NativeCancellationAvailable { get; set; }
        [JsonProperty("executionStopped")] public bool ExecutionStopped { get; set; }
        [JsonProperty("sourceMatchedAtCompletion")] public bool? SourceMatchedAtCompletion { get; set; }
        [JsonProperty("bindingMatchedAtCompletion")] public bool? BindingMatchedAtCompletion { get; set; }
        [JsonProperty("requestedTableTarget")] public string RequestedTableTarget { get; set; }
        [JsonProperty("loadedTableTarget")] public string LoadedTableTarget { get; set; }
        [JsonProperty("sheet")] public string Sheet { get; set; }
        [JsonProperty("rowsObserved")] public int? RowsObserved { get; set; }
        [JsonProperty("columnsObserved")] public int? ColumnsObserved { get; set; }
        [JsonProperty("sheetCreated")] public bool? SheetCreated { get; set; }
        [JsonProperty("tableCreated")] public bool? TableCreated { get; set; }
        [JsonProperty("dataValidation")] public string DataValidation { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
