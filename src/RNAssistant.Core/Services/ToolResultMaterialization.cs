using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Core.Services
{
    // Request-local projection only. Replacing/bounding this payload cannot change
    // the immutable terminal result and execution evidence already recorded by the kernel.
    public sealed class ToolResultMaterialization
    {
        private ResourceRef _resultResource;
        public TerminalResult Result { get; private set; }
        public JToken Data { get; private set; }
        public IReadOnlyList<ChatAttachment> ModelAttachments { get; private set; }
        public IReadOnlyList<ResourceEvidence> ResourceEvidence { get; private set; }
        public ResourceEffect ResourceEffect { get; private set; }
        public RNAssistant.Core.Tools.ToolExecutionProgress ExecutionProgress { get; set; }
        public string AuthorityCommitId { get; private set; }
        public ResourceRef ResultResource
        {
            get { return _resultResource == null ? null : new ResourceRef(_resultResource.Uri, _resultResource.Revision); }
        }
        public string ResultResourceKind { get; private set; }

        public ToolResultMaterialization(TerminalResult result,
            IEnumerable<ChatAttachment> attachments = null,
            ResourceRef resultResource = null, string resultResourceKind = null,
            JToken data = null, IEnumerable<ResourceEvidence> resourceEvidence = null,
            ResourceEffect resourceEffect = null, string authorityCommitId = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            Data = data ?? ToolResultWire.ParseData(result.DataJson);
            ModelAttachments = Array.AsReadOnly((attachments ?? new ChatAttachment[0]).ToArray());
            _resultResource = resultResource == null ? null : new ResourceRef(resultResource.Uri, resultResource.Revision);
            ResultResourceKind = resultResourceKind;
            ResourceEvidence = Array.AsReadOnly((resourceEvidence ?? new ResourceEvidence[0]).ToArray());
            ResourceEffect = resourceEffect;
            AuthorityCommitId = authorityCommitId;
        }

        public void ReplaceResult(TerminalResult result, JToken data = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            Data = data ?? ToolResultWire.ParseData(result.DataJson);
        }

        public void IncludeResultResource(ResourceRef reference, string kind)
        {
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            var resources = Result.Resources.ToList();
            if (!resources.Any(item => string.Equals(item.Uri, reference.Uri, StringComparison.Ordinal) &&
                string.Equals(item.Revision, reference.Revision, StringComparison.Ordinal)))
                resources.Add(new ResourceRef(reference.Uri, reference.Revision));
            Result = new TerminalResult(Result.Status, Result.Message, Result.DataJson, resources);
            _resultResource = new ResourceRef(reference.Uri, reference.Revision);
            ResultResourceKind = kind;
        }
    }
}
