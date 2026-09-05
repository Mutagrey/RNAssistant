using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Domains.Excel;

namespace RNAssistant.Office.Services
{
    internal sealed class ExcelResourceProvider : ILiveOfficeResourceProvider
    {
        internal const string ProviderName = "excel";
        internal const string RangeKind = "excel-range";
        private readonly ExcelReadService _reader;
        private readonly LiveOfficeResourceScope _scope;
        private readonly RNAssistant.Core.Storage.ChatBlobStore _payloads;
        public string Id { get { return ProviderName; } }
        internal ExcelResourceProvider(IOfficeApplicationAdapter adapter, IExcelReadBackend backend,
            RNAssistant.Core.Storage.ChatBlobStore payloads)
        { _reader = new ExcelReadService(backend); _scope = new LiveOfficeResourceScope(adapter); _payloads = payloads; }

        internal ResourceDescriptor ResolveRange(ChatSession session, string target)
        {
            return _scope.Read(session, () =>
            {
                var match = Regex.Match(target ?? "", @"\A(?:Excel range: )?(?<sheet>[^!]{1,128})!(?<address>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}(?::\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})?)\z");
                if (!match.Success) throw Error("RESOURCE_TARGET_INVALID", "Use an explicit sheet!A1:B10 range.");
                var sheet = match.Groups["sheet"].Value.Trim().Trim('\'').Replace("''", "'").ToUpperInvariant();
                var address = NormalizeAddress(match.Groups["address"].Value);
                return Describe(session, sheet, address);
            });
        }

        public ResourceListPage List(ChatSession session, string kind, string cursor, int limit)
        {
            return _scope.Read(session, () =>
            {
                var structure = _reader.CaptureStructure("sheets");
                var items = (structure.Sheets ?? new List<ExcelSheetSnapshot>()).Where(item => !string.IsNullOrWhiteSpace(item.UsedRange))
                    .Select(item => Describe(session, item.Name.ToUpperInvariant(), NormalizeAddress(item.UsedRange))).ToList();
                var binding = ResourceReadCursor.ListBinding(Id, kind);
                var position = ResourceReadCursor.ParseRevisionBound(cursor, binding);
                var revision = ResourceReadCursor.CollectionRevision(items);
                ResourceReadCursor.ValidateContinuation(position, revision);
                var selected = items.Skip(position.Offset).Take(Math.Max(1, Math.Min(50, limit))).ToList();
                return new ResourceListPage { Items = selected, Total = items.Count, Truncated = structure.Truncated || position.Offset + selected.Count < items.Count,
                    NextCursor = position.Offset + selected.Count < items.Count ? ResourceReadCursor.CreateRevisionBound(position.Offset + selected.Count, revision, binding) : null };
            });
        }

        public ResourceDescriptor Resolve(ChatSession session, string uri)
        {
            return _scope.Read(session, () => {
                var address = Parse(session, uri);
                return Describe(session, address.Segments[2], address.Segments[3]);
            });
        }

