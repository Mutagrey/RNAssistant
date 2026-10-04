using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
            "Read-only: Read a semantic target supplied by RUNTIME_CONTEXT, common.resources_find, workspace member results or binding sourceTarget. Copy target verbatim, follow its usage and supported representations; it never contains :// and must not be constructed from a title. Do not read an Excel search scope to enumerate worksheet data: use excel.find_cells for discovery or an Excel range/table/name target for values. table/records return bounded row coverage with optional fields, offset and limit; omit path for Office targets because runtime applies their canonical record view. Text/source/structure read a complete representation by default. For a large text/source, supply 1-based startLine and lineCount (1-500, at most 32000 characters) for an exact excerpt; excerpts cannot authorize a whole-file overwrite. For document Markdown/Plans, section with representation=text selects a unique ATX heading and its nested subsections, bounded to 32000 characters. Section coverage never proves a whole-resource read; duplicate/missing/oversized sections fail explicitly. For a project-wide VBA request, read RUNTIME_CONTEXT.document.vba_project_target with representation=structure first. Exact URI, revision, cursor and guards remain runtime-owned. Media is hydrated only for the next model step; base64 is never embedded in JSON.",
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
            var lines = context.Arguments.ContainsKey("startLine") || context.Arguments.ContainsKey("lineCount");
            if (lines && (selected.Type == "shared context" || selected.Type == "HTML data" || selected.Type == "HTML workspace"))
                throw new ResourceRequestException("This target requires its complete semantic projection. Select a file/source target for line reads.", "resource_lines_unsupported", false);
            if (lines && (section != null || structured || !context.Arguments.ContainsKey("startLine") ||
                !context.Arguments.ContainsKey("lineCount") || representation != "source" && representation != "text"))
                throw new ResourceRequestException("Line selection requires source/text, startLine and lineCount, without section or row selectors.", "resource_lines_invalid", false);
            if (section != null && representation != "text")
                throw new ResourceRequestException("section requires representation=text.", "resource_section_unsupported", false);
            if (!structured && new[] { "limit", "offset", "path", "fields" }.Any(context.Arguments.ContainsKey))
                throw new ResourceRequestException("Structural selectors require representation=table or records.", "RESOURCE_VIEW_UNSUPPORTED", false);
            var viewPath = structured
                ? ResourceSelectorContract.ResolvePath(selected, representation,
                    ToolArgumentReader.String(context.Arguments, "path", null))
                : null;
            var selection = lines
                ? Gateway.ReadLines(Session, reference, representation,
                    ToolArgumentReader.Int32(context.Arguments, "startLine", 1), ToolArgumentReader.Int32(context.Arguments, "lineCount", 100))
                : section != null
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = "text", Section = section, MaxChars = InternalReadCharacters })
                : structured
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = representation,
                    MaxRows = ToolArgumentReader.Int32(context.Arguments, "limit", 500),
                    RowOffset = ToolArgumentReader.Int32(context.Arguments, "offset", 0),
                    ViewPath = viewPath,
                    Fields = Fields(context.Arguments) })
                : Gateway.ReadWhole(Session, reference, representation);
            var observation = new ResourceReadObservation(selection.Result,
                Gateway.Evidence(Session, selection.Result));
            var projection = ResourceReadProjection.From(observation.Result,
                selected.Target, selected.Type, selected.Scope);
            projection.Section = section;
            if (lines) { projection.StartLine = ToolArgumentReader.Int32(context.Arguments, "startLine", 1);
                projection.RequestedLines = ToolArgumentReader.Int32(context.Arguments, "lineCount", 100); }
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
                    : lines ? "Exact selected lines read; coverage is limited to this excerpt, not the whole resource."
                    : section != null ? "Complete selected Markdown section read; coverage is limited to that section." : structured ? "Bounded exact structural view read." : "Complete resource representation read.",
                Serialize(projection),
                selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: observation.Evidence);
        }

        internal static ToolResultSummary Summarize(JObject data)
        {
            if (data?["kind"]?.Type != JTokenType.String ||
                (string)data["kind"] != "resource-read" ||
                data["representation"]?.Type != JTokenType.String ||
                data["complete"]?.Type != JTokenType.Boolean) return null;
            var representation = data.Value<string>("representation");
            if (representation.Length > 32) return null;
            var rows = (data["table"] as JObject)?["rows"] as JArray;
            var characters = data["returnedCharacters"]?.Type == JTokenType.Integer
                ? data.Value<int?>("returnedCharacters") : null;
            return new ToolResultSummary {
                Kind = "resource-read",
                Representation = representation,
                ReturnedCharacters = characters,
                ReturnedRows = rows?.Count,
                Complete = data.Value<bool>("complete"),
                HydratedForNextModelStep = representation == "media" &&
                    data["hydratedForNextModelStep"]?.Type == JTokenType.Boolean &&
                    data.Value<bool>("hydratedForNextModelStep")
            };
        }

        private static string Parameters()
        {
            var target = "\"target\":" + ResourceSelectorContract.Target().ToString(Formatting.None);
            const string representation = "\"representation\":{\"type\":\"string\",\"description\":\"Representation to read. Complete views cannot be combined with table/records selectors.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\",\"table\",\"records\"]}";
            const string section = "\"section\":{\"type\":\"string\",\"description\":\"Exact unique ATX heading title copied from Markdown, without leading #. Includes nested subsections; maximum selected text is 32000 characters. Does not grant whole-resource read evidence.\",\"minLength\":1,\"maxLength\":200}";
            const string lines = "\"startLine\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"1-based first source/text line. Use with lineCount for an exact excerpt.\"},\"lineCount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"description\":\"Maximum selected lines; the excerpt must fit 32000 characters. Not whole-file observation.\"}";
            var selectors = "\"limit\":{\"type\":\"integer\",\"description\":\"Maximum rows in this table/records batch.\",\"minimum\":1,\"maximum\":5000},\"offset\":{\"type\":\"integer\",\"description\":\"Zero-based table/records row offset.\",\"minimum\":0}" + ",\"path\":" + ResourceSelectorContract.RecordPath().ToString(Formatting.None) + ",\"fields\":{\"type\":\"array\",\"description\":\"Exact column keys from returned columns metadata, not display headings or translated aliases.\",\"maxItems\":128,\"items\":{\"type\":\"string\",\"maxLength\":128}}";
            return "{\"type\":\"object\",\"properties\":{" + target + "," + representation + "," + section + "," + selectors + "," + lines +
                "},\"required\":[\"target\"],\"additionalProperties\":false,\"anyOf\":[" +
                "{\"type\":\"object\",\"description\":\"Read one complete metadata, text, structure, source, media, or formulas representation. Do not send limit, offset, path, or fields.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Complete representation to read; omit for provider-selected auto.\",\"enum\":[\"metadata\",\"text\",\"structure\",\"source\",\"media\",\"formulas\"]}},\"required\":[\"target\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read bounded rows. Structural selectors are valid only in this table/records branch.\",\"properties\":{" + target + "," +
                "\"representation\":{\"type\":\"string\",\"description\":\"Bounded structural representation to read.\",\"enum\":[\"table\",\"records\"]}," +
                selectors +
                "},\"required\":[\"target\",\"representation\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read exact bounded source/text lines. No section or structural selectors.\",\"properties\":{" + target + "," + lines + ",\"representation\":{\"type\":\"string\",\"enum\":[\"source\",\"text\"]}},\"required\":[\"target\",\"representation\",\"startLine\",\"lineCount\"],\"additionalProperties\":false}," +
                "{\"type\":\"object\",\"description\":\"Read one uniquely named document Markdown/Plan section.\",\"properties\":{" + target + "," + section + "," +
                "\"representation\":{\"type\":\"string\",\"enum\":[\"text\"]}},\"required\":[\"target\",\"representation\",\"section\"],\"additionalProperties\":false}]}";
        }

        private static List<string> Fields(IDictionary<string, object> arguments)
        {
            object value;
            if (!arguments.TryGetValue("fields", out value) || value == null) return null;
            return JsonConvert.DeserializeObject<List<string>>(JsonConvert.SerializeObject(value));
        }

    }
}
