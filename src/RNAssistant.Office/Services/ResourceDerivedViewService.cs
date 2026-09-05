using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    internal static class ResourceDerivedViewService
    {
        internal const string VirtualContentType = "application/vnd.rnassistant.virtual-derived+json";
        [ThreadStatic] private static int _depth;

        internal static ResourceReadSelection TryRead(ResourceGatewayService gateway, ResourceAuthorityService authority,
            ChatSession session, ResourceReadRequest request)
        {
            var address = ResourceUri.Parse(request.Reference.Uri);
            if (address.Provider != "state") return null;
            if (address.Segments.Count != 3 || address.Segments[0] != "conversation" || address.Segments[1] != session.Id)
                throw Error("RESOURCE_ACCESS_DENIED", "The derived resource belongs to another scope.");
            var binding = ResourceReadCursor.ProjectionBinding(request);
            var position = ResourceReadCursor.ParseExact(request, binding);
            var scope = authority.Scope(session, false);
            var frozen = authority.CaptureMany(new[] { scope }).Get(scope);
            var reference = request.Reference.IsExact ? request.Reference : frozen.GetHead(request.Reference.Identity)?.Revision;
            if (reference == null) return null;
            var metadata = ((IResourceRevisionStore)authority.Store).GetRevision(scope, reference);
            if (metadata?.Payload?.ContentType != VirtualContentType) return null;
            authority.RequirePublished(frozen, reference, session);
            if (!string.IsNullOrWhiteSpace(request.ViewPath) && request.ViewPath != "$")
                throw Error("RESOURCE_VIEW_UNSUPPORTED", "A virtual derived table exposes only its root record projection.");
            if (++_depth > 16) { _depth--; throw Error("RESOURCE_DERIVATION_DEPTH", "The exact derivation exceeds its bounded dependency depth."); }
            try
            {
                if (metadata.Payload.ByteLength > 128000) throw Error("RESOURCE_VIEW_UNAVAILABLE", "The derived definition exceeds its bound.");
                var definition = JsonConvert.DeserializeObject<ResourceDerivedDefinition>(ResourceSnapshotReadService.ReadPayload(authority.Payloads, metadata.Payload));
                if (definition?.Contract != "resource-derived-v1" || definition.Mode != DerivedResourceMode.Virtual)
                    throw Error("RESOURCE_VIEW_UNAVAILABLE", "The exact virtual definition is invalid.");
                var fields = request.Fields == null || request.Fields.Count == 0 ? definition.Fields.Select(item => item.Field).ToList() : request.Fields;
                if (fields.Any(field => !definition.Fields.Any(item => item.Field == field)) || fields.Distinct().Count() != fields.Count)
                    throw Error("RESOURCE_FIELD_UNAVAILABLE", "The field is not defined by this exact mapping.");
                var offset = string.IsNullOrEmpty(request.Cursor) ? request.RowOffset : position.Offset;
                var table = Project(gateway, session, definition, offset, request.MaxRows <= 0 ? 500 : request.MaxRows, fields);
                var next = offset + table.Rows.Count;
                var coverage = new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: offset, end: next, fields: fields);
                return new ResourceReadSelection { Result = new ResourceReadResult {
                    Resource = new ResourceDescriptor { Reference = reference.Copy(), Provider = "state", Title = definition.Name,
                        Kind = "derived", MimeType = "application/json", Mutable = true, Dependencies = metadata.Dependencies.ToList() },
                    Representation = request.Representation, Offset = offset, Table = table, ContentSha256 = metadata.ContentSha256,
                    Coverage = coverage, AuthorityGeneration = frozen.Generation, Complete = next == table.TotalRows, Truncated = next < table.TotalRows,
                    Payload = PayloadRef.FromBlob(authority.Payloads.StoreText(JsonConvert.SerializeObject(table), "application/json")),
                    NextCursor = next < table.TotalRows ? ResourceReadCursor.CreateRevisionBound(next, reference.Revision, binding) : null
                }, ResourceRefs = new[] { reference.Copy() } };
            }
            finally { _depth--; }
        }

        internal static IReadOnlyList<IDictionary<string, object>> Materialize(ResourceGatewayService gateway,
            ChatSession session, ResourceDerivedDefinition definition, CancellationToken cancellationToken)
        {
            var rows = new List<IDictionary<string, object>>();
            var characters = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = Project(gateway, session, definition, rows.Count, 500, definition.Fields.Select(item => item.Field).ToList());
                foreach (var row in table.Rows)
                {
                    characters += JsonConvert.SerializeObject(row).Length;
                    if (characters > 1900000 || rows.Count >= 100000)
                        throw Error("RESOURCE_SNAPSHOT_TOO_LARGE", "Select a narrower source or use a virtual derived resource.");
                    rows.Add(row);
                }
                if (rows.Count == table.TotalRows) return rows;
                if (table.Rows.Count == 0) throw Error("RESOURCE_VIEW_UNAVAILABLE", "The exact derivation made no progress.");
            }
        }

        private static ResourceTableBatch Project(ResourceGatewayService gateway, ChatSession session,
            ResourceDerivedDefinition definition, int offset, int limit, IReadOnlyList<string> fields)
        {
            if (offset < 0 || limit < 1 || limit > 32000 || definition.Source?.IsExact != true ||
                definition.Schema?.IsExact != true || definition.Mapping?.IsExact != true || definition.SkipRows < 0)
                throw Error("RESOURCE_VIEW_UNAVAILABLE", "A bounded, exact derivation is required.");
            var selected = definition.Fields.Where(item => fields.Contains(item.Field)).ToArray();
            var source = gateway.Read(session, new ResourceReadRequest { Reference = definition.Source.Copy(),
                Representation = "table", RowOffset = checked(offset + definition.SkipRows), MaxRows = limit,
                Fields = selected.Select(item => item.SourceField).Distinct(StringComparer.Ordinal).ToList() }).Result;
            ResourceRef schemaReference;
            var schema = ResourceDefinitionReader.Read<SemanticSchemaDefinition>(gateway, session, definition.Schema, out schemaReference);
            if (schema?.State != SemanticSchemaState.Published || schema.Contract != "resource-schema-v1")
                throw Error("RESOURCE_SCHEMA_UNAVAILABLE", "The exact derivation requires a published schema revision.");
            // Publication validates a sample only. Every subsequently emitted batch is validated too.
            var semanticFields = schema.Fields.Where(field => fields.Contains(field.Name)).ToArray();
            ResourceSchemaValidator.ValidateMapping(semanticFields, selected, source.Table);
            return new ResourceTableBatch {
                Columns = selected.Select(item => new ResourceTableColumn { Key = item.Field, Label = item.Field,
                    Type = semanticFields.Single(field => field.Name == item.Field).Type }).ToArray(),
                Rows = source.Table.Rows.Select(row => (IDictionary<string, object>)selected.ToDictionary(item => item.Field,
                    item => row[item.SourceField], StringComparer.Ordinal)).ToArray(),
                TotalRows = Math.Max(0, source.Table.TotalRows - definition.SkipRows)
            };
        }
        private static ResourceRequestException Error(string code, string message) { return new ResourceRequestException(message, code, false); }
    }
}
