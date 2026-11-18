using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Office.Domains.Excel;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed partial class ExcelPowerQueryInteropBackend
    {
        public ExcelPowerQueryBindingsSnapshot ReadBindings(string queryName)
        {
            RequireWorkbook();
            var query = ReadQuery(queryName);
            var connections = new List<ExcelPowerQueryConnectionBinding>();
            var loads = new List<ExcelPowerQueryLoadBinding>();
            var config = new BindingConfiguration { QueryIdentity = query.QueryIdentity };
            var nativeConnections = new Dictionary<string, Excel.WorkbookConnection>(
                StringComparer.Ordinal);
            var complete = true;
            var workbookConnections = _workbook.Connections;
            if (workbookConnections.Count > MaxNativeObjects)
                throw Failure("The connection collection exceeds the supported bound.",
                    "excel_query_bound_reached");
            foreach (Excel.WorkbookConnection connection in workbookConnections)
            {
                try
                {
                    if (connection.Type != Excel.XlConnectionType.xlConnectionTypeOLEDB) continue;
                    var connectionText = Convert.ToString(connection.OLEDBConnection.Connection);
                    string location;
                    if (!ExcelPowerQueryConnection.TryGetMashupLocation(connectionText, out location) ||
                        !string.Equals(location, queryName, StringComparison.Ordinal)) continue;
                    var identity = Identity(connection);
                    connections.Add(new ExcelPowerQueryConnectionBinding
                    {
                        ConnectionName = connection.Name,
                        ConnectionIdentity = identity,
                        InDataModel = connection.InModel
                    });
                    nativeConnections.Add(identity, connection);
                    config.Connections.Add(new ConnectionConfiguration
                    {
                        Identity = identity,
                        Name = connection.Name,
                        ConnectionVersion = Hash(connectionText),
                        CommandType = (int)connection.OLEDBConnection.CommandType,
                        CommandVersion = Hash(JsonConvert.SerializeObject(
                            connection.OLEDBConnection.CommandText)),
                        InDataModel = connection.InModel
                    });
                }
                catch (Exception)
                {
                    // Incomplete inspection cannot authorize choosing a binding.
                    complete = false;
                }
            }
            var tableCount = 0;
            foreach (Excel.Worksheet sheet in _workbook.Worksheets)
            {
                if (!BelongsToSession(sheet))
                    throw Failure("A worksheet is outside the bound workbook.",
                        "excel_query_binding_changed");
                foreach (Excel.ListObject table in sheet.ListObjects)
                {
                    if (++tableCount > MaxNativeObjects)
                        throw Failure("The worksheet table collection exceeds the supported bound.",
                            "excel_query_bound_reached");
                    var sourceType = table.SourceType;
                    if (sourceType != Excel.XlListObjectSourceType.xlSrcExternal &&
                        sourceType != Excel.XlListObjectSourceType.xlSrcQuery) continue;
                    Excel.QueryTable queryTable;
                    try { queryTable = table.QueryTable; }
                    catch (Exception) { complete = false; continue; }
                    if (queryTable == null) continue;
                    try
                    {
                        var nativeConnection = queryTable.WorkbookConnection;
                        if (nativeConnection == null) { complete = false; continue; }
                        var connectionIdentity = Identity(nativeConnection);
                        if (!nativeConnections.ContainsKey(connectionIdentity)) continue;
                        var load = ReadLoad(sheet, table, queryTable, nativeConnection);
                        loads.Add(load);
                        config.Loads.Add(new LoadConfiguration
                        {
                            Identity = load.BindingIdentity,
                            ConnectionIdentity = load.ConnectionIdentity,
                            SheetIdentity = Identity(sheet),
                            Sheet = load.Sheet,
                            Table = load.Table,
                            Anchor = load.Anchor,
                            CommandType = (int)queryTable.CommandType,
                            CommandVersion = Hash(JsonConvert.SerializeObject(queryTable.CommandText))
                        });
                    }
                    catch (Exception) { complete = false; }
                }
            }
            config.Connections = config.Connections.OrderBy(x => x.Identity,
                StringComparer.Ordinal).ToList();
            config.Loads = config.Loads.OrderBy(x => x.Identity,
                StringComparer.Ordinal).ToList();
            return new ExcelPowerQueryBindingsSnapshot
            {
                QueryName = query.QueryName,
                QueryIdentity = query.QueryIdentity,
                Connections = connections,
                Loads = loads,
                BindingVersion = Hash(JsonConvert.SerializeObject(config)),
                IsComplete = complete,
                IsAmbiguous = loads.GroupBy(x => x.Sheet + "\0" + x.Table,
                    StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1),
                UnavailableReason = complete ? null : "excel_query_bindings_incomplete",
                ObservedAtUtc = DateTime.UtcNow
            };
        }

        private ExcelPowerQueryLoadBinding ReadLoad(
            Excel.Worksheet sheet, Excel.ListObject table, Excel.QueryTable queryTable,
            Excel.WorkbookConnection connection)
        {
            if (!BelongsToSession(sheet))
                throw Failure("The load target is outside the bound workbook.",
                    "excel_query_binding_changed");
            var range = table.Range;
            var body = table.DataBodyRange;
            var header = table.HeaderRowRange;
            var anchor = (Excel.Range)range.Cells[1, 1];
            return new ExcelPowerQueryLoadBinding
            {
                Sheet = sheet.Name,
                Table = table.Name,
                ConnectionName = connection.Name,
                ConnectionIdentity = Identity(connection),
                BindingIdentity = Identity(table) + ":" + Identity(queryTable),
                Range = range.Address[false, false],
                HeaderRange = header == null ? null : header.Address[false, false],
                DataRange = body == null ? null : body.Address[false, false],
                Anchor = anchor.Address[false, false],
                Rows = body == null ? 0 : Convert.ToInt32(body.Rows.Count),
                Columns = table.ListColumns.Count,
                Refreshing = queryTable.Refreshing
            };
        }

        private ExcelPowerQueryLoadBinding RequireBinding(
            ExcelPowerQueryBindingsSnapshot bindings, string sheet, string table)
        {
            if (!bindings.IsComplete || bindings.IsAmbiguous)
                throw Failure("Power Query load bindings cannot be resolved unambiguously.",
                    "excel_query_bindings_ambiguous");
            var matches = bindings.Loads.Where(x =>
                (string.IsNullOrWhiteSpace(sheet) || string.Equals(x.Sheet, sheet,
                    StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(table) || string.Equals(x.Table, table,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0)
                throw Failure("The query has no matching worksheet load.",
                    "excel_query_worksheet_load_missing");
            if (matches.Length != 1)
                throw Failure("Specify the exact sheet and table for this query load.",
                    "excel_query_bindings_ambiguous");
            return matches[0];
        }

        private Excel.ListObject ResolveLoad(
            ExcelPowerQueryLoadBinding binding, out Excel.Worksheet sheet,
            out Excel.QueryTable queryTable)
        {
            sheet = RequireSheet(binding.Sheet);
            Excel.ListObject found = null;
            foreach (Excel.ListObject candidate in sheet.ListObjects)
                if (string.Equals(candidate.Name, binding.Table, StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null)
                        throw Failure("The worksheet table binding is ambiguous.",
                            "excel_query_bindings_ambiguous");
                    found = candidate;
                }
            if (found == null)
                throw Failure("The worksheet load was removed.", "excel_query_binding_changed");
            queryTable = found.QueryTable;
            if (queryTable == null || !string.Equals(
                    Identity(found) + ":" + Identity(queryTable), binding.BindingIdentity,
                    StringComparison.Ordinal) || !string.Equals(
                    Identity(queryTable.WorkbookConnection), binding.ConnectionIdentity,
                    StringComparison.Ordinal))
                throw Failure("The worksheet load was replaced or rebound.",
                    "excel_query_binding_changed");
            return found;
        }

        private Excel.Worksheet RequireSheet(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw Failure("An explicit worksheet is required.", "excel_query_sheet_required");
            foreach (Excel.Worksheet sheet in RequireWorkbook().Worksheets)
                if (string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    BelongsToSession(sheet)) return sheet;
            throw Failure("The requested worksheet was not found: " + name,
                "excel_query_sheet_not_found");
        }

        private sealed class BindingConfiguration
        {
            public string QueryIdentity { get; set; }
            public List<ConnectionConfiguration> Connections { get; set; } =
                new List<ConnectionConfiguration>();
            public List<LoadConfiguration> Loads { get; set; } =
                new List<LoadConfiguration>();
        }

        private sealed class ConnectionConfiguration
        {
            public string Identity { get; set; }
            public string Name { get; set; }
            public string ConnectionVersion { get; set; }
            public int CommandType { get; set; }
            public string CommandVersion { get; set; }
            public bool InDataModel { get; set; }
        }

        private sealed class LoadConfiguration
        {
            public string Identity { get; set; }
            public string ConnectionIdentity { get; set; }
            public string SheetIdentity { get; set; }
            public string Sheet { get; set; }
            public string Table { get; set; }
            public string Anchor { get; set; }
            public int CommandType { get; set; }
            public string CommandVersion { get; set; }
        }
    }
}
