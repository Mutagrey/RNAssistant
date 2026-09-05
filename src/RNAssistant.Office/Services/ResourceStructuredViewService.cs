using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Office.Services
{
    // A structural view owner. Index metadata and bounded parts use the same
    // immutable revision journal and CAS; this service owns no freshness state.
    internal sealed class ResourceStructuredViewService
    {
        private const int MaximumSourceCharacters = 2000000;
        private const int MaximumRows = 100000;
        private const int PartRows = 256;
        private readonly ResourceGatewayService _gateway;
        private readonly ResourceAuthorityService _authority;
        private readonly IResourceRevisionStore _revisions;
        private readonly ChatBlobStore _payloads;
        internal ResourceStructuredViewService(ResourceGatewayService gateway, ResourceAuthorityService authority)
        {
            _gateway = gateway; _authority = authority;
            _revisions = (IResourceRevisionStore)authority.Store; _payloads = authority.Payloads;
        }

        internal ResourceReadSelection Read(ChatSession session, ResourceReadRequest request, bool live)
        {
            var path = string.IsNullOrWhiteSpace(request.ViewPath) ? "$" : request.ViewPath;
            if (path.Length > 256 || !Regex.IsMatch(path, @"\A\$(?:\.[A-Za-z_][A-Za-z0-9_]*)*\z"))
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "Only an explicit object-property path is supported by this records view.");
            var binding = ResourceReadCursor.ProjectionBinding(request);
            var position = ResourceReadCursor.ParseExact(request, binding);
            var scope = _authority.ScopeFor(session, request.Reference, live);
            _authority.CaptureMany(new[] { scope });
            var reference = request.Reference;
            var indexView = "record-index-v1:" + path;
            ResourceRevisionView captured = reference.IsExact ? _revisions.GetView(scope, reference, indexView) : null;
            ResourceDescriptor descriptor;
            long? generation;
            if (captured == null)
            {
                ResourceReadResult first = null;
                var text = new StringBuilder();
                string cursor = null;
                do
                {
                    var selected = _gateway.Read(session, new ResourceReadRequest { Reference = reference,
                        Representation = "text", Cursor = cursor, MaxChars = 32000 });
                    var part = selected.Result;
                    if (first == null)
                    {
                        first = part; reference = part.Resource.Reference.Copy();
                        if (!(first.Resource.MimeType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                            throw Error("RESOURCE_VIEW_UNSUPPORTED", "This provider has no JSON structural table/records capability.");
                    }
                    if (part.Resource.Reference.Revision != first.Resource.Reference.Revision ||
                        part.Offset != text.Length || part.ContentSha256 != first.ContentSha256)
                        throw Error("RESOURCE_REVISION_CHANGED", "The source changed during snapshot capture. No mixed-revision view was published.");
                    if (part.TotalCharacters > MaximumSourceCharacters || text.Length + (part.Text ?? "").Length > MaximumSourceCharacters)
                        throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Select a narrower resource before materializing this structural view.");
                    text.Append(part.Text);
                    cursor = part.NextCursor;
                    if (!part.Complete && (string.IsNullOrEmpty(cursor) || part.ReturnedCharacters == 0))
                        throw Error("RESOURCE_VIEW_UNAVAILABLE", "The source cannot establish a complete bounded snapshot.");
                    if (part.Complete) break;
                } while (true);
                descriptor = first.Resource; generation = first.AuthorityGeneration;
                captured = Materialize(scope, reference, first.ContentSha256, path, text.ToString(), indexView);
            }
            else
            {
                descriptor = new ResourceDescriptor { Reference = reference.Copy(), Provider = ResourceUri.Parse(reference.Uri).Provider,
                    MimeType = "application/json", Title = reference.Identity.Uri, Mutable = live };
                descriptor.Dependencies = _revisions.GetRevision(scope, reference).Dependencies.ToList();
                generation = _authority.Store.Capture(scope).Generation;
            }
            var index = JsonConvert.DeserializeObject<RecordIndex>(RequiredText(captured.Payload, 1024 * 1024));
            if (index == null || index.Columns == null || index.Parts == null || index.RowCount > MaximumRows)
                throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "The exact structural index is unavailable.");
            var fields = request.Fields == null || request.Fields.Count == 0 ? index.Columns.Select(item => item.Key).ToArray() : request.Fields.ToArray();
            if (fields.Length > 1024 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                fields.Any(field => !index.Columns.Any(column => column.Key == field)))
                throw Error("RESOURCE_FIELD_UNAVAILABLE", "The requested fields are not present in this exact view.");
            // The first read may resolve a logical artifact URI to its exact address.
            binding = ResourceReadCursor.ProjectionBinding(request, reference.Uri);
            var offset = string.IsNullOrEmpty(request.Cursor) ? request.RowOffset : position.Offset;
            var limit = request.MaxRows <= 0 ? 500 : request.MaxRows;
            if (offset < 0 || offset > index.RowCount || limit < 1 || limit > 32000)
                throw Error("RESOURCE_BATCH_TOO_LARGE", "Requested rows exceed the bounded structural view.");
            var rows = new List<IDictionary<string, object>>();
            var bytes = 0;
            foreach (var part in index.Parts.Where(item => item.Start + item.Count > offset && item.Start < (long)offset + limit))
            {
                var values = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(RequiredText(part.Payload, 2 * 1024 * 1024));
                if (values == null || values.Count != part.Count) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A structural part is incomplete.");
                foreach (var value in values.Skip(Math.Max(0, offset - part.Start)))
                {
                    var projected = fields.ToDictionary(field => field, field => value.ContainsKey(field) ? value[field] : null, StringComparer.Ordinal);
                    var size = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(projected));
                    if (bytes + size > 2 * 1024 * 1024 || rows.Count == limit) break;
                    rows.Add(projected); bytes += size;
                }
                if (rows.Count == limit || bytes >= 2 * 1024 * 1024) break;
                if (offset + rows.Count < part.Start + part.Count) break;
            }
            if (offset < index.RowCount && rows.Count == 0) throw Error("RESOURCE_BATCH_TOO_LARGE", "One record exceeds the negotiated batch limit.");
            var next = offset + rows.Count;
            var coverage = new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: offset, end: next, path: path, fields: fields);
            descriptor.Reference = reference.Copy();
            descriptor.Representations.Add("table"); descriptor.Representations.Add("records");
            var result = new ResourceReadResult { Resource = descriptor, Representation = request.Representation,
                ContentSha256 = captured.ContentSha256, AuthorityGeneration = generation, Offset = offset,
                Table = new ResourceTableBatch { Columns = index.Columns.Where(column => fields.Contains(column.Key)).ToArray(), Rows = rows, TotalRows = index.RowCount },
                Complete = next == index.RowCount, Truncated = next < index.RowCount, Coverage = coverage,
                NextCursor = next < index.RowCount ? ResourceReadCursor.CreateRevisionBound(next, reference.Revision, binding) : null };
            result.Payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(result.Table), "application/json"));
            return new ResourceReadSelection { Result = result, ResourceRefs = new[] { reference.Copy() } };
        }

        private ResourceRevisionView Materialize(ResourceAuthorityScopeId scope, ResourceRef reference, string hash,
            string path, string text, string view)
        {
            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal, MaxDepth = 64 })
                {
                    root = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw Error("RESOURCE_VIEW_INVALID", "Structural JSON must contain exactly one value.");
                }
            }
            catch (JsonException)
            { throw Error("RESOURCE_VIEW_INVALID", "Structural JSON must be unambiguous and bounded. Use text for exact source lexemes."); }
            var source = path == "$" ? root : root.SelectToken(path, false);
            var array = source as JArray;
            if (array == null) throw Error("RESOURCE_VIEW_UNSUPPORTED", "The selected JSON path is not a record array.");
            if (array.Count > MaximumRows) throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "The selected record array exceeds the materialization bound.");
            var rows = new List<IDictionary<string, object>>();
            var keys = new List<string>();
            foreach (var value in array)
            {
                var record = value as JObject;
                if (record == null && value is JArray)
                    record = new JObject(((JArray)value).Select((cell, i) => new JProperty("c" + (i + 1), cell.DeepClone())));
                if (record == null) throw Error("RESOURCE_VIEW_UNSUPPORTED", "Records must be objects or positional row arrays.");
                foreach (var number in record.Descendants().Where(item => item.Type == JTokenType.Integer))
                {
                    decimal valueNumber;
                    if (!decimal.TryParse(number.ToString(Formatting.None), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out valueNumber) || Math.Abs(valueNumber) > 9007199254740991m)
                        throw Error("RESOURCE_NUMBER_PRECISION_UNSUPPORTED", "This table contains an integer outside the lossless JavaScript range. Read the exact text view or use explicit string identifiers.");
                }
                var row = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in record.Properties())
                {
                    if (!keys.Contains(property.Name)) keys.Add(property.Name);
                    if (keys.Count > 1024) throw Error("RESOURCE_VIEW_UNSUPPORTED", "The table has too many structural fields.");
                    var scalar = property.Value as JValue;
                    row[property.Name] = scalar == null ? (object)property.Value.DeepClone() : scalar.Value;
                }
                rows.Add(row);
            }
            var index = new RecordIndex { RowCount = rows.Count, Columns = keys.Select(key =>
                new ResourceTableColumn { Key = key, Label = key, Type = "json" }).ToList(), Parts = new List<RecordPart>() };
            for (var start = 0; start < rows.Count; start += PartRows)
            {
                var json = JsonConvert.SerializeObject(rows.Skip(start).Take(PartRows));
                if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024)
                    throw Error("RESOURCE_BATCH_TOO_LARGE", "Select narrower fields before snapshot materialization.");
                index.Parts.Add(new RecordPart { Start = start, Count = Math.Min(PartRows, rows.Count - start),
                    Payload = PayloadRef.FromBlob(_payloads.StoreText(json, "application/json")) });
            }
            var payload = PayloadRef.FromBlob(_payloads.StoreText(JsonConvert.SerializeObject(index), "application/vnd.rnassistant.record-index+json"));
            var captured = new ResourceRevisionView(reference, view, hash, payload, ResourceCoverage.Whole(), index.Parts.Select(item => item.Payload));
            _revisions.RegisterView(scope, captured);
            return captured;
        }

        private string RequiredText(PayloadRef payload, long maximumBytes)
        {
            if (payload == null || payload.ByteLength > maximumBytes) throw Error("RESOURCE_BATCH_TOO_LARGE", "The exact payload exceeds its bounded view.");
            var text = _payloads.ReadText(payload.ToBlobReference());
            if (text == null) throw Error("RESOURCE_SNAPSHOT_UNAVAILABLE", "A retained structural payload is unavailable.");
            return text;
        }
        private static ResourceRequestException Error(string code, string message) { return new ResourceRequestException(message, code, false); }
        private sealed class RecordIndex
        {
            public int RowCount { get; set; }
            public List<ResourceTableColumn> Columns { get; set; }
            public List<RecordPart> Parts { get; set; }
        }
        private sealed class RecordPart
        {
            public int Start { get; set; }
            public int Count { get; set; }
            public PayloadRef Payload { get; set; }
        }
    }
}
