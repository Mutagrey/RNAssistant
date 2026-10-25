using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Runtime
{
    // One semantic resource entrypoint. It never issues mutation observations for
    // instruction resources; files remain owned by WorkspaceFileService.
    internal sealed class WorkspaceResourceHandler : IReadOnlyToolHandler
    {
        private readonly WorkspaceFileResourceProvider _files;
        private readonly SkillPublicationService _publications;
        private readonly Func<SkillCatalogSnapshot> _skills;
        private readonly Dictionary<string, ResourceEvidence> _observed;
        private readonly bool _find;
        private readonly WorkspacePlanService _plans;
        private readonly IReadOnlyToolHandler _office;
        private readonly WorkspaceChatAttachmentResourceProvider _chatAttachments;
        private readonly int _maximumTextReadCharacters;

        internal WorkspaceResourceHandler(WorkspaceFileResourceProvider files, SkillPublicationService publications,
            Func<SkillCatalogSnapshot> skills, Dictionary<string, ResourceEvidence> observed, bool find,
            WorkspacePlanService plans, int maximumTextReadCharacters, IReadOnlyToolHandler office = null,
            WorkspaceChatAttachmentResourceProvider chatAttachments = null)
        { _files = files; _publications = publications; _skills = skills; _observed = observed; _find = find; _plans = plans;
            _office = office; _chatAttachments = chatAttachments;
            if (maximumTextReadCharacters < 1 || maximumTextReadCharacters > WorkspaceFileService.MaximumWindowTextCharacters)
                throw new ArgumentOutOfRangeException(nameof(maximumTextReadCharacters));
            _maximumTextReadCharacters = maximumTextReadCharacters; }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var type = Value(context, "type");
                if (string.IsNullOrEmpty(type)) type = "file";
                if (type == "office")
                {
                    if (_office == null) throw Error("No Office document is bound to this run.", "RESOURCE_PROVIDER_UNAVAILABLE");
                    if (_find && string.IsNullOrEmpty(Value(context, "scope"))) context.Arguments["scope"] = "document";
                    return _office.ExecuteAsync(context, cancellationToken);
                }
                if (type != "file" && type != "skill" && type != "plan" && type != "attachment")
                    throw Error("Unknown resource type.", "RESOURCE_NOT_FOUND");
                if (type == "attachment" && _chatAttachments == null)
                    throw Error("Workspace attachment resources are unavailable.", "RESOURCE_PROVIDER_UNAVAILABLE");
                if (type == "file" && !string.IsNullOrEmpty(Value(context, "referencePath")) ||
                    type == "skill" && !string.IsNullOrEmpty(Value(context, "directory")))
                    throw Error("The selected resource type does not support this selector.", "RESOURCE_REQUEST_INVALID");
                if (_find)
                {
                    if (type == "attachment")
                    {
                        if (context.Arguments.Keys.Any(key => key != "type" && key != "query"))
                            throw Error("Attachment discovery accepts only a query.", "RESOURCE_REQUEST_INVALID");
                        var attachments = _chatAttachments.Find(Value(context, "query"));
                        return Return(ToolResult.Ok(ResourceFindProjection.Message(attachments),
                            ResourceFindProjection.Serialize(attachments), attachments.ResourceRefs));
                    }
                    var page = type == "plan" ? _plans.Find(Value(context, "query")) : type == "file" ? (context.Arguments.ContainsKey("recursive") || context.Arguments.ContainsKey("includeGlobs") || context.Arguments.ContainsKey("excludeGlobs")
                        ? FindPaths(context, cancellationToken) : _files.Find(Value(context, "directory"), Value(context, "query"))) :
                        SkillCatalogService.Find(_skills(), Value(context, "query"));
                    if (type == "file")
                        foreach (var item in page.Items)
                        {
                            // Binary delivery belongs to application/browser consumers;
                            // model file reads admit UTF-8 text and structured navigation.
                            item.Representations.RemoveAll(view => view != ResourceRepresentations.Metadata && view != ResourceRepresentations.Text);
                            if (item.Type == "file" && (item.Target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                item.Target.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                item.Target.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
                                item.Representations.Add(ResourceRepresentations.Tree);
                            if (item.Type == "file" && item.Target.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                                item.Usage = "Use representation=text with startPage/pageCount for consecutive complete PDF pages, or pageIndex with startChar/charCount for a long page. Visual or scanned content may be absent from text.";
                            if (item.Type == "file" && !item.Representations.Contains(ResourceRepresentations.Text))
                                item.Usage = "Binary source; model text reading is unavailable. Application raw/image views can inspect it.";
                        }
                    return Return(ToolResult.Ok(ResourceFindProjection.Message(page), ResourceFindProjection.Serialize(page), page.ResourceRefs));
                }
                var target = Value(context, "target");
                var pdfSelection = ResourcePdfPageSelectorContract.Validate(context.Arguments,
                    Value(context, "representation"), type == "file", _maximumTextReadCharacters);
                ResourceReadObservation read;
                SkillDefinition skill = null;
                var referencePath = Value(context, "referencePath");
                var startLine = Number(context, "startLine");
                var lineCount = Number(context, "lineCount");
                var startChar = Number(context, "startChar");
                var charCount = Number(context, "charCount");
                var pageIndex = Number(context, "pageIndex");
                var startPage = Number(context, "startPage");
                var requestedPages = Number(context, "pageCount");
                var pageRange = pdfSelection == ResourcePdfPageSelectionKind.Range;
                var procedure = Value(context, "procedure");
                var procedureKind = Value(context, "procedureKind");
                var isTree = Value(context, "representation") == ResourceRepresentations.Tree;
                var hasPointer = context.Arguments.ContainsKey("pointer");
                var pointer = Value(context, "pointer");
                var depth = Number(context, "depth") ?? 1;
                var treeOffset = Number(context, "offset") ?? 0;
                var treeLimit = Number(context, "limit") ?? 100;
                if (type == "attachment")
                {
                    if (string.IsNullOrWhiteSpace(target) ||
                        context.Arguments.Keys.Any(key => key != "type" && key != "target" &&
                            key != "representation" && key != "startLine" && key != "lineCount" &&
                            key != "startChar" && key != "charCount") ||
                        (Value(context, "representation") != string.Empty &&
                            Value(context, "representation") != ResourceRepresentations.Text) ||
                        isTree || hasPointer || pageRange || pageIndex.HasValue ||
                        context.Arguments.ContainsKey("procedure") || context.Arguments.ContainsKey("procedureKind") ||
                        !string.IsNullOrEmpty(referencePath) || context.Arguments.ContainsKey("version") ||
                        startLine.HasValue != lineCount.HasValue || startChar.HasValue != charCount.HasValue ||
                        startLine.HasValue && startChar.HasValue)
                        throw Error("Attachment reads require one semantic target and optional text window.",
                            "RESOURCE_REQUEST_INVALID");
                    read = _chatAttachments.Read(target, _maximumTextReadCharacters,
                        startLine, lineCount, startChar, charCount);
                    var selected = startChar.HasValue
                        ? ResourceReadProjection.FromCharacters(read.Result, target, "attachment", "conversation",
                            startChar.Value, charCount.Value)
                        : startLine.HasValue
                        ? ResourceReadProjection.FromLines(read.Result, target, "attachment", "conversation",
                            startLine.Value, lineCount.Value)
                        : ResourceReadProjection.From(read.Result, target, "attachment", "conversation");
                    selected.SourceComplete = read.Result.Complete;
                    if (!read.Result.Complete)
                    {
                        selected.SourceCoverage = read.Result.Coverage;
                        selected.SourceEndChar = read.Result.TotalCharacters;
                        if (!startChar.HasValue && !startLine.HasValue)
                        {
                            selected.SelectionComplete = true;
                            selected.StartChar = 0;
                            selected.RequestedCharacters = _maximumTextReadCharacters;
                        }
                    }
                    var attachmentRequestedCoverage = startChar.HasValue
                        ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: startChar,
                            end: (long)startChar.Value + charCount.Value)
                        : startLine.HasValue
                        ? new ResourceCoverage(ResourceCoverageKinds.LineRange, start: startLine,
                            end: (long)startLine.Value + lineCount.Value)
                        : ResourceCoverage.Whole();
                    read = read.WithReadMetadata(selected, attachmentRequestedCoverage);
                    if (read.Result.Complete) read.RequireCompleteExactText();
                    return Return(ToolResult.Ok(read.Result.Complete ? "Complete visual selection read." :
                        "Exact bounded visual selection excerpt read.", JsonConvert.SerializeObject(selected),
                        new[] { read.Result.Resource.Reference }), read.Evidence);
                }
                if (isTree && (type != "file" || !hasPointer || depth < 0 || treeOffset < 0 || treeLimit < 1 ||
                    startLine.HasValue || lineCount.HasValue || startChar.HasValue || charCount.HasValue ||
                    context.Arguments.ContainsKey("procedure") || context.Arguments.ContainsKey("procedureKind") ||
                    !string.IsNullOrEmpty(referencePath) || context.Arguments.ContainsKey("version")) ||
                    !isTree && (hasPointer || context.Arguments.ContainsKey("depth") ||
                        context.Arguments.ContainsKey("offset") || context.Arguments.ContainsKey("limit")))
                    throw Error("JSON/YAML tree requires a file, pointer, optional depth/offset/limit and no other selectors.",
                        "resource_tree_selector_invalid");
                if (string.IsNullOrEmpty(procedureKind)) procedureKind = null;
                var hasProcedure = context.Arguments.ContainsKey("procedure") || context.Arguments.ContainsKey("procedureKind");
                if (hasProcedure && (type != "file" || string.IsNullOrWhiteSpace(procedure) ||
                    startLine.HasValue || lineCount.HasValue || startChar.HasValue || charCount.HasValue))
                    throw Error("VBA procedure selection requires a file and procedure, optionally procedureKind, without line/character selectors.", "resource_procedure_invalid");
                if (type == "file")
                {
                    if (startChar.HasValue != charCount.HasValue ||
                        (startChar.HasValue && (startLine.HasValue || lineCount.HasValue)))
                        throw Error("Supply startChar and charCount together, without line selectors.", "resource_chars_invalid");
                    read = isTree
                        ? _files.ReadTree(target, pointer, depth, treeOffset, treeLimit,
                            Math.Max(1, _maximumTextReadCharacters - 512), cancellationToken)
                        : pageRange
                        ? _files.ReadPdfPageRangeText(target, startPage.Value, requestedPages.Value,
                            _maximumTextReadCharacters, cancellationToken)
                        : pageIndex.HasValue
                        ? _files.ReadPdfPageText(target, pageIndex.Value, startChar ?? 0,
                            charCount ?? _maximumTextReadCharacters, cancellationToken)
                        : hasProcedure
                        ? _files.ReadProcedure(target, procedure, procedureKind, _maximumTextReadCharacters, cancellationToken)
                        : startChar.HasValue
                        ? _files.ReadCharacters(target, startChar.Value, charCount.Value, cancellationToken,
                            _maximumTextReadCharacters)
                        : startLine.HasValue || lineCount.HasValue
                        ? _files.Read(target, startLine, lineCount, cancellationToken, _maximumTextReadCharacters)
                        : _files.ReadModelText(target, _maximumTextReadCharacters, cancellationToken);
                }
                else if (type == "plan")
                {
                    var versionText = Value(context, "version");
                    read = _plans.Read(target, string.IsNullOrEmpty(versionText) ? (int?)null : int.Parse(versionText, System.Globalization.CultureInfo.InvariantCulture),
                        _maximumTextReadCharacters, startLine, lineCount, startChar, charCount);
                    target = read.Result.Resource.Title;
                }
                else
                {
                    skill = _skills().Skills.SingleOrDefault(item => item.Enabled && item.Id == target);
                    if (skill == null) throw Error("The skill is not enabled in the current host catalog.", "RESOURCE_NOT_FOUND");
                    if (!string.IsNullOrEmpty(referencePath) && !(skill.References ?? new List<SkillReferenceMetadata>()).Any(item => item.Path == referencePath))
                        throw Error("The reference is not part of the selected skill publication.", "RESOURCE_NOT_FOUND");
                    read = _publications.ReadSkill(SkillPublicationService.SkillResource(skill,
                        string.IsNullOrEmpty(referencePath) ? null : referencePath),
                        _maximumTextReadCharacters, startLine, lineCount, startChar, charCount);
                }
                if (read.Result.Text.Length > _maximumTextReadCharacters)
                    throw Error("The source exceeds the model read budget. Use startLine/lineCount or startChar/charCount to select a smaller region.", "read_too_large");
                if (read.Result.Complete) read.RequireCompleteExactText();
                var projection = isTree
                    ? ResourceReadProjection.FromTree(read.Result, target, type, "workspace", pointer,
                        depth, treeOffset, treeLimit)
                    : pageRange
                    ? ResourceReadProjection.FromPdfRange(read.Result, target, type, "workspace",
                        startPage.Value, requestedPages.Value)
                    : pageIndex.HasValue
                    ? ResourceReadProjection.FromPdfPage(read.Result, target, type, "workspace",
                        pageIndex.Value, startChar ?? 0, charCount ?? _maximumTextReadCharacters,
                        startChar.HasValue)
                    : hasProcedure
                    ? ResourceReadProjection.FromProcedure(read.Result, target, type, "workspace")
                    : startChar.HasValue
                    ? ResourceReadProjection.FromCharacters(read.Result, target, type, type == "skill" ? "catalog" : "workspace", startChar.Value, charCount.Value)
                    : startLine.HasValue
                    ? ResourceReadProjection.FromLines(read.Result, target, type, type == "skill" ? "catalog" : "workspace", startLine.Value, lineCount.Value)
                    : ResourceReadProjection.From(read.Result, target, type, type == "skill" ? "catalog" : "workspace");
                if (!isTree && !pageRange && !pageIndex.HasValue && (type == "file" || !read.Result.Complete))
                {
                    projection.SourceComplete = read.Result.Complete;
                    if (!read.Result.Complete)
                    {
                        projection.SourceCoverage = read.Result.Coverage;
                        projection.SourceEndChar = read.Result.TotalCharacters;
                        if (!hasProcedure && !startChar.HasValue && !startLine.HasValue)
                        {
                            projection.SelectionComplete = true;
                            projection.StartChar = 0;
                            projection.RequestedCharacters = _maximumTextReadCharacters;
                        }
                    }
                }
                if (type == "plan")
                {
                    projection.Version = int.Parse(read.Result.Resource.Metadata["version"], System.Globalization.CultureInfo.InvariantCulture);
                    projection.Historical = bool.Parse(read.Result.Resource.Metadata["historical"]);
                }
                if (skill != null)
                {
                    projection.Section = string.IsNullOrEmpty(referencePath) ? null : referencePath;
                    projection.References = (skill.References ?? new List<SkillReferenceMetadata>())
                        .Select(item => new SkillReferenceProjection { Path = item.Path, ByteLength = item.ByteLength }).ToArray();
                }
                if (isTree && JsonConvert.SerializeObject(projection).Length > _maximumTextReadCharacters)
                    throw Error("Selected tree exceeds the model read budget; narrow pointer, depth or limit.",
                        "resource_tree_view_too_large");
                var requestedCoverage = isTree
                    ? new ResourceCoverage(read.Result.Tree.Coverage().Kind,
                        address: "depth=" + depth + ";limit=" + treeLimit,
                        start: treeOffset, end: (long)treeOffset + treeLimit, path: pointer)
                    : pageRange
                    ? new ResourceCoverage(ResourceCoverageKinds.PageRange,
                        start: startPage, end: (long)startPage.Value + requestedPages.Value)
                    : pageIndex.HasValue
                    ? startChar.HasValue
                        ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                            address: "pdf-page:" + pageIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            start: startChar, end: (long)startChar.Value + charCount.Value)
                        : new ResourceCoverage(ResourceCoverageKinds.PageRange,
                            start: pageIndex, end: (long)pageIndex.Value + 1)
                    : startChar.HasValue
                    ? new ResourceCoverage(ResourceCoverageKinds.CharacterRange, start: startChar,
                        end: (long)startChar.Value + charCount.Value)
                    : startLine.HasValue
                    ? new ResourceCoverage(ResourceCoverageKinds.LineRange, start: startLine,
                        end: (long)startLine.Value + lineCount.Value)
                    : hasProcedure ? null : ResourceCoverage.Whole();
                read = read.WithReadMetadata(projection, requestedCoverage,
                    requestedProcedure: hasProcedure ? procedure : null, requestedProcedureKind: procedureKind,
                    requestedDepth: isTree ? (int?)depth : null);
                if (type == "file") _observed[target] = read.Evidence.Single();
                var message = isTree ? "Exact structured tree read; navigate with pointer, depth and child offset. Values are decoded previews, not raw source lexemes." :
                    pageRange ? "Exact PDF page range extraction; only complete extracted pages are covered. Continue at nextPageIndex when present; visual content may require media inspection." :
                    pageIndex.HasValue ? "Exact PDF page text extraction; coverage is limited to this page and character window. Visual content may require media inspection." :
                    read.Result.Complete ? "Complete resource representation read." :
                    "Exact text excerpt read from the current version; other regions are not observed. Read another bounded range as needed; each read rechecks the current source.";
                return Return(ToolResult.Ok(message, JsonConvert.SerializeObject(projection),
                    new[] { read.Result.Resource.Reference }), read.Evidence);
            }
            catch (ResourceRequestException error)
            { return Return(ToolResult.Error(error.Message, JsonConvert.SerializeObject(new ResourceFailure { Code = error.ErrorCode, Retryable = error.Retryable }))); }
            catch (System.IO.InvalidDataException error)
            { return Return(ToolResult.Error(error.Message, JsonConvert.SerializeObject(new ResourceFailure { Code = "resource_unavailable" }))); }
            catch (RNAssistant.Core.Storage.WorkspaceFileException error)
            {
                var message = error.Message;
                if (_find && error.Code == "path_outside_mount")
                    message += " For workspace root, omit directory or use null; otherwise use a safe workspace-relative directory.";
                return Return(ToolResult.Error(message, JsonConvert.SerializeObject(new ResourceFailure { Code = error.Code })));
            }
        }

        private ResourceFindPage FindPaths(ToolHandlerContext context, CancellationToken token)
        {
            var request = ResourceModelSearchSchema.Parse(context.Arguments);
            return _files.FindPaths(request.Directory, request.Query, request.Recursive, request.IncludeGlobs, request.ExcludeGlobs, token);
        }

        private sealed class ResourceFailure
        {
            [JsonProperty("code")] public string Code { get; set; }
            [JsonProperty("retryable")] public bool Retryable { get; set; }
        }

        private static ResourceRequestException Error(string message, string code)
        { return new ResourceRequestException(message, code, false); }
        private static string Value(ToolHandlerContext context, string name)
        { object value; return context.Arguments.TryGetValue(name, out value) ? Convert.ToString(value) : string.Empty; }
        private static int? Number(ToolHandlerContext context, string name)
        { object value; return context.Arguments.TryGetValue(name, out value) && value != null ? (int?)Convert.ToInt32(value) : null; }
        private static Task<ToolHandlerResult> Return(ToolResult result, IReadOnlyList<ResourceEvidence> evidence = null)
        { return Task.FromResult(new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: evidence)); }
    }
}
