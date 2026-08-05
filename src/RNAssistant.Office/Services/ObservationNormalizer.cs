using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;

namespace RNAssistant.Office.Services
{
    internal sealed class ObservationNormalizer
    {
        private int _nextId = 1;

        public AgentObservation Normalize(ToolCommand command, ToolDefinition tool, ToolResult result, string purpose = null)
        {
            var id = "obs_" + _nextId++;
            var success = result != null && result.Success;
            var observation = new AgentObservation
            {
                Id = id,
                ToolId = command == null ? string.Empty : command.ToolId,
                Status = success ? "success" : "error",
                Mutation = tool != null && tool.MutatesDocument,
                LocalMutation = tool != null && tool.MutatesLocalState,
                RequiresVerification = success && tool != null && tool.MutatesDocument,
                Purpose = string.IsNullOrWhiteSpace(purpose)
                    ? tool != null && (tool.MutatesDocument || tool.MutatesLocalState)
                        ? AgentObservationPurposes.Mutation
                        : AgentObservationPurposes.Inspection
                    : purpose,
                Summary = BuildSummary(command, result, tool),
                FactsJson = BuildFactsJson(command, result)
            };
            return observation;
        }

        private static string BuildSummary(ToolCommand command, ToolResult result, ToolDefinition tool)
        {
            var toolId = command == null ? string.Empty : command.ToolId;
            var status = result != null && result.Success ? "succeeded" : "failed";
            var message = result == null ? string.Empty : result.Message ?? string.Empty;
            var errorCode = result == null || string.IsNullOrWhiteSpace(result.ErrorCode)
                ? string.Empty
                : " [" + result.ErrorCode + "]";
            if (!string.IsNullOrWhiteSpace(message))
            {
                return toolId + " " + status + errorCode + ": " + AgentText.Truncate(message, 500);
            }
            return toolId + " " + status + errorCode + ".";
        }

        private static string BuildFactsJson(ToolCommand command, ToolResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.DataJson))
            {
                return null;
            }
            try
            {
                var token = JToken.Parse(result.DataJson);
                return token.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return null;
            }
        }


    }
}
