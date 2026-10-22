using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
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

        internal WorkspaceResourceHandler(WorkspaceFileResourceProvider files, SkillPublicationService publications,
            Func<SkillCatalogSnapshot> skills, Dictionary<string, ResourceEvidence> observed, bool find,
            WorkspacePlanService plans, IReadOnlyToolHandler office = null)
        { _files = files; _publications = publications; _skills = skills; _observed = observed; _find = find; _plans = plans;
            _office = office; }

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
                if (type != "file" && type != "skill" && type != "plan") throw Error("Unknown resource type.", "RESOURCE_NOT_FOUND");
                if (type == "file" && !string.IsNullOrEmpty(Value(context, "referencePath")) ||
                    type == "skill" && !string.IsNullOrEmpty(Value(context, "directory")))
                    throw Error("The selected resource type does not support this selector.", "RESOURCE_REQUEST_INVALID");
                if (_find)
                {
                    var page = type == "plan" ? _plans.Find(Value(context, "query")) : type == "file" ? _files.Find(Value(context, "directory"), Value(context, "query")) :
                        SkillCatalogService.Find(_skills(), Value(context, "query"));
                    if (type == "file")
                        foreach (var item in page.Items)
                        {
                            // Binary delivery belongs to application/browser consumers;
                            // this model tool currently admits complete UTF-8 text only.
                            item.Representations.RemoveAll(view => view != ResourceRepresentations.Metadata && view != ResourceRepresentations.Text);
                            if (item.Type == "file" && !item.Representations.Contains(ResourceRepresentations.Text))
                                item.Usage = "Binary source; model text reading is unavailable. Application raw/image views can inspect it.";
                        }
                    return Return(ToolResult.Ok(ResourceFindProjection.Message(page), ResourceFindProjection.Serialize(page), page.ResourceRefs));
                }
                var target = Value(context, "target");
                ResourceReadObservation read;
                SkillDefinition skill = null;
                var referencePath = Value(context, "referencePath");
                int? startLine = null, lineCount = null, startChar = null, charCount = null;
                if (type == "file")
                {
                    startLine = Number(context, "startLine");
                    lineCount = Number(context, "lineCount");
                    startChar = Number(context, "startChar");
                    charCount = Number(context, "charCount");
                    if (startChar.HasValue != charCount.HasValue ||
                        (startChar.HasValue && (startLine.HasValue || lineCount.HasValue)))
                        throw Error("Supply startChar and charCount together, without line selectors.", "resource_chars_invalid");
                    read = startChar.HasValue
                        ? _files.ReadCharacters(target, startChar.Value, charCount.Value, cancellationToken)
                        : _files.Read(target, startLine, lineCount, cancellationToken);
                }
                else if (type == "plan")
                {
                    var versionText = Value(context, "version");
                    read = _plans.Read(target, string.IsNullOrEmpty(versionText) ? (int?)null : int.Parse(versionText, System.Globalization.CultureInfo.InvariantCulture));
                    target = read.Result.Resource.Title;
                }
                else
                {
                    skill = _skills().Skills.SingleOrDefault(item => item.Enabled && item.Id == target);
                    if (skill == null) throw Error("The skill is not enabled in the current host catalog.", "RESOURCE_NOT_FOUND");
                    if (!string.IsNullOrEmpty(referencePath) && !(skill.References ?? new List<SkillReferenceMetadata>()).Any(item => item.Path == referencePath))
                        throw Error("The reference is not part of the selected skill publication.", "RESOURCE_NOT_FOUND");
                    read = _publications.ReadSkill(SkillPublicationService.SkillResource(skill,
                        string.IsNullOrEmpty(referencePath) ? null : referencePath));
                }
                if (read.Result.Text.Length > (type == "plan" ? PlanDocumentService.MaximumMarkdownCharacters : 16000))
                    throw Error("The source exceeds the model read bound. For files, use startLine/lineCount or startChar/charCount to read a bounded region.", "read_too_large");
                if (type != "file" || read.Result.Complete) read.RequireCompleteExactText();
                if (type == "file") _observed[target] = read.Evidence.Single();
                var projection = startChar.HasValue
                    ? ResourceReadProjection.FromCharacters(read.Result, target, type, "workspace", startChar.Value, charCount.Value)
                    : startLine.HasValue
                    ? ResourceReadProjection.FromLines(read.Result, target, type, "workspace", startLine.Value, lineCount.Value)
                    : ResourceReadProjection.From(read.Result, target, type, type == "skill" ? "catalog" : "workspace");
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
                var message = read.Result.Complete ? "Complete resource representation read." :
                    "Exact text excerpt read from the current version; other regions are not observed. Read another bounded range as needed; each read rechecks the current source.";
                if (type == "file" && _files.ExceedsTextWriteBound(read.Result.Resource.Reference))
                    message += " This file exceeds the 1-MiB text mutation bound; files.patch is unavailable.";
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