        public ResourceSearchResult Search(ChatSession session, string query, string kind, int limit, int maxCharsPerMatch)
        {
            var items = List(session, kind, null, 50).Items.Where(item => item.Title.IndexOf(query ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
            return new ResourceSearchResult { Query = query, Matches = items.Take(Math.Max(1, Math.Min(20, limit)))
                .Select(item => new ResourceSearchMatch { Reference = item.Reference, Title = item.Title, Kind = item.Kind, Representation = "metadata" }).ToList() };
        }

        public ResourceReadSelection Read(ChatSession session, ResourceReadRequest request)
        {
            return _scope.Read(session, () =>
            {
                var parsed = Parse(session, request.Reference.Uri);
                var view = string.IsNullOrWhiteSpace(request.Representation) || request.Representation == "auto" ? "text" : request.Representation;
                if (view != "text" && view != "formulas") throw Error("RESOURCE_VIEW_UNSUPPORTED", "This range supports text, formulas, table and records views.");
                var range = parsed.Segments[3];
                if (CellCount(range) > ExcelReadService.MaxReadCells)
                    throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Choose a narrower range; the bounded snapshot permits at most 100000 cells.");
                var snapshot = _reader.CaptureRange(parsed.Segments[2], range, view == "formulas" ? "formulas" : "values");
                var text = JsonConvert.SerializeObject(view == "formulas" ? snapshot.Formulas : snapshot.Values);
                if (text.Length > ChatArtifactLimits.MaximumTextCharacters) throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Choose a narrower range for this view.");
                var hash = TextPatternEngine.Sha256(text);
                var binding = ResourceReadCursor.ReadBinding(request.Reference.Uri, view);
                var position = ResourceReadCursor.ParseRevisionBound(request, binding);
                ResourceReadCursor.ValidateContinuation(position, hash);
                if (request.Reference.IsExact && request.Reference.Revision != hash)
                    throw Error("RESOURCE_REVISION_CHANGED", "The live range differs from the expected exact snapshot.");
                if (position.Offset > text.Length) throw Error("RESOURCE_CURSOR_INVALID", "The cursor exceeds this exact range view.");
                var count = Math.Min(text.Length - position.Offset, Math.Max(1, Math.Min(32000, request.MaxChars <= 0 ? 32000 : request.MaxChars)));
                var next = position.Offset + count;
                var descriptor = Describe(session, parsed.Segments[2], range);
                descriptor.Reference = new ResourceRef(request.Reference.Uri, hash);
                return new ResourceReadSelection { Result = new ResourceReadResult { Resource = descriptor, Representation = view,
                    Text = text.Substring(position.Offset, count), ContentSha256 = hash, Offset = position.Offset,
                    CompleteViewPayload = _payloads == null ? null : PayloadRef.FromBlob(_payloads.StoreText(text, "application/json")),
                    ReturnedCharacters = count, TotalCharacters = text.Length, Complete = next == text.Length, Truncated = next < text.Length,
                    NextCursor = next < text.Length ? ResourceReadCursor.CreateRevisionBound(next, hash, binding) : null },
                    ResourceRefs = new[] { descriptor.Reference.Copy() } };
            });
        }

        private ResourceDescriptor Describe(ChatSession session, string sheet, string range)
        {
            var descriptor = new ResourceDescriptor { Reference = new ResourceRef(ResourceUri.Create(Id, _scope.DocumentToken(session), "range", sheet, range)),
                Provider = Id, Kind = RangeKind, Title = sheet + "!" + range, MimeType = "application/json", Mutable = true,
                Tracking = "externally-observed" };
            descriptor.Representations.AddRange(new[] { "text", "formulas", "table", "records" });
            descriptor.Capabilities.Add("read");
            descriptor.Metadata["sheet"] = sheet; descriptor.Metadata["address"] = range;
            descriptor.Metadata["maximumSnapshotCells"] = ExcelReadService.MaxReadCells.ToString();
            return descriptor;
        }

        private ResourceAddress Parse(ChatSession session, string uri)
        {
            var address = ResourceUri.Parse(uri);
            if (address.Provider != Id || address.Segments.Count != 4 || address.Segments[1] != "range" ||
                !_scope.MatchesDocumentToken(session, address.Segments[0]) || NormalizeAddress(address.Segments[3]) != address.Segments[3])
                throw Error("RESOURCE_ACCESS_DENIED", "The range does not belong to this exact document scope.");
            return address;
        }

        private static string NormalizeAddress(string value)
        {
            value = (value ?? "").Replace("$", "").ToUpperInvariant();
            if (!Regex.IsMatch(value, @"\A[A-Z]{1,3}[1-9][0-9]{0,6}(?::[A-Z]{1,3}[1-9][0-9]{0,6})?\z"))
                throw Error("RESOURCE_TARGET_INVALID", "A bounded A1 rectangle is required.");
            CellCount(value);
            return value;
        }

        private static long CellCount(string value)
        {
            var parts = value.Split(':');
            var cells = parts.Select(part => {
                var match = Regex.Match(part, @"\A(?<column>[A-Z]+)(?<row>[0-9]+)\z");
                var column = 0;
                foreach (var character in match.Groups["column"].Value) column = column * 26 + character - 'A' + 1;
                var row = int.Parse(match.Groups["row"].Value);
                if (column < 1 || column > 16384 || row < 1 || row > 1048576) throw Error("RESOURCE_TARGET_INVALID", "A1 rectangle exceeds Excel bounds.");
                return new[] { column, row };
            }).ToArray();
            var first = cells[0]; var last = cells[cells.Length - 1];
            if (last[0] < first[0] || last[1] < first[1]) throw Error("RESOURCE_TARGET_INVALID", "Reversed A1 rectangles are not supported.");
            return (long)(last[0] - first[0] + 1) * (last[1] - first[1] + 1);
        }
        private static ResourceRequestException Error(string code, string message) { return new ResourceRequestException(message, code, false); }
    }
}
