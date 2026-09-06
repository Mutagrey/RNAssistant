using System;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed partial class ExcelResourceProvider
    {
        internal const string SearchKind = "excel-search-scope";
        private readonly IExcelFindReplaceBackend _searchBackend;

        internal static string SearchTitle(ExcelCellScopeRequest request)
        {
            var sheet = request.Scope == "selection" ? "" : request.Sheet ?? "";
            var address = request.Scope == "range" ? request.Address ?? "" : "";
            return request.Scope + (sheet.Length > 0 ? " '" + sheet.Replace("'", "''") + "'" : "") +
                (address.Length > 0 ? "!" + address : "");
        }

        internal ResourceDescriptor ResolveSearch(ChatSession session, string title)
        {
            if (_searchBackend == null) throw Error("RESOURCE_PROVIDER_UNAVAILABLE", "The bound Excel search reader is unavailable.");
            var match = Regex.Match(title ?? "", @"\A(?<scope>workbook|sheet|range|selection)(?: '(?<sheet>(?:[^']|'')*)')?(?:!(?<address>[^\r\n]+))?\z");
            var request = new ExcelCellScopeRequest { Scope = match.Groups["scope"].Value,
                Sheet = match.Groups["sheet"].Value.Replace("''", "'"), Address = match.Groups["address"].Value };
            if (!match.Success || request.Sheet.Length > 128 || request.Address.Length > 1024 ||
                (request.Scope == "range" && request.Address.Length == 0) || SearchTitle(request) != title)
                throw Error("RESOURCE_TARGET_INVALID", "Use workbook, selection, sheet 'Name', or range 'Name'!A1:B10; omit the sheet for the bound active sheet.");
            return _scope.Read(session, () => DescribeSearch(session, request));
        }

        private ResourceDescriptor DescribeSearch(ChatSession session, ExcelCellScopeRequest request)
        {
            // Excel sheet/range names are case-insensitive, as in the existing range provider.
            // Input spelling must not create a parallel logical identity for one scope.
            request = new ExcelCellScopeRequest { Scope = request.Scope,
                Sheet = (request.Scope == "selection" ? "" : request.Sheet ?? "").ToUpperInvariant(),
                Address = (request.Scope == "range" ? request.Address ?? "" : "").ToUpperInvariant() };
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(ResourceUri.Create(Id, _scope.DocumentToken(session),
                "search", request.Scope, "sheet:" + (request.Scope == "selection" ? "" : request.Sheet ?? "") + ":",
                "address:" + (request.Scope == "range" ? request.Address ?? "" : "") + ":")),
                Provider = Id, Kind = SearchKind, Title = SearchTitle(request), Mutable = true,
                MimeType = "application/json", Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "metadata", "text" });
            return descriptor;
        }

        private static ExcelCellScopeRequest SearchRequest(ResourceAddress address)
        {
            if (!address.Segments[3].StartsWith("sheet:", StringComparison.Ordinal) ||
                !address.Segments[4].StartsWith("address:", StringComparison.Ordinal) ||
                address.Segments[3].Length < 7 || address.Segments[4].Length < 9 ||
                !address.Segments[3].EndsWith(":", StringComparison.Ordinal) || !address.Segments[4].EndsWith(":", StringComparison.Ordinal))
                throw Error("RESOURCE_TARGET_INVALID", "Invalid Excel search scope.");
            var request = new ExcelCellScopeRequest { Scope = address.Segments[2],
                Sheet = address.Segments[3].Substring(6, address.Segments[3].Length - 7),
                Address = address.Segments[4].Substring(8, address.Segments[4].Length - 9) };
            if (ExcelFindReplaceService.NormalizeScope(request.Scope, null, null, "workbook") != request.Scope ||
                request.Sheet != request.Sheet.ToUpperInvariant() || request.Address != request.Address.ToUpperInvariant() ||
                request.Sheet.Length > 128 || request.Address.Length > 1024 ||
                (request.Scope == "range" ? request.Address.Length == 0 : request.Address.Length != 0) ||
                (request.Scope == "selection" && request.Sheet.Length != 0))
                throw Error("RESOURCE_TARGET_INVALID", "Invalid Excel search scope.");
            return request;
        }

        private ResourceReadSelection ReadSearch(ChatSession session, ResourceReadRequest request, ResourceAddress address)
        {
            var source = SearchRequest(address);
            var descriptor = DescribeSearch(session, source);
            if (request.Representation == "metadata")
            {
                ResourceReadCursor.RejectCursor(request);
                return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor, Representation = "metadata", Complete = true } };
            }
            if (!string.IsNullOrEmpty(request.Representation) && request.Representation != "auto" && request.Representation != "text")
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "Excel search scopes expose exact text JSON.");
            if (_searchBackend == null) throw Error("RESOURCE_PROVIDER_UNAVAILABLE", "The bound Excel search reader is unavailable.");
            try
            {
                var snapshot = new ExcelFindReplaceService(_searchBackend).CaptureSearch(source, CancellationToken.None);
                var text = JsonConvert.SerializeObject(snapshot);
                if (text.Length > ExcelFindReplaceService.MaximumSearchCharacters)
                    throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Choose a smaller Excel search scope.");
                return SelectCapture(request, descriptor, text, "text");
            }
            catch (ExcelFindReplaceBackendException error) { throw Error(error.ErrorCode, error.Message); }
        }
    }
}
