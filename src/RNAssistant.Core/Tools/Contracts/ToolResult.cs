using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools.Contracts
{
    public enum ToolResultStatus { Ok, Error, Unknown }

    // Typed terminal result only. ModelProtocol.ToolResultWire owns the v1 JSON;
    // default DTO serialization is not the model-facing wire contract.
    public sealed class ToolResult
    {
        public ToolResultStatus Status { get; private set; }
        public string Message { get; private set; }
        private string _dataJson;
        private readonly ResourceReadProjection _sourceRead;
        public string DataJson { get { return _dataJson ?? (_sourceRead == null ? null :
            (_dataJson = Newtonsoft.Json.JsonConvert.SerializeObject(_sourceRead))); } }
        [Newtonsoft.Json.JsonIgnore]
        public ResourceReadProjection SourceRead { get { return _sourceRead?.CopyText(_sourceRead.Text); } }
        public IReadOnlyList<ResourceRef> Resources { get; }

        public ToolResult(ToolResultStatus status, string message = null, string dataJson = null,
            IEnumerable<ResourceRef> resources = null, ResourceReadProjection sourceRead = null)
        {
            if (!Enum.IsDefined(typeof(ToolResultStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
            var references = (resources ?? new ResourceRef[0]).ToArray();
            if (references.Any(reference => reference == null || string.IsNullOrWhiteSpace(reference.Uri)))
                throw new ArgumentException("Complete resource references are required.", nameof(resources));
            Status = status;
            Message = message ?? string.Empty;
            if (sourceRead != null && (dataJson != null || sourceRead.Text == null))
                throw new ArgumentException("A typed source read supplies its own complete text projection.", nameof(sourceRead));
            _dataJson = dataJson;
            _sourceRead = sourceRead?.CopyText(sourceRead.Text);
            Resources = Array.AsReadOnly(references);
        }

        public static ToolResult Ok(string message, string dataJson = null, IEnumerable<ResourceRef> resources = null)
        {
            return new ToolResult(ToolResultStatus.Ok, message, dataJson, resources);
        }

        public static ToolResult OkRead(string message, ResourceReadProjection projection, IEnumerable<ResourceRef> resources)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            return projection.Text != null && projection.Table == null && projection.Tree == null
                ? new ToolResult(ToolResultStatus.Ok, message, resources: resources, sourceRead: projection)
                : Ok(message, Newtonsoft.Json.JsonConvert.SerializeObject(projection), resources);
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
