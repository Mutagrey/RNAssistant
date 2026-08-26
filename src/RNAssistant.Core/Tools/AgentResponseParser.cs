using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Core.Tools
{
    public sealed class AgentResponseParser
    {
        public AgentResponseParseResult Parse(
            string content,
            IEnumerable<ToolDefinition> tools,
            bool allowPlanned = false)
        {
            var raw = (content ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return AgentResponseParseResult.Fail("Model returned an empty response.");
            }
            if (!raw.StartsWith("{", StringComparison.Ordinal) || !raw.EndsWith("}", StringComparison.Ordinal))
            {
                return AgentResponseParseResult.Fail("Agent response must be one JSON object without markdown or surrounding prose.");
            }

            JObject root;
            try
            {
                root = JObject.Parse(raw, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (JsonException ex)
            {
                return AgentResponseParseResult.Fail("Agent response is invalid JSON: " + ex.Message);
            }

            var statusToken = root["status"];
            if (statusToken == null || statusToken.Type != JTokenType.String)
            {
                return AgentResponseParseResult.Fail("Agent response requires a string status field.");
            }
            var status = (string)statusToken;
            if (!AgentResponseStatuses.IsKnown(status))
            {
                return AgentResponseParseResult.Fail("Agent response status is not supported: " + status + ".");
            }
            if (string.Equals(status, AgentResponseStatuses.Planned, StringComparison.Ordinal) && !allowPlanned)
            {
                return AgentResponseParseResult.Fail(
                    "Agent response status planned is unavailable because runtime did not select planning mode.");
            }

            var messageToken = root["message"];
            if (messageToken == null || messageToken.Type != JTokenType.String)
            {
                return AgentResponseParseResult.Fail("Agent response requires a string message field.");
            }
            var response = new AgentResponse { Status = status, Message = (string)messageToken };
            var callsToken = root["tool_calls"];
            if (callsToken == null)
            {
                return AgentResponseParseResult.Fail("Agent response requires a tool_calls array.");
            }

            var calls = callsToken as JArray;
            if (calls == null)
            {
                return AgentResponseParseResult.Fail("tool_calls must be an array.");
            }
            if (calls.Count > AgentResponseSchemaBuilder.MaximumToolCalls)
            {
                return AgentResponseParseResult.Fail(
                    "tool_calls exceeds the maximum of " + AgentResponseSchemaBuilder.MaximumToolCalls + " calls per response.");
            }
            if (calls.Count == 0)
            {
                if (string.Equals(response.Status, AgentResponseStatuses.InProgress, StringComparison.Ordinal))
                {
                    return AgentResponseParseResult.Fail(
                        "Agent response status in_progress requires at least one tool call.");
                }
                return ValidateFinalResponse(response);
            }
            if (!string.Equals(response.Status, AgentResponseStatuses.InProgress, StringComparison.Ordinal))
            {
                return AgentResponseParseResult.Fail(
                    "Agent response status " + response.Status + " requires an empty tool_calls array.");
            }
            if (string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentResponseParseResult.Fail("Tool response requires a non-empty message describing the current step.");
            }
            var knownTools = (tools ?? new ToolDefinition[0])
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var callIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var containsConfirmationCall = false;
            foreach (var token in calls)
            {
                var call = token as JObject;
                var unsupportedCallField = call == null ? null : call.Properties().FirstOrDefault(property =>
                    !string.Equals(property.Name, "id", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "name", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "arguments", StringComparison.Ordinal));
                if (unsupportedCallField != null)
                {
                    return AgentResponseParseResult.Fail(
                        "Tool call contains unsupported field: " + unsupportedCallField.Name + ".");
                }
                var idToken = call == null ? null : call["id"];
                var nameToken = call == null ? null : call["name"];
                var id = idToken != null && idToken.Type == JTokenType.String ? (string)idToken : null;
                var name = nameToken != null && nameToken.Type == JTokenType.String ? (string)nameToken : null;
                var arguments = call == null ? null : call["arguments"] as JObject;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || arguments == null)
                {
                    return AgentResponseParseResult.Fail("Each tool call requires id, name, and object arguments.");
                }
                if (!callIds.Add(id))
                {
                    return AgentResponseParseResult.Fail("Tool call ids must be unique within one response: " + id + ".");
                }
                ToolDefinition tool;
                if (!knownTools.TryGetValue(name, out tool))
                {
                    return AgentResponseParseResult.Fail("Unknown tool: " + name + ". Use an exact name from tools.");
                }
                containsConfirmationCall |= tool.RequiresConfirmation;

                var duplicateArgument = arguments.Properties()
                    .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateArgument != null)
                {
                    return AgentResponseParseResult.Fail(
                        "Tool arguments must not contain duplicate names that differ only by case: " + duplicateArgument.Key + ".");
                }
                var parsedArguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    ToolArgumentNormalizer.AddProperties(arguments, parsedArguments);
                }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is ArgumentException)
                {
                    return AgentResponseParseResult.Fail("Tool arguments could not be normalized: " + ex.Message);
                }
                response.ToolCalls.Add(new AgentToolCall
                {
                    Id = id,
                    Name = tool.Id,
                    Arguments = parsedArguments
                });
            }
            if (calls.Count > 1 && containsConfirmationCall)
            {
                return AgentResponseParseResult.Fail(
                    "Tool calls that may require confirmation must be returned one at a time. " +
                    "Return exactly one tool call and wait for its TOOL_RESULT before choosing the next action.");
            }
            return AgentResponseParseResult.Ok(response);
        }

        private static AgentResponseParseResult ValidateFinalResponse(AgentResponse response)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentResponseParseResult.Fail("Final agent response requires a non-empty message.");
            }
            return AgentResponseParseResult.Ok(response);
        }
    }
}
