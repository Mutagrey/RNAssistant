using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools.Contracts
{
    public enum ToolResultStatus { Ok, Error, Unknown }

    // Typed terminal result only. The coordinated model-facing serializer switch
    // is Phase 4B; default DTO serialization is not the Tool Result wire contract.
    public sealed class ToolResult
    {
        private readonly ResourceRef[] _resources;
        public ToolResultStatus Status { get; private set; }
        public string Message { get; private set; }
        public string DataJson { get; private set; }
        public IReadOnlyList<ResourceRef> Resources
        {
            get { return Array.AsReadOnly(_resources.Select(reference => new ResourceRef(reference.Uri, reference.Revision)).ToArray()); }
        }

        public ToolResult(ToolResultStatus status, string message = null, string dataJson = null,
            IEnumerable<ResourceRef> resources = null)
        {
            if (!Enum.IsDefined(typeof(ToolResultStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
            var references = (resources ?? new ResourceRef[0]).ToArray();
            if (references.Any(reference => reference == null || string.IsNullOrWhiteSpace(reference.Uri)))
                throw new ArgumentException("Complete resource references are required.", nameof(resources));
            Status = status;
            Message = message ?? string.Empty;
            DataJson = dataJson;
            _resources = references.Select(reference => new ResourceRef(reference.Uri, reference.Revision)).ToArray();
        }

        public static ToolResult Ok(string message, string dataJson = null, IEnumerable<ResourceRef> resources = null)
        {
            return new ToolResult(ToolResultStatus.Ok, message, dataJson, resources);
        }

        public static ToolResult Error(string message, string dataJson = null, IEnumerable<ResourceRef> resources = null)
        {
            return new ToolResult(ToolResultStatus.Error, message, dataJson, resources);
        }

        public static ToolResult Unknown(string message, string dataJson = null, IEnumerable<ResourceRef> resources = null)
        {
            return new ToolResult(ToolResultStatus.Unknown, message, dataJson, resources);
        }
    }
}
