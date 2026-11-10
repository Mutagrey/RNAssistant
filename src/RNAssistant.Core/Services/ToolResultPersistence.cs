using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;

namespace RNAssistant.Core.Services
{
    // Shared terminal/body owner. Hosts provide typed results/evidence and retain
    // their own source/effect adapters; they do not serialize a durable wire body.
    public static class ToolResultPersistence
    {
        public static ChatMessage Create(string callId, string toolName, string stepId,
            ToolResultMaterialization materialized, ChatBlobStore payloads, string runId = null)
        {
            if (materialized == null) throw new ArgumentNullException(nameof(materialized));
            var result = materialized.Result;
            var read = result.SourceRead;
            var message = new ChatMessage { Role = ToolResultRoles.User, ToolResultRole = ToolResultRoles.User,
                ProtocolMessage = true, ToolCallId = callId, ToolName = toolName, ResultStepId = stepId,
                RunId = runId, ToolResultProtocolVersion = ToolResultWire.CurrentVersion,
                ResourceRefs = result.Resources.Select(reference => reference.Copy()).ToList(),
                ResourceEvidence = materialized.ResourceEvidence.ToList(), ResourceEffect = materialized.ResourceEffect,
                AuthorityCommitId = materialized.AuthorityCommitId, ExecutionProgress = materialized.ExecutionProgress };
            JToken compact;
            if (read != null)
            {
                if (payloads == null) throw new InvalidOperationException("Retained source read requires its CAS owner.");
                message.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(read.Text, "text/plain"));
                var metadata = read.CopyText(null);
                message.ResultBody = new RetainedToolResultBody(1, ToolResultBodyKind.ResourceText,
                    result.Status, result.Message, materialized.ResultResource, metadata);
                compact = JObject.FromObject(metadata);
                ((JObject)compact)["payload_externalized"] = true;
            }
            else if ((result.DataJson?.Length ?? 0) > 8192)
            {
                if (payloads == null) throw new InvalidOperationException("Retained result requires its CAS owner.");
                message.ResultPayload = PayloadRef.FromBlob(payloads.StoreText(result.DataJson, "application/json"));
                message.ResultBody = new RetainedToolResultBody(1, ToolResultBodyKind.JsonData,
                    result.Status, result.Message, materialized.ResultResource);
                compact = CompactData(toolName, materialized);
            }
            else compact = materialized.Data;
            message.Content = ToolResultWire.WriteParsed(callId, toolName, result, compact, materialized.ResultResource);
            return message;
        }

        public static ToolResult Terminal(ToolExecutionRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Result != null) return record.Result;
            if (record.Outcome == ToolExecutionOutcome.Ok)
                throw new InvalidOperationException("Successful tool execution has no terminal result.");
            if (record.Outcome == ToolExecutionOutcome.Waiting || record.Outcome == ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException("An unresolved call has no terminal result.");
            return record.Outcome == ToolExecutionOutcome.Unknown ? ToolResult.Unknown(record.Message) :
                ToolResult.Error(record.Message, record.Outcome == ToolExecutionOutcome.NotDispatched
                    ? "{\"code\":\"not_dispatched\",\"dispatched\":false}" : "{\"code\":\"execution_error\"}");
        }

        public static string ReadContent(ChatMessage message, ChatBlobStore payloads)
        {
            if (message?.ResultPayload == null) return message?.Content;
            var content = payloads?.ReadText(message.ResultPayload.ToBlobReference());
            if (content == null) throw new InvalidDataException("Exact tool result body is missing.");
            return Expand(message, content);
        }

        public static ChatMessage Hydrate(ChatMessage message, ChatBlobStore payloads)
        {
            var copy = RNAssistant.Core.Llm.HistoricalContextProjector.Project(message);
            if (copy != null) copy.Content = ReadContent(message, payloads);
            return copy;
        }

        // Also used by the compiler after its single selected CAS read. This is
        // rendering, not evidence/currentness/delivery admission.
        public static string Expand(ChatMessage message, string body)
        {
            var retained = message?.ResultBody;
            if (retained == null)
            {
                if (message?.ProtocolMessage == true && !string.IsNullOrWhiteSpace(message.ToolCallId) &&
                    message.ToolResultProtocolVersion == ToolResultWire.CurrentVersion)
                    throw new InvalidDataException("Incompatible retained tool result. Start a new chat; stored history is preserved.");
                return body; // typed user-context/source-observation payload, not a tool-result envelope
            }
            if (body == null || message.ResultPayload == null || string.IsNullOrWhiteSpace(message.ToolCallId) ||
                string.IsNullOrWhiteSpace(message.ToolName)) throw new InvalidDataException("Retained result identity/body is missing.");
            JToken data;
            if (retained.Kind == ToolResultBodyKind.ResourceText)
                data = JObject.FromObject(retained.Read.CopyText(body));
            else if (retained.Kind == ToolResultBodyKind.JsonData) data = ToolResultWire.ParseData(body);
            else throw new InvalidDataException("Unknown result body representation.");
            return ToolResultWire.WriteParsed(message.ToolCallId, message.ToolName,
                new ToolResult(retained.Status, retained.Message, resources: message.ResourceRefs), data, retained.ResultResource);
        }

        private static JToken CompactData(string toolName, ToolResultMaterialization value)
        {
            var compact = new JObject { ["payload_externalized"] = true,
                ["complete"] = value.ResourceEvidence.All(item => item.Complete),
                ["characters"] = value.Result.DataJson.Length };
            var read = value.Data as JObject;
            if (toolName == "core.read" && (string)read?["type"] == "shared context") compact["type"] = "shared context";
            var source = read?["tool_data"] as JObject ?? read;
            foreach (var label in new[] { "target", "moduleName", "title" })
                if (source?[label]?.Type == JTokenType.String && !((string)source[label]).Contains("://"))
                    compact[label] = source[label].DeepClone();
            if (toolName.StartsWith("html.", StringComparison.Ordinal) && source?["members"] is JArray)
                compact["members"] = new JArray(((JArray)source["members"]).OfType<JObject>().Select(item =>
                    new JObject { ["path"] = item["path"]?.DeepClone(), ["target"] = item["target"]?.DeepClone(), ["uri"] = item["uri"]?.DeepClone() }));
            return compact;
        }
    }
}
