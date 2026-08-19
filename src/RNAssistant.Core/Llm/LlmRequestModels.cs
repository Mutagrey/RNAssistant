using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public enum LlmFailureKind
    {
        Http,
        ResponseFormatUnsupported,
        RateLimited,
        TransientServer,
        Network,
        Timeout,
        InvalidResponse,
        RequestTooLarge,
        ResponseTooLarge
    }

    public sealed class LlmRequestException : InvalidOperationException
    {
        public LlmRequestException(LlmFailureKind kind, string message, Exception innerException = null, int? statusCode = null)
            : base(message, innerException)
        {
            Kind = kind;
            StatusCode = statusCode;
        }

        public LlmFailureKind Kind { get; private set; }
        public int? StatusCode { get; private set; }
    }

    public delegate Task<LlmCompletionResult> LlmCompletionDelegate(
        AppSettings settings,
        IEnumerable<ChatMessage> messages,
        LlmRequestOptions requestOptions,
        Action<LlmStreamUpdate> streamProgress,
        CancellationToken cancellationToken);

    public delegate string LlmAttachmentTextReader(ChatAttachment attachment, int maxChars);

    public delegate IReadOnlyList<ModelImagePart> LlmModelImageProvider(
        AppSettings settings,
        ChatAttachment attachment,
        int maxImages,
        CancellationToken cancellationToken);

    public static class LlmResponseFormats
    {
        public const string Text = "text";
        public const string JsonObject = "json_object";
        public const string JsonSchema = "json_schema";
    }

    public sealed class LlmRequestOptions
    {
        public string ResponseFormat { get; set; }
        public string ResponseSchemaName { get; set; }
        public string ResponseSchemaJson { get; set; }
        public bool NativeTools { get; set; }
        public IReadOnlyList<LlmToolDefinition> Tools { get; set; }
        public LlmRunCache RunCache { get; set; }
        public bool? ReasoningEnabled { get; set; }
        public bool PlanDecisionAllowed { get; set; }

        public LlmRequestOptions()
        {
            ResponseFormat = LlmResponseFormats.Text;
            Tools = new LlmToolDefinition[0];
            PlanDecisionAllowed = true;
        }
    }

    public sealed class LlmRunCache
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _attachmentText = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<ModelImagePart>> _modelImages =
            new Dictionary<string, IReadOnlyList<ModelImagePart>>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _attachmentBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        internal bool TryGetAttachmentText(string key, out string value)
        {
            lock (_sync) return _attachmentText.TryGetValue(key, out value);
        }

        internal void StoreAttachmentText(string key, string value)
        {
            lock (_sync) _attachmentText[key] = value ?? string.Empty;
        }

        internal bool TryGetModelImages(string key, out IReadOnlyList<ModelImagePart> value)
        {
            lock (_sync) return _modelImages.TryGetValue(key, out value);
        }

        internal void StoreModelImages(string key, IReadOnlyList<ModelImagePart> value)
        {
            lock (_sync) _modelImages[key] = value ?? new ModelImagePart[0];
        }

        internal bool TryGetAttachmentBytes(string key, out byte[] value)
        {
            lock (_sync) return _attachmentBytes.TryGetValue(key, out value);
        }

        internal void StoreAttachmentBytes(string key, byte[] value)
        {
            lock (_sync) _attachmentBytes[key] = value;
        }
    }

    public sealed class ModelImagePart
    {
        public string ContentType { get; set; }
        public byte[] Bytes { get; set; }
        public string Label { get; set; }
    }

    public sealed class LlmToolDefinition
    {
        public string ToolId { get; set; }
        public string ApiName { get; set; }
        public string Description { get; set; }
        public string ParametersSchemaJson { get; set; }
    }

    public sealed class LlmToolCall
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }

        public LlmToolCall()
        {
            Type = "function";
            ArgumentsJson = "{}";
        }
    }
}
