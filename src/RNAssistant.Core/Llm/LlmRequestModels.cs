using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
        public LlmRunCache RunCache { get; set; }
        public bool? ReasoningEnabled { get; set; }
        public Action<LlmRequestDiagnosticUpdate> DiagnosticProgress { get; set; }
        [JsonIgnore]
        public ChatSession TraceSession { get; set; }
        [JsonIgnore]
        public string TracePurpose { get; set; }
        [JsonIgnore]
        public Action<LlmTraceRecord> TraceSink { get; set; }
        [JsonIgnore]
        public bool TraceSinkConfigured { get; set; }

        public LlmRequestOptions()
        {
            ResponseFormat = LlmResponseFormats.Text;
        }
    }

    public sealed class LlmTraceRecord
    {
        public string Type { get; set; }
        public string RequestId { get; set; }
        public string Purpose { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ResponseFormat { get; set; }
        public int MessageCount { get; set; }
        public int? Attempt { get; set; }
        public int? EstimatedPromptTokens { get; set; }
        public int? StatusCode { get; set; }
        public string FailureKind { get; set; }
        public string Error { get; set; }
        public int? ChunkIndex { get; set; }
        public int? ChunkCount { get; set; }
        public bool? Completed { get; set; }
        public string ChunkEncoding { get; set; }
        [JsonIgnore]
        public string PayloadJson { get; set; }
        [JsonIgnore]
        public string PayloadContentType { get; set; }
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

}
