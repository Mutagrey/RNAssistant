using System;
using System.Linq;
using RNAssistant.Office.Domains.Excel;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class ExcelPowerQueryInteropBackend
    {
        public ExcelPowerQueryRefreshResult RefreshWorksheet(
            ExcelPowerQueryRefreshRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            RequireWritableWorkbook();
            EnsureNoNativeRefresh();
            RequireSourceVersion(request.QueryName, request.ExpectedQueryIdentity,
                request.ExpectedSourceVersion);
            var bindings = ReadBindings(request.QueryName);
            if (!string.Equals(bindings.BindingVersion, request.ExpectedBindingVersion,
                    StringComparison.Ordinal))
                throw Failure("Power Query load configuration changed before dispatch.",
                    "excel_query_binding_changed");
            var binding = RequireBinding(bindings, request.Sheet, request.Table);
            if (bindings.Connections.Any(x =>
                string.Equals(x.ConnectionIdentity, binding.ConnectionIdentity,
                    StringComparison.Ordinal) && x.InDataModel))
                throw Failure("A worksheet connection shared with the Data Model is outside this refresh scope.",
                    "excel_query_model_refresh_unsupported");
            Excel.Worksheet sheet;
            Excel.QueryTable queryTable;
            var table = ResolveLoad(binding, out sheet, out queryTable);
            if (queryTable.CommandType != Excel.XlCmdType.xlCmdSql)
                throw Failure("This worksheet load does not use the SQL QueryTable mode required for a documented synchronous refresh.",
                    "excel_query_refresh_mode_unsupported");
            if (queryTable.Refreshing)
                throw Failure("The selected load is already refreshing.", "excel_query_busy");
            RequireSourceVersion(request.QueryName, request.ExpectedQueryIdentity,
                request.ExpectedSourceVersion);
            markDispatchPossible();
            return RefreshExactLoad(request, binding, sheet, table, queryTable);
        }

        private ExcelPowerQueryRefreshResult RefreshExactLoad(
            ExcelPowerQueryRefreshRequest request, ExcelPowerQueryLoadBinding originalBinding,
            Excel.Worksheet sheet, Excel.ListObject table, Excel.QueryTable queryTable)
        {
            var result = new ExcelPowerQueryRefreshResult
            {
                State = ExcelPowerQueryRefreshState.Unresolved,
                Binding = originalBinding
            };
            bool accepted;
            try
            {
                // Do not use Workbook.RefreshAll: it can leave old worksheet data
                // without surfacing a Power Query load error. This call is synchronous;
                // its lifetime belongs to the session, not the caller's timeout.
                accepted = queryTable.Refresh(false);
            }
            catch (Exception ex)
            {
                result.NativeRunning = TryReadRefreshing(queryTable);
                result.State = result.NativeRunning == false
                    ? ExcelPowerQueryRefreshState.Failed : ExcelPowerQueryRefreshState.Unresolved;
                result.ErrorCode = "excel_query_refresh_failed";
                var detail = ex.Message ?? string.Empty;
                if (detail.Length > 1000) detail = detail.Substring(0, 1000) + "…";
                result.Message = "Excel reported a refresh error. Existing worksheet values are not proof of success. " + detail;
                ObserveRefreshReadBack(request, result, sheet, table, queryTable);
                return result;
            }
            result.NativeRunning = TryReadRefreshing(queryTable);
            if (result.NativeRunning != false)
            {
                result.ErrorCode = "excel_query_refresh_completion_unresolved";
                result.Message = "Excel returned without a confirmed stopped refresh. Completion remains unresolved.";
            }
            else if (!accepted)
            {
                result.State = ExcelPowerQueryRefreshState.Cancelled;
                result.ErrorCode = "excel_query_refresh_cancelled";
                result.Message = "Excel returned false from the synchronous refresh; no successful refresh is established.";
            }
            else
            {
                try
                {
                    if (queryTable.FetchedRowOverflow)
                    {
                        result.State = ExcelPowerQueryRefreshState.Failed;
                        result.ErrorCode = "excel_query_row_overflow";
                        result.Message = "Excel completed the fetch but rows exceeded worksheet capacity. The loaded result is incomplete.";
                    }
                    else
                    {
                        result.State = ExcelPowerQueryRefreshState.Succeeded;
                        result.Message = "The addressed worksheet refresh completed. Data quality requires separate checks.";
                    }
                }
                catch (Exception)
                {
                    result.ErrorCode = "excel_query_overflow_observation_unavailable";
                    result.Message = "Refresh returned, but Excel could not establish whether the worksheet result overflowed.";
                }
            }
            ObserveRefreshReadBack(request, result, sheet, table, queryTable);
            return result;
        }

        private void ObserveRefreshReadBack(
            ExcelPowerQueryRefreshRequest request, ExcelPowerQueryRefreshResult result,
            Excel.Worksheet sheet, Excel.ListObject table, Excel.QueryTable queryTable)
        {
            try
            {
                var source = ReadQuery(request.QueryName);
                result.SourceStillCurrent = string.Equals(source.QueryIdentity,
                    request.ExpectedQueryIdentity, StringComparison.Ordinal) &&
                    string.Equals(source.SourceVersion, request.ExpectedSourceVersion,
                        StringComparison.Ordinal);
                var bindings = ReadBindings(request.QueryName);
                result.BindingStillCurrent = bindings.IsComplete && !bindings.IsAmbiguous &&
                    string.Equals(bindings.BindingVersion, request.ExpectedBindingVersion,
                        StringComparison.Ordinal);
                result.Binding = ReadLoad(sheet, table, queryTable, queryTable.WorkbookConnection);
                if (result.State == ExcelPowerQueryRefreshState.Succeeded &&
                    (!result.SourceStillCurrent || !result.BindingStillCurrent))
                {
                    result.ErrorCode = "excel_query_changed_during_refresh";
                    result.Message = "Refresh completed, but source or load configuration changed. The current query result is not verified.";
                }
            }
            catch (Exception)
            {
                // Preserve the native return fact separately from unavailable
                // current-source/output evidence. Do not replace it with old rows.
                result.SourceStillCurrent = false;
                result.BindingStillCurrent = false;
                if (string.IsNullOrWhiteSpace(result.ErrorCode))
                    result.ErrorCode = "excel_query_refresh_readback_unavailable";
                result.Message = "Refresh read-back is unavailable. Current source, binding and loaded data are not verified.";
            }
            result.ObservedAtUtc = DateTime.UtcNow;
        }

        private void RequireSourceVersion(string queryName, string expectedIdentity, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(expectedIdentity) ||
                string.IsNullOrWhiteSpace(expectedVersion))
                throw Failure("A current query source observation is required.",
                    "excel_query_source_observation_required");
            var source = ReadQuery(queryName);
            if (!string.Equals(source.QueryIdentity, expectedIdentity, StringComparison.Ordinal) ||
                !string.Equals(source.SourceVersion, expectedVersion, StringComparison.Ordinal))
                throw Failure("Power Query source or native identity changed before dispatch.",
                    "excel_query_source_changed");
        }

        private static bool? TryReadRefreshing(Excel.QueryTable queryTable)
        {
            try { return queryTable.Refreshing; }
            catch (Exception) { return null; }
        }
    }
}
