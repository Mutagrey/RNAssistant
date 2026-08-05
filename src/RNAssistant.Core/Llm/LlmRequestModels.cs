using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Llm
{
    public delegate Task<LlmCompletionResult> LlmCompletionDelegate(
        AppSettings settings,
        IEnumerable<ChatMessage> messages,
        LlmRequestOptions requestOptions,
        Action<LlmStreamUpdate> streamProgress,
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

        public LlmRequestOptions()
        {
            ResponseFormat = LlmResponseFormats.Text;
            Tools = new LlmToolDefinition[0];
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
