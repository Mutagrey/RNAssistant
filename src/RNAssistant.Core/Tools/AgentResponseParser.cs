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
        public AgentResponseParseResult Parse(string content, IEnumerable<ToolDefinition> tools)
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
                root = JObject.Parse(raw);
            }
            catch (JsonException ex)
            {
                return AgentResponseParseResult.Fail("Agent response is invalid JSON: " + ex.Message);
            }

            var messageToken = root["message"];
            if (messageToken != null && messageToken.Type != JTokenType.Null && messageToken.Type != JTokenType.String)
            {
                return AgentResponseParseResult.Fail("message must be a string or null.");
            }
            var response = new AgentResponse { Message = (string)messageToken ?? string.Empty };
            var callsToken = root["tool_calls"];
            if (callsToken == null || callsToken.Type == JTokenType.Null)
            {
                return string.IsNullOrWhiteSpace(response.Message)
                    ? AgentResponseParseResult.Fail("Final agent response requires a non-empty message.")
                    : AgentResponseParseResult.Ok(response);
            }

            var calls = callsToken as JArray;
            if (calls == null)
            {
                return AgentResponseParseResult.Fail("tool_calls must be an array or null.");
            }
            if (calls.Count == 0)
            {
                return string.IsNullOrWhiteSpace(response.Message)
                    ? AgentResponseParseResult.Fail("Final agent response requires a non-empty message.")
                    : AgentResponseParseResult.Ok(response);
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

                var parsedArguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ToolArgumentNormalizer.AddProperties(arguments, parsedArguments);
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
    }
}
