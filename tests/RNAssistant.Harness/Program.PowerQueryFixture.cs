using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Harness
{
    internal sealed partial class FakeOfficeAdapter : IExcelPowerQueryBackendProvider
    {
        public IExcelPowerQueryBackend ExcelPowerQueryBackend { get; set; }
    }

    internal sealed class StubPowerQueryBackend : IExcelPowerQueryBackend
    {
        private readonly Dictionary<string, ExcelPowerQuerySnapshot> _queries =
            new Dictionary<string, ExcelPowerQuerySnapshot>(StringComparer.Ordinal);
        internal readonly List<ExcelPowerQueryWriteRequest> Writes = new List<ExcelPowerQueryWriteRequest>();
        internal readonly List<ExcelPowerQueryCreateRequest> Creates = new List<ExcelPowerQueryCreateRequest>();
        internal readonly List<ExcelPowerQueryRefreshRequest> Refreshes = new List<ExcelPowerQueryRefreshRequest>();
        internal readonly List<ExcelPowerQueryLoadRequest> Loads = new List<ExcelPowerQueryLoadRequest>();
        internal int SourceReads;
        internal int ListReads;
        internal int BindingsReads;
        internal int Dispatches;
        internal Exception ReadFailure;
        internal Action<ExcelPowerQueryWriteRequest> BeforeWrite;
        internal Action<ExcelPowerQueryWriteRequest> AfterWrite;
        internal Func<string, ExcelPowerQueryBindingsSnapshot> BindingsOverride;
        internal Func<ExcelPowerQueryRefreshRequest, Action, ExcelPowerQueryRefreshResult> RefreshOverride;
        internal Func<ExcelPowerQueryLoadRequest, Action, ExcelPowerQueryLoadResult> LoadOverride;
        internal ExcelPowerQueryCellErrorSnapshot CellErrors = new ExcelPowerQueryCellErrorSnapshot {
            Complete = true, ErrorCount = 0, ObservedAtUtc = DateTime.UtcNow };

        internal void Seed(string name, string formula, string description = "")
        {
            _queries[name] = new ExcelPowerQuerySnapshot { QueryName = name, QueryIdentity = "fixture-query:" + name,
                Formula = formula, SourceVersion = TextPatternEngine.Sha256(formula), Description = description,
                ObservedAtUtc = DateTime.UtcNow };
        }

        internal string Formula(string name) { return _queries[name].Formula; }

        public ExcelPowerQueryCapabilities GetCapabilities()
        {
            return new ExcelPowerQueryCapabilities { Supported = true, ReadSource = true, WriteSource = true,
                CreateDefinition = true, WorksheetRefresh = true, WorksheetLoad = true };
        }

        public ExcelPowerQueryListSnapshot ListQueries(int maxQueries)
        {
            ListReads++;
            return new ExcelPowerQueryListSnapshot {
                Items = _queries.Values.OrderBy(item => item.QueryName, StringComparer.Ordinal).Take(maxQueries)
                    .Select(item => new ExcelPowerQueryMetadata { QueryName = item.QueryName,
                        QueryIdentity = item.QueryIdentity, Description = item.Description }).ToArray(),
                TotalCount = _queries.Count, IsTruncated = _queries.Count > maxQueries, ObservedAtUtc = DateTime.UtcNow };
        }

        public ExcelPowerQuerySnapshot ReadQuery(string queryName)
        {
            SourceReads++;
            if (ReadFailure != null) throw ReadFailure;
            ExcelPowerQuerySnapshot source;
            if (!_queries.TryGetValue(queryName, out source))
                throw new ExcelPowerQueryBackendException("Query is absent.", "excel_query_not_found");
            return new ExcelPowerQuerySnapshot { QueryName = source.QueryName, QueryIdentity = source.QueryIdentity,
                Formula = source.Formula, SourceVersion = source.SourceVersion, Description = source.Description,
                ObservedAtUtc = DateTime.UtcNow };
        }

        public ExcelPowerQueryBindingsSnapshot ReadBindings(string queryName)
        {
            BindingsReads++;
            if (BindingsOverride != null) return BindingsOverride(queryName);
            ExcelPowerQuerySnapshot source;
            if (!_queries.TryGetValue(queryName, out source))
                throw new ExcelPowerQueryBackendException("Query is absent.", "excel_query_not_found");
            return new ExcelPowerQueryBindingsSnapshot { QueryName = source.QueryName, QueryIdentity = source.QueryIdentity,
                BindingVersion = TextPatternEngine.Sha256("no-loads:" + source.QueryIdentity),
                Connections = new ExcelPowerQueryConnectionBinding[0], Loads = new ExcelPowerQueryLoadBinding[0],
                IsComplete = true, ObservedAtUtc = DateTime.UtcNow };
        }

        public void WriteSource(ExcelPowerQueryWriteRequest request, Action markDispatchPossible)
        {
            Writes.Add(request);
            if (BeforeWrite != null) BeforeWrite(request);
            var source = ReadQuery(request.QueryName);
            if (source.QueryIdentity != request.ExpectedQueryIdentity || source.Formula != request.ExpectedFormula)
                throw new ExcelPowerQueryBackendException("Query changed before write.", "excel_query_source_changed");
            markDispatchPossible();
            Dispatches++;
            Seed(request.QueryName, request.Formula, source.Description);
            if (AfterWrite != null) AfterWrite(request);
        }

        public ExcelPowerQueryCellErrorSnapshot ReadWorksheetErrors(string queryName, string sheet, string table, int maximumCells)
        { return CellErrors; }

        public void CreateDefinition(ExcelPowerQueryCreateRequest request, Action markDispatchPossible)
        {
            Creates.Add(request);
            if (_queries.ContainsKey(request.QueryName))
                throw new ExcelPowerQueryBackendException("Query already exists.", "excel_query_exists");
            markDispatchPossible();
            Dispatches++;
            Seed(request.QueryName, request.Formula, request.Description);
        }

        public ExcelPowerQueryRefreshResult RefreshWorksheet(ExcelPowerQueryRefreshRequest request, Action markDispatchPossible)
        {
            Refreshes.Add(request);
            if (RefreshOverride == null) throw new InvalidOperationException("Refresh must be explicitly configured by the test.");
            return RefreshOverride(request, () => { markDispatchPossible(); Dispatches++; });
        }

        public ExcelPowerQueryLoadResult CreateWorksheetLoad(ExcelPowerQueryLoadRequest request, Action markDispatchPossible)
        {
            Loads.Add(request);
            if (LoadOverride == null) throw new InvalidOperationException("Load must be explicitly configured by the test.");
            return LoadOverride(request, () => { markDispatchPossible(); Dispatches++; });
        }
    }
}
