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
        private readonly Dictionary<string, ResourceRef> _observed;
        private readonly bool _find;

        internal WorkspaceResourceHandler(WorkspaceFileResourceProvider files, SkillPublicationService publications,
            Func<SkillCatalogSnapshot> skills, Dictionary<string, ResourceRef> observed, bool find)
        { _files = files; _publications = publications; _skills = skills; _observed = observed; _find = find; }

        public Task<ToolHandlerResult> ExecuteAsync(ToolHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var type = Value(context, "type");
                if (string.IsNullOrEmpty(type)) type = "file";
                if (type != "file" && type != "skill") throw Error("Unknown resource type.", "RESOURCE_NOT_FOUND");
                if (type == "file" && !string.IsNullOrEmpty(Value(context, "referencePath")) ||
                    type == "skill" && !string.IsNullOrEmpty(Value(context, "directory")))
                    throw Error("The selected resource type does not support this selector.", "RESOURCE_REQUEST_INVALID");
                if (_find)
                {
                    var page = type == "file" ? _files.Find(Value(context, "directory"), Value(context, "query")) :
                        SkillCatalogService.Find(_skills(), Value(context, "query"));
                    return Return(ToolResult.Ok(ResourceFindProjection.Message(page), ResourceFindProjection.Serialize(page), page.ResourceRefs));
                }
                var target = Value(context, "target");
                ResourceReadObservation read;
                SkillDefinition skill = null;
                var referencePath = Value(context, "referencePath");
                if (type == "file") read = _files.Read(target);
                else
                {
                    skill = _skills().Skills.SingleOrDefault(item => item.Enabled && item.Id == target);
                    if (skill == null) throw Error("The skill is not enabled in the current host catalog.", "RESOURCE_NOT_FOUND");
                    if (!string.IsNullOrEmpty(referencePath) && !(skill.References ?? new List<SkillReferenceMetadata>()).Any(item => item.Path == referencePath))
                        throw Error("The reference is not part of the selected skill publication.", "RESOURCE_NOT_FOUND");
                    read = _publications.ReadSkill(SkillPublicationService.SkillResource(skill,
                        string.IsNullOrEmpty(referencePath) ? null : referencePath));
                }
                if (read.Result.Text.Length > 16000)
                    throw Error("The complete source exceeds the model read bound; no read evidence was accepted.", "read_too_large");
                read.RequireCompleteExactText();
                if (type == "file") _observed[target] = read.Result.Resource.Reference;
                var projection = ResourceReadProjection.From(read.Result, target, type, type == "file" ? "workspace" : "catalog");
                if (skill != null)
                {
                    projection.Section = string.IsNullOrEmpty(referencePath) ? null : referencePath;
                    projection.References = (skill.References ?? new List<SkillReferenceMetadata>())
                        .Select(item => new SkillReferenceProjection { Path = item.Path, ByteLength = item.ByteLength }).ToArray();
                }
                return Return(ToolResult.Ok("Complete resource representation read.", JsonConvert.SerializeObject(projection),
                    new[] { read.Result.Resource.Reference }), read.Evidence);
            }
            catch (ResourceRequestException error)
            { return Return(ToolResult.Error(error.Message, JsonConvert.SerializeObject(new ResourceFailure { Code = error.ErrorCode, Retryable = error.Retryable }))); }
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
        private static Task<ToolHandlerResult> Return(ToolResult result, IReadOnlyList<ResourceEvidence> evidence = null)
        { return Task.FromResult(new ToolHandlerResult(result, ToolEffectEvidence.None, resourceEvidence: evidence)); }
    }
}
