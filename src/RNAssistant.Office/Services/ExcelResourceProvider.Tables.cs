using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        internal const string TableKind = "excel-table";

        internal ResourceDescriptor ResolveTable(ChatSession session, string name)
        { return _scope.Read(session, () => FindTable(session, NormalizeTableName(name))); }

        private ResourceListPage ListTables(ChatSession session, string cursor, int limit)
        {
            var snapshot = CaptureTableCatalog();
            var items = snapshot.Tables.Select(table => DescribeTable(session, table)).ToList();
            if (items.Select(item => item.Reference.Uri).Distinct(StringComparer.Ordinal).Count() != items.Count)
                throw Error("RESOURCE_TARGET_AMBIGUOUS", "The table catalog contains duplicate names.");
            var binding = ResourceReadCursor.ListBinding(Id, TableKind);
            var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
            var revision = ResourceReadCursor.CollectionRevision(items);
            ResourceReadCursor.ValidateContinuation(position, revision);
            ResourceReadCursor.ValidateCollectionOffset(position, items.Count);
            var selected = items.Skip(position.Offset).Take(Math.Max(1, Math.Min(50, limit))).ToList();
            var next = position.Offset + selected.Count;
            return new ResourceListPage { Items = selected, Total = items.Count, Truncated = next < items.Count,
                NextCursor = next < items.Count ? ResourceReadCursor.CreateRevisionBound(next, revision, binding) : null };
        }

        private ExcelInspectSnapshot CaptureTableCatalog()
        {
            var snapshot = _reader.CaptureStructure("tables");
            if (snapshot.Truncated)
                throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "The bounded table catalog is incomplete; named-table resolution is unavailable.");
            return snapshot;
        }

        private ResourceDescriptor FindTable(ChatSession session, string name)
        {
            var snapshot = CaptureTableCatalog();
            var matches = snapshot.Tables.Where(table => NormalizeTableName(table.Name) == name).Take(2).ToList();
            if (matches.Count != 1)
                throw Error(matches.Count == 0 ? "RESOURCE_TARGET_NOT_FOUND" : "RESOURCE_TARGET_AMBIGUOUS",
                    "Exactly one table with this name must exist in the bound workbook.");
            return DescribeTable(session, matches[0]);
        }

        private ResourceDescriptor DescribeTable(ChatSession session, ExcelTableSnapshot table)
        {
            var name = NormalizeTableName(table.Name);
            if (table.Sheet.Length > 128 || table.Sheet.Any(char.IsControl))
                throw Error("RESOURCE_TARGET_INVALID", "The table sheet name is invalid.");
            var descriptor = Describe(session, table.Sheet.ToUpperInvariant(), NormalizeAddress(table.Range));
            descriptor.Reference = new ResourceRef(ResourceUri.Create(Id, _scope.DocumentToken(session), "table", name));
            descriptor.Kind = TableKind; descriptor.Title = table.Name;
            descriptor.Metadata["table"] = table.Name;
            descriptor.Metadata["recordsPath"] = "$.values";
            return descriptor;
        }

        private ResourceReadSelection ReadTable(ChatSession session, ResourceReadRequest request, string name)
        {
            var view = string.IsNullOrWhiteSpace(request.Representation) || request.Representation == "auto" ? "text" : request.Representation;
            if (view != "text" && view != "formulas" && view != "structure")
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "This table supports text, formulas, structure and records/table at $.values.");
            var descriptor = FindTable(session, name);
            var sheet = descriptor.Metadata["sheet"];
            var range = descriptor.Metadata["address"];
            if (CellCount(range) > ExcelReadService.MaxReadCells)
                throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "The table exceeds the 100000-cell snapshot bound.");
            var snapshot = _reader.CaptureRange(sheet, range, view == "structure" ? "profile" : view == "formulas" ? "formulas" : "values");
            if (!string.Equals(snapshot.Sheet, sheet, StringComparison.OrdinalIgnoreCase) || NormalizeAddress(snapshot.Address) != range)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The captured range does not match the resolved table extent.");
            // Preserve extent in the captured bytes: a moved table with identical
            // cells must not silently reuse its former physical revision.
            var text = view == "structure" ? ExcelReadService.ProfileOutput(snapshot).ToString(Formatting.None) : JsonConvert.SerializeObject(snapshot);
            return SelectCapture(request, descriptor, text, view);
        }

        private static string NormalizeTableName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name != name.Trim() || name.Any(char.IsControl))
                throw Error("RESOURCE_TARGET_INVALID", "Use one bounded, exact Excel table name.");
            return name.ToUpperInvariant();
        }
    }
}
