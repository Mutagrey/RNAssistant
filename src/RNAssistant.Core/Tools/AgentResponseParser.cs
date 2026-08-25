using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
                return ValidateFinalResponse(response, tools);
            }

            var calls = callsToken as JArray;
            if (calls == null)
            {
                return AgentResponseParseResult.Fail("tool_calls must be an array or null.");
            }
            if (calls.Count == 0)
            {
                return ValidateFinalResponse(response, tools);
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

        private static AgentResponseParseResult ValidateFinalResponse(
            AgentResponse response,
            IEnumerable<ToolDefinition> tools)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.Message))
            {
                return AgentResponseParseResult.Fail("Final agent response requires a non-empty message.");
            }
            if ((tools ?? new ToolDefinition[0]).Any(item => item != null && item.Enabled) &&
                LooksLikeUnexecutedAction(response.Message))
            {
                return AgentResponseParseResult.Fail(
                    "An empty tool_calls array is terminal, but message looks like unfinished progress. " +
                    "Return the promised action as an exact tool call now, or replace message with a completed outcome, clarification, refusal, or concrete inability.");
            }
            return AgentResponseParseResult.Ok(response);
        }

        private static bool LooksLikeUnexecutedAction(string message)
        {
            var value = (message ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 240 || value.IndexOf('?') >= 0 || value.IndexOf(':') >= 0) return false;
            var lower = value.ToLowerInvariant();
            var terminalMarkers = new[]
            {
                "готово", "завершено", "создано", "создан ", "создана ", "обновлено", "исправлено",
                "не могу", "невозможно", "нужно уточнить", "требуется уточнить",
                "done", "completed", "created", "updated", "fixed", "cannot", "can't", "unable"
            };
            if (terminalMarkers.Any(marker => lower.IndexOf(marker, StringComparison.Ordinal) >= 0)) return false;

            return Regex.IsMatch(
                       value,
                       "^(?:сейчас\\s+)?(?:создаю|создам|обновляю|обновлю|исправляю|исправлю|проверяю|проверю|читаю|прочитаю|добавляю|добавлю|удаляю|удалю|переименовываю|переименую|применяю|применю|запускаю|запущу|выполняю|выполню|привязываю|привяжу|сохраняю|сохраню|редактирую|отредактирую|анализирую|проанализирую|пробую|попробую|начинаю|приступаю)(?:\\b|\\s|[.…])",
                       RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                   Regex.IsMatch(
                       value,
                       "^(?:now\\s+)?(?:creating|updating|fixing|checking|reading|adding|deleting|renaming|applying|running|executing|binding|saving|editing|analyzing|trying|starting|working\\s+on)(?:\\b|\\s|[.…])|^(?:let\\s+me|i(?:'|’)ll|i\\s+will)\\s+(?:create|update|fix|check|read|inspect|add|delete|rename|apply|run|execute|bind|save|edit|analyze|try|start|write|build)(?:\\b|\\s|[.…])",
                       RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
