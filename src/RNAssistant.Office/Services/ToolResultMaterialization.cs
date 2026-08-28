using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using TerminalResult = RNAssistant.Core.Tools.Contracts.ToolResult;

namespace RNAssistant.Office.Services
{
    // Request-local projection only. Replacing/bounding this payload cannot change
    // the immutable terminal result and execution evidence already recorded by the kernel.
    internal sealed class ToolResultMaterialization
    {
        private ResourceRef _resultResource;
        internal TerminalResult Result { get; private set; }
        internal IReadOnlyList<ChatAttachment> ModelAttachments { get; private set; }
        internal ResourceRef ResultResource
        {
            get { return _resultResource == null ? null : new ResourceRef(_resultResource.Uri, _resultResource.Revision); }
        }
        internal string ResultResourceKind { get; private set; }

        internal ToolResultMaterialization(TerminalResult result,
            IEnumerable<ChatAttachment> attachments = null,
            ResourceRef resultResource = null, string resultResourceKind = null)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
            ModelAttachments = Array.AsReadOnly((attachments ?? new ChatAttachment[0]).ToArray());
            _resultResource = resultResource == null ? null : new ResourceRef(resultResource.Uri, resultResource.Revision);
            ResultResourceKind = resultResourceKind;
        }

        internal void ReplaceResult(TerminalResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        internal void IncludeResultResource(ResourceRef reference, string kind)
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
