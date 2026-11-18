using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    // One instance belongs to one immutable ExcelDocumentSession. COM references
    // never leave this owner; public snapshots contain only managed values.
    internal sealed partial class ExcelPowerQueryInteropBackend : IExcelPowerQueryBackend
    {
        private const int MaxNativeObjects = 4096;
        private readonly ExcelDocumentSession _session;
        private readonly Excel.Workbook _workbook;
        private readonly Dictionary<long, NativeIdentity> _identities =
            new Dictionary<long, NativeIdentity>();

        internal ExcelPowerQueryInteropBackend(ExcelDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workbook = session.BoundDocumentObject as Excel.Workbook;
            if (_workbook == null)
                throw new InvalidOperationException("The bound Excel workbook is unavailable.");
        }

        public ExcelPowerQueryCapabilities GetCapabilities()
        {
            RequireWorkbook();
            try
            {
                var queries = RequireQueries();
                var count = Convert.ToInt32(((dynamic)queries).Count);
                var writable = !_workbook.ReadOnly;
                return new ExcelPowerQueryCapabilities
                {
                    Supported = count >= 0,
                    ReadSource = true,
                    WriteSource = writable,
                    CreateDefinition = writable,
                    WorksheetRefresh = writable,
                    WorksheetLoad = writable,
                    // The first native path uses synchronous Refresh(false).
                    // Its owner STA cannot concurrently service cancellation.
                    NativeCancellation = false,
                    DataModelLoad = false,
                    EvaluatedPreview = false
                };
            }
            catch (ExcelPowerQueryBackendException ex)
            {
                return new ExcelPowerQueryCapabilities { UnavailableReason = ex.ErrorCode };
            }
        }

        public ExcelPowerQueryListSnapshot ListQueries(int maxQueries)
        {
            RequireWorkbook();
            if (maxQueries < 1 || maxQueries > MaxNativeObjects)
                throw Failure("Query list limit is outside the supported bound.",
                    "excel_query_bound_invalid");
            dynamic queries = RequireQueries();
            var count = Convert.ToInt32(queries.Count);
            var items = new List<ExcelPowerQueryMetadata>();
            for (var index = 1; index <= Math.Min(count, maxQueries); index++)
            {
                dynamic query = queries.Item(index);
                items.Add(new ExcelPowerQueryMetadata
                {
                    QueryName = Convert.ToString(query.Name),
                    QueryIdentity = Identity((object)query),
                    Description = Convert.ToString(query.Description)
                });
            }
            return new ExcelPowerQueryListSnapshot
            {
                Items = items,
                TotalCount = count,
                IsTruncated = count > items.Count,
                ObservedAtUtc = DateTime.UtcNow
            };
        }

        public ExcelPowerQuerySnapshot ReadQuery(string queryName)
        {
            RequireWorkbook();
            dynamic query = RequireQuery(queryName);
            var formula = Convert.ToString(query.Formula) ?? string.Empty;
            return new ExcelPowerQuerySnapshot
            {
                QueryName = Convert.ToString(query.Name),
                QueryIdentity = Identity((object)query),
                Formula = formula,
                Description = Convert.ToString(query.Description),
                SourceVersion = Hash(formula),
                ObservedAtUtc = DateTime.UtcNow
            };
        }

        public void WriteSource(
            ExcelPowerQueryWriteRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            RequireWritableWorkbook();
            EnsureNoNativeRefresh();
            var current = ReadQuery(request.QueryName);
            if (!string.Equals(current.QueryIdentity, request.ExpectedQueryIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(current.Formula, request.ExpectedFormula,
                    StringComparison.Ordinal))
                throw Failure("Power Query source or native identity changed before dispatch.",
                    "excel_query_source_changed");
            if (request.Formula == null)
                throw Failure("Power Query source is required.", "excel_query_source_required");
            if (string.Equals(current.Formula, request.Formula, StringComparison.Ordinal))
                return;
            dynamic query = RequireQuery(request.QueryName);
            if (!string.Equals(Identity((object)query), current.QueryIdentity,
                    StringComparison.Ordinal))
                throw Failure("Power Query was replaced before dispatch.",
                    "excel_query_source_changed");
            markDispatchPossible();
            query.Formula = request.Formula;
            // Read-back and journaling belong to the domain service. No refresh.
        }

        public void CreateDefinition(
            ExcelPowerQueryCreateRequest request, Action markDispatchPossible)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (markDispatchPossible == null)
                throw new ArgumentNullException(nameof(markDispatchPossible));
            RequireWritableWorkbook();
            EnsureNoNativeRefresh();
            if (string.IsNullOrWhiteSpace(request.QueryName) || request.Formula == null)
                throw Failure("A query name and M source are required.",
                    "excel_query_definition_required");
            dynamic queries = RequireQueries();
            var count = Convert.ToInt32(queries.Count);
            if (count >= MaxNativeObjects)
                throw Failure("The supported query collection bound was reached.",
                    "excel_query_bound_reached");
            for (var index = 1; index <= count; index++)
            {
                dynamic query = queries.Item(index);
                if (string.Equals(Convert.ToString(query.Name), request.QueryName,
                        StringComparison.OrdinalIgnoreCase))
                    throw Failure("The query name is already in use.",
                        "excel_query_already_exists");
            }
            markDispatchPossible();
            queries.Add(request.QueryName, request.Formula, request.Description ?? string.Empty);
            // Creating a definition does not create a connection, load or table.
        }

        private Excel.Workbook RequireWorkbook()
        {
            if (!_session.StaDispatcher.CheckAccess)
                throw Failure("Power Query access requires the bound owner STA.",
                    "document_session_thread_mismatch");
            if (!_session.IsAlive)
                throw Failure("The bound Excel workbook is closed.",
                    "active_document_changed");
            return _workbook;
        }

        private void RequireWritableWorkbook()
        {
            if (RequireWorkbook().ReadOnly)
                throw Failure("The bound workbook is read-only.", "excel_query_read_only");
        }

        private object RequireQueries()
        {
            RequireWorkbook();
            try
            {
                // Late binding supports a host with a compatible older PIA;
                // the actual Excel application must implement Workbook.Queries.
                object queries = ((dynamic)_workbook).Queries;
                if (queries == null) throw new InvalidOperationException();
                return queries;
            }
            catch (Exception)
            {
                throw Failure("This Excel session does not expose Power Query definitions.",
                    "excel_query_unavailable");
            }
        }

        private object RequireQuery(string queryName)
        {
            if (string.IsNullOrWhiteSpace(queryName))
                throw Failure("An exact query name is required.", "excel_query_name_required");
            dynamic queries = RequireQueries();
            try
            {
                dynamic query = queries.Item(queryName);
                if (query != null && string.Equals(Convert.ToString(query.Name), queryName,
                        StringComparison.Ordinal)) return query;
            }
            catch (COMException) { }
            throw Failure("The exact Power Query definition was not found: " + queryName,
                "excel_query_not_found");
        }

        private void EnsureNoNativeRefresh()
        {
            var connections = RequireWorkbook().Connections;
            if (connections.Count > MaxNativeObjects)
                throw Failure("The connection collection exceeds the supported bound.",
                    "excel_query_bound_reached");
            foreach (Excel.WorkbookConnection connection in connections)
            {
                if (connection.Type != Excel.XlConnectionType.xlConnectionTypeOLEDB) continue;
                bool refreshing;
                try { refreshing = connection.OLEDBConnection.Refreshing; }
                catch (Exception)
                {
                    throw Failure("Excel cannot establish whether a connection is refreshing.",
                        "excel_query_refresh_state_unavailable");
                }
                if (refreshing)
                    throw Failure("A native refresh is already running in the bound workbook.",
                        "excel_query_busy");
            }
        }

        private string Identity(object native)
        {
            if (native == null) throw Failure("A native query binding is missing.",
                "excel_query_binding_missing");
            IntPtr pointer = IntPtr.Zero;
            try
            {
                pointer = Marshal.GetIUnknownForObject(native);
                var key = pointer.ToInt64();
                NativeIdentity entry;
                if (!_identities.TryGetValue(key, out entry))
                {
                    entry = new NativeIdentity
                    {
                        Object = native,
                        Value = Guid.NewGuid().ToString("N")
                    };
                    _identities.Add(key, entry);
                }
                return entry.Value;
            }
            finally
            {
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            }
        }

        private bool BelongsToSession(Excel.Worksheet sheet)
        {
            var workbook = sheet == null ? null : sheet.Parent as Excel.Workbook;
            return workbook != null && string.Equals(
                DocumentIdentity.RuntimeKey("Excel", workbook), _session.RuntimeDocumentId,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                    result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static ExcelPowerQueryBackendException Failure(string message, string code)
        {
            return new ExcelPowerQueryBackendException(message, code);
        }

        private sealed class NativeIdentity
        {
            // Retaining the exact RCW also prevents a released native pointer
            // being recycled into a different query/table during this session.
            public object Object;
            public string Value;
        }
    }
}
