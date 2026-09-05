using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using Result = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceDefinitionToolHandler : IToolHandler
    {
        internal const string Draft = "common.resources_schema_draft";
        internal const string Publish = "common.resources_schema_publish";
        internal const string Mapping = "common.resources_mapping_publish";
        internal const string Derive = "common.resources_derive";
        internal static readonly string[] Ids = { Draft, Publish, Mapping, Derive };
        private readonly ResourceGatewayService _gateway;
        private readonly ChatSession _session;
        private readonly string _operation;
        internal ResourceDefinitionToolHandler(ResourceGatewayService gateway, ChatSession session, string operation)
        { _gateway = gateway; _session = session; _operation = operation; }
        internal static bool Owns(string id) { return Ids.Contains(id, StringComparer.Ordinal); }
        internal static ToolBinding BindingFor(string id) { return Owns(id) ? new ToolBinding(id + ".v1") : null; }
        internal static string NodeName(string operation, IDictionary<string, object> arguments)
        {
            var name = ToolArgumentReader.String(arguments, "name", "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(name, @"\A[a-z][a-z0-9._-]{0,63}\z")) throw new InvalidOperationException("A stable resource name is required.");
            return (operation == Draft ? "schema-draft-" : operation == Publish ? "schema-published-" : operation == Mapping ? "mapping-" : "derived-") + name;
        }
        internal static IEnumerable<ToolCatalogEntry> Catalog()
        {
            foreach (var id in Ids)
            {
                var action = id == Draft ? "Propose a versioned semantic schema draft. Drafts are not authoritative; field names/types/units have no Excel coordinates." :
                    id == Publish ? "Validate a draft against a bounded exact source sample and explicitly publish its semantic schema. Sample coverage is recorded, never claimed as whole-data validation." :
                    id == Mapping ? "Publish a versioned physical-field mapping from an exact source to a published semantic schema. Mapping owns coordinates/field selection, not the schema." :
                    "Create a derived resource from an exact mapping: virtual computes bounded rows on demand; materialized stores an immutable bounded CAS output. Provenance never grants activation authority.";
                yield return ControllerToolCatalogEntry.CreateTypedProjection(new ToolDescriptor(id, action, Schema(id)),
                    new ToolPolicy(ToolEffect.Write, ToolVerification.Tool, false, false, new[] { "agent" }, 1),
                    name: id.Substring("common.".Length), scope: "session", mutatesLocalState: true);
            }
        }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            using (DocumentAccessGate.BeginOperation())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var args = context.Arguments;
                var identity = ResourceStateProvider.Identity(_gateway.Authority.Scope(_session, false), NodeName(_operation, args));
                var name = ToolArgumentReader.String(args, "name", "").ToLowerInvariant();
                var dependencies = new List<ResourceDependency>();
                object definition;
                var contentType = "application/json";
                if (_operation == Draft)
                {
                    var fields = ReadArgument<List<SemanticResourceField>>(args, "fields");
                    ResourceSchemaValidator.ValidateFields(fields);
                    definition = new SemanticSchemaDefinition { Name = name, State = SemanticSchemaState.Draft, Fields = fields };
                }
                else if (_operation == Publish || _operation == Mapping)
                {
                    var schemaTarget = ToolArgumentReader.String(args, _operation == Publish ? "draft" : "schema", "");
                    ResourceRef schemaReference;
                    var schema = ReadDefinition<SemanticSchemaDefinition>(schemaTarget, out schemaReference);
                    if (schema.Contract != "resource-schema-v1" || schema.State != (_operation == Publish ? SemanticSchemaState.Draft : SemanticSchemaState.Published))
                        throw new InvalidOperationException("An exact " + (_operation == Publish ? "draft" : "published schema") + " is required.");
                    ResourceSchemaValidator.ValidateFields(schema.Fields);
                    var mapping = ReadArgument<List<ResourceFieldMapping>>(args, "mapping");
                    var skipRows = ToolArgumentReader.Int32(args, "skipRows", 0);
                    var source = _gateway.ResolveIntentTarget(_session, ToolArgumentReader.String(args, "source", "")).Reference;
                    var sample = _gateway.Read(_session, new ResourceReadRequest { Reference = source, Representation = "table", MaxRows = 128, RowOffset = skipRows }).Result;
                    ResourceSchemaValidator.ValidateMapping(schema.Fields, mapping, sample.Table);
                    if (_operation == Publish)
                    {
                        definition = new SemanticSchemaDefinition { Name = name, State = SemanticSchemaState.Published,
                            Fields = schema.Fields, ValidationSource = sample.Resource.Reference.Copy(), ValidationCoverage = sample.Coverage,
                            ValidationRows = sample.Table.Rows.Count, ValidationComplete = sample.Complete && skipRows == 0 };
                        dependencies.Add(new ResourceDependency(schemaReference, "text", ResourceCoverage.Whole(), "immutable-snapshot"));
                        dependencies.Add(new ResourceDependency(sample.Resource.Reference, "table", sample.Coverage, "immutable-snapshot"));
                    }
                    else
                    {
                        definition = new ResourceMappingDefinition { Name = name, Source = sample.Resource.Reference.Copy(),
                            Schema = schemaReference, Fields = mapping, SkipRows = skipRows, ValidationCoverage = sample.Coverage,
                            SourceDependencies = sample.Resource.Dependencies.ToList() };
                        dependencies.Add(new ResourceDependency(schemaReference, "text", ResourceCoverage.Whole(), "schema"));
                        dependencies.Add(new ResourceDependency(sample.Resource.Reference, "table", ResourceCoverage.Whole(), "source"));
                        dependencies.AddRange(sample.Resource.Dependencies);
                    }
                }
                else
                {
                    ResourceRef mappingReference;
                    var mapping = ReadDefinition<ResourceMappingDefinition>(ToolArgumentReader.String(args, "mapping", ""), out mappingReference);
                    if (mapping.Contract != "resource-mapping-v1" || mapping.Source?.IsExact != true || mapping.Schema?.IsExact != true)
                        throw new InvalidOperationException("An exact published mapping is required.");
                    var mode = ToolArgumentReader.String(args, "mode", "virtual");
                    if (mode != "virtual" && mode != "materialized") throw new InvalidOperationException("Unsupported derived mode.");
                    var derived = new ResourceDerivedDefinition { Name = name, Mode = mode == "virtual" ? DerivedResourceMode.Virtual : DerivedResourceMode.Materialized,
                        Mapping = mappingReference, Source = mapping.Source, Schema = mapping.Schema, Fields = mapping.Fields, SkipRows = mapping.SkipRows,
                        SourceDependencies = mapping.SourceDependencies };
                    dependencies.Add(new ResourceDependency(mappingReference, "text", ResourceCoverage.Whole(), "mapping"));
                    dependencies.Add(new ResourceDependency(mapping.Schema, "text", ResourceCoverage.Whole(), "schema"));
                    dependencies.Add(new ResourceDependency(mapping.Source, "table", ResourceCoverage.Whole(), "source"));
                    dependencies.AddRange(mapping.SourceDependencies ?? new List<ResourceDependency>());
                    if (mode == "virtual")
                    { definition = derived; contentType = ResourceDerivedViewService.VirtualContentType; }
                    else definition = ResourceDerivedViewService.Materialize(_gateway, _session, derived, cancellationToken);
                }
                var json = JsonConvert.SerializeObject(definition);
                if (json.Length > 2000000) throw new InvalidOperationException("The materialized output exceeds its resource bound.");
                cancellationToken.ThrowIfCancellationRequested();
                context.MarkDispatchPossible();
                var payload = PayloadRef.FromBlob(_gateway.Authority.Payloads.StoreText(json, contentType));
                return Task.FromResult(context.Complete(new ToolHandlerResult(Result.Ok("Resource definition captured and published by canonical authority.",
                    JsonConvert.SerializeObject(new DefinitionResult { Name = name, Kind = NodeName(_operation, args).Substring(0, NodeName(_operation, args).Length - name.Length).TrimEnd('-') })),
                    ToolEffectEvidence.VerifiedChange, resourceReadBack: new[] {
                        new ResourceMutationReadBack(identity, true, "text", payload.Sha256, payload, dependencies: dependencies) })));
            }
        }

        private T ReadDefinition<T>(string target, out ResourceRef reference)
        {
            return ResourceDefinitionReader.Read<T>(_gateway, _session,
                _gateway.ResolveIntentTarget(_session, target).Reference, out reference);
        }

        private static T ReadArgument<T>(IDictionary<string, object> arguments, string name)
        {
            object value;
            if (!arguments.TryGetValue(name, out value)) throw new InvalidOperationException(name + " is required.");
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        }
        private static string Schema(string id)
        {
            Func<string, JObject> text = description => new JObject { ["type"] = "string", ["description"] = description, ["minLength"] = 1, ["maxLength"] = 1000 };
            var properties = new JObject { ["name"] = text("Stable resource definition name, lowercase letters/digits/dot/hyphen/underscore, at most 64 characters.") };
            var required = new JArray("name");
            if (id == Draft)
            {
                properties["fields"] = new JObject { ["type"] = "array", ["description"] = "Reusable semantic fields, without physical coordinates.", ["minItems"] = 1, ["maxItems"] = 128,
                    ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject {
                        ["name"] = text("Unique semantic field key."), ["type"] = new JObject { ["type"] = "string", ["description"] = "Semantic value type.", ["enum"] = new JArray("string", "number", "integer", "boolean", "date", "any") },
                        ["nullable"] = new JObject { ["type"] = "boolean", ["description"] = "Whether null is valid." }, ["unit"] = text("Optional semantic measurement unit.") },
                        ["required"] = new JArray("name", "type"), ["additionalProperties"] = false } };
                required.Add("fields");
            }
            else if (id == Derive)
            {
                properties["mapping"] = text("Exact semantic target of a published mapping.");
                properties["mode"] = new JObject { ["type"] = "string", ["description"] = "Virtual bounded computation or immutable materialized CAS output.", ["enum"] = new JArray("virtual", "materialized") };
                required.Add("mapping");
            }
            else
            {
                var field = id == Publish ? "draft" : "schema";
                properties[field] = text("Exact semantic target of the " + field + ".");
                properties["source"] = text("Exact semantic source target with a structural table view.");
                properties["skipRows"] = new JObject { ["type"] = "integer", ["description"] = "Explicit leading physical rows to skip (for example one header row).", ["minimum"] = 0, ["maximum"] = 100 };
                properties["mapping"] = new JObject { ["type"] = "array", ["description"] = "Explicit physical-to-semantic field mapping.", ["minItems"] = 1, ["maxItems"] = 128,
                    ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["field"] = text("Semantic field key."), ["sourceField"] = text("Exact structural source field key.") },
                        ["required"] = new JArray("field", "sourceField"), ["additionalProperties"] = false } };
                required.Add(field); required.Add("source"); required.Add("mapping");
            }
            return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }.ToString(Formatting.None);
        }
        private sealed class DefinitionResult
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("kind")] public string Kind { get; set; }
        }
    }
}
