using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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
            "Read-only: Read a semantic target supplied by RUNTIME_CONTEXT, core.find, workspace member results or binding sourceTarget. Copy supplied targets verbatim; they never contain :// and must not be constructed from display titles. For known Excel cells, use target=Excel range: Sheet!A1:B10 with representation=text directly; discovery is unnecessary. Do not read an Excel search scope to enumerate worksheet data: use excel.cells.find for text discovery or an Excel range/table/name target for values. table returns bounded row coverage with optional fields, offset and limit; omit path for Office targets because runtime applies their canonical record view. For a complete JSON or YAML text artifact, representation=tree with pointer (empty root or RFC 6901-style path) returns typed nodes; depth controls expansion and offset/limit page immediate children. Numeric and YAML scalar values are decoded previews, not exact source lexemes. YAML aliases, merge keys, complex keys and multi-document streams fail explicitly. Text/source return a complete representation when available within the configured model context; large Word, VBA and PowerPoint slide text return an exact first excerpt with sourceCoverage. Continue with startChar/charCount for Word and PowerPoint slide text or speaker notes (PowerPoint notes: N), or startLine/lineCount for live VBA modules. Word structure gives main-story bounds; an explicit Word range: start:end is also available. For an indexed uploaded or authored artifact text/source, supply partNumber to read at most 32000 characters, including files written as one line; partNumber is unsupported for live VBA modules. For PDF attachments, representation=text with startPage/pageCount extracts consecutive complete pages within the model budget and nextPageIndex continues; pageIndex with optional startChar/charCount selects a long page. Page text is extraction, not a guarantee that visual content or OCR was captured. For VBA source, procedure selects one unique Sub/Function/Property by name; optional procedureKind=sub/function/property-get/property-let/property-set disambiguates accessors. Conditional branches are not evaluated; duplicate names require line/character selectors. Only the selected body must fit the model read budget. For source at any size positive startLine and lineCount within the configured model read budget select an exact excerpt; a long single line can use zero-based startChar and positive charCount within that budget. Excerpts cannot authorize a whole-file overwrite. For document Markdown/Plans, section with representation=text selects a unique ATX heading and its nested subsections, bounded to 32000 characters. Section coverage never proves a whole-resource read; duplicate/missing/oversized sections fail explicitly. For a project-wide VBA request, read RUNTIME_CONTEXT.document.vba_project_target with representation=structure first. Exact URI, revision, cursor and guards remain runtime-owned. Media is hydrated only for the next model step; base64 is never embedded in JSON.",
            ResourceModelReadSchema.Office());
        internal static readonly ToolPolicy Policy = new ToolPolicy(ToolEffect.Read, ToolVerification.None,
            false, true, new[] { "agent", "plan", "chat" });
        internal static readonly ToolBinding Binding = new ToolBinding("resources.read.intent.v2");
        private readonly Action<string, IReadOnlyList<ChatAttachment>> _captureAttachments;
        private readonly int _maximumTextCharacters;

        internal ResourceReadToolHandler(ResourceGatewayService gateway, ChatSession session,
            Action<string, IReadOnlyList<ChatAttachment>> captureAttachments, AppSettings settings)
            : base(gateway, session)
        {
            _captureAttachments = captureAttachments;
            _maximumTextCharacters = ModelContextBudget.ApproximateTextCharacterCapacity(
                ModelContextBudget.InputBudgetTokens(settings), settings);
        }

        protected override ToolHandlerResult Execute(ToolHandlerContext context, System.Threading.CancellationToken cancellationToken)
        {
            var target = ToolArgumentReader.String(
                context.Arguments, "target", string.Empty).Trim();
            var selected = Gateway.ResolveIntentTarget(Session, target);
            // A semantic mutable target requests its current head, not the last
            // observed revision from discovery. Internal pages pin the first read.
            var reference = selected.Descriptor.Mutable ? new ResourceRef(selected.Reference.Uri) : selected.Reference;
            var representation = ToolArgumentReader.String(context.Arguments, "representation", "auto");
            if (representation == "auto" && selected.Descriptor.Kind == ChatArtifactKinds.PlanDocument) representation = "source";
            // The workspace root has a structure view, but its file members have
            // source. A model may carry the root's view over to an exact file target.
            // Negotiate this one read explicitly; the result still reports source.
            var htmlFileStructureAsSource = representation == ResourceRepresentations.Structure &&
                string.Equals(selected.Descriptor.Kind, ChatHtmlResourceCatalog.FileKind, StringComparison.Ordinal) &&
                selected.Descriptor.Representations.Contains(ResourceRepresentations.Source);
            if (htmlFileStructureAsSource) representation = ResourceRepresentations.Source;
            if (representation == "records")
                throw new ResourceRequestException(
                    "Use representation=table for structural reads; records is retained only for saved bindings.",
                    "RESOURCE_VIEW_UNSUPPORTED", false);
            var pdfSelection = ResourcePdfPageSelectorContract.Validate(context.Arguments,
                representation, true, _maximumTextCharacters);
            var structured = representation == "table";
            var tree = representation == ResourceRepresentations.Tree;
            var section = ToolArgumentReader.String(context.Arguments, "section", null);
            var lines = context.Arguments.ContainsKey("startLine") || context.Arguments.ContainsKey("lineCount");
            var chars = context.Arguments.ContainsKey("startChar") || context.Arguments.ContainsKey("charCount");
            var parts = context.Arguments.ContainsKey("partNumber");
            var page = pdfSelection == ResourcePdfPageSelectionKind.Page;
            var pageRange = pdfSelection == ResourcePdfPageSelectionKind.Range;
            var procedure = ToolArgumentReader.String(context.Arguments, "procedure", null);
            var procedureKind = ToolArgumentReader.String(context.Arguments, "procedureKind", null);
            var pointer = ToolArgumentReader.String(context.Arguments, "pointer", null);
            var depth = ToolArgumentReader.Int32(context.Arguments, "depth", 1);
            var treeOffset = ToolArgumentReader.Int32(context.Arguments, "offset", 0);
            var treeLimit = ToolArgumentReader.Int32(context.Arguments, "limit", 100);
            if (tree && (!context.Arguments.ContainsKey("pointer") || pointer == null || depth < 0 || treeOffset < 0 || treeLimit < 1 ||
                section != null || lines || chars || parts || procedure != null || procedureKind != null ||
                context.Arguments.ContainsKey("path") || context.Arguments.ContainsKey("fields")) ||
                !tree && (context.Arguments.ContainsKey("pointer") || context.Arguments.ContainsKey("depth")))
                throw new ResourceRequestException("JSON/YAML tree requires pointer, optional depth/offset/limit, and no other selectors.",
                    "resource_tree_selector_invalid", false);
            if ((context.Arguments.ContainsKey("procedure") || context.Arguments.ContainsKey("procedureKind")) &&
                (string.IsNullOrWhiteSpace(procedure) || representation != "source" || lines || chars || parts || section != null || structured))
                throw new ResourceRequestException("VBA procedure selection requires source and procedure, optionally procedureKind, without other selectors.",
                    "resource_procedure_invalid", false);
            if (parts && (lines || chars || section != null || structured || representation != "source" && representation != "text"))
                throw new ResourceRequestException("partNumber requires source/text without other selectors.", "resource_part_invalid", false);
            if ((lines || chars) && (selected.Type == "shared context" || selected.Type == "HTML data" || selected.Type == "HTML workspace"))
                throw new ResourceRequestException("This target requires its complete semantic projection. Select a file/source target for bounded text reads.", "resource_lines_unsupported", false);
            if (lines && (chars || section != null || structured || !context.Arguments.ContainsKey("startLine") ||
                !context.Arguments.ContainsKey("lineCount") || representation != "source" && representation != "text"))
                throw new ResourceRequestException("Line selection requires source/text, startLine and lineCount, without section or row selectors.", "resource_lines_invalid", false);
            if (chars && (lines || section != null || structured ||
                !context.Arguments.ContainsKey("startChar") || !context.Arguments.ContainsKey("charCount") ||
                representation != "source" && representation != "text"))
                throw new ResourceRequestException("Character selection requires source/text, startChar and charCount, without other selectors.", "resource_chars_invalid", false);
            if (section != null && representation != "text")
                throw new ResourceRequestException("section requires representation=text.", "resource_section_unsupported", false);
            if (!structured && !tree && new[] { "limit", "offset", "path", "fields" }.Any(context.Arguments.ContainsKey))
                throw new ResourceRequestException("Structural selectors require representation=table.", "RESOURCE_VIEW_UNSUPPORTED", false);
            var viewPath = structured
                ? ResourceSelectorContract.ResolvePath(selected, representation,
                    ToolArgumentReader.String(context.Arguments, "path", null))
                : null;
            var selection = tree
                ? Gateway.ReadTree(Session, reference, selected.Descriptor, pointer, depth, treeOffset,
                    treeLimit, Math.Max(1, _maximumTextCharacters - 512), cancellationToken)
                : pageRange
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference,
                    Representation = ResourceRepresentations.Text,
                    PageStart = ToolArgumentReader.Int32(context.Arguments, "startPage", -1),
                    PageRangeCount = ToolArgumentReader.Int32(context.Arguments, "pageCount", 0),
                    MaxChars = _maximumTextCharacters }, cancellationToken)
                : page
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference,
                    Representation = ResourceRepresentations.Text,
                    PageIndex = ToolArgumentReader.Int32(context.Arguments, "pageIndex", -1),
                    PageStartChar = ToolArgumentReader.Int32(context.Arguments, "startChar", 0),
                    PageCharCount = chars ? ToolArgumentReader.Int32(context.Arguments, "charCount", 0)
                        : _maximumTextCharacters }, cancellationToken)
                : procedure != null
                ? Gateway.ReadProcedure(Session, reference, procedure, procedureKind, _maximumTextCharacters, cancellationToken)
                : lines
                ? Gateway.ReadLines(Session, reference, representation,
                    ToolArgumentReader.Int32(context.Arguments, "startLine", 1), ToolArgumentReader.Int32(context.Arguments, "lineCount", 100), cancellationToken, _maximumTextCharacters)
                : chars
                ? Gateway.ReadCharacters(Session, reference, representation,
                    ToolArgumentReader.Int32(context.Arguments, "startChar", 0), ToolArgumentReader.Int32(context.Arguments, "charCount", 1000), cancellationToken, _maximumTextCharacters)
                : parts
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = representation,
                    PartNumber = ToolArgumentReader.Int32(context.Arguments, "partNumber", 1), MaxChars = InternalReadCharacters }, cancellationToken)
                : section != null
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = "text", Section = section, MaxChars = InternalReadCharacters }, cancellationToken)
                : structured
                ? Gateway.Read(Session, new ResourceReadRequest { Reference = reference, Representation = representation,
                    MaxRows = ToolArgumentReader.Int32(context.Arguments, "limit", 500),
                    RowOffset = ToolArgumentReader.Int32(context.Arguments, "offset", 0),
                    ViewPath = viewPath,
                    Fields = Fields(context.Arguments) }, cancellationToken)
                : Gateway.ReadAdaptiveText(Session, reference, selected.Descriptor, representation,
                    _maximumTextCharacters, cancellationToken);
            if (selected.Descriptor.CurrentTarget != null)
                Gateway.EnsureCurrentHtmlReference(Session, selected.Reference);
            var observation = new ResourceReadObservation(selection.Result,
                Gateway.Evidence(Session, selection.Result));
            var selectedSource = selection.Result.SourceCoverage;
            if (!page && selectedSource != null && chars &&
                (selectedSource.Kind != ResourceCoverageKinds.CharacterRange ||
                 selectedSource.Start != ToolArgumentReader.Int32(context.Arguments, "startChar", 0) ||
                 selectedSource.End > (long)ToolArgumentReader.Int32(context.Arguments, "startChar", 0) +
                     ToolArgumentReader.Int32(context.Arguments, "charCount", 1000)) ||
                selectedSource != null && lines &&
                (selectedSource.Kind != ResourceCoverageKinds.LineRange ||
                 selectedSource.Start != ToolArgumentReader.Int32(context.Arguments, "startLine", 1) ||
                 selectedSource.End > (long)ToolArgumentReader.Int32(context.Arguments, "startLine", 1) +
                     ToolArgumentReader.Int32(context.Arguments, "lineCount", 100)))
                throw new InvalidOperationException("Provider source coverage differs from the requested window.");
            var projection = tree
                ? ResourceReadProjection.FromTree(observation.Result, selected.Target, selected.Type,
                    selected.Scope, pointer, depth, treeOffset, treeLimit)
                : pageRange
                ? ResourceReadProjection.FromPdfRange(observation.Result, selected.Target, selected.Type,
                    selected.Scope, ToolArgumentReader.Int32(context.Arguments, "startPage", -1),
                    ToolArgumentReader.Int32(context.Arguments, "pageCount", 0))
                : page
                ? ResourceReadProjection.FromPdfPage(observation.Result, selected.Target, selected.Type,
                    selected.Scope, ToolArgumentReader.Int32(context.Arguments, "pageIndex", -1),
                    ToolArgumentReader.Int32(context.Arguments, "startChar", 0),
                    chars ? ToolArgumentReader.Int32(context.Arguments, "charCount", 0) : _maximumTextCharacters,
                    chars)
                : procedure != null
                ? ResourceReadProjection.FromProcedure(observation.Result, selected.Target, selected.Type, selected.Scope)
                : selectedSource != null
                ? ResourceReadProjection.FromSelectedSourceRange(observation.Result,
                    selected.Target, selected.Type, selected.Scope, selectedSource)
                : lines
                ? ResourceReadProjection.FromLines(observation.Result,
                    selected.Target, selected.Type, selected.Scope,
                    ToolArgumentReader.Int32(context.Arguments, "startLine", 1),
                    ToolArgumentReader.Int32(context.Arguments, "lineCount", 100))
                : chars
                ? ResourceReadProjection.FromCharacters(observation.Result,
                    selected.Target, selected.Type, selected.Scope,
                    ToolArgumentReader.Int32(context.Arguments, "startChar", 0),
                    ToolArgumentReader.Int32(context.Arguments, "charCount", 1000))
                : parts
                ? ResourceReadProjection.FromPart(observation.Result,
                    selected.Target, selected.Type, selected.Scope,
                    ToolArgumentReader.Int32(context.Arguments, "partNumber", 1))
                : section != null
                ? ResourceReadProjection.FromSection(observation.Result,
                    selected.Target, selected.Type, selected.Scope, section)
                : structured
                ? ResourceReadProjection.FromTable(observation.Result,
                    selected.Target, selected.Type, selected.Scope,
                    ToolArgumentReader.Int32(context.Arguments, "offset", 0),
                    ToolArgumentReader.Int32(context.Arguments, "limit", 500))
                : ResourceReadProjection.From(observation.Result,
                    selected.Target, selected.Type, selected.Scope);
            if (!page && selectedSource != null && chars)
            {
                projection.StartChar = ToolArgumentReader.Int32(context.Arguments, "startChar", 0);
                projection.RequestedCharacters = ToolArgumentReader.Int32(context.Arguments, "charCount", 1000);
            }
            if (selectedSource != null && lines)
            {
                projection.StartLine = ToolArgumentReader.Int32(context.Arguments, "startLine", 1);
                projection.RequestedLines = ToolArgumentReader.Int32(context.Arguments, "lineCount", 100);
            }
            projection.SnapshotTarget = selected.Descriptor.SnapshotTarget;
            if (tree && Serialize(projection).Length > _maximumTextCharacters)
                throw new ResourceRequestException("Selected tree exceeds the model read budget; narrow pointer, depth or limit.",
                    "resource_tree_view_too_large", false);
            var requestedCoverage = tree
                ? new ResourceCoverage(selection.Result.Tree.Coverage().Kind,
                    address: "depth=" + depth + ";limit=" + treeLimit, start: treeOffset,
                    end: (long)treeOffset + treeLimit, path: pointer)
                : pageRange
                ? new ResourceCoverage(ResourceCoverageKinds.PageRange,
                    start: projection.StartPage, end: (long)projection.StartPage.Value + projection.RequestedPages.Value)
                : page
                ? chars
                    ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                        address: "pdf-page:" + projection.PageIndex.Value,
                        start: projection.StartChar,
                        end: (long)projection.StartChar.Value + projection.RequestedCharacters.Value)
                    : new ResourceCoverage(ResourceCoverageKinds.PageRange,
                        start: projection.PageIndex, end: (long)projection.PageIndex.Value + 1)
                : lines
                ? new ResourceCoverage(ResourceCoverageKinds.LineRange, start: projection.StartLine,
                    end: (long)projection.StartLine.Value + projection.RequestedLines.Value)
                : chars
                ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: projection.StartChar,
                    end: (long)projection.StartChar.Value + projection.RequestedCharacters.Value)
                : structured
                ? new ResourceCoverage(ResourceCoverageKinds.RecordRange, start: projection.RequestedOffset,
                    end: (long)projection.RequestedOffset.Value + projection.RequestedRows.Value,
                    path: ToolArgumentReader.String(context.Arguments, "path", null), fields: Fields(context.Arguments))
                : section != null || parts || procedure != null ? null : ResourceCoverage.Whole();
            observation = observation.WithReadMetadata(projection, requestedCoverage, section,
                parts ? (int?)ToolArgumentReader.Int32(context.Arguments, "partNumber", 1) : null, procedure, procedureKind,
                tree ? (int?)depth : null);
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
                    Members = manifest.Resources.Select(member => Gateway.DescribeHtmlMember(Session, member, selected.Descriptor)).ToList() });
                projection.ReturnedCharacters = projection.TotalCharacters = projection.Text.Length;
            }
            var result = RuntimeResult.Ok(
                htmlFileStructureAsSource
                    ? "HTML file has no structure view; returned source text with the reported coverage. The HTML workspace root has the structure view."
                    : tree ? "Exact structured tree read; navigate with pointer, depth and child offset. Values are decoded previews, not raw source lexemes."
                    : procedure != null ? "Complete selected VBA procedure read; coverage is limited to its exact source span. Conditional compilation is not evaluated."
                    : page ? "Exact PDF page text extraction; coverage is limited to this page and character window. Visual content may require media inspection."
                    : lines ? "Exact selected lines read; coverage is limited to this excerpt, not the whole resource."
                    : chars ? "Exact selected characters read; coverage is limited to this excerpt, not the whole resource."
                    : parts ? "Exact selected text part read; coverage is limited to this part."
                    : section != null ? "Complete selected Markdown section read; coverage is limited to that section." : structured ? "Bounded exact structural view read."
                    : projection.Complete && projection.SourceComplete != false ? "Complete resource representation read." : "Exact source excerpt read; continue using the reported coverage and source bounds.",
                Serialize(projection),
                selection.ResourceRefs);
            var attachments = selection.ModelAttachments ?? new ChatAttachment[0];
            if (_captureAttachments != null && attachments.Count > 0)
                _captureAttachments(context.Execution.Call.Id, attachments);
            return new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: observation.Evidence);
        }


        private static List<string> Fields(IDictionary<string, object> arguments)
        {
            object value;
            if (!arguments.TryGetValue("fields", out value) || value == null) return null;
            return JsonConvert.DeserializeObject<List<string>>(JsonConvert.SerializeObject(value));
        }

    }
}
