using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Tools;
using RNAssistant.Office.Services;
using RuntimeResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Tools
{
    internal sealed class ResourceReadToolHandler : ResourceToolHandlerBase
    {
        private const int InternalReadCharacters = ResourceReadRequest.MaximumCharacters;
        internal static readonly ToolDescriptor Descriptor = new ToolDescriptor(
            ResourceToolCatalog.ReadToolId,
            "Read-only: Read a semantic target supplied by RUNTIME_CONTEXT, common.resources_find, workspace member results or binding sourceTarget. Copy target verbatim, follow its usage and supported representations; it never contains :// and must not be constructed from a title. Do not read an Excel search scope to enumerate worksheet data: use excel.find_cells for discovery or an Excel range/table/name target for values. table/records return bounded row coverage with optional fields, offset and limit; omit path for Office targets because runtime applies their canonical record view. Text/source/structure read a complete representation by default. For document Markdown/Plans, section with representation=text selects a unique ATX heading and its nested subsections, bounded to 32000 characters. Section coverage never proves a whole-resource read; duplicate/missing/oversized sections fail explicitly. For a project-wide VBA request, read RUNTIME_CONTEXT.document.vba_project_target with representation=structure first. Exact URI, revision, cursor and guards remain runtime-owned. Media is hydrated only for the next model step; base64 is never embedded in JSON.",
            Parameters());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.read.intent.v2");
        private readonly Action<string, IReadOnlyList<ChatAttachment>> _captureAttachments;

        internal ResourceReadToolHandler(ResourceGatewayService gateway, ChatSession session,
            Action<string, IReadOnlyList<ChatAttachment>> captureAttachments)
            : base(gateway, session)
        {
            _captureAttachments = captureAttachments;
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context)
        {
            var target = ToolArgumentReader.String(
                context.Arguments, "target", string.Empty).Trim();
            var selected = Gateway.ResolveIntentTarget(Session, target);
            // A semantic mutable target requests its current head, not the last
            // observed revision from discovery. Internal pages pin the first read.
            var reference = selected.Descriptor.Mutable ? new ResourceRef(selected.Reference.Uri) : selected.Reference;
            var representation = ToolArgumentReader.String(context.Arguments, "representation", "auto");
            // The workspace root has a structure view, but its file members have
            // source. A model may carry the root's view over to an exact file target.
            // Negotiate this one read explicitly; the result still reports source.
            var htmlFileStructureAsSource = representation == ResourceRepresentations.Structure &&
                string.Equals(selected.Descriptor.Kind, ChatHtmlResourceCatalog.FileKind, StringComparison.Ordinal) &&
                selected.Descriptor.Representations.Contains(ResourceRepresentations.Source);
            if (htmlFileStructureAsSource) representation = ResourceRepresentations.Source;
            var structured = representation == "table" || representation == "records";
            var section = ToolArgumentReader.String(context.Arguments, "section", null);
            if (section != null && representation != "text")
                throw new ResourceRequestException("section requires representation=text.", "resource_section_unsupported", false);
            if (!structured && new[] { "limit", "offset", "path", "fields" }.Any(context.Arguments.ContainsKey))
                throw new ResourceRequestException("Structural selectors require representation=table or records.", "RESOURCE_VIEW_UNSUPPORTED", false);
            var viewPath = structured
                ? ResourceSelectorContract.ResolvePath(selected, representation,
                    ToolArgumentReader.String(context.Arguments, "path", null))
                : null;
            var selection = section != null
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = "text", Section = section, MaxChars = InternalReadCharacters })
                : structured
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = representation,
                    MaxRows = ToolArgumentReader.Int32(context.Arguments, "limit", 500),
                    RowOffset = ToolArgumentReader.Int32(context.Arguments, "offset", 0),
                    ViewPath = viewPath,
                    Fields = Fields(context.Arguments) })
                : Gateway.ReadWhole(Session, reference, representation);
            var projection = Project(
                selection,
                selected.Target,
                selected.Type,
                selected.Scope);
            projection.Section = section;
            // These bodies are runtime metadata, not user JSON/source. Publish
            // addressable semantic targets instead of opaque resource references.
            if (selected.Type == "HTML data" && selection.Result.Representation == "text")
            {
                var binding = JsonConvert.DeserializeObject<HtmlWorkspaceDataBinding>(selection.Result.Text);
                projection.Text = Serialize(Gateway.DescribeHtmlBinding(Session, selected.Descriptor.Title, binding));
                projection.ReturnedCharacters = projection.TotalCharacters = projection.Text.Length;
            }
            else if (selected.Type == "HTML workspace" && selection.Result.Representation == "structure")
            {
                var manifest = JsonConvert.DeserializeObject<HtmlWorkspaceResourceManifest>(selection.Result.Text);
                projection.Text = Serialize(new HtmlWorkspaceReadInfo {
                    Members = manifest.Resources.Select(member => Gateway.DescribeHtmlMember(Session, member)).ToList() });
                projection.ReturnedCharacters = projection.TotalCharacters = projection.Text.Length;
            }
            var result = RuntimeResult.Ok(
                htmlFileStructureAsSource
                    ? "HTML file has no structure view; returned its complete source representation instead. The HTML workspace root has the structure view."
                    : section != null ? "Complete selected Markdown section read; coverage is limited to that section." : structured ? "Bounded exact structural view read." : "Complete resource representation read.",
                Serialize(projection),
                selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: Gateway.Evidence(Session, selection.Result));
        }

        private static ResourceReadProjection Project(
            ResourceReadSelection selection,
            string target,
            string type,
            string scope)
        {
            if (selection == null || selection.Result == null)
                throw new InvalidOperationException("Resource provider returned no read result.");
            var result = selection.Result;
            return new ResourceReadProjection
            {
                Kind = "resource-read",
                Target = target,
                Type = type,
                Scope = scope,
                Representation = result.Representation,
                Text = result.Text,
                Table = result.Table,
                Coverage = result.Coverage,
                Offset = result.Offset,
                ReturnedCharacters = result.ReturnedCharacters,
                TotalCharacters = result.TotalCharacters,
                Complete = result.Complete,
                HydratedForNextModelStep = result.HydratedForNextModelStep,
                RawContentIncluded = result.RawContentIncluded
            };
        }

        private static string Parameters()
        {
            var target = "\"target\":" + ResourceSelectorContract.Target().ToString(Formatting.None);
            const string representation = "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read. Complete views cannot be combined with table/records selectors.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\",\"table\",\"records\"]}";
            const string section = "\"section\":{\"type\":\"string\",\"description\":\"Exact unique ATX heading title copied from Markdown, without leading #. Includes nested subsections; maximum selected text is 32000 characters. Does not grant whole-resource read evidence.\",\"minLength\":1,\"maxLength\":200}";
            var selectors = "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum rows in this table/records batch.\",\"minimum\":1,\"maximum\":5000},\"offset\":{\"type\":\"integer\",\"description\":\"Zero-based table/records row offset.\",\"minimum\":0}" + ",\"path\":" + ResourceSelectorContract.RecordPath().ToString(Formatting.None) + ",\"fields\":{\"type\":\"array\",\"description\":\"Exact column keys from returned columns metadata, not display headings or translated aliases.\",\"maxItems\":128,\"items\":{\"type\":\"string\",\"maxLength\":128}}";
            return "{\"type\":\"object\",\"properties\":{" + target + "," + representation + "," + section + "," + selectors +
                "},\"required\":[\"target\"],\"additionalProperties\":false,\"anyOf\":[" +
                "{\"type\":\"object\",\"description\":\"Read one complete metadata, text, structure, source, media, or formulas representation. Do not send limit, offset, path, or fields.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Complete representation to read; omit for provider-selected auto.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\"]}},\"required\":[\"target\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read bounded rows. Structural selectors are valid only in this table/records branch.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Bounded structural representation to read.\",\"enum\":[\"table\",\"records\"]}," +
                selectors +
                "},\"required\":[\"target\",\"representation\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read one uniquely named document Markdown/Plan section.\",\"properties\":{" + target + "," + section + "," +
                "\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"section\"],\"additionalProperties\":false}]}";
        }

        private static List<string> Fields(IDictionary<string, object> arguments)
        {
            object value;
            if (!arguments.TryGetValue("fields", out value) || value == null) return null;
            return JsonConvert.DeserializeObject<List<string>>(JsonConvert.SerializeObject(value));
        }

        private sealed class ResourceReadProjection
        {
            [JsonProperty("section", NullValueHandling = NullValueHandling.Ignore)]
            public string Section { get; set; }
            [JsonProperty("kind")]
            public string Kind { get; set; }
            [JsonProperty("target")]
            public string Target { get; set; }
            [JsonProperty("type")]
            public string Type { get; set; }
            [JsonProperty("scope")]
            public string Scope { get; set; }
            [JsonProperty("representation")]
            public string Representation { get; set; }
            [JsonProperty("text")]
            public string Text { get; set; }
            [JsonProperty("table", NullValueHandling = NullValueHandling.Ignore)]
            public ResourceTableBatch Table { get; set; }
            [JsonProperty("coverage")]
            public ResourceCoverage Coverage { get; set; }
            [JsonProperty("offset")]
            public int Offset { get; set; }
            [JsonProperty("returnedCharacters")]
            public int ReturnedCharacters { get; set; }
            [JsonProperty("totalCharacters")]
            public int TotalCharacters { get; set; }
            [JsonProperty("complete")]
            public bool Complete { get; set; }
            [JsonProperty("hydratedForNextModelStep")]
            public bool HydratedForNextModelStep { get; set; }
            [JsonProperty("rawContentIncluded")]
            public bool RawContentIncluded { get; set; }
        }
    }
}
