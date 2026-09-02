using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Services;

namespace RNAssistant.Office.Tools
{
    internal sealed class HtmlAcceptedReadSource
    {
        internal string ToolId { get; private set; }
        internal JObject Arguments { get; private set; }
        internal string DataJson { get; private set; }

        internal HtmlAcceptedReadSource(
            string toolId, JObject arguments, string dataJson)
        {
            ToolId = toolId;
            Arguments = arguments;
            DataJson = dataJson;
        }
    }

    internal sealed class HtmlAcceptedReadSourceException : InvalidOperationException
    {
        internal string ErrorCode { get; private set; }

        internal HtmlAcceptedReadSourceException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    // Binding consumes the already accepted source call/result pair from the
    // current Agent run. Tool ids, arguments and result-resource identity stay
    // runtime-owned; the model only expresses the HTML binding intent.
    internal static class HtmlAcceptedReadSourceResolver
    {
        internal static HtmlAcceptedReadSource Resolve(
            ChatSession session,
            IReadOnlyDictionary<string, ToolCatalogEntry> eligibleTools)
        {
            var runId = session == null || session.LastRun == null
                ? null : session.LastRun.RunId;
            if (string.IsNullOrWhiteSpace(runId))
                throw Failure(
                    "HTML data binding requires a successful Office read earlier in the current Agent run.",
                    "html_data_source_read_required");

            var messages = session.Messages ?? new List<ChatMessage>();
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                var resultMessage = messages[index];
                if (resultMessage == null || resultMessage.Activity != null ||
                    !string.Equals(resultMessage.RunId, runId,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(resultMessage.ToolName) ||
                    eligibleTools == null ||
                    !eligibleTools.ContainsKey(resultMessage.ToolName))
                    continue;

                ToolResultWireReadResult wire;
                string error;
                if (!ToolResultHistoryReader.TryRead(
                    resultMessage, out wire, out error))
                    throw Failure(
                        "The latest eligible Office read has invalid accepted result evidence: " +
                            error,
                        "html_data_source_evidence_invalid");
                if (wire.Result.Status != ToolResultStatus.Ok)
                    throw Failure(
                        "The latest eligible Office read did not succeed. Run the intended read again before binding.",
                        "html_data_source_read_failed");

                var callMessage = messages.Take(index).LastOrDefault(message =>
                    message != null && message.Activity == null &&
                    string.Equals(message.Role, "assistant",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(message.RunId, runId,
                        StringComparison.Ordinal) &&
                    string.Equals(message.ToolCallId, wire.ToolCallId,
                        StringComparison.Ordinal) &&
                    string.Equals(message.ToolName, wire.Name,
                        StringComparison.Ordinal));
                if (callMessage == null)
                    throw Failure(
                        "The latest eligible Office result has no matching accepted call.",
                        "html_data_source_evidence_invalid");

                var accepted = ConversationResponseHistoryReader.Read(callMessage);
                if (!accepted.Success || accepted.Response.ToolCalls.Count != 1)
                    throw Failure(
                        "The latest eligible Office call evidence is invalid: " +
                            (accepted.Error ?? "one accepted call is required"),
                        "html_data_source_evidence_invalid");
                var call = accepted.Response.ToolCalls[0];
                if (!string.Equals(call.Id, wire.ToolCallId,
                        StringComparison.Ordinal) ||
                    !string.Equals(call.Name, wire.Name,
                        StringComparison.Ordinal))
                    throw Failure(
                        "The latest eligible Office call/result pair does not match.",
                        "html_data_source_evidence_invalid");

                JObject arguments;
                try
                {
                    arguments = JObject.Parse(call.ArgumentsJson);
                }
                catch (JsonException ex)
                {
                    throw Failure(
                        "The accepted Office read arguments are invalid: " +
                            ex.Message,
                        "html_data_source_evidence_invalid");
                }
                return new HtmlAcceptedReadSource(
                    wire.Name, arguments,
                    ExactResultData(session, wire));
            }

            throw Failure(
                "No successful approved Office read exists in the current Agent run. Run the intended read, then bind its result.",
                "html_data_source_read_required");
        }

        private static string ExactResultData(
            ChatSession session, ToolResultWireReadResult wire)
        {
            var reference = wire == null ? null : wire.ResultResource;
            if (reference == null)
            {
                var data = wire == null || wire.Result == null
                    ? null : wire.Result.DataJson;
                if (IsTransportPreview(data))
                    throw Failure(
                        "The accepted Office read is only a transport preview. Run a smaller read before binding.",
                        "html_data_source_result_incomplete");
                return data;
            }

            string artifactId;
            int revision;
            if (session == null || !ChatResourceUri.TryParseArtifactRevision(
                    session.Id, reference, out artifactId, out revision))
                throw Failure(
                    "The accepted Office read points outside the current chat.",
                    "html_data_source_evidence_invalid");
            var matches = (session.Artifacts ?? new List<ChatArtifact>())
                .Where(item => item != null && string.Equals(
                    item.Id, artifactId, StringComparison.OrdinalIgnoreCase) &&
                    Math.Max(1, item.Revision) == revision)
                .Take(2)
                .ToList();
            if (matches.Count != 1)
                throw Failure(
                    "The complete accepted Office result is missing or ambiguous.",
                    "html_data_source_result_missing");
            var artifact = matches[0];
            if (!string.Equals(artifact.RunId,
                    session.LastRun == null ? null : session.LastRun.RunId,
                    StringComparison.Ordinal))
                throw Failure(
                    "The complete accepted Office result belongs to another Agent run.",
                    "html_data_source_evidence_invalid");
            JObject metadata;
            try
            {
                metadata = JObject.Parse(artifact.MetadataJson ?? "{}");
            }
            catch (JsonException)
            {
                throw Failure(
                    "The complete accepted Office result metadata is invalid.",
                    "html_data_source_evidence_invalid");
            }
            if (!string.Equals((string)metadata["toolId"], wire.Name,
                    StringComparison.Ordinal) ||
                !string.Equals((string)metadata["toolCallId"],
                    wire.ToolCallId, StringComparison.Ordinal) ||
                artifact.InlineText == null)
                throw Failure(
                    "The complete accepted Office result does not match its call evidence.",
                    "html_data_source_evidence_invalid");
            if (!string.IsNullOrWhiteSpace(artifact.ContentSha256) &&
                !string.Equals(artifact.ContentSha256,
                    TextPatternEngine.Sha256(artifact.InlineText),
                    StringComparison.OrdinalIgnoreCase))
                throw Failure(
                    "The complete accepted Office result failed its content check.",
                    "html_data_source_evidence_invalid");
            return artifact.InlineText;
        }

        private static bool IsTransportPreview(string dataJson)
        {
            try
            {
                var data = JObject.Parse(dataJson ?? string.Empty);
                return (bool?)data["truncated"] == true &&
                    data["original_chars"] != null &&
                    data["preview"] != null && data["hint"] != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static HtmlAcceptedReadSourceException Failure(
            string message, string errorCode)
        {
            return new HtmlAcceptedReadSourceException(message, errorCode);
        }
    }
}
